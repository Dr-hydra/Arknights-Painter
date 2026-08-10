using ArknightsPainter.Core.Models;

namespace ArknightsPainter.Core.Abstractions;

public interface IImageQuantizer
{
    Task<Artwork24> ConvertAsync(
        string imagePath,
        PaletteDefinition palette,
        ImageConversionOptions options,
        CancellationToken cancellationToken = default);

    byte[] RenderPreview(Artwork24 artwork, PaletteDefinition palette, int outputSize = 576, bool showGrid = true);
}

public interface IAdbClient
{
    string ExecutablePath { get; }

    Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default);

    Task<byte[]> CaptureScreenshotAsync(string serial, CancellationToken cancellationToken = default);

    Task<(int Width, int Height)> GetScreenSizeAsync(string serial, CancellationToken cancellationToken = default);

    Task TapAsync(string serial, PixelPoint point, CancellationToken cancellationToken = default);

    Task TapBatchAsync(
        string serial,
        IReadOnlyList<PixelPoint> points,
        TimeSpan delay,
        CancellationToken cancellationToken = default);

    Task SwipeAsync(
        string serial,
        PixelPoint from,
        PixelPoint to,
        int durationMilliseconds,
        CancellationToken cancellationToken = default);
}

public interface IScreenLocator
{
    ScreenLocationResult Locate(string deviceSerial, byte[] screenshotPng);

    double ScoreCanvas(byte[] screenshotPng, PixelRect bounds);
}

public interface IPaletteVision
{
    IReadOnlyList<VisibleSwatch> ReadVisibleSwatches(byte[] screenshotPng, PixelRect paletteViewport, int columns = 4);

    bool ValidateVisiblePalette(
        byte[] screenshotPng,
        PixelRect paletteViewport,
        PaletteDefinition palette,
        double minimumMatchRatio = 0.65);

    bool VerifySelectionGlow(byte[] screenshotPng, PixelRect paletteViewport, PixelPoint selectedCenter);

    bool VerifySelectionGlow(
        byte[] beforeScreenshotPng,
        byte[] afterScreenshotPng,
        PixelRect paletteViewport,
        PixelPoint selectedCenter) =>
        VerifySelectionGlow(afterScreenshotPng, paletteViewport, selectedCenter);
}

public interface IPaletteNavigator
{
    Task ResetToTopAsync(string serial, CalibrationProfile profile, CancellationToken cancellationToken = default);

    Task SelectColorAsync(
        string serial,
        CalibrationProfile profile,
        PaletteColor color,
        CancellationToken cancellationToken = default);
}

public interface IDrawExecutor
{
    Task ExecuteAsync(
        string serial,
        CalibrationProfile profile,
        PaletteDefinition palette,
        DrawPlan plan,
        DrawExecutionOptions options,
        PauseController pauseController,
        IProgress<DrawProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
