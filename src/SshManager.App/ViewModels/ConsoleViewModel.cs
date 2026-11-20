using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.App.Services;
using SshManager.Core;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

/// <summary>一次命令执行的展示单元（含节流输出）。</summary>
public partial class CommandRunViewModel : ViewModelBase
{
    public string Command { get; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public string StartedAtText => StartedAt.ToString("HH:mm:ss");
    public System.Threading.CancellationTokenSource Cts { get; } = new();
    public UiLog Out { get; } = new();

    [ObservableProperty]
    private string _status = "运行中";

    [ObservableProperty]
    private bool _isRunningStatus = true;

    [ObservableProperty]
    private bool _hasOutput;

    [ObservableProperty]
    private Avalonia.Media.IBrush _statusBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2dd4bf"));

    public CommandRunViewModel(string command)
    {
        Command = command;
        Out.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Out.Text))
                HasOutput = Out.Text.Length > 0;
        };
    }

    [RelayCommand]
    private void StopRun()
    {
        try { Cts.Cancel(); } catch { }
    }

    public void Finish(CommandResult r)
    {
        IsRunningStatus = false;
        if (r.WasCancelled)
        {
            Status = "已停止";
            StatusBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8b93a5"));
        }
        else if (r.ExitCode == 0)
        {
            Status = "完成 · 0";
            StatusBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#10b981"));
        }
        else
        {
            Status = $"退出码 {r.ExitCode}";
            StatusBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#f43f5e"));
        }
    }

    public void Fail(string message)
    {
        IsRunningStatus = false;
        Status = "失败";
        StatusBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#f43f5e"));
        Out.Line("✖ " + message);
    }
}

public partial class ConsoleViewModel : ViewModelBase
{
    private readonly SshSession _session;
    private readonly List<string> _history = new();
    private int _historyIndex = -1;

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _sudoHint;

    /// <summary>交互终端（PTY）模式：持久 shell，cd/export 状态保留，
    /// 支持 sudo -i、top、vim 等所有交互命令。默认关闭（单次执行模式）。</summary>
    [ObservableProperty] private bool _isPtyMode;

    /// <summary>交互终端模式的 ViewModel（模式开启时使用）。</summary>
    public TerminalViewModel Pty { get; }

    public string ModeHint => IsPtyMode
        ? "交互终端模式：真实 PTY 会话，cd / sudo -i / top / vim 等所有命令可用，直接键盘输入"
        : "单次执行模式：每条命令独立执行（cd 等状态不保留），适合批量执行/查看输出";

    public ObservableCollection<CommandRunViewModel> Runs { get; } = new();

    /// <summary>顶部快捷命令（点按即执行，只读安全命令）。</summary>
    public IReadOnlyList<QuickCommand> Presets { get; } = CommandLibrary.Chips;

    public ConsoleViewModel(SshSession session)
    {
        _session = session;
        Pty = new TerminalViewModel(session);
        SudoHint = session.IsRoot
            ? "当前为 root 用户，所有命令直接执行"
            : "当前为普通用户：sudo 命令将使用已保存的 sudo 密码自动执行";
    }

