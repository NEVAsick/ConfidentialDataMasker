using System.IO;
using System.Threading.Tasks;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public class PdfRenderService
{
    public Task<int> GetPageCountAsync(string filePath)
    {
        return Task.Run(() =>
        {
            using var stream = File.OpenRead(filePath);
            return PDFtoImage.Conversion.GetPageCount(stream);
        });
    }

    public Task<SKBitmap> RenderPageAsync(string filePath, int pageIndex, int dpi = 150)
    {
        return Task.Run(() =>
        {
            SKBitmap rendered;
            using (var stream = File.OpenRead(filePath))
            {
                rendered = PDFtoImage.Conversion.ToImage(stream, password: null, page: pageIndex, options: new PDFtoImage.RenderOptions(Dpi: dpi));
            }

            var normalized = PageImageNormalizer.FlattenIfPhotographed(rendered);
            if (!ReferenceEquals(normalized, rendered)) rendered.Dispose();
            return normalized;
        });
    }
}
