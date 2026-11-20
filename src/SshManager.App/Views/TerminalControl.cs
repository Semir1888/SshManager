using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using SshManager.App.Services.Terminal;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

/// <summary>
/// 终端渲染控件：等宽字符网格绘制（256 色 + RGB + 粗体/反色）+ 键盘输入映射 +
/// 回滚滚轮查看。配色跟随应用主题（深/浅两套）。每个字符独立锚定在自己的
/// 单元格内（字符缓存，居中绘制），与背景色块、光标三者严格同一网格，
/// 不随行长累积偏移；光标为 Windows 控制台风格的细竖线并闪烁（失焦常亮）。
/// </summary>
public class TerminalControl : Control
{
    // ------------------------------------------------------------ 依赖属性

    public static readonly StyledProperty<TerminalViewModel?> TerminalProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalViewModel?>(nameof(Terminal));

    public TerminalViewModel? Terminal
    {
        get => GetValue(TerminalProperty);
        set => SetValue(TerminalProperty, value);
    }

    // ------------------------------------------------------------ 主题配色

    /// <summary>一套终端主题：背景/前景/光标 + 基础 16 色。</summary>
    private sealed record TermTheme(Color Bg, Color Fg, Color Cursor, Color[] Base16);

    // 深色主题（沿用原配色）
    private static readonly Color[] DarkBase16 =
    {
        Color.Parse("#1c1f26"), Color.Parse("#cc0401"), Color.Parse("#19cb00"), Color.Parse("#cecb00"),
        Color.Parse("#0d73cc"), Color.Parse("#cb1ed1"), Color.Parse("#0dcdcd"), Color.Parse("#dddddd"),
        Color.Parse("#767676"), Color.Parse("#f2201f"), Color.Parse("#23fd00"), Color.Parse("#ffad0c"),
        Color.Parse("#1a8fff"), Color.Parse("#fd28ff"), Color.Parse("#14ffff"), Color.Parse("#ffffff"),
    };

    // 浅色主题（浅底友好的亮度适配版）
    private static readonly Color[] LightBase16 =
    {
        Color.Parse("#616161"), Color.Parse("#a31705"), Color.Parse("#0c7a0c"), Color.Parse("#8b6c00"),
        Color.Parse("#0d4faf"), Color.Parse("#8c2fa8"), Color.Parse("#0b7c7c"), Color.Parse("#c9c9c9"),
        Color.Parse("#8a8a8a"), Color.Parse("#d82416"), Color.Parse("#14a814"), Color.Parse("#b08800"),
        Color.Parse("#1858c4"), Color.Parse("#a642c4"), Color.Parse("#0fa0a0"), Color.Parse("#ffffff"),
    };

    private static readonly TermTheme DarkTheme =
        new(Color.Parse("#0c0f14"), Color.Parse("#d8dee9"), Color.Parse("#d8dee9"), DarkBase16);

    private static readonly TermTheme LightTheme =
        new(Color.Parse("#fafbfc"), Color.Parse("#24292f"), Color.Parse("#24292f"), LightBase16);

    /// <summary>当前应使用的主题（跟随应用主题切换，与 ConsoleView 的 C_TermBg 保持一致）。</summary>
    private TermTheme CurrentTheme =>
        Application.Current?.RequestedThemeVariant == ThemeVariant.Light ? LightTheme : DarkTheme;

    /// <summary>xterm 标准 256 色调色板（16-255；基础 16 色按主题从 CurrentTheme.Base16 取）。</summary>
    private static readonly Color[] Palette = BuildPalette();

    private static Color[] BuildPalette()
    {
        var p = new Color[256];
        for (var i = 0; i < 16; i++) p[i] = Colors.Black; // 占位，渲染时按主题替换
        // 6×6×6 色立方（16-231）
        int[] ramp = { 0, 95, 135, 175, 215, 255 };
        var idx = 16;
        for (var r = 0; r < 6; r++)
            for (var g = 0; g < 6; g++)
                for (var b = 0; b < 6; b++)
                    p[idx++] = Color.FromArgb(255, (byte)ramp[r], (byte)ramp[g], (byte)ramp[b]);
        // 灰阶（232-255）
        for (var i = 0; i < 24; i++)
        {
            var v = (byte)(8 + i * 10);
            p[232 + i] = Color.FromArgb(255, v, v, v);
        }
        return p;
    }

