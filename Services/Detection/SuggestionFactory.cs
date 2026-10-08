using System;
using ConfidentialDataMasker.Models;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

internal static class SuggestionFactory
{
    private const int PaddingPx = 3;

    public static SuggestedRect Create(
        SKBitmap bitmap, SKRectI px, SuggestionKind kind, string label, string? matchedText, string? lineText)
    {
        var (x, y, width, height) = Normalize(bitmap, px);
        return new SuggestedRect(x, y, width, height, kind, label, matchedText, lineText);
    }

    public static SuggestedRect Create(
        SKBitmap bitmap, SKRectI px, SuggestionKind kind, string label,
        double aspect, double fillRatio, int maxColumnThicknessBlocks, int rectWidthPx, int rectHeightPx)
    {
        var (x, y, width, height) = Normalize(bitmap, px);
        return new SuggestedRect(
            x, y, width, height, kind, label,
            aspect, fillRatio, maxColumnThicknessBlocks, rectWidthPx, rectHeightPx);
    }

    private static (double X, double Y, double Width, double Height) Normalize(SKBitmap bitmap, SKRectI px)
    {
        var left = Math.Max(0, px.Left - PaddingPx);
        var top = Math.Max(0, px.Top - PaddingPx);
        var right = Math.Min(bitmap.Width, px.Right + PaddingPx);
        var bottom = Math.Min(bitmap.Height, px.Bottom + PaddingPx);

        double width = bitmap.Width, height = bitmap.Height;
        return (left / width, top / height, (right - left) / width, (bottom - top) / height);
    }
}
