namespace MultiSSH.Terminal;

[Flags]
public enum CellFlags : byte
{
    None      = 0,
    Bold      = 1 << 0,
    Underline = 1 << 1,
    Inverse   = 1 << 2,
    Dim       = 1 << 3,
    Italic    = 1 << 4,
    Hidden    = 1 << 5,
    /// <summary>
    /// Set on the LAST cell of a line whose text ran off the right edge and
    /// auto-wrapped onto the next line. Copying a wrapped line must not insert a
    /// newline, or a long pasted command comes back broken in two. The flag lives
    /// on the cell (not a parallel array) so it travels with the row when lines are
    /// shuffled by scrolling, and is cleared automatically when the row is blanked
    /// or its last column is overwritten.
    /// </summary>
    LineWrapped = 1 << 6,
}

/// <summary>
/// One character cell. Foreground/background are encoded as:
///   -1              = default colour
///   0..255          = ANSI / 256-colour palette index
///   >= TrueColorBase= packed 24-bit RGB (value - TrueColorBase)
/// </summary>
public struct Cell
{
    public const int Default = -1;
    public const int TrueColorBase = 0x1000000;

    public char Char;
    public int Fg;
    public int Bg;
    public CellFlags Flags;

    public static Cell Blank(int fg, int bg) => new() { Char = ' ', Fg = fg, Bg = bg, Flags = CellFlags.None };

    public bool SameStyle(in Cell o) => Fg == o.Fg && Bg == o.Bg && Flags == o.Flags;

    public static int PackRgb(byte r, byte g, byte b) => TrueColorBase | (r << 16) | (g << 8) | b;
}
