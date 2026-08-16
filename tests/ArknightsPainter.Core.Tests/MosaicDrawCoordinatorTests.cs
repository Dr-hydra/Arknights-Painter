using ArknightsPainter.Core;
using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Automation;
using ArknightsPainter.Core.Models;

namespace ArknightsPainter.Core.Tests;

public sealed class MosaicDrawCoordinatorTests
{
    [Fact]
    public async Task Coordinator_DrawsAndSavesAll16Tiles()
    {
        var executor = new FakeExecutor();
        var navigator = new FakeScreenNavigator();
        var coordinator = new MosaicDrawCoordinator(executor, navigator);
        var artwork = new Artwork96(Enumerable.Range(0, Artwork96.PixelCount).Select(index => index % 2));
        var saved = new List<int>();
        var progress = new InlineProgress();

        await coordinator.ExecuteAsync(
            "fake",
            CreateProfile(),
            TestPalette.Create(new RgbColor(0, 0, 0), new RgbColor(255, 255, 255)),
            artwork,
            0,
            new DrawExecutionOptions(),
            new PauseController(),
            nextTile =>
            {
                saved.Add(nextTile);
                return Task.CompletedTask;
            },
            progress);

        Assert.Equal(Artwork96.TileCount, executor.CallCount);
        Assert.Equal(Artwork96.TileCount, navigator.EnsureEditorCount);
        Assert.Equal(Artwork96.TileCount, navigator.SaveCount);
        Assert.Equal(Enumerable.Range(1, Artwork96.TileCount), saved);
        Assert.Equal(DrawStage.Completed, progress.Last?.Stage);
        Assert.Equal(Artwork96.PixelCount, progress.Last?.CompletedCells);
    }

    [Fact]
    public async Task Coordinator_ResumesFromRequestedTile()
    {
        var executor = new FakeExecutor();
        var navigator = new FakeScreenNavigator();
        var coordinator = new MosaicDrawCoordinator(executor, navigator);

        await coordinator.ExecuteAsync(
            "fake",
            CreateProfile(),
            TestPalette.Create(new RgbColor(0, 0, 0)),
            new Artwork96(Enumerable.Repeat(0, Artwork96.PixelCount)),
            12,
            new DrawExecutionOptions(),
            new PauseController());

        Assert.Equal(4, executor.CallCount);
        Assert.Equal(4, navigator.SaveCount);
    }

    [Fact]
    public async Task Coordinator_DrawsSourceTilesFromBottomRightToTopLeft()
    {
        var indexes = Enumerable.Range(0, Artwork96.PixelCount)
            .Select(flat =>
            {
                var column = flat % Artwork96.Size;
                var row = flat / Artwork96.Size;
                return ((row / Artwork24.Size) * Artwork96.TilesPerAxis) + (column / Artwork24.Size);
            });
        var palette = TestPalette.Create(Enumerable.Range(0, Artwork96.TileCount)
            .Select(index => new RgbColor((byte)index, 0, 0))
            .ToArray());
        var executor = new FakeExecutor();
        var coordinator = new MosaicDrawCoordinator(executor, new FakeScreenNavigator());

        await coordinator.ExecuteAsync(
            "fake",
            CreateProfile(),
            palette,
            new Artwork96(indexes),
            0,
            new DrawExecutionOptions(),
            new PauseController());

        Assert.Equal(Enumerable.Range(0, Artwork96.TileCount).Reverse(), executor.SourceTileIndexes);
    }

    [Fact]
    public async Task Coordinator_RetriesTransientFailuresUpToThreeAttempts()
    {
        var executor = new FakeExecutor { FailuresRemaining = 1 };
        var navigator = new FakeScreenNavigator
        {
            EnsureFailuresRemaining = 1,
            SaveFailuresRemaining = 2
        };
        var coordinator = new MosaicDrawCoordinator(executor, navigator, maxAttempts: 3, retryDelay: TimeSpan.Zero);
        var progress = new InlineProgress();

        await coordinator.ExecuteAsync(
            "fake",
            CreateProfile(),
            TestPalette.Create(new RgbColor(0, 0, 0)),
            new Artwork96(Enumerable.Repeat(0, Artwork96.PixelCount)),
            0,
            new DrawExecutionOptions(),
            new PauseController(),
            progress: progress);

        Assert.Equal(17, executor.CallCount);
        Assert.Equal(18, navigator.EnsureEditorCount);
        Assert.Equal(18, navigator.SaveCount);
        Assert.Equal(DrawStage.Completed, progress.Last?.Stage);
    }

    private static CalibrationProfile CreateProfile() => new(
        "fake",
        1920,
        1080,
        new PixelRect(443, 180, 844, 842),
        new PixelRect(1433, 377, 420, 650),
        1,
        DateTimeOffset.UtcNow);

    private sealed class FakeExecutor : IDrawExecutor
    {
        public int CallCount { get; private set; }

        public int FailuresRemaining { get; set; }

        public List<int> SourceTileIndexes { get; } = [];

        public Task ExecuteAsync(
            string serial,
            CalibrationProfile profile,
            PaletteDefinition palette,
            DrawPlan plan,
            DrawExecutionOptions options,
            PauseController pauseController,
            IProgress<DrawProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            SourceTileIndexes.Add(plan.Artwork[0, 0]);
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("transient drawing failure");
            }

            progress?.Report(new DrawProgress(
                DrawStage.Completed,
                plan.TotalCells,
                plan.TotalCells,
                "done"));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScreenNavigator : IMosaicScreenNavigator
    {
        public int EnsureEditorCount { get; private set; }

        public int SaveCount { get; private set; }

        public int EnsureFailuresRemaining { get; set; }

        public int SaveFailuresRemaining { get; set; }

        public Task EnsureEditorAsync(
            string serial,
            CalibrationProfile profile,
            CancellationToken cancellationToken = default)
        {
            EnsureEditorCount++;
            if (EnsureFailuresRemaining > 0)
            {
                EnsureFailuresRemaining--;
                throw new InvalidOperationException("transient editor failure");
            }

            return Task.CompletedTask;
        }

        public Task SaveAsync(
            string serial,
            CalibrationProfile profile,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (SaveFailuresRemaining > 0)
            {
                SaveFailuresRemaining--;
                throw new InvalidOperationException("transient save failure");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InlineProgress : IProgress<DrawProgress>
    {
        public DrawProgress? Last { get; private set; }

        public void Report(DrawProgress value) => Last = value;
    }
}
