namespace SshManager.App.Services.Terminal;

/// <summary>单元格属性标志。</summary>
[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
}

/// <summary>
/// 终端单元格。颜色优先用调色板索引（0-255，FgIdx/BgIdx）；
/// 0xFFFF 表示默认色，此时若 DirectRgb 非 0 则用直接 RGB 色。
/// </summary>
public readonly struct Cell
{
    public const ushort DefaultColor = 0xFFFF;

    public readonly char Ch;
    public readonly ushort FgIdx;
    public readonly ushort BgIdx;
    public readonly uint DirectFg;
    public readonly uint DirectBg;
    public readonly CellFlags Flags;

    /// <summary>双宽字符（中文等）的第二格：只占位/继承背景，不单独绘制字形。</summary>
    public readonly bool Continuation;

    public Cell(char ch, ushort fgIdx, ushort bgIdx, uint df, uint db, CellFlags flags,
                bool continuation = false)
    {
        Ch = ch; FgIdx = fgIdx; BgIdx = bgIdx; DirectFg = df; DirectBg = db;
        Flags = flags; Continuation = continuation;
    }

    public static Cell Blank { get; } = new(' ', DefaultColor, DefaultColor, 0, 0, CellFlags.None);

    /// <summary>双宽字符的续格（背景/样式与首格一致，保证背景块连成两格）。</summary>
    public static Cell MakeContinuation(ushort fgIdx, ushort bgIdx, uint df, uint db, CellFlags flags)
        => new(' ', fgIdx, bgIdx, df, db, flags, continuation: true);
}

/// <summary>
/// 精简 xterm 终端缓冲模型：固定 Cols×Rows 视口网格 + 回滚历史（主屏）+
/// 备选屏幕（vim/top 等全屏程序）+ 滚动区（DECSTBM）。
/// 非线程安全：仅 UI 线程调用（VtDecoder 同线程驱动）。
/// </summary>
public sealed class TerminalBuffer
{
    /// <summary>回滚历史行数上限（防爆内存）。</summary>
    private const int MaxScrollback = 5000;

    private Cell[,] _main;
    private Cell[,] _alt;
    private readonly LinkedList<Cell[]> _scrollback = new();

    private ushort _fgIdx = Cell.DefaultColor;
    private ushort _bgIdx = Cell.DefaultColor;
    private uint _directFg;
    private uint _directBg;
    private CellFlags _flags;

    private bool _altScreen;
    private bool _pendingWrap;
    private (int Row, int Col, ushort Fg, ushort Bg, uint Df, uint Db, CellFlags Flags) _savedCursor;

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool IsAltScreen => _altScreen;

    /// <summary>滚动区（DECSTBM），含端点。</summary>
    private int _scrollTop;
    private int _scrollBottom;

    /// <summary>回看偏移（0 = 跟随最新输出；&gt;0 = 向上翻 N 行）。</summary>
    public int ViewShift { get; private set; }

    /// <summary>内容/光标变化（渲染层订阅后失效重绘）。</summary>
    public event Action? Changed;

    /// <summary>窗口标题变化（OSC 0/2）。</summary>
    public event Action<string>? TitleChanged;

