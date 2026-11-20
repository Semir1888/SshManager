using System.Text;

namespace SshManager.App.Services.Terminal;

/// <summary>解析器状态。</summary>
internal enum VtState
{
    Ground,      // 正常输出
    Escape,      // ESC 之后
    EscapeCh,    // ESC ( ) * + 等字符集声明（忽略一字符）
    Csi,         // ESC [ 之后（收集参数）
    Osc,         // ESC ] 之后（标题等，BEL/ST 结束）
    OscEsc,      // OSC 中遇到 ESC（可能是 ST = ESC \）
    Dcs,         // ESC P ...（忽略直到 ST）
    DcsEsc,
    SosPmApc,    // ESC X / ^ / _（忽略直到 ST）
    SosPmApcEsc,
}

/// <summary>
/// 精简 xterm/VT100 转义序列解码器：把 PTY 输出的字节流解码为
/// TerminalBuffer 上的操作。覆盖 bash / htop / top / less / vim 的常用序列：
/// SGR 颜色（含 256 色/RGB）、光标移动、清屏、插删行/字符、滚动区、
/// 备选屏幕、光标显隐、保存/恢复光标、OSC 标题。
/// 非线程安全（仅 UI 线程调用）。
/// </summary>
public sealed class VtDecoder
{
    private readonly TerminalBuffer _b;
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private readonly char[] _chars = new char[8192]; // 与 PTY 读缓冲等大，UTF-8 最坏 1 字节 1 字符
    private readonly StringBuilder _csiParams = new(32);
    private readonly StringBuilder _csiPrivate = new(4);
    private readonly StringBuilder _oscText = new(128);
    private VtState _state = VtState.Ground;

    /// <summary>终端主动查询（DA/DA2/CPR）时需要回发的响应（由 VM 写回 PTY）。</summary>
    public event Action<string>? ResponseRequested;

    public VtDecoder(TerminalBuffer buffer) => _b = buffer;

    public void Feed(byte[] data, int count)
    {
        var n = _utf8.GetChars(data, 0, count, _chars, 0);
        for (var i = 0; i < n; i++)
            HandleChar(_chars[i]);
    }

    private void HandleChar(char c)
    {
        // C0 控制字符在大多数状态下立即生效（xterm 行为）
        switch (c)
        {
            case '\a': // BEL
                if (_state == VtState.Osc) { EndOsc(); return; }
                return; // 响铃忽略
            case '\x1b':
                // 各状态遇 ESC 的转移
                switch (_state)
                {
                    case VtState.Osc: _state = VtState.OscEsc; return;
                    case VtState.Dcs: _state = VtState.DcsEsc; return;
                    case VtState.SosPmApc: _state = VtState.SosPmApcEsc; return;
                    case VtState.OscEsc or VtState.DcsEsc or VtState.SosPmApcEsc: return; // ESC ESC，保持
                    default: _state = VtState.Escape; return;
                }
            case '\x18' or '\x1a': // CAN / SUB：取消当前序列
                _state = VtState.Ground;
                return;
        }

        switch (_state)
        {
            case VtState.Ground: GroundChar(c); break;
            case VtState.Escape: EscapeChar(c); break;
            case VtState.EscapeCh: _state = VtState.Ground; break; // 忽略字符集声明字符
            case VtState.Csi: CsiChar(c); break;
            case VtState.Osc: _oscText.Append(c); break;
            case VtState.Dcs or VtState.SosPmApc: break; // 吞掉载荷
            case VtState.OscEsc:
                if (c == '\\') EndOsc();
                else _state = VtState.Osc; // 其他字符继续 OSC（极少见）
                break;
            case VtState.DcsEsc or VtState.SosPmApcEsc:
                if (c == '\\') _state = VtState.Ground; // ST 结束
                else _state = VtState.Dcs; // 宽容处理
                break;
        }
    }

    private void GroundChar(char c)
    {
        switch (c)
        {
            case '\r': _b.CarriageReturn(); return;
            case '\n' or '\v' or '\f': _b.LineFeed(); return;
            case '\b': _b.Backspace(); return;
            case '\t': _b.Tab(); return;
            case '\x00' or '\x0e' or '\x0f': return; // NUL / SO / SI 忽略
            case >= ' ' when c < char.MaxValue && !char.IsControl(c):
                _b.Put(c);
                return;
            default: return;
        }
    }

