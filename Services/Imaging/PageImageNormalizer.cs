using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public static class PageImageNormalizer
{
    private const int AnalysisWidth = 400;
    private const double MinCornerShiftFraction = 0.02;
    private const double MinBackgroundShare = 0.05;
    private const double MinPaperShare = 0.5;
    private const int MinPaperContrast = 50;
    private const double BorderRingFraction = 0.02;
    private const int HistogramSmoothingWidth = 9;

    public static SKBitmap FlattenIfPhotographed(SKBitmap page)
    {
        var quad = FindPhotographedPage(page);
        return quad is null ? page : Flatten(page, quad);
    }

    public static SKPoint[]? FindPhotographedPage(SKBitmap page)
    {
        var width = page.Width;
        var height = page.Height;
        if (width < AnalysisWidth || height < AnalysisWidth) return null;

        var smallWidth = AnalysisWidth;
        var smallHeight = Math.Max(1, (int)Math.Round((double)height * smallWidth / width));
        var luma = DownscaleLuma(page.Pixels, width, height, smallWidth, smallHeight);
        var paper = LargestBrightComponent(luma, smallWidth, smallHeight, PaperThreshold(luma, smallWidth, smallHeight));
        if (paper.Count == 0) return null;

        var smallQuad = ExtremeCorners(paper, smallWidth);
        if (!IsConvex(smallQuad)) return null;
        if (!HasDarkBackground(luma, smallWidth, smallHeight, smallQuad)) return null;

        var scaleX = (double)width / smallWidth;
        var scaleY = (double)height / smallHeight;
        var quad = smallQuad
            .Select(p => new SKPoint((float)((p.X + 0.5) * scaleX), (float)((p.Y + 0.5) * scaleY)))
            .ToArray();

        var imageCorners = new[]
        {
            new SKPoint(0, 0), new SKPoint(width, 0), new SKPoint(width, height), new SKPoint(0, height)
        };
        for (var i = 0; i < 4; i++)
        {
            if (SKPoint.Distance(quad[i], imageCorners[i]) < MinCornerShiftFraction * width) return null;
        }

        return quad;
    }

    public static SKBitmap Flatten(SKBitmap page, SKPoint[] quad)
    {
        var width = page.Width;
        var height = page.Height;
        var outputCorners = new[]
        {
            new SKPoint(0, 0), new SKPoint(width, 0), new SKPoint(width, height), new SKPoint(0, height)
        };
        var h = Homography.Solve(outputCorners, quad);

        var source = page.Pixels;
        var output = new SKColor[source.Length];
        for (var y = 0; y < height; y++)
        {
            var oy = y + 0.5;
            for (var x = 0; x < width; x++)
            {
                var ox = x + 0.5;
                var (sx, sy) = Homography.Map(h, ox, oy);
                output[y * width + x] = SampleBilinear(source, width, height, sx - 0.5, sy - 0.5);
            }
        }

        var result = new SKBitmap(width, height, page.ColorType, page.AlphaType);
        result.Pixels = output;
        return result;
    }

    private static SKColor SampleBilinear(SKColor[] pixels, int width, int height, double x, double y)
    {
        var x0 = Math.Clamp((int)Math.Floor(x), 0, width - 2);
        var y0 = Math.Clamp((int)Math.Floor(y), 0, height - 2);
        var fx = Math.Clamp(x - x0, 0, 1);
        var fy = Math.Clamp(y - y0, 0, 1);

        var c00 = pixels[y0 * width + x0];
        var c10 = pixels[y0 * width + x0 + 1];
        var c01 = pixels[(y0 + 1) * width + x0];
        var c11 = pixels[(y0 + 1) * width + x0 + 1];

        byte Mix(byte a, byte b, byte c, byte d) =>
            (byte)Math.Round(a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) + c * (1 - fx) * fy + d * fx * fy);

        return new SKColor(
            Mix(c00.Red, c10.Red, c01.Red, c11.Red),
            Mix(c00.Green, c10.Green, c01.Green, c11.Green),
            Mix(c00.Blue, c10.Blue, c01.Blue, c11.Blue),
            Mix(c00.Alpha, c10.Alpha, c01.Alpha, c11.Alpha));
    }

    private static byte[] DownscaleLuma(SKColor[] pixels, int width, int height, int smallWidth, int smallHeight)
    {
        var sums = new long[smallWidth * smallHeight];
        var counts = new int[smallWidth * smallHeight];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min(smallHeight - 1, (int)((long)y * smallHeight / height));
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Min(smallWidth - 1, (int)((long)x * smallWidth / width));
                var c = pixels[y * width + x];
                var index = sy * smallWidth + sx;
                sums[index] += (299 * c.Red + 587 * c.Green + 114 * c.Blue) / 1000;
                counts[index]++;
            }
        }

        var luma = new byte[sums.Length];
        for (var i = 0; i < luma.Length; i++)
        {
            luma[i] = (byte)(counts[i] == 0 ? 0 : sums[i] / counts[i]);
        }
        return luma;
    }

    private static int PaperThreshold(byte[] luma, int width, int height)
    {
        var otsu = OtsuThreshold(luma);
        var background = BorderMedian(luma, width, height);
        if (background >= otsu) return otsu;

        var histogram = new double[256];
        foreach (var v in luma) histogram[v]++;
        var smoothed = new double[256];
        var half = HistogramSmoothingWidth / 2;
        for (var t = 0; t < 256; t++)
        {
            for (var k = Math.Max(0, t - half); k <= Math.Min(255, t + half); k++) smoothed[t] += histogram[k];
        }

        var paperPeak = otsu;
        for (var t = otsu; t < 256; t++)
        {
            if (smoothed[t] > smoothed[paperPeak]) paperPeak = t;
        }

        var valley = background;
        for (var t = background; t <= paperPeak; t++)
        {
            if (smoothed[t] < smoothed[valley]) valley = t;
        }
        return valley;
    }

    private static int BorderMedian(byte[] luma, int width, int height)
    {
        var ring = Math.Max(1, (int)Math.Round(BorderRingFraction * width));
        var values = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            var edgeRow = y < ring || y >= height - ring;
            for (var x = 0; x < width; x++)
            {
                if (edgeRow || x < ring || x >= width - ring) values.Add(luma[y * width + x]);
            }
        }
        values.Sort();
        return values[values.Count / 2];
    }

    private static int OtsuThreshold(byte[] luma)
    {
        var histogram = new double[256];
        foreach (var v in luma) histogram[v]++;

        double total = luma.Length, sumAll = 0;
        for (var t = 0; t < 256; t++) sumAll += t * histogram[t];

        double weightBack = 0, sumBack = 0, best = -1;
        var threshold = 128;
        for (var t = 0; t < 256; t++)
        {
            weightBack += histogram[t];
            if (weightBack == 0) continue;
            var weightFore = total - weightBack;
            if (weightFore == 0) break;
            sumBack += t * histogram[t];
            var meanBack = sumBack / weightBack;
            var meanFore = (sumAll - sumBack) / weightFore;
            var between = weightBack * weightFore * (meanBack - meanFore) * (meanBack - meanFore);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }
        return threshold;
    }

    private static List<int> LargestBrightComponent(byte[] luma, int width, int height, int threshold)
    {
        var visited = new bool[luma.Length];
        var largest = new List<int>();
        var queue = new Queue<int>();

        for (var start = 0; start < luma.Length; start++)
        {
            if (visited[start] || luma[start] <= threshold) continue;

            var component = new List<int>();
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                component.Add(index);
                var x = index % width;
                var y = index / width;
                TryVisit(x - 1, y);
                TryVisit(x + 1, y);
                TryVisit(x, y - 1);
                TryVisit(x, y + 1);
            }

            if (component.Count > largest.Count) largest = component;
        }
        return largest;

        void TryVisit(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            var index = y * width + x;
            if (visited[index] || luma[index] <= threshold) return;
            visited[index] = true;
            queue.Enqueue(index);
        }
    }

    private static SKPoint[] ExtremeCorners(List<int> component, int width)
    {
        int topLeft = component[0], topRight = component[0], bottomRight = component[0], bottomLeft = component[0];
        foreach (var index in component)
        {
            if (Sum(index) < Sum(topLeft)) topLeft = index;
            if (Sum(index) > Sum(bottomRight)) bottomRight = index;
            if (Difference(index) > Difference(topRight)) topRight = index;
            if (Difference(index) < Difference(bottomLeft)) bottomLeft = index;
        }
        return new[] { ToPoint(topLeft), ToPoint(topRight), ToPoint(bottomRight), ToPoint(bottomLeft) };

        int Sum(int i) => i % width + i / width;
        int Difference(int i) => i % width - i / width;
        SKPoint ToPoint(int i) => new(i % width, i / width);
    }

    private static bool IsConvex(SKPoint[] quad)
    {
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = quad[i];
            var b = quad[(i + 1) % 4];
            var c = quad[(i + 2) % 4];
            var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-6) return false;
            var s = Math.Sign(cross);
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    private static bool HasDarkBackground(byte[] luma, int width, int height, SKPoint[] quad)
    {
        var insideHistogram = new int[256];
        long outsideSum = 0;
        int inside = 0, outside = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = luma[y * width + x];
                if (IsInside(quad, x, y))
                {
                    insideHistogram[v]++;
                    inside++;
                }
                else
                {
                    outsideSum += v;
                    outside++;
                }
            }
        }

        var total = (double)luma.Length;
        if (inside < MinPaperShare * total || outside < MinBackgroundShare * total) return false;

        var half = inside / 2;
        var median = 0;
        for (var seen = 0; median < 255; median++)
        {
            seen += insideHistogram[median];
            if (seen > half) break;
        }
        return median - (double)outsideSum / outside >= MinPaperContrast;
    }

    private static bool IsInside(SKPoint[] quad, double x, double y)
    {
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = quad[i];
            var b = quad[(i + 1) % 4];
            var cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            var s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }
}
