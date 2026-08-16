using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Models;

namespace ArknightsPainter.Core.Automation;

public sealed class MosaicDrawCoordinator
{
    private readonly IDrawExecutor _executor;
    private readonly IMosaicScreenNavigator _screenNavigator;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;

    public MosaicDrawCoordinator(
        IDrawExecutor executor,
        IMosaicScreenNavigator screenNavigator,
        int maxAttempts = 3,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(screenNavigator);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        _executor = executor;
        _screenNavigator = screenNavigator;
        _maxAttempts = maxAttempts;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
    }

    public async Task ExecuteAsync(
        string serial,
        CalibrationProfile profile,
        PaletteDefinition palette,
        Artwork96 artwork,
        int startTileIndex,
        DrawExecutionOptions options,
        PauseController pauseController,
        Func<int, Task>? tileSaved = null,
        IProgress<DrawProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (startTileIndex is < 0 or >= Artwork96.TileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(startTileIndex));
        }

        var tiles = artwork.SplitIntoTiles();
        var completedCells = startTileIndex * Artwork24.PixelCount;
        try
        {
            for (var tileIndex = startTileIndex; tileIndex < tiles.Count; tileIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await pauseController.WaitIfPausedAsync(cancellationToken);
                var currentTile = tileIndex;
                var sourceTileIndex = (Artwork96.TileCount - 1) - currentTile;
                progress?.Report(new DrawProgress(
                    DrawStage.Validating,
                    currentTile * Artwork24.PixelCount,
                    Artwork96.PixelCount,
                    $"正在准备分片 {currentTile + 1}/{Artwork96.TileCount}（画面位置 {sourceTileIndex + 1}）。"));

                var mappedProgress = new DelegatingProgress<DrawProgress>(inner =>
                {
                    var tileCells = (int)Math.Round(inner.Fraction * Artwork24.PixelCount);
                    completedCells = (currentTile * Artwork24.PixelCount) + tileCells;
                    var stage = inner.Stage == DrawStage.Completed ? DrawStage.Painting : inner.Stage;
                    progress?.Report(new DrawProgress(
                        stage,
                        completedCells,
                        Artwork96.PixelCount,
                        $"分片 {currentTile + 1}/{Artwork96.TileCount}：{inner.Message}",
                        inner.CurrentPaletteIndex));
                });

                await ExecuteWithRetryAsync(
                    async () =>
                    {
                        await _screenNavigator.EnsureEditorAsync(serial, profile, cancellationToken);
                        await _executor.ExecuteAsync(
                            serial,
                            profile,
                            palette,
                            DrawPlan.Create(tiles[sourceTileIndex], palette),
                            options,
                            pauseController,
                            mappedProgress,
                            cancellationToken);
                    },
                    $"分片 {currentTile + 1}/{Artwork96.TileCount} 绘制");

                await pauseController.WaitIfPausedAsync(cancellationToken);
                completedCells = (currentTile + 1) * Artwork24.PixelCount;
                progress?.Report(new DrawProgress(
                    DrawStage.Verifying,
                    completedCells,
                    Artwork96.PixelCount,
                    $"分片 {currentTile + 1}/{Artwork96.TileCount} 绘制完成，正在保存。"));
                await ExecuteWithRetryAsync(
                    () => _screenNavigator.SaveAsync(serial, profile, cancellationToken),
                    $"分片 {currentTile + 1}/{Artwork96.TileCount} 保存");

                if (tileSaved is not null)
                {
                    await tileSaved(currentTile + 1);
                }
            }

            progress?.Report(new DrawProgress(
                DrawStage.Completed,
                Artwork96.PixelCount,
                Artwork96.PixelCount,
                "96×96 的 16 个分片已全部绘制并保存。"));
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new DrawProgress(
                DrawStage.Cancelled,
                completedCells,
                Artwork96.PixelCount,
                "96×96 分片任务已取消。"));
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report(new DrawProgress(
                DrawStage.Failed,
                completedCells,
                Artwork96.PixelCount,
                ex.Message));
            throw;
        }

        async Task ExecuteWithRetryAsync(Func<Task> operation, string operationName)
        {
            for (var attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                try
                {
                    await operation();
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < _maxAttempts)
                {
                    progress?.Report(new DrawProgress(
                        DrawStage.Validating,
                        completedCells,
                        Artwork96.PixelCount,
                        $"{operationName}失败：{ex.Message} 将进行第 {attempt + 1}/{_maxAttempts} 次尝试。"));
                    await Task.Delay(_retryDelay, cancellationToken);
                }
            }
        }
    }

    private sealed class DelegatingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
