using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConfidentialDataMasker.Models;

namespace ConfidentialDataMasker.ViewModels;

public partial class PageViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    public int PageIndex { get; }

    public int PageNumber => PageIndex + 1;

    [ObservableProperty]
    private Bitmap? _pageImage;

    public ObservableCollection<RedactionRect> Rectangles { get; } = new();
    public ObservableCollection<SuggestedRect> Suggestions { get; } = new();

    public PageViewModel(MainWindowViewModel owner, int pageIndex)
    {
        _owner = owner;
        PageIndex = pageIndex;
    }

    public double Zoom => _owner.ZoomLevel;

    internal void NotifyZoomChanged() => OnPropertyChanged(nameof(Zoom));

    [RelayCommand]
    private void AddRectangle(RedactionRect rect) => _owner.AddRectangleToPage(this, rect);

    [RelayCommand]
    private void ClassifyRectangle(ManualRectangleClassification classification) =>
        _owner.ClassifyManualRectangle(this, classification);

    [RelayCommand]
    private void RemoveRectangle(RedactionRect rect) => _owner.RemoveRectangleFromPage(this, rect);

    [RelayCommand]
    private void AcceptSuggestion(SuggestedRect suggestion) => _owner.AcceptSuggestionOnPage(this, suggestion);

    [RelayCommand]
    private void RejectSuggestion(SuggestedRect suggestion) => _owner.RejectSuggestionOnPage(this, suggestion);

    [RelayCommand]
    private void AcceptSuggestionAs(SuggestionClassification classification) =>
        _owner.AcceptSuggestionOnPage(this, classification.Suggestion, classification.Type);
}
