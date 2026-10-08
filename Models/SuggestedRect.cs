namespace ConfidentialDataMasker.Models;

public enum SuggestionKind
{

    Text,

    Stamp,

    Signature
}

public sealed class SuggestedRect
{
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public SuggestionKind Kind { get; }
    public string Label { get; }

    public double? Aspect { get; }
    public double? FillRatio { get; }
    public int? MaxColumnThicknessBlocks { get; }
    public int? RectWidthPx { get; }
    public int? RectHeightPx { get; }

    public string? MatchedText { get; }
    public string? LineText { get; }

    public SuggestedRect(double x, double y, double width, double height, SuggestionKind kind, string label)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Kind = kind;
        Label = label;
    }

    public SuggestedRect(
        double x, double y, double width, double height, SuggestionKind kind, string label,
        double aspect, double fillRatio, int maxColumnThicknessBlocks, int rectWidthPx, int rectHeightPx)
        : this(x, y, width, height, kind, label)
    {
        Aspect = aspect;
        FillRatio = fillRatio;
        MaxColumnThicknessBlocks = maxColumnThicknessBlocks;
        RectWidthPx = rectWidthPx;
        RectHeightPx = rectHeightPx;

    }

    public SuggestedRect(
        double x, double y, double width, double height, SuggestionKind kind, string label,
        string? matchedText, string? lineText)
        : this(x, y, width, height, kind, label)
    {
        MatchedText = matchedText;
        LineText = lineText;
    }

    public RedactionRect ToRedactionRect() => new(X, Y, Width, Height);
}
