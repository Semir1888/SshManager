using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SshManager.App.Views;

public partial class InputDialog : Window
{
    private readonly bool _password;

    public string Value { get; private set; } = "";

    public InputDialog()
    {
        InitializeComponent();
        void Submit(object? s, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Enter) OnOk(s, new RoutedEventArgs());
            if (e.Key == Avalonia.Input.Key.Escape) Close(false);
        }
        ValueBox.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, Submit, RoutingStrategies.Tunnel);
        PwBox.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, Submit, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close(false);
        }, RoutingStrategies.Tunnel);
    }

    public InputDialog(string title, string message, string init, bool password, string okText)
        : this()
    {
        Title = title;
        _password = password;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        ValueBox.IsVisible = !password;
        PwBox.IsVisible = password;
        if (!password) ValueBox.Text = init;
        else PwBox.Text = init;
        Opened += (_, _) => ((Avalonia.Input.InputElement)(password ? PwBox : ValueBox)).Focus();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Value = _password ? PwBox.Text ?? "" : ValueBox.Text ?? "";
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
