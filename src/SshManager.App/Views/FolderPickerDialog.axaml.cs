using Avalonia.Controls;
using Avalonia.Interactivity;
using SshManager.Core;
using SshManager.Core.Helpers;
using SshManager.Core.Services;

namespace SshManager.App.Views;

/// <summary>远程目录选择对话框：列出服务器上的子目录，用于部署包路径等场景。无权限目录自动 sudo 降级。</summary>
public partial class FolderPickerDialog : Window
{
    private readonly SshSession? _session;

    /// <summary>用户最终选择的目录（点击“选择此目录”后有效）。</summary>
    public string Result { get; private set; } = "/";

    private string Current { get; set; } = "/";

    public FolderPickerDialog()
    {
        InitializeComponent();
        PathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) OnGo(null, new RoutedEventArgs());
        };
    }

    public FolderPickerDialog(SshSession session, string startDir) : this()
    {
        _session = session;
        Current = string.IsNullOrWhiteSpace(startDir) ? "/" : UnixPath.Normalize(startDir);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = LoadAsync(Current);
    }

    private async Task LoadAsync(string dir)
    {
        if (_session == null) return;
        Current = UnixPath.Normalize(dir);
        PathBox.Text = Current;
        StatusText.Text = "读取中…";
        DirList.ItemsSource = null;
        try
        {
            // SFTP 优先；无权限且允许时自动 sudo 降级（与文件管理一致）
            var listing = await _session.ListDirectoryExAsync(Current, _session.Profile.AutoSudo);
            if (listing.Error != null)
            {
                DirList.ItemsSource = null;
                StatusText.Text = "读取失败：" + listing.Error;
                return;
            }
            var dirs = listing.Items
                .Where(i => i.IsDirectory)
                .Select(i => i.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            DirList.ItemsSource = dirs;
            StatusText.Text = $"{dirs.Count} 个子目录 · 当前：{Current}" +
                              (listing.UsedSudo ? "（sudo 提权）" : "");
        }
        catch (SudoPasswordRequiredException)
        {
            var d = new InputDialog("sudo 密码",
                $"用户 {_session.Profile.Username} 无权访问 {Current}，提权浏览需要提供 sudo 密码：",
                "", password: true, okText: "提权浏览");
            var ok = await d.ShowDialog<bool>(this);
            if (ok && d.Value.Length > 0)
            {
                _session.SudoPassword = d.Value;
                await LoadAsync(dir); // 拿到密码后重试
            }
            else
            {
                StatusText.Text = "已取消：未提供 sudo 密码，无法浏览该目录。";
            }
        }
        catch (Exception ex)
        {
            DirList.ItemsSource = null;
            StatusText.Text = "读取失败：" + ex.Message;
        }
    }

    private void OnGo(object? sender, RoutedEventArgs e)
    {
        var t = (PathBox.Text ?? "").Trim();
        if (t.Length > 0) _ = LoadAsync(t);
    }

    private void OnUp(object? sender, RoutedEventArgs e)
        => _ = LoadAsync(UnixPath.Parent(Current));

    private void OnItemDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DirList.SelectedItem is string name)
            _ = LoadAsync(UnixPath.Combine(Current, name));
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var t = (PathBox.Text ?? "").Trim();
        Result = t.Length > 0 ? UnixPath.Normalize(t) : Current;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