    private Color PaletteColor(int idx) => idx < 16 ? CurrentTheme.Base16[idx] : Palette[idx];

    // ------------------------------------------------------------ 字体度量

    private readonly Typeface _typeface = new(FontFamily.Parse(
        "Consolas, 'DejaVu Sans Mono', 'Courier New', 'Noto Sans Mono CJK SC', monospace"));
    private readonly Typeface _boldTypeface;
    private double _fontSize = 13;
    private double _cellW = 8;
    private double _cellH = 17;
    private bool _metricsReady;

    public TerminalControl()
    {
        _boldTypeface = new Typeface(_typeface.FontFamily, FontStyle.Normal, FontWeight.Bold);
        _blink = new DispatcherTimer { Interval = BlinkInterval };
        _blink.Tick += (_, _) =>
        {
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        };
    }

    private void EnsureMetrics()
    {
        if (_metricsReady) return;
        var ft = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, _fontSize, Brushes.White);
        _cellW = Math.Ceiling(ft.Width);
        _cellH = Math.Ceiling(ft.Height * 1.1);
        _metricsReady = true;
    }

    // ------------------------------------------------------------ 生命周期

    static TerminalControl()
    {
        FocusableProperty.OverrideDefaultValue<TerminalControl>(true);
    }

    private TerminalBuffer? _subscribed;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookBuffer();
        PointerPressed += OnPointer;
        EffectiveViewportChanged += OnViewportChanged;
        if (Application.Current != null)
            Application.Current.PropertyChanged += OnAppPropertyChanged;
        _blink.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UnhookBuffer();
        if (Application.Current != null)
            Application.Current.PropertyChanged -= OnAppPropertyChanged;
        _blink.Stop();
    }

    /// <summary>应用主题切换（深/浅）→ 立即换配色重绘。</summary>
    private void OnAppPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Application.RequestedThemeVariantProperty)
            InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TerminalProperty)
            HookBuffer();
    }

    private void HookBuffer()
    {
        UnhookBuffer();
        var buf = Terminal?.Buffer;
        if (buf == null) return;
        _subscribed = buf;
        buf.Changed += OnBufferChanged;
        InvalidateVisual();
    }

    private void UnhookBuffer()
    {
        if (_subscribed != null)
            _subscribed.Changed -= OnBufferChanged;
        _subscribed = null;
    }

    private void OnBufferChanged()
    {
        // 有输出/输入时光标立即可见并重新起闪（终端惯例）
        _cursorOn = true;
        InvalidateVisual();
    }

    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        EnsureMetrics();
        var size = e.EffectiveViewport.Size;
        var cols = (int)(size.Width / _cellW);
        var rows = (int)(size.Height / _cellH);
        if (cols >= 10 && rows >= 4)
            Terminal?.Resize(cols, rows);
    }

    private void OnPointer(object? sender, PointerPressedEventArgs e)
    {
        // 点击终端即聚焦，键盘直接输入
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var buf = Terminal?.Buffer;
        if (buf == null) return;
        var dy = e.Delta.Y;
        if (buf.IsAltScreen)
        {
            // 全屏程序（less/vim）：滚轮转为方向键
            var key = dy > 0 ? "\x1b[A" : "\x1b[B";
            for (var i = 0; i < 3; i++) Terminal?.Send(key);
        }
        else
        {
            // dy > 0（向上滚）→ 回看历史；负 → 回到最新
            buf.ScrollView((int)(dy * 3));
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------ 渲染

    // 光标闪烁（约 530ms，与常见终端一致）；失焦时常亮不闪
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(530);
    private readonly DispatcherTimer _blink;
    private bool _cursorOn = true;

    public override void Render(DrawingContext ctx)
    {
        EnsureMetrics();
        var size = Bounds.Size;
        var theme = CurrentTheme;
        ctx.FillRectangle(new SolidColorBrush(theme.Bg), new Rect(0, 0, size.Width, size.Height));

        var vm = Terminal;
        var buf = vm?.Buffer;
        if (buf == null || _cellW <= 0 || _cellH <= 0) return;

        var rows = Math.Min(buf.Rows, (int)(size.Height / _cellH));
        var cols = Math.Min(buf.Cols, (int)(size.Width / _cellW));

        for (var r = 0; r < rows; r++)
        {
            var line = buf.GetViewLine(r);
            DrawRowBackgrounds(ctx, line, r, cols);
            DrawRowCharacters(ctx, line, r, cols);
        }

        // 光标：细竖线（聚焦时闪烁，失焦常亮），与字符网格严格对齐、紧贴最后字符
        if (buf.CursorVisible && (_cursorOn || !IsFocused))
        {
            var vr = buf.ViewCursorRow;
            if (vr >= 0 && vr < rows && buf.CursorCol < cols)
            {
                // 光标落在双宽字符的续格时，退回到该字符起点
                var cursorCol = buf.CursorCol;
                if (cursorCol > 0)
                {
                    var cursorLine = buf.GetViewLine(vr);
                    if (cursorCol < cursorLine.Length && cursorLine[cursorCol].Continuation)
                        cursorCol--;
                }
                ctx.FillRectangle(new SolidColorBrush(theme.Cursor),
                    new Rect(cursorCol * _cellW + 0.5, vr * _cellH + 1.5, 2, _cellH - 3));
            }
        }
    }

    /// <summary>一行的背景色块：连续同底色合并为矩形（与字符同网格）。</summary>
    private void DrawRowBackgrounds(DrawingContext ctx, Cell[] line, int r, int cols)
    {
        var c = 0;
        while (c < cols)
        {
            var bg = ResolveBg(GetCell(line, c));
            if (bg == null) { c++; continue; }
            var start = c;
            while (c < cols)
            {
                var next = ResolveBg(GetCell(line, c));
                if (next == null || next.Value != bg.Value) break;
                c++;
            }
            ctx.FillRectangle(new SolidColorBrush(bg.Value),
                new Rect(start * _cellW, r * _cellH, (c - start) * _cellW, _cellH));
        }
    }

    /// <summary>一行的字符：连续同样式段一次扫描，段内逐字符绘制——
    /// 每个字符独立锚定在自己的单元格（水平/垂直居中），不随行长累积偏移。</summary>
    private void DrawRowCharacters(DrawingContext ctx, Cell[] line, int r, int cols)
    {
        var c = 0;
        while (c < cols)
        {
            var cell = GetCell(line, c);
            if (cell.Ch == '\0' || cell.Ch == ' ') { c++; continue; }

            var (fg, _) = ResolveColors(cell);
            var bold = (cell.Flags & CellFlags.Bold) != 0;

            // 合并连续同样式字符段（减少逐格判断）
            var start = c;
            c++;
            while (c < cols)
            {
                var next = GetCell(line, c);
                if (next.Ch == '\0' || next.Ch == ' ') break;
                if (!SameStyle(cell, next)) break;
                c++;
            }

            // 段内逐字符：窄字符占 1 格；CJK 等双宽字符跨 2 格绘制并跳过续格
            for (var i = start; i < c; i++)
            {
                var glyphCell = GetCell(line, i);
                if (glyphCell.Continuation) continue;
                var ft = GetCharText(glyphCell.Ch, fg, bold);
                var y = r * _cellH + (_cellH - ft.Height) / 2;
                if (TerminalWidth.IsWide(glyphCell.Ch))
                {
                    var left = i * _cellW;
                    // 限制在 2 格内，避免回退字体过宽时压到下一个字符
                    using (ctx.PushClip(new Rect(left, r * _cellH, 2 * _cellW, _cellH)))
                    {
                        ctx.DrawText(ft, new Point(left + (2 * _cellW - ft.Width) / 2, y));
                    }
                    i++; // 跳过续格
                }
                else
                {
                    ctx.DrawText(ft, new Point(i * _cellW + (_cellW - ft.Width) / 2, y));
                }
            }
        }
    }

    // ------------------------------------------------------------ 字形缓存

    private readonly struct CharKey : IEquatable<CharKey>
    {
        private readonly char _ch;
        private readonly uint _fg;
        private readonly bool _bold;
        public CharKey(char ch, Color fg, bool bold)
        {
            _ch = ch; _fg = fg.ToUInt32(); _bold = bold;
        }
        public bool Equals(CharKey other) => _ch == other._ch && _fg == other._fg && _bold == other._bold;
        public override bool Equals(object? obj) => obj is CharKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(_ch, _fg, _bold);
    }

    private readonly Dictionary<CharKey, FormattedText> _charCache = new();

    /// <summary>字符级字形缓存：稳态渲染（打字 / 滚动查看）全部命中缓存，构建成本为零。</summary>
    private FormattedText GetCharText(char ch, IBrush brush, bool bold)
    {
        var key = new CharKey(ch, (brush as SolidColorBrush)?.Color ?? Colors.White, bold);
        if (_charCache.TryGetValue(key, out var ft)) return ft;
        if (_charCache.Count > 16384) _charCache.Clear(); // 防爆内存
        ft = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            bold ? _boldTypeface : _typeface, _fontSize, brush);
        _charCache[key] = ft;
        return ft;
    }

    // ------------------------------------------------------------ 样式解析

    private static Cell GetCell(Cell[] line, int c) => c < line.Length ? line[c] : Cell.Blank;

    private static bool SameStyle(Cell a, Cell b)
        => a.FgIdx == b.FgIdx && a.BgIdx == b.BgIdx
           && a.DirectFg == b.DirectFg && a.DirectBg == b.DirectBg
           && a.Flags == b.Flags;

    /// <summary>解析单元格的背景色；默认背景返回 null（整块跳过，由主题背景承担）。</summary>
    private Color? ResolveBg(Cell cell)
    {
        Color bg;
        if ((cell.Flags & CellFlags.Inverse) != 0)
        {
            // 反色：背景取前景色
            bg = ResolveFg(cell);
            return bg == CurrentTheme.Bg ? null : bg;
        }
        if (cell.BgIdx != Cell.DefaultColor && cell.BgIdx < 256)
            bg = PaletteColor(cell.BgIdx);
        else if (cell.DirectBg != 0)
            bg = Color.FromUInt32(cell.DirectBg);
        else
            return null;
        return bg == CurrentTheme.Bg ? null : bg;
    }

    /// <summary>解析单元格的前景色。</summary>
    private Color ResolveFg(Cell cell)
    {
        if (cell.FgIdx != Cell.DefaultColor && cell.FgIdx < 256)
            return PaletteColor(cell.FgIdx);
        if (cell.DirectFg != 0) return Color.FromUInt32(cell.DirectFg);
        return CurrentTheme.Fg;
    }

    private (IBrush Fg, IBrush? Bg) ResolveColors(Cell cell)
    {
        if ((cell.Flags & CellFlags.Inverse) != 0)
        {
            // 反色：前景背景互换
            var fg = ResolveBg(cell) ?? CurrentTheme.Bg;
            var bg = ResolveFg(cell);
            return (new SolidColorBrush(fg), new SolidColorBrush(bg));
        }
        var f = ResolveFg(cell);
        if ((cell.Flags & CellFlags.Dim) != 0)
            f = Color.FromArgb(160, f.R, f.G, f.B);
        return (new SolidColorBrush(f), null);
    }

    // ------------------------------------------------------------ 键盘

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var vm = Terminal;
        if (vm == null) return;
        var mods = e.KeyModifiers;

        // Ctrl+V / Shift+Insert 粘贴
        if ((mods & KeyModifiers.Control) != 0 && e.Key == Key.V
            || (mods & KeyModifiers.Shift) != 0 && e.Key == Key.Insert)
        {
            PasteFromClipboard(vm);
            e.Handled = true;
            return;
        }

        // Ctrl+字母 → 控制字符
        if ((mods & KeyModifiers.Control) != 0 && mods == KeyModifiers.Control
            && e.Key is >= Key.A and <= Key.Z)
        {
            vm.Send(((char)(e.Key - Key.A + 1)).ToString());
            e.Handled = true;
            return;
        }
        // Ctrl+Shift+字母（部分终端习惯发送 Ctrl 字符，bash 的 Ctrl+C 常被系统拦截）
        if ((mods & KeyModifiers.Control) != 0 && (mods & KeyModifiers.Shift) != 0
            && e.Key is >= Key.A and <= Key.Z)
        {
            vm.Send(((char)(e.Key - Key.A + 1)).ToString());
            e.Handled = true;
            return;
        }
        if ((mods & KeyModifiers.Control) != 0 && mods == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.OemOpenBrackets: // Ctrl+[
                    vm.Send("\x1b"); e.Handled = true; return;
                case Key.OemCloseBrackets: // Ctrl+]
                    vm.Send("\x1d"); e.Handled = true; return;
                case Key.OemMinus: // Ctrl+- → ^_
                    vm.Send("\x1f"); e.Handled = true; return;
                case Key.OemPlus:
                    vm.Send("\x1e"); e.Handled = true; return;
                case Key.OemBackslash:
                    vm.Send("\x1c"); e.Handled = true; return;
                case Key.Space:
                    vm.Send("\x00"); e.Handled = true; return;
            }
        }

        string? seq = e.Key switch
        {
            Key.Enter => "\r",
            Key.Back => "\x7f",
            Key.Tab when (mods & KeyModifiers.Shift) != 0 => "\x1b[Z",
            Key.Tab => "\t",
            Key.Escape => "\x1b",
            Key.Up when (mods & KeyModifiers.Control) != 0 => "\x1b[1;5A",
            Key.Down when (mods & KeyModifiers.Control) != 0 => "\x1b[1;5B",
            Key.Right when (mods & KeyModifiers.Control) != 0 => "\x1b[1;5C",
            Key.Left when (mods & KeyModifiers.Control) != 0 => "\x1b[1;5D",
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            Key.Home when (mods & KeyModifiers.Control) != 0 => "\x1b[1;5H",
            Key.End when (mods & KeyModifiers.Control) != 0 => "\x1b[1;5F",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Delete => "\x1b[3~",
            Key.Insert => "\x1b[2~",
            Key.PageUp when (mods & KeyModifiers.Shift) != 0 => null, // 本地回滚（下处理）
            Key.PageDown when (mods & KeyModifiers.Shift) != 0 => null,
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            _ => null,
        };

        if (seq != null)
        {
            vm.Send(seq);
            e.Handled = true;
            return;
        }

        // Shift+PageUp/Down：本地回滚
        if ((mods & KeyModifiers.Shift) != 0 && e.Key is Key.PageUp or Key.PageDown)
        {
            vm.Buffer.ScrollView(e.Key == Key.PageUp ? vm.Buffer.Rows : -vm.Buffer.Rows);
            e.Handled = true;
            return;
        }

        // 其余按键（字母数字等可打印字符）交给 TextInput 事件
        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        var vm = Terminal;
        if (vm == null || string.IsNullOrEmpty(e.Text)) return;
        vm.Send(e.Text);
        e.Handled = true;
    }

    private async void PasteFromClipboard(TerminalViewModel vm)
    {
        try
        {
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip == null) return;
#pragma warning disable CS0618
            var text = await clip.GetTextAsync();
#pragma warning restore CS0618
            if (!string.IsNullOrEmpty(text))
                vm.Send(text.Replace("\r\n", "\n").Replace("\n", "\r"));
        }
        catch { /* 剪贴板不可用时忽略 */ }
    }
}
