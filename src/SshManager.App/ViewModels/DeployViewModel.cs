using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.App.Services;
using SshManager.App.Views;
using SshManager.Core;
using SshManager.Core.Helpers;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

/// <summary>部署包内一项文件/目录的展示行。</summary>
public partial class BundleRowViewModel : ViewModelBase
{
    public string Name { get; init; } = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _found;

    public string Mark => Found ? "✔" : "–";

    public IBrush Brush => Found
        ? new SolidColorBrush(Avalonia.Media.Color.Parse("#10b981"))
        : new SolidColorBrush(Avalonia.Media.Color.Parse("#6b7386"));
}

/// <summary>预检条目展示行。</summary>
public partial class PreflightRowViewModel : ViewModelBase
{
    public string Name { get; init; } = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private PreflightState _state = PreflightState.Info;

    public string Glyph => State switch
    {
        PreflightState.Ok => "✔",
        PreflightState.Warn => "!",
        PreflightState.Bad => "✖",
        _ => "i",
    };

    public IBrush Brush => State switch
    {
        PreflightState.Ok => new SolidColorBrush(Avalonia.Media.Color.Parse("#10b981")),
        PreflightState.Warn => new SolidColorBrush(Avalonia.Media.Color.Parse("#f59e0b")),
        PreflightState.Bad => new SolidColorBrush(Avalonia.Media.Color.Parse("#f43f5e")),
        _ => new SolidColorBrush(Avalonia.Media.Color.Parse("#8b93a5")),
    };
}

public partial class DeployViewModel : ViewModelBase
{
    private readonly SshSession _session;
    private readonly DeployService _deploy;
    private readonly DispatcherTimer? _elapsedTimer;
    private DateTime _startedUtc;
    private CancellationTokenSource? _cts;
    private bool _suppressCommandSync;
    private PreflightResult? _preflight;

    public UiLog Log { get; } = new();

    public ObservableCollection<BundleRowViewModel> BundleRows { get; } = new();
    public ObservableCollection<PreflightRowViewModel> PreflightRows { get; } = new();
    public ObservableCollection<string> EntryScripts { get; } = new();

    [ObservableProperty] private string _bundleDir = "";
    [ObservableProperty] private string _selectedEntryScript = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanButtonText = "扫描并预检";
    [ObservableProperty] private bool _hasResult;

    [ObservableProperty] private bool _modeCheck = true;
    [ObservableProperty] private bool _modeFull;
    [ObservableProperty] private bool _modeUpdate;
    [ObservableProperty] private bool _modeKiosk;
    private DeployMode _mode = DeployMode.Check;
    private bool _commandDirty;

    [ObservableProperty] private string _commandText = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private string _statusText = "未执行";
    [ObservableProperty] private IBrush _statusBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#8b93a5"));

    [ObservableProperty] private string _recommendation = "填写部署包在服务器上的目录（文件可先用「文件管理」上传），然后点击「扫描并预检」。";
    [ObservableProperty] private bool _recommendDanger;
    [ObservableProperty] private IBrush _recommendBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#10b981"));

    public DeployViewModel(SshSession session)
    {
        _session = session;
        _deploy = new DeployService(session);
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();
    }

    // ------------------------------------------------------------------ 扫描 / 预检

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var start = string.IsNullOrWhiteSpace(BundleDir) ? "/home" : BundleDir;
        var dlg = new FolderPickerDialog(_session, start);
        var ok = await dlg.ShowDialog<bool>(Dialogs.MainWindow!);
        if (ok) BundleDir = dlg.Result;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        var dir = (BundleDir ?? "").Trim();
        if (dir.Length == 0 || IsScanning) return;
        IsScanning = true;
        ScanButtonText = "扫描中…";
        try
        {
            // 非 root 且还没有 sudo 密码时先要一次（预检需要读 docker/snap 信息）
            if (!_session.IsRoot && string.IsNullOrEmpty(_session.SudoPassword))
            {
                var (ok, pw) = await Dialogs.InputAsync(
                    "sudo 密码",
                    $"预检需要读取 Docker / snap 状态，请输入用户 {_session.Profile.Username} 的密码（仅用于本次会话）：",
                    "", password: true);
                if (ok && pw.Length > 0) _session.SudoPassword = pw;
            }

            var scan = await _deploy.ScanBundleAsync(dir);
            BundleRows.Clear();
            foreach (var item in scan.Items)
                BundleRows.Add(new BundleRowViewModel { Name = item.Name, Detail = item.Detail, Found = item.Found });

            EntryScripts.Clear();
            foreach (var s in scan.CandidateScripts) EntryScripts.Add(s);
            SelectedEntryScript = EntryScripts.Count > 0 ? EntryScripts[0] : "";

            var preflight = await _deploy.PreflightAsync(dir);

            _preflight = preflight;
            PreflightRows.Clear();
            foreach (var p in preflight.Items)
                PreflightRows.Add(new PreflightRowViewModel { Name = p.Name, Detail = p.Detail, State = p.State });

            var scanNote = scan.Error.Length > 0
                ? "注意（部署包）：" + scan.Error
                : (scan.SizeText.Length > 0 ? $"部署包大小约 {scan.SizeText}。" : "");
            Recommendation = (scanNote.Length > 0 ? scanNote + "\n" : "") + "建议：" + preflight.Recommendation;
            RecommendDanger = preflight.ExistingSsts;
            RecommendBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(
                preflight.ExistingSsts ? "#f43f5e" : "#10b981"));

