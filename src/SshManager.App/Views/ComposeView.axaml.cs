using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SshManager.App.Services;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class ComposeView : UserControl
{
    private ComposeViewModel? Vm => DataContext as ComposeViewModel;
    private ComposeViewModel? _hookedVm;

    public ComposeView()
    {
        InitializeComponent();
        DataContextChanged += HookViewModel;
        SizeChanged += (_, e) => ClampLogHeight(e.NewSize.Height);
    }

    private void HookViewModel(object? sender, EventArgs e)
    {
        if (_hookedVm != null)
        {
            _hookedVm.Logs.PropertyChanged -= OnLogTextChanged;
            _hookedVm.PropertyChanged -= OnVmPropertyChanged;
        }
        _hookedVm = Vm;
        if (_hookedVm == null) return;
        _hookedVm.Logs.PropertyChanged += OnLogTextChanged;
        _hookedVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComposeViewModel.PanelOpen) && Vm is { PanelOpen: true })
            ClampLogHeight(Bounds.Height);
    }

    /// <summary>窗口变矮时收缩日志面板，保证项目列表/服务表仍有可用高度。</summary>
    private void ClampLogHeight(double viewHeight)
    {
        if (Vm is not { PanelOpen: true }) return;
        var max = Math.Max(120, Math.Min(480, viewHeight - 300));
        if (Vm.LogPanelHeight > max) Vm.LogPanelHeight = max;
    }

    /// <summary>日志输出时自动滚动到底部（正在选中文本时不打断复制）。</summary>
    private void OnLogTextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UiLog.Text) || Vm?.PanelOpen != true) return;
        if (LogText.SelectionStart != LogText.SelectionEnd) return;
        Dispatcher.UIThread.Post(() => LogScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    // ------------------------------------------------------------ 日志复制 / 面板高度

    private bool _logGripDragging;
    private double _logGripStartY;
    private double _logGripStartHeight;

    private void OnLogGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm == null) return;
        _logGripDragging = true;
        _logGripStartY = e.GetPosition(this).Y;
        _logGripStartHeight = Vm.LogPanelHeight;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void OnLogGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_logGripDragging || Vm == null) return;
        var y = e.GetPosition(this).Y;
        var max = Math.Max(120, Math.Min(480, Bounds.Height - 300));
        Vm.LogPanelHeight = Math.Clamp(_logGripStartHeight + (_logGripStartY - y), 120, max);
        e.Handled = true;
    }

    private void OnLogGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_logGripDragging) return;
        _logGripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private async void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;
        try
        {
            if (e.Key == Key.A)
            {
                LogText.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.C)
            {
                var selected = LogText.SelectedText;
                if (string.IsNullOrEmpty(selected)) return;
                var clip = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clip == null) return;
#pragma warning disable CS0618
                await clip.SetTextAsync(selected);
#pragma warning restore CS0618
                e.Handled = true;
            }
        }
        catch { /* 剪贴板不可用时忽略 */ }
    }

    private async void OnCopyAllLogs(object? sender, RoutedEventArgs e)
    {
        var text = Vm?.Logs.Text;
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

    private void OnTailChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm == null || e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is int tail)
            Vm.SetLogTail(tail);
    }
}
