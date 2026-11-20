using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

/// <summary>
/// 编辑器专用多行文本框：Ctrl+S 保存、Tab 插入 4 空格缩进、光标行列跟踪。
/// </summary>
public sealed class EditorTextBox : TextBox
{
    private const string Indent = "    ";

    /// <summary>
    /// 派生类必须指回基类样式键，否则 Fluent 主题按具体类型查找 ControlTheme
    /// 会找不到 TextBox 模板，控件将渲染为空白（无文字、无法输入）。
    /// </summary>
    protected override Type StyleKeyOverride => typeof(TextBox);

    public EditorTextBox()
    {
        AcceptsReturn = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        (DataContext as EditorTabViewModel)?.UpdateCaret(CaretIndex);
    }

    /// <summary>光标或文本变化时更新行列显示。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CaretIndexProperty || change.Property == TextProperty)
            (DataContext as EditorTabViewModel)?.UpdateCaret(CaretIndex);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (DataContext is EditorTabViewModel vm)
            {
                e.Handled = true;
                vm.SaveCommand.Execute(null);
            }
            return;
        }

        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            var text = Text ?? "";
            var start = Math.Min(SelectionStart, SelectionEnd);
            var end = Math.Max(SelectionStart, SelectionEnd);
            Text = text[..start] + Indent + text[end..];
            CaretIndex = start + Indent.Length;
            return;
        }

        base.OnKeyDown(e);
    }
}
