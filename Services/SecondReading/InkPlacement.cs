using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

internal static class InkPlacement
{
    private const int InkMaxLuma = 150;
    private const double WindowGrowToWidth = 0.3;
    private const double WindowGrowToHeight = 1.5;
    private const int MaskPaddingPx = 2;
    private const int MinWindowPx = 4;
    private const int MinPeakRowInk = 3;
    private const int MinRowInk = 2;
    private const double RowShareOfPeak = 0.12;
    private const int MaxRowGap = 2;
    private const double MinBandToHeight = 0.5;
    private const double MaxBandToHeight = 2.5;
    private const double MaxBandShiftToHeight = 1.0;
    private const double ColumnGapToHeight = 1.2;
    private const double MinRunToHeight = 0.5;

    public static byte[] ToLuma(SKBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        var luma = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            luma[i] = (byte)((299 * c.Red + 587 * c.Green + 114 * c.Blue) / 1000);
        }
        return luma;
    }

    public static SKRectI? Place(
        byte[] luma, int width, int height, SKRect located, IReadOnlyList<SKRectI> known, double textHeight, SKRectI bounds)
    {
        var locatedWidth = Math.Max(1.0, located.Width);
        var x0 = Math.Max(0, (int)Math.Max(bounds.Left, located.Left - WindowGrowToWidth * locatedWidth));
        var x1 = Math.Min(width, (int)Math.Min(bounds.Right, located.Right + WindowGrowToWidth * locatedWidth));
        var y0 = Math.Max(0, (int)Math.Max(bounds.Top, located.Top - WindowGrowToHeight * textHeight));
        var y1 = Math.Min(height, (int)Math.Min(bounds.Bottom, located.Bottom + WindowGrowToHeight * textHeight));
        if (x1 - x0 < MinWindowPx || y1 - y0 < MinWindowPx) return null;

        var w = x1 - x0;
        var h = y1 - y0;
        var ink = new bool[h, w];
        for (var y = 0; y < h; y++)
        {
            var row = (y0 + y) * width + x0;
            for (var x = 0; x < w; x++) ink[y, x] = luma[row + x] < InkMaxLuma;
        }

        foreach (var box in known)
        {
            var left = Math.Max(box.Left - MaskPaddingPx, x0) - x0;
            var top = Math.Max(box.Top - MaskPaddingPx, y0) - y0;
            var right = Math.Min(box.Right + MaskPaddingPx, x1) - x0;
            var bottom = Math.Min(box.Bottom + MaskPaddingPx, y1) - y0;
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++) ink[y, x] = false;
            }
        }

        var rows = new int[h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (ink[y, x]) rows[y]++;
            }
        }

        var peak = rows.Max();
        if (peak < MinPeakRowInk) return null;

        var threshold = Math.Max(MinRowInk, RowShareOfPeak * peak);
        var center = (located.Top + located.Bottom) / 2.0 - y0;
        (int Start, int End)? best = null;
        var bestDistance = double.MaxValue;
        foreach (var (start, end) in Runs(rows.Select(v => v >= threshold).ToArray(), MaxRowGap))
        {
            var bandHeight = end - start + 1;
            if (bandHeight < MinBandToHeight * textHeight || bandHeight > MaxBandToHeight * textHeight) continue;
            var distance = Math.Abs((start + end) / 2.0 - center);
            if (distance > MaxBandShiftToHeight * textHeight || distance >= bestDistance) continue;
            bestDistance = distance;
            best = (start, end);
        }
        if (best is null) return null;

        var (bandStart, bandEnd) = best.Value;
        var columns = new bool[w];
        for (var x = 0; x < w; x++)
        {
            for (var y = bandStart; y <= bandEnd; y++)
            {
                if (!ink[y, x]) continue;
                columns[x] = true;
                break;
            }
        }

        var runs = Runs(columns, ColumnGapToHeight * textHeight)
            .Where(r => r.End - r.Start + 1 >= MinRunToHeight * textHeight)
            .ToList();
        if (runs.Count == 0) return null;

        var lo = located.Left - x0;
        var hi = located.Right - x0;
        var hits = runs.Where(r => Math.Min(r.End, hi) - Math.Max(r.Start, lo) > 0).ToList();
        if (hits.Count == 0)
        {
            hits.Add(runs.MinBy(r => Math.Min(Math.Abs(r.Start - hi), Math.Abs(r.End - lo))));
        }

        return new SKRectI(x0 + hits.Min(r => r.Start), y0 + bandStart, x0 + hits.Max(r => r.End) + 1, y0 + bandEnd + 1);
    }

    private static List<(int Start, int End)> Runs(bool[] mask, double maxGap)
    {
        var runs = new List<(int Start, int End)>();
        var start = -1;
        var last = -1;
        for (var i = 0; i < mask.Length; i++)
        {
            if (!mask[i]) continue;
            if (start < 0)
            {
                start = i;
            }
            else if (i - last - 1 > maxGap)
            {
                runs.Add((start, last));
                start = i;
            }
            last = i;
        }
        if (start >= 0) runs.Add((start, last));
        return runs;
    }
}
