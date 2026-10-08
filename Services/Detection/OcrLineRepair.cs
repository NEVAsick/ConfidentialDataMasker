using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

internal static class OcrLineRepair
{
    private const double SameRowToleranceToHeight = 0.6;
    private const double FragmentMergeGapToHeight = 1.5;
    private const double RowNeighbourGapToHeight = 1.0;
    private const double RowNeighbourGapToMedianGap = 2.5;
    private const double RowNeighbourMaxGapToHeight = 4.0;
    private const double WordBelowMinShiftToHeight = 0.5;
    private const double WordBelowMaxShiftToHeight = 2.0;
    private const double WordBelowLeftToleranceToHeight = 1.0;

    private static readonly Regex MisreadCapitalDePattern = new(@"^J\](?=[.,]?$)", RegexOptions.Compiled);
    private static readonly Regex GluedNamePattern =
        new(@"^(?<first>[А-ЯЁ][а-яё]{2,})(?<second>[А-ЯЁ][а-яё]{2,}[,.;]?)$", RegexOptions.Compiled);
    private static readonly Regex PatronymicWordPattern =
        new(@"^[А-ЯЁ][а-яё]+(?:ович|евич|инич|овна|евна|ична)[,.;]?$", RegexOptions.Compiled);

    private sealed class Piece
    {
        public List<OcrWord> Words = new();
        public float Confidence;
        public OcrLine? Original;
        public bool IsFragment;
        public bool Merged;
        public bool Removed;
    }

    public static List<OcrLine> Repair(List<OcrLine> lines)
    {
        lines = FixWords(lines);
        var pieces = new List<Piece>();
        foreach (var line in lines)
        {
            if (line.Confidence >= OcrWordText.TextLayerConfidence || line.Words.Count < 2)
            {
                pieces.Add(new Piece { Words = line.Words.ToList(), Confidence = line.Confidence, Original = line });
                continue;
            }

            var rows = SplitIntoRows(line);
            if (rows.Count == 1)
            {
                pieces.Add(new Piece { Words = line.Words.ToList(), Confidence = line.Confidence, Original = line });
                continue;
            }

            foreach (var row in rows.OrderBy(r => CenterY(r[0])))
            {
                pieces.Add(new Piece { Words = row, Confidence = line.Confidence, IsFragment = true });
            }
        }

        MergeFragments(pieces);
        MergeInterleavedLines(pieces);
        MergeRowNeighbours(pieces);

        var result = new List<OcrLine>();
        foreach (var piece in pieces)
        {
            if (piece.Removed) continue;
            if (piece.Original is not null && !piece.Merged)
            {
                result.Add(WithWordBounds(piece.Original));
                continue;
            }

            result.Add(new OcrLine(
                string.Join(" ", piece.Words.Select(w => w.Text)),
                piece.Words.Select(w => w.PixelBounds).Aggregate(RectMath.Union),
                piece.Confidence,
                piece.Words));
        }
        return result;
    }

