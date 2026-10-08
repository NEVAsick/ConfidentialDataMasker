using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ConfidentialDataMasker.Models;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed partial class ConfidentialDataDetector
{
    private const double NestedCoverage = 0.9;

    private static readonly IReadOnlyList<(Regex Pattern, string Label)> NoDictionaryPatterns =
        Array.Empty<(Regex Pattern, string Label)>();

    private readonly RedactionDictionary? _dictionary;
    private readonly StampAndSignatureDetector _stampAndSignatureDetector;

    public ConfidentialDataDetector(DetectorThresholds? thresholds = null, RedactionDictionary? dictionary = null)
    {
        _dictionary = dictionary;
        _stampAndSignatureDetector = new StampAndSignatureDetector(thresholds ?? new DetectorThresholds());
    }

    public List<SuggestedRect> Detect(SKBitmap bitmap, List<OcrLine> lines)
    {
        var suggestions = new List<SuggestedRect>();
        var dictionaryPatterns = _dictionary?.GetPatterns() ?? NoDictionaryPatterns;
        var textLines = OcrLineRepair.Repair(lines);

        foreach (var line in textLines)
        {
            if (TryAddStandaloneLabelValue(bitmap, line, textLines, suggestions)) continue;
            AddLineMatches(bitmap, line, textLines, dictionaryPatterns, suggestions);
        }

        AddTableColumnValues(bitmap, textLines, suggestions);
        AddIllegibleText(bitmap, lines, textLines, suggestions);
        AddSeamSuggestions(bitmap, textLines, dictionaryPatterns, suggestions);
        AddCellSeamSuggestions(bitmap, textLines, dictionaryPatterns, suggestions);
        AddElectronicSignatureBlocks(bitmap, textLines, suggestions);
        suggestions = RemoveNestedSuggestions(suggestions);
        suggestions.AddRange(_stampAndSignatureDetector.Detect(bitmap, lines));

        return suggestions;
    }

    private static bool TryAddStandaloneLabelValue(
        SKBitmap bitmap, OcrLine line, List<OcrLine> lines, List<SuggestedRect> suggestions)
    {
        var label = GetStandaloneLabel(line.Text);
        if (label is null || HasLeftNeighbor(line, lines)) return false;

        var valueLine = FindFieldValueLine(line, lines);
        if (valueLine is null) return false;

        suggestions.Add(SuggestionFactory.Create(
            bitmap, valueLine.PixelBounds, SuggestionKind.Text, label, valueLine.Text, line.Text));
        return true;
    }

    private static void AddLineMatches(
        SKBitmap bitmap, OcrLine line, List<OcrLine> lines,
        IReadOnlyList<(Regex Pattern, string Label)> dictionaryPatterns, List<SuggestedRect> suggestions)
    {
        foreach (var (label, match) in ClassifyAll(line, dictionaryPatterns))
        {
            var isFullField = FullFieldLabels.Contains(label);
            if (isFullField && !HasValue(match.Groups["secret"]))
            {
                var valueLine = FindFieldValueLine(line, lines);
                if (valueLine is not null)
                {
                    suggestions.Add(SuggestionFactory.Create(
                        bitmap, valueLine.PixelBounds, SuggestionKind.Text, label, valueLine.Text, line.Text));
                }
                continue;
            }

            var matchBounds = GetMatchPixelBounds(line, match) ?? line.PixelBounds;
            if (label == NameLabel)
            {
                matchBounds = ExtendNameBoundsToTrailingWords(line, match, matchBounds);
            }
            suggestions.Add(SuggestionFactory.Create(
                bitmap, matchBounds, SuggestionKind.Text, label, match.Value, line.Text));

            if (isFullField)
            {
                AddWrappedFieldValue(bitmap, line, lines, label, suggestions);
            }
            else if (label == NameLabel)
            {
                AddWrappedNameParts(bitmap, line, match, lines, suggestions);
            }
        }
    }

    private static OcrLine? FindFieldValueLine(OcrLine labelLine, List<OcrLine> lines)
    {
        var right = FindLineToTheRight(labelLine, lines);
        if (right is not null && GetStandaloneLabel(right.Text) is null) return right;
        return FindLineDirectlyBelow(labelLine, lines, LooksLikeWrappedFieldValue, LabelValueMaxGapToHeight);
    }

    private static void AddWrappedFieldValue(
        SKBitmap bitmap, OcrLine line, List<OcrLine> lines, string label, List<SuggestedRect> suggestions)
    {
        var wrappedValue = FindLineDirectlyBelow(line, lines, LooksLikeWrappedFieldValue);
        if (wrappedValue is null) return;

        suggestions.Add(SuggestionFactory.Create(
            bitmap, wrappedValue.PixelBounds, SuggestionKind.Text, label, wrappedValue.Text, line.Text));
    }

    private static void AddWrappedNameParts(
        SKBitmap bitmap, OcrLine line, Match match, List<OcrLine> lines, List<SuggestedRect> suggestions)
    {
        var nameWords = match.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (nameWords.Length != 2) return;

        var wrappedPatronymic = FindLineDirectlyBelow(line, lines, LooksLikeWrappedPatronymic);
        if (wrappedPatronymic is not null)
        {
            suggestions.Add(SuggestionFactory.Create(
                bitmap, wrappedPatronymic.PixelBounds, SuggestionKind.Text, NameLabel,
                wrappedPatronymic.Text, line.Text));
        }

        var surname = FindSurnameOnLineAbove(line, match, nameWords, lines);
        if (surname is not null)
        {
            suggestions.Add(SuggestionFactory.Create(
                bitmap, surname.Value.Word.PixelBounds, SuggestionKind.Text, NameLabel,
                surname.Value.Word.Text, surname.Value.Line.Text));
        }
    }

    private static List<SuggestedRect> RemoveNestedSuggestions(List<SuggestedRect> suggestions)
    {
        var result = new List<SuggestedRect>();
        for (var i = 0; i < suggestions.Count; i++)
        {
            var inner = suggestions[i];
            var innerArea = inner.Width * inner.Height;
            var nested = false;

            for (var j = 0; j < suggestions.Count && !nested; j++)
            {
                if (i == j) continue;
                var outer = suggestions[j];
                var outerArea = outer.Width * outer.Height;
                if (outerArea < innerArea) continue;
                if (outerArea == innerArea && j > i) continue;
                nested = IntersectionArea(inner, outer) >= NestedCoverage * innerArea;
            }

            if (!nested) result.Add(inner);
        }
        return result;
    }

    private static double IntersectionArea(SuggestedRect a, SuggestedRect b)
    {
        var width = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var height = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }
}