            HasResult = true;
            SyncCommand(false);
        }
        catch (Exception ex)
        {
            HasResult = false;
            Recommendation = "扫描失败：" + ex.Message;
            RecommendDanger = true;
            RecommendBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#f43f5e"));
        }
        finally
        {
            IsScanning = false;
            ScanButtonText = "扫描并预检";
        }
    }

    partial void OnSelectedEntryScriptChanged(string value) => SyncCommand(_commandDirty);

    // ------------------------------------------------------------------ 模式与命令

    [RelayCommand]
    private void SelectMode(string key)
    {
        ModeCheck = key == "check";
        ModeFull = key == "full";
        ModeUpdate = key == "update";
        ModeKiosk = key == "kiosk";
        _mode = key switch
        {
            "full" => DeployMode.Full,
            "update" => DeployMode.Update,
            "kiosk" => DeployMode.Kiosk,
            _ => DeployMode.Check,
        };
        SyncCommand(_commandDirty);
    }

    partial void OnCommandTextChanged(string value)
    {
        if (!_suppressCommandSync) _commandDirty = true;
    }

    /// <summary>根据模式/入口脚本生成默认命令；keepDirty 为 true 时只在命令为空时生成。</summary>
    private void SyncCommand(bool keepDirty)
    {
        if (keepDirty && (CommandText ?? "").Trim().Length > 0) return;
        var dir = (BundleDir ?? "").Trim();
        _suppressCommandSync = true;
        CommandText = dir.Length == 0
            ? ""
            : DeployService.DefaultCommand(_mode, dir, SelectedEntryScript);
        _commandDirty = false;
        _suppressCommandSync = false;
    }

    // ------------------------------------------------------------------ 执行

    [RelayCommand]
    private async Task RunAsync()
    {
        var command = (CommandText ?? "").Trim();
        if (command.Length == 0 || IsRunning) return;

        if (_mode == DeployMode.Full)
        {
            var ok = await Dialogs.ConfirmAsync(
                "全新部署确认",
                $"即将在服务器上执行「全新部署」：\n{command}\n\n该模式只适用于干净的新服务器；已有 SSTS 部署的服务器请使用「更新」或「kiosk」模式。\n继续执行？",
                danger: true, okText: "执行全新部署", mono: true);
            if (!ok) return;
        }
        else
        {
            var what = _mode switch
            {
                DeployMode.Update => "更新应用/Web/配置",
                DeployMode.Kiosk => "部署/更新 kiosk 运行环境",
                _ => "预检",
            };
            var ok = await Dialogs.ConfirmAsync("执行确认", $"即将执行「{what}」：\n{command}\n\n继续？", okText: "执行", mono: true);
            if (!ok) return;
        }

        string wrapped;
        try
        {
            wrapped = WrapForRemote(command);
        }
        catch (SudoPasswordRequiredException)
        {
            var (ok, pw) = await Dialogs.InputAsync(
                "sudo 密码",
                $"用户 {_session.Profile.Username} 需要提权执行部署，请输入其密码：",
                "", password: true);
            if (!ok || pw.Length == 0)
            {
                Log.Line("✖ 已取消：未提供 sudo 密码。");
                return;
            }
            _session.SudoPassword = pw;
            wrapped = WrapForRemote(command);
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        StatusText = "执行中";
        StatusBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#2dd4bf"));
        _startedUtc = DateTime.UtcNow;
        ElapsedText = "00:00";
        _elapsedTimer?.Start();
        Log.Line($"━━━ {DateTime.Now:HH:mm:ss} 开始执行 ━━━");
        Log.Line("$ " + command);
        if (!_session.IsRoot)
            Log.Line("（非 root 用户，已自动以 sudo 执行）");

        try
        {
            var result = await _session.RunStreamingAsync(
                wrapped, l => Log.Line(l), l => Log.Line(l), _cts.Token, TimeSpan.FromHours(2));
            if (result.WasCancelled)
            {
                StatusText = "已停止";
                StatusBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#8b93a5"));
                Log.Line("━━━ 已手动停止 ━━━");
            }
            else if (result.ExitCode == 0)
            {
                StatusText = "成功 · 退出码 0";
                StatusBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#10b981"));
                Log.Line("━━━ 执行成功（退出码 0） ━━━");
            }
            else
            {
                StatusText = $"失败 · 退出码 {result.ExitCode}";
                StatusBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#f43f5e"));
                Log.Line($"━━━ 执行失败（退出码 {result.ExitCode}） ━━━");
            }
        }
        catch (Exception ex)
        {
            StatusText = "出错";
            StatusBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#f43f5e"));
            Log.Line("✖ " + ex.Message);
        }
        finally
        {
            IsRunning = false;
            _elapsedTimer?.Stop();
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    private string WrapForRemote(string command) =>
        _session.IsRoot
            ? $"bash -c {UnixPath.ShellQuote(command)}"
            : _session.WrapSudo($"bash -c {UnixPath.ShellQuote(command)}");

    private void UpdateElapsed()
    {
        var el = DateTime.UtcNow - _startedUtc;
        ElapsedText = el.TotalHours >= 1
            ? el.ToString(@"hh\:mm\:ss")
            : el.ToString(@"mm\:ss");
    }
}
