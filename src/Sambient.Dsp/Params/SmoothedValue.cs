using System.Runtime.CompilerServices;

namespace Sambient.Dsp.Params;

/// <summary>Linear parameter smoother: ramps to a new target over a fixed number of samples.</summary>
public struct SmoothedValue
{
    private float _current, _target, _step;
    private int _remaining, _rampSamples;

    /// <summary>Creates a smoother at <paramref name="initial"/> with the given ramp time.</summary>
    public SmoothedValue(float initial, double sampleRate, float timeMs = 20f)
    {
        _current = _target = initial;
        _step = 0;
        _remaining = 0;
        _rampSamples = 1;
        SetTime(sampleRate, timeMs);
    }

    public void SetTime(double sampleRate, float timeMs)
        => _rampSamples = Math.Max(1, (int)(sampleRate * timeMs * 0.001));

    public readonly float Current => _current;
    public readonly float Target => _target;
    public readonly bool IsSmoothing => _remaining > 0;

    /// <summary>Starts a ramp from the current value to <paramref name="target"/>.</summary>
    public void SetTarget(float target)
    {
        if (target == _target) return;     // same target: let a running ramp finish instead of restarting it every block
        _target = target;
        _remaining = _rampSamples;
        _step = (target - _current) / _rampSamples;
    }

    /// <summary>Jumps immediately to <paramref name="value"/> (no ramp).</summary>
    public void SetImmediate(float value)
    {
        _current = _target = value;
        _remaining = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Next()
    {
        if (_remaining == 0) return _current;
        if (--_remaining == 0) _current = _target;
        else _current += _step;
        return _current;
    }

    /// <summary>Advances by <paramref name="n"/> samples without producing output.</summary>
    public void Skip(int n)
    {
        if (n >= _remaining) { _current = _target; _remaining = 0; }
        else if (n > 0) { _current += _step * n; _remaining -= n; }
    }
}
