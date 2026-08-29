using System.Text;

namespace MultiSSH.Terminal;

/// <summary>
/// Streaming VT100/xterm escape-sequence parser. Bytes arrive from the SSH
/// channel via <see cref="Feed"/>; the parser mutates the attached
/// <see cref="TerminalBuffer"/>. Handles the common CSI/OSC/SGR set that
/// covers bash, vim, htop, tmux, less, etc.
/// </summary>
public class AnsiParser
{
    private enum State { Ground, Escape, Csi, Osc, OscEsc, CharSet }

    private readonly TerminalBuffer _buf;
    private State _state = State.Ground;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _osc = new();
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private readonly char[] _charBuf = new char[8];

    /// <summary>Application Cursor Keys mode (DECCKM) — affects arrow key encoding.</summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>
    /// DECSET 2004. When the remote app asks for bracketed paste, pasted text must be
    /// wrapped in ESC[200~ … ESC[201~ so editors treat it as data rather than typing —
    /// without this, pasting into vim/nano triggers auto-indent and mangles the text.
    /// </summary>
    public bool BracketedPaste { get; private set; }

    public AnsiParser(TerminalBuffer buffer) => _buf = buffer;

    public void Feed(byte[] data, int length)
    {
        for (int i = 0; i < length; i++)
            FeedByte(data[i]);
    }

    private void FeedByte(byte b)
    {
        switch (_state)
        {
            case State.Ground: Ground(b); break;
            case State.Escape: Escape(b); break;
            case State.Csi: Csi(b); break;
            case State.Osc: Osc(b); break;
            case State.OscEsc: OscEsc(b); break;
            case State.CharSet: _state = State.Ground; break; // consume charset designator
        }
    }

    private void Ground(byte b)
    {
        switch (b)
        {
            case 0x1B: _state = State.Escape; return;
            case 0x07: _buf.RingBell(); return;
            case 0x08: _buf.Backspace(); return;
            case 0x09: _buf.Tab(); return;
            case 0x0A: case 0x0B: case 0x0C: _buf.LineFeed(); return;
            case 0x0D: _buf.CarriageReturn(); return;
            case 0x00: return;
        }

        // Decode as UTF-8 (multi-byte aware).
        int n = _utf8.GetChars(new[] { b }, 0, 1, _charBuf, 0);
        for (int i = 0; i < n; i++)
        {
            char c = _charBuf[i];
            if (c >= ' ' || c == '\t') _buf.PutChar(c);
        }
    }

    private void Escape(byte b)
    {
        switch ((char)b)
        {
            case '[': _params.Clear(); _state = State.Csi; return;
            case ']': _osc.Clear(); _state = State.Osc; return;
            case '(': case ')': case '*': case '+': _state = State.CharSet; return;
            case 'M': _buf.ReverseLineFeed(); _state = State.Ground; return; // RI
            case 'D': _buf.LineFeed(); _state = State.Ground; return;        // IND
            case 'E': _buf.CarriageReturn(); _buf.LineFeed(); _state = State.Ground; return; // NEL
            case '7': _buf.SaveCursor(); _state = State.Ground; return;
            case '8': _buf.RestoreCursor(); _state = State.Ground; return;
            case '=': case '>': _state = State.Ground; return; // keypad mode
            case 'c': _buf.EraseInDisplay(2); _buf.SetCursor(0, 0); _buf.ResetSgr(); _state = State.Ground; return;
            default: _state = State.Ground; return;
        }
    }

    private void Csi(byte b)
    {
        char c = (char)b;
        // Parameter / intermediate bytes.
        if ((c >= '0' && c <= '9') || c == ';' || c == '?' || c == ':' || c == ' ' || c == '>' || c == '!')
        {
            // Cap the parameter run so a malformed/hostile stream of digits can't grow
            // this buffer without bound. A real CSI never needs more than a few dozen chars.
            if (_params.Length < 256) _params.Append(c);
            return;
        }
        DispatchCsi(c, _params.ToString());
        _state = State.Ground;
    }

