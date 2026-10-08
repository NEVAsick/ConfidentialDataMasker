using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfidentialDataMasker.Models;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed record PageDetectionResult(List<SuggestedRect> Suggestions, SecondReadingResult? SecondReading);

public static class PageDetection
{
    private const double CoveredShare = 0.9;
    private const double ApproximateInsideShare = 0.5;

    public static async Task<PageDetectionResult> DetectAsync(
        ConfidentialDataDetector detector, SKBitmap bitmap, List<OcrLine> lines, ModelSecondReading? secondReading,
        CancellationToken ct)
    {
        var suggestions = detector.Detect(bitmap, lines);
        if (secondReading is null) return new PageDetectionResult(suggestions, null);

        var imageAreas = suggestions
            .Where(s => s.Kind != SuggestionKind.Text)
            .Select(s => ToPixels(s, bitmap))
            .ToList();
        var reading = await secondReading.ImproveAsync(bitmap, lines, imageAreas, detector.ContainsConfidentialText, ct);
        if (!reading.Changed) return new PageDetectionResult(suggestions, reading);

        var merged = new List<SuggestedRect>(suggestions);
        foreach (var extra in detector.Detect(bitmap, reading.Lines))
        {
            if (extra.Kind != SuggestionKind.Text) continue;
            var expanded = ExpandToApproximateArea(extra, reading.ApproximateAreas, bitmap);
            if (merged.Any(s => Covers(s, expanded))) continue;
            merged.Add(expanded);
        }

        return new PageDetectionResult(merged, reading);
    }

    private static SuggestedRect ExpandToApproximateArea(SuggestedRect suggestion, List<SKRectI> areas, SKBitmap bitmap)
    {
        var px = ToPixels(suggestion, bitmap);
        foreach (var area in areas)
        {
            if (InsideShare(px, area) < ApproximateInsideShare) continue;
            var union = RectMath.Union(px, area);
            double width = bitmap.Width, height = bitmap.Height;
            return new SuggestedRect(
                union.Left / width, union.Top / height, union.Width / width, union.Height / height,
                suggestion.Kind, suggestion.Label, suggestion.MatchedText, suggestion.LineText);
        }
        return suggestion;
    }

    private static bool Covers(SuggestedRect outer, SuggestedRect inner)
    {
        var w = Math.Min(outer.X + outer.Width, inner.X + inner.Width) - Math.Max(outer.X, inner.X);
        var h = Math.Min(outer.Y + outer.Height, inner.Y + inner.Height) - Math.Max(outer.Y, inner.Y);
        return w > 0 && h > 0 && w * h >= CoveredShare * inner.Width * inner.Height;
    }

    private static SKRectI ToPixels(SuggestedRect s, SKBitmap bitmap) => new(
        (int)Math.Floor(s.X * bitmap.Width),
        (int)Math.Floor(s.Y * bitmap.Height),
        (int)Math.Ceiling((s.X + s.Width) * bitmap.Width),
        (int)Math.Ceiling((s.Y + s.Height) * bitmap.Height));

    private static double InsideShare(SKRectI a, SKRectI b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (w <= 0 || h <= 0) return 0;
        return (double)w * h / Math.Max(1, (long)a.Width * a.Height);
    }
}