    public TerminalBuffer(int cols, int rows)
    {
        Cols = Math.Clamp(cols, 10, 500);
        Rows = Math.Clamp(rows, 4, 300);
        _main = NewGrid(Cols, Rows);
        _alt = NewGrid(Cols, Rows);
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    private static Cell[,] NewGrid(int cols, int rows)
    {
        var g = new Cell[rows, cols];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                g[r, c] = Cell.Blank;
        return g;
    }

    private Cell[,] Grid => _altScreen ? _alt : _main;

    private void ChangedNotify() => Changed?.Invoke();

    // ================================================== 当前属性（SGR）

    public void SetDefaultColors()
    {
        _fgIdx = Cell.DefaultColor; _bgIdx = Cell.DefaultColor;
        _directFg = 0; _directBg = 0;
        _flags = CellFlags.None;
    }

    public void SetColor(int index, bool foreground)
    {
        if (index is < 0 or > 255) index = 0;
        if (foreground) { _fgIdx = (ushort)index; _directFg = 0; }
        else { _bgIdx = (ushort)index; _directBg = 0; }
    }

    public void SetDirectColor(uint rgb, bool foreground)
    {
        if (foreground) { _fgIdx = Cell.DefaultColor; _directFg = rgb; }
        else { _bgIdx = Cell.DefaultColor; _directBg = rgb; }
    }

    public void SetFlags(CellFlags flags, bool on)
    {
        if (on) _flags |= flags;
        else _flags &= ~flags;
    }

    // ================================================== 打印与基础控制

    public void Put(char ch)
    {
        if (_pendingWrap)
        {
            _pendingWrap = false;
            CursorCol = 0;
            CursorDownOrScroll();
        }

        var width = TerminalWidth.Of(ch);

        // 双宽字符只剩最后一列：先换行，避免把字符劈成两半
        if (width == 2 && CursorCol >= Cols - 1)
        {
            CursorCol = 0;
            CursorDownOrScroll();
        }

        var row = CursorRow;
        var col = CursorCol;
        ClearWideAt(row, col);

        if (width == 2)
        {
            ClearWideAt(row, col + 1);
            Grid[row, col] = new Cell(ch, _fgIdx, _bgIdx, _directFg, _directBg, _flags);
            Grid[row, col + 1] = Cell.MakeContinuation(_fgIdx, _bgIdx, _directFg, _directBg, _flags);
            if (col + 1 >= Cols - 1)
            {
                CursorCol = Cols - 1;
                _pendingWrap = true;
            }
            else
            {
                CursorCol = col + 2;
            }
        }
        else
        {
            Grid[row, col] = new Cell(ch, _fgIdx, _bgIdx, _directFg, _directBg, _flags);
            if (col >= Cols - 1) _pendingWrap = true;
            else CursorCol = col + 1;
        }
        ChangedNotify();
    }

    public void CarriageReturn()
    {
        CursorCol = 0;
        _pendingWrap = false;
        ChangedNotify();
    }

    /// <summary>LF / VT / FF。</summary>
    public void LineFeed()
    {
        _pendingWrap = false;
        CursorDownOrScroll();
        ChangedNotify();
    }

    private void CursorDownOrScroll()
    {
        if (CursorRow >= _scrollBottom)
            ScrollUp(1);
        else if (CursorRow < Rows - 1)
            CursorRow++;
    }

    public void Backspace()
    {
        if (CursorCol > 0)
        {
            CursorCol--;
            // 落在双宽字符的续格上时，退回该字符起点
            if (CursorCol > 0 && Grid[CursorRow, CursorCol].Continuation)
                CursorCol--;
        }
        _pendingWrap = false;
        ChangedNotify();
    }

    public void Tab()
    {
        var next = (CursorCol / 8 + 1) * 8;
        CursorCol = Math.Min(next, Cols - 1);
        ChangedNotify();
    }

    /// <summary>ESC D：光标下移（滚动区底则滚动）。</summary>
    public void IndexDown() => LineFeed();

    /// <summary>ESC M：光标上移（滚动区顶则内容下滚）。</summary>
    public void ReverseIndex()
    {
        if (CursorRow <= _scrollTop)
            ScrollDown(1);
        else if (CursorRow > 0)
            CursorRow--;
        ChangedNotify();
    }

    // ================================================== 光标移动

    public void MoveCursor(int row, int col)
    {
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorCol = Math.Clamp(col, 0, Cols - 1);
        _pendingWrap = false;
        ChangedNotify();
    }

    public void MoveCursorRelative(int dRow, int dCol)
    {
        var r = Math.Clamp(CursorRow + dRow, 0, Rows - 1);
        var c = Math.Clamp(CursorCol + dCol, 0, Cols - 1);
        MoveCursor(r, c);
    }

    public void MoveCursorColumn(int col) => MoveCursor(CursorRow, Math.Clamp(col, 0, Cols - 1));

    public void SaveCursor()
    {
        _savedCursor = (CursorRow, CursorCol, _fgIdx, _bgIdx, _directFg, _directBg, _flags);
    }

    public void RestoreCursor()
    {
        CursorRow = Math.Clamp(_savedCursor.Row, 0, Rows - 1);
        CursorCol = Math.Clamp(_savedCursor.Col, 0, Cols - 1);
        _fgIdx = _savedCursor.Fg; _bgIdx = _savedCursor.Bg;
        _directFg = _savedCursor.Df; _directBg = _savedCursor.Db;
        _flags = _savedCursor.Flags;
        _pendingWrap = false;
        ChangedNotify();
    }

    public void ShowCursor(bool show)
    {
        CursorVisible = show;
        ChangedNotify();
    }

    // ================================================== 清屏

    public void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0: // 光标到结尾
                EraseInRow(CursorRow, CursorCol, Cols - CursorCol);
                for (var r = CursorRow + 1; r < Rows; r++) EraseInRow(r, 0, Cols);
                break;
            case 1: // 开头到光标
                for (var r = 0; r < CursorRow; r++) EraseInRow(r, 0, Cols);
                EraseInRow(CursorRow, 0, CursorCol + 1);
                break;
            case 2 or 3: // 全屏
                for (var r = 0; r < Rows; r++) EraseInRow(r, 0, Cols);
                break;
        }
        ChangedNotify();
    }

    public void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0: EraseInRow(CursorRow, CursorCol, Cols - CursorCol); break;
            case 1: EraseInRow(CursorRow, 0, CursorCol + 1); break;
            case 2: EraseInRow(CursorRow, 0, Cols); break;
        }
        ChangedNotify();
    }

    private void EraseInRow(int row, int start, int count)
    {
        if (row < 0 || row >= Rows || count <= 0) return;
        var g = Grid;
        var end = Math.Min(start + count, Cols);
        for (var c = Math.Max(0, start); c < end; c++)
            g[row, c] = Cell.Blank;
        RepairWideCells(g, row, Cols);
    }

    /// <summary>覆盖/擦除双宽字符的任意一半时，把配对的另一半一起清掉。</summary>
    private void ClearWideAt(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols) return;
        var g = Grid;
        var cell = g[row, col];
        if (cell.Continuation && col > 0 && TerminalWidth.IsWide(g[row, col - 1].Ch))
        {
            g[row, col - 1] = Cell.Blank;
            g[row, col] = Cell.Blank;
        }
        else if (TerminalWidth.IsWide(cell.Ch) && col + 1 < Cols && g[row, col + 1].Continuation)
        {
            g[row, col] = Cell.Blank;
            g[row, col + 1] = Cell.Blank;
        }
    }

    /// <summary>把被切断/孤立的双宽字符半格清空，避免残影（擦除/插入/删除/缩放后调用）。</summary>
    private static void RepairWideCells(Cell[,] g, int row, int cols)
    {
        for (var c = 0; c < cols; c++)
        {
            var cell = g[row, c];
            if (cell.Continuation)
            {
                if (c == 0 || !TerminalWidth.IsWide(g[row, c - 1].Ch))
                    g[row, c] = Cell.Blank;
            }
            else if (TerminalWidth.IsWide(cell.Ch))
            {
                if (c + 1 >= cols || !g[row, c + 1].Continuation)
                    g[row, c] = Cell.Blank; // 宽字符缺续格：清掉，避免只画半个字
                else
                    c++; // 跳过续格
            }
        }
    }

    // ================================================== 插入 / 删除

    /// <summary>在光标处插入 n 个空行（滚动区内下移）。</summary>
    public void InsertLines(int n)
    {
        n = Math.Clamp(n, 1, Rows);
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        var g = Grid;
        for (var r = _scrollBottom; r >= CursorRow + n; r--)
            CopyRow(g, r - n, g, r);
        for (var r = CursorRow; r < CursorRow + n && r <= _scrollBottom; r++)
            EraseInRow(r, 0, Cols);
        ChangedNotify();
    }

    /// <summary>删除光标处 n 行（滚动区内上移）。</summary>
    public void DeleteLines(int n)
    {
        n = Math.Clamp(n, 1, Rows);
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        var g = Grid;
        for (var r = CursorRow; r <= _scrollBottom - n; r++)
            CopyRow(g, r + n, g, r);
        for (var r = Math.Max(_scrollBottom - n + 1, CursorRow); r <= _scrollBottom; r++)
            EraseInRow(r, 0, Cols);
        ChangedNotify();
    }

    /// <summary>光标处插入 n 个空字符（行内右移）。</summary>
    public void InsertChars(int n)
    {
        n = Math.Clamp(n, 1, Cols);
        var g = Grid;
        var row = CursorRow;
        for (var c = Cols - 1; c >= CursorCol + n; c--)
            g[row, c] = g[row, c - n];
        for (var c = CursorCol; c < CursorCol + n && c < Cols; c++)
            g[row, c] = Cell.Blank;
        RepairWideCells(g, row, Cols);
        ChangedNotify();
    }

    /// <summary>光标处删除 n 个字符（行内左移）。</summary>
    public void DeleteChars(int n)
    {
        n = Math.Clamp(n, 1, Cols);
        var g = Grid;
        var row = CursorRow;
        for (var c = CursorCol; c + n < Cols; c++)
            g[row, c] = g[row, c + n];
        for (var c = Math.Max(Cols - n, CursorCol); c < Cols; c++)
            g[row, c] = Cell.Blank;
        RepairWideCells(g, row, Cols);
        ChangedNotify();
    }

    /// <summary>光标处擦除 n 个字符（不移动，填空）。</summary>
    public void EraseChars(int n)
    {
        n = Math.Clamp(n, 1, Cols);
        EraseInRow(CursorRow, CursorCol, n);
        ChangedNotify();
    }

    private static void CopyRow(Cell[,] src, int srcRow, Cell[,] dst, int dstRow)
    {
        for (var c = 0; c < src.GetLength(1); c++)
            dst[dstRow, c] = src[srcRow, c];
    }

    // ================================================== 滚动

    /// <summary>滚动区内内容上移 n 行（顶行进回滚历史——仅主屏）。</summary>
    public void ScrollUp(int n)
    {
        n = Math.Clamp(n, 1, Rows);
        var g = Grid;
        for (var i = 0; i < n; i++)
        {
            if (!_altScreen)
            {
                var line = new Cell[Cols];
                for (var c = 0; c < Cols; c++) line[c] = g[_scrollTop, c];
                _scrollback.AddLast(line);
                if (_scrollback.Count > MaxScrollback)
                    _scrollback.RemoveFirst();
            }
            for (var r = _scrollTop; r < _scrollBottom; r++)
                CopyRow(g, r + 1, g, r);
            EraseInRow(_scrollBottom, 0, Cols);
        }
        ChangedNotify();
    }

    /// <summary>滚动区内内容下移 n 行。</summary>
    public void ScrollDown(int n)
    {
        n = Math.Clamp(n, 1, Rows);
        var g = Grid;
        for (var i = 0; i < n; i++)
        {
            for (var r = _scrollBottom; r > _scrollTop; r--)
                CopyRow(g, r - 1, g, r);
            EraseInRow(_scrollTop, 0, Cols);
        }
        ChangedNotify();
    }

    /// <summary>设置滚动区（DECSTBM）；设置后光标回原点。</summary>
    public void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        bottom = Math.Clamp(bottom, 0, Rows - 1);
        if (top >= bottom) return;
        _scrollTop = top;
        _scrollBottom = bottom;
        MoveCursor(0, 0);
    }

    // ================================================== 备选屏幕

    /// <summary>切换主/备屏幕（1049：保存光标并清备屏）。</summary>
    public void SwitchScreen(bool alt, bool clearAlt)
    {
        if (alt == _altScreen) return;
        if (alt)
        {
            SaveCursor();
            _altScreen = true;
            if (clearAlt)
                for (var r = 0; r < Rows; r++) EraseInRow(r, 0, Cols);
        }
        else
        {
            _altScreen = false;
            RestoreCursor();
        }
        ViewShift = 0;
        ChangedNotify();
    }

    // ================================================== 尺寸

    public void Resize(int cols, int rows)
    {
        cols = Math.Clamp(cols, 10, 500);
        rows = Math.Clamp(rows, 4, 300);
        if (cols == Cols && rows == Rows) return;

        var newMain = NewGrid(cols, rows);
        var newAlt = NewGrid(cols, rows);

        if (rows < Rows)
        {
            // 行数减少：主屏顶部行推入回滚
            for (var r = 0; r < Rows - rows; r++)
            {
                var line = new Cell[Cols];
                for (var c = 0; c < Cols; c++) line[c] = _main[r, c];
                _scrollback.AddLast(line);
                if (_scrollback.Count > MaxScrollback) _scrollback.RemoveFirst();
            }
            CopyRegion(_main, newMain, Rows - rows, rows, cols);
            CopyRegion(_alt, newAlt, Rows - rows, rows, cols);
        }
        else
        {
            // 行数增加：优先从回滚拉回
            var pull = Math.Min(rows - Rows, _scrollback.Count);
            var copyStart = 0;
            if (pull > 0)
            {
                for (var i = 0; i < pull; i++)
                {
                    var line = _scrollback.Last!.Value;
                    CopyRowInto(line, newMain, pull - 1 - i);
                    _scrollback.RemoveLast();
                }
                copyStart = pull;
            }
            CopyRegion(_main, newMain, 0, Rows, cols, copyStart);
            CopyRegion(_alt, newAlt, 0, Math.Min(Rows, rows), cols);
        }

        _main = newMain;
        _alt = newAlt;
        Cols = cols;
        Rows = rows;

        // 缩放可能把宽字符的续格截掉：逐行清理孤立半格
        for (var r = 0; r < rows; r++)
        {
            RepairWideCells(_main, r, cols);
            RepairWideCells(_alt, r, cols);
        }
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        CursorRow = Math.Clamp(CursorRow, 0, rows - 1);
        CursorCol = Math.Clamp(CursorCol, 0, cols - 1);
        _pendingWrap = false;
        ViewShift = 0;
        ChangedNotify();
    }

    private void CopyRegion(Cell[,] src, Cell[,] dst, int srcStart, int srcRows, int dstCols, int dstRow = 0)
    {
        for (var r = 0; r < srcRows; r++)
        {
            var sr = srcStart + r;
            if (sr >= src.GetLength(0)) break;
            var dr = dstRow + r;
            if (dr >= dst.GetLength(0)) break;
            for (var c = 0; c < Math.Min(src.GetLength(1), Math.Min(dstCols, dst.GetLength(1))); c++)
                dst[dr, c] = src[sr, c];
        }
    }

    private void CopyRowInto(Cell[] src, Cell[,] dst, int dstRow)
    {
        for (var c = 0; c < Math.Min(src.Length, dst.GetLength(1)); c++)
            dst[dstRow, c] = src[c];
    }

    // ================================================== 回滚查看

    /// <summary>滚轮回看（正 = 向上翻，负 = 向下回最新）。</summary>
    public void ScrollView(int delta)
    {
        var next = ViewShift + delta;
        if (next <= 0) ViewShift = 0;
        else ViewShift = Math.Min(next, _scrollback.Count);
        ChangedNotify();
    }

    /// <summary>总历史行数（回滚 + 当前屏）。</summary>
    public int HistoryLines => _scrollback.Count + Rows;

    /// <summary>
    /// 取一条显示行（index 0..Rows-1，0 为窗口顶）。
    /// 窗口顶 = 总高度 - Rows - ViewShift。
    /// </summary>
    public Cell[] GetViewLine(int index)
    {
        var top = HistoryLines - Rows - ViewShift;
        var abs = top + index;
        if (abs < _scrollback.Count)
        {
            var i = 0;
            for (var node = _scrollback.First; node != null; node = node.Next, i++)
                if (i == abs) return node.Value;
        }
        else
        {
            var row = abs - _scrollback.Count;
            var g = Grid;
            if (row >= 0 && row < Rows)
            {
                var line = new Cell[Cols];
                for (var c = 0; c < Cols; c++) line[c] = g[row, c];
                return line;
            }
        }
        return BlankLine();
    }

    private Cell[]? _blankLine;
    private Cell[] BlankLine()
    {
        if (_blankLine == null || _blankLine.Length != Cols)
        {
            _blankLine = new Cell[Cols];
            for (var c = 0; c < Cols; c++) _blankLine[c] = Cell.Blank;
        }
        return _blankLine;
    }

    /// <summary>光标在显示窗口中的行号（回看时不可见返回 -1）。</summary>
    public int ViewCursorRow
    {
        get
        {
            if (ViewShift > 0) return -1;
            var top = HistoryLines - Rows;
            return _scrollback.Count + CursorRow - top;
        }
    }

    // ================================================== 其他

    /// <summary>清空回滚历史并清屏、光标回原点（用户点“清屏”按钮）。</summary>
    public void ClearAll()
    {
        _scrollback.Clear();
        for (var r = 0; r < Rows; r++) EraseInRow(r, 0, Cols);
        CursorRow = 0;
        CursorCol = 0;
        ViewShift = 0;
        ChangedNotify();
    }

    /// <summary>RIS 全量复位。</summary>
    public void Reset()
    {
        _scrollback.Clear();
        for (var r = 0; r < Rows; r++) EraseInRow(r, 0, Cols);
        _altScreen = false;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        CursorRow = 0;
        CursorCol = 0;
        SetDefaultColors();
        ViewShift = 0;
        ChangedNotify();
    }

    public void SetTitle(string title) => TitleChanged?.Invoke(title);
}
