using System;
using System.Collections.Generic;
using System.Linq;

namespace ConfidentialDataMasker.Services;

internal static class TableGridLines
{
    private const int BandHalfWidth = 2;
    private const int MinHorizontalLength = 120;
    private const int MinVerticalLength = 100;
    private const int MaxBreak = 6;
    private const double MinVisibleShare = 0.6;
    private const int TouchMargin = 6;
    private const int MinCrossingLines = 2;

    private readonly record struct Segment(int Position, int Start, int End);

    public static bool[] Find(bool[] dark, bool[] visible, bool[] faintDark, bool[] faintVisible, int width, int height)
    {
        var horizontal = FindSegments(dark, visible, width, height, MinHorizontalLength, false);
        horizontal.AddRange(FindSegments(faintDark, faintVisible, width, height, MinHorizontalLength, true));
        var vertical = FindSegments(
            Transpose(dark, width, height), Transpose(visible, width, height), height, width, MinVerticalLength, false);
        vertical.AddRange(FindSegments(
            Transpose(faintDark, width, height), Transpose(faintVisible, width, height), height, width, MinVerticalLength, true));

        (horizontal, vertical) = KeepGridSegments(horizontal, vertical);

        var grid = new bool[width * height];
        foreach (var s in horizontal)
        {
            for (var d = -BandHalfWidth; d <= BandHalfWidth; d++)
            {
                var y = s.Position + d;
                if (y < 0 || y >= height) continue;
                for (var x = s.Start; x < s.End; x++)
                {
                    if (dark[y * width + x]) grid[y * width + x] = true;
                }
            }
        }
        foreach (var s in vertical)
        {
            for (var d = -BandHalfWidth; d <= BandHalfWidth; d++)
            {
                var x = s.Position + d;
                if (x < 0 || x >= width) continue;
                for (var y = s.Start; y < s.End; y++)
                {
                    if (dark[y * width + x]) grid[y * width + x] = true;
                }
            }
        }
        return grid;
    }

    private static List<Segment> FindSegments(
        bool[] dark, bool[] visible, int width, int height, int minLength, bool requireClearSides)
    {
        var result = new List<Segment>();
        var isLine = new bool[width];
        var hasVisible = new bool[width];
        var outer = BandHalfWidth + 2;

        for (var y = 0; y < height; y++)
        {
            var count = 0;
            for (var x = 0; x < width; x++)
            {
                var band = false;
                var bandVisible = false;
                for (var d = -BandHalfWidth; d <= BandHalfWidth; d++)
                {
                    var yy = y + d;
                    if (yy < 0 || yy >= height) continue;
                    var i = yy * width + x;
                    band |= dark[i];
                    bandVisible |= visible[i];
                }
                var above = y - outer >= 0 && dark[(y - outer) * width + x];
                var below = y + outer < height && dark[(y + outer) * width + x];
                var thick = requireClearSides ? above || below : above && below;
                isLine[x] = band && !thick;
                hasVisible[x] = bandVisible;
                if (isLine[x]) count++;
            }
            if (count < minLength) continue;

            var start = -1;
            var last = -1;
            for (var x = 0; x <= width; x++)
            {
                var on = x < width && isLine[x];
                if (on && start >= 0 && x - last > MaxBreak + 1)
                {
                    AddSegment(result, y, start, last + 1, minLength, hasVisible);
                    start = x;
                }
                else if (on && start < 0)
                {
                    start = x;
                }
                if (on) last = x;
            }
            if (start >= 0) AddSegment(result, y, start, last + 1, minLength, hasVisible);
        }
        return result;
    }

    private static void AddSegment(List<Segment> result, int position, int start, int end, int minLength, bool[] hasVisible)
    {
        if (end - start < minLength) return;
        var visibleCount = 0;
        for (var x = start; x < end; x++)
        {
            if (hasVisible[x]) visibleCount++;
        }
        if ((double)visibleCount / (end - start) < MinVisibleShare) return;
        result.Add(new Segment(position, start, end));
    }

    private static (List<Segment> Horizontal, List<Segment> Vertical) KeepGridSegments(
        List<Segment> horizontal, List<Segment> vertical)
    {
        var hGroup = GroupNeighbours(horizontal);
        var vGroup = GroupNeighbours(vertical);
        var hTouches = new Dictionary<int, HashSet<int>>();
        var vTouches = new Dictionary<int, HashSet<int>>();

        for (var i = 0; i < horizontal.Count; i++)
        {
            var h = horizontal[i];
            for (var j = 0; j < vertical.Count; j++)
            {
                var v = vertical[j];
                if (v.Position < h.Start - TouchMargin || v.Position > h.End + TouchMargin) continue;
                if (h.Position < v.Start - TouchMargin || h.Position > v.End + TouchMargin) continue;
                if (!hTouches.TryGetValue(hGroup[i], out var hs)) hTouches[hGroup[i]] = hs = new HashSet<int>();
                if (!vTouches.TryGetValue(vGroup[j], out var vs)) vTouches[vGroup[j]] = vs = new HashSet<int>();
                hs.Add(vGroup[j]);
                vs.Add(hGroup[i]);
            }
        }

        var hAlive = new HashSet<int>(hTouches.Keys);
        var vAlive = new HashSet<int>(vTouches.Keys);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var g in hAlive.ToList())
            {
                if (hTouches[g].Count(vAlive.Contains) >= MinCrossingLines) continue;
                hAlive.Remove(g);
                changed = true;
            }
            foreach (var g in vAlive.ToList())
            {
                if (vTouches[g].Count(hAlive.Contains) >= MinCrossingLines) continue;
                vAlive.Remove(g);
                changed = true;
            }
        }

        return (
            horizontal.Where((_, i) => hAlive.Contains(hGroup[i])).ToList(),
            vertical.Where((_, i) => vAlive.Contains(vGroup[i])).ToList());
    }

    private static int[] GroupNeighbours(List<Segment> segments)
    {
        var parent = Enumerable.Range(0, segments.Count).ToArray();
        int Root(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        var order = Enumerable.Range(0, segments.Count).OrderBy(i => segments[i].Position).ToList();
        for (var a = 0; a < order.Count; a++)
        {
            var i = order[a];
            for (var b = a + 1; b < order.Count; b++)
            {
                var j = order[b];
                if (segments[j].Position - segments[i].Position > 2 * BandHalfWidth + 1) break;
                if (segments[j].Start < segments[i].End && segments[i].Start < segments[j].End) parent[Root(j)] = Root(i);
            }
        }
        return Enumerable.Range(0, segments.Count).Select(Root).ToArray();
    }

    private static bool[] Transpose(bool[] source, int width, int height)
    {
        var result = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++) result[x * height + y] = source[y * width + x];
        }
        return result;
    }
}
