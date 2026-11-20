using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SshManager.App.Views;

namespace SshManager.App.Services;

/// <summary>简单的代码驱动弹窗集合（避开繁琐的 DataContext 搭建）。</summary>
public static class Dialogs
{
    public static Window? MainWindow
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static Task<bool> ConfirmAsync(string title, string message, bool danger = false, string okText = "确认", bool mono = false)
        => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var d = new ConfirmDialog(title, message, danger, okText, mono);
            return await d.ShowDialog<bool>(MainWindow!);
        });

    /// <summary>首次连接时确认服务器主机密钥指纹。</summary>
    public static Task<bool> ConfirmFingerprintAsync(string fingerprint)
        => ConfirmAsync(
            "确认服务器指纹",
            $"首次连接该服务器。\n主机密钥指纹 (md5)：\n{fingerprint}\n\n请与服务器上 ssh-keygen -l /etc/ssh/ssh_host_ed25519_key.pub 的输出比对后确认。",
            danger: false, okText: "信任并连接", mono: true);

    public static Task<(bool Ok, string Value)> InputAsync(string title, string message, string init = "", bool password = false, string okText = "确定")
        => Dispatcher.UIThread.InvokeAsync<(bool, string)>(async () =>
        {
            var d = new InputDialog(title, message, init, password, okText);
            var ok = await d.ShowDialog<bool>(MainWindow!);
            return (ok, d.Value);
        });

    /// <summary>
    /// 三选一（0 = 确定，1 = 次要操作，2 = 取消；点 X / Esc 也视为取消）。
    /// 用于“保存 / 不保存 / 取消”。
    /// </summary>
    public static Task<int> ChoiceAsync(string title, string message, string okText, string discardText)
        => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var d = new ChoiceDialog(title, message, okText, discardText);
            // 窗口被 X 关闭时 ShowDialog 返回 default(int?) = null，按取消处理
            var r = await d.ShowDialog<int?>(MainWindow!);
            return r ?? 2;
        });

    public static Task InfoAsync(string title, IReadOnlyList<DialogRow> rows)
        => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var d = new InfoDialog(title, rows);
            await d.ShowDialog<bool>(MainWindow!);
        });
}

public sealed record DialogRow(string Key, string Value);
