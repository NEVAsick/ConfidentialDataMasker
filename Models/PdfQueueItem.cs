using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ConfidentialDataMasker.Models;

public enum PdfStatus
{
    Pending,
    Editing,
    Done,
    Error
}

public partial class PdfQueueItem : ObservableObject
{
    private const double SameSuggestionOverlap = 0.8;

    public string FilePath { get; }
    public string FileName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private PdfStatus _status = PdfStatus.Pending;

    [ObservableProperty]
    private int _pageCount;

    public string StatusText => Status switch
    {
        PdfStatus.Pending => "ждёт",
        PdfStatus.Editing => "открыт",
        PdfStatus.Done => "сохранён",
        PdfStatus.Error => "ошибка",
        _ => ""
    };

    public Dictionary<int, List<RedactionRect>> RectsByPage { get; } = new();

    private readonly Dictionary<int, List<SuggestedRect>> _suggestionsByPage = new();
    private readonly Dictionary<int, List<SuggestedRect>> _rejectedByPage = new();

    public PdfQueueItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
    }

    public List<RedactionRect> GetRectsForPage(int pageIndex)
    {
        if (!RectsByPage.TryGetValue(pageIndex, out var list))
        {
            list = new List<RedactionRect>();
            RectsByPage[pageIndex] = list;
        }
        return list;
    }

    public bool TryGetCachedSuggestions(int pageIndex, out List<SuggestedRect> suggestions)
    {
        if (_suggestionsByPage.TryGetValue(pageIndex, out var list))
        {
            suggestions = list;
            return true;
        }
        suggestions = new List<SuggestedRect>();
        return false;
    }

    public void SetCachedSuggestions(int pageIndex, List<SuggestedRect> suggestions) =>
        _suggestionsByPage[pageIndex] = suggestions.Where(s => !WasRejected(pageIndex, s)).ToList();

    public void ClearCachedSuggestions() => _suggestionsByPage.Clear();

    public void RemoveCachedSuggestion(int pageIndex, SuggestedRect suggestion)
    {
        if (_suggestionsByPage.TryGetValue(pageIndex, out var list))
        {
            list.Remove(suggestion);
        }
    }

    public void RememberRejection(int pageIndex, SuggestedRect suggestion)
    {
        if (!_rejectedByPage.TryGetValue(pageIndex, out var list))
        {
            list = new List<SuggestedRect>();
            _rejectedByPage[pageIndex] = list;
        }
        list.Add(suggestion);
    }

    public List<RedactionRect> GetAllRectsForPage(int pageIndex)
    {
        var result = new List<RedactionRect>(GetRectsForPage(pageIndex));
        if (TryGetCachedSuggestions(pageIndex, out var suggestions))
        {
            foreach (var s in suggestions) result.Add(s.ToRedactionRect());
        }
        return result;
    }

    private bool WasRejected(int pageIndex, SuggestedRect suggestion) =>
        _rejectedByPage.TryGetValue(pageIndex, out var rejected) &&
        rejected.Any(r => r.Kind == suggestion.Kind && OverlapOfLarger(r, suggestion) >= SameSuggestionOverlap);

    private static double OverlapOfLarger(SuggestedRect a, SuggestedRect b)
    {
        var width = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var height = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (width <= 0 || height <= 0) return 0;
        var larger = Math.Max(a.Width * a.Height, b.Width * b.Height);
        return larger <= 0 ? 0 : width * height / larger;
    }
}
