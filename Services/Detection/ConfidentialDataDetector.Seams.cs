using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ConfidentialDataMasker.Models;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed partial class ConfidentialDataDetector
{
    private const int SeamWords = 10;
    private const int SeamMiddleWords = 20;
    private const int MinSeamPartChars = 2;
    private const double ColumnGapToHeight = 1.0;
    private const double ColumnGapToMedianGap = 2.5;
    private const double WideColumnGapToHeight = 4.0;
    private const double CellAlignToHeight = 0.5;

    private static readonly Regex ListNumberPattern = new(@"^\d{1,3}[.)]$", RegexOptions.Compiled);

    private static readonly Regex TableBarPattern = new(@"^[|\[\]{}!]+$", RegexOptions.Compiled);

    private static readonly Regex PatronymicLikePattern =
        new(@"(?:ович|евич|ич|овн|евн|ичн)(?:а|у|ем|е|ы|ой|ою)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void AddSeamSuggestions(
        SKBitmap bitmap, List<OcrLine> lines, IReadOnlyList<(Regex Pattern, string Label)> dictionaryPatterns,
        List<SuggestedRect> suggestions)
    {
        foreach (var line in lines)
        {
            if (line.Words.Count == 0) continue;
            if (FindColumnStarts(line).Count > 1) continue;
            var next = FindParagraphContinuation(line, lines);
            if (next is null || next.Words.Count == 0) continue;
            var afterNext = FindParagraphContinuation(next, lines);

            var parts = new List<(OcrLine Line, List<OcrWord> Words)>
            {
                (line, line.Words.Skip(Math.Max(0, line.Words.Count - SeamWords)).ToList()),
            };
            if (afterNext is not null && afterNext.Words.Count > 0 && next.Words.Count <= SeamMiddleWords)
            {
                parts.Add((next, next.Words));
                parts.Add((afterNext, afterNext.Words.Take(SeamWords).ToList()));
            }
            else
            {
                parts.Add((next, next.Words.Take(SeamWords).ToList()));
            }

            AddSeamMatches(bitmap, parts, dictionaryPatterns, suggestions);
        }
    }

    private static void AddCellSeamSuggestions(
        SKBitmap bitmap, List<OcrLine> lines, IReadOnlyList<(Regex Pattern, string Label)> dictionaryPatterns,
        List<SuggestedRect> suggestions)
    {
        var columnStarts = new Dictionary<OcrLine, List<int>>();
        foreach (var line in lines)
        {
            if (line.Words.Count > 0) columnStarts[line] = FindColumnStarts(line);
        }

        foreach (var line in lines)
        {
            if (line.Words.Count == 0) continue;
            var height = OcrLineRepair.MedianHeight(line.Words);
            var starts = columnStarts[line];
            for (var k = 0; k < starts.Count; k++)
            {
                var cellEnd = k + 1 < starts.Count ? starts[k + 1] : line.Words.Count;
                var cell = line.Words.Skip(starts[k]).Take(cellEnd - starts[k]).Where(w => !IsTableBar(w)).ToList();
                if (cell.Count == 0) continue;
                var cellLeft = cell[0].PixelBounds.Left;
                var tail = cell.Skip(Math.Max(0, cell.Count - SeamWords)).ToList();

                var next = FindCellContinuation(lines, columnStarts, cellLeft, WordBounds(tail), height, line, null);
                if (next is null) continue;
                var (nextLine, nextStart) = next.Value;
                var head = CellWords(nextLine, nextStart, columnStarts[nextLine]);
                if (head.Count == 0) continue;

                var afterNext = head.Count <= SeamMiddleWords
                    ? FindCellContinuation(lines, columnStarts, cellLeft, WordBounds(head), height, line, nextLine)
                    : null;
                var afterHead = afterNext is { } found
                    ? CellWords(found.Line, found.Start, columnStarts[found.Line])
                    : new List<OcrWord>();

                var parts = new List<(OcrLine Line, List<OcrWord> Words)> { (line, tail) };
                if (afterNext is { } third && afterHead.Count > 0)
                {
                    parts.Add((nextLine, head));
                    parts.Add((third.Line, afterHead.Take(SeamWords).ToList()));
                }
                else
                {
                    parts.Add((nextLine, head.Take(SeamWords).ToList()));
                }
                AddSeamMatches(bitmap, parts, dictionaryPatterns, suggestions);
            }
        }
    }

    private static (OcrLine Line, int Start)? FindCellContinuation(
        List<OcrLine> lines, Dictionary<OcrLine, List<int>> columnStarts, int cellLeft, SKRectI above, int height,
        OcrLine first, OcrLine? second)
    {
        (OcrLine Line, int Start)? best = null;
        var bestGap = int.MaxValue;
        foreach (var candidate in lines)
        {
            if (ReferenceEquals(candidate, first) || ReferenceEquals(candidate, second) || candidate.Words.Count == 0) continue;
            foreach (var start in columnStarts[candidate])
            {
                var word = candidate.Words[start];
                var gap = word.PixelBounds.Top - above.Bottom;
                if (gap < -height / 4 || gap > height) continue;
                if (Math.Abs(word.PixelBounds.Left - cellLeft) > CellAlignToHeight * height) continue;
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = (candidate, start);
                }
            }
        }
        return best;
    }

    private static List<OcrWord> CellWords(OcrLine line, int start, List<int> starts)
    {
        var end = starts.Where(s => s > start).DefaultIfEmpty(line.Words.Count).First();
        return line.Words.Skip(start).Take(end - start).Where(w => !IsTableBar(w)).ToList();
    }

    private static SKRectI WordBounds(List<OcrWord> words) => words.Select(w => w.PixelBounds).Aggregate(RectMath.Union);

    private static bool IsTableBar(OcrWord word) => TableBarPattern.IsMatch(word.Text);

    private static List<int> FindColumnStarts(OcrLine line)
    {
        var words = line.Words;
        var height = OcrLineRepair.MedianHeight(words);
        var positiveGaps = new List<int>();
        for (var i = 1; i < words.Count; i++)
        {
            var gap = words[i].PixelBounds.Left - words[i - 1].PixelBounds.Right;
            if (gap > 0) positiveGaps.Add(gap);
        }
        positiveGaps.Sort();
        var medianGap = positiveGaps.Count == 0 ? 0 : positiveGaps[(positiveGaps.Count - 1) / 2];

        var isBar = words.Select(IsTableBar).ToArray();
        var starts = new SortedSet<int> { 0 };
        for (var i = 1; i < words.Count; i++)
        {
            if (isBar[i - 1] && !isBar[i])
            {
                starts.Add(i);
                continue;
            }
            var gap = words[i].PixelBounds.Left - words[i - 1].PixelBounds.Right;
            if (gap <= ColumnGapToHeight * height) continue;
            if (gap > ColumnGapToMedianGap * medianGap || gap > WideColumnGapToHeight * height) starts.Add(i);
        }

        var result = starts.Where(i => !isBar[i]).ToList();
        if (result.Count == 0) result.Add(0);
        if (result[0] != 0 && !isBar.Take(result[0]).All(b => b)) result.Insert(0, 0);
        return result;
    }

    private static void AddSeamMatches(
        SKBitmap bitmap, List<(OcrLine Line, List<OcrWord> Words)> parts,
        IReadOnlyList<(Regex Pattern, string Label)> dictionaryPatterns, List<SuggestedRect> suggestions)
    {
        var words = parts.SelectMany(p => p.Words).ToList();
        var owner = parts.SelectMany((p, index) => p.Words.Select(_ => index)).ToList();
        var text = string.Join(" ", words.Select(w => w.Text));
        var seamStart = parts[0].Words.Sum(w => w.Text.Length + 1);
        var bounds = parts.Select(p => p.Line.PixelBounds).Aggregate(RectMath.Union);
        var seam = new OcrLine(text, bounds, parts.Min(p => p.Line.Confidence), words);

        foreach (var (label, match) in ClassifyAll(seam, dictionaryPatterns, includeFullFields: false))
        {
            if (FullFieldLabels.Contains(label)) continue;
            if (match.Index >= seamStart - 1 || match.Index + match.Length <= seamStart) continue;
            if (label == NameLabel)
            {
                if (match.Groups["secret"].Success) continue;
                var nameWords = match.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (nameWords.Length < 3 && !PatronymicLikePattern.IsMatch(nameWords[^1])) continue;
            }

            var target = GetRedactionTarget(match);
            var partBounds = new SKRectI?[parts.Count];
            var partChars = new int[parts.Count];
            var partIsListNumber = Enumerable.Repeat(true, parts.Count).ToArray();
            var offset = 0;
            for (var i = 0; i < words.Count; i++)
            {
                var wordStart = offset;
                var wordEnd = offset + words[i].Text.Length;
                offset = wordEnd + 1;
                if (wordEnd <= target.Index || wordStart >= target.Index + target.Length) continue;

                var from = Math.Max(wordStart, target.Index) - wordStart;
                var to = Math.Min(wordEnd, target.Index + target.Length) - wordStart;
                partChars[owner[i]] += words[i].Text.Substring(from, to - from).Count(char.IsLetterOrDigit);
                if (!ListNumberPattern.IsMatch(words[i].Text)) partIsListNumber[owner[i]] = false;
                var wordBounds = words[i].PixelBounds;
                partBounds[owner[i]] = partBounds[owner[i]] is null ? wordBounds : RectMath.Union(partBounds[owner[i]]!.Value, wordBounds);
            }

            for (var k = 0; k < parts.Count; k++)
            {
                if (partBounds[k] is null) continue;
                if (k > 0 && (partChars[k] < MinSeamPartChars || partIsListNumber[k])) continue;
                suggestions.Add(SuggestionFactory.Create(
                    bitmap, partBounds[k]!.Value, SuggestionKind.Text, label, match.Value, parts[k].Line.Text));
            }
        }
    }
}
