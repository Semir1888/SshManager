using Avalonia.Controls;
using Avalonia.Interactivity;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class ComposeProjectDialog : Window
{
    public ComposeProjectDialog()
    {
        InitializeComponent();
    }

    private ComposeProjectDialogViewModel? Vm => DataContext as ComposeProjectDialogViewModel;

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        if (await Vm.TrySaveAsync()) Close(true);
    }
}
