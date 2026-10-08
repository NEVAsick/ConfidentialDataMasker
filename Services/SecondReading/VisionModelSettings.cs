using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ConfidentialDataMasker.Services;

public sealed class VisionModelSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3.5:4b";
    public int TimeoutSeconds { get; set; } = 600;
    public int MaxRegionsPerPage { get; set; } = 12;
    public int MaxNewLinesPerRegion { get; set; } = 6;

    public static string DefaultPath => AppDataPaths.VisionSettings;

    public static VisionModelSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<VisionModelSettings>(File.ReadAllText(path)) ?? new VisionModelSettings();
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Не удалось прочитать настройки модели {path}: {ex.Message}");
        }
        return new VisionModelSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Не удалось сохранить настройки модели {path}: {ex.Message}");
        }
    }
}
