using ArknightsPainter.Core.Imaging;
using ArknightsPainter.Core.Models;
using ArknightsPainter.Core.Vision;
using SkiaSharp;

namespace ArknightsPainter.Core.Tests;

public sealed class VisionTests
{
    private static readonly PixelRect Board = new(447, 183, 844, 842);
    private static readonly PixelRect PaletteRegion = new(1433, 377, 420, 630);

    [Fact]
    public void Locator_UsesAnchorsAndFindsNonIntegralCellBoard()
    {
        var screenshot = CreateScreenshot();
        var locator = new ScreenLocator();

        var result = locator.Locate("test-device", screenshot);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Profile);
        Assert.InRange(result.Profile!.CanvasBounds.X, Board.X - 2, Board.X + 2);
        Assert.InRange(result.Profile.CanvasBounds.Y, Board.Y - 2, Board.Y + 2);
        Assert.InRange(result.Profile.CanvasBounds.Right, Board.Right - 2, Board.Right + 2);
        Assert.InRange(result.Profile.CanvasBounds.Bottom, Board.Bottom - 2, Board.Bottom + 2);
        Assert.True(result.Confidence >= 0.30);
    }

    [Fact]
    public void Locator_AnchorPointsIncreaseCanvasConfidence()
    {
        var locator = new ScreenLocator();
        var anchored = CreateScreenshot(includeAnchors: true);
        var gridOnly = CreateScreenshot(includeAnchors: false);

        var anchoredScore = locator.ScoreCanvas(anchored, Board);
        var gridOnlyScore = locator.ScoreCanvas(gridOnly, Board);

        Assert.True(anchoredScore > gridOnlyScore + 0.05,
            $"Expected anchor score {anchoredScore:F3} to exceed grid-only score {gridOnlyScore:F3}.");
    }

    [Fact]
    public void Locator_RejectsGridWithoutAnchorPoints()
    {
        var locator = new ScreenLocator();
        var result = locator.Locate("test-device", CreateScreenshot(includeAnchors: false));

        Assert.False(result.Success);
        Assert.Contains("定位点", result.Message);
    }

    [Fact]
    public void PaletteVision_SamplesCentersAndFindsSelectionGlow()
    {
        var screenshot = CreateScreenshot();
        var vision = new PaletteVision();

        var swatches = vision.ReadVisibleSwatches(screenshot, PaletteRegion);

        Assert.Equal(24, swatches.Count);
        Assert.True(ColorMath.DeltaE2000(swatches[0].Color, new RgbColor(34, 34, 34)) < 1);
        Assert.True(swatches[0].HasSelectionGlow);
        Assert.False(swatches[1].HasSelectionGlow);
        Assert.True(vision.VerifySelectionGlow(screenshot, PaletteRegion, swatches[0].Center));
    }

    [Fact]
    public void PaletteVision_FindsGlowInRealMuMuScreenshot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "selected-black-1920x1080.png");
        var screenshot = File.ReadAllBytes(path);
        var vision = new PaletteVision();

        var swatches = vision.ReadVisibleSwatches(screenshot, new PixelRect(1433, 377, 420, 650));

        Assert.True(swatches[0].HasSelectionGlow);
        Assert.True(vision.VerifySelectionGlow(
            screenshot,
            new PixelRect(1433, 377, 420, 650),
            swatches[0].Center));
        Assert.All(swatches.Skip(1), swatch => Assert.False(swatch.HasSelectionGlow));
    }

    [Fact]
    public void PaletteVision_RecognizesEveryRealPaletteSelection()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "selected-black-1920x1080.png");
        var source = File.ReadAllBytes(path);
        using var original = SKBitmap.Decode(source);
        var vision = new PaletteVision();
        var viewport = new PixelRect(1433, 377, 420, 650);
        var pitch = viewport.Width / 4.0;

        for (var index = 0; index < 24; index++)
        {
            using var bitmap = original.Copy();
            using var canvas = new SKCanvas(bitmap);
            using var background = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(62, 62, 62) };
            canvas.DrawRect(viewport.X, viewport.Y, viewport.Width, viewport.Height, background);
            var column = index % 4;
            var row = index / 4;
            var center = new PixelPoint(
                (int)Math.Round(viewport.X + ((column + 0.5) * pitch)),
                (int)Math.Round(viewport.Y + ((row + 0.5) * pitch)));
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Black };
            canvas.DrawRect(center.X - 45, center.Y - 45, 90, 90, fill);
            using var glow = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 5,
                Color = new SKColor(25, 248, 245)
            };
            canvas.DrawRect(center.X - 51, center.Y - 51, 102, 102, glow);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            var detected = vision.ReadVisibleSwatches(data.ToArray(), viewport);

            Assert.True(detected[index].HasSelectionGlow, $"Selection glow not found for swatch {index}.");
        }
    }

    [Fact]
    public void PaletteVision_FindsActualRowsWhenViewportIncludesPaletteHeader()
    {
        var viewport = new PixelRect(80, 40, 400, 540);
        using var bitmap = new SKBitmap(560, 640);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(55, 55, 55));
        using var paint = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(225, 225, 225) };
        canvas.DrawRect(105, 75, 105, 24, paint);

        var colors = new[]
        {
            new SKColor(20, 30, 40), new SKColor(180, 180, 180),
            new SKColor(235, 225, 210), new SKColor(255, 255, 255)
        };
        const int firstRowTop = 150;
        const int rowPitch = 75;
        for (var row = 0; row < 5; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                paint.Color = colors[column];
                canvas.DrawRect(90 + (column * 100), firstRowTop + (row * rowPitch), 80, 60, paint);
            }
        }

        var screenshot = Encode(bitmap);
        var swatches = new PaletteVision().ReadVisibleSwatches(screenshot, viewport);

        Assert.Equal(20, swatches.Count);
        Assert.Equal(firstRowTop + 29, swatches[0].Center.Y);
        Assert.Equal(firstRowTop + (4 * rowPitch) + 29, swatches[^1].Center.Y);
        Assert.True(ColorMath.DeltaE2000(swatches[3].Color, new RgbColor(255, 255, 255)) < 1);
    }

    [Fact]
    public void PaletteVision_AcceptsNearWhiteSelectionGlow()
    {
        var viewport = new PixelRect(20, 20, 400, 100);
        using var before = new SKBitmap(460, 160);
        using var after = new SKBitmap(460, 160);
        DrawWhitePalette(before, viewport, drawGlow: false);
        DrawWhitePalette(after, viewport, drawGlow: true);
        var beforePng = Encode(before);
        var afterPng = Encode(after);
        var selectedCenter = new PixelPoint(70, 70);
        var vision = new PaletteVision();

        Assert.False(vision.VerifySelectionGlow(beforePng, viewport, selectedCenter));
        Assert.True(vision.VerifySelectionGlow(afterPng, viewport, selectedCenter));
        Assert.True(vision.VerifySelectionGlow(beforePng, afterPng, viewport, selectedCenter));
    }

    [Fact]
    public void PaletteVision_DoesNotTreatCyanPaintAsSelectionGlow()
    {
        var viewport = new PixelRect(20, 20, 400, 100);
        using var bitmap = new SKBitmap(460, 160);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(55, 55, 55));
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(145, 216, 230) };
        for (var column = 0; column < 4; column++)
        {
            canvas.DrawRect(30 + (column * 100), 30, 80, 80, fill);
        }

        Assert.False(new PaletteVision().VerifySelectionGlow(
            Encode(bitmap),
            viewport,
            new PixelPoint(70, 70)));
    }

    private static byte[] CreateScreenshot(bool includeAnchors = true)
    {
        using var bitmap = new SKBitmap(1920, 1080);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(205, 205, 205));
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White };
        canvas.DrawRect(Board.X, Board.Y, Board.Width, Board.Height, fill);
        using var grid = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(232, 232, 232) };
        for (var index = 0; index <= Artwork24.Size; index++)
        {
            var x = Board.X + (float)(index * Board.Width / (double)Artwork24.Size);
            var y = Board.Y + (float)(index * Board.Height / (double)Artwork24.Size);
            canvas.DrawLine(x, Board.Y, x, Board.Bottom, grid);
            canvas.DrawLine(Board.X, y, Board.Right, y, grid);
        }

        if (includeAnchors)
        {
            DrawCanvasAnchors(canvas);
        }

        var palette = new[]
        {
            new RgbColor(34,34,34), new RgbColor(180,180,180), new RgbColor(234,231,223), new RgbColor(255,255,255),
            new RgbColor(211,47,54), new RgbColor(156,10,0), new RgbColor(214,12,74), new RgbColor(230,150,141),
            new RgbColor(254,152,117), new RgbColor(247,208,192), new RgbColor(252,239,234), new RgbColor(251,246,232),
            new RgbColor(220,210,200), new RgbColor(226,206,171), new RgbColor(213,99,34), new RgbColor(212,140,66),
            new RgbColor(242,153,0), new RgbColor(249,201,51), new RgbColor(252,228,153), new RgbColor(179,180,122),
            new RgbColor(194,218,114), new RgbColor(108,110,0), new RgbColor(177,145,85), new RgbColor(169,143,116)
        };
        var pitch = PaletteRegion.Width / 4f;
        for (var index = 0; index < palette.Length; index++)
        {
            var column = index % 4;
            var row = index / 4;
            var centerX = PaletteRegion.X + ((column + 0.5f) * pitch);
            var centerY = PaletteRegion.Y + ((row + 0.5f) * pitch);
            fill.Color = new SKColor(palette[index].R, palette[index].G, palette[index].B);
            canvas.DrawRect(centerX - 45, centerY - 45, 90, 90, fill);
            if (index == 0)
            {
                using var glow = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 6,
                    Color = new SKColor(30, 245, 245)
                };
                canvas.DrawRect(centerX - 48, centerY - 48, 96, 96, glow);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawWhitePalette(SKBitmap bitmap, PixelRect viewport, bool drawGlow)
    {
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(55, 55, 55));
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White };
        for (var column = 0; column < 4; column++)
        {
            var centerX = viewport.X + 50 + (column * 100);
            canvas.DrawRect(centerX - 40, 30, 80, 80, fill);
        }

        if (!drawGlow)
        {
            return;
        }

        using var glow = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 6,
            Color = new SKColor(250, 255, 255)
        };
        canvas.DrawRect(27, 27, 86, 86, glow);
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawCanvasAnchors(SKCanvas canvas)
    {
        var scaleX = Board.Width / 844f;
        var scaleY = Board.Height / 842f;
        var cornerOffsetX = 18 * scaleX;
        var cornerTop = 20 * scaleY;
        var cornerNear = 6 * scaleY;
        var outerFar = 38 * scaleX;
        var outerNear = 27 * scaleX;
        var crossGap = 28 * scaleX;
        var crossRadius = 8 * ((scaleX + scaleY) / 2);
        var crossArm = 14 * ((scaleX + scaleY) / 2);
        var centerY = Board.Y + (Board.Height / 2f);
        using var marker = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            Color = new SKColor(80, 80, 80),
            IsAntialias = true
        };

        canvas.DrawLine(Board.X - outerFar, Board.Y, Board.X - outerNear, Board.Y, marker);
        canvas.DrawLine(Board.X - cornerOffsetX, Board.Y - cornerTop, Board.X - cornerOffsetX, Board.Y - cornerNear, marker);
        canvas.DrawLine(Board.Right + outerNear, Board.Y, Board.Right + outerFar, Board.Y, marker);
        canvas.DrawLine(Board.Right + cornerOffsetX, Board.Y - cornerTop, Board.Right + cornerOffsetX, Board.Y - cornerNear, marker);
        canvas.DrawLine(Board.X - outerFar, Board.Bottom, Board.X - outerNear, Board.Bottom, marker);
        canvas.DrawLine(Board.X - cornerOffsetX, Board.Bottom + cornerNear, Board.X - cornerOffsetX, Board.Bottom + cornerTop, marker);
        canvas.DrawLine(Board.Right + outerNear, Board.Bottom, Board.Right + outerFar, Board.Bottom, marker);
        canvas.DrawLine(Board.Right + cornerOffsetX, Board.Bottom + cornerNear, Board.Right + cornerOffsetX, Board.Bottom + cornerTop, marker);

        DrawCross(Board.X - crossGap, centerY);
        DrawCross(Board.Right + crossGap, centerY);
        return;

        void DrawCross(float x, float y)
        {
            canvas.DrawCircle(x, y, crossRadius, marker);
            canvas.DrawLine(x - crossArm, y, x + crossArm, y, marker);
            canvas.DrawLine(x, y - crossArm, x, y + crossArm, marker);
        }
    }
}
