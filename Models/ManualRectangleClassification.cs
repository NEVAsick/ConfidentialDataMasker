namespace ConfidentialDataMasker.Models;

public enum ManualRedactionType
{
    Unspecified,
    PersonName,
    DocumentNumber,
    ContactInfo,
    Organization,
    Signature,
    Stamp,
    Other
}

public sealed record ManualRectangleClassification(RedactionRect Rect, ManualRedactionType Type);

public sealed record SuggestionClassification(SuggestedRect Suggestion, ManualRedactionType Type);
