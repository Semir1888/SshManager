using Avalonia.Controls;
using Avalonia.Input;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class MainWindow : Window
{
    private bool _gripDragging;
    private double _gripStartY;
    private double _gripStartHeight;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnProfileDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            sender is ListBox { SelectedItem: ProfileItemViewModel item })
        {
            _ = vm.ConnectProfileAsync(item);
        }
    }

    // ------------------------------------------------------------ 传输抽屉高度拖拽

    private void OnTransferGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        _gripDragging = true;
        _gripStartY = e.GetPosition(this).Y;
        _gripStartHeight = vm.TransfersHeight;
        e.Pointer.Capture(TransferGrip);
        e.Handled = true;
    }

    private void OnTransferGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_gripDragging || DataContext is not MainWindowViewModel vm) return;
        var y = e.GetPosition(this).Y;
        // 向上拖 = 增大高度
        vm.SetTransfersHeight(_gripStartHeight + (_gripStartY - y));
        e.Handled = true;
    }

    private void OnTransferGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_gripDragging) return;
        _gripDragging = false;
        e.Pointer.Capture(null);
        (DataContext as MainWindowViewModel)?.PersistTransfersHeight();
        e.Handled = true;
    }
}
