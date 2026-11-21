using Avalonia.Controls;
using Avalonia.Interactivity;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class IpConfigView : UserControl
{
    public IpConfigView()
    {
        InitializeComponent();
    }

    private async void OnCopyApplyLog(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as NetworkViewModel;
        var text = vm?.Ip.ApplyLogText;
        if (string.IsNullOrEmpty(text)) return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip == null) return;
        try
        {
#pragma warning disable CS0618
            await clip.SetTextAsync(text);
#pragma warning restore CS0618
        }
        catch { /* 剪贴板不可用时忽略 */ }
    }
}
