using System;
using System.Linq;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public static class LightingCorrection
{
    private const int CellSize = 16;
    private const int SpreadCells = 2;
    private const double PaperPercentile = 0.9;
    private const double MinUnevenRatio = 1.15;
    private const double TargetBrightness = 245;
    private const double MinBackground = 40;
    private const double MaxGain = 3.0;

    public static SKBitmap? EvenOutIfUneven(SKBitmap page)
    {
        var pixels = page.Pixels;
        var paper = PaperBrightnessIfUneven(pixels, page.Width, page.Height);
        if (paper is null) return null;

        var output = new SKColor[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var gain = Math.Min(TargetBrightness / Math.Max(paper[i], MinBackground), MaxGain);
            var c = pixels[i];
            output[i] = new SKColor(Scale(c.Red, gain), Scale(c.Green, gain), Scale(c.Blue, gain), c.Alpha);
        }

        var result = new SKBitmap(page.Width, page.Height, page.ColorType, page.AlphaType);
        result.Pixels = output;
        return result;
    }

    public static double[]? PaperBrightnessIfUneven(SKColor[] pixels, int width, int height)
    {
        var gridW = width / CellSize;
        var gridH = height / CellSize;
        if (gridW < 3 || gridH < 3) return null;

        var background = SpreadAndSmooth(PaperBrightnessPerCell(pixels, width, gridW, gridH));

        var sorted = background.Cast<double>().OrderBy(v => v).ToArray();
        var low = sorted[(int)(0.05 * (sorted.Length - 1))];
        var high = sorted[(int)(0.95 * (sorted.Length - 1))];
        if (high / Math.Max(1, low) < MinUnevenRatio) return null;

        var paper = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++) paper[y * width + x] = Interpolate(background, gridW, gridH, x, y);
        }
        return paper;
    }

    private static byte Scale(byte value, double gain) => (byte)Math.Clamp(Math.Round(value * gain), 0, 255);

    private static double[,] PaperBrightnessPerCell(SKColor[] pixels, int width, int gridW, int gridH)
    {
        var result = new double[gridH, gridW];
        var histogram = new int[256];
        var rank = (int)(PaperPercentile * CellSize * CellSize);
        for (var gy = 0; gy < gridH; gy++)
        {
            for (var gx = 0; gx < gridW; gx++)
            {
                Array.Clear(histogram);
                for (var y = gy * CellSize; y < (gy + 1) * CellSize; y++)
                {
                    for (var x = gx * CellSize; x < (gx + 1) * CellSize; x++)
                    {
                        var c = pixels[y * width + x];
                        histogram[(299 * c.Red + 587 * c.Green + 114 * c.Blue) / 1000]++;
                    }
                }

                var seen = 0;
                var value = 0;
                for (; value < 255; value++)
                {
                    seen += histogram[value];
                    if (seen > rank) break;
                }
                result[gy, gx] = value;
            }
        }
        return result;
    }

    private static double[,] SpreadAndSmooth(double[,] grid)
    {
        return Filter(Filter(grid, (window, v) => Math.Max(window, v), double.MinValue, false), (window, v) => window + v, 0, true);
    }

    private static double[,] Filter(double[,] grid, Func<double, double, double> combine, double seed, bool average)
    {
        var gridH = grid.GetLength(0);
        var gridW = grid.GetLength(1);
        var result = new double[gridH, gridW];
        for (var gy = 0; gy < gridH; gy++)
        {
            for (var gx = 0; gx < gridW; gx++)
            {
                var acc = seed;
                for (var dy = -SpreadCells; dy <= SpreadCells; dy++)
                {
                    for (var dx = -SpreadCells; dx <= SpreadCells; dx++)
                    {
                        var y = Math.Clamp(gy + dy, 0, gridH - 1);
                        var x = Math.Clamp(gx + dx, 0, gridW - 1);
                        acc = combine(acc, grid[y, x]);
                    }
                }
                var side = 2 * SpreadCells + 1;
                result[gy, gx] = average ? acc / (side * side) : acc;
            }
        }
        return result;
    }

    private static double Interpolate(double[,] grid, int gridW, int gridH, int x, int y)
    {
        var gx = Math.Clamp((x + 0.5) / CellSize - 0.5, 0, gridW - 1);
        var gy = Math.Clamp((y + 0.5) / CellSize - 0.5, 0, gridH - 1);
        var x0 = Math.Min((int)gx, gridW - 2);
        var y0 = Math.Min((int)gy, gridH - 2);
        var fx = gx - x0;
        var fy = gy - y0;
        return grid[y0, x0] * (1 - fx) * (1 - fy) + grid[y0, x0 + 1] * fx * (1 - fy) +
            grid[y0 + 1, x0] * (1 - fx) * fy + grid[y0 + 1, x0 + 1] * fx * fy;
    }
}
