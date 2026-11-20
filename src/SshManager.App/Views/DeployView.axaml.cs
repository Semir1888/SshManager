using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class DeployView : UserControl
{
    private DeployViewModel? Vm => DataContext as DeployViewModel;

    public DeployView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;
        vm.Log.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.Log.Text))
                PostScroll();
        };
    }

    private void PostScroll()
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogScroller.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void OnDirKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm?.ScanCommand.CanExecute(null) == true)
        {
            Vm.ScanCommand.Execute(null);
            e.Handled = true;
        }
    }
}
