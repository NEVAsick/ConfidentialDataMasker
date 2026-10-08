using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ConfidentialDataMasker.Models;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed partial class ConfidentialDataDetector
{
    private static readonly Regex ESignAnchorPattern =
        new(@"электронн[а-яё]*\s+подпис|^\s*(?:документ\s+)?подписан[а-яё]*[.:]?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ESignFieldPattern =
        new(@"^\s*(?:сертификат|владелец|действител[а-яё]*|издатель|серийный\s+номер|отпечаток)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int ESignMaxAnchorWords = 8;
    private const int ESignMaxLines = 12;
    private const double ESignMaxGapToHeight = 2.5;
    private const int ESignMaxWordsPerLine = 12;
    private const double ESignPaddingToHeight = 0.8;

    private static void AddElectronicSignatureBlocks(SKBitmap bitmap, List<OcrLine> lines, List<SuggestedRect> suggestions)
    {
        var used = new HashSet<OcrLine>();
        foreach (var anchor in lines)
        {
            if (used.Contains(anchor) || anchor.Words.Count > ESignMaxAnchorWords) continue;
            if (!ESignAnchorPattern.IsMatch(NormalizeHomoglyphs(anchor.Text))) continue;

            var block = new List<OcrLine> { anchor };
            var bounds = anchor.PixelBounds;
            var height = Math.Max(1, anchor.PixelBounds.Height);

            var grown = true;
            while (grown && block.Count < ESignMaxLines)
            {
                grown = false;
                foreach (var candidate in lines)
                {
                    if (block.Count >= ESignMaxLines) break;
                    if (block.Contains(candidate) || used.Contains(candidate) || candidate.Words.Count > ESignMaxWordsPerLine) continue;
                    var gap = candidate.PixelBounds.Top - bounds.Bottom;
                    if (gap < -height || gap > ESignMaxGapToHeight * height) continue;
                    if (candidate.PixelBounds.Right <= bounds.Left || candidate.PixelBounds.Left >= bounds.Right) continue;
                    block.Add(candidate);
                    bounds = RectMath.Union(bounds, candidate.PixelBounds);
                    grown = true;
                }
            }

            if (!block.Any(l => ESignFieldPattern.IsMatch(NormalizeHomoglyphs(l.Text)))) continue;
            foreach (var line in block) used.Add(line);

            var pad = (int)Math.Round(ESignPaddingToHeight * height);
            var padded = new SKRectI(
                Math.Max(0, bounds.Left - pad), Math.Max(0, bounds.Top - pad),
                Math.Min(bitmap.Width, bounds.Right + pad), Math.Min(bitmap.Height, bounds.Bottom + pad));
            suggestions.Add(SuggestionFactory.Create(
                bitmap, padded, SuggestionKind.Text, "Электронная подпись?",
                string.Join(" / ", block.Select(l => l.Text)), anchor.Text));
        }
    }
}
