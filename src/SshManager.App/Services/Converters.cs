using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SshManager.App.Services;

/// <summary>XAML 绑定转换器。</summary>
public static class Converters
{
    /// <summary>bool → TextWrapping（编辑器“自动换行”开关）。</summary>
    public static readonly IValueConverter BoolToWrap =
        new FuncValueConverter<bool, TextWrapping>(b => b ? TextWrapping.Wrap : TextWrapping.NoWrap);

    /// <summary>bool 取反（忙碌时禁用按钮等）。</summary>
    public static readonly IValueConverter Not =
        new FuncValueConverter<bool, bool>(b => !b);
}
