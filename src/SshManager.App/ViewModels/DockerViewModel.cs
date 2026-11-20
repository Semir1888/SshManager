using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.App.Services;
using SshManager.Core;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

// ═══════════════════════════════════════════════════ 行视图模型 ═════════════════════════════════════════════════════

public partial class DockerContainerViewModel : ViewModelBase
{
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#10b981"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#f59e0b"));
    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#8b93a5"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8b93a5"));

    private readonly DockerViewModel _parent;

    public DockerContainer Model { get; }

    public string Id => Model.Id;
    public string ShortId => Model.Id.Length > 12 ? Model.Id[..12] : Model.Id;
    public string Name
    {
        get
        {
            var n = Model.Names.Split(',')[0].Trim();
            return n.Length > 0 ? n : ShortId;
        }
    }
    public string NamesText => Model.Names.Replace(",", ", ");
    public bool HasMultipleNames => Model.Names.Contains(',');
    public string Image => Model.Image;
    public bool IsRunning => Model.State == "running";
    public string StatusText => Model.Status;
    public string PortsText => Model.Ports;

    /// <summary>docker stats 合并进来的实时占用。</summary>
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _memText = "—";

    public string StateLabel => Model.State switch
    {
        "running" => "运行中",
        "paused" => "已暂停",
        "exited" => "已退出",
        "created" => "已创建",
        "restarting" => "重启中",
        "removing" => "移除中",
        "dead" => "异常",
        _ => Model.State,
    };

    public IBrush StateBrush => Model.State switch
    {
        "running" => Green,
        "paused" or "restarting" => Amber,
        _ => Gray,
    };

    public DockerContainerViewModel(DockerContainer model, DockerViewModel parent)
    {
        Model = model;
        _parent = parent;
    }

    [RelayCommand] private Task Start() => _parent.StartContainerAsync(this);
    [RelayCommand] private Task Stop() => _parent.StopContainerAsync(this);
    [RelayCommand] private Task Restart() => _parent.RestartContainerAsync(this);
    [RelayCommand] private Task Logs() => _parent.ShowLogsAsync(this);
    [RelayCommand] private void Exec() => _parent.ExecHint(this);
    [RelayCommand] private Task Remove() => _parent.RemoveContainerAsync(this);
}

public partial class DockerImageViewModel : ViewModelBase
{
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#10b981"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8b93a5"));

    private readonly DockerViewModel _parent;

    public DockerImage Model { get; }

    public string Id => Model.Id;
    public string ShortId => Model.Id.StartsWith("sha256:", StringComparison.Ordinal)
        ? (Model.Id.Length > 19 ? Model.Id[..19] : Model.Id)
        : (Model.Id.Length > 12 ? Model.Id[..12] : Model.Id);

    public string Repository => Model.Repository;
    public string Tag => Model.Tag;

    /// <summary>&lt;none&gt; 标签（悬空/被覆盖）用暗色，正常标签用主色。</summary>
    public IBrush TagBrush => Model.Tag == "<none>" ? Dim : Green;

    public string RepoTag => Model.Repository == "<none>" ? $"〈悬空 {ShortId}〉" : $"{Model.Repository}:{Model.Tag}";
    public string Size => Model.Size;
    public string CreatedSince => Model.CreatedSince;

    public DockerImageViewModel(DockerImage model, DockerViewModel parent)
    {
        Model = model;
        _parent = parent;
    }

    [RelayCommand] private Task Remove() => _parent.RemoveImageAsync(this);
}

// ═══════════════════════════════════════════════════ 页面视图模型 ═════════════════════════════════════════════════════

public partial class DockerViewModel : ViewModelBase
{
    private readonly SshSession _session;
    private readonly MainWindowViewModel _owner;
    private DockerService _docker = null!;
    private DispatcherTimer? _autoTimer;
    private bool _reloading;
    private bool _logsBusy;
    private string? _logContainerId;

    [ObservableProperty] private DockerState _state = DockerState.Unknown;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private bool _sudoMode;
    [ObservableProperty] private string _failText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showContainers = true;
    [ObservableProperty] private bool _showImages;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _autoRefresh;

    // 日志 / 操作输出面板
    public UiLog Logs { get; } = new();
    [ObservableProperty] private bool _panelOpen;
    [ObservableProperty] private string _logTitle = "操作输出";
    [ObservableProperty] private int _logTail = 500;
    [ObservableProperty] private bool _logsLoading;

    /// <summary>日志 / 操作输出面板高度（可由拖拽条调节）。</summary>
    [ObservableProperty] private double _logPanelHeight = 180;

