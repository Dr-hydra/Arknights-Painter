using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Imaging;
using ArknightsPainter.Core.Models;
using SkiaSharp;

namespace ArknightsPainter.Core.Vision;

public sealed class PaletteVision : IPaletteVision
{
    public IReadOnlyList<VisibleSwatch> ReadVisibleSwatches(
        byte[] screenshotPng,
        PixelRect paletteViewport,
        int columns = 4)
    {
        using var bitmap = SKBitmap.Decode(screenshotPng)
            ?? throw new InvalidDataException("Unable to decode screenshot.");
        return ReadVisibleSwatches(bitmap, paletteViewport, columns);
    }

    public bool ValidateVisiblePalette(
        byte[] screenshotPng,
        PixelRect paletteViewport,
        PaletteDefinition palette,
        double minimumMatchRatio = 0.65)
    {
        var swatches = ReadVisibleSwatches(screenshotPng, paletteViewport, palette.Columns);
        if (swatches.Count < palette.Columns)
        {
            return false;
        }

        var matches = swatches.Count(swatch => palette.Colors.Any(color =>
            ColorMath.DeltaE2000(swatch.Color, color.Color) <= 8));
        return matches / (double)swatches.Count >= minimumMatchRatio;
    }

    public bool VerifySelectionGlow(byte[] screenshotPng, PixelRect paletteViewport, PixelPoint selectedCenter)
    {
        using var bitmap = SKBitmap.Decode(screenshotPng);
        if (bitmap is null)
        {
            return false;
        }

        var pitch = paletteViewport.Width / 4.0;
        return HasCyanRing(bitmap, selectedCenter, pitch);
    }

    private static IReadOnlyList<VisibleSwatch> ReadVisibleSwatches(
        SKBitmap bitmap,
        PixelRect viewport,
        int columns)
    {
        if (!viewport.IsValid || viewport.Right > bitmap.Width || viewport.Bottom > bitmap.Height || columns <= 0)
        {
            return [];
        }

        var pitch = viewport.Width / (double)columns;
        var rows = Math.Max(0, (int)Math.Floor(viewport.Height / pitch));
        var sampleRadius = Math.Max(2, (int)Math.Round(pitch * 0.20));
        var result = new List<VisibleSwatch>(rows * columns);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var center = new PixelPoint(
                    (int)Math.Round(viewport.X + ((column + 0.5) * pitch)),
                    (int)Math.Round(viewport.Y + ((row + 0.5) * pitch)));
                var color = MedianColor(bitmap, center, sampleRadius);
                result.Add(new VisibleSwatch(column, row, center, color, HasCyanRing(bitmap, center, pitch)));
            }
        }

        return result;
    }

    private static RgbColor MedianColor(SKBitmap bitmap, PixelPoint center, int radius)
    {
        var reds = new List<byte>();
        var greens = new List<byte>();
        var blues = new List<byte>();
        var step = Math.Max(1, radius / 5);
        for (var y = center.Y - radius; y <= center.Y + radius; y += step)
        {
            for (var x = center.X - radius; x <= center.X + radius; x += step)
            {
                if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
                {
                    continue;
                }

                var pixel = bitmap.GetPixel(x, y);
                reds.Add(pixel.Red);
                greens.Add(pixel.Green);
                blues.Add(pixel.Blue);
            }
        }

        reds.Sort();
        greens.Sort();
        blues.Sort();
        var middle = reds.Count / 2;
        return reds.Count == 0
            ? default
            : new RgbColor(reds[middle], greens[middle], blues[middle]);
    }

    private static bool HasCyanRing(SKBitmap bitmap, PixelPoint center, double pitch)
    {
        var minimumRadius = Math.Max(3, (int)Math.Round(pitch * 0.40));
        var maximumRadius = Math.Max(minimumRadius, (int)Math.Floor(pitch * 0.49));
        var bestRatio = 0.0;
        for (var radius = minimumRadius; radius <= maximumRadius; radius++)
        {
            var hits = 0;
            var samples = 0;
            for (var offset = -radius; offset <= radius; offset += Math.Max(1, radius / 12))
            {
                foreach (var point in new[]
                         {
                             new PixelPoint(center.X + offset, center.Y - radius),
                             new PixelPoint(center.X + offset, center.Y + radius),
                             new PixelPoint(center.X - radius, center.Y + offset),
                             new PixelPoint(center.X + radius, center.Y + offset)
                         })
                {
                    if (point.X < 0 || point.Y < 0 || point.X >= bitmap.Width || point.Y >= bitmap.Height)
                    {
                        continue;
                    }

                    samples++;
                    var pixel = bitmap.GetPixel(point.X, point.Y);
                    if (IsSelectionCyan(pixel))
                    {
                        hits++;
                    }
                }
            }

            bestRatio = Math.Max(bestRatio, samples == 0 ? 0 : hits / (double)samples);
        }

        return bestRatio >= 0.12;
    }

    private static bool IsSelectionCyan(SKColor pixel) =>
        pixel.Green > 165 && pixel.Blue > 165 && pixel.Red < 165 &&
        pixel.Green - pixel.Red > 35 && pixel.Blue - pixel.Red > 35;
}
