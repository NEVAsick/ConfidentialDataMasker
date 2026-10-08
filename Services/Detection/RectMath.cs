using System;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

internal static class RectMath
{
    public static SKRectI Union(SKRectI a, SKRectI b)
    {
        return new SKRectI(
            Math.Min(a.Left, b.Left),
            Math.Min(a.Top, b.Top),
            Math.Max(a.Right, b.Right),
            Math.Max(a.Bottom, b.Bottom));
    }
}
