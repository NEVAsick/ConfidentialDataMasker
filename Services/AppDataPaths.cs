using System;
using System.IO;

namespace ConfidentialDataMasker.Services;

public static class AppDataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConfidentialDataMasker");

    public static string Dictionary => Path.Combine(Root, "dictionary.txt");

    public static string VisionSettings => Path.Combine(Root, "vision.json");

    public static string TrainingData => Path.Combine(Root, "training_data");
}
