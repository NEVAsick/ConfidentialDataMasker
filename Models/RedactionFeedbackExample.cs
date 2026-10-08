using System;

namespace ConfidentialDataMasker.Models;

public enum FeedbackEventType
{
    SuggestionAccepted,
    SuggestionRejected,
    ManualAddition
}

public sealed class RedactionFeedbackExample
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public FeedbackEventType EventType { get; set; }

    public SuggestionKind? Kind { get; set; }
    public string? Label { get; set; }
    public ManualRedactionType? ManualType { get; set; }

    public bool Implicit { get; set; }

    public string? OverlappedSuggestionLabel { get; set; }

    public string? MatchedText { get; set; }
    public string? LineText { get; set; }

    public double NormalizedX { get; set; }
    public double NormalizedY { get; set; }
    public double NormalizedWidth { get; set; }
    public double NormalizedHeight { get; set; }

    public double? Aspect { get; set; }
    public double? FillRatio { get; set; }
    public int? MaxColumnThicknessBlocks { get; set; }
    public int? RectWidthPx { get; set; }
    public int? RectHeightPx { get; set; }

    public string SourceDocumentId { get; set; } = "";
    public int PageIndex { get; set; }

    public string? CropFileName { get; set; }
}
