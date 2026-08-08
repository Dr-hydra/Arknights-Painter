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
