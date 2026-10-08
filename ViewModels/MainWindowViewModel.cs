using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConfidentialDataMasker.Models;
using ConfidentialDataMasker.Services;

namespace ConfidentialDataMasker.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int DisplayMaxWidthPx = 760;
    private const int DisplayDpi = 130;
    private const int DetectionDpi = 200;
    private const int OutputDpi = 200;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 0.25;
    private const string RedactedFilePrefix = "Обработанный_";
    private static readonly TimeSpan DictionaryChangeDelay = TimeSpan.FromMilliseconds(700);

    private readonly IFilePickerService _filePicker;
    private readonly PdfRenderService _renderService = new();
    private readonly PdfRedactionService _redactionService;
    private readonly OcrService _ocrService = new();
    private readonly PageTextExtractor _pageTextExtractor;
    private readonly FeedbackLogger _feedbackLogger = new();
    private readonly FeedbackRecorder _feedbackRecorder;
    private readonly SemaphoreSlim _detectGate = new(1, 1);
    private readonly Dictionary<string, string> _outputOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextFileTermSource _dictionarySource = new(AppDataPaths.Dictionary);
    private readonly ConfidentialDataDetector _detector;
    private readonly VisionModelSettings _visionSettings = VisionModelSettings.Load();
    private readonly OllamaVisionClient _visionClient;
    private readonly ModelSecondReading _secondReading;

    private FileSystemWatcher? _dictionaryWatcher;
    private int _dictionaryChangeVersion;
    private bool _dictionaryChangePending;
    private int _busyCount;
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<PdfQueueItem> Queue { get; } = new();

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    [ObservableProperty]
    private PdfQueueItem? _selectedItem;

    [ObservableProperty]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    private string _statusText = "Добавьте PDF-файлы и выберите файл в списке слева";

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    private bool _hasPages;

    [ObservableProperty]
    private bool _useVisionModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentText))]
    private double _zoomLevel = 1.0;

    public string ZoomPercentText => $"{(int)Math.Round(ZoomLevel * 100)}%";

    public MainWindowViewModel(IFilePickerService filePicker)
    {
        _filePicker = filePicker;
        _redactionService = new PdfRedactionService(_renderService);
        _pageTextExtractor = new PageTextExtractor(_ocrService);
        _feedbackRecorder = new FeedbackRecorder(_renderService, _feedbackLogger);
        _visionClient = new OllamaVisionClient(_visionSettings);
        _secondReading = new ModelSecondReading(_visionClient, _visionSettings);
        UseVisionModel = _visionSettings.Enabled;

        try
        {
            _dictionarySource.EnsureExists(RedactionDictionary.DefaultFileContent);
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось создать словарь ({_dictionarySource.FilePath}): {ex.Message}";
        }

        var (thresholds, changes) = ThresholdTrainer.Train(_feedbackLogger.LoadExamples(), new DetectorThresholds());
        _detector = new ConfidentialDataDetector(thresholds, new RedactionDictionary(_dictionarySource));
        if (changes.Count > 0)
        {
            StatusText = "Поиск подписей и печатей подстроен под ваши прошлые решения: " + string.Join("; ", changes);
        }

        Pages.CollectionChanged += (_, _) => HasPages = Pages.Count > 0;
        WatchDictionaryFile();
    }

    [RelayCommand]
    private void OpenDictionary()
    {
        try
        {
            _dictionarySource.EnsureExists(RedactionDictionary.DefaultFileContent);
            Process.Start(new ProcessStartInfo(_dictionarySource.FilePath) { UseShellExecute = true });
            StatusText = "Словарь открыт. После сохранения файла документ будет проверен заново автоматически.";
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось открыть словарь ({_dictionarySource.FilePath}): {ex.Message}";
        }
    }

    partial void OnUseVisionModelChanged(bool value)
    {
        if (_visionSettings.Enabled == value) return;
        _visionSettings.Enabled = value;
        _visionSettings.Save();
        StatusText = value
            ? $"Сканы и фото будут дополнительно проверяться моделью {_visionSettings.Model} ({_visionSettings.Endpoint})"
            : "Проверка моделью выключена";

        if (IsDetecting)
        {
            _dictionaryChangePending = true;
            return;
        }

        _ = RedetectAfterSettingsChangeAsync();
    }

    private async Task RedetectAfterSettingsChangeAsync()
    {
        foreach (var item in Queue)
        {
            item.ClearCachedSuggestions();
        }

        if (SelectedItem is null || Pages.Count == 0) return;
        await RescanDocumentAsync();
    }

    partial void OnZoomLevelChanged(double value)
    {
        foreach (var page in Pages)
        {
            page.NotifyZoomChanged();
        }
    }

    [RelayCommand]
    private void ZoomIn() => ZoomLevel = Math.Min(MaxZoom, Math.Round(ZoomLevel + ZoomStep, 2));

    [RelayCommand]
    private void ZoomOut() => ZoomLevel = Math.Max(MinZoom, Math.Round(ZoomLevel - ZoomStep, 2));

    [RelayCommand]
    private void ResetZoom() => ZoomLevel = 1.0;

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var files = await _filePicker.PickPdfFilesAsync("Выберите PDF-файл(ы)");
        if (files.Count == 0) return;

        var added = 0;
        foreach (var path in files)
        {
            if (Queue.Any(q => q.FilePath == path)) continue;
            Queue.Add(new PdfQueueItem(path));
            added++;
        }

        if (string.IsNullOrEmpty(OutputFolder))
        {
            var firstFolder = Path.GetDirectoryName(files[0]);
            if (!string.IsNullOrEmpty(firstFolder))
            {
                OutputFolder = Path.Combine(firstFolder, "redacted");
            }
        }

        if (SelectedItem is null && Queue.Count > 0)
        {
            SelectedItem = Queue[0];
            return;
        }

        StatusText = added > 0 ? $"Добавлено файлов: {added}" : "Эти файлы уже в очереди";
    }

    [RelayCommand]
    private async Task ChooseOutputFolderAsync()
    {
        var path = await _filePicker.PickFolderAsync("Куда сохранять обработанные файлы");
        if (path is not null)
        {
            OutputFolder = path;
        }
    }

    partial void OnSelectedItemChanged(PdfQueueItem? value)
    {
        _ = LoadSelectedItemAsync(value);
    }

    private async Task LoadSelectedItemAsync(PdfQueueItem? item)
    {
        var token = StartNewBackgroundWork();

        foreach (var oldPage in Pages)
        {
            oldPage.PageImage?.Dispose();
        }
        Pages.Clear();

        if (item is null)
        {
            StatusText = "Готово";
            return;
        }

        try
        {
            if (item.PageCount == 0)
            {
                item.PageCount = await _renderService.GetPageCountAsync(item.FilePath);
            }
            if (token.IsCancellationRequested) return;

            if (item.Status == PdfStatus.Pending)
            {
                item.Status = PdfStatus.Editing;
            }

            await LoadAndDetectAllPagesAsync(item, token);
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                StatusText = $"Не удалось открыть {item.FileName}: {ex.Message}";
            }
        }
    }

    private CancellationToken StartNewBackgroundWork()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        return _loadCts.Token;
    }

    private void BeginBusy()
    {
        _busyCount++;
        IsDetecting = true;
    }

    private void EndBusy()
    {
        _busyCount = Math.Max(0, _busyCount - 1);
        IsDetecting = _busyCount > 0;
        if (!IsDetecting && _dictionaryChangePending)
        {
            _dictionaryChangePending = false;
            _ = RedetectAfterDictionaryChangeAsync();
        }
    }

    private async Task LoadAndDetectAllPagesAsync(PdfQueueItem item, CancellationToken ct)
    {
        BeginBusy();
        try
        {
            for (var pageIndex = 0; pageIndex < item.PageCount; pageIndex++)
            {
                if (ct.IsCancellationRequested) return;

                StatusText = $"Открываю страницу {pageIndex + 1} из {item.PageCount}...";

                using var bitmap = await _renderService.RenderPageAsync(item.FilePath, pageIndex, DisplayDpi);
                if (ct.IsCancellationRequested) return;

                var pageVm = new PageViewModel(this, pageIndex)
                {
                    PageImage = ImageConversion.ToAvaloniaBitmap(bitmap, DisplayMaxWidthPx)
                };
                foreach (var r in item.GetRectsForPage(pageIndex))
                {
                    pageVm.Rectangles.Add(r);
                }

                Pages.Add(pageVm);

                await DetectPageAsync(item, pageVm, ct);
            }

            if (!ct.IsCancellationRequested)
            {
                StatusText = $"Готово. Страниц: {item.PageCount}. Проверьте рамки и нажмите «Сохранить PDF».";
            }
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task RescanDocumentAsync()
    {
        if (SelectedItem is null || Pages.Count == 0 || IsDetecting) return;

        var item = SelectedItem;
        item.ClearCachedSuggestions();
        var token = StartNewBackgroundWork();
        var pages = Pages.ToList();

        BeginBusy();
        try
        {
            foreach (var pageVm in pages)
            {
                if (token.IsCancellationRequested) return;
                StatusText = $"Проверяю заново страницу {pageVm.PageNumber} из {pages.Count}...";
                await DetectPageAsync(item, pageVm, token);
            }

            if (!token.IsCancellationRequested)
            {
                StatusText = $"Документ проверен заново, страниц: {pages.Count}";
            }
        }
        finally
        {
            EndBusy();
        }
    }

    private void WatchDictionaryFile()
    {
        try
        {
            var folder = Path.GetDirectoryName(_dictionarySource.FilePath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            _dictionaryWatcher = new FileSystemWatcher(folder, Path.GetFileName(_dictionarySource.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _dictionaryWatcher.Changed += OnDictionaryFileChanged;
            _dictionaryWatcher.Created += OnDictionaryFileChanged;
            _dictionaryWatcher.Renamed += OnDictionaryFileChanged;
            _dictionaryWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Не удалось следить за изменениями словаря: {ex.Message}");
            _dictionaryWatcher = null;
        }
    }

    private void OnDictionaryFileChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(() => _ = ApplyDictionaryChangeAsync());

    private async Task ApplyDictionaryChangeAsync()
    {
        var version = ++_dictionaryChangeVersion;
        await Task.Delay(DictionaryChangeDelay);
        if (version != _dictionaryChangeVersion) return;

        if (IsDetecting)
        {
            _dictionaryChangePending = true;
            return;
        }

        await RedetectAfterDictionaryChangeAsync();
    }

    private async Task RedetectAfterDictionaryChangeAsync()
    {
        foreach (var item in Queue)
        {
            item.ClearCachedSuggestions();
        }

        if (SelectedItem is null || Pages.Count == 0)
        {
            StatusText = "Словарь обновлён";
            return;
        }

        await RescanDocumentAsync();
    }

    private async Task DetectPageAsync(PdfQueueItem item, PageViewModel pageVm, CancellationToken ct)
    {
        if (item.TryGetCachedSuggestions(pageVm.PageIndex, out var cached))
        {
            ShowSuggestions(item, pageVm, cached);
            return;
        }

        try
        {
            var suggestions = await DetectSuggestionsAsync(item, pageVm.PageIndex);
            if (ct.IsCancellationRequested) return;

            item.SetCachedSuggestions(pageVm.PageIndex, suggestions);
            item.TryGetCachedSuggestions(pageVm.PageIndex, out var kept);
            ShowSuggestions(item, pageVm, kept);
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка распознавания на странице {pageVm.PageNumber}: {ex.Message}";
        }
    }

    private async Task EnsureAllPagesDetectedAsync(PdfQueueItem item, string statusPrefix)
    {
        if (item.PageCount == 0)
        {
            item.PageCount = await _renderService.GetPageCountAsync(item.FilePath);
        }

        for (var pageIndex = 0; pageIndex < item.PageCount; pageIndex++)
        {
            if (item.TryGetCachedSuggestions(pageIndex, out _)) continue;
            StatusText = $"{statusPrefix}: поиск на странице {pageIndex + 1}/{item.PageCount}";
            item.SetCachedSuggestions(pageIndex, await DetectSuggestionsAsync(item, pageIndex));
        }
    }

    private static void ShowSuggestions(PdfQueueItem item, PageViewModel pageVm, List<SuggestedRect> suggestions)
    {
        var existing = item.GetRectsForPage(pageVm.PageIndex);
        pageVm.Suggestions.Clear();
        foreach (var s in suggestions)
        {
            if (existing.Any(r => r.Intersects(s))) continue;
            pageVm.Suggestions.Add(s);
        }
    }

    private async Task<List<SuggestedRect>> DetectSuggestionsAsync(PdfQueueItem item, int pageIndex)
    {
        await _detectGate.WaitAsync();
        try
        {
            ModelSecondReading? secondReading = null;
            if (UseVisionModel)
            {
                if (await _visionClient.IsAvailableAsync())
                {
                    secondReading = _secondReading;
                }
                else
                {
                    StatusText = $"Модель {_visionClient.Model} недоступна ({_visionClient.Endpoint}) — страница {pageIndex + 1} проверена без неё. Запущена ли Ollama?";
                }
            }

            var detection = await Task.Run(async () =>
            {
                using var bitmap = await _renderService.RenderPageAsync(item.FilePath, pageIndex, DetectionDpi);
                var pageText = _pageTextExtractor.Extract(item.FilePath, pageIndex, bitmap);
                return await PageDetection.DetectAsync(_detector, bitmap, pageText.Lines, secondReading, CancellationToken.None);
            });

            var reading = detection.SecondReading;
            if (reading?.Error is not null)
            {
                StatusText = $"Страница {pageIndex + 1}: модель ответила с ошибкой — {reading.Error}";
            }
            else if (reading is not null && reading.Regions > 0)
            {
                StatusText = $"Страница {pageIndex + 1}: модель проверила мест: {reading.Regions}, исправлено строк: {reading.Replaced}, добавлено: {reading.Added}";
            }

            return detection.Suggestions;
        }
        finally
        {
            _detectGate.Release();
        }
    }

    [RelayCommand]
    private async Task SaveRedactedAsync()
    {
        if (SelectedItem is null)
        {
            StatusText = "Выберите файл в списке слева";
            return;
        }

        if (IsDetecting)
        {
            StatusText = "Дождитесь окончания проверки документа — иначе непроверенные страницы сохранятся без закрашивания";
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            StatusText = "Сначала укажите папку для сохранения";
            return;
        }

        var item = SelectedItem;
        BeginBusy();
        try
        {
            StatusText = $"Сохраняю {item.FileName}...";
            var outputPath = await SaveItemAsync(item, $"Сохранение {item.FileName}");
            StatusText = $"Сохранено: {outputPath}";
            _ = _feedbackRecorder.RecordImplicitAcceptancesAsync(item);
        }
        catch (Exception ex)
        {
            item.Status = PdfStatus.Error;
            StatusText = $"Не сохранено: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand]
    private async Task AutoProcessQueueAsync()
    {
        if (IsDetecting)
        {
            StatusText = "Дождитесь окончания текущей проверки";
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            StatusText = "Сначала укажите папку для сохранения";
            return;
        }

        var items = Queue.ToList();
        if (items.Count == 0)
        {
            StatusText = "Очередь пуста — добавьте PDF-файлы";
            return;
        }

        BeginBusy();
        var done = 0;
        var failed = 0;
        try
        {
            foreach (var item in items)
            {
                try
                {
                    await SaveItemAsync(item, $"Автообработка {done + failed + 1}/{items.Count}: {item.FileName}");
                    done++;
                }
                catch
                {
                    item.Status = PdfStatus.Error;
                    failed++;
                }
            }
        }
        finally
        {
            EndBusy();
        }

        StatusText = failed == 0
            ? $"Автообработка завершена: {done} файл(ов) сохранено в {OutputFolder}"
            : $"Автообработка: сохранено {done}, с ошибкой {failed} (отмечены в списке)";
    }

    private async Task<string> SaveItemAsync(PdfQueueItem item, string statusPrefix)
    {
        Directory.CreateDirectory(OutputFolder);
        await EnsureAllPagesDetectedAsync(item, statusPrefix);
        var outputPath = ResolveOutputPath(item);
        await _redactionService.SaveRedactedPdfAsync(item, outputPath, OutputDpi);
        item.Status = PdfStatus.Done;
        return outputPath;
    }

    internal void AddRectangleToPage(PageViewModel page, RedactionRect rect)
    {
        if (SelectedItem is null) return;
        SelectedItem.GetRectsForPage(page.PageIndex).Add(rect);
        page.Rectangles.Add(rect);
    }

    internal void RemoveRectangleFromPage(PageViewModel page, RedactionRect rect)
    {
        if (SelectedItem is null) return;
        SelectedItem.GetRectsForPage(page.PageIndex).Remove(rect);
        page.Rectangles.Remove(rect);
    }

    internal void ClassifyManualRectangle(PageViewModel page, ManualRectangleClassification classification)
    {
        if (SelectedItem is null) return;
        _feedbackRecorder.RecordManualRectangle(SelectedItem, page.PageIndex, classification, page.Suggestions);
    }

    internal void AcceptSuggestionOnPage(PageViewModel page, SuggestedRect suggestion, ManualRedactionType? correctedType = null)
    {
        if (SelectedItem is not null)
        {
            _feedbackRecorder.RecordSuggestionDecision(
                SelectedItem, page.PageIndex, suggestion, FeedbackEventType.SuggestionAccepted, correctedType);
        }
        page.Suggestions.Remove(suggestion);
        SelectedItem?.RemoveCachedSuggestion(page.PageIndex, suggestion);
        AddRectangleToPage(page, suggestion.ToRedactionRect());
    }

    internal void RejectSuggestionOnPage(PageViewModel page, SuggestedRect suggestion)
    {
        if (SelectedItem is not null)
        {
            _feedbackRecorder.RecordSuggestionDecision(
                SelectedItem, page.PageIndex, suggestion, FeedbackEventType.SuggestionRejected);
        }
        page.Suggestions.Remove(suggestion);
        SelectedItem?.RemoveCachedSuggestion(page.PageIndex, suggestion);
        SelectedItem?.RememberRejection(page.PageIndex, suggestion);
    }

    private string ResolveOutputPath(PdfQueueItem item)
    {
        var baseName = item.FileName.StartsWith(RedactedFilePrefix, StringComparison.OrdinalIgnoreCase)
            ? item.FileName
            : RedactedFilePrefix + item.FileName;
        var stem = Path.GetFileNameWithoutExtension(baseName);
        var extension = Path.GetExtension(baseName);
        var sourcePath = Path.GetFullPath(item.FilePath);

        for (var n = 1; ; n++)
        {
            var name = n == 1 ? baseName : $"{stem} ({n}){extension}";
            var path = Path.GetFullPath(Path.Combine(OutputFolder, name));
            if (string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (_outputOwners.TryGetValue(path, out var owner) &&
                !string.Equals(owner, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;

            _outputOwners[path] = sourcePath;
            return path;
        }
    }

    public void Dispose()
    {
        _dictionaryWatcher?.Dispose();
        _dictionaryWatcher = null;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        foreach (var page in Pages)
        {
            page.PageImage?.Dispose();
        }

        if (_detectGate.Wait(TimeSpan.FromSeconds(10)))
        {
            _ocrService.Dispose();
            _visionClient.Dispose();
        }
    }
}
