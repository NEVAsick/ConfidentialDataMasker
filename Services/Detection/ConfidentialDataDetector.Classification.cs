using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed partial class ConfidentialDataDetector
{
    private static readonly Dictionary<char, char> LatinToCyrillic = new()
    {
        ['A'] = 'А', ['B'] = 'В', ['C'] = 'С', ['E'] = 'Е', ['H'] = 'Н', ['K'] = 'К', ['M'] = 'М',
        ['O'] = 'О', ['P'] = 'Р', ['T'] = 'Т', ['X'] = 'Х', ['Y'] = 'У',
        ['a'] = 'а', ['c'] = 'с', ['e'] = 'е', ['o'] = 'о', ['p'] = 'р', ['x'] = 'х', ['y'] = 'у',
    };

    private static readonly string[] RedactionTargetGroupNames = { "secret", "org" };

    private static bool HasValue(Group group) => group.Success && group.Value.Any(char.IsLetterOrDigit);

    private static string? GetStandaloneLabel(string text)
    {
        var key = NormalizeHomoglyphs(text).Trim().TrimEnd(':', '-', '–', '—', '.', ' ');
        return StandaloneFieldLabels.TryGetValue(key, out var label) ? label : null;
    }

    private const int MaxLatinTokenForHomoglyphs = 3;

    private static string NormalizeHomoglyphs(string text)
    {
        if (IsMostlyCyrillic(text))
        {
            text = LatinCapsWordPattern.Replace(text, m => new string(m.Value.Select(c => LatinToCyrillic[c]).ToArray()));
        }
        var cyrillicText = IsMostlyCyrillic(text);
        char[]? chars = null;
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            var hasCyrillic = false;
            while (i < text.Length && char.IsLetter(text[i]))
            {
                if (IsCyrillic(text[i])) hasCyrillic = true;
                i++;
            }
            var shortMappable = cyrillicText && i - start <= MaxLatinTokenForHomoglyphs &&
                Enumerable.Range(start, i - start).All(k => LatinToCyrillic.ContainsKey(text[k]));
            if (!hasCyrillic && !shortMappable) continue;

            for (var j = start; j < i; j++)
            {
                if (LatinToCyrillic.TryGetValue(text[j], out var replacement))
                {
                    chars ??= text.ToCharArray();
                    chars[j] = replacement;
                }
            }
        }
        var normalized = chars is null ? text : new string(chars);
        normalized = DigitLikeLettersPattern.Replace(normalized, m => m.Value
            .Replace('O', '0').Replace('О', '0').Replace('o', '0').Replace('о', '0'));
        return ZeroAfterLetterPattern.Replace(normalized, "О");
    }

    private static readonly Regex DigitLikeLettersPattern =
        new(@"(?<![^\W\d_])(?=[\dOОoо]*\d)(?=[\dOОoо]{2})[\dOОoо]+(?![^\W\d_])", RegexOptions.Compiled);

    private static bool IsMostlyCyrillic(string text) => text.Count(IsCyrillic) * 2 >= text.Count(char.IsLetter);

    private static readonly Regex ZeroAfterLetterPattern = new(@"(?<=[а-яё])0(?=\.)", RegexOptions.Compiled);

    private static readonly Regex LatinCapsWordPattern =
        new(@"(?<![^\W\d_])[ABCEHKMOPTXY]{4,}(?![^\W\d_])", RegexOptions.Compiled);

    private static bool IsCyrillic(char c) => (c >= 'А' && c <= 'я') || c == 'Ё' || c == 'ё';

    private static (string Label, Match Match)? ClassifyText(OcrLine line)
    {
        var all = ClassifyAll(line, NoDictionaryPatterns);
        return all.Count == 0 ? null : all[0];
    }

    private static List<(string Label, Match Match)> ClassifyAll(
        OcrLine line, IReadOnlyList<(Regex Pattern, string Label)> dictionaryPatterns, bool includeFullFields = true)
    {
        var text = NormalizeHomoglyphs(line.Text);
        var result = new List<(string Label, Match Match)>();

        if (includeFullFields)
        {
            foreach (var (pattern, fieldLabel) in FullFieldPatterns)
            {
                var fullFieldMatch = pattern.Match(text);
                if (fullFieldMatch.Success)
                {
                    result.Add((fieldLabel, fullFieldMatch));
                    return result;
                }
            }
        }

        var claimed = new List<(int Start, int End)>();

        foreach (var (pattern, dictionaryLabel) in dictionaryPatterns)
        {
            foreach (Match match in pattern.Matches(text))
            {
                if (TryClaim(claimed, match)) result.Add((dictionaryLabel, match));
            }
        }

        foreach (var (pattern, fieldLabel) in SimplePatterns)
        {
            foreach (Match match in pattern.Matches(text))
            {
                if (TryClaim(claimed, match)) result.Add((fieldLabel, match));
            }
        }

        foreach (var nameMatch in FindNameMatches(line, text))
        {
            if (TryClaim(claimed, nameMatch)) result.Add((NameLabel, nameMatch));
        }

        return result;
    }

    private static bool TryClaim(List<(int Start, int End)> claimed, Match match)
    {
        var target = GetRedactionTarget(match);
        var start = target.Index;
        var end = target.Index + target.Length;
        if (end <= start) return false;

        for (var i = start; i < end; i++)
        {
            var position = i;
            if (!claimed.Any(c => c.Start <= position && position < c.End))
            {
                claimed.Add((start, end));
                return true;
            }
        }
        return false;
    }

    private static SKRectI? GetMatchPixelBounds(OcrLine line, Match match)
    {
        var target = GetRedactionTarget(match);

        SKRectI? union = null;
        var offset = 0;

        foreach (var word in line.Words)
        {
            var wordStart = offset;
            var wordEnd = offset + word.Text.Length;
            offset = wordEnd + 1;

            var overlaps = wordEnd > target.Index && wordStart < target.Index + target.Length;
            if (!overlaps) continue;

            union = union is null ? word.PixelBounds : RectMath.Union(union.Value, word.PixelBounds);
        }

        return union;
    }

    private static Group GetRedactionTarget(Match match)
    {
        foreach (var name in RedactionTargetGroupNames)
        {
            var group = match.Groups[name];
            if (group.Success) return group;
        }

        return match;
    }
}