    private void EscapeChar(char c)
    {
        _state = VtState.Ground;
        switch (c)
        {
            case '[':
                _state = VtState.Csi;
                _csiParams.Clear();
                _csiPrivate.Clear();
                break;
            case ']': _state = VtState.Osc; _oscText.Clear(); break;
            case 'P': _state = VtState.Dcs; break;          // DCS：忽略
            case 'X' or '^' or '_': _state = VtState.SosPmApc; break; // PM/APC/SOS：忽略
            case '(' or ')' or '*' or '+': _state = VtState.EscapeCh; break;
            case '7': _b.SaveCursor(); break;
            case '8': _b.RestoreCursor(); break;
            case 'D': _b.IndexDown(); break;
            case 'M': _b.ReverseIndex(); break;
            case 'E': _b.CarriageReturn(); _b.LineFeed(); break;
            case 'c': _b.Reset(); break;                    // RIS
            case '=' or '>': break;                         // 键盘模式，忽略
        }
    }

    private void CsiChar(char c)
    {
        // 收集参数 / 私有前缀（? = DEC 私有序列，如 ?1049h 备选屏幕、?25l 光标隐藏）
        if (c is >= '0' and <= '9' or ';' or ':' or '<' or '=' or '>' or '?')
        {
            if (c is '<' or '=' or '>' or '?') _csiPrivate.Append(c);
            else _csiParams.Append(c);
            return;
        }
        if (c is >= ' ' and <= '/') // 中间字节
        {
            _csiPrivate.Append(c);
            return;
        }
        if (c is >= '@' and <= '~')
        {
            _state = VtState.Ground;
            Dispatch(c);
            return;
        }
        // 其他控制字符（如 CR/LF 出现在 CSI 中）：xterm 允许执行
        if (c is '\r' or '\n' or '\b' or '\t') HandleChar(c);
        _state = VtState.Ground; // 异常序列放弃
    }

    private void EndOsc()
    {
        _state = VtState.Ground;
        // OSC 0 ; title / OSC 2 ; title → 设置标题
        var text = _oscText.ToString();
        var semi = text.IndexOf(';');
        var op = semi > 0 ? text[..semi] : "";
        var value = semi >= 0 && semi + 1 <= text.Length ? text[(semi + 1)..] : "";
        if ((op == "0" || op == "2") && value.Length > 0)
            _b.SetTitle(value);
    }

    /// <summary>CSI 参数（1 起）；缺省/0 返回 def（VT 规范 0 视为 1）。</summary>
    private int P(int index = 0, int def = 1)
    {
        var all = _csiParams.ToString().Split(';');
        if (index >= all.Length || all[index].Length == 0) return def;
        return int.TryParse(all[index], out var v) && v > 0 ? v : def;
    }

    /// <summary>CSI 参数（0 允许，缺省 0）。</summary>
    private int P0(int index = 0)
    {
        var all = _csiParams.ToString().Split(';');
        if (index >= all.Length || all[index].Length == 0) return 0;
        return int.TryParse(all[index], out var v) ? v : 0;
    }

    private void Dispatch(char final)
    {
        var priv = _csiPrivate.Length > 0 ? _csiPrivate[^1] : '\0';
        switch (final)
        {
            case 'A': _b.MoveCursorRelative(-P(), 0); break;          // CUU
            case 'B': _b.MoveCursorRelative(P(), 0); break;           // CUD
            case 'C': _b.MoveCursorRelative(0, P()); break;           // CUF
            case 'D': _b.MoveCursorRelative(0, -P()); break;          // CUB
            case 'E': _b.MoveCursorRelative(P(), 0); _b.CarriageReturn(); break; // CNL
            case 'F': _b.MoveCursorRelative(-P(), 0); _b.CarriageReturn(); break; // CPL
            case 'G' or '`': _b.MoveCursorColumn(P() - 1); break;     // CHA / HPA
            case 'H' or 'f': _b.MoveCursor(P() - 1, P(1) - 1); break; // CUP / HVP
            case 'd': _b.MoveCursor(P() - 1, _b.CursorCol); break;    // VPA
            case 'J': _b.EraseDisplay(P0(0)); break;                  // ED
            case 'K': _b.EraseLine(P0(0)); break;                     // EL
            case 'L': _b.InsertLines(P()); break;                     // IL
            case 'M': _b.DeleteLines(P()); break;                     // DL
            case 'P': _b.DeleteChars(P()); break;                     // DCH
            case '@': _b.InsertChars(P()); break;                     // ICH
            case 'X': _b.EraseChars(P()); break;                      // ECH
            case 'S': _b.ScrollUp(P()); break;                        // SU
            case 'T': _b.ScrollDown(P()); break;                      // SD
            case 'r': _b.SetScrollRegion(P() - 1, P(1) - 1); break;   // DECSTBM
            case 's': _b.SaveCursor(); break;                          // SCP
            case 'u': _b.RestoreCursor(); break;                       // RCP
            case 'm': DispatchSgr(); break;                            // SGR
            case 'h' or 'l': DispatchModes(final == 'h'); break;       // SM/RM（含 ? 私有）
            case 'c': // DA / DA2 查询
                if (priv == '>') ResponseRequested?.Invoke("\x1b[>0;276;0c");
                else ResponseRequested?.Invoke("\x1b[?1;2c");
                break;
            case 'n': // DSR：CPR 光标位置报告
                if (P0(0) == 6)
                    ResponseRequested?.Invoke($"\x1b[{_b.CursorRow + 1};{_b.CursorCol + 1}R");
                break;
            case 'q' or 't' or 'g' or 'e' or 'a': break;               // DECSCUSR / 窗口操作等：忽略
        }
    }

