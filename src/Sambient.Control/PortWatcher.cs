namespace Sambient.Control;

/// <summary>
/// Polls the MIDI port list (default every 2 s, on a thread-pool timer, never an audio thread) and raises
/// <see cref="LaunchkeyConnected"/> / <see cref="LaunchkeyDisconnected"/> when the detection result changes.
/// Events arrive on a thread-pool thread.
/// </summary>
public sealed class PortWatcher : IDisposable
{
    private readonly Func<IReadOnlyList<MidiPortInfo>> _listPorts;
    private readonly TimeSpan _interval;
    private Timer? _timer;
    private int _polling;
    private LaunchkeyPorts _current = LaunchkeyPorts.None;

    /// <summary>Raised when ports appear, or the set of found ports changes while present.</summary>
    public event Action<LaunchkeyPorts>? LaunchkeyConnected;
    /// <summary>Raised when all Launchkey ports vanished. Argument is the last known ports.</summary>
    public event Action<LaunchkeyPorts>? LaunchkeyDisconnected;

    public LaunchkeyPorts Current => _current;

    public PortWatcher(TimeSpan? interval = null, Func<IReadOnlyList<MidiPortInfo>>? listPorts = null)
    {
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _listPorts = listPorts ?? MidiPortCatalog.ListAll;
    }

    /// <summary>Starts polling; the first poll happens immediately.</summary>
    public void Start() => _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, _interval);

    /// <summary>Runs one poll synchronously (used by the timer; public for tests).</summary>
    public void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            LaunchkeyPorts found;
            try { found = LaunchkeyMk3Detector.Detect(_listPorts()); }
            catch { return; } // CoreMIDI hiccup: keep previous state

            var previous = _current;
            if (found == previous) return;
            _current = found;
            if (found.IsAny) LaunchkeyConnected?.Invoke(found);
            else LaunchkeyDisconnected?.Invoke(previous);
        }
        finally { Volatile.Write(ref _polling, 0); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
