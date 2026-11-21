using Avalonia.Controls;
using Avalonia.Interactivity;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class ComposeUpgradeDialog : Window
{
    public ComposeUpgradeDialog()
    {
        InitializeComponent();
    }

    private ComposeUpgradeViewModel? Vm => DataContext as ComposeUpgradeViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 已绑定本地目录的项目：打开对话框即自动比对，保持“一键升级”的节奏
        Vm?.AutoScanIfReady();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        if (Vm.TryBuildDecision()) Close(true);
    }
}
