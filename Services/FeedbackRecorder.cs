using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ConfidentialDataMasker.Models;

namespace ConfidentialDataMasker.Services;

public sealed class FeedbackRecorder
{
    private const int CropDpi = 150;
    private const double ManualOverlapWithSuggestion = 0.5;

    private readonly PdfRenderService _renderService;
    private readonly FeedbackLogger _logger;
    private readonly HashSet<SuggestedRect> _implicitlyLogged = new();

    public FeedbackRecorder(PdfRenderService renderService, FeedbackLogger logger)
    {
        _renderService = renderService;
        _logger = logger;
    }

    public void RecordSuggestionDecision(
        PdfQueueItem item, int pageIndex, SuggestedRect suggestion, FeedbackEventType eventType,
        ManualRedactionType? correctedType = null)
    {
        var example = BuildExample(
            suggestion, null, pageIndex, FeedbackLogger.ComputeSourceDocumentId(item.FilePath),
            eventType, correctedType, null);
        _ = LogWithCropAsync(example, item.FilePath, pageIndex);
    }

    public void RecordManualRectangle(
        PdfQueueItem item, int pageIndex, ManualRectangleClassification classification,
        IEnumerable<SuggestedRect> pageSuggestions)
    {
        var overlapped = pageSuggestions.FirstOrDefault(
            s => OverlapOfSmaller(s, classification.Rect) >= ManualOverlapWithSuggestion);
        var example = BuildExample(
            null, classification.Rect, pageIndex, FeedbackLogger.ComputeSourceDocumentId(item.FilePath),
            FeedbackEventType.ManualAddition, classification.Type, overlapped?.Label);
        _ = LogWithCropAsync(example, item.FilePath, pageIndex);
    }

    public async Task RecordImplicitAcceptancesAsync(PdfQueueItem item)
    {
        try
        {
            var sourceDocumentId = FeedbackLogger.ComputeSourceDocumentId(item.FilePath);
            for (var pageIndex = 0; pageIndex < item.PageCount; pageIndex++)
            {
                if (!item.TryGetCachedSuggestions(pageIndex, out var pending)) continue;
                var rects = item.GetRectsForPage(pageIndex);
                var fresh = pending.Where(s => !rects.Any(r => r.Intersects(s)) && _implicitlyLogged.Add(s)).ToList();
                if (fresh.Count == 0) continue;

                using var bitmap = await _renderService.RenderPageAsync(item.FilePath, pageIndex, CropDpi);
                foreach (var suggestion in fresh)
                {
                    var example = BuildExample(
                        suggestion, null, pageIndex, sourceDocumentId,
                        FeedbackEventType.SuggestionAccepted, null, null);
                    example.Implicit = true;
                    _logger.Log(example, bitmap);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Не удалось записать неявно принятые предложения: {ex}");
        }
    }

    private async Task LogWithCropAsync(RedactionFeedbackExample example, string filePath, int pageIndex)
    {
        try
        {
            using var bitmap = await _renderService.RenderPageAsync(filePath, pageIndex, CropDpi);
            _logger.Log(example, bitmap);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Не удалось записать решение пользователя: {ex}");
        }
    }

    private static RedactionFeedbackExample BuildExample(
        SuggestedRect? suggestion, RedactionRect? manualRect, int pageIndex, string sourceDocumentId,
        FeedbackEventType eventType, ManualRedactionType? manualType, string? overlappedSuggestionLabel)
    {
        var (x, y, w, h) = suggestion is not null
            ? (suggestion.X, suggestion.Y, suggestion.Width, suggestion.Height)
            : (manualRect!.X, manualRect.Y, manualRect.Width, manualRect.Height);

        return new RedactionFeedbackExample
        {
            EventType = eventType,
            Kind = suggestion?.Kind,
            Label = suggestion?.Label,
            ManualType = manualType,
            MatchedText = suggestion?.MatchedText,
            LineText = suggestion?.LineText,
            NormalizedX = x,
            NormalizedY = y,
            NormalizedWidth = w,
            NormalizedHeight = h,
            Aspect = suggestion?.Aspect,
            FillRatio = suggestion?.FillRatio,
            MaxColumnThicknessBlocks = suggestion?.MaxColumnThicknessBlocks,
            RectWidthPx = suggestion?.RectWidthPx,
            RectHeightPx = suggestion?.RectHeightPx,
            SourceDocumentId = sourceDocumentId,
            PageIndex = pageIndex,
            OverlappedSuggestionLabel = overlappedSuggestionLabel,
        };
    }

    private static double OverlapOfSmaller(SuggestedRect s, RedactionRect r)
    {
        var w = Math.Min(s.X + s.Width, r.X + r.Width) - Math.Max(s.X, r.X);
        var h = Math.Min(s.Y + s.Height, r.Y + r.Height) - Math.Max(s.Y, r.Y);
        if (w <= 0 || h <= 0) return 0;
        var smaller = Math.Min(s.Width * s.Height, r.Width * r.Height);
        return smaller <= 0 ? 0 : w * h / smaller;
    }
}
