using System;
using System.IO;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace ConfidentialDataMasker.Services;

public static class ImageConversion
{

    public static Bitmap ToAvaloniaBitmap(SKBitmap skBitmap, int? maxWidthPx = null)
    {
        if (maxWidthPx is int maxWidth && maxWidth > 0 && skBitmap.Width > maxWidth)
        {
            var scale = (double)maxWidth / skBitmap.Width;
            var newWidth = maxWidth;
            var newHeight = Math.Max(1, (int)Math.Round(skBitmap.Height * scale));

            using var scaled = new SKBitmap(newWidth, newHeight, skBitmap.ColorType, skBitmap.AlphaType);
            using (var canvas = new SKCanvas(scaled))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(skBitmap, new SKRect(0, 0, newWidth, newHeight));
            }

            return Encode(scaled);
        }

        return Encode(skBitmap);
    }

    private static Bitmap Encode(SKBitmap bitmap)
    {
        using var skImage = SKImage.FromBitmap(bitmap);
        using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream();
        data.SaveTo(stream);
        stream.Position = 0;
        return new Bitmap(stream);
    }
}
