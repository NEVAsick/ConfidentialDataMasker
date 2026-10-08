using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkiaSharp;
using UglyToad.PdfPig;

namespace ConfidentialDataMasker.Services;

public sealed record PageText(List<OcrLine> Lines, string Reason);

public sealed class PageTextExtractor
{
    private const int MinTextLayerWords = 3;
    private const double ScannedPageImageFraction = 0.8;
    private const double SignificantImageFraction = 0.005;
    private const double MaxSuspiciousCharFraction = 0.1;
    private const double MinLetterOrDigitFraction = 0.5;
    private const string FillerChars = "_.-–—…";
    private const double MaxAspectMismatch = 0.02;
    private const double ColumnGapToHeightRatio = 2.5;
    private const double MaxJoinGapToHeightRatio = 8.0;
    private const double ColumnGapToMedianGapRatio = 2.0;
    private const double CoveredByLayerFraction = 0.3;

    private readonly OcrService _ocr;

    public PageTextExtractor(OcrService ocr)
    {
        _ocr = ocr;
    }

    public PageText Extract(string pdfPath, int pageIndex, SKBitmap bitmap)
    {
        LayerInfo layer;
        try
        {
            layer = ReadLayer(pdfPath, pageIndex, bitmap.Width, bitmap.Height);
        }
        catch (Exception ex)
        {
            return OcrOnly(bitmap, "текст из PDF не прочитан (" + ex.Message + ")");
        }

        if (layer.UnusableReason is not null) return OcrOnly(bitmap, layer.UnusableReason);
        if (layer.ImageFraction >= ScannedPageImageFraction) return OcrOnly(bitmap, "страница — скан");
        if (layer.Words.Count < MinTextLayerWords) return OcrOnly(bitmap, "текста в PDF нет");
        if (SuspiciousCharFraction(layer.Words) > MaxSuspiciousCharFraction ||
            LetterOrDigitFraction(layer.Words) < MinLetterOrDigitFraction)
        {
            return OcrOnly(bitmap, "текст в PDF в нечитаемой кодировке");
        }

        var layerLines = GroupIntoLines(layer.Words);
        if (layer.ImageFraction < SignificantImageFraction)
        {
            return new PageText(layerLines, "текст из PDF");
        }

        var ocrLines = _ocr.RecognizeLines(bitmap);
        var fromImages = ocrLines.SelectMany(l => UncoveredParts(l, layer.Words)).ToList();
        return new PageText(
            layerLines.Concat(fromImages).ToList(),
            $"текст из PDF + распознано на картинках: {fromImages.Count} строк");
    }

    private PageText OcrOnly(SKBitmap bitmap, string reason)
    {
        using var evenedOut = LightingCorrection.EvenOutIfUneven(bitmap);
        var lines = _ocr.RecognizeLines(evenedOut ?? bitmap);
        return new PageText(lines, "распознавание: " + reason + (evenedOut is null ? "" : ", освещение выровнено"));
    }

    private sealed class LayerInfo
    {
        public List<OcrWord> Words { get; } = new();
        public double ImageFraction { get; set; }
        public string? UnusableReason { get; set; }
    }

    private static LayerInfo ReadLayer(string pdfPath, int pageIndex, int bitmapWidth, int bitmapHeight)
    {
        var info = new LayerInfo();
        using var document = PdfDocument.Open(pdfPath);
        var page = document.GetPage(pageIndex + 1);

        if (page.Rotation.Value % 360 != 0)
        {
            info.UnusableReason = "страница повёрнута";
            return info;
        }

        var crop = page.CropBox.Bounds;
        if (crop.Width <= 0 || crop.Height <= 0 || bitmapWidth <= 0 || bitmapHeight <= 0)
        {
            info.UnusableReason = "у страницы нулевой размер";
            return info;
        }

        var pageAspect = crop.Width / crop.Height;
        var bitmapAspect = (double)bitmapWidth / bitmapHeight;
        if (Math.Abs(pageAspect - bitmapAspect) / bitmapAspect > MaxAspectMismatch)
        {
            info.UnusableReason = "размеры страницы в PDF не совпадают с картинкой";
            return info;
        }

        var scaleX = bitmapWidth / crop.Width;
        var scaleY = bitmapHeight / crop.Height;

        foreach (var word in page.GetWords())
        {
            var text = word.Text.Trim();
            if (text.Length == 0) continue;

            var box = word.BoundingBox;
            var rect = new SKRectI(
                (int)Math.Floor((box.Left - crop.Left) * scaleX),
                (int)Math.Floor((crop.Top - box.Top) * scaleY),
                (int)Math.Ceiling((box.Right - crop.Left) * scaleX),
                (int)Math.Ceiling((crop.Top - box.Bottom) * scaleY));
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            if (info.Words.Any(w => w.Text == text && OverlapOfSmaller(w.PixelBounds, rect) > 0.8)) continue;

            info.Words.Add(new OcrWord(text, rect, 100f));
        }

        var imageArea = 0.0;
        foreach (var image in page.GetImages())
        {
            var b = image.BoundingBox;
            var w = Math.Max(0, Math.Min(b.Right, crop.Right) - Math.Max(b.Left, crop.Left));
            var h = Math.Max(0, Math.Min(b.Top, crop.Top) - Math.Max(b.Bottom, crop.Bottom));
            imageArea += w * h;
        }
        info.ImageFraction = Math.Min(1.0, imageArea / (crop.Width * crop.Height));

        return info;
    }

