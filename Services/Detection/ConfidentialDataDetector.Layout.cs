using System;
using System.Collections.Generic;

namespace ConfidentialDataMasker.Services;

public sealed partial class ConfidentialDataDetector
{
    private static OcrLine? FindLineToTheRight(OcrLine labelLine, List<OcrLine> lines)
    {
        var labelCenterY = (labelLine.PixelBounds.Top + labelLine.PixelBounds.Bottom) / 2.0;
        var labelHeight = Math.Max(1, labelLine.PixelBounds.Bottom - labelLine.PixelBounds.Top);

        OcrLine? best = null;
        var bestLeft = int.MaxValue;

        foreach (var candidate in lines)
        {
            if (ReferenceEquals(candidate, labelLine)) continue;
            if (candidate.PixelBounds.Left <= labelLine.PixelBounds.Right) continue;

            var candidateCenterY = (candidate.PixelBounds.Top + candidate.PixelBounds.Bottom) / 2.0;
            if (Math.Abs(candidateCenterY - labelCenterY) > labelHeight) continue;

            if (candidate.PixelBounds.Left < bestLeft)
            {
                bestLeft = candidate.PixelBounds.Left;
                best = candidate;
            }
        }

        return best;
    }

    private const double LabelValueMaxGapToHeight = 2.0;

    private static OcrLine? FindLineDirectlyBelow(OcrLine anchor, List<OcrLine> lines, Func<OcrLine, bool> isAcceptable, double maxGapToHeight = 1.0)
    {
        var anchorHeight = Math.Max(1, anchor.PixelBounds.Bottom - anchor.PixelBounds.Top);
        const int leftTolerancePx = 15;

        OcrLine? best = null;
        var bestGap = int.MaxValue;

        foreach (var candidate in lines)
        {
            if (ReferenceEquals(candidate, anchor)) continue;

            var verticalGap = candidate.PixelBounds.Top - anchor.PixelBounds.Bottom;
            if (verticalGap < 0 || verticalGap > maxGapToHeight * anchorHeight) continue;
            if (Math.Abs(candidate.PixelBounds.Left - anchor.PixelBounds.Left) > leftTolerancePx) continue;
            if (!isAcceptable(candidate)) continue;

            if (verticalGap < bestGap)
            {
                bestGap = verticalGap;
                best = candidate;
            }
        }

        return best;
    }

    private static bool LooksLikeWrappedFieldValue(OcrLine candidate)
    {
        var text = candidate.Text;
        if (text.Contains(':')) return false;
        if (GetStandaloneLabel(text) is not null) return false;
        return ClassifyText(candidate) is null;
    }

    private const double ParagraphIndentToHeightRatio = 4.0;
    private const double FirstLineIndentToHeightRatio = 6.0;

    private static OcrLine? FindParagraphContinuation(OcrLine line, List<OcrLine> lines)
    {
        var height = Math.Max(1, line.PixelBounds.Height);
        var recognized = IsRecognized(line);
        var lineBottom = recognized ? line.Words[0].PixelBounds.Bottom : line.PixelBounds.Bottom;
        OcrLine? best = null;
        var bestGap = int.MaxValue;
        foreach (var candidate in lines)
        {
            if (ReferenceEquals(candidate, line)) continue;
            var candidateTop = recognized && IsRecognized(candidate)
                ? candidate.Words[0].PixelBounds.Top
                : candidate.PixelBounds.Top;
            var gap = candidateTop - lineBottom;
            if (gap < -height / 4 || gap > height) continue;
            var shift = candidate.PixelBounds.Left - line.PixelBounds.Left;
            if (shift > ParagraphIndentToHeightRatio * height) continue;
            if (-shift > FirstLineIndentToHeightRatio * height) continue;
            if (candidate.PixelBounds.Left >= line.PixelBounds.Right) continue;
            if (gap < bestGap)
            {
                bestGap = gap;
                best = candidate;
            }
        }
        return best;
    }

    private static bool IsRecognized(OcrLine line) =>
        line.Confidence < OcrWordText.TextLayerConfidence && line.Words.Count > 0;

    private static bool HasLeftNeighbor(OcrLine line, List<OcrLine> lines)
    {
        var height = Math.Max(1, line.PixelBounds.Height);
        var centerY = (line.PixelBounds.Top + line.PixelBounds.Bottom) / 2.0;
        foreach (var candidate in lines)
        {
            if (ReferenceEquals(candidate, line)) continue;
            var candidateCenterY = (candidate.PixelBounds.Top + candidate.PixelBounds.Bottom) / 2.0;
            if (Math.Abs(candidateCenterY - centerY) > height / 2.0) continue;
            var gap = line.PixelBounds.Left - candidate.PixelBounds.Right;
            if (gap >= 0 && gap <= ParagraphIndentToHeightRatio * height) return true;
        }
        return false;
    }
}
