using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using SshManager.App.Services;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class AppCenterView : UserControl
{
    private AppCenterViewModel? Vm => DataContext as AppCenterViewModel;

    public AppCenterView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        EffectiveViewportChanged += OnViewportChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;
        vm.Log.PropertyChanged += (_, e2) =>
        {
            if (e2.PropertyName == nameof(UiLog.Text))
                Dispatcher.UIThread.Post(() => LogScroller.ScrollToEnd(), DispatcherPriority.Background);
        };
        vm.Entries.CollectionChanged += (_, _) =>
            CountText.Text = $"共 {vm.Entries.Count} 个应用";
    }

    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (Vm is { } vm && CountText is { } ct && string.IsNullOrEmpty(ct.Text))
            ct.Text = $"共 {vm.Entries.Count} 个应用";
    }

    private void OnInstallKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm != null)
        {
            Vm.InstallCommand.Execute(null);
            e.Handled = true;
        }
    }
}