    private static double SuspiciousCharFraction(List<OcrWord> words)
    {
        var total = 0;
        var suspicious = 0;
        foreach (var ch in words.SelectMany(w => w.Text))
        {
            if (char.IsWhiteSpace(ch)) continue;
            total++;
            var category = char.GetUnicodeCategory(ch);
            if (ch == '�' || char.IsControl(ch) ||
                category == UnicodeCategory.PrivateUse ||
                category == UnicodeCategory.OtherNotAssigned ||
                category == UnicodeCategory.Surrogate ||
                (ch >= '' && ch <= '') ||
                (ch >= 'À' && ch <= 'ÿ'))
            {
                suspicious++;
            }
        }
        return total == 0 ? 1.0 : (double)suspicious / total;
    }

    private static List<OcrLine> GroupIntoLines(List<OcrWord> words)
    {
        var segments = new List<List<OcrWord>>();
        foreach (var word in words.OrderBy(w => w.PixelBounds.Left))
        {
            List<OcrWord>? best = null;
            var bestScore = double.MaxValue;

            foreach (var segment in segments)
            {
                var last = segment[^1];
                var height = Math.Max(last.PixelBounds.Height, word.PixelBounds.Height);
                var gap = word.PixelBounds.Left - last.PixelBounds.Right;
                if (gap < -0.5 * height || gap > MaxJoinGapToHeightRatio * height) continue;

                var verticalShift = Math.Abs(CenterY(word) - CenterY(last));
                var smallerHeight = Math.Min(last.PixelBounds.Height, word.PixelBounds.Height);
                var verticalOverlap = Math.Min(last.PixelBounds.Bottom, word.PixelBounds.Bottom) -
                    Math.Max(last.PixelBounds.Top, word.PixelBounds.Top);
                var sameLine = verticalShift <= 0.5 * smallerHeight || verticalOverlap >= 0.8 * smallerHeight;
                if (!sameLine) continue;

                var score = verticalShift * 10 + Math.Max(0, gap);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = segment;
                }
            }

            if (best is null) segments.Add(new List<OcrWord> { word });
            else best.Add(word);
        }

        return segments
            .SelectMany(SplitAtColumnGaps)
            .OrderBy(s => s.Min(w => w.PixelBounds.Top))
            .ThenBy(s => s[0].PixelBounds.Left)
            .Select(s => ToLine(s))
            .ToList();
    }

    private static IEnumerable<List<OcrWord>> SplitAtColumnGaps(List<OcrWord> segment)
    {
        if (segment.Count < 2)
        {
            yield return segment;
            yield break;
        }

        if (segment.Count == 2)
        {
            var pairGap = segment[1].PixelBounds.Left - segment[0].PixelBounds.Right;
            var pairHeight = Math.Max(segment[0].PixelBounds.Height, segment[1].PixelBounds.Height);
            if (pairGap > ColumnGapToHeightRatio * pairHeight)
            {
                yield return new List<OcrWord> { segment[0] };
                yield return new List<OcrWord> { segment[1] };
            }
            else
            {
                yield return segment;
            }
            yield break;
        }

        var gaps = new List<int>();
        for (var i = 1; i < segment.Count; i++)
        {
            gaps.Add(segment[i].PixelBounds.Left - segment[i - 1].PixelBounds.Right);
        }
        var median = gaps.OrderBy(g => g).ElementAt((gaps.Count - 1) / 2);

        var current = new List<OcrWord> { segment[0] };
        for (var i = 1; i < segment.Count; i++)
        {
            var height = Math.Max(segment[i].PixelBounds.Height, segment[i - 1].PixelBounds.Height);
            var gap = gaps[i - 1];
            if (gap > ColumnGapToHeightRatio * height && gap > ColumnGapToMedianGapRatio * Math.Max(1, median))
            {
                yield return current;
                current = new List<OcrWord>();
            }
            current.Add(segment[i]);
        }
        yield return current;
    }

    private static double LetterOrDigitFraction(List<OcrWord> words)
    {
        var total = 0;
        var lettersOrDigits = 0;
        foreach (var ch in words.SelectMany(w => w.Text))
        {
            if (char.IsWhiteSpace(ch) || FillerChars.Contains(ch)) continue;
            total++;
            if (char.IsLetterOrDigit(ch)) lettersOrDigits++;
        }
        return total == 0 ? 0 : (double)lettersOrDigits / total;
    }

    private static IEnumerable<OcrLine> UncoveredParts(OcrLine line, List<OcrWord> layerWords)
    {
        var segment = new List<OcrWord>();
        foreach (var word in line.Words)
        {
            var covered = layerWords.Any(w => OverlapOfSmaller(w.PixelBounds, word.PixelBounds) > CoveredByLayerFraction);
            if (!covered)
            {
                segment.Add(word);
                continue;
            }
            if (segment.Count > 0)
            {
                yield return ToLine(segment, line.Confidence);
                segment = new List<OcrWord>();
            }
        }
        if (segment.Count > 0) yield return ToLine(segment, line.Confidence);
    }

    private static OcrLine ToLine(List<OcrWord> words, float confidence = 100f)
    {
        var bounds = words
            .Select(w => w.PixelBounds)
            .Aggregate((a, b) => new SKRectI(
                Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));
        var text = string.Join(" ", words.Select(w => w.Text));
        return new OcrLine(text, bounds, confidence, words);
    }

    private static double CenterY(OcrWord word) => (word.PixelBounds.Top + word.PixelBounds.Bottom) / 2.0;

    private static double OverlapOfSmaller(SKRectI a, SKRectI b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (w <= 0 || h <= 0) return 0;
        var smaller = Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
        return smaller <= 0 ? 0 : (double)w * h / smaller;
    }
}
