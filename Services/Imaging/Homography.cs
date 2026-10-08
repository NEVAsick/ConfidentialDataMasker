using System;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

internal static class Homography
{
    public static double[] Solve(SKPoint[] from, SKPoint[] to)
    {
        var a = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            double x = from[i].X, y = from[i].Y, u = to[i].X, v = to[i].Y;
            var r = 2 * i;
            a[r, 0] = x; a[r, 1] = y; a[r, 2] = 1; a[r, 6] = -u * x; a[r, 7] = -u * y; a[r, 8] = u;
            a[r + 1, 3] = x; a[r + 1, 4] = y; a[r + 1, 5] = 1; a[r + 1, 6] = -v * x; a[r + 1, 7] = -v * y; a[r + 1, 8] = v;
        }

        for (var col = 0; col < 8; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < 8; row++)
            {
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            }
            for (var k = 0; k < 9; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);

            for (var row = 0; row < 8; row++)
            {
                if (row == col) continue;
                var factor = a[row, col] / a[col, col];
                for (var k = col; k < 9; k++) a[row, k] -= factor * a[col, k];
            }
        }

        var h = new double[8];
        for (var i = 0; i < 8; i++) h[i] = a[i, 8] / a[i, i];
        return h;
    }

    public static (double X, double Y) Map(double[] h, double x, double y)
    {
        var denominator = h[6] * x + h[7] * y + 1;
        return ((h[0] * x + h[1] * y + h[2]) / denominator, (h[3] * x + h[4] * y + h[5]) / denominator);
    }
}