    partial void OnIsPtyModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeHint));
        if (value) Pty.Attach();   // 开启：建立 PTY
        else Pty.Detach();         // 关闭：结束 PTY
    }

    /// <summary>切到单次执行模式。</summary>
    [RelayCommand]
    private void UseSingle() => IsPtyMode = false;

    /// <summary>切到交互终端模式。</summary>
    [RelayCommand]
    private void UsePty() => IsPtyMode = true;

    /// <summary>终端清屏（含回滚历史）。</summary>
    [RelayCommand]
    private void PtyClear() => Pty.ClearScreen();

    /// <summary>向终端发送 Ctrl+C。</summary>
    [RelayCommand]
    private void PtyCtrlC() => Pty.SendBreak();

    /// <summary>关闭并重建 PTY 会话（shell 状态重置）。</summary>
    [RelayCommand]
    private void PtyReopen()
    {
        Pty.Detach();
        Pty.Attach();
    }

    /// <summary>会话断开时由 MainWindowViewModel 调用。</summary>
    internal void Cleanup()
    {
        IsPtyMode = false;
        Pty.Cleanup();
    }

    /// <summary>概览页快捷命令入口（自动切回单次执行模式）。</summary>
    internal void RunExternal(string command)
    {
        if (IsPtyMode) IsPtyMode = false;
        Input = command;
        _ = RunAsync();
    }

    /// <summary>只填入不执行（Docker 页“进入终端”等场景：命令可能需要用户修改后自行执行）。</summary>
    internal void PrefillExternal(string command)
    {
        if (IsPtyMode) IsPtyMode = false;
        Input = command;
    }

    [RelayCommand]
    private Task UsePreset(QuickCommand? preset)
    {
        if (preset is null) return Task.CompletedTask;
        Input = preset.Command;
        return RunAsync();
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        var text = (Input ?? "").Trim();
        if (text.Length == 0 || IsRunning) return;
        Input = "";
        _history.Add(text);
        _historyIndex = -1;

        var run = new CommandRunViewModel(text);
        Runs.Add(run);

        if (IsInteractiveCommand(text))
        {
            run.Fail("该命令需要真实终端（PTY），单次执行模式无法运行。\n" +
                     "请打开上方“模式”开关切换到“交互终端（PTY）”，再执行该命令。");
            return;
        }

        var command = text;
        if (text.StartsWith("sudo ") && !_session.IsRoot)
        {
            var rest = text[5..].TrimStart();
            if (rest is "-i" or "-s" or "su" || rest.StartsWith("-i ") || rest.StartsWith("-s ") || rest.StartsWith("su "))
            {
                run.Fail("单次执行模式不支持交互式登录 shell（sudo -i / -s / su）。\n请切换到上方“交互终端”模式，即可直接运行 sudo -i、top、vim 等所有命令。");
                return;
            }
            try
            {
                command = _session.WrapSudo(rest);
            }
            catch (SudoPasswordRequiredException)
            {
                var (ok, pw) = await Dialogs.InputAsync(
                    "sudo 密码",
                    $"用户 {_session.Profile.Username} 需要提权执行此命令，请输入其密码：",
                    "", password: true);
                if (!ok || pw.Length == 0)
                {
                    run.Fail("已取消：未提供 sudo 密码。");
                    return;
                }
                _session.SudoPassword = pw;
                command = _session.WrapSudo(rest);
            }
        }

        IsRunning = true;
        try
        {
            var result = await _session.RunStreamingAsync(command, run.Out.Line, run.Out.Line, run.Cts.Token);
            run.Finish(result);
        }
        catch (SshManagerException ex)
        {
            run.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            run.Fail(ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        var running = Runs.LastOrDefault(r => r.Status == "运行中");
        if (running != null)
        {
            try { running.Cts.Cancel(); } catch { }
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Runs.Clear();
    }

    /// <summary>键盘历史导航（视图调用）。</summary>
    internal string? PrevInput()
    {
        if (_history.Count == 0) return null;
        _historyIndex = _historyIndex < 0 ? _history.Count - 1 : Math.Max(0, _historyIndex - 1);
        return _history[_historyIndex];
    }

    internal string? NextInput()
    {
        if (_historyIndex < 0) return null;
        _historyIndex++;
        if (_historyIndex >= _history.Count)
        {
            _historyIndex = -1;
            return "";
        }
        return _history[_historyIndex];
    }

    /// <summary>
    /// 单次执行模式下检测常见全屏/TUI 命令，提示用户切换到交互终端（PTY）。
    /// 例如 htop/top/vim 需要真实 TTY，在单次模式会直接报“terminal”相关错误。
    /// </summary>
    private static bool IsInteractiveCommand(string command)
    {
        var text = command.Trim();
        while (text.StartsWith("sudo ", StringComparison.Ordinal))
            text = text[5..].TrimStart();

        var token = text.Split(' ', '\t')[0];
        var slash = token.LastIndexOf('/');
        if (slash >= 0) token = token[(slash + 1)..];

        return token switch
        {
            "htop" or "btop" or "vim" or "vi" or "nano" or "less" or "more" or "man"
                or "watch" or "tmux" or "screen" or "ncdu" or "mc" or "ranger" or "lf"
                or "emacs" => true,
            // top 的批处理模式（top -b ...）可以单次执行；交互模式不行
            "top" => !text.Contains(" -b", StringComparison.Ordinal)
                     && !text.Contains("\t-b", StringComparison.Ordinal),
            _ => false,
        };
    }
}