    private void DispatchCsi(char final, string raw)
    {
        bool priv = raw.StartsWith("?");
        string body = priv ? raw.Substring(1) : raw;
        int[] ps = ParseParams(body);
        int P0 = ps.Length > 0 ? ps[0] : 0;
        int P0d = P0 == 0 ? 1 : P0; // default-1 variant

        switch (final)
        {
            case 'A': _buf.MoveCursor(-P0d, 0); break;
            case 'B': _buf.MoveCursor(P0d, 0); break;
            case 'C': _buf.MoveCursor(0, P0d); break;
            case 'D': _buf.MoveCursor(0, -P0d); break;
            case 'E': _buf.SetCursor(_buf.CursorY + P0d, 0); break;
            case 'F': _buf.SetCursor(_buf.CursorY - P0d, 0); break;
            case 'G': _buf.CursorToColumn(P0d - 1); break;
            case 'd': _buf.CursorToRow(P0d - 1); break;
            case 'H': case 'f':
            {
                int row = (ps.Length > 0 ? (ps[0] == 0 ? 1 : ps[0]) : 1) - 1;
                int col = (ps.Length > 1 ? (ps[1] == 0 ? 1 : ps[1]) : 1) - 1;
                _buf.SetCursor(row, col);
                break;
            }
            case 'J': _buf.EraseInDisplay(P0); break;
            case 'K': _buf.EraseInLine(P0); break;
            case 'L': _buf.InsertLines(P0d); break;
            case 'M': _buf.DeleteLines(P0d); break;
            case 'P': _buf.DeleteChars(P0d); break;
            case '@': _buf.InsertChars(P0d); break;
            case 'X': _buf.EraseChars(P0d); break;
            case 'S': _buf.ScrollUp(P0d); break;
            case 'T': _buf.ScrollDown(P0d); break;
            case 'r':
            {
                int top = (ps.Length > 0 ? (ps[0] == 0 ? 1 : ps[0]) : 1) - 1;
                int bot = (ps.Length > 1 && ps[1] != 0 ? ps[1] : _buf.Rows) - 1;
                _buf.SetScrollRegion(top, bot);
                break;
            }
            case 'm': ApplySgr(ps, body); break;
            case 'h': SetMode(priv, ps, true); break;
            case 'l': SetMode(priv, ps, false); break;
            case 's': _buf.SaveCursor(); break;
            case 'u': _buf.RestoreCursor(); break;
            // 'c' (device attributes), 'n' (device status), 't' (window ops) — ignored.
        }
    }

    private void SetMode(bool priv, int[] ps, bool on)
    {
        if (!priv) return;
        foreach (var p in ps)
        {
            switch (p)
            {
                case 1: ApplicationCursorKeys = on; break;   // DECCKM
                case 7: _buf.AutoWrap = on; break;           // DECAWM
                case 25: _buf.CursorVisible = on; break;     // DECTCEM
                case 2004: BracketedPaste = on; break;       // bracketed paste
                // 1049/47/1047 alternate screen and mouse modes are not modelled;
                // ignoring them keeps full-screen apps mostly usable.
            }
        }
    }

