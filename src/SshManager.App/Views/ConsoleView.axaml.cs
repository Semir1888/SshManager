using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using SshManager.App.Services;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class ConsoleView : UserControl
{
    private ConsoleViewModel? Vm => DataContext as ConsoleViewModel;

    public ConsoleView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;
        vm.Runs.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                PostScroll();
        };
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ConsoleViewModel.IsRunning))
                PostScroll();
        };
    }

    // ------------------------------------------------------------ 命令库

    private async void OnOpenLibrary(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var vm = Vm;
            if (vm == null) return;
            var owner = TopLevel.GetTopLevel(this) as Window ?? Dialogs.MainWindow;
            if (owner == null) return;
            var dlg = new CommandLibraryDialog();
            var pick = await dlg.ShowDialog<LibraryPick?>(owner);
            if (pick == null) return;

            vm.Input = pick.Command.Command;
            if (pick.Run)
            {
                if (pick.Command.Danger)
                {
                    var ok = await Dialogs.ConfirmAsync(
                        "危险命令确认",
                        $"“{pick.Command.Name}” 属于危险操作：\n{pick.Command.Command}\n\n确定执行？",
                        danger: true, okText: "执行", mono: true);
                    if (!ok) return;
                }
                vm.RunExternal(pick.Command.Command);
            }
            else
            {
                // 填入后聚焦输入框，光标移到末尾方便改占位符
                InputBox.Focus();
                InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
            }
        }
        catch { /* 对话框异常不致命 */ }
    }

    private void PostScroll()
    {
        Dispatcher.UIThread.Post(() =>
        {
            OutScroller.ScrollToEnd();
            InputBox.Focus();
        }, DispatcherPriority.Background);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;
        switch (e.Key)
        {
            case Key.Enter:
                vm.RunCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                var prev = vm.PrevInput();
                if (prev != null)
                {
                    vm.Input = prev;
                    e.Handled = true;
                }
                break;
            case Key.Down:
                var next = vm.NextInput();
                if (next != null)
                {
                    vm.Input = next;
                    e.Handled = true;
                }
                break;
        }
    }
}
