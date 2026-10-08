using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

internal static class TextAlignment
{
    private const int MinMatchedChars = 3;
    private const int MinComparableChars = 4;
    private const int MinTokenWidthPx = 2;

    private static readonly Dictionary<char, char> LatinLookalikes = new()
    {
        ['a'] = 'а', ['b'] = 'в', ['c'] = 'с', ['e'] = 'е', ['h'] = 'н', ['k'] = 'к', ['m'] = 'м',
        ['o'] = 'о', ['p'] = 'р', ['t'] = 'т', ['x'] = 'х', ['y'] = 'у', ['ё'] = 'е',
    };

    private readonly record struct Token(int Start, int End, string Text);

    public static double Similarity(string a, string b)
    {
        var keysA = Keys(a, LetterOrDigitPositions(a));
        var keysB = Keys(b, LetterOrDigitPositions(b));
        var (pattern, text) = keysA.Length <= keysB.Length ? (keysA, keysB) : (keysB, keysA);
        if (pattern.Length < MinComparableChars) return 0.0;
        var fit = Fit(pattern, text);
        return fit is null ? 0.0 : 1.0 - (double)fit.Value.Cost / pattern.Length;
    }

    public static List<OcrWord>? Realign(List<OcrWord> words, string modelText)
    {
        if (words.Count == 0) return null;

        var lineText = string.Join(" ", words.Select(w => w.Text));
        var owner = new List<int>(lineText.Length);
        for (var wi = 0; wi < words.Count; wi++)
        {
            owner.AddRange(Enumerable.Repeat(wi, words[wi].Text.Length));
            if (wi + 1 < words.Count) owner.Add(-1);
        }

        var linePositions = LetterOrDigitPositions(lineText);
        var modelPositions = LetterOrDigitPositions(modelText);
        var pairs = Pairs(Keys(lineText, linePositions), Keys(modelText, modelPositions));
        if (pairs.Count < MinMatchedChars) return null;

        var mapped = new SortedList<int, int>();
        foreach (var (lineIndex, modelIndex) in pairs) mapped[modelPositions[modelIndex]] = linePositions[lineIndex];

        var firstModel = modelPositions[pairs[0].B];
        var lastModel = modelPositions[pairs[^1].B];
        var firstWord = owner[linePositions[pairs[0].A]];
        var lastWord = owner[linePositions[pairs[^1].A]];

        var tokens = Tokenize(modelText);
        var firstToken = tokens.FindIndex(t => t.Start <= firstModel && firstModel < t.End);
        var lastToken = tokens.FindIndex(t => t.Start <= lastModel && lastModel < t.End);
        if (firstToken < 0 || lastToken < 0) return null;

        while (firstToken > 0 && !HasLetterOrDigit(tokens[firstToken - 1].Text)) firstToken--;
        while (lastToken + 1 < tokens.Count && !HasLetterOrDigit(tokens[lastToken + 1].Text)) lastToken++;
        while (firstWord > 0 && !HasLetterOrDigit(words[firstWord - 1].Text)) firstWord--;
        while (lastWord + 1 < words.Count && !HasLetterOrDigit(words[lastWord + 1].Text)) lastWord++;

        var region = words.GetRange(firstWord, lastWord - firstWord + 1);
        var modelTokens = tokens.GetRange(firstToken, lastToken - firstToken + 1);
        if (modelTokens.Select(t => t.Text).SequenceEqual(region.Select(w => w.Text))) return null;

        var known = mapped.ToList();
        var charBounds = CharBounds(words);
        var lo = region.Min(w => w.PixelBounds.Left);
        var hi = region.Max(w => w.PixelBounds.Right);
        var top = region.Min(w => w.PixelBounds.Top);
        var bottom = region.Max(w => w.PixelBounds.Bottom);
        var regionConfidence = region.Min(w => w.Confidence);

        var newWords = new List<OcrWord>();
        for (var k = 0; k < modelTokens.Count; k++)
        {
            var token = modelTokens[k];
            var x0 = XAt(charBounds, LinePosition(known, mapped, token.Start));
            var x1 = XAt(charBounds, LinePosition(known, mapped, token.End - 1) + 1);
            x0 = Math.Max(lo, Math.Min(x0, hi - MinTokenWidthPx));
            x1 = Math.Min(hi, Math.Max(x1, x0 + MinTokenWidthPx));

            var over = region.Where(w => w.PixelBounds.Left < x1 && w.PixelBounds.Right > x0).ToList();
            var confidence = over.Count > 0 ? over.Min(w => w.Confidence) : regionConfidence;
            var tokenTop = over.Count > 0 ? over.Min(w => w.PixelBounds.Top) : top;
            var tokenBottom = over.Count > 0 ? over.Max(w => w.PixelBounds.Bottom) : bottom;

            var text = token.Text;
            var main = over.Count == 0
                ? null
                : over.MaxBy(w => Math.Min(w.PixelBounds.Right, x1) - Math.Max(w.PixelBounds.Left, x0));
            if (main is not null)
            {
                if (AllKeys(main.Text).SequenceEqual(AllKeys(token.Text)))
                {
                    text = main.Text;
                }
                else
                {
                    var mainKey = Keys(main.Text, LetterOrDigitPositions(main.Text));
                    var tokenKey = Keys(token.Text, LetterOrDigitPositions(token.Text));
                    if (tokenKey.Length > 0 && tokenKey.Length < mainKey.Length)
                    {
                        var isCutStart = k == 0 && mainKey.Skip(mainKey.Length - tokenKey.Length).SequenceEqual(tokenKey);
                        var isCutEnd = k == modelTokens.Count - 1 && mainKey.Take(tokenKey.Length).SequenceEqual(tokenKey);
                        if (isCutStart || isCutEnd) text = main.Text;
                    }
                }
            }

            newWords.Add(new OcrWord(
                text,
                new SKRectI((int)Math.Round(x0), tokenTop, (int)Math.Round(x1), tokenBottom),
                confidence));
        }

        if (newWords.Select(w => w.Text).SequenceEqual(region.Select(w => w.Text))) return null;
        return words.Take(firstWord).Concat(newWords).Concat(words.Skip(lastWord + 1)).ToList();
    }

