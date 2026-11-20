using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SshManager.App.Services.Terminal;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

/// <summary>
/// 命令台“交互终端（PTY）”模式的 ViewModel：
/// 持有 PTY 会话 + VT 解码器 + 终端缓冲，桥接输入输出。
/// 解码与缓冲全部在 UI 线程执行（线程安全由 Dispatcher.Post 保障）。
/// </summary>
public partial class TerminalViewModel : ViewModelBase
{
    private readonly SshSession _session;
    private PtySession? _pty;
    private VtDecoder? _decoder;

    public TerminalBuffer Buffer { get; } = new(100, 28);

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _title = "交互终端";
    [ObservableProperty] private string _statusText = "未连接";

    public TerminalViewModel(SshSession session)
    {
        _session = session;
        Buffer.TitleChanged += t =>
        {
            if (t.Trim().Length > 0) Title = t.Trim();
        };
    }

    public bool IsAttached => _pty != null;

    /// <summary>打开 PTY 会话（已打开则忽略）。失败时状态栏显示原因。</summary>
    public void Attach()
    {
        if (_pty != null) return;
        try
        {
            var decoder = new VtDecoder(Buffer);
            _decoder = decoder;
            decoder.ResponseRequested += r => _pty?.Write(r);

            var pty = _session.OpenPty(Buffer.Cols, Buffer.Rows);
            _pty = pty;
            pty.DataReceived += OnPtyData;
            pty.Closed += OnPtyClosed;
            IsConnected = true;
            StatusText = "已连接";
        }
        catch (Exception ex)
        {
            StatusText = "打开终端失败：" + ex.Message;
        }
    }

    private void OnPtyData(byte[] data)
    {
        // 后台读线程 → UI 线程解码（buffer/decoder 仅 UI 线程访问）
        Dispatcher.UIThread.Post(() =>
        {
            try { _decoder?.Feed(data, data.Length); }
            catch { /* 解析异常不致命 */ }
        });
    }

    private void OnPtyClosed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            if (_pty != null)
            {
                _pty.DataReceived -= OnPtyData;
                _pty.Closed -= OnPtyClosed;
                _pty = null;
            }
            StatusText = "终端已关闭（shell 退出或连接断开）。可点击“重新打开”。";
        });
    }

    /// <summary>发送键盘输入到远端。</summary>
    public void Send(string text)
    {
        if (text.Length > 0) _pty?.Write(text);
    }

    /// <summary>发送一行（清屏按钮等快捷入口）。</summary>
    public void SendLine(string text) => _pty?.Write(text + "\r");

    /// <summary>发送 Ctrl+C。</summary>
    public void SendBreak() => _pty?.Write("\x03");

    /// <summary>终端区域尺寸变化。</summary>
    public void Resize(int cols, int rows)
    {
        cols = Math.Clamp(cols, 10, 500);
        rows = Math.Clamp(rows, 4, 300);
        Buffer.Resize(cols, rows);
        _pty?.Resize(cols, rows);
    }

    /// <summary>清空回滚历史并清屏。</summary>
    public void ClearScreen() => Buffer.ClearAll();

    /// <summary>关闭 PTY（切回单次模式 / 断开连接时调用）。</summary>
    public void Detach()
    {
        var pty = _pty;
        if (pty == null) return;
        _pty = null;
        pty.DataReceived -= OnPtyData;
        pty.Closed -= OnPtyClosed;
        try { pty.Dispose(); } catch { }
        IsConnected = false;
        StatusText = "未连接";
    }

    /// <summary>会话断开时的强制清理（MainWindowViewModel 调用）。</summary>
    internal void Cleanup() => Detach();
}
