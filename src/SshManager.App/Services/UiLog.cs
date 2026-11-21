using System.Collections.Generic;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SshManager.App.Services;

/// <summary>
/// 后台线程持续追加、UI 线程节流刷新的文本日志（用于命令输出等高频内容）。
/// </summary>
public partial class UiLog : ObservableObject
{
    private readonly object _lock = new();
    private readonly List<string> _pending = new();
    private readonly StringBuilder _sb = new();
    private int _scheduled;

    [ObservableProperty]
    private string _text = "";

    /// <summary>任意线程调用；自动调度到 UI 线节流刷新。</summary>
    public void Line(string line)
    {
        lock (_lock) _pending.Add(RedactSudo(line));
        TrySchedule();
    }

    /// <summary>
    /// 防御性脱敏：命令超时等错误信息可能回显完整命令，其中 sudo 包装包含密码
    /// （echo 'pw' | sudo -S -p '' …）。日志中一律去掉密码部分。
    /// </summary>
    private static string RedactSudo(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;
        const string marker = "| sudo -S -p ''";
        while (true)
        {
            var end = line.IndexOf(marker, StringComparison.Ordinal);
            if (end < 0) return line;
            var start = line.LastIndexOf("echo ", end, StringComparison.Ordinal);
            if (start < 0) return line;
            line = line[..start] + "sudo -S -p ''" + line[(end + marker.Length)..];
        }
    }

    public void Clear()
    {
        lock (_lock) _pending.Clear();
        _sb.Clear();
        if (Dispatcher.UIThread.CheckAccess()) Text = "";
        else Dispatcher.UIThread.Post(() => Text = "");
    }

    private void TrySchedule()
    {
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0) return;
        _ = FlushAsync();
    }

    private async Task FlushAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(70);
                List<string> taken;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    lock (_lock)
                    {
                        taken = new List<string>(_pending);
                        _pending.Clear();
                    }
                    foreach (var l in taken) _sb.AppendLine(l);
                    if (_sb.Length > 200_000) _sb.Remove(0, _sb.Length - 200_000);
                    Text = _sb.ToString();
                });
                lock (_lock)
                {
                    if (_pending.Count == 0) return;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _scheduled, 0);
            lock (_lock)
            {
                if (_pending.Count > 0) TrySchedule();
            }
        }
    }
}
