using System;
using System.Collections.Generic;
using System.Linq;
using ConfidentialDataMasker.Models;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed partial class ConfidentialDataDetector
{
    private const string IllegibleLabel = "Неразборчивый текст?";
    private const float IllegibleWordMaxConfidence = 60f;
    private const float UncertainNeighbourMaxConfidence = 80f;
    private const int IllegibleMinLettersOrDigits = 2;
    private const double IllegibleMinWidthToHeight = 0.6;
    private const double VerticalRuleLineMinHeightToMedian = 4.0;
    private const double VerticalRuleLineMinHeightToWidth = 4.0;
    private const float PrintedShortWordMinConfidence = 50f;
    private const int PrintedShortWordMaxChars = 3;
    private const float PrintedReferenceMinConfidence = 90f;
    private const int PrintedReferenceMinLetters = 3;
    private const int PrintedReferenceMinWords = 3;
    private const double PrintedShortWordMinHeightRatio = 0.8;
    private const double PrintedShortWordMaxHeightRatio = 1.25;
    private const double PrintedShortWordMaxCenterShift = 0.35;

    private static void AddIllegibleText(
        SKBitmap bitmap, List<OcrLine> ocrLines, List<OcrLine> lines, List<SuggestedRect> suggestions)
    {
        var ruleWords = WordsOnVerticalRules(ocrLines);
        foreach (var line in lines)
        {
            var words = line.Words.Where(w => !ruleWords.Contains(w)).ToList();
            var flagged = words.Select(IsIllegibleWord).ToArray();
            if (!flagged.Any(f => f)) continue;

            IncludeUncertainNeighbours(words, flagged);
            FillSingleWordGaps(flagged);

            var start = 0;
            while (start < words.Count)
            {
                if (!flagged[start])
                {
                    start++;
                    continue;
                }

                var end = start;
                while (end + 1 < words.Count && flagged[end + 1]) end++;

                var run = words.Skip(start).Take(end - start + 1).ToList();
                if (run.Count == 1 && LooksLikePrintedShortWord(run[0], words))
                {
                    start = end + 1;
                    continue;
                }
                suggestions.Add(SuggestionFactory.Create(
                    bitmap, run.Select(w => w.PixelBounds).Aggregate(RectMath.Union), SuggestionKind.Text,
                    IllegibleLabel, string.Join(" ", run.Select(w => w.Text)), line.Text));
                start = end + 1;
            }
        }
    }

    private static bool IsIllegibleWord(OcrWord word) =>
        word.Confidence < IllegibleWordMaxConfidence &&
        word.Text.Count(char.IsLetterOrDigit) >= IllegibleMinLettersOrDigits &&
        word.PixelBounds.Width >= IllegibleMinWidthToHeight * word.PixelBounds.Height &&
        !OcrWordText.LooksLikeRealWord(word.Text) &&
        !OcrWordText.LooksLikeAbbreviation(word.Text) &&
        !OcrWordText.IsGluedRealWords(word.Text) &&
        !OcrWordText.IsWordGluedToDigits(word.Text);

    private static HashSet<OcrWord> WordsOnVerticalRules(List<OcrLine> lines)
    {
        var result = new HashSet<OcrWord>();
        var heights = lines
            .SelectMany(l => l.Words)
            .Where(w => w.Text.Any(char.IsLetterOrDigit))
            .Select(w => w.PixelBounds.Height)
            .OrderBy(h => h)
            .ToList();
        if (heights.Count == 0) return result;
        var median = heights[heights.Count / 2];
        if (median <= 0) return result;

        foreach (var line in lines)
        {
            if (line.Words.Count != 1) continue;
            var bounds = line.PixelBounds;
            if (bounds.Height < VerticalRuleLineMinHeightToMedian * median) continue;
            if (bounds.Width * VerticalRuleLineMinHeightToWidth > bounds.Height) continue;
            foreach (var word in line.Words) result.Add(word);
        }
        return result;
    }

    private static bool LooksLikePrintedShortWord(OcrWord word, List<OcrWord> lineWords)
    {
        if (word.Confidence < PrintedShortWordMinConfidence) return false;
        if (word.Text.Count(char.IsLetterOrDigit) > PrintedShortWordMaxChars) return false;

        var reference = lineWords
            .Where(w => !ReferenceEquals(w, word) &&
                w.Confidence >= PrintedReferenceMinConfidence &&
                w.Text.Count(char.IsLetter) >= PrintedReferenceMinLetters)
            .ToList();
        if (reference.Count < PrintedReferenceMinWords) return false;

        var heights = reference.Select(w => w.PixelBounds.Height).OrderBy(h => h).ToList();
        var height = heights[heights.Count / 2];
        var bounds = word.PixelBounds;
        var nearest = reference.MinBy(w => Math.Max(Math.Max(w.PixelBounds.Left - bounds.Right, bounds.Left - w.PixelBounds.Right), 0))!;
        var nearestCenter = (nearest.PixelBounds.Top + nearest.PixelBounds.Bottom) / 2.0;
        var center = (bounds.Top + bounds.Bottom) / 2.0;
        return bounds.Height >= PrintedShortWordMinHeightRatio * height &&
            bounds.Height <= PrintedShortWordMaxHeightRatio * height &&
            Math.Abs(center - nearestCenter) <= PrintedShortWordMaxCenterShift * height;
    }

    private static void IncludeUncertainNeighbours(List<OcrWord> words, bool[] flagged)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < words.Count; i++)
            {
                if (flagged[i] || words[i].Confidence >= UncertainNeighbourMaxConfidence) continue;
                if (words[i].Text is "/" or "|") continue;
                var nextToFlagged = (i > 0 && flagged[i - 1]) || (i + 1 < words.Count && flagged[i + 1]);
                if (!nextToFlagged) continue;
                flagged[i] = true;
                changed = true;
            }
        }
    }

    private static void FillSingleWordGaps(bool[] flagged)
    {
        for (var i = 1; i + 1 < flagged.Length; i++)
        {
            if (!flagged[i] && flagged[i - 1] && flagged[i + 1]) flagged[i] = true;
        }
    }
}
