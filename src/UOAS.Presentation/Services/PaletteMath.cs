namespace UOAS.Presentation.Services;

public static class PaletteMath
{
    public static ushort Parse(string color)
    {
        if (color.Length != 7 || color[0] != '#') throw new ArgumentException("Use an RGB color.");
        int Channel(int start) => (Convert.ToInt32(color.Substring(start, 2), 16) * 31 + 127) / 255;
        return (ushort)(Channel(1) << 10 | Channel(3) << 5 | Channel(5));
    }
    public static ushort[] Gradient(ushort shadow, ushort middle, ushort highlight) => Enumerable.Range(0, 32).Select(index =>
    {
        int a = index <= 15 ? shadow : middle, b = index <= 15 ? middle : highlight;
        int step = index <= 15 ? index : index - 15, length = index <= 15 ? 15 : 16;
        int Blend(int shift) => ((((a >> shift) & 31) * (length - step) + ((b >> shift) & 31) * step + length / 2) / length) << shift;
        return (ushort)(Blend(10) | Blend(5) | Blend(0));
    }).ToArray();
    public static uint Apply(uint pixel, IReadOnlyList<ushort> colors, bool grayOnly)
    {
        if (colors.Count != 32 || pixel >> 24 == 0) return pixel;
        int r = (int)(pixel >> 19) & 31, g = (int)(pixel >> 11) & 31, b = (int)(pixel >> 3) & 31;
        if (grayOnly && (r != g || r != b)) return pixel;
        uint color = colors[r];
        uint Expand(int shift) { uint value = (color >> shift) & 31; return (value << 3) | (value >> 2); }
        return (pixel & 0xFF000000) | Expand(10) << 16 | Expand(5) << 8 | Expand(0);
    }
}