    public static List<OcrWord> WordsInBox(string text, SKRectI box, float confidence)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var total = tokens.Sum(t => t.Length) + Math.Max(0, tokens.Length - 1);
        var words = new List<OcrWord>(tokens.Length);
        var position = 0;
        foreach (var token in tokens)
        {
            var left = (int)Math.Round(box.Left + (double)box.Width * position / total);
            var right = (int)Math.Round(box.Left + (double)box.Width * (position + token.Length) / total);
            words.Add(new OcrWord(token, new SKRectI(left, box.Top, Math.Max(left + MinTokenWidthPx, right), box.Bottom), confidence));
            position += token.Length + 1;
        }
        return words;
    }

    private static char Key(char c)
    {
        var lower = char.ToLowerInvariant(c);
        return LatinLookalikes.TryGetValue(lower, out var mapped) ? mapped : lower;
    }

    private static char[] AllKeys(string s) => s.Select(Key).ToArray();

    private static bool HasLetterOrDigit(string s) => s.Any(char.IsLetterOrDigit);

    private static List<int> LetterOrDigitPositions(string s)
    {
        var positions = new List<int>();
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsLetterOrDigit(s[i])) positions.Add(i);
        }
        return positions;
    }

    private static char[] Keys(string s, List<int> positions) => positions.Select(p => Key(s[p])).ToArray();

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var start = 0;
        foreach (var part in text.Split(' '))
        {
            if (part.Length > 0) tokens.Add(new Token(start, start + part.Length, part));
            start += part.Length + 1;
        }
        return tokens;
    }

    private static List<(int A, int B)> Pairs(char[] a, char[] b)
    {
        if (b.Length <= a.Length)
        {
            var fit = Fit(b, a);
            return fit is null ? new List<(int, int)>() : fit.Value.Pairs.Select(p => (p.Text, p.Pattern)).ToList();
        }

        var reverse = Fit(a, b);
        return reverse is null ? new List<(int, int)>() : reverse.Value.Pairs.Select(p => (p.Pattern, p.Text)).ToList();
    }

    private static (int Cost, List<(int Pattern, int Text)> Pairs)? Fit(char[] pattern, char[] text)
    {
        var m = pattern.Length;
        var n = text.Length;
        if (m == 0 || n == 0) return null;

        var dp = new int[m + 1, n + 1];
        for (var i = 0; i <= m; i++) dp[i, 0] = i;
        for (var i = 1; i <= m; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                var diagonal = dp[i - 1, j - 1] + (pattern[i - 1] == text[j - 1] ? 0 : 1);
                var up = dp[i - 1, j] + 1;
                var left = dp[i, j - 1] + 1;
                dp[i, j] = diagonal <= up && diagonal <= left ? diagonal : (up <= left ? up : left);
            }
        }

        var end = 0;
        for (var j = 1; j <= n; j++)
        {
            if (dp[m, j] < dp[m, end]) end = j;
        }

        var cost = dp[m, end];
        var pairs = new List<(int Pattern, int Text)>();
        var pi = m;
        var tj = end;
        while (pi > 0 && tj > 0)
        {
            var same = pattern[pi - 1] == text[tj - 1];
            if (dp[pi, tj] == dp[pi - 1, tj - 1] + (same ? 0 : 1))
            {
                if (same) pairs.Add((pi - 1, tj - 1));
                pi--;
                tj--;
            }
            else if (dp[pi, tj] == dp[pi - 1, tj] + 1)
            {
                pi--;
            }
            else
            {
                tj--;
            }
        }
        pairs.Reverse();
        return (cost, pairs);
    }

    private static double LinePosition(List<KeyValuePair<int, int>> known, SortedList<int, int> mapped, int modelPosition)
    {
        if (mapped.TryGetValue(modelPosition, out var exact)) return exact;

        KeyValuePair<int, int>? previous = null;
        KeyValuePair<int, int>? next = null;
        foreach (var pair in known)
        {
            if (pair.Key < modelPosition) previous = pair;
            else if (pair.Key > modelPosition)
            {
                next = pair;
                break;
            }
        }

        if (previous is not null && next is not null)
        {
            var (k0, v0) = (previous.Value.Key, previous.Value.Value);
            var (k1, v1) = (next.Value.Key, next.Value.Value);
            return v0 + (double)(v1 - v0) * (modelPosition - k0) / (k1 - k0);
        }

        if (previous is not null) return previous.Value.Value + (modelPosition - previous.Value.Key);
        return next!.Value.Value - (next.Value.Key - modelPosition);
    }

    private static List<(double Left, double Right)> CharBounds(List<OcrWord> words)
    {
        var bounds = new List<(double, double)>();
        for (var wi = 0; wi < words.Count; wi++)
        {
            var b = words[wi].PixelBounds;
            var length = Math.Max(1, words[wi].Text.Length);
            for (var k = 0; k < words[wi].Text.Length; k++)
            {
                bounds.Add((b.Left + (double)b.Width * k / length, b.Left + (double)b.Width * (k + 1) / length));
            }
            if (wi + 1 < words.Count)
            {
                bounds.Add((b.Right, Math.Max(b.Right, words[wi + 1].PixelBounds.Left)));
            }
        }
        return bounds;
    }

    private static double XAt(List<(double Left, double Right)> bounds, double position)
    {
        if (bounds.Count == 0) return 0;
        if (position <= 0) return bounds[0].Left;
        if (position >= bounds.Count) return bounds[^1].Right;
        var i = (int)position;
        var (left, right) = bounds[i];
        return left + (right - left) * (position - i);
    }
}
