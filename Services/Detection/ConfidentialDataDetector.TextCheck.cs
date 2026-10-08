using System;
using System.Collections.Generic;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed partial class ConfidentialDataDetector
{
    private const int SyntheticCharWidthPx = 20;
    private const int SyntheticGapPx = 15;
    private const int SyntheticHeightPx = 30;
    private const float SyntheticConfidence = 90f;

    public bool ContainsConfidentialText(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        var words = new List<OcrWord>(tokens.Length);
        var x = 0;
        foreach (var token in tokens)
        {
            var width = SyntheticCharWidthPx * token.Length;
            words.Add(new OcrWord(token, new SKRectI(x, 0, x + width, SyntheticHeightPx), SyntheticConfidence));
            x += width + SyntheticGapPx;
        }

        var line = new OcrLine(string.Join(" ", tokens), new SKRectI(0, 0, x, SyntheticHeightPx), SyntheticConfidence, words);
        var dictionaryPatterns = _dictionary?.GetPatterns() ?? NoDictionaryPatterns;
        return ClassifyAll(line, dictionaryPatterns).Count > 0;
    }
}
