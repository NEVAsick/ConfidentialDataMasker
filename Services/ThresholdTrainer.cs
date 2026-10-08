using System;
using System.Collections.Generic;
using System.Linq;
using ConfidentialDataMasker.Models;

namespace ConfidentialDataMasker.Services;

public static class ThresholdTrainer
{
    private const int MinExamplesPerClass = 4;

    private sealed record Decision(bool Accepted, double Aspect, double FillRatio, int ColumnThickness, int WidthPx, int HeightPx);

    public static (DetectorThresholds Thresholds, IReadOnlyList<string> Changes) Train(
        IEnumerable<RedactionFeedbackExample> examples, DetectorThresholds current)
    {
        var decisions = examples
            .Where(e => !e.Implicit && e.Aspect is not null)
            .Where(e => e.EventType is FeedbackEventType.SuggestionAccepted or FeedbackEventType.SuggestionRejected)
            .ToList();
        var signature = ToDecisions(decisions, SuggestionKind.Signature);
        var stamp = ToDecisions(decisions, SuggestionKind.Stamp);

        var result = Clone(current);
        var changes = new List<string>();

        ApplyLower(signature, d => d.Aspect, current.SignatureAspectMin,
            v => result.SignatureAspectMin = v, "форма подписи", changes);
        ApplyLower(signature, d => d.WidthPx, current.SignatureMinWidthPx,
            v => result.SignatureMinWidthPx = (int)Math.Round(v), "минимальная ширина подписи", changes);
        ApplyLower(signature, d => d.HeightPx, current.SignatureMinHeightPx,
            v => result.SignatureMinHeightPx = (int)Math.Round(v), "минимальная высота подписи", changes);
        ApplyLower(signature, d => d.ColumnThickness, current.SignatureMinColumnThicknessBlocks,
            v => result.SignatureMinColumnThicknessBlocks = (int)Math.Round(v), "толщина штриха подписи", changes);
        ApplyUpper(signature, d => d.FillRatio, current.SignatureMaxFillRatio,
            v => result.SignatureMaxFillRatio = v, "заполненность рамки подписи", changes);

        ApplyLower(stamp, d => d.Aspect, current.StampAspectMin,
            v => result.StampAspectMin = v, "мин. соотношение сторон печати", changes);
        ApplyUpper(stamp, d => d.Aspect, current.StampAspectMax,
            v => result.StampAspectMax = v, "макс. соотношение сторон печати", changes);

        return (result, changes);
    }

    private static List<Decision> ToDecisions(List<RedactionFeedbackExample> examples, SuggestionKind kind) =>
        examples
            .Where(e => e.Kind == kind)
            .Select(e => new Decision(
                e.EventType == FeedbackEventType.SuggestionAccepted,
                e.Aspect ?? 0,
                e.FillRatio ?? 0,
                e.MaxColumnThicknessBlocks ?? 0,
                e.RectWidthPx ?? 0,
                e.RectHeightPx ?? 0))
            .ToList();

    private static DetectorThresholds Clone(DetectorThresholds t) => new()
    {
        StampAspectMin = t.StampAspectMin,
        StampAspectMax = t.StampAspectMax,
        SignatureAspectMin = t.SignatureAspectMin,
        SignatureMinWidthPx = t.SignatureMinWidthPx,
        SignatureMinHeightPx = t.SignatureMinHeightPx,
        SignatureMinColumnThicknessBlocks = t.SignatureMinColumnThicknessBlocks,
        SignatureMaxFillRatio = t.SignatureMaxFillRatio,
    };

    private static void ApplyLower(
        List<Decision> data, Func<Decision, double> feature, double currentValue,
        Action<double> setter, string name, List<string> changes)
    {
        var value = BestSplit(data, feature, currentValue, lower: true);
        Apply(value, currentValue, setter, name, changes);
    }

    private static void ApplyUpper(
        List<Decision> data, Func<Decision, double> feature, double currentValue,
        Action<double> setter, string name, List<string> changes)
    {
        var value = BestSplit(data, feature, currentValue, lower: false);
        Apply(value, currentValue, setter, name, changes);
    }

    private static void Apply(double? value, double currentValue, Action<double> setter, string name, List<string> changes)
    {
        if (value is null || Math.Abs(value.Value - currentValue) < 0.001) return;
        setter(value.Value);
        changes.Add($"{name}: {currentValue:0.##} → {value.Value:0.##}");
    }

    private static double? BestSplit(
        List<Decision> data, Func<Decision, double> feature, double currentDefault, bool lower)
    {
        var accepted = data.Where(d => d.Accepted).Select(feature).ToList();
        var rejected = data.Where(d => !d.Accepted).Select(feature).ToList();
        if (accepted.Count < MinExamplesPerClass || rejected.Count < MinExamplesPerClass) return null;

        var candidates = data.Select(feature).Distinct().OrderBy(v => v).ToList();
        var bestThreshold = currentDefault;
        var bestScore = -1;

        foreach (var threshold in candidates)
        {
            var score = lower
                ? accepted.Count(v => v >= threshold) + rejected.Count(v => v < threshold)
                : accepted.Count(v => v <= threshold) + rejected.Count(v => v > threshold);

            var closerToDefault = Math.Abs(threshold - currentDefault) < Math.Abs(bestThreshold - currentDefault);
            if (score > bestScore || (score == bestScore && closerToDefault))
            {
                bestScore = score;
                bestThreshold = threshold;
            }
        }

        return lower ? Math.Min(bestThreshold, accepted.Min()) : Math.Max(bestThreshold, accepted.Max());
    }
}
