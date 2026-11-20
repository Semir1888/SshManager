using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class ConnectDialog : Window
{
    private ConnectDialogViewModel Vm => (ConnectDialogViewModel)DataContext!;

    public ConnectDialog()
    {
        InitializeComponent();
        // 打开即聚焦密码框（密码登录时）；私钥登录则聚焦私钥路径
        Opened += (_, _) =>
        {
            if (Vm.AuthPassword) PwBox.Focus();
            else if (Vm.AuthKey) KeyPathBox.Focus();
            else HostBox.Focus();
        };
        PwBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) OnConnect(this, new RoutedEventArgs());
            if (e.Key == Avalonia.Input.Key.Escape) Close(false);
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close(false);
        }, RoutingStrategies.Tunnel);
    }

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        Vm.Password = PwBox.Text;
        Vm.KeyPassphrase = KeyPwBox.Text;
        if (await Vm.ConnectAsync())
            Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnBrowseKey(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择私钥文件",
            AllowMultiple = false,
        });
        if (files.Count > 0)
            KeyPathBox.Text = files[0].Path.LocalPath;
    }
}
