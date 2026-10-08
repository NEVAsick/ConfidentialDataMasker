using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ConfidentialDataMasker.Models;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed partial class ConfidentialDataDetector
{
    private const double ColumnSideToleranceToHeight = 0.5;
    private const double MaxColumnRowGapToHeight = 8.0;
    private const double HeaderSecondLineGapToHeight = 1.2;
    private const int MinColumnValueDigits = 4;

    private static readonly Regex TabNumberValue = new(@"^\d{3,10}$", RegexOptions.Compiled);
    private static readonly Regex DateValue = new(@"^\d{1,2}[./]\d{1,2}[./]\d{2,4}$", RegexOptions.Compiled);
    private static readonly Regex PhoneValue = new(@"^(?!\d{1,2}\.\d{1,2}\.\d{2,4}$)\+?\(?\d[\d\-–().]*$", RegexOptions.Compiled);
    private static readonly Regex DigitsWithDashesValue = new(@"^\d[\d\-–]*$", RegexOptions.Compiled);

    private static readonly (Regex First, Regex? Second, Regex Value, string Label)[] ColumnHeaders =
    {
        (new Regex(@"^Табельн[а-яё]*$", RegexOptions.Compiled), new Regex(@"^(?:номер[а-яё]*|№)$", RegexOptions.Compiled | RegexOptions.IgnoreCase), TabNumberValue, "Табельный номер?"),
        (new Regex(@"^Таб\.$", RegexOptions.Compiled), new Regex(@"^№$", RegexOptions.Compiled), TabNumberValue, "Табельный номер?"),
        (new Regex(@"^Дата$", RegexOptions.Compiled), new Regex(@"^рождения$", RegexOptions.Compiled | RegexOptions.IgnoreCase), DateValue, "Дата рождения?"),
        (new Regex(@"^Телефон[а-яё]*$", RegexOptions.Compiled), null, PhoneValue, "Телефон?"),
        (new Regex(@"^СНИЛС$", RegexOptions.Compiled), null, DigitsWithDashesValue, "СНИЛС?"),
        (new Regex(@"^ИНН$", RegexOptions.Compiled), null, DigitsWithDashesValue, "ИНН?"),
    };

    private static readonly char[] ColumnValueTrimChars = { '|', '[', ']', '{', '}', ',', ';', ':', '.', '"', '\'' };
    private static readonly char[] ColumnHeaderTrimChars = { '\'', '"', '|', '[', ']', '{', '}', '«', '»' };

    private static void AddTableColumnValues(SKBitmap bitmap, List<OcrLine> lines, List<SuggestedRect> suggestions)
    {
        var words = lines.SelectMany(l => l.Words.Select(w => (Word: w, Line: l))).ToList();

        foreach (var (headerWords, valuePattern, label) in FindColumnHeaders(lines, words))
        {
            foreach (var (bounds, text) in CollectColumnValues(headerWords, valuePattern, words))
            {
                suggestions.Add(SuggestionFactory.Create(
                    bitmap, bounds, SuggestionKind.Text, label, text, string.Join(" ", headerWords.Select(w => w.Text))));
            }
        }
    }

    private static IEnumerable<(List<OcrWord> Words, Regex Value, string Label)> FindColumnHeaders(
        List<OcrLine> lines, List<(OcrWord Word, OcrLine Line)> words)
    {
        foreach (var line in lines)
        {
            for (var i = 0; i < line.Words.Count; i++)
            {
                var first = line.Words[i];
                var firstText = HeaderWordText(first);
                foreach (var (firstPattern, secondPattern, valuePattern, label) in ColumnHeaders)
                {
                    if (!firstPattern.IsMatch(firstText)) continue;

                    var header = new List<OcrWord> { first };
                    var lastIndexOnLine = i;
                    if (secondPattern is not null)
                    {
                        if (i + 1 < line.Words.Count && secondPattern.IsMatch(HeaderWordText(line.Words[i + 1])))
                        {
                            header.Add(line.Words[i + 1]);
                            lastIndexOnLine = i + 1;
                        }
                        else
                        {
                            var below = FindWordDirectlyBelow(first, words, secondPattern);
                            if (below is null) continue;
                            header.Add(below);
                        }
                    }

                    var nextOnLine = lastIndexOnLine + 1 < line.Words.Count ? line.Words[lastIndexOnLine + 1] : null;
                    var inlineValue = nextOnLine is not null && nextOnLine.Text.Any(char.IsDigit) &&
                        IsOnSameRow(header[^1], nextOnLine);
                    if (header[^1].Text.EndsWith(':') || inlineValue) continue;

                    yield return (header, valuePattern, label);
                }
            }
        }
    }

    private static string HeaderWordText(OcrWord word) => NormalizeHomoglyphs(word.Text).Trim(ColumnHeaderTrimChars);

    private static bool IsOnSameRow(OcrWord a, OcrWord b)
    {
        var height = Math.Max(1, Math.Min(a.PixelBounds.Height, b.PixelBounds.Height));
        var centerA = (a.PixelBounds.Top + a.PixelBounds.Bottom) / 2.0;
        var centerB = (b.PixelBounds.Top + b.PixelBounds.Bottom) / 2.0;
        return Math.Abs(centerA - centerB) <= height / 2.0;
    }

    private static OcrWord? FindWordDirectlyBelow(OcrWord anchor, List<(OcrWord Word, OcrLine Line)> words, Regex pattern)
    {
        var height = Math.Max(1, anchor.PixelBounds.Height);
        foreach (var (word, _) in words)
        {
            if (ReferenceEquals(word, anchor)) continue;
            var gap = word.PixelBounds.Top - anchor.PixelBounds.Bottom;
            if (gap < -height / 4 || gap > HeaderSecondLineGapToHeight * height) continue;
            if (Math.Abs(word.PixelBounds.Left - anchor.PixelBounds.Left) > height) continue;
            if (pattern.IsMatch(HeaderWordText(word))) return word;
        }
        return null;
    }

    private static List<(SKRectI Bounds, string Text)> CollectColumnValues(
        List<OcrWord> header, Regex valuePattern, List<(OcrWord Word, OcrLine Line)> words)
    {
        var headerBounds = header.Select(w => w.PixelBounds).Aggregate(RectMath.Union);
        var height = Math.Max(1, header[0].PixelBounds.Height);
        var tolerance = (int)Math.Round(ColumnSideToleranceToHeight * height);
        var columnLeft = headerBounds.Left - tolerance;
        var columnRight = headerBounds.Right + tolerance;

        var below = words
            .Where(x => !header.Contains(x.Word))
            .Where(x => x.Word.PixelBounds.Top >= headerBounds.Bottom - height / 4)
            .Where(x => x.Word.PixelBounds.Left < columnRight && x.Word.PixelBounds.Right > columnLeft)
            .OrderBy(x => x.Word.PixelBounds.Top)
            .ToList();

        var rows = new List<(OcrLine Line, SKRectI Bounds, List<string> Texts)>();
        var lastBottom = headerBounds.Bottom;
        foreach (var (word, line) in below)
        {
            if (word.PixelBounds.Top - lastBottom > MaxColumnRowGapToHeight * height) break;
            var value = word.Text.Trim(ColumnValueTrimChars);
            if (!value.Any(char.IsLetterOrDigit)) continue;
            if (!valuePattern.IsMatch(value)) break;

            lastBottom = Math.Max(lastBottom, word.PixelBounds.Bottom);
            var row = rows.FindIndex(r => ReferenceEquals(r.Line, line));
            if (row < 0)
            {
                rows.Add((line, word.PixelBounds, new List<string> { value }));
            }
            else
            {
                rows[row].Texts.Add(value);
                rows[row] = (line, RectMath.Union(rows[row].Bounds, word.PixelBounds), rows[row].Texts);
            }
        }

        return rows
            .Where(r => r.Texts.Sum(t => t.Count(char.IsDigit)) >= MinColumnValueDigits)
            .Select(r => (r.Bounds, string.Join(" ", r.Texts)))
            .ToList();
    }
}
