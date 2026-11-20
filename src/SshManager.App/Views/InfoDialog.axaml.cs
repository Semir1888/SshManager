using Avalonia.Controls;
using Avalonia.Interactivity;
using SshManager.App.Services;

namespace SshManager.App.Views;

public partial class InfoDialog : Window
{
    public InfoDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close(true);
        }, RoutingStrategies.Tunnel);
    }

    public InfoDialog(string title, IReadOnlyList<DialogRow> rows) : this()
    {
        Title = title;
        TitleText.Text = title;
        RowsList.ItemsSource = rows;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);
}
