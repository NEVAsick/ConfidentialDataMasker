using System;
using System.Collections.Generic;
using System.Linq;
using ConfidentialDataMasker.Models;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

internal sealed class StampAndSignatureDetector
{
    private enum BlockClass { Background, Ink, Color }

    private const int BlockSize = 12;
    private const int InkMaxLuma = 150;
    private const int FaintLineMaxLuma = 185;
    private const int InkMaxSaturation = 45;
    private const int ColorMinSaturation = 45;
    private const int ColorMaxLuma = 215;
    private const double MinInkFractionPerBlock = 0.04;
    private const int BlobLinkRadiusBlocks = 2;
    private const int MinBlobBlocks = 3;
    private const double MaxBlobWidthFraction = 0.45;
    private const double MaxStampHeightFraction = 0.3;
    private const double MaxSignatureHeightFraction = 0.15;
    private const double MinInkStampSideFraction = 0.025;
    private const int MinInkStampBlocks = 8;
    private const double MinColoredShare = 0.5;
    private const int MinMaskedWordConfidence = 30;
    private const double MaskWordMaxHeightToMedian = 1.8;
    private const double MaskLongWordMaxHeightToMedian = 2.5;
    private const double MaskTallWordMaxHeightToMedian = 3.0;
    private const float MinTallWordConfidence = 60f;
    private const float MinWordConfidenceInWeakLine = 80f;
    private const int MaskLongWordMinChars = 3;
    private const double MaskVerticalExpand = 0.3;
    private const double MaskHorizontalExpand = 0.15;
    private const double LongRuleWidthFraction = 0.3;
    private const double ShortRuleWidthFraction = 0.08;
    private const double LongVerticalRuleFraction = 0.21;
    private const double ShortVerticalRuleFraction = 0.07;
    private const int RuleWindowBlocks = 2;
    private const int SignatureMergeGapBlocks = 2;
    private const double MaxStraightStrokeSpreadPx = 2.5;
    private const int TextFringePx = 3;
    private const double MaxTextFringeShare = 0.6;
    private const double ShadowInkToPaper = 0.75;
    private const int MinStrokePixels = 10;
    private const double SideMarginFraction = 0.07;
    private const int MaxSmallColorSpotBlocks = 8;
    private const double MaxFrameRemnantDensity = 0.035;
    private const double MinFrameRemnantStraightShare = 0.8;
    private const int FrameRunMinLength = 24;
    private const int FrameRunMaxGap = 1;
    private const double StampGrowFraction = 0.5;
    private const int StampGrowCellPx = 4;
    private const int StampGrowCellMinPixels = 2;
    private const int StampGrowMinSeedCells = 6;

    private sealed record StampBlob(SKRectI Rect, double Aspect, double Fill, int Thickness, int Width, int Height);
    private const double TopMarginFraction = 0.03;

    private readonly DetectorThresholds _thresholds;

    public StampAndSignatureDetector(DetectorThresholds thresholds)
    {
        _thresholds = thresholds;
    }