    private void ApplySgr(int[] ps, string body)
    {
        if (ps.Length == 0) { _buf.ResetSgr(); return; }

        for (int i = 0; i < ps.Length; i++)
        {
            int p = ps[i];
            switch (p)
            {
                case 0: _buf.ResetSgr(); break;
                case 1: _buf.AddFlag(CellFlags.Bold); break;
                case 2: _buf.AddFlag(CellFlags.Dim); break;
                case 3: _buf.AddFlag(CellFlags.Italic); break;
                case 4: _buf.AddFlag(CellFlags.Underline); break;
                case 7: _buf.AddFlag(CellFlags.Inverse); break;
                case 8: _buf.AddFlag(CellFlags.Hidden); break;
                case 22: _buf.RemoveFlag(CellFlags.Bold); _buf.RemoveFlag(CellFlags.Dim); break;
                case 23: _buf.RemoveFlag(CellFlags.Italic); break;
                case 24: _buf.RemoveFlag(CellFlags.Underline); break;
                case 27: _buf.RemoveFlag(CellFlags.Inverse); break;
                case 28: _buf.RemoveFlag(CellFlags.Hidden); break;
                case 39: _buf.SetFg(Cell.Default); break;
                case 49: _buf.SetBg(Cell.Default); break;
                case 38: i = ExtendedColor(ps, i, isFg: true); break;
                case 48: i = ExtendedColor(ps, i, isFg: false); break;
                default:
                    if (p >= 30 && p <= 37) _buf.SetFg(p - 30);
                    else if (p >= 40 && p <= 47) _buf.SetBg(p - 40);
                    else if (p >= 90 && p <= 97) _buf.SetFg(p - 90 + 8);
                    else if (p >= 100 && p <= 107) _buf.SetBg(p - 100 + 8);
                    break;
            }
        }
    }

    private int ExtendedColor(int[] ps, int i, bool isFg)
    {
        // 38;5;n  (256 colour)  or  38;2;r;g;b (truecolor)
        if (i + 1 >= ps.Length) return i;
        int mode = ps[i + 1];
        if (mode == 5 && i + 2 < ps.Length)
        {
            int idx = ps[i + 2];
            if (isFg) _buf.SetFg(idx); else _buf.SetBg(idx);
            return i + 2;
        }
        if (mode == 2 && i + 4 < ps.Length)
        {
            int packed = Cell.PackRgb((byte)ps[i + 2], (byte)ps[i + 3], (byte)ps[i + 4]);
            if (isFg) _buf.SetFg(packed); else _buf.SetBg(packed);
            return i + 4;
        }
        return i;
    }

    private void Osc(byte b)
    {
        if (b == 0x07) { FinishOsc(); return; }          // BEL terminator
        if (b == 0x1B) { _state = State.OscEsc; return; } // maybe ST (ESC \)
        // Cap the OSC string (e.g. a window title) so a host that opens an OSC and never
        // terminates it can't grow this buffer until the app runs out of memory.
        if (_osc.Length < 4096) _osc.Append((char)b);
    }

    private void OscEsc(byte b)
    {
        if (b == (byte)'\\') { FinishOsc(); return; }
        // Not an ST; treat previous ESC as start of a new escape.
        _state = State.Escape;
        Escape(b);
    }

    private void FinishOsc()
    {
        string s = _osc.ToString();
        // OSC 0;title  or  2;title  set the window/icon title.
        int sep = s.IndexOf(';');
        if (sep >= 0)
        {
            string code = s.Substring(0, sep);
            if (code is "0" or "2")
                _buf.Title = s.Substring(sep + 1);
        }
        _state = State.Ground;
    }

    private static int[] ParseParams(string s)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<int>();
        var result = new List<int>();
        foreach (var part in s.Split(';'))
        {
            if (part.IndexOf(':') < 0)
            {
                int.TryParse(part, out var v);
                result.Add(v);
                continue;
            }

            // Colon sub-parameters (ITU form). For the extended-colour introducers 38/48,
            // flatten every numeric sub-value so 38:2:R:G:B and 38:5:N parse exactly like
            // their semicolon forms; an empty colorspace field (38:2::R:G:B, common from
            // libvte) is skipped so the values still line up as [38,2,R,G,B]. Any other
            // colon parameter (e.g. 4:3 curly underline) keeps just its first value.
            var subs = part.Split(':');
            int.TryParse(subs[0], out var head);
            result.Add(head);
            if (head is 38 or 48)
            {
                for (int k = 1; k < subs.Length; k++)
                {
                    if (subs[k].Length == 0) continue;   // absent colorspace id
                    int.TryParse(subs[k], out var sv);
                    result.Add(sv);
                }
            }
        }
        return result.ToArray();
    }
}
