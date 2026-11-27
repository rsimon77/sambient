using System.Collections.Concurrent;

namespace Sambient.Dsp;

/// <summary>
/// One shared, below-normal-priority worker thread for heavy non-real-time builds (wavetables, noise textures).
/// Running builds one at a time on a low-priority thread keeps them from competing with the audio callback and the UI
/// for CPU (parallel thread-pool builds caused audible dropouts when switching WAVEs or palettes).
/// </summary>
public static class BackgroundBuilder
{
    private static readonly BlockingCollection<(Action Work, TaskCompletionSource Done)> Queue = new();

    static BackgroundBuilder()
    {
        var t = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Sambient builder",
            Priority = ThreadPriority.BelowNormal,
        };
        t.Start();
    }

    /// <summary>Queues <paramref name="work"/>; the task completes (or faults) when it has run.</summary>
    public static Task Run(Action work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add((work, tcs));
        return tcs.Task;
    }

    private static void Loop()
    {
        foreach (var (work, done) in Queue.GetConsumingEnumerable())
        {
            try { work(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }
    }
}
