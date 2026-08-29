using System.Collections.Generic;

namespace MultiSSH.Terminal;

/// <summary>
/// A VT100/xterm style screen buffer: a grid of cells, a cursor, a scroll
/// region and a scrollback history. The <see cref="AnsiParser"/> calls into
/// this class; the rendering control reads from it.
/// </summary>
public class TerminalBuffer
{
    public int Rows { get; private set; }
    public int Cols { get; private set; }

    private Cell[][] _grid = Array.Empty<Cell[]>();
    private readonly List<Cell[]> _scrollback = new();
    public int MaxScrollback { get; set; } = 2000;

    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; set; } = true;

    // Current SGR (graphic rendition) state.
    private int _fg = Cell.Default;
    private int _bg = Cell.Default;
    private CellFlags _flags = CellFlags.None;

    // Scroll region (0-based, inclusive).
    private int _scrollTop;
    private int _scrollBottom;

    // Saved cursor (DECSC/DECRC).
    private int _savedX, _savedY, _savedFg = Cell.Default, _savedBg = Cell.Default;
    private CellFlags _savedFlags;

    public string Title { get; set; } = "";
    public bool WrapPending { get; private set; }
    public bool AutoWrap { get; set; } = true;

    public event Action? Bell;

    public TerminalBuffer(int rows, int cols)
    {
        Resize(rows, cols);
    }

    public IReadOnlyList<Cell[]> Scrollback => _scrollback;
    public Cell[] Line(int row) => _grid[row];

    public (int fg, int bg) DefaultAttrs => (Cell.Default, Cell.Default);

    // -------------------- geometry --------------------

    public void Resize(int rows, int cols)
    {
        if (rows < 1) rows = 1;
        if (cols < 1) cols = 1;
        if (rows == Rows && cols == Cols) return;

        int oldRows = Rows;

        var newGrid = new Cell[rows][];
        for (int r = 0; r < rows; r++)
        {
            newGrid[r] = new Cell[cols];
            for (int c = 0; c < cols; c++)
                newGrid[r][c] = Cell.Blank(Cell.Default, Cell.Default);
        }

        // Growing: keep existing lines anchored to the TOP and let the new blank rows
        // appear at the bottom (standard terminal behaviour). Bottom-aligning a grow would
        // push a freshly started shell's prompt — and the cursor — into the middle of the
        // pane. Shrinking: keep the BOTTOM rows (the most recent output and the prompt) and
        // let the oldest rows fall away.
        bool growing = rows >= oldRows;
        int copyRows = Math.Min(rows, oldRows);
        int copyCols = Math.Min(cols, Cols);
        for (int r = 0; r < copyRows; r++)
        {
            int srcRow = growing ? r : oldRows - copyRows + r;
            int dstRow = growing ? r : rows - copyRows + r;
            for (int c = 0; c < copyCols; c++)
                newGrid[dstRow][c] = _grid[srcRow][c];
        }

        _grid = newGrid;
        Rows = rows;
        Cols = cols;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        CursorX = Math.Min(CursorX, cols - 1);
        // Growing leaves the cursor on its line (content stayed at the top). Shrinking moved
        // the kept content up by (oldRows-rows), so move the cursor by the same amount.
        CursorY = growing
            ? Math.Min(CursorY, rows - 1)
            : Math.Clamp(CursorY + (rows - oldRows), 0, rows - 1);
        WrapPending = false;
    }

    // -------------------- SGR --------------------

    public void ResetSgr()
    {
        _fg = Cell.Default;
        _bg = Cell.Default;
        _flags = CellFlags.None;
    }

    public void SetFg(int v) => _fg = v;
    public void SetBg(int v) => _bg = v;
    public void AddFlag(CellFlags f) => _flags |= f;
    public void RemoveFlag(CellFlags f) => _flags &= ~f;

    // -------------------- writing --------------------

    public void PutChar(char ch)
    {
        if (WrapPending && AutoWrap)
        {
            // Mark the line we are leaving as continued, before LineFeed can push it
            // into scrollback — otherwise a copied wrapped line gains a stray newline.
            MarkWrapped(_grid[CursorY]);
            CursorX = 0;
            LineFeed();
            WrapPending = false;
        }

        _grid[CursorY][CursorX] = new Cell { Char = ch, Fg = _fg, Bg = _bg, Flags = _flags };

        if (CursorX + 1 >= Cols)
        {
            WrapPending = true;
        }
        else
        {
            CursorX++;
        }
    }

    public void CarriageReturn()
    {
        CursorX = 0;
        WrapPending = false;
    }

    public void LineFeed()
    {
        WrapPending = false;
        if (CursorY == _scrollBottom)
            ScrollUp(1);
        else if (CursorY < Rows - 1)
            CursorY++;
    }

    public void ReverseLineFeed()
    {
        WrapPending = false;
        if (CursorY == _scrollTop)
            ScrollDown(1);
        else if (CursorY > 0)
            CursorY--;
    }

    public void Backspace()
    {
        WrapPending = false;
        if (CursorX > 0) CursorX--;
    }

    public void Tab()
    {
        WrapPending = false;
        int next = ((CursorX / 8) + 1) * 8;
        CursorX = Math.Min(next, Cols - 1);
    }

    public void RingBell() => Bell?.Invoke();

    // -------------------- cursor movement --------------------

    public void SetCursor(int row, int col)
    {
        CursorY = Math.Clamp(row, 0, Rows - 1);
        CursorX = Math.Clamp(col, 0, Cols - 1);
        WrapPending = false;
    }

    public void MoveCursor(int dRow, int dCol)
        => SetCursor(CursorY + dRow, CursorX + dCol);

    public void CursorToColumn(int col) => SetCursor(CursorY, col);
    public void CursorToRow(int row) => SetCursor(row, CursorX);

    public void SaveCursor()
    {
        _savedX = CursorX; _savedY = CursorY;
        _savedFg = _fg; _savedBg = _bg; _savedFlags = _flags;
    }

    public void RestoreCursor()
    {
        CursorX = _savedX; CursorY = _savedY;
        _fg = _savedFg; _bg = _savedBg; _flags = _savedFlags;
        WrapPending = false;
    }

    // -------------------- scrolling --------------------

    public void SetScrollRegion(int top, int bottom)
    {
        _scrollTop = Math.Clamp(top, 0, Rows - 1);
        _scrollBottom = Math.Clamp(bottom, _scrollTop, Rows - 1);
        SetCursor(0, 0);
    }

    /// <summary>When false, lines scrolled off the top are discarded instead of kept in scrollback.</summary>
    public bool PushErasedToScrollback { get; set; } = true;

    public void ScrollUp(int n)
    {
        n = Math.Clamp(n, 0, Rows);   // a remote host can send a huge count; never loop billions of times
        for (int i = 0; i < n; i++)
        {
            // Push the top scroll-region line into scrollback (only when region is full screen top).
            // Copy the row first: the same array is recycled as the new bottom line below,
            // so storing it by reference would let later writes mutate the saved history.
            if (_scrollTop == 0 && PushErasedToScrollback)
            {
                var snapshot = new Cell[Cols];
                Array.Copy(_grid[0], snapshot, Cols);
                _scrollback.Add(snapshot);
                if (_scrollback.Count > MaxScrollback)
                    _scrollback.RemoveAt(0);
            }

            var recycled = _grid[_scrollTop];
            for (int r = _scrollTop; r < _scrollBottom; r++)
                _grid[r] = _grid[r + 1];
            BlankLine(recycled);
            _grid[_scrollBottom] = recycled;
        }
    }

    public void ScrollDown(int n)
    {
        n = Math.Clamp(n, 0, Rows);
        for (int i = 0; i < n; i++)
        {
            var recycled = _grid[_scrollBottom];
            for (int r = _scrollBottom; r > _scrollTop; r--)
                _grid[r] = _grid[r - 1];
            BlankLine(recycled);
            _grid[_scrollTop] = recycled;
        }
    }

    private static void MarkWrapped(Cell[] line)
    {
        if (line.Length > 0) line[^1].Flags |= CellFlags.LineWrapped;
    }

    /// <summary>True when this row's text continued onto the next row (auto-wrap).</summary>
    public static bool IsWrapped(Cell[] line)
        => line.Length > 0 && (line[^1].Flags & CellFlags.LineWrapped) != 0;

    private void BlankLine(Cell[] line)
    {
        // Honour the current background (BCE — background-colour erase): a full-screen
        // app that sets a background and then clears/scrolls expects the blanked cells
        // to take that colour, matching EraseInLine/EraseChars below.
        for (int c = 0; c < line.Length; c++)
            line[c] = Cell.Blank(_fg, _bg);
    }

    // -------------------- erasing --------------------

    /// <summary>ED — Erase in Display. 0=below, 1=above, 2=all, 3=all+scrollback.</summary>
    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int r = CursorY + 1; r < Rows; r++) BlankLine(_grid[r]);
                break;
            case 1:
                for (int r = 0; r < CursorY; r++) BlankLine(_grid[r]);
                EraseInLine(1);
                break;
            case 2:
                for (int r = 0; r < Rows; r++) BlankLine(_grid[r]);
                break;
            case 3:
                _scrollback.Clear();
                for (int r = 0; r < Rows; r++) BlankLine(_grid[r]);
                break;
        }
    }

    /// <summary>EL — Erase in Line. 0=right, 1=left, 2=whole line.</summary>
    public void EraseInLine(int mode)
    {
        var line = _grid[CursorY];
        int from = mode switch { 1 => 0, 2 => 0, _ => CursorX };
        int to = mode switch { 0 => Cols - 1, 2 => Cols - 1, _ => CursorX };
        for (int c = from; c <= to && c < Cols; c++)
            line[c] = Cell.Blank(_fg, _bg);
    }

    public void EraseChars(int n)
    {
        n = Math.Clamp(n, 0, Cols);
        var line = _grid[CursorY];
        for (int c = CursorX; c < CursorX + n && c < Cols; c++)
            line[c] = Cell.Blank(_fg, _bg);
    }

    public void InsertLines(int n)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom) return;
        n = Math.Clamp(n, 0, Rows);
        for (int i = 0; i < n; i++)
        {
            var recycled = _grid[_scrollBottom];
            for (int r = _scrollBottom; r > CursorY; r--)
                _grid[r] = _grid[r - 1];
            BlankLine(recycled);
            _grid[CursorY] = recycled;
        }
    }

    public void DeleteLines(int n)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom) return;
        n = Math.Clamp(n, 0, Rows);
        for (int i = 0; i < n; i++)
        {
            var recycled = _grid[CursorY];
            for (int r = CursorY; r < _scrollBottom; r++)
                _grid[r] = _grid[r + 1];
            BlankLine(recycled);
            _grid[_scrollBottom] = recycled;
        }
    }

    public void InsertChars(int n)
    {
        n = Math.Clamp(n, 0, Cols);   // clamp so CursorX + n can't overflow into a negative index
        var line = _grid[CursorY];
        for (int c = Cols - 1; c >= CursorX + n; c--)
            line[c] = line[c - n];
        for (int c = CursorX; c < CursorX + n && c < Cols; c++)
            line[c] = Cell.Blank(_fg, _bg);
    }

    public void DeleteChars(int n)
    {
        n = Math.Clamp(n, 0, Cols);   // clamp so c + n can't overflow into a negative index
        var line = _grid[CursorY];
        for (int c = CursorX; c < Cols; c++)
            line[c] = (c + n < Cols) ? line[c + n] : Cell.Blank(_fg, _bg);
    }

    // -------------------- text extraction (for copy) --------------------

    public string GetText(int startRow, int startCol, int endRow, int endCol, bool includeScrollback)
    {
        // Rows are addressed as: 0..scrollback.Count-1 = history, then screen rows.
        var sb = new System.Text.StringBuilder();
        int totalHistory = includeScrollback ? _scrollback.Count : 0;

        // Selection coordinates are captured at mouse-time and may be stale by now
        // (e.g. scrollback trimmed as new output arrived while dragging). Clamp so
        // we never index past the current buffer and crash the UI thread.
        int maxRow = totalHistory + Rows - 1;
        startRow = Math.Clamp(startRow, 0, maxRow);
        endRow = Math.Clamp(endRow, 0, maxRow);
        if (endRow < startRow) return "";

        Cell[] RowAt(int idx)
        {
            if (idx < totalHistory) return _scrollback[idx];
            return _grid[Math.Clamp(idx - totalHistory, 0, Rows - 1)];
        }

        for (int r = startRow; r <= endRow; r++)
        {
            var line = RowAt(r);
            int from = (r == startRow) ? startCol : 0;
            int to = (r == endRow) ? endCol : line.Length - 1;
            var lineSb = new System.Text.StringBuilder();
            for (int c = from; c <= to && c < line.Length; c++)
                lineSb.Append(line[c].Char == '\0' ? ' ' : line[c].Char);
            // A row that auto-wrapped is the same logical line as the next one: emit it
            // verbatim with no break and no trim, so a wrapped command pastes back whole.
            bool wrapped = to == line.Length - 1 && IsWrapped(line);
            if (r != endRow && !wrapped) sb.AppendLine(lineSb.ToString().TrimEnd());
            else if (r != endRow) sb.Append(lineSb.ToString());
            else sb.Append(lineSb.ToString().TrimEnd());
        }
        return sb.ToString();
    }
}
