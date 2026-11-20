using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SshManager.App.Views;

/// <summary>
/// 三选一对话框（确定 / 次要操作 / 取消）。
/// 返回值：0 = 确定，1 = 次要操作，null/2 = 取消（含点窗口 X 关闭）。
/// </summary>
public partial class ChoiceDialog : Window
{
    public ChoiceDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close(2);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public ChoiceDialog(string title, string message, string okText, string discardText)
        : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        DiscardButton.Content = discardText;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(0);

    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(1);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(2);
}
