using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ConfidentialDataMasker.Services;

public sealed record RedactionTerm(string Label, string Text);

public interface IRedactionTermSource
{
    IReadOnlyList<RedactionTerm> LoadTerms();
}

public sealed class TextFileTermSource : IRedactionTermSource
{
    private static readonly IReadOnlyList<RedactionTerm> Empty = Array.Empty<RedactionTerm>();

    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private IReadOnlyList<RedactionTerm> _cached = Empty;

    public string FilePath { get; }

    public TextFileTermSource(string filePath)
    {
        FilePath = filePath;
    }

    public IReadOnlyList<RedactionTerm> LoadTerms()
    {
        if (!File.Exists(FilePath))
        {
            _loadedWriteTimeUtc = DateTime.MinValue;
            _cached = Empty;
            return _cached;
        }

        var writeTimeUtc = File.GetLastWriteTimeUtc(FilePath);
        if (writeTimeUtc == _loadedWriteTimeUtc) return _cached;

        var result = new List<RedactionTerm>();
        var section = "Организация";
        foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            result.Add(new RedactionTerm(section, line));
        }

        _cached = result;
        _loadedWriteTimeUtc = writeTimeUtc;
        return _cached;
    }

    public void EnsureExists(string defaultContent)
    {
        if (File.Exists(FilePath)) return;
        var folder = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath, defaultContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}

public sealed class RedactionDictionary
{
    private const string TokenChar = @"[^\s,;:()«»""'<>]";
    private const string WordSeparator = @"[\s\-«»""']+";

    private readonly IRedactionTermSource _source;
    private readonly object _gate = new();
    private IReadOnlyList<RedactionTerm>? _builtFrom;
    private IReadOnlyList<(Regex Pattern, string Label)> _patterns = Array.Empty<(Regex Pattern, string Label)>();

    public RedactionDictionary(IRedactionTermSource source)
    {
        _source = source;
    }

    public IReadOnlyList<(Regex Pattern, string Label)> GetPatterns()
    {
        lock (_gate)
        {
            IReadOnlyList<RedactionTerm> terms;
            try
            {
                terms = _source.LoadTerms();
            }
            catch
            {
                return _patterns;
            }

            if (ReferenceEquals(terms, _builtFrom)) return _patterns;
            _patterns = Build(terms);
            _builtFrom = terms;
            return _patterns;
        }
    }

    private static IReadOnlyList<(Regex Pattern, string Label)> Build(IReadOnlyList<RedactionTerm> terms)
    {
        var result = new List<(Regex Pattern, string Label)>();
        foreach (var group in terms.GroupBy(t => t.Label))
        {
            var alternatives = group
                .Select(t => TermToPattern(t.Text))
                .OfType<string>()
                .Distinct()
                .OrderByDescending(p => p.Length)
                .ToList();
            if (alternatives.Count == 0) continue;

            var regex = new Regex(
                "(?:" + string.Join("|", alternatives) + ")",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            result.Add((regex, group.Key + "?"));
        }
        return result;
    }

    private static string? TermToPattern(string term)
    {
        var words = term
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('«', '»', '"', '\''))
            .Where(w => w.Length > 0)
            .ToList();
        if (words.Count == 0) return null;

        if (words.Count == 1 && (words[0].Contains('.') || words[0].Contains('@')))
        {
            return TokenChar + "*" + Regex.Escape(words[0]) + TokenChar + "*";
        }

        return @"(?<![\p{L}\d])" + string.Join(WordSeparator, words.Select(WordToPattern)) + @"(?![\p{L}\d])";
    }

    private static string WordToPattern(string word)
    {
        if (word.Length < 5 || !word.All(char.IsLetter)) return Regex.Escape(word);
        return Regex.Escape(word[..^2]) + @"\p{L}{0,4}";
    }

    public const string DefaultFileContent =
@"# Словарь для закрашивания: названия и значения, которые нужно закрывать всегда.
#
# Как заполнять:
#   [Раздел] — тип, под которым система будет показывать находки, например [Организация].
#   Ниже — по одному названию на строку. Большие и маленькие буквы не важны.
#   Слова из 5 и более букв находятся и с другими окончаниями: ""Норникель"" найдёт ""Норникеля"".
#   Если в записи есть точка или @ (домен, почта), закрашивается всё слово целиком:
#   ""nornik.ru"" закроет и ""ssc.nornik.ru"", и ""5911@nornik.ru"".
#   Строки, начинающиеся с #, — комментарии, их система не читает.
#
# Не добавляйте сюда обычные слова (например, просто ""Спутник"") — они будут закрываться
# в любом тексте. Лучше полное название: ""Норникель Спутник"".
#
# После сохранения этого файла открытый документ проверяется заново сам — перезапускать программу не нужно.

[Организация]
Норникель
Норникель Спутник
Норильский никель
ГМК «Норильский никель»

[Сайт]
nornik.ru
";
}
