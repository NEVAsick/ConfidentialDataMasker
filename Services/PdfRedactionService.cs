using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ConfidentialDataMasker.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public class PdfRedactionService
{
    private readonly PdfRenderService _renderService;

    public PdfRedactionService(PdfRenderService renderService)
    {
        _renderService = renderService;
    }

    public async Task SaveRedactedPdfAsync(PdfQueueItem item, string outputPath, int dpi = 200)
    {
        using var document = new PdfDocument();
        var streams = new List<MemoryStream>();
        var images = new List<XImage>();

        try
        {
            for (var pageIndex = 0; pageIndex < item.PageCount; pageIndex++)
            {
                using var pageBitmap = await _renderService.RenderPageAsync(item.FilePath, pageIndex, dpi);
                BakeRedactions(pageBitmap, item.GetAllRectsForPage(pageIndex));

                var pngStream = new MemoryStream();
                using (var skImage = SKImage.FromBitmap(pageBitmap))
                using (var data = skImage.Encode(SKEncodedImageFormat.Png, 100))
                {
                    data.SaveTo(pngStream);
                }
                pngStream.Position = 0;
                streams.Add(pngStream);

                var image = XImage.FromStream(pngStream);
                images.Add(image);
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(pageBitmap.Width * 72.0 / dpi);
                page.Height = XUnit.FromPoint(pageBitmap.Height * 72.0 / dpi);

                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            document.Save(outputPath);
        }
        finally
        {
            foreach (var image in images) image.Dispose();
            foreach (var s in streams) s.Dispose();
        }
    }

    private static void BakeRedactions(SKBitmap bitmap, List<RedactionRect> rects)
    {
        if (rects.Count == 0) return;

        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };

        foreach (var r in rects)
        {
            var rect = new SKRect(
                (float)(r.X * bitmap.Width),
                (float)(r.Y * bitmap.Height),
                (float)((r.X + r.Width) * bitmap.Width),
                (float)((r.Y + r.Height) * bitmap.Height));
            canvas.DrawRect(rect, paint);
        }
    }
}
