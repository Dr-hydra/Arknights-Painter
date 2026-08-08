namespace ArknightsPainter.Core;

public sealed class PauseController
{
    private volatile TaskCompletionSource _resumeSource = CompletedSource();

    public bool IsPaused { get; private set; }

    public void Pause()
    {
        if (IsPaused)
        {
            return;
        }

        IsPaused = true;
        _resumeSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        if (!IsPaused)
        {
            return;
        }

        IsPaused = false;
        _resumeSource.TrySetResult();
    }

    public Task WaitIfPausedAsync(CancellationToken cancellationToken = default) =>
        IsPaused ? _resumeSource.Task.WaitAsync(cancellationToken) : Task.CompletedTask;

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
