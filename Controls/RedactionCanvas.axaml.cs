using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ConfidentialDataMasker.Models;

namespace ConfidentialDataMasker.Controls;

public partial class RedactionCanvas : UserControl
{
    public static readonly StyledProperty<Bitmap?> PageImageProperty =
        AvaloniaProperty.Register<RedactionCanvas, Bitmap?>(nameof(PageImage));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<RedactionCanvas, double>(nameof(Zoom), defaultValue: 1.0);

    public static readonly StyledProperty<ObservableCollection<RedactionRect>?> RectanglesProperty =
        AvaloniaProperty.Register<RedactionCanvas, ObservableCollection<RedactionRect>?>(nameof(Rectangles));

    public static readonly StyledProperty<ObservableCollection<SuggestedRect>?> SuggestionsProperty =
        AvaloniaProperty.Register<RedactionCanvas, ObservableCollection<SuggestedRect>?>(nameof(Suggestions));

    public static readonly StyledProperty<ICommand?> AddRectangleCommandProperty =
        AvaloniaProperty.Register<RedactionCanvas, ICommand?>(nameof(AddRectangleCommand));

    public static readonly StyledProperty<ICommand?> RemoveRectangleCommandProperty =
        AvaloniaProperty.Register<RedactionCanvas, ICommand?>(nameof(RemoveRectangleCommand));

    public static readonly StyledProperty<ICommand?> AcceptSuggestionCommandProperty =
        AvaloniaProperty.Register<RedactionCanvas, ICommand?>(nameof(AcceptSuggestionCommand));

    public static readonly StyledProperty<ICommand?> RejectSuggestionCommandProperty =
        AvaloniaProperty.Register<RedactionCanvas, ICommand?>(nameof(RejectSuggestionCommand));

    public static readonly StyledProperty<ICommand?> ClassifyRectangleCommandProperty =
        AvaloniaProperty.Register<RedactionCanvas, ICommand?>(nameof(ClassifyRectangleCommand));

    public static readonly StyledProperty<ICommand?> AcceptSuggestionAsCommandProperty =
        AvaloniaProperty.Register<RedactionCanvas, ICommand?>(nameof(AcceptSuggestionAsCommand));

    public Bitmap? PageImage
    {
        get => GetValue(PageImageProperty);
        set => SetValue(PageImageProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public ObservableCollection<RedactionRect>? Rectangles
    {
        get => GetValue(RectanglesProperty);
        set => SetValue(RectanglesProperty, value);
    }

    public ObservableCollection<SuggestedRect>? Suggestions
    {
        get => GetValue(SuggestionsProperty);
        set => SetValue(SuggestionsProperty, value);
    }

    public ICommand? AddRectangleCommand
    {
        get => GetValue(AddRectangleCommandProperty);
        set => SetValue(AddRectangleCommandProperty, value);
    }

    public ICommand? RemoveRectangleCommand
    {
        get => GetValue(RemoveRectangleCommandProperty);
        set => SetValue(RemoveRectangleCommandProperty, value);
    }

    public ICommand? AcceptSuggestionCommand
    {
        get => GetValue(AcceptSuggestionCommandProperty);
        set => SetValue(AcceptSuggestionCommandProperty, value);
    }

    public ICommand? RejectSuggestionCommand
    {
        get => GetValue(RejectSuggestionCommandProperty);
        set => SetValue(RejectSuggestionCommandProperty, value);
    }

    public ICommand? ClassifyRectangleCommand
    {
        get => GetValue(ClassifyRectangleCommandProperty);
        set => SetValue(ClassifyRectangleCommandProperty, value);
    }

    public ICommand? AcceptSuggestionAsCommand
    {
        get => GetValue(AcceptSuggestionAsCommandProperty);
        set => SetValue(AcceptSuggestionAsCommandProperty, value);
    }

    private static readonly (ManualRedactionType Type, string Title)[] ManualTypeChoices =
    {
        (ManualRedactionType.PersonName, "ФИО"),
        (ManualRedactionType.DocumentNumber, "Номер документа (паспорт, СНИЛС, ИНН, полис…)"),
        (ManualRedactionType.ContactInfo, "Адрес / телефон / email / сайт"),
        (ManualRedactionType.Organization, "Организация (название, логотип)"),
        (ManualRedactionType.Signature, "Подпись"),
        (ManualRedactionType.Stamp, "Печать / штамп"),
        (ManualRedactionType.Other, "Другое"),
    };

    private Point? _dragStart;
    private Rectangle? _dragPreview;

    public RedactionCanvas()
    {
        InitializeComponent();

        OverlayCanvas.PointerPressed += OnCanvasPointerPressed;
        OverlayCanvas.PointerMoved += OnCanvasPointerMoved;
        OverlayCanvas.PointerReleased += OnCanvasPointerReleased;
        OverlayCanvas.PointerCaptureLost += OnCanvasPointerCaptureLost;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PageImageProperty)
        {
            ImageHost.Source = PageImage;
            ApplyZoom();
        }
        else if (change.Property == ZoomProperty)
        {
            ApplyZoom();
        }
        else if (change.Property == RectanglesProperty || change.Property == SuggestionsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldCollection)
            {
                oldCollection.CollectionChanged -= OnOverlayCollectionChanged;
            }
            if (change.NewValue is INotifyCollectionChanged newCollection)
            {
                newCollection.CollectionChanged += OnOverlayCollectionChanged;
            }
            Redraw();
        }
    }

