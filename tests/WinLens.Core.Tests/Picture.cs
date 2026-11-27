using WinLens.Layout;

namespace WinLens.Core.Tests;

/// <summary>A screenshot for tests: one colour, with rectangles of other colours painted on it.</summary>
internal sealed class Picture : IPixels
{
    private readonly int[,] _rgb;

    public Picture(int width, int height, int colour)
    {
        _rgb = new int[height, width];
        Width = width;
        Height = height;
        Paint(0, 0, width, height, colour);
    }

    public int Width { get; }
    public int Height { get; }
    public int Rgb(int x, int y) => _rgb[y, x];

    public Picture Paint(int x, int y, int width, int height, int colour)
    {
        for (int row = Math.Max(0, y); row < Math.Min(Height, y + height); row++)
        for (int column = Math.Max(0, x); column < Math.Min(Width, x + width); column++)
            _rgb[row, column] = colour;
        return this;
    }
}
