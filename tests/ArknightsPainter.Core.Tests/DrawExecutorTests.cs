using ArknightsPainter.Core;
using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Automation;
using ArknightsPainter.Core.Models;
using SkiaSharp;

namespace ArknightsPainter.Core.Tests;

public sealed class DrawExecutorTests
{
    [Fact]
    public async Task Executor_BatchesAll576CellsAndCompletes()
    {
        var black = new RgbColor(0, 0, 0);
        var palette = TestPalette.Create(black);
        var artwork = new Artwork24(Enumerable.Repeat(0, Artwork24.PixelCount));
        var bounds = new PixelRect(0, 0, 240, 240);
        var profile = new CalibrationProfile("fake", 300, 300, bounds, new PixelRect(250, 0, 40, 240), 1, DateTimeOffset.UtcNow);
        var adb = new FakeAdbClient(bounds, new RgbColor(255, 255, 255));
        var navigator = new FakeNavigator(adb);
        var progress = new InlineProgress();
        var executor = new DrawExecutor(adb, new AlwaysValidLocator(), new AlwaysValidPaletteVision(), navigator);

        await executor.ExecuteAsync(
            "fake",
            profile,
            palette,
            DrawPlan.Create(artwork, palette),
            new DrawExecutionOptions(20, TimeSpan.Zero, 1),
            new PauseController(),
            progress);

        Assert.Equal(29, adb.BatchCount);
        Assert.Equal(DrawStage.Completed, progress.Last?.Stage);
        Assert.Equal(Artwork24.PixelCount, progress.Last?.CompletedCells);
    }

    [Fact]
    public async Task PauseController_BlocksUntilResume()
    {
        var controller = new PauseController();
        controller.Pause();

        var waiting = controller.WaitIfPausedAsync();
        await Task.Delay(20);
        Assert.False(waiting.IsCompleted);

        controller.Resume();
        await waiting;
        Assert.True(waiting.IsCompletedSuccessfully);
    }

    private sealed class FakeAdbClient(PixelRect board, RgbColor initial) : IAdbClient
    {
        private readonly RgbColor[] _cells = Enumerable.Repeat(initial, Artwork24.PixelCount).ToArray();

        public string ExecutablePath => "fake-adb";

        public int BatchCount { get; private set; }

        public RgbColor SelectedColor { get; set; }

        public Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdbDevice>>([]);

        public Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<(int Width, int Height)> GetScreenSizeAsync(string serial, CancellationToken cancellationToken = default) =>
            Task.FromResult((300, 300));

        public Task TapAsync(string serial, PixelPoint point, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TapBatchAsync(string serial, IReadOnlyList<PixelPoint> points, TimeSpan delay, CancellationToken cancellationToken = default)
        {
            BatchCount++;
            foreach (var point in points)
            {
                var column = Math.Clamp((point.X - board.X) * Artwork24.Size / board.Width, 0, Artwork24.Size - 1);
                var row = Math.Clamp((point.Y - board.Y) * Artwork24.Size / board.Height, 0, Artwork24.Size - 1);
                _cells[(row * Artwork24.Size) + column] = SelectedColor;
            }

            return Task.CompletedTask;
        }

        public Task SwipeAsync(string serial, PixelPoint from, PixelPoint to, int durationMilliseconds, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<byte[]> CaptureScreenshotAsync(string serial, CancellationToken cancellationToken = default)
        {
            using var bitmap = new SKBitmap(300, 300);
            bitmap.Erase(SKColors.White);
            using var canvas = new SKCanvas(bitmap);
            using var paint = new SKPaint { Style = SKPaintStyle.Fill };
            var cellSize = board.Width / (float)Artwork24.Size;
            for (var row = 0; row < Artwork24.Size; row++)
            {
                for (var column = 0; column < Artwork24.Size; column++)
                {
                    var color = _cells[(row * Artwork24.Size) + column];
                    paint.Color = new SKColor(color.R, color.G, color.B);
                    canvas.DrawRect(board.X + (column * cellSize), board.Y + (row * cellSize), cellSize, cellSize, paint);
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult(data.ToArray());
        }
    }

    private sealed class FakeNavigator(FakeAdbClient adb) : IPaletteNavigator
    {
        public Task ResetToTopAsync(string serial, CalibrationProfile profile, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SelectColorAsync(string serial, CalibrationProfile profile, PaletteColor color, CancellationToken cancellationToken = default)
        {
            adb.SelectedColor = color.Color;
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysValidLocator : IScreenLocator
    {
        public ScreenLocationResult Locate(string deviceSerial, byte[] screenshotPng) =>
            new(true, null, 1, string.Empty);

        public double ScoreCanvas(byte[] screenshotPng, PixelRect bounds) => 1;
    }

    private sealed class AlwaysValidPaletteVision : IPaletteVision
    {
        public IReadOnlyList<VisibleSwatch> ReadVisibleSwatches(byte[] screenshotPng, PixelRect paletteViewport, int columns = 4) => [];

        public bool ValidateVisiblePalette(byte[] screenshotPng, PixelRect paletteViewport, PaletteDefinition palette, double minimumMatchRatio = 0.65) => true;

        public bool VerifySelectionGlow(byte[] screenshotPng, PixelRect paletteViewport, PixelPoint selectedCenter) => true;
    }

    private sealed class InlineProgress : IProgress<DrawProgress>
    {
        public DrawProgress? Last { get; private set; }

        public void Report(DrawProgress value) => Last = value;
    }
}
