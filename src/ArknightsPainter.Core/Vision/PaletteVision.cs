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

    public bool VerifySelectionGlow(
        byte[] beforeScreenshotPng,
        byte[] afterScreenshotPng,
        PixelRect paletteViewport,
        PixelPoint selectedCenter)
    {
        using var before = SKBitmap.Decode(beforeScreenshotPng);
        using var after = SKBitmap.Decode(afterScreenshotPng);
        if (before is null || after is null || before.Width != after.Width || before.Height != after.Height)
        {
            return false;
        }

        var pitch = paletteViewport.Width / 4.0;
        return HasCyanRing(after, selectedCenter, pitch) ||
               HasNewSelectionRing(before, after, selectedCenter, pitch);
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
        var rowCenters = FindRowCenters(bitmap, viewport, columns, pitch);
        if (rowCenters.Count == 0)
        {
            var rows = Math.Max(0, (int)Math.Floor(viewport.Height / pitch));
            rowCenters = Enumerable.Range(0, rows)
                .Select(row => (int)Math.Round(viewport.Y + ((row + 0.5) * pitch)))
                .ToArray();
        }

        var sampleRadius = Math.Max(2, (int)Math.Round(pitch * 0.16));
        var result = new List<VisibleSwatch>(rowCenters.Count * columns);
        for (var row = 0; row < rowCenters.Count; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var center = new PixelPoint(
                    (int)Math.Round(viewport.X + ((column + 0.5) * pitch)),
                    rowCenters[row]);
                var color = MedianColor(bitmap, center, sampleRadius);
                result.Add(new VisibleSwatch(column, row, center, color, HasCyanRing(bitmap, center, pitch)));
            }
        }

        return result;
    }

    private static IReadOnlyList<int> FindRowCenters(
        SKBitmap bitmap,
        PixelRect viewport,
        int columns,
        double pitch)
    {
        var activeRows = new bool[viewport.Height];
        var patchRadius = Math.Max(1, (int)Math.Round(pitch * 0.06));
        for (var offsetY = 0; offsetY < viewport.Height; offsetY++)
        {
            var y = viewport.Y + offsetY;
            var contrastingColumns = 0;
            for (var column = 0; column < columns; column++)
            {
                var cellLeft = viewport.X + (column * pitch);
                var centerX = (int)Math.Round(cellLeft + (pitch * 0.5));
                var leftGapX = (int)Math.Round(cellLeft + (pitch * 0.04));
                var rightGapX = (int)Math.Round(cellLeft + (pitch * 0.96));
                var center = MedianColor(bitmap, new PixelPoint(centerX, y), patchRadius, 1);
                var leftGap = MedianColor(bitmap, new PixelPoint(leftGapX, y), 1, 1);
                var rightGap = MedianColor(bitmap, new PixelPoint(rightGapX, y), 1, 1);
                if (RgbDistance(center, leftGap) >= 18 || RgbDistance(center, rightGap) >= 18)
                {
                    contrastingColumns++;
                }
            }

            activeRows[offsetY] = contrastingColumns >= Math.Max(2, columns - 1);
        }

        var minimumHeight = Math.Max(4, (int)Math.Round(pitch * 0.28));
        var maximumHeight = Math.Max(minimumHeight, (int)Math.Round(pitch * 1.08));
        var centers = new List<int>();
        for (var start = 0; start < activeRows.Length;)
        {
            if (!activeRows[start])
            {
                start++;
                continue;
            }

            var end = start;
            while (end + 1 < activeRows.Length && activeRows[end + 1])
            {
                end++;
            }

            var height = end - start + 1;
            if (height >= minimumHeight && height <= maximumHeight)
            {
                centers.Add(viewport.Y + ((start + end) / 2));
            }

            start = end + 1;
        }

        return centers;
    }

    private static RgbColor MedianColor(SKBitmap bitmap, PixelPoint center, int radius, int? verticalRadius = null)
    {
        var reds = new List<byte>();
        var greens = new List<byte>();
        var blues = new List<byte>();
        var step = Math.Max(1, radius / 5);
        var yRadius = verticalRadius ?? radius;
        for (var y = center.Y - yRadius; y <= center.Y + yRadius; y += step)
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
        var hitCount = 0;
        var sideHits = new int[4];
        foreach (var sample in EnumerateRing(bitmap, center, pitch))
        {
            if (!IsSelectionCyan(sample.Color))
            {
                continue;
            }

            hitCount++;
            sideHits[sample.Side]++;
        }

        var minimumHits = Math.Max(8, (int)Math.Round(pitch * 0.35));
        return hitCount >= minimumHits && sideHits.Count(hits => hits >= Math.Max(2, minimumHits / 10)) >= 2;
    }

    private static bool HasNewSelectionRing(
        SKBitmap before,
        SKBitmap after,
        PixelPoint center,
        double pitch)
    {
        var hitCount = 0;
        var sideHits = new int[4];
        foreach (var sample in EnumerateRing(after, center, pitch))
        {
            var previous = before.GetPixel(sample.X, sample.Y);
            if (RgbDistance(previous, sample.Color) < 18 || !IsSelectionHighlight(sample.Color))
            {
                continue;
            }

            hitCount++;
            sideHits[sample.Side]++;
        }

        var minimumHits = Math.Max(10, (int)Math.Round(pitch * 0.45));
        return hitCount >= minimumHits && sideHits.Count(hits => hits >= Math.Max(2, minimumHits / 10)) >= 2;
    }

    private static IEnumerable<RingSample> EnumerateRing(SKBitmap bitmap, PixelPoint center, double pitch)
    {
        var inner = Math.Max(2, (int)Math.Round(pitch * 0.30));
        var outer = Math.Max(inner + 1, (int)Math.Round(pitch * 0.56));
        for (var offsetY = -outer; offsetY <= outer; offsetY++)
        {
            for (var offsetX = -outer; offsetX <= outer; offsetX++)
            {
                var absoluteX = Math.Abs(offsetX);
                var absoluteY = Math.Abs(offsetY);
                if (Math.Max(absoluteX, absoluteY) < inner || Math.Max(absoluteX, absoluteY) > outer)
                {
                    continue;
                }

                var x = center.X + offsetX;
                var y = center.Y + offsetY;
                if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
                {
                    continue;
                }

                var side = absoluteX >= absoluteY
                    ? offsetX < 0 ? 0 : 1
                    : offsetY < 0 ? 2 : 3;
                yield return new RingSample(x, y, side, bitmap.GetPixel(x, y));
            }
        }
    }

    private static bool IsSelectionCyan(SKColor pixel)
    {
        var minimumCyanLead = pixel.Red >= 220 ? 3 : 8;
        return pixel.Green >= 225 && pixel.Blue >= 225 &&
               pixel.Green >= pixel.Red + minimumCyanLead && pixel.Blue >= pixel.Red + minimumCyanLead &&
               Math.Abs(pixel.Green - pixel.Blue) <= 70;
    }

    private static bool IsSelectionHighlight(SKColor pixel) =>
        IsSelectionCyan(pixel) || (pixel.Red >= 220 && pixel.Green >= 235 && pixel.Blue >= 235);

    private static double RgbDistance(RgbColor left, RgbColor right)
    {
        var red = left.R - right.R;
        var green = left.G - right.G;
        var blue = left.B - right.B;
        return Math.Sqrt((red * red) + (green * green) + (blue * blue));
    }

    private static double RgbDistance(SKColor left, SKColor right) =>
        RgbDistance(new RgbColor(left.Red, left.Green, left.Blue), new RgbColor(right.Red, right.Green, right.Blue));

    private readonly record struct RingSample(int X, int Y, int Side, SKColor Color);
}
