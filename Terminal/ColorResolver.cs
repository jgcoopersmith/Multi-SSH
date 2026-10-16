using System.Windows.Media;

namespace MultiSSH.Terminal;

/// <summary>Turns a <see cref="Cell"/> colour code into a WPF <see cref="Color"/>.</summary>
public static class ColorResolver
{
    private static readonly int[] CubeSteps = { 0x00, 0x5F, 0x87, 0xAF, 0xD7, 0xFF };

    public static Color Resolve(int code, ColorScheme scheme, bool isForeground)
    {
        // Default, or any other negative code: never index the palette with it — this runs in
        // OnRender, where a throw becomes an error box on every redraw.
        if (code < 0)
            return isForeground ? scheme.Foreground : scheme.Background;

        if (code >= Cell.TrueColorBase)
        {
            int rgb = code - Cell.TrueColorBase;
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        if (code < 16)
            return scheme.Palette[code];

        if (code < 232)
        {
            int i = code - 16;
            int r = CubeSteps[(i / 36) % 6];
            int g = CubeSteps[(i / 6) % 6];
            int b = CubeSteps[i % 6];
            return Color.FromRgb((byte)r, (byte)g, (byte)b);
        }

        // 232..255 grayscale ramp
        int level = 8 + (code - 232) * 10;
        return Color.FromRgb((byte)level, (byte)level, (byte)level);
    }
}
