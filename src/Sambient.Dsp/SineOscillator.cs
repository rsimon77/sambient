namespace Sambient.Dsp;

/// <summary>Phase-accumulator sine oscillator. Allocation-free; safe for the audio thread.</summary>
public struct SineOscillator
{
    private double _phase;      // radians, [0, 2*pi)
    private double _increment;  // radians per sample

    public SineOscillator(double frequencyHz, double sampleRate)
    {
        _phase = 0;
        _increment = 0;
        SetFrequency(frequencyHz, sampleRate);
    }

    public double Phase
    {
        readonly get => _phase;
        set => _phase = value;
    }

    public void SetFrequency(double frequencyHz, double sampleRate)
    {
        _increment = 2.0 * Math.PI * frequencyHz / sampleRate;
    }

    public float Next()
    {
        float s = (float)Math.Sin(_phase);
        _phase += _increment;
        if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;
        return s;
    }

    public void Process(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = Next();
    }
}
