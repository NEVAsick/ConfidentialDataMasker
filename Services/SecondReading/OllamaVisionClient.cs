using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public sealed class OllamaVisionClient : IDisposable
{
    private const string ReadPrompt =
        "На изображении фрагмент отсканированного документа на русском языке. " +
        "Выпиши весь текст, который на нём виден, строка за строкой, включая текст под печатью или подписью. " +
        "Пиши только сам текст, без пояснений.";
    private const string LocatePromptFormat =
        "На изображении фрагмент отсканированного документа на русском языке. Найди на нём текст «{0}». " +
        "Ответь только JSON без пояснений: {{\"bbox_2d\": [x1, y1, x2, y2]}} – рамка вокруг этого текста.";
    private const int MaxAnswerTokens = 400;
    private const double RelativeCoordinateScale = 1000.0;
    private const int MinTimeoutSeconds = 10;

    private static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AvailabilityCacheTime = TimeSpan.FromSeconds(30);
    private static readonly Regex BracketPattern = new(@"\[([^\[\]]*)\]", RegexOptions.Compiled);
    private static readonly Regex NumberPattern = new(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private bool? _available;
    private DateTime _availabilityCheckedAt;
    private bool _sendThinkFlag = true;

    public OllamaVisionClient(VisionModelSettings settings)
    {
        Model = settings.Model;
        Endpoint = settings.Endpoint.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(Endpoint + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(MinTimeoutSeconds, settings.TimeoutSeconds))
        };
    }

    public string Model { get; }

    public string Endpoint { get; }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available is not null && DateTime.UtcNow - _availabilityCheckedAt < AvailabilityCacheTime) return _available.Value;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AvailabilityTimeout);
            using var response = await _http.GetAsync("api/tags", cts.Token);
            _available = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _available = false;
        }

        _availabilityCheckedAt = DateTime.UtcNow;
        return _available.Value;
    }

    public async Task<List<string>> ReadLinesAsync(SKBitmap page, SKRectI region, CancellationToken ct)
    {
        var answer = await AskAsync(page, region, ReadPrompt, ct);
        return answer
            .Split('\n')
            .Select(l => WhitespacePattern.Replace(l, " ").Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal))
            .ToList();
    }

    public async Task<SKRect?> LocateAsync(SKBitmap page, SKRectI region, string text, CancellationToken ct)
    {
        var answer = await AskAsync(page, region, string.Format(CultureInfo.InvariantCulture, LocatePromptFormat, text), ct);
        var bracket = BracketPattern.Match(answer);
        if (!bracket.Success) return null;

        var numbers = NumberPattern.Matches(bracket.Groups[1].Value)
            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
            .Take(4)
            .ToList();
        if (numbers.Count < 4) return null;

        var scaleX = region.Width / RelativeCoordinateScale;
        var scaleY = region.Height / RelativeCoordinateScale;
        var left = region.Left + Math.Min(numbers[0], numbers[2]) * scaleX;
        var right = region.Left + Math.Max(numbers[0], numbers[2]) * scaleX;
        var top = region.Top + Math.Min(numbers[1], numbers[3]) * scaleY;
        var bottom = region.Top + Math.Max(numbers[1], numbers[3]) * scaleY;
        if (right - left < 1 || bottom - top < 1) return null;

        return new SKRect((float)left, (float)top, (float)right, (float)bottom);
    }

    private async Task<string> AskAsync(SKBitmap page, SKRectI region, string prompt, CancellationToken ct)
    {
        var image = EncodeRegion(page, region);
        while (true)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = Model,
                ["prompt"] = prompt,
                ["images"] = new[] { image },
                ["stream"] = false,
                ["options"] = new Dictionary<string, object> { ["temperature"] = 0, ["num_predict"] = MaxAnswerTokens }
            };
            if (_sendThinkFlag) body["think"] = false;

            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("api/generate", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                if (_sendThinkFlag && json.Contains("think", StringComparison.OrdinalIgnoreCase))
                {
                    _sendThinkFlag = false;
                    continue;
                }
                throw new HttpRequestException($"Ollama ответила {(int)response.StatusCode}: {json}");
            }

            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("response", out var text) ? text.GetString() ?? "" : "";
        }
    }

    private static string EncodeRegion(SKBitmap page, SKRectI region)
    {
        using var crop = new SKBitmap();
        if (!page.ExtractSubset(crop, region)) throw new InvalidOperationException("Не удалось вырезать область страницы");

        using var image = SKImage.FromBitmap(crop);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToBase64String(data.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
