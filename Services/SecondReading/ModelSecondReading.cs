using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed record SecondReadingResult(
    List<OcrLine> Lines, List<SKRectI> ApproximateAreas, int Regions, int Replaced, int Added, string? Error)
{
    public bool Changed => Replaced + Added > 0;
}

public sealed class ModelSecondReading
{
    private const float LowWordConfidence = 60f;
    private const float LowLineConfidence = 70f;
    private const int MinLowConfidenceChars = 2;
    private const int MinModelLineChars = 3;
    private const double MatchSimilarity = 0.6;
    private const double AreaGrowToWidth = 0.75;
    private const double AreaGrowToHeight = 0.5;
    private const double NestedAreaShare = 0.8;
    private const double LineInsideShare = 0.5;
    private const double LinePaddingToHeight = 1.0;
    private const int MinLineRegionHeightPx = 120;
    private const double NearDuplicateToHeight = 2.0;
    private const double ApproximatePaddingToHeight = 0.2;
    private const float NewLineConfidence = 90f;
    private const int DefaultTextHeightPx = 30;

    private readonly OllamaVisionClient _client;
    private readonly int _maxRegions;
    private readonly int _maxNewLines;

    public ModelSecondReading(OllamaVisionClient client, VisionModelSettings settings)
    {
        _client = client;
        _maxRegions = Math.Max(1, settings.MaxRegionsPerPage);
        _maxNewLines = Math.Max(0, settings.MaxNewLinesPerRegion);
    }

