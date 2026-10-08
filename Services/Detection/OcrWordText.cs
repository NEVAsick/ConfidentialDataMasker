using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ConfidentialDataMasker.Services;

internal static class OcrWordText
{
    public const float TextLayerConfidence = 100f;

    private static readonly char[] EdgePunctuation = "«»„“”‘’‚\"'()[]{}<>.,:;!?/\\|—–-_*№".ToCharArray();

    private static readonly Regex CyrillicWordPattern =
        new(@"^(?:[А-ЯЁ]?[а-яё]{2,}(?:-[А-ЯЁа-яё]+)*|[А-ЯЁ]{3,}(?:-[А-ЯЁ]+)*)$", RegexOptions.Compiled);

    private static readonly Regex LatinTokenPattern =
        new(@"^(?=.*[.@\d])[A-Za-z0-9._@\-]{4,}$", RegexOptions.Compiled);

    private static readonly Regex TwoDigitsPattern = new(@"\d.*\d", RegexOptions.Compiled);

    private static readonly Regex TypedWordPattern =
        new(@"^[А-ЯЁ]?[а-яё]{2,}(?:-[А-ЯЁа-яё]+)*$", RegexOptions.Compiled);

    private static readonly Regex AbbreviationPattern =
        new(@"^[«""(]*[А-ЯЁ]?[а-яё]{1,3}\.[,;:»"")]*$", RegexOptions.Compiled);

    private static readonly char[] InnerPunctuation = { ',', '.', ';', ':' };

    private static readonly char[] GluedWordSeparators = "«»()[],.;:".ToCharArray();

    private static readonly Regex WordGluedToDigitsPattern =
        new(@"^(?:\d+[а-яё]{6,}|[а-яё]{6,}\d+)$", RegexOptions.Compiled);

    public static bool LooksLikeRealWord(string text)
    {
        var core = text.Trim(EdgePunctuation);
        return CyrillicWordPattern.IsMatch(core) || LatinTokenPattern.IsMatch(core) || TwoDigitsPattern.IsMatch(core);
    }

    public static bool LooksLikeTypedOrGluedTypedWords(string text)
    {
        var core = text.Trim(EdgePunctuation);
        if (TypedWordPattern.IsMatch(core)) return true;
        var parts = core.Split(GluedWordSeparators, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts.All(p => TypedWordPattern.IsMatch(p));
    }

    public static bool IsWordGluedToDigits(string text) => WordGluedToDigitsPattern.IsMatch(text.Trim(EdgePunctuation));

    public static bool LooksLikeAbbreviation(string text) => AbbreviationPattern.IsMatch(text);

    public static bool IsGluedRealWords(string text)
    {
        var parts = text.Trim(EdgePunctuation).Split(InnerPunctuation, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts.All(LooksLikeRealWord);
    }
}
