using Avalonia.Controls;
using Avalonia.Interactivity;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class NetworkApplyDialog : Window
{
    public NetworkApplyDialog()
    {
        InitializeComponent();
    }

    private NetworkApplyDialogViewModel? Vm => DataContext as NetworkApplyDialogViewModel;

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (Vm?.TryConfirm() == true) Close(true);
    }
}