    public async Task<SecondReadingResult> ImproveAsync(
        SKBitmap bitmap, List<OcrLine> lines, IReadOnlyList<SKRectI> imageAreas, Func<string, bool> hasSecret,
        CancellationToken ct)
    {
        var result = new List<OcrLine>(lines);
        var approximate = new List<SKRectI>();
        var ocrLines = lines.Where(IsRecognized).ToList();
        if (ocrLines.Count == 0) return new SecondReadingResult(result, approximate, 0, 0, 0, null);

        var textHeight = MedianWordHeight(ocrLines);
        var areas = ocrLines.Count == lines.Count ? imageAreas : Array.Empty<SKRectI>();
        var regions = FindRegions(ocrLines, areas, bitmap.Width, bitmap.Height, textHeight);

        byte[]? luma = null;
        var replaced = 0;
        var added = 0;
        string? error = null;

        foreach (var (isArea, rect) in regions)
        {
            ct.ThrowIfCancellationRequested();

            List<string> texts;
            try
            {
                texts = await _client.ReadLinesAsync(bitmap, rect, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                error = ex.Message;
                continue;
            }

            var candidates = Enumerable.Range(0, result.Count)
                .Where(i => IsRecognized(result[i]) && InsideShare(result[i].PixelBounds, rect) >= LineInsideShare)
                .ToList();
            var used = new HashSet<int>();
            var unknown = new List<string>();

            foreach (var text in texts)
            {
                if (text.Count(char.IsLetterOrDigit) < MinModelLineChars) continue;

                var best = -1;
                var bestScore = 0.0;
                foreach (var i in candidates)
                {
                    if (used.Contains(i)) continue;
                    var score = TextAlignment.Similarity(result[i].Text, text);
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = i;
                }

                if (best >= 0 && bestScore >= MatchSimilarity)
                {
                    used.Add(best);
                    var words = TextAlignment.Realign(result[best].Words, text);
                    if (words is null) continue;
                    result[best] = MakeLine(words, result[best].Confidence);
                    replaced++;
                }
                else if (isArea && hasSecret(text))
                {
                    unknown.Add(text);
                }
            }

            if (unknown.Count == 0) continue;

            luma ??= InkPlacement.ToLuma(bitmap);
            var known = result.SelectMany(l => l.Words).Select(w => w.PixelBounds).ToList();
            foreach (var text in unknown.Take(_maxNewLines))
            {
                SKRect? located;
                try
                {
                    located = await _client.LocateAsync(bitmap, rect, text, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    error = ex.Message;
                    continue;
                }
                if (located is null) continue;

                var near = Grow(located.Value, NearDuplicateToHeight * textHeight);
                if (result.Any(l => Intersects(l.PixelBounds, near) && TextAlignment.Similarity(l.Text, text) >= MatchSimilarity))
                {
                    continue;
                }

                var place = InkPlacement.Place(luma, bitmap.Width, bitmap.Height, located.Value, known, textHeight, rect);
                if (place is null) continue;

                known.Add(place.Value);
                result.Add(MakeLine(TextAlignment.WordsInBox(text, place.Value, NewLineConfidence), NewLineConfidence));
                var pad = (int)Math.Round(ApproximatePaddingToHeight * textHeight);
                approximate.Add(Clip(
                    new SKRectI(place.Value.Left - pad, place.Value.Top - pad, place.Value.Right + pad, place.Value.Bottom + pad),
                    bitmap.Width, bitmap.Height));
                added++;
            }
        }

        return new SecondReadingResult(result, approximate, regions.Count, replaced, added, error);
    }

    private List<(bool IsArea, SKRectI Rect)> FindRegions(
        List<OcrLine> ocrLines, IReadOnlyList<SKRectI> imageAreas, int width, int height, double textHeight)
    {
        var areas = imageAreas
            .Select(b => Clip(new SKRectI(
                (int)(b.Left - AreaGrowToWidth * b.Width),
                (int)(b.Top - AreaGrowToHeight * b.Height),
                (int)(b.Right + AreaGrowToWidth * b.Width),
                (int)(b.Bottom + AreaGrowToHeight * b.Height)), width, height))
            .ToList();

        var kept = new List<SKRectI>();
        for (var i = 0; i < areas.Count; i++)
        {
            var nested = false;
            for (var j = 0; j < areas.Count && !nested; j++)
            {
                if (j == i) continue;
                nested = InsideShare(areas[i], areas[j]) >= NestedAreaShare &&
                    (InsideShare(areas[j], areas[i]) < NestedAreaShare || j < i);
            }
            if (!nested) kept.Add(areas[i]);
        }

        var regions = kept.Select(a => (true, a)).ToList();
        foreach (var line in ocrLines)
        {
            if (!IsLowConfidence(line)) continue;
            if (kept.Any(a => InsideShare(line.PixelBounds, a) >= LineInsideShare)) continue;

            var pad = LinePaddingToHeight * textHeight;
            var b = line.PixelBounds;
            var rect = Clip(new SKRectI((int)(b.Left - pad), (int)(b.Top - pad), (int)(b.Right + pad), (int)(b.Bottom + pad)), width, height);
            regions.Add((false, EnsureMinHeight(rect, MinLineRegionHeightPx, height)));
        }

        return regions.Take(_maxRegions).ToList();
    }

    private static bool IsRecognized(OcrLine line) => line.Confidence < OcrWordText.TextLayerConfidence;

    private static bool IsLowConfidence(OcrLine line)
    {
        if (line.Text.Count(char.IsLetterOrDigit) < MinLowConfidenceChars) return false;
        return line.Confidence < LowLineConfidence ||
            line.Words.Any(w => w.Confidence < LowWordConfidence && w.Text.Count(char.IsLetterOrDigit) >= MinLowConfidenceChars);
    }

    private static double MedianWordHeight(List<OcrLine> lines)
    {
        var heights = lines.SelectMany(l => l.Words).Select(w => w.PixelBounds.Height).OrderBy(h => h).ToList();
        return heights.Count == 0 ? DefaultTextHeightPx : heights[heights.Count / 2];
    }

    private static OcrLine MakeLine(List<OcrWord> words, float confidence)
    {
        var bounds = words.Select(w => w.PixelBounds).Aggregate(RectMath.Union);
        return new OcrLine(string.Join(" ", words.Select(w => w.Text)), bounds, confidence, words);
    }

    private static double InsideShare(SKRectI a, SKRectI b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (w <= 0 || h <= 0) return 0;
        return (double)w * h / Math.Max(1, (long)a.Width * a.Height);
    }

    private static bool Intersects(SKRectI a, SKRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private static SKRect Grow(SKRect r, double by) =>
        new((float)(r.Left - by), (float)(r.Top - by), (float)(r.Right + by), (float)(r.Bottom + by));

    private static SKRectI Clip(SKRectI r, int width, int height) =>
        new(Math.Max(0, r.Left), Math.Max(0, r.Top), Math.Min(width, r.Right), Math.Min(height, r.Bottom));

    private static SKRectI EnsureMinHeight(SKRectI r, int minHeight, int height)
    {
        if (r.Height >= minHeight) return r;
        var extra = (minHeight - r.Height + 1) / 2;
        return new SKRectI(r.Left, Math.Max(0, r.Top - extra), r.Right, Math.Min(height, r.Bottom + extra));
    }
}