    private static List<OcrLine> FixWords(List<OcrLine> lines)
    {
        var result = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            if (line.Confidence >= OcrWordText.TextLayerConfidence)
            {
                result.Add(line);
                continue;
            }

            List<OcrWord>? fixedWords = null;
            for (var i = 0; i < line.Words.Count; i++)
            {
                var word = line.Words[i];
                var next = i + 1 < line.Words.Count ? line.Words[i + 1] : null;
                var replacement = FixWord(word, next, lines);
                if (replacement is null)
                {
                    fixedWords?.Add(word);
                    continue;
                }

                fixedWords ??= line.Words.Take(i).ToList();
                fixedWords.AddRange(replacement);
            }

            result.Add(fixedWords is null
                ? line
                : new OcrLine(string.Join(" ", fixedWords.Select(w => w.Text)), line.PixelBounds, line.Confidence, fixedWords));
        }
        return result;
    }

    private static List<OcrWord>? FixWord(OcrWord word, OcrWord? next, List<OcrLine> lines)
    {
        if (MisreadCapitalDePattern.IsMatch(word.Text))
        {
            return new List<OcrWord>
            {
                new(MisreadCapitalDePattern.Replace(word.Text, "Д"), word.PixelBounds, word.Confidence)
            };
        }

        var glued = GluedNamePattern.Match(word.Text);
        if (!glued.Success) return null;
        var patronymicFollows = (next is not null && PatronymicWordPattern.IsMatch(next.Text)) ||
            PatronymicWordPattern.IsMatch(WordBelow(word, lines)?.Text ?? "");
        if (!patronymicFollows) return null;

        var first = glued.Groups["first"].Value;
        var bounds = word.PixelBounds;
        var split = bounds.Left + (int)Math.Round((double)bounds.Width * first.Length / word.Text.Length);
        return new List<OcrWord>
        {
            new(first, new SKRectI(bounds.Left, bounds.Top, split, bounds.Bottom), word.Confidence),
            new(glued.Groups["second"].Value, new SKRectI(split, bounds.Top, bounds.Right, bounds.Bottom), word.Confidence)
        };
    }

    private static OcrWord? WordBelow(OcrWord word, List<OcrLine> lines)
    {
        var height = Math.Max(1, word.PixelBounds.Height);
        OcrWord? best = null;
        var bestShift = double.MaxValue;
        foreach (var candidate in lines.SelectMany(l => l.Words))
        {
            var shift = CenterY(candidate) - CenterY(word);
            if (shift < WordBelowMinShiftToHeight * height || shift > WordBelowMaxShiftToHeight * height) continue;
            if (Math.Abs(candidate.PixelBounds.Left - word.PixelBounds.Left) > WordBelowLeftToleranceToHeight * height) continue;
            if (shift >= bestShift) continue;
            best = candidate;
            bestShift = shift;
        }
        return best;
    }

    private static OcrLine WithWordBounds(OcrLine line)
    {
        if (line.Confidence >= OcrWordText.TextLayerConfidence || line.Words.Count == 0) return line;
        var bounds = line.Words.Select(w => w.PixelBounds).Aggregate(RectMath.Union);
        return bounds == line.PixelBounds ? line : new OcrLine(line.Text, bounds, line.Confidence, line.Words);
    }

    private static List<List<OcrWord>> SplitIntoRows(OcrLine line)
    {
        var tolerance = SameRowToleranceToHeight * MedianHeight(line.Words);
        var rows = new List<List<OcrWord>>();
        foreach (var word in line.Words.OrderBy(w => w.PixelBounds.Left))
        {
            List<OcrWord>? best = null;
            var bestDistance = double.MaxValue;
            foreach (var row in rows)
            {
                var distance = Math.Abs(CenterY(row[^1]) - CenterY(word));
                if (distance <= tolerance && distance < bestDistance)
                {
                    best = row;
                    bestDistance = distance;
                }
            }

            if (best is null) rows.Add(new List<OcrWord> { word });
            else best.Add(word);
        }
        return rows;
    }

    private static void MergeFragments(List<Piece> pieces)
    {
        foreach (var fragment in pieces)
        {
            if (!fragment.IsFragment || fragment.Removed) continue;

            var words = fragment.Words;
            var height = MedianHeight(words);
            var maxGap = FragmentMergeGapToHeight * height;
            var tolerance = SameRowToleranceToHeight * height;
            var left = words[0].PixelBounds.Left;
            var right = words[^1].PixelBounds.Right;

            Piece? best = null;
            var bestGap = double.MaxValue;
            foreach (var piece in pieces)
            {
                if (ReferenceEquals(piece, fragment) || piece.Removed) continue;
                if (piece.Confidence >= OcrWordText.TextLayerConfidence) continue;
                if (piece.Words.Any(w => w.PixelBounds.Left < right && w.PixelBounds.Right > left)) continue;

                var leftNeighbor = LastBefore(piece.Words, left);
                if (leftNeighbor is not null)
                {
                    var gap = left - leftNeighbor.PixelBounds.Right;
                    if (gap <= maxGap && Math.Abs(CenterY(leftNeighbor) - CenterY(words[0])) <= tolerance && gap < bestGap)
                    {
                        bestGap = gap;
                        best = piece;
                    }
                }

                var rightNeighbor = FirstAfter(piece.Words, right);
                if (rightNeighbor is not null)
                {
                    var gap = rightNeighbor.PixelBounds.Left - right;
                    if (gap <= maxGap && Math.Abs(CenterY(rightNeighbor) - CenterY(words[^1])) <= tolerance && gap < bestGap)
                    {
                        bestGap = gap;
                        best = piece;
                    }
                }
            }

            if (best is null) continue;
            best.Words = best.Words.Concat(words).OrderBy(w => w.PixelBounds.Left).ToList();
            best.Merged = true;
            fragment.Removed = true;
        }
    }

    private static OcrWord? LastBefore(List<OcrWord> words, int left)
    {
        OcrWord? result = null;
        foreach (var word in words)
        {
            if (word.PixelBounds.Right > left) continue;
            if (result is null || word.PixelBounds.Right > result.PixelBounds.Right) result = word;
        }
        return result;
    }

    private static OcrWord? FirstAfter(List<OcrWord> words, int right)
    {
        OcrWord? result = null;
        foreach (var word in words)
        {
            if (word.PixelBounds.Left < right) continue;
            if (result is null || word.PixelBounds.Left < result.PixelBounds.Left) result = word;
        }
        return result;
    }

    private static void MergeInterleavedLines(List<Piece> pieces)
    {
        var live = pieces
            .Where(p => !p.Removed && p.Confidence < OcrWordText.TextLayerConfidence && p.Words.Count > 0)
            .ToList();

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var target in live)
            {
                if (target.Removed) continue;
                foreach (var other in live)
                {
                    if (ReferenceEquals(other, target) || other.Removed) continue;
                    if (!AreInterleavedOnSameRow(target.Words, other.Words)) continue;

                    target.Words = target.Words.Concat(other.Words).OrderBy(w => w.PixelBounds.Left).ToList();
                    target.Merged = true;
                    target.Original = null;
                    other.Removed = true;
                    changed = true;
                }
            }
        }
    }

    private static bool AreInterleavedOnSameRow(List<OcrWord> a, List<OcrWord> b)
    {
        var aLeft = a.Min(w => w.PixelBounds.Left);
        var aRight = a.Max(w => w.PixelBounds.Right);
        var bLeft = b.Min(w => w.PixelBounds.Left);
        var bRight = b.Max(w => w.PixelBounds.Right);
        if (aLeft >= bRight || bLeft >= aRight) return false;
        if (a.Any(x => b.Any(y => Overlaps(x.PixelBounds, y.PixelBounds)))) return false;

        var tolerance = SameRowToleranceToHeight * MedianHeight(a.Concat(b).ToList());
        foreach (var word in b)
        {
            var nearest = a[0];
            var nearestDistance = HorizontalDistance(nearest, word);
            foreach (var candidate in a)
            {
                var distance = HorizontalDistance(candidate, word);
                if (distance < nearestDistance)
                {
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }
            if (Math.Abs(CenterY(nearest) - CenterY(word)) > tolerance) return false;
        }
        return true;
    }

    private static void MergeRowNeighbours(List<Piece> pieces)
    {
        var live = pieces
            .Where(p => !p.Removed && p.Confidence < OcrWordText.TextLayerConfidence && p.Words.Count > 0)
            .ToList();

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var target in live)
            {
                if (target.Removed) continue;
                foreach (var other in live)
                {
                    if (ReferenceEquals(other, target) || other.Removed) continue;
                    if (!AreRowNeighbours(target.Words, other.Words)) continue;

                    target.Words = target.Words.Concat(other.Words).OrderBy(w => w.PixelBounds.Left).ToList();
                    target.Merged = true;
                    target.Original = null;
                    other.Removed = true;
                    changed = true;
                }
            }
        }
    }

    private static bool AreRowNeighbours(List<OcrWord> left, List<OcrWord> right)
    {
        var leftEnd = left.Max(w => w.PixelBounds.Right);
        var rightStart = right.Min(w => w.PixelBounds.Left);
        if (rightStart < leftEnd) return false;

        var height = MedianHeight(left.Concat(right).ToList());
        var gap = rightStart - leftEnd;
        var medianGap = MedianPositiveGap(left, right);
        if (gap > RowNeighbourGapToHeight * height &&
            (gap > RowNeighbourGapToMedianGap * medianGap || gap > RowNeighbourMaxGapToHeight * height)) return false;

        var last = left.OrderByDescending(w => w.PixelBounds.Right).First();
        var first = right.OrderBy(w => w.PixelBounds.Left).First();
        return Math.Abs(CenterY(last) - CenterY(first)) <= SameRowToleranceToHeight * height;
    }

    private static int MedianPositiveGap(List<OcrWord> a, List<OcrWord> b)
    {
        var gaps = new List<int>();
        foreach (var words in new[] { a, b })
        {
            for (var i = 1; i < words.Count; i++)
            {
                var gap = words[i].PixelBounds.Left - words[i - 1].PixelBounds.Right;
                if (gap > 0) gaps.Add(gap);
            }
        }
        if (gaps.Count == 0) return 0;
        gaps.Sort();
        return gaps[(gaps.Count - 1) / 2];
    }

    private static int HorizontalDistance(OcrWord a, OcrWord b) =>
        Math.Min(Math.Abs(a.PixelBounds.Left - b.PixelBounds.Right), Math.Abs(b.PixelBounds.Left - a.PixelBounds.Right));

    private static bool Overlaps(SKRectI a, SKRectI b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private static double CenterY(OcrWord word) => (word.PixelBounds.Top + word.PixelBounds.Bottom) / 2.0;

    public static int MedianHeight(List<OcrWord> words)
    {
        if (words.Count == 0) return 1;
        var heights = words.Select(w => w.PixelBounds.Height).OrderBy(h => h).ToList();
        return Math.Max(1, heights[heights.Count / 2]);
    }
}
