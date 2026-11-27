using System.Runtime.CompilerServices;

namespace Sambient.Dsp.Envelopes;

/// <summary>
/// Attack/Release envelope with analog-style exponential segments. While the gate is on it holds at 1.0.
/// Attack: one-pole toward an overshoot target (1.3) so it reaches exactly 1.0 after ~attackMs from zero.
/// Release: exponential decay; releaseMs is the time to -60 dB; goes idle below -100 dB.
/// </summary>
public struct ArEnvelope
{
    private enum Stage : byte { Idle, Attack, Hold, Release, Kill }

    private const float Overshoot = 1.3f;
    private const float IdleLevel = 1e-5f;       // -100 dB
    private const float MinMs = 1f;
    private const float KillMs = 5f;

    private readonly float _sampleRate;
    private float _level;
    private float _attackCoef, _releaseCoef, _killCoef;
    private Stage _stage;

    public ArEnvelope(float sampleRate, float attackMs = 10f, float releaseMs = 500f)
    {
        _sampleRate = sampleRate;
        _level = 0;
        _stage = Stage.Idle;
        _attackCoef = _releaseCoef = 0;
        _killCoef = DecayCoef(KillMs, IdleLevel);
        SetTimes(attackMs, releaseMs);
    }

    public readonly bool IsActive => _stage != Stage.Idle;
    public readonly float Level => _level;

    private readonly float DecayCoef(float ms, float ratio)
        => MathF.Exp(MathF.Log(ratio) / (ms * 0.001f * _sampleRate));

    /// <summary>Sets attack and release times in ms (min 1 ms); may be called mid-segment.</summary>
    public void SetTimes(float attackMs, float releaseMs)
    {
        attackMs = Math.Max(attackMs, MinMs);
        releaseMs = Math.Max(releaseMs, MinMs);
        // 1 = Overshoot * (1 - exp(-T/tau))  =>  tau = T / ln(Overshoot / (Overshoot - 1))
        float tauSamples = attackMs * 0.001f * _sampleRate / MathF.Log(Overshoot / (Overshoot - 1f));
        _attackCoef = 1f - MathF.Exp(-1f / tauSamples);
        _releaseCoef = DecayCoef(releaseMs, 1e-3f);   // -60 dB in releaseMs
    }

    /// <summary>Starts (or retriggers from the current level) the attack.</summary>
    public void NoteOn() => _stage = Stage.Attack;

    public void NoteOff()
    {
        if (_stage == Stage.Attack || _stage == Stage.Hold) _stage = Stage.Release;
    }

    /// <summary>Fast ~5 ms fade to silence (voice stealing).</summary>
    public void Kill()
    {
        if (_stage != Stage.Idle) _stage = Stage.Kill;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Next()
    {
        switch (_stage)
        {
            case Stage.Attack:
                _level += (Overshoot - _level) * _attackCoef;
                if (_level >= 1f) { _level = 1f; _stage = Stage.Hold; }
                break;
            case Stage.Release:
                _level *= _releaseCoef;
                if (_level < IdleLevel) { _level = 0f; _stage = Stage.Idle; }
                break;
            case Stage.Kill:
                _level *= _killCoef;
                if (_level < IdleLevel) { _level = 0f; _stage = Stage.Idle; }
                break;
        }
        return _level;
    }

    public void Process(Span<float> gainOut)
    {
        for (int i = 0; i < gainOut.Length; i++) gainOut[i] = Next();
    }
}
