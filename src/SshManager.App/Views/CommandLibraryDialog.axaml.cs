using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SshManager.App.Services;

namespace SshManager.App.Views;

/// <summary>用户在命令库中的选择结果。Run=false 表示只填入输入框。</summary>
public sealed record LibraryPick(QuickCommand Command, bool Run);

public partial class CommandLibraryDialog : Window
{
    private readonly ObservableCollection<QuickCommand> _view = new();

    private string _filter = "";
    private string _category = "全部分类";

    public CommandLibraryDialog()
    {
        InitializeComponent();
        CategoryBox.ItemsSource = new[] { "全部分类" }.Concat(CommandLibrary.Categories).ToArray();
        CategoryBox.SelectedIndex = 0;
        List.ItemsSource = _view;
        List.DoubleTapped += (_, _) => Insert(false);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) Close(null);
        }, RoutingStrategies.Tunnel);
        Refresh();
        Opened += (_, _) => SearchBox.Focus();
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text ?? "";
        Refresh();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (List.ItemCount > 0)
            {
                List.SelectedIndex = 0;
                Insert(false);
            }
            e.Handled = true;
        }
    }

    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        _category = CategoryBox.SelectedItem as string ?? "全部分类";
        Refresh();
    }

    private void Refresh()
    {
        // XAML 填充阶段（ItemsSource/SelectedIndex 赋值）控件可能尚未全部就绪
        if (List == null || CountText == null) return;
        var f = _filter.Trim();
        _view.Clear();
        foreach (var c in CommandLibrary.All)
        {
            if (_category != "全部分类" && c.Category != _category) continue;
            if (f.Length > 0 &&
                !c.Name.Contains(f, StringComparison.OrdinalIgnoreCase) &&
                !c.Command.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            _view.Add(c);
        }
        CountText.Text = $"共 {_view.Count} 条";
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e) => Insert(false);

    private void OnInsert(object? sender, RoutedEventArgs e) => Insert(false);

    private void OnRun(object? sender, RoutedEventArgs e) => Insert(true);

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);

    private void Insert(bool run)
    {
        if (List.SelectedItem is not QuickCommand cmd) return;
        // “需编辑”的命令含占位符，执行按钮也降级为只填入
        Close(new LibraryPick(cmd, run && !cmd.NeedEdit));
    }
}