    private void ApplyZoom()
    {
        var width = (PageImage?.PixelSize.Width ?? 0) * Zoom;
        var height = (PageImage?.PixelSize.Height ?? 0) * Zoom;

        OverlayCanvas.Width = width;
        OverlayCanvas.Height = height;
        ImageHost.Width = width;
        ImageHost.Height = height;

        Redraw();
    }

    private void OnOverlayCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(OverlayCanvas).Properties.IsLeftButtonPressed) return;

        CancelDrag();
        _dragStart = e.GetPosition(OverlayCanvas);
        _dragPreview = new Rectangle
        {
            Fill = ConfirmedFillBrush,
            Stroke = ConfirmedStrokeBrush,
            StrokeThickness = 1.5,
            Width = 0,
            Height = 0
        };
        Canvas.SetLeft(_dragPreview, _dragStart.Value.X);
        Canvas.SetTop(_dragPreview, _dragStart.Value.Y);
        OverlayCanvas.Children.Add(_dragPreview);
        e.Pointer.Capture(OverlayCanvas);
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is null || _dragPreview is null) return;

        var current = e.GetPosition(OverlayCanvas);
        var x = Math.Min(_dragStart.Value.X, current.X);
        var y = Math.Min(_dragStart.Value.Y, current.Y);
        var w = Math.Abs(current.X - _dragStart.Value.X);
        var h = Math.Abs(current.Y - _dragStart.Value.Y);

        Canvas.SetLeft(_dragPreview, x);
        Canvas.SetTop(_dragPreview, y);
        _dragPreview.Width = w;
        _dragPreview.Height = h;
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart is null || _dragPreview is null) return;

        var preview = _dragPreview;
        _dragPreview = null;
        _dragStart = null;
        e.Pointer.Capture(null);
        OverlayCanvas.Children.Remove(preview);

        var width = OverlayCanvas.Width;
        var height = OverlayCanvas.Height;

        if (preview.Width > 4 && preview.Height > 4 && width > 0 && height > 0)
        {
            var rect = new RedactionRect(
                Canvas.GetLeft(preview) / width,
                Canvas.GetTop(preview) / height,
                preview.Width / width,
                preview.Height / height);

            AddRectangleCommand?.Execute(rect);
            AskManualRectangleType(rect);
        }
    }

    private void OnCanvasPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => CancelDrag();

    private void CancelDrag()
    {
        if (_dragPreview is not null) OverlayCanvas.Children.Remove(_dragPreview);
        _dragPreview = null;
        _dragStart = null;
    }

    private void AskManualRectangleType(RedactionRect rect)
    {
        if (ClassifyRectangleCommand is null) return;

        var chosen = false;
        var flyout = BuildTypeMenu(type =>
        {
            chosen = true;
            ClassifyRectangleCommand?.Execute(new ManualRectangleClassification(rect, type));
        });

        flyout.Closed += (_, _) =>
        {
            if (!chosen)
            {
                ClassifyRectangleCommand?.Execute(new ManualRectangleClassification(rect, ManualRedactionType.Unspecified));
            }
        };

        flyout.ShowAt(OverlayCanvas, true);
    }

    private void AskSuggestionDecision(SuggestedRect suggestion)
    {
        var flyout = BuildTypeMenu(type =>
            AcceptSuggestionAsCommand?.Execute(new SuggestionClassification(suggestion, type)),
            titlePrefix: "Закрыть как: ");

        flyout.Items.Add(new Separator());
        var reject = new MenuItem { Header = "Не закрывать" };
        reject.Click += (_, _) => RejectSuggestionCommand?.Execute(suggestion);
        flyout.Items.Add(reject);

        flyout.ShowAt(OverlayCanvas, true);
    }

    private static MenuFlyout BuildTypeMenu(Action<ManualRedactionType> onChoose, string titlePrefix = "")
    {
        var flyout = new MenuFlyout();
        foreach (var (type, title) in ManualTypeChoices)
        {
            var item = new MenuItem { Header = titlePrefix + title };
            item.Click += (_, _) => onChoose(type);
            flyout.Items.Add(item);
        }
        return flyout;
    }

    private void Redraw()
    {
        for (var i = OverlayCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (OverlayCanvas.Children[i] is Control c && (c.Tag is RedactionRect || c.Tag is SuggestedRect))
            {
                OverlayCanvas.Children.RemoveAt(i);
            }
        }

        var width = OverlayCanvas.Width;
        var height = OverlayCanvas.Height;
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0) return;

        if (Rectangles is not null)
        {
            foreach (var rect in Rectangles)
            {
                DrawConfirmed(rect, width, height);
            }
        }

        if (Suggestions is not null)
        {
            foreach (var suggestion in Suggestions)
            {
                DrawSuggestion(suggestion, width, height);
            }
        }
    }

    private static readonly IBrush ConfirmedFillBrush = new SolidColorBrush(Colors.Red, 0.4);
    private static readonly IBrush ConfirmedStrokeBrush = new SolidColorBrush(Colors.Red, 0.9);

    private void DrawConfirmed(RedactionRect rect, double width, double height)
    {
        var shape = new Rectangle
        {
            Fill = ConfirmedFillBrush,
            Stroke = ConfirmedStrokeBrush,
            StrokeThickness = 1.5,
            Width = rect.Width * width,
            Height = rect.Height * height,
            Tag = rect
        };
        ToolTip.SetTip(shape, "ПКМ (или Ctrl+клик) — удалить это закрашивание");
        shape.PointerPressed += (_, e) =>
        {
            var isRightClick = e.GetCurrentPoint(shape).Properties.IsRightButtonPressed;
            if (isRightClick || e.KeyModifiers == KeyModifiers.Control)
            {
                RemoveRectangleCommand?.Execute(rect);
                e.Handled = true;
            }
        };
        Canvas.SetLeft(shape, rect.X * width);
        Canvas.SetTop(shape, rect.Y * height);
        OverlayCanvas.Children.Add(shape);
    }

    private void DrawSuggestion(SuggestedRect suggestion, double width, double height)
    {
        var color = suggestion.Kind switch
        {
            SuggestionKind.Stamp => Color.FromRgb(0x9C, 0x27, 0xB0),
            SuggestionKind.Signature => Color.FromRgb(0xFF, 0x98, 0x00),
            _ => Color.FromRgb(0x19, 0x76, 0xD2)
        };
        var brush = new SolidColorBrush(color);

        var shape = new Rectangle
        {
            Stroke = brush,
            StrokeThickness = 2,
            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
            Fill = new SolidColorBrush(color, 0.18),
            Width = suggestion.Width * width,
            Height = suggestion.Height * height,
            Tag = suggestion,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(shape, $"{suggestion.Label}  — при сохранении закрасится автоматически. Клик — подтвердить, ПКМ — выбрать тип или не закрывать, Ctrl+клик — не закрывать");

        shape.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(shape).Properties.IsRightButtonPressed)
            {
                AskSuggestionDecision(suggestion);
            }
            else if (e.KeyModifiers == KeyModifiers.Control)
            {
                RejectSuggestionCommand?.Execute(suggestion);
            }
            else
            {
                AcceptSuggestionCommand?.Execute(suggestion);
            }
            e.Handled = true;
        };

        Canvas.SetLeft(shape, suggestion.X * width);
        Canvas.SetTop(shape, suggestion.Y * height);
        OverlayCanvas.Children.Add(shape);

        var label = new TextBlock
        {
            Text = suggestion.Label,
            Foreground = brush,
            Background = Brushes.White,
            FontSize = 11,
            Padding = new Thickness(2, 0),
            Tag = suggestion,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, suggestion.X * width);
        Canvas.SetTop(label, Math.Max(0, suggestion.Y * height - 16));
        OverlayCanvas.Children.Add(label);
    }
}