    public ObservableCollection<DockerContainerViewModel> AllContainers { get; } = new();
    public ObservableCollection<DockerContainerViewModel> Containers { get; } = new();
    public ObservableCollection<DockerImageViewModel> AllImages { get; } = new();
    public ObservableCollection<DockerImageViewModel> Images { get; } = new();

    /// <summary>磁盘占用摘要（镜像/容器/卷/构建缓存）。</summary>
    public ObservableCollection<string> DiskUsageTexts { get; } = new();

    public IReadOnlyList<int> TailChoices { get; } = new[] { 100, 500, 2000, 5000 };

    public bool IsReady => State == DockerState.Ready;
    public bool IsLoadingState => State == DockerState.Unknown;
    public bool IsNotInstalled => State == DockerState.NotInstalled;
    public bool IsFailedState => State == DockerState.Failed;
    public string ContainersCountText => $"容器（{AllContainers.Count}）";
    public string ImagesCountText => $"镜像（{AllImages.Count}）";

    public DockerViewModel(SshSession session, MainWindowViewModel owner)
    {
        _session = session;
        _owner = owner;
        _ = ProbeAsync();
    }

    partial void OnStateChanged(DockerState value)
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsLoadingState));
        OnPropertyChanged(nameof(IsNotInstalled));
        OnPropertyChanged(nameof(IsFailedState));
    }

    // ---------------------------------------------------------------- 探测

    private async Task ProbeAsync()
    {
        State = DockerState.Unknown;
        _docker = new DockerService(_session);
        try
        {
            DockerProbe probe;
            try
            {
                probe = await _docker.ProbeAsync();
            }
            catch (SudoPasswordRequiredException)
            {
                var (ok, pw) = await Dialogs.InputAsync(
                    "sudo 密码",
                    $"用户 {_session.Profile.Username} 不在 docker 组，需要 sudo 才能访问 Docker，请输入其密码：",
                    "", password: true);
                if (!ok || pw.Length == 0)
                {
                    FailText = "已取消：未提供 sudo 密码。";
                    State = DockerState.Failed;
                    return;
                }
                _session.SudoPassword = pw;
                probe = await _docker.ProbeAsync();
            }

            switch (probe.State)
            {
                case DockerState.Ready:
                    VersionText = "Docker " + probe.Version;
                    SudoMode = probe.Sudo;
                    State = DockerState.Ready;
                    await RefreshAllAsync();
                    break;
                case DockerState.NotInstalled:
                    State = DockerState.NotInstalled;
                    break;
                default:
                    FailText = probe.Error ?? "未知错误";
                    State = DockerState.Failed;
                    break;
            }
        }
        catch (Exception ex)
        {
            FailText = ex.Message;
            State = DockerState.Failed;
        }
    }

    [RelayCommand]
    private Task Recheck() => ProbeAsync();

    // ---------------------------------------------------------------- 列表刷新

    [RelayCommand]
    private Task Refresh() => RefreshAllAsync();

    private async Task RefreshAllAsync(bool silent = false)
    {
        if (State != DockerState.Ready || _reloading || IsBusy) return;
        _reloading = true;
        if (!silent) IsLoading = true;
        try
        {
            var containers = await _docker.ListContainersAsync();
            var images = await _docker.ListImagesAsync();

            // 磁盘占用（旧版 docker 不支持 --format 时忽略）
            try
            {
                var df = await _docker.DiskUsageAsync();
                DiskUsageTexts.Clear();
                foreach (var d in df)
                    DiskUsageTexts.Add($"{TypeName(d.Type)} {d.Size} · 可回收 {d.Reclaimable}");
            }
            catch
            {
                DiskUsageTexts.Clear();
            }

            AllContainers.Clear();
            foreach (var c in containers)
                AllContainers.Add(new DockerContainerViewModel(c, this));
            AllImages.Clear();
            foreach (var i in images)
                AllImages.Add(new DockerImageViewModel(i, this));

            // 资源占用单独采样（约 1~2 秒），失败不影响列表
            try
            {
                var stats = await _docker.StatsAsync();
                foreach (var s in stats)
                {
                    DockerContainerViewModel? vm = null;
                    if (s.Id.Length > 0)
                        vm = AllContainers.FirstOrDefault(c => c.Id.StartsWith(s.Id, StringComparison.Ordinal));
                    vm ??= AllContainers.FirstOrDefault(c => c.Name == s.Name);
                    if (vm == null) continue;
                    vm.CpuText = s.CpuPercent;
                    vm.MemText = $"{s.MemUsage}（{s.MemPercent}）";
                }
            }
            catch
            {
                foreach (var c in AllContainers) { c.CpuText = "—"; c.MemText = "—"; }
            }

            ApplyFilter();
            OnPropertyChanged(nameof(ContainersCountText));
            OnPropertyChanged(nameof(ImagesCountText));
        }
        catch (SudoPasswordRequiredException)
        {
            FailText = "需要 sudo 密码（可能已被清空），请点“重新检测”。";
            State = DockerState.Failed;
        }
        catch (Exception ex)
        {
            // 例如未安装 Docker / sudo 探测超时：显示失败卡片与原因，而不是把异常抛到后台任务
            FailText = "Docker 操作失败：" + ex.Message;
            State = DockerState.Failed;
            _owner.SetStatus("Docker 刷新失败：" + ex.Message, error: true);
        }
        finally
        {
            _reloading = false;
            IsLoading = false;
        }
    }

    private static string TypeName(string type) => type switch
    {
        "Images" => "镜像",
        "Containers" => "容器",
        "Local Volumes" => "卷",
        "Build Cache" => "构建缓存",
        _ => type,
    };

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = (SearchText ?? "").Trim();
        Containers.Clear();
        foreach (var c in AllContainers)
        {
            if (f.Length > 0 &&
                !c.Name.Contains(f, StringComparison.OrdinalIgnoreCase) &&
                !c.Image.Contains(f, StringComparison.OrdinalIgnoreCase) &&
                !c.Id.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                continue;
            Containers.Add(c);
        }
        Images.Clear();
        foreach (var i in AllImages)
        {
            if (f.Length > 0 &&
                !i.Repository.Contains(f, StringComparison.OrdinalIgnoreCase) &&
                !i.Tag.Contains(f, StringComparison.OrdinalIgnoreCase) &&
                !i.Id.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                continue;
            Images.Add(i);
        }
    }

    [RelayCommand]
    private void SelectList(string key)
    {
        ShowContainers = key == "containers";
        ShowImages = key == "images";
    }

    // ---------------------------------------------------------------- 自动刷新

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
        {
            if (_autoTimer == null)
            {
                _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _autoTimer.Tick += (_, _) =>
                {
                    if (State == DockerState.Ready && !_reloading && !IsBusy)
                        _ = RefreshAllAsync(silent: true);
                };
            }
            _autoTimer.Start();
        }
        else
        {
            _autoTimer?.Stop();
        }
    }

    // ---------------------------------------------------------------- 容器 / 镜像操作

    public Task StartContainerAsync(DockerContainerViewModel c)
        => OpAsync($"启动容器 {c.Name}", ct => _docker.StartAsync(c.Id, ct), "运行中");

    public Task StopContainerAsync(DockerContainerViewModel c)
        => OpAsync($"停止容器 {c.Name}", ct => _docker.StopAsync(c.Id, ct), "已退出");

    public Task RestartContainerAsync(DockerContainerViewModel c)
        => OpAsync($"重启容器 {c.Name}", ct => _docker.RestartAsync(c.Id, ct));

    public async Task RemoveContainerAsync(DockerContainerViewModel c)
    {
        if (IsBusy) return;
        if (!await Dialogs.ConfirmAsync(
                "删除容器",
                $"删除容器 “{c.Name}”（镜像 {c.Image}）？\n容器内未持久化的数据将丢失，此操作不可恢复。",
                danger: true, okText: "删除"))
            return;
        await OpAsync($"删除容器 {c.Name}", ct => _docker.RemoveContainerAsync(c.Id, ct));
    }

    public async Task RemoveImageAsync(DockerImageViewModel i)
    {
        if (IsBusy) return;
        var label = i.Repository == "<none>" ? $"悬空镜像 {i.ShortId}" : $"镜像 {i.Repository}:{i.Tag}";
        if (!await Dialogs.ConfirmAsync(
                "删除镜像",
                $"删除{label}？\n正在被容器使用的镜像无法删除。",
                danger: true, okText: "删除"))
            return;
        await OpAsync($"删除镜像 {i.RepoTag}", ct => _docker.RemoveImageAsync(i.Id, ct));
    }

    [RelayCommand]
    private async Task PruneImages()
    {
        if (IsBusy) return;
        if (!await Dialogs.ConfirmAsync(
                "清理悬空镜像",
                "删除全部悬空镜像（<none>:<none>，多为构建或拉取的中间产物）？\n带标签的镜像不受影响。",
                okText: "清理"))
            return;
        await OpAsync("清理悬空镜像", ct => _docker.PruneImagesAsync(ct));
    }

    [RelayCommand]
    private async Task SystemPrune()
    {
        if (IsBusy) return;
        if (!await Dialogs.ConfirmAsync(
                "深度清理",
                "将删除：所有已停止的容器、全部悬空镜像、未使用的网络、构建缓存。\n正在运行的容器与正在使用的镜像不受影响。",
                danger: true, okText: "开始清理"))
            return;
        await OpAsync("深度清理（docker system prune）", ct => _docker.SystemPruneAsync(ct));
    }

    /// <summary>统一操作流：忙判定 → 输出到面板 → sudo 补密重试 → 刷新列表。</summary>
    private async Task OpAsync(string title, Func<CancellationToken, Task<CommandResult>> op, string? waitHint = null)
    {
        if (IsBusy) return;
        IsBusy = true;
        PanelOpen = true;
        LogTitle = "操作输出";
        Logs.Line(">>> " + title);
        if (waitHint != null)
            Logs.Line($"提示：容器状态可能需要几秒才会变为“{waitHint}”完成");
        try
        {
            var r = await ExecuteWithSudoRetryAsync(op);
            if (r.ExitCode == 0)
                Logs.Line("✔ 完成");
            else
                Logs.Line($"✖ 失败（退出码 {r.ExitCode}）：" + FirstErrorLine(r));
        }
        catch (Exception ex)
        {
            Logs.Line("✖ " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            _ = RefreshAllAsync(silent: true);
        }
    }

    /// <summary>遇到需要 sudo 密码时弹窗补齐后重试一次。</summary>
    private async Task<CommandResult> ExecuteWithSudoRetryAsync(Func<CancellationToken, Task<CommandResult>> f)
    {
        try
        {
            return await f(CancellationToken.None);
        }
        catch (SudoPasswordRequiredException)
        {
            var (ok, pw) = await Dialogs.InputAsync(
                "sudo 密码",
                $"用户 {_session.Profile.Username} 需要提权执行此操作，请输入其密码：",
                "", password: true);
            if (!ok || pw.Length == 0)
                throw new SshManagerException("已取消：未提供 sudo 密码。");
            _session.SudoPassword = pw;
            return await f(CancellationToken.None);
        }
    }

    private static string FirstErrorLine(CommandResult r)
    {
        var s = (r.Output + " " + r.Error).Trim();
        if (s.Length == 0) return "未知错误";
        var i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).Trim();
    }

    // ---------------------------------------------------------------- 日志

    public async Task ShowLogsAsync(DockerContainerViewModel c)
    {
        PanelOpen = true;
        _logContainerId = c.Id;
        LogTitle = $"日志 · {c.Name}";
        await LoadLogsAsync();
    }

    [RelayCommand]
    private Task RefreshLogs() => LoadLogsAsync();

    [RelayCommand]
    private void ClearLog() => Logs.Clear();

    [RelayCommand]
    private void TogglePanel() => PanelOpen = !PanelOpen;

    /// <summary>尾部行数选择变化（视图调用）。</summary>
    internal void SetLogTail(int tail)
    {
        LogTail = tail;
        if (PanelOpen && _logContainerId != null)
            _ = LoadLogsAsync();
    }

    private async Task LoadLogsAsync()
    {
        if (_logContainerId == null || _logsBusy) return;
        _logsBusy = true;
        LogsLoading = true;
        Logs.Clear();
        try
        {
            var gotLines = false;
            var r = await _docker.LogsAsync(_logContainerId, LogTail, line =>
            {
                gotLines = true;
                Logs.Line(line);
            });
            if (!gotLines)
                Logs.Line(r.ExitCode == 0 ? "（该容器暂无日志输出）" : $"✖ 获取日志失败（退出码 {r.ExitCode}）");
        }
        catch (Exception ex)
        {
            Logs.Line("✖ " + ex.Message);
        }
        finally
        {
            _logsBusy = false;
            LogsLoading = false;
        }
    }

    /// <summary>“终端”按钮：把 docker exec 命令填入命令台（用户可改为 sh 后执行）。</summary>
    public void ExecHint(DockerContainerViewModel c)
    {
        var cmd = DockerService.BuildExecCommand(c.Name);
        _owner.PrefillInConsole(cmd);
        _owner.SetStatus($"命令已填入终端，回车执行；若容器没有 bash，可把结尾的 bash 改为 sh");
    }

    // ---------------------------------------------------------------- 清理

    /// <summary>断开连接时停止自动刷新定时器。</summary>
    internal void Cleanup()
    {
        _autoTimer?.Stop();
        _autoTimer = null;
    }
}
