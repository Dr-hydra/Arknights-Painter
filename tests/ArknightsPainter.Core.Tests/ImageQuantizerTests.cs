using ArknightsPainter.Core.Imaging;
using ArknightsPainter.Core.Models;
using SkiaSharp;

namespace ArknightsPainter.Core.Tests;

public sealed class ImageQuantizerTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ArknightsPainter-{Guid.NewGuid():N}");

    public ImageQuantizerTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task Contain_PreservesAspectRatioAndUsesBackground()
    {
        var path = CreateSolidImage("wide.png", 48, 24, SKColors.Red);
        var palette = TestPalette.Create(
            new RgbColor(0, 0, 0),
            new RgbColor(255, 255, 255),
            new RgbColor(255, 0, 0));
        var quantizer = new SkiaImageQuantizer();

        var artwork = await quantizer.ConvertAsync(path, palette,
            new ImageConversionOptions(ImageFitMode.Contain, new RgbColor(255, 255, 255), false));

        Assert.Equal(1, artwork[12, 0]);
        Assert.Equal(2, artwork[12, 12]);
        Assert.Equal(1, artwork[12, 23]);
    }

    [Theory]
    [InlineData(ImageFitMode.Cover)]
    [InlineData(ImageFitMode.Stretch)]
    public async Task FullFrameModes_FillEveryPixel(ImageFitMode mode)
    {
        var path = CreateSolidImage($"{mode}.png", 48, 24, SKColors.Red);
        var palette = TestPalette.Create(new RgbColor(255, 255, 255), new RgbColor(255, 0, 0));
        var quantizer = new SkiaImageQuantizer();

        var artwork = await quantizer.ConvertAsync(path, palette,
            new ImageConversionOptions(mode, new RgbColor(255, 255, 255), false));

        Assert.All(artwork.PaletteIndexes.ToArray(), index => Assert.Equal(1, index));
    }

    [Theory]
    [InlineData(DitherMode.FloydSteinberg)]
    [InlineData(DitherMode.Atkinson)]
    [InlineData(DitherMode.Bayer4x4)]
    public async Task Dithering_IsDeterministicAndUsesBothPaletteColors(DitherMode mode)
    {
        var path = CreateSolidImage($"{mode}.png", 24, 24, new SKColor(128, 128, 128));
        var palette = TestPalette.Create(new RgbColor(0, 0, 0), new RgbColor(255, 255, 255));
        var quantizer = new SkiaImageQuantizer();
        var options = new ImageConversionOptions(
            ImageFitMode.Stretch,
            new RgbColor(255, 255, 255),
            PixelArtAlgorithm.BeadAverage,
            mode);

        var first = await quantizer.ConvertAsync(path, palette, options);
        var second = await quantizer.ConvertAsync(path, palette, options);

        Assert.True(first.PaletteIndexes.Span.SequenceEqual(second.PaletteIndexes.Span));
        Assert.Equal(Artwork24.PixelCount, first.PaletteIndexes.Length);
        Assert.Equal(2, first.PaletteIndexes.ToArray().Distinct().Count());
    }

    [Fact]
    public async Task BeadAverage_AreaAveragesEachOutputCell()
    {
        using var bitmap = new SKBitmap(48, 24);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, x % 2 == 0 ? SKColors.Black : SKColors.White);
            }
        }

        var path = Save("bead-average.png", bitmap);
        var palette = TestPalette.Create(
            new RgbColor(0, 0, 0),
            new RgbColor(128, 128, 128),
            new RgbColor(255, 255, 255));
        var quantizer = new SkiaImageQuantizer();
        var options = new ImageConversionOptions(
            ImageFitMode.Stretch,
            new RgbColor(255, 255, 255),
            PixelArtAlgorithm.BeadAverage,
            DitherMode.None);

        var artwork = await quantizer.ConvertAsync(path, palette, options);

        Assert.All(artwork.PaletteIndexes.ToArray(), index => Assert.Equal(1, index));
    }

    [Fact]
    public async Task BeadDominant_PicksClusterWithLargestArea()
    {
        using var bitmap = new SKBitmap(72, 24);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, x % 3 < 2 ? SKColors.Red : SKColors.Blue);
            }
        }

        var path = Save("bead-dominant.png", bitmap);
        var palette = TestPalette.Create(
            new RgbColor(255, 0, 0),
            new RgbColor(170, 0, 85),
            new RgbColor(0, 0, 255));
        var quantizer = new SkiaImageQuantizer();
        var options = new ImageConversionOptions(
            ImageFitMode.Stretch,
            new RgbColor(255, 255, 255),
            PixelArtAlgorithm.BeadDominant,
            DitherMode.None);

        var artwork = await quantizer.ConvertAsync(path, palette, options);

        Assert.All(artwork.PaletteIndexes.ToArray(), index => Assert.Equal(0, index));
    }

    [Fact]
    public async Task Crop_UsesOnlySelectedSourceRegion()
    {
        using var bitmap = new SKBitmap(48, 24);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, x < 24 ? SKColors.Red : SKColors.Blue);
            }
        }

        var path = Save("crop.png", bitmap);
        var palette = TestPalette.Create(new RgbColor(255, 0, 0), new RgbColor(0, 0, 255));
        var quantizer = new SkiaImageQuantizer();
        var options = new ImageConversionOptions(
            ImageFitMode.Stretch,
            new RgbColor(255, 255, 255),
            false,
            new ImageCropRect(0.5, 0, 0.5, 1));

        var artwork = await quantizer.ConvertAsync(path, palette, options);

        Assert.All(artwork.PaletteIndexes.ToArray(), index => Assert.Equal(1, index));
    }

    [Fact]
    public async Task Preview_HasRequestedStableDimensions()
    {
        var path = CreateSolidImage("preview.png", 24, 24, SKColors.Black);
        var palette = TestPalette.Create(new RgbColor(0, 0, 0));
        var quantizer = new SkiaImageQuantizer();
        var artwork = await quantizer.ConvertAsync(path, palette, ImageConversionOptions.Default);

        using var preview = SKBitmap.Decode(quantizer.RenderPreview(artwork, palette, 480));

        Assert.Equal(480, preview.Width);
        Assert.Equal(480, preview.Height);
    }

    [Fact]
    public async Task Svg_ViewBoxIsRasterizedAndConverted()
    {
        var path = Path.Combine(_temporaryDirectory, "solid.svg");
        File.WriteAllText(path, """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100">
              <rect width="200" height="100" fill="#ff0000" />
            </svg>
            """);
        var palette = TestPalette.Create(new RgbColor(255, 0, 0), new RgbColor(0, 0, 255));
        var quantizer = new SkiaImageQuantizer();

        using var loaded = SkiaImageLoader.LoadOriented(path);
        var artwork = await quantizer.ConvertAsync(
            path,
            palette,
            new ImageConversionOptions(ImageFitMode.Stretch, new RgbColor(255, 255, 255), false));

        Assert.Equal(2048, loaded.Width);
        Assert.Equal(1024, loaded.Height);
        Assert.Equal(SKColors.Red, loaded.GetPixel(loaded.Width / 2, loaded.Height / 2));
        Assert.All(artwork.PaletteIndexes.ToArray(), index => Assert.Equal(0, index));
    }

    [Fact]
    public void Svg_TransparencyIsPreserved()
    {
        var path = Path.Combine(_temporaryDirectory, "transparent.svg");
        File.WriteAllText(path, """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
              <circle cx="50" cy="50" r="25" fill="#0080ff" />
            </svg>
            """);

        using var loaded = SkiaImageLoader.LoadOriented(path);

        Assert.Equal(0, loaded.GetPixel(0, 0).Alpha);
        Assert.Equal(new SKColor(0, 128, 255), loaded.GetPixel(loaded.Width / 2, loaded.Height / 2));
    }

    [Fact]
    public void Svg_ExternalResourcesAreRejected()
    {
        var path = Path.Combine(_temporaryDirectory, "external.svg");
        File.WriteAllText(path, """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
              <image href="https://example.com/image.png" width="100" height="100" />
            </svg>
            """);

        var exception = Assert.Throws<InvalidDataException>(() => SkiaImageLoader.LoadOriented(path));

        Assert.Contains("外部", exception.Message);
    }

    [Fact]
    public async Task BrightnessAdjustment_ChangesPaletteMapping()
    {
        var path = CreateSolidImage("brightness.png", 24, 24, new SKColor(80, 80, 80));
        var palette = TestPalette.Create(new RgbColor(80, 80, 80), new RgbColor(208, 208, 208));
        var quantizer = new SkiaImageQuantizer();
        var options = new ImageConversionOptions(
            ImageFitMode.Stretch,
            new RgbColor(255, 255, 255),
            PixelArtAlgorithm.BeadAverage,
            DitherMode.None,
            Brightness: 50);

        var artwork = await quantizer.ConvertAsync(path, palette, options);

        Assert.All(artwork.PaletteIndexes.ToArray(), index => Assert.Equal(1, index));
    }

    [Fact]
    public async Task ContrastAdjustment_ChangesPaletteMapping()
    {
        var path = CreateSolidImage("contrast.png", 24, 24, new SKColor(160, 160, 160));
        var palette = TestPalette.Create(new RgbColor(160, 160, 160), new RgbColor(255, 255, 255));
        var quantizer = new SkiaImageQuantizer();
        var options = new ImageConversionOptions(
            ImageFitMode.Stretch,
            new RgbColor(255, 255, 255),
            PixelArtAlgorithm.BeadAverage,
            DitherMode.None,
            Contrast: 100);

        var artwork = await quantizer.ConvertAsync(path, palette, options);

        Assert.All(artwork.PaletteIndexes.ToArray(), index => Assert.Equal(1, index));
    }

    [Fact]
    public async Task SaturationMinus100_ProducesGrayscale()
    {
        var path = CreateSolidImage("saturation.png", 24, 24, SKColors.Red);
        var palette = TestPalette.Create(new RgbColor(255, 0, 0), new RgbColor(54, 54, 54));
        var quantizer = new SkiaImageQuantizer();
        var options = new ImageConversionOptions(
            ImageFitMode.Stretch,
            new RgbColor(255, 255, 255),
            PixelArtAlgorithm.BeadAverage,
            DitherMode.None,
            Saturation: -100);

        var artwork = await quantizer.ConvertAsync(path, palette, options);

        Assert.All(artwork.PaletteIndexes.ToArray(), index => Assert.Equal(1, index));
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, true);
    }

    private string CreateSolidImage(string fileName, int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        return Save(fileName, bitmap);
    }

    private string Save(string fileName, SKBitmap bitmap)
    {
        var path = Path.Combine(_temporaryDirectory, fileName);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }
}
