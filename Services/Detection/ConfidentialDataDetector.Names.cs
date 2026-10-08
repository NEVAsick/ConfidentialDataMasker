using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed partial class ConfidentialDataDetector
{
    private static readonly Regex NameWithInitialsPattern =
        new(@"\b(?:[А-ЯЁ][а-яё]{2,}(?:-[А-ЯЁ][а-яё]+)?(?:\s*|\.\s?)(?:(?:[А-ЯЁ]|[A-Z]{1,2})\.\s?(?:[А-ЯЁ]|[A-Z]{1,2})[.:]|[А-ЯЁ]\.\s?(?:[А-ЯЁ][.:])?)|[A-Z][a-z]{2,}(?:\s*|\.\s?)(?:[А-ЯЁ]|[A-Z]{1,2})\.\s?(?:[А-ЯЁ]|[A-Z]{1,2})[.:])", RegexOptions.Compiled);

    private static readonly Regex InitialsBeforeSurnamePattern =
        new(@"(?<![А-ЯЁа-яёA-Za-z])(?:[А-ЯЁ](?:\.\s?(?:[А-ЯЁ][.:]\s?)?|[А-ЯЁ]\.\s?)(?:[А-ЯЁ][а-яё]{2,}(?:-[А-ЯЁ][а-яё]+)?|[А-ЯЁ]{3,})|[A-Z]{1,3}\.\s?(?:[A-Z]{1,2}\.\s?)?[А-ЯЁ][а-яё]{2,}(?:-[А-ЯЁ][а-яё]+)?)",
            RegexOptions.Compiled);

    private static readonly Regex SignedNamePattern =
        new(@"(?<![^\s(])/\s?(?<secret>[A-ZА-ЯЁ][^\s/.]{0,2}\.\s?(?:[^\s/.]{1,2}[.,]\s?)?[A-ZА-ЯЁ][a-zа-яё]{2,}(?:-[A-ZА-ЯЁ][a-zа-яё]+)?)\s?/",
            RegexOptions.Compiled);

    private static readonly Regex NameTokenPattern =
        new(@"(?:[А-ЯЁA-Z]{2,}(?![а-яёa-z])|[А-ЯЁA-Z]?[а-яёa-z]+)(?:-(?:[А-ЯЁA-Z]{2,}(?![а-яёa-z])|[А-ЯЁA-Z]?[а-яёa-z]+))*", RegexOptions.Compiled);

    private static readonly Regex SpanPattern = new(@"\S(?:.*\S)?", RegexOptions.Compiled);

    private const int MaxNameWords = 3;

    private static readonly string[] NameFalsePositiveStems =
    {
        "росси", "федера", "мужск", "женск", "дата", "адрес", "мест", "номер", "серия", "код",
        "гражд", "красноярск", "област", "район", "город", "полис", "омс", "снилс", "инн", "мвд",
        "шифр", "мкб", "диагноз", "обращен", "результат",
        "увольнени", "прием", "перевод",
        "подпис", "электрон", "сертификат", "владелец", "действител",
        "кодекс", "гражданств", "директор", "генеральн", "начальник", "руководител", "заместител",
        "бухгалтер", "менеджер", "специалист", "инженер", "главн", "старш", "ведущ",
        "общество", "ограниченн", "ответственн", "акционерн", "публичн", "договор", "приложени",
        "согласован", "утвержд", "приказ", "должност", "отдел", "управлени", "департамент",
        "служб", "филиал", "уважаем", "исполнител", "работник", "сотрудник", "заказчик",
        "подрядчик", "поставщик", "покупател", "продав", "арендатор", "арендодател", "сторон",
        "учреждени", "организаци", "предприяти", "компани", "поликлиник", "больниц", "межрайонн",
        "университет", "институт", "колледж",
        "крайн", "республик", "огрн", "кпп", "бик", "окпо", "тел", "факс", "исх",
        "фамили", "имя", "отчеств", "стать", "глав", "раздел", "пункт", "част", "учетн", "учётн", "копи", "тема", "отправлен", "рудник", "принят", "надеждинск", "металлург",
        "табельн", "структурн", "подразделени", "назначени", "наименовани", "примечани", "основани", "количеств", "итого", "всего",
        "окуд", "окато", "октмо", "оквэд", "окопф", "окфс",
    };

    private static readonly string[] PatronymicSuffixes =
    {
        "ович", "евич", "инич", "овна", "евна", "ична",
    };

    private static bool LooksLikeWrappedPatronymic(OcrLine candidate)
    {
        var word = candidate.Text.Trim();
        if (word.Length < 5 || word.Contains(' ') || word.Contains(':')) return false;
        return PatronymicSuffixes.Any(suffix => word.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static (OcrWord Word, OcrLine Line)? FindSurnameOnLineAbove(
        OcrLine line, Match match, string[] nameWords, List<OcrLine> lines)
    {
        var patronymic = nameWords[1];
        if (!PatronymicSuffixes.Any(suffix => patronymic.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))) return null;

        var target = GetRedactionTarget(match);
        if (line.Text.Substring(0, target.Index).Any(char.IsLetter)) return null;

        var anchorHeight = Math.Max(1, line.PixelBounds.Height);
        OcrLine? above = null;
        var bestGap = int.MaxValue;
        foreach (var candidate in lines)
        {
            if (ReferenceEquals(candidate, line) || candidate.Words.Count == 0) continue;
            var gap = line.PixelBounds.Top - candidate.PixelBounds.Bottom;
            if (gap < -anchorHeight / 4 || gap > anchorHeight) continue;
            if (candidate.PixelBounds.Right <= line.PixelBounds.Left || line.PixelBounds.Right <= candidate.PixelBounds.Left) continue;
            if (gap < bestGap)
            {
                bestGap = gap;
                above = candidate;
            }
        }

        if (above is null) return null;
        var lastWord = above.Words[^1];
        return IsQualifyingNameWord(lastWord.Text) ? (lastWord, above) : null;
    }

    private static readonly Regex AllCapsWordPattern = new(@"^[А-ЯЁ]{3,}$", RegexOptions.Compiled);

    private static IEnumerable<Match> FindNameMatches(OcrLine line, string text)
    {
        var result = new List<Match>();
        var run = new List<Match>();
        Match? previous = null;

        foreach (Match token in NameTokenPattern.Matches(text))
        {
            var qualifies = IsQualifyingNameWord(token.Value);
            var joinsPrevious = previous is not null &&
                string.IsNullOrWhiteSpace(text.Substring(
                    previous.Index + previous.Length, token.Index - previous.Index - previous.Length));

            if (!qualifies || !joinsPrevious)
            {
                AddNameRun(line, text, run, result);
                run.Clear();
            }
            if (qualifies) run.Add(token);
            previous = token;
        }
        AddNameRun(line, text, run, result);

        foreach (Match candidate in NameWithInitialsPattern.Matches(text))
        {
            var surname = candidate.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            if (IsFalsePositiveWord(surname)) continue;
            if (HasSuspiciousWordGap(line, candidate)) continue;
            result.Add(candidate);
        }

        foreach (Match candidate in InitialsBeforeSurnamePattern.Matches(text))
        {
            var surname = candidate.Value.Substring(candidate.Value.LastIndexOfAny(new[] { '.', ':' }) + 1).Trim();
            if (IsFalsePositiveWord(surname)) continue;
            var initialsCount = candidate.Value.Count(c => c == '.' || c == ':');
            if (initialsCount == 1 && text.Substring(0, candidate.Index).Trim().Length == 0) continue;
            if (HasSuspiciousWordGap(line, candidate)) continue;
            result.Add(candidate);
        }

        foreach (Match candidate in SignedNamePattern.Matches(text))
        {
            var name = candidate.Groups["secret"].Value;
            if (IsFalsePositiveWord(name.Substring(name.LastIndexOf('.') + 1).Trim())) continue;
            result.Add(candidate);
        }

        return result;
    }

    private static void AddNameRun(OcrLine line, string text, List<Match> run, List<Match> result)
    {
        if (run.Count < 2) return;

        var chunkCount = (run.Count + MaxNameWords - 1) / MaxNameWords;
        var baseSize = run.Count / chunkCount;
        var remainder = run.Count % chunkCount;
        var index = 0;

        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            var size = baseSize + (chunk < remainder ? 1 : 0);
            var first = run[index];
            var last = run[index + size - 1];
            index += size;

            var words = run.Skip(index - size).Take(size).Select(m => m.Value).ToList();
            if (words.Count < 3 && words.All(w => AllCapsWordPattern.IsMatch(w))) continue;

            var span = SpanPattern.Match(text, first.Index, last.Index + last.Length - first.Index);
            if (!span.Success) continue;
            if (!span.Value.Any(char.IsWhiteSpace)) continue;
            if (HasSuspiciousWordGap(line, span)) continue;
            result.Add(span);
        }
    }

    private const double MaxNameWordGapToHeightRatio = 1.3;
    private const double MaxNameGapToMedianRatio = 2.0;

    private static double MedianWordGapInRow(OcrLine line, OcrWord anchor)
    {
        var centerY = (anchor.PixelBounds.Top + anchor.PixelBounds.Bottom) / 2.0;
        var height = Math.Max(1, anchor.PixelBounds.Height);
        bool InRow(OcrWord w) =>
            Math.Abs((w.PixelBounds.Top + w.PixelBounds.Bottom) / 2.0 - centerY) <= Math.Max(height, w.PixelBounds.Height) / 2.0;

        var gaps = new List<int>();
        for (var i = 1; i < line.Words.Count; i++)
        {
            if (!InRow(line.Words[i]) || !InRow(line.Words[i - 1])) continue;
            var gap = line.Words[i].PixelBounds.Left - line.Words[i - 1].PixelBounds.Right;
            if (gap > 0) gaps.Add(gap);
        }
        if (gaps.Count == 0) return 0;
        gaps.Sort();
        return gaps[(gaps.Count - 1) / 2];
    }

    private static bool HasSuspiciousWordGap(OcrLine line, Match match)
    {
        var target = GetRedactionTarget(match);
        var targetStart = target.Index;
        var targetEnd = target.Index + target.Length;

        OcrWord? previous = null;
        var offset = 0;

        foreach (var word in line.Words)
        {
            var wordStart = offset;
            var wordEnd = offset + word.Text.Length;
            offset = wordEnd + 1;

            var overlaps = wordEnd > targetStart && wordStart < targetEnd;
            if (!overlaps) continue;

            if (previous is not null)
            {
                var gap = word.PixelBounds.Left - previous.PixelBounds.Right;
                var maxHeight = Math.Max(previous.PixelBounds.Height, word.PixelBounds.Height);
                if (gap > MaxNameWordGapToHeightRatio * maxHeight &&
                    gap > MaxNameGapToMedianRatio * MedianWordGapInRow(line, word)) return true;
            }

            previous = word;
        }

        return false;
    }

    private static readonly Regex NameWordPattern =
        new(@"^(?:[А-ЯЁ][а-яё]{2,}(?:-[А-ЯЁ][а-яё]+)?|[А-ЯЁ]{3,}(?:-[А-ЯЁ]{2,})?)$", RegexOptions.Compiled);

    private const int GluedStemMinLength = 7;
    private const int GluedStemMinOffset = 3;

    private static bool IsQualifyingNameWord(string word)
    {
        return NameWordPattern.IsMatch(word) && !IsFalsePositiveWord(word);
    }

    private static bool IsFalsePositiveWord(string word)
    {
        foreach (var stem in NameFalsePositiveStems)
        {
            if (stem.Length >= GluedStemMinLength && word.Length > GluedStemMinOffset &&
                word.IndexOf(stem, GluedStemMinOffset, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!word.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
            var extraLetters = word.Length - stem.Length;
            if (stem.Length <= 3 && extraLetters > 0) continue;
            if (stem.Length == 4 && extraLetters > 3) continue;
            return true;
        }
        return false;
    }

    private static SKRectI ExtendNameBoundsToTrailingWords(OcrLine line, Match match, SKRectI bounds)
    {
        var target = GetRedactionTarget(match);
        var targetEnd = target.Index + target.Length;

        var offset = 0;
        var pastTarget = false;

        foreach (var word in line.Words)
        {
            var wordEnd = offset + word.Text.Length;
            offset = wordEnd + 1;

            if (!pastTarget)
            {
                if (wordEnd >= targetEnd) pastTarget = true;
                continue;
            }

            if (!IsQualifyingNameWord(word.Text)) break;

            bounds = RectMath.Union(bounds, word.PixelBounds);
        }

        return bounds;
    }
}
