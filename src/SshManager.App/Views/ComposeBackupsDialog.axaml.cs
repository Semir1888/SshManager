using Avalonia.Controls;
using Avalonia.Interactivity;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class ComposeBackupsDialog : Window
{
    public ComposeBackupsDialog()
    {
        InitializeComponent();
    }

    private ComposeBackupsViewModel? Vm => DataContext as ComposeBackupsViewModel;

    private async void OnReload(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) await Vm.LoadAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);
}