    private void DispatchSgr()
    {
        var all = _csiParams.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (all.Length == 0)
        {
            _b.SetDefaultColors();
            return;
        }
        for (var i = 0; i < all.Length; i++)
        {
            var part = all[i].Split(':'); // 忽略冒号子参数
            if (!int.TryParse(part[0], out var code)) continue;
            switch (code)
            {
                case 0: _b.SetDefaultColors(); break;
                case 1: _b.SetFlags(CellFlags.Bold, true); break;
                case 2: _b.SetFlags(CellFlags.Dim, true); break;
                case 3: _b.SetFlags(CellFlags.Italic, true); break;
                case 4: _b.SetFlags(CellFlags.Underline, true); break;
                case 7: _b.SetFlags(CellFlags.Inverse, true); break;
                case 22: _b.SetFlags(CellFlags.Bold | CellFlags.Dim, false); break;
                case 23: _b.SetFlags(CellFlags.Italic, false); break;
                case 24: _b.SetFlags(CellFlags.Underline, false); break;
                case 27: _b.SetFlags(CellFlags.Inverse, false); break;
                case 30 or 31 or 32 or 33 or 34 or 35 or 36 or 37:
                    _b.SetColor(code - 30, true); break;
                case 90 or 91 or 92 or 93 or 94 or 95 or 96 or 97:
                    _b.SetColor(code - 90 + 8, true); break;
                case 39: _b.SetColor(0xFFFF, true); _b.SetDirectColor(0, true); break;
                case 40 or 41 or 42 or 43 or 44 or 45 or 46 or 47:
                    _b.SetColor(code - 40, false); break;
                case 100 or 101 or 102 or 103 or 104 or 105 or 106 or 107:
                    _b.SetColor(code - 100 + 8, false); break;
                case 49: _b.SetColor(0xFFFF, false); _b.SetDirectColor(0, false); break;
                case 38 or 48:
                    // 38;5;n / 38;2;r;g;b
                    var fg = code == 38;
                    if (i + 1 < all.Length && all[i + 1].StartsWith('5') && int.TryParse(all[i + 1].Split(':')[0], out _))
                    {
                        if (i + 2 < all.Length && int.TryParse(all[i + 2].Split(':')[0], out var idx))
                        {
                            _b.SetColor(idx, fg);
                            i += 2;
                        }
                    }
                    else if (i + 1 < all.Length && all[i + 1].StartsWith('2'))
                    {
                        if (i + 4 < all.Length
                            && int.TryParse(all[i + 2].Split(':')[0], out var r)
                            && int.TryParse(all[i + 3].Split(':')[0], out var g)
                            && int.TryParse(all[i + 4].Split(':')[0], out var b2))
                        {
                            var rgb = ((uint)r << 16) | ((uint)g << 8) | (uint)b2;
                            _b.SetDirectColor(0xFF000000 | rgb, fg);
                            i += 4;
                        }
                    }
                    break;
            }
        }
    }

    private void DispatchModes(bool set)
    {
        var all = _csiParams.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in all)
        {
            if (!int.TryParse(raw.Split(':')[0], out var code)) continue;
            switch (code)
            {
                case 25: _b.ShowCursor(set); break;
                case 47 or 1047: _b.SwitchScreen(set, clearAlt: false); break;
                case 1048: // 保存/恢复光标
                    if (set) _b.SaveCursor(); else _b.RestoreCursor();
                    break;
                case 1049: _b.SwitchScreen(set, clearAlt: true); break;
                // 2004 括号粘贴、1000 鼠标等：忽略
            }
        }
    }
}
