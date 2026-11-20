using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SshManager.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close(false);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public ConfirmDialog(string title, string message, bool danger, string okText, bool mono)
        : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        OkButton.Classes.Add(danger ? "danger" : "primary");
        if (mono) MessageText.Classes.Add("mono");
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