    public List<SuggestedRect> Detect(SKBitmap bitmap, List<OcrLine> lines)
    {
        var result = new List<SuggestedRect>();
        var width = bitmap.Width;
        var height = bitmap.Height;
        var gridW = width / BlockSize;
        var gridH = height / BlockSize;
        if (gridW < 3 || gridH < 3) return result;

        var pixels = bitmap.Pixels;
        var textMask = BuildTextMask(lines, width, height);
        var paper = LightingCorrection.PaperBrightnessIfUneven(pixels, width, height);
        var (color, ink, faintInk) = ClassifyPixels(pixels, paper);
        var dark = new bool[pixels.Length];
        var visible = new bool[pixels.Length];
        var faintDark = new bool[pixels.Length];
        var faintVisible = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            dark[i] = color[i] || ink[i];
            visible[i] = dark[i] && !textMask[i];
            faintDark[i] = color[i] || faintInk[i];
            faintVisible[i] = faintDark[i] && !textMask[i];
        }
        var gridLines = TableGridLines.Find(dark, visible, faintDark, faintVisible, width, height);
        var marks = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++) marks[i] = visible[i] && !gridLines[i];

        var (classes, blue) = ClassifyBlocks(pixels, color, marks, width, gridW, gridH);
        classes = RemoveTableAndUnderlineRules(classes);
        var nearText = Dilate(textMask, width, height, TextFringePx);

        var visited = new bool[gridW, gridH];
        var signatures = new List<(SKRectI Rect, double Fill, int Thickness)>();
        var coloredStamps = new List<StampBlob>();

        for (var by = 0; by < gridH; by++)
        {
            for (var bx = 0; bx < gridW; bx++)
            {
                if (visited[bx, by] || classes[bx, by] == BlockClass.Background) continue;

                var blocks = FloodFill(classes, visited, bx, by, gridW, gridH);
                if (blocks.Count < MinBlobBlocks) continue;

                var minBx = blocks.Min(p => p.X);
                var maxBx = blocks.Max(p => p.X);
                var minBy = blocks.Min(p => p.Y);
                var maxBy = blocks.Max(p => p.Y);
                var touchesPageEdge = minBx == 0 || minBy == 0 || maxBx == gridW - 1 || maxBy == gridH - 1;
                if (touchesPageEdge) continue;

                var pxRect = new SKRectI(
                    minBx * BlockSize,
                    minBy * BlockSize,
                    Math.Min(width, (maxBx + 1) * BlockSize),
                    Math.Min(height, (maxBy + 1) * BlockSize));
                var rectWidth = pxRect.Width;
                var rectHeight = Math.Max(1, pxRect.Height);
                if (rectWidth > MaxBlobWidthFraction * width) continue;
                if (IsInPageMargin(pxRect, width, height)) continue;

                var aspect = (double)rectWidth / rectHeight;
                var maxColumnThickness = blocks.GroupBy(p => p.X).Max(g => g.Count());
                var fillRatio = (double)blocks.Count / ((maxBx - minBx + 1) * (maxBy - minBy + 1));
                var coloredShare = (double)blocks.Count(p => classes[p.X, p.Y] == BlockClass.Color) / blocks.Count;
                var mostlyBlue = blocks.Count(p => blue[p.X, p.Y]) * 2 >= blocks.Count;

                if (TextFringeShare(blocks, marks, nearText, width) >= MaxTextFringeShare) continue;

                var stampShaped = aspect >= _thresholds.StampAspectMin && aspect <= _thresholds.StampAspectMax &&
                    rectHeight <= MaxStampHeightFraction * height;
                var largeInkMark = rectWidth >= MinInkStampSideFraction * width &&
                    rectHeight >= MinInkStampSideFraction * width &&
                    blocks.Count >= MinInkStampBlocks;
                if (stampShaped && (coloredShare >= MinColoredShare || largeInkMark))
                {
                    if (coloredShare >= MinColoredShare && !mostlyBlue && blocks.Count <= MaxSmallColorSpotBlocks) continue;
                    if (coloredShare < MinColoredShare && IsTableFrameRemnant(blocks, marks, width, pxRect)) continue;
                    if (coloredShare >= MinColoredShare)
                    {
                        coloredStamps.Add(new StampBlob(
                            pxRect, aspect, fillRatio, maxColumnThickness, rectWidth, rectHeight));
                        continue;
                    }
                    result.Add(SuggestionFactory.Create(
                        bitmap, pxRect, SuggestionKind.Stamp, "Печать/штамп?",
                        aspect, fillRatio, maxColumnThickness, rectWidth, rectHeight));
                    continue;
                }

                if (coloredShare >= MinColoredShare && !mostlyBlue) continue;
                if (!LooksLikeSignature(aspect, rectWidth, rectHeight, height, maxColumnThickness, fillRatio)) continue;
                if (IsStraightStroke(blocks, marks, width)) continue;

                signatures.Add((pxRect, fillRatio, maxColumnThickness));
            }
        }

        foreach (var stamp in GrowColoredStamps(coloredStamps, color, width, height))
        {
            result.Add(SuggestionFactory.Create(
                bitmap, stamp.Rect, SuggestionKind.Stamp, "Печать/штамп?",
                stamp.Aspect, stamp.Fill, stamp.Thickness, stamp.Width, stamp.Height));
        }

        foreach (var (rect, fill, thickness) in MergeNearby(signatures))
        {
            var rectHeight = Math.Max(1, rect.Height);
            result.Add(SuggestionFactory.Create(
                bitmap, rect, SuggestionKind.Signature, "Подпись?",
                (double)rect.Width / rectHeight, fill, thickness, rect.Width, rectHeight));
        }

        return result;
    }

    private static bool IsInPageMargin(SKRectI rect, int pageWidth, int pageHeight)
    {
        var centerX = (rect.Left + rect.Right) / 2.0;
        var centerY = (rect.Top + rect.Bottom) / 2.0;
        return centerX < SideMarginFraction * pageWidth ||
            centerX > (1 - SideMarginFraction) * pageWidth ||
            centerY < TopMarginFraction * pageHeight;
    }

    private bool LooksLikeSignature(
        double aspect, int rectWidth, int rectHeight, int pageHeight, int maxColumnThickness, double fillRatio)
    {
        return aspect >= _thresholds.SignatureAspectMin &&
            rectWidth >= _thresholds.SignatureMinWidthPx &&
            rectHeight >= _thresholds.SignatureMinHeightPx &&
            rectHeight <= MaxSignatureHeightFraction * pageHeight &&
            maxColumnThickness >= _thresholds.SignatureMinColumnThicknessBlocks &&
            fillRatio <= _thresholds.SignatureMaxFillRatio;
    }

    private static BlockClass[,] RemoveTableAndUnderlineRules(BlockClass[,] classes)
    {
        classes = RemoveRules(classes, LongRuleWidthFraction, RuleWindowBlocks, 2);
        classes = RemoveRules(classes, ShortRuleWidthFraction, 0, 1);
        classes = Transpose(RemoveRules(Transpose(classes), LongVerticalRuleFraction, RuleWindowBlocks, 2));
        classes = Transpose(RemoveRules(Transpose(classes), ShortVerticalRuleFraction, 0, 1));
        return classes;
    }

    private static bool[] BuildTextMask(List<OcrLine> lines, int width, int height)
    {
        var mask = new bool[width * height];
        var heights = lines
            .SelectMany(l => l.Words)
            .Where(w => w.Text.Any(char.IsLetterOrDigit))
            .Select(w => w.PixelBounds.Height)
            .OrderBy(h => h)
            .ToList();
        if (heights.Count == 0) return mask;
        var median = Math.Max(1, heights[heights.Count / 2]);

        foreach (var line in lines)
        {
            var lowConfidenceLine = line.Confidence < MinMaskedWordConfidence;
            if (IsUnreadableOcrLine(line)) continue;
            foreach (var word in line.Words)
            {
                var typed = OcrWordText.LooksLikeTypedOrGluedTypedWords(word.Text);
                if (lowConfidenceLine && (!typed || word.Confidence < MinWordConfidenceInWeakLine)) continue;
                if (word.Confidence < MinMaskedWordConfidence && !typed) continue;
                var bounds = word.PixelBounds;
                var wordHeight = bounds.Height;
                var chars = word.Text.Count(char.IsLetterOrDigit);
                var regular = wordHeight <= MaskWordMaxHeightToMedian * median;
                var longWord = chars >= MaskLongWordMinChars && wordHeight <= MaskLongWordMaxHeightToMedian * median;
                var tallTitleWord = chars >= MaskLongWordMinChars && word.Confidence >= MinTallWordConfidence &&
                    wordHeight <= MaskTallWordMaxHeightToMedian * median && OcrWordText.LooksLikeRealWord(word.Text);
                if (!regular && !longWord && !tallTitleWord) continue;

                var expandY = (int)Math.Round(MaskVerticalExpand * wordHeight);
                var expandX = Math.Max(2, (int)Math.Round(MaskHorizontalExpand * wordHeight));
                var left = Math.Max(0, bounds.Left - expandX);
                var right = Math.Min(width, bounds.Right + expandX + 1);
                var top = Math.Max(0, bounds.Top - expandY);
                var bottom = Math.Min(height, bounds.Bottom + expandY + 1);
                for (var y = top; y < bottom; y++)
                {
                    Array.Fill(mask, true, y * width + left, Math.Max(0, right - left));
                }
            }
        }
        return mask;
    }

    private static bool IsUnreadableOcrLine(OcrLine line) =>
        line.Confidence < OcrWordText.TextLayerConfidence && !line.Words.Any(w => OcrWordText.LooksLikeRealWord(w.Text));

    private static (bool[] Color, bool[] Ink, bool[] FaintInk) ClassifyPixels(SKColor[] pixels, double[]? paper)
    {
        var color = new bool[pixels.Length];
        var ink = new bool[pixels.Length];
        var faintInk = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            int r = c.Red, g = c.Green, b = c.Blue;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var saturation = max - min;
            var luma = (299 * r + 587 * g + 114 * b) / 1000;
            if (saturation >= ColorMinSaturation && luma < ColorMaxLuma)
            {
                color[i] = true;
                continue;
            }
            if (saturation >= InkMaxSaturation) continue;
            faintInk[i] = luma < FaintLineMaxLuma;
            ink[i] = luma < InkMaxLuma && (paper is null || luma < ShadowInkToPaper * paper[i]);
        }
        return (color, ink, faintInk);
    }

    private static (BlockClass[,] Classes, bool[,] Blue) ClassifyBlocks(
        SKColor[] pixels, bool[] color, bool[] marks, int width, int gridW, int gridH)
    {
        var classes = new BlockClass[gridW, gridH];
        var blue = new bool[gridW, gridH];
        var minCount = (int)Math.Ceiling(MinInkFractionPerBlock * BlockSize * BlockSize);

        for (var by = 0; by < gridH; by++)
        {
            for (var bx = 0; bx < gridW; bx++)
            {
                int inkCount = 0, colorCount = 0, blueCount = 0;
                for (var y = by * BlockSize; y < (by + 1) * BlockSize; y++)
                {
                    var row = y * width;
                    for (var x = bx * BlockSize; x < (bx + 1) * BlockSize; x++)
                    {
                        var i = row + x;
                        if (!marks[i]) continue;
                        if (color[i])
                        {
                            colorCount++;
                            if (pixels[i].Blue > pixels[i].Red + 20) blueCount++;
                        }
                        else
                        {
                            inkCount++;
                        }
                    }
                }

                if (colorCount >= minCount) classes[bx, by] = BlockClass.Color;
                else if (inkCount >= minCount) classes[bx, by] = BlockClass.Ink;
                blue[bx, by] = blueCount * 2 >= minCount;
            }
        }
        return (classes, blue);
    }

    private static double TextFringeShare(List<(int X, int Y)> blocks, bool[] marks, bool[] nearText, int width)
    {
        var total = 0;
        var nearCount = 0;
        foreach (var (bx, by) in blocks)
        {
            for (var y = by * BlockSize; y < (by + 1) * BlockSize; y++)
            {
                for (var x = bx * BlockSize; x < (bx + 1) * BlockSize; x++)
                {
                    var i = y * width + x;
                    if (!marks[i]) continue;
                    total++;
                    if (nearText[i]) nearCount++;
                }
            }
        }
        return (double)nearCount / Math.Max(1, total);
    }

    private static bool[] Dilate(bool[] mask, int width, int height, int radius)
    {
        var horizontal = new bool[mask.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var lastOn = -1000000;
            for (var x = 0; x < width + radius; x++)
            {
                if (x < width && mask[row + x]) lastOn = x;
                var target = x - radius;
                if (target >= 0 && target < width && x - lastOn <= 2 * radius) horizontal[row + target] = true;
            }
        }

        var result = new bool[mask.Length];
        for (var x = 0; x < width; x++)
        {
            var lastOn = -1000000;
            for (var y = 0; y < height + radius; y++)
            {
                if (y < height && horizontal[y * width + x]) lastOn = y;
                var target = y - radius;
                if (target >= 0 && target < height && y - lastOn <= 2 * radius) result[target * width + x] = true;
            }
        }
        return result;
    }

    private static List<StampBlob> GrowColoredStamps(List<StampBlob> stamps, bool[] color, int width, int height)
    {
        var grown = stamps.Select(s => s with { Rect = GrowOverColor(s.Rect, color, width, height) }).ToList();
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < grown.Count && !merged; i++)
            {
                for (var j = i + 1; j < grown.Count; j++)
                {
                    var a = grown[i].Rect;
                    var b = grown[j].Rect;
                    if (a.Left >= b.Right || b.Left >= a.Right || a.Top >= b.Bottom || b.Top >= a.Bottom) continue;
                    grown[i] = grown[i] with
                    {
                        Rect = new SKRectI(
                            Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
                            Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom))
                    };
                    grown.RemoveAt(j);
                    merged = true;
                    break;
                }
            }
        }
        return grown;
    }

    private static SKRectI GrowOverColor(SKRectI rect, bool[] color, int width, int height)
    {
        var grow = (int)(StampGrowFraction * Math.Max(rect.Width, rect.Height));
        var left = Math.Max(0, rect.Left - grow);
        var top = Math.Max(0, rect.Top - grow);
        var right = Math.Min(width, rect.Right + grow);
        var bottom = Math.Min(height, rect.Bottom + grow);
        var cellsW = (right - left) / StampGrowCellPx;
        var cellsH = (bottom - top) / StampGrowCellPx;
        if (cellsW < 1 || cellsH < 1) return rect;

        var on = new bool[cellsW, cellsH];
        for (var cy = 0; cy < cellsH; cy++)
        {
            for (var cx = 0; cx < cellsW; cx++)
            {
                var count = 0;
                for (var y = top + cy * StampGrowCellPx; y < top + (cy + 1) * StampGrowCellPx; y++)
                {
                    for (var x = left + cx * StampGrowCellPx; x < left + (cx + 1) * StampGrowCellPx; x++)
                    {
                        if (color[y * width + x]) count++;
                    }
                }
                on[cx, cy] = count >= StampGrowCellMinPixels;
            }
        }

        var seen = new bool[cellsW, cellsH];
        var stack = new Stack<(int X, int Y)>();
        var seedLeft = Math.Max(0, (rect.Left - left) / StampGrowCellPx);
        var seedTop = Math.Max(0, (rect.Top - top) / StampGrowCellPx);
        var seedRight = Math.Min(cellsW, (rect.Right - left + StampGrowCellPx - 1) / StampGrowCellPx);
        var seedBottom = Math.Min(cellsH, (rect.Bottom - top + StampGrowCellPx - 1) / StampGrowCellPx);
        for (var cy = seedTop; cy < seedBottom; cy++)
        {
            for (var cx = seedLeft; cx < seedRight; cx++)
            {
                if (!on[cx, cy]) continue;
                seen[cx, cy] = true;
                stack.Push((cx, cy));
            }
        }
        if (stack.Count < StampGrowMinSeedCells) return rect;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            minX = Math.Min(minX, cx);
            minY = Math.Min(minY, cy);
            maxX = Math.Max(maxX, cx);
            maxY = Math.Max(maxY, cy);
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = cx + dx;
                    var ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= cellsW || ny >= cellsH) continue;
                    if (!on[nx, ny] || seen[nx, ny]) continue;
                    seen[nx, ny] = true;
                    stack.Push((nx, ny));
                }
            }
        }

        return new SKRectI(
            Math.Min(rect.Left, left + minX * StampGrowCellPx),
            Math.Min(rect.Top, top + minY * StampGrowCellPx),
            Math.Max(rect.Right, left + (maxX + 1) * StampGrowCellPx),
            Math.Max(rect.Bottom, top + (maxY + 1) * StampGrowCellPx));
    }

    private static bool IsTableFrameRemnant(List<(int X, int Y)> blocks, bool[] marks, int width, SKRectI rect)
    {
        var w = rect.Width;
        var h = rect.Height;
        if (w <= 0 || h <= 0) return false;
        var local = new bool[w * h];
        var count = 0;
        foreach (var (bx, by) in blocks)
        {
            for (var y = by * BlockSize; y < (by + 1) * BlockSize; y++)
            {
                for (var x = bx * BlockSize; x < (bx + 1) * BlockSize; x++)
                {
                    if (!marks[y * width + x]) continue;
                    local[(y - rect.Top) * w + (x - rect.Left)] = true;
                    count++;
                }
            }
        }
        if (count == 0) return false;
        if ((double)count / (w * h) > MaxFrameRemnantDensity) return false;
        var straight = StraightRunPixels(local, w, h, true) + StraightRunPixels(local, w, h, false);
        return straight >= MinFrameRemnantStraightShare * count;
    }

    private static int StraightRunPixels(bool[] local, int w, int h, bool horizontal)
    {
        var lineCount = horizontal ? h : w;
        var length = horizontal ? w : h;
        var total = 0;
        for (var a = 0; a < lineCount; a++)
        {
            var onCount = 0;
            for (var b = 0; b < length; b++)
            {
                if (horizontal ? local[a * w + b] : local[b * w + a]) onCount++;
            }
            if (onCount < FrameRunMinLength) continue;

            var runStart = -1;
            var last = -1;
            var runPixels = 0;
            for (var b = 0; b < length; b++)
            {
                if (!(horizontal ? local[a * w + b] : local[b * w + a])) continue;
                if (runStart >= 0 && b - last > FrameRunMaxGap + 1)
                {
                    if (last - runStart + 1 >= FrameRunMinLength) total += runPixels;
                    runStart = -1;
                }
                if (runStart < 0)
                {
                    runStart = b;
                    runPixels = 0;
                }
                runPixels++;
                last = b;
            }
            if (runStart >= 0 && last - runStart + 1 >= FrameRunMinLength) total += runPixels;
        }
        return total;
    }

    private static bool IsStraightStroke(List<(int X, int Y)> blocks, bool[] marks, int width)
    {
        long n = 0;
        double sumX = 0, sumY = 0, sumXX = 0, sumYY = 0, sumXY = 0;
        foreach (var (bx, by) in blocks)
        {
            for (var y = by * BlockSize; y < (by + 1) * BlockSize; y++)
            {
                for (var x = bx * BlockSize; x < (bx + 1) * BlockSize; x++)
                {
                    if (!marks[y * width + x]) continue;
                    n++;
                    sumX += x;
                    sumY += y;
                    sumXX += (double)x * x;
                    sumYY += (double)y * y;
                    sumXY += (double)x * y;
                }
            }
        }
        if (n < MinStrokePixels) return true;

        var meanX = sumX / n;
        var meanY = sumY / n;
        var varX = (sumXX - n * meanX * meanX) / (n - 1);
        var varY = (sumYY - n * meanY * meanY) / (n - 1);
        var covXY = (sumXY - n * meanX * meanY) / (n - 1);
        var half = (varX - varY) / 2;
        var minorVariance = (varX + varY) / 2 - Math.Sqrt(half * half + covXY * covXY);
        return Math.Sqrt(Math.Max(0, minorVariance)) <= MaxStraightStrokeSpreadPx;
    }

    private static BlockClass[,] RemoveRules(BlockClass[,] classes, double minLengthFraction, int window, int maxThickness)
    {
        var gridW = classes.GetLength(0);
        var gridH = classes.GetLength(1);
        var minRun = Math.Max(MinBlobBlocks, (int)(gridW * minLengthFraction));
        var clear = new bool[gridW, gridH];

        bool Filled(int x, int y) => classes[x, y] != BlockClass.Background;

        bool InBand(int x, int y)
        {
            for (var dy = -window; dy <= window; dy++)
            {
                var yy = y + dy;
                if (yy >= 0 && yy < gridH && Filled(x, yy)) return true;
            }
            return false;
        }

        int Thickness(int x, int y)
        {
            var thickness = 1;
            for (var yy = y - 1; yy >= 0 && Filled(x, yy) && thickness <= maxThickness; yy--) thickness++;
            for (var yy = y + 1; yy < gridH && Filled(x, yy) && thickness <= maxThickness; yy++) thickness++;
            return thickness;
        }

        for (var y = 0; y < gridH; y++)
        {
            var x = 0;
            while (x < gridW)
            {
                if (!InBand(x, y))
                {
                    x++;
                    continue;
                }

                var start = x;
                while (x < gridW && InBand(x, y)) x++;
                if (x - start < minRun) continue;

                for (var yy = Math.Max(0, y - window); yy <= Math.Min(gridH - 1, y + window); yy++)
                {
                    for (var rx = start; rx < x; rx++)
                    {
                        if (Filled(rx, yy) && Thickness(rx, yy) <= maxThickness) clear[rx, yy] = true;
                    }
                }
            }
        }

        var result = (BlockClass[,])classes.Clone();
        for (var y = 0; y < gridH; y++)
        {
            for (var x = 0; x < gridW; x++)
            {
                if (clear[x, y]) result[x, y] = BlockClass.Background;
            }
        }
        return result;
    }

    private static BlockClass[,] Transpose(BlockClass[,] classes)
    {
        var w = classes.GetLength(0);
        var h = classes.GetLength(1);
        var result = new BlockClass[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) result[y, x] = classes[x, y];
        }
        return result;
    }

    private static List<(int X, int Y)> FloodFill(
        BlockClass[,] classes, bool[,] visited, int startX, int startY, int gridW, int gridH)
    {
        var result = new List<(int X, int Y)>();
        var stack = new Stack<(int X, int Y)>();
        stack.Push((startX, startY));
        visited[startX, startY] = true;

        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            result.Add((x, y));
            for (var dy = -BlobLinkRadiusBlocks; dy <= BlobLinkRadiusBlocks; dy++)
            {
                for (var dx = -BlobLinkRadiusBlocks; dx <= BlobLinkRadiusBlocks; dx++)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= gridW || ny >= gridH) continue;
                    if (visited[nx, ny] || classes[nx, ny] == BlockClass.Background) continue;
                    visited[nx, ny] = true;
                    stack.Push((nx, ny));
                }
            }
        }

        return result;
    }

    private static List<(SKRectI Rect, double Fill, int Thickness)> MergeNearby(
        List<(SKRectI Rect, double Fill, int Thickness)> items)
    {
        var merged = new List<(SKRectI Rect, double Fill, int Thickness)>(items);
        var gap = SignatureMergeGapBlocks * BlockSize;
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < merged.Count && !changed; i++)
            {
                for (var j = i + 1; j < merged.Count && !changed; j++)
                {
                    var a = merged[i].Rect;
                    var b = merged[j].Rect;
                    if (a.Left - gap > b.Right || b.Left - gap > a.Right || a.Top - gap > b.Bottom || b.Top - gap > a.Bottom) continue;
                    merged[i] = (RectMath.Union(a, b), Math.Max(merged[i].Fill, merged[j].Fill),
                        Math.Max(merged[i].Thickness, merged[j].Thickness));
                    merged.RemoveAt(j);
                    changed = true;
                }
            }
        }
        return merged;
    }
}
