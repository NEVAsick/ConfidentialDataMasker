using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;
using Tesseract;

namespace ConfidentialDataMasker.Services;

public sealed class OcrWord
{
    public string Text { get; }
    public SKRectI PixelBounds { get; }
    public float Confidence { get; }

    public OcrWord(string text, SKRectI pixelBounds, float confidence)
    {
        Text = text;
        PixelBounds = pixelBounds;
        Confidence = confidence;
    }
}

public sealed class OcrLine
{

    public string Text { get; }

    public SKRectI PixelBounds { get; }

    public float Confidence { get; }

    public List<OcrWord> Words { get; }

    public OcrLine(string text, SKRectI pixelBounds, float confidence, List<OcrWord> words)
    {
        Text = text;
        PixelBounds = pixelBounds;
        Confidence = confidence;
        Words = words;
    }
}

public sealed class OcrService : IDisposable
{
    private TesseractEngine? _engine;

    private TesseractEngine GetEngine()
    {
        if (_engine is not null) return _engine;

        var tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        var missing = new List<string>();
        if (!Directory.Exists(tessDataPath))
        {
            missing.Add("вся папка tessdata отсутствует");
        }
        else
        {
            if (!File.Exists(Path.Combine(tessDataPath, "rus.traineddata"))) missing.Add("rus.traineddata");
            if (!File.Exists(Path.Combine(tessDataPath, "eng.traineddata"))) missing.Add("eng.traineddata");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Не найдены файлы языковых моделей Tesseract в папке \"{tessDataPath}\": " +
                $"{string.Join(", ", missing)}. Скачайте их (см. tessdata/README.txt) и пересоберите проект.");
        }

        _engine = new TesseractEngine(tessDataPath, "rus+eng", EngineMode.Default);
        return _engine;
    }

    public List<OcrLine> RecognizeLines(SKBitmap bitmap)
    {
        var engine = GetEngine();

        using var skImage = SKImage.FromBitmap(bitmap);
        using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
        using var pix = Pix.LoadFromMemory(data.ToArray());
        pix.XRes = 200;
        pix.YRes = 200;
        using var page = engine.Process(pix);

        var lineBounds = new List<(SKRectI Bounds, float Confidence)>();
        using (var lineIter = page.GetIterator())
        {
            lineIter.Begin();
            do
            {
                if (!lineIter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var rect)) continue;
                var confidence = lineIter.GetConfidence(PageIteratorLevel.TextLine);
                lineBounds.Add((new SKRectI(rect.X1, rect.Y1, rect.X2, rect.Y2), confidence));
            } while (lineIter.Next(PageIteratorLevel.TextLine));
        }

        var words = new List<OcrWord>();
        using (var wordIter = page.GetIterator())
        {
            wordIter.Begin();
            do
            {
                if (!wordIter.TryGetBoundingBox(PageIteratorLevel.Word, out var rect)) continue;
                var text = wordIter.GetText(PageIteratorLevel.Word)?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                var confidence = wordIter.GetConfidence(PageIteratorLevel.Word);
                words.Add(new OcrWord(text, new SKRectI(rect.X1, rect.Y1, rect.X2, rect.Y2), confidence));
            } while (wordIter.Next(PageIteratorLevel.Word));
        }

        var result = new List<OcrLine>();
        var assignedWords = new HashSet<OcrWord>();

        foreach (var (bounds, lineConfidence) in lineBounds)
        {
            var lineWords = words
                .Where(w => !assignedWords.Contains(w)
                            && VerticalCenterInside(w.PixelBounds, bounds)
                            && HorizontalOverlaps(w.PixelBounds, bounds))
                .OrderBy(w => w.PixelBounds.Left)
                .ToList();

            if (lineWords.Count == 0) continue;

            foreach (var w in lineWords) assignedWords.Add(w);

            var text = string.Join(" ", lineWords.Select(w => w.Text));
            result.Add(new OcrLine(text, bounds, lineConfidence, lineWords));
        }

        foreach (var w in words)
        {
            if (assignedWords.Contains(w)) continue;
            result.Add(new OcrLine(w.Text, w.PixelBounds, w.Confidence, new List<OcrWord> { w }));
        }

        return result;
    }

    private static bool VerticalCenterInside(SKRectI word, SKRectI line)
    {
        var centerY = (word.Top + word.Bottom) / 2.0;
        return centerY >= line.Top && centerY <= line.Bottom;
    }

    private static bool HorizontalOverlaps(SKRectI word, SKRectI line)
    {
        return word.Left < line.Right && line.Left < word.Right;
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
    }
}
