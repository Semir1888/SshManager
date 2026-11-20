using System.Text;
using Renci.SshNet;

namespace SshManager.Core.Services;

/// <summary>
/// 一个持久化 PTY（伪终端）会话：命令台“交互终端”模式的后端。
/// 拥有真正的 TTY，cd / export 等状态持久，sudo -i、top、vim 等交互程序可用。
/// 非线程安全：Write/Dispose 可从任意线程调用，DataReceived/Closed 在后台线程回调。
/// </summary>
public sealed class PtySession : IDisposable
{
    private readonly ShellStream _stream;
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private int _closedRaised;

    /// <summary>终端输出（后台线程回调，UI 需自行调度）。</summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>会话结束（远端 exit / 连接断开 / 本地 Dispose，后台线程回调）。</summary>
    public event Action? Closed;

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    private PtySession(ShellStream stream, int cols, int rows)
    {
        _stream = stream;
        Cols = cols;
        Rows = rows;
    }

    public static PtySession Open(SshClient ssh, int cols, int rows)
    {
        cols = Math.Clamp(cols, 10, 500);
        rows = Math.Clamp(rows, 4, 300);
        var stream = ssh.CreateShellStream(
            "xterm-256color", (uint)cols, (uint)rows, 0, 0, 4096, null);
        var session = new PtySession(stream, cols, rows);
        session.StartReadLoop();
        return session;
    }

    private void StartReadLoop()
    {
        Task.Run(async () =>
        {
            var buf = new byte[8192];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int n;
                    try { n = await _stream.ReadAsync(buf, 0, buf.Length, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch { break; } // 连接关闭 / 对象释放
                    if (n <= 0) break; // 流结束（shell 退出 / 远端关闭）→ 触发 Closed
                    var data = new byte[n];
                    Array.Copy(buf, data, n);
                    DataReceived?.Invoke(data);
                }
            }
            catch { /* 退出即结束 */ }
            finally
            {
                RaiseClosed();
            }
        });
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 1) return;
        try { Closed?.Invoke(); }
        catch { }
    }

    /// <summary>向终端发送键盘输入。</summary>
    public void Write(string text)
    {
        if (_disposed || text.Length == 0) return;
        try
        {
            lock (_writeLock)
            {
                _stream.Write(text);
                _stream.Flush();
            }
        }
        catch { /* 连接断开时忽略写入 */ }
    }

    /// <summary>向终端发送原始字节。</summary>
    public void Write(byte[] data)
    {
        if (_disposed || data.Length == 0) return;
        try
        {
            lock (_writeLock)
            {
                _stream.Write(data, 0, data.Length);
                _stream.Flush();
            }
        }
        catch { }
    }

    /// <summary>终端尺寸变化（vim/top 等全屏程序会收到 SIGWINCH 重新布局）。</summary>
    public void Resize(int cols, int rows)
    {
        if (_disposed) return;
        cols = Math.Clamp(cols, 10, 500);
        rows = Math.Clamp(rows, 4, 300);
        if (cols == Cols && rows == Rows) return;
        Cols = cols;
        Rows = rows;
        try { _stream.ChangeWindowSize((uint)cols, (uint)rows, 0, 0); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _stream.Dispose(); } catch { }
        RaiseClosed();
    }
}
