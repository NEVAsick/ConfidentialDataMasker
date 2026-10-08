using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConfidentialDataMasker.Models;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed class FeedbackLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _writeGate = new();
    private readonly string _folder;
    private readonly string _cropsFolder;
    private string MetadataPath => Path.Combine(_folder, "examples.jsonl");

    public FeedbackLogger()
    {
        _folder = AppDataPaths.TrainingData;
        _cropsFolder = Path.Combine(_folder, "crops");
    }

    public static string ComputeSourceDocumentId(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var bytes = Encoding.UTF8.GetBytes(fullPath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..12];
    }

    public void Log(RedactionFeedbackExample example, SKBitmap? pageBitmap)
    {
        try
        {
            Directory.CreateDirectory(_folder);

            if (pageBitmap is not null)
            {
                Directory.CreateDirectory(_cropsFolder);
                var cropFileName = example.Id + ".png";
                var cropPath = Path.Combine(_cropsFolder, cropFileName);
                if (SaveCrop(pageBitmap, example.NormalizedX, example.NormalizedY,
                        example.NormalizedWidth, example.NormalizedHeight, cropPath))
                {
                    example.CropFileName = cropFileName;
                }
            }

            var json = JsonSerializer.Serialize(example, JsonOptions);
            lock (_writeGate)
            {
                File.AppendAllText(MetadataPath, json + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Не удалось сохранить пример в {_folder}: {ex}");
        }
    }

    public List<RedactionFeedbackExample> LoadExamples()
    {
        var result = new List<RedactionFeedbackExample>();
        try
        {
            if (!File.Exists(MetadataPath)) return result;
            foreach (var line in File.ReadLines(MetadataPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var example = JsonSerializer.Deserialize<RedactionFeedbackExample>(line, JsonOptions);
                    if (example is not null) result.Add(example);
                }
                catch (JsonException ex)
                {
                    Trace.TraceWarning($"Пропущена повреждённая строка в {MetadataPath}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Не удалось прочитать {MetadataPath}: {ex}");
        }
        return result;
    }

    private static bool SaveCrop(SKBitmap bitmap, double x, double y, double width, double height, string outputPath)
    {
        var left = (int)Math.Round(x * bitmap.Width);
        var top = (int)Math.Round(y * bitmap.Height);
        var right = (int)Math.Round((x + width) * bitmap.Width);
        var bottom = (int)Math.Round((y + height) * bitmap.Height);

        left = Math.Clamp(left, 0, bitmap.Width);
        top = Math.Clamp(top, 0, bitmap.Height);
        right = Math.Clamp(right, 0, bitmap.Width);
        bottom = Math.Clamp(bottom, 0, bitmap.Height);

        var cropWidth = right - left;
        var cropHeight = bottom - top;
        if (cropWidth <= 0 || cropHeight <= 0) return false;

        using var cropBitmap = new SKBitmap(cropWidth, cropHeight, bitmap.ColorType, bitmap.AlphaType);
        using (var canvas = new SKCanvas(cropBitmap))
        {
            var srcRect = new SKRect(left, top, right, bottom);
            var destRect = new SKRect(0, 0, cropWidth, cropHeight);
            canvas.DrawBitmap(bitmap, srcRect, destRect);
        }

        using var skImage = SKImage.FromBitmap(cropBitmap);
        using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(outputPath);
        data.SaveTo(stream);
        return true;
    }
}
