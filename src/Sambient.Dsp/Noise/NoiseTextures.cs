namespace Sambient.Dsp.Noise;

/// <summary>
/// Procedural, deterministic stand-ins for the 8 ambient stereo loops of the NOISE layer
/// (32 kHz, 8.0 s, seamless). Every texture is rendered with a pre-roll (filter warm-up) and an extra tail;
/// the tail is equal-power crossfaded into the start so that sample[N-1] -> sample[0] is continuous.
/// All slow modulations are exactly periodic over 8 s, so the loop is level-consistent as well.
/// </summary>
public static class NoiseTextures
{
    public const int SampleRate = 32000;
    public const double Seconds = 8.0;
    public const int Length = 256000;          // 8 s
    public const int FlutRootMidi = 60;        // FLUT is tuned to C4: play MIDI 60 for the original pitch
    public const double FlutRootHz = 261.6255653005986;

    private const int Pre = 16000;             // 0.5 s warm-up (discarded)
    private const int Tail = 32000;            // 1 s crossfade tail
    private const int Total = Pre + Length + Tail;
    private const double Sr = SampleRate;
    private const double Period = Seconds;

    private static double T(int j) => (j - Pre) / Sr;

    /// <summary>Builds the texture for slot 1..8 (RAIN, FIRE, RMBL, FRST, SEA, WIND, WODS, FLUT).</summary>
    public static NoiseSample Generate(int slot)
    {
        if (slot < 1 || slot > 8) throw new ArgumentOutOfRangeException(nameof(slot));
        var rng = new NoiseRng(0x5AB1E47UL + (ulong)slot * 7919UL);
        var l = new float[Total];
        var r = new float[Total];
        double targetRms = 0.1; // -20 dBFS
        switch (slot)
        {
            case 1: Rain(l, r, rng); break;
            case 2: Fire(l, r, rng); break;
            case 3: Rumble(l, r, rng); targetRms = 0.07; break;
            case 4: Frost(l, r, rng); targetRms = 0.085; break;
            case 5: Sea(l, r, rng); break;
            case 6: Wind(l, r, rng); break;
            case 7: Woods(l, r, rng); break;
            case 8: Flute(l, r, rng); targetRms = 0.065; break;
        }
        return Finish(NoiseBank.DefaultNames[slot - 1], l, r, targetRms);
    }

    // ---------------------------------------------------------------- finishing

    private static NoiseSample Finish(string name, float[] bl, float[] br, double targetRms)
    {
        var l = Loopify(bl);
        var r = Loopify(br);
        double meanL = Mean(l), meanR = Mean(r);
        double e = 0;
        for (int i = 0; i < Length; i++)
        {
            l[i] -= (float)meanL; r[i] -= (float)meanR;
            e += (double)l[i] * l[i] + (double)r[i] * r[i];
        }
        double rms = Math.Sqrt(e / (2.0 * Length));
        float gain = rms > 1e-12 ? (float)(targetRms / rms) : 1f;
        for (int i = 0; i < Length; i++) { l[i] = SoftClip(l[i] * gain); r[i] = SoftClip(r[i] * gain); }
        return new NoiseSample(name, SampleRate, l, r);
    }

    private static float[] Loopify(float[] b)
    {
        var o = new float[Length];
        for (int i = 0; i < Length; i++) o[i] = b[Pre + i];
        for (int i = 0; i < Tail; i++)
        {
            double w = Math.PI * 0.5 * i / Tail;
            o[i] = (float)(b[Pre + i] * Math.Sin(w) + b[Pre + Length + i] * Math.Cos(w));
        }
        return o;
    }

    private static double Mean(float[] a) { double s = 0; foreach (var v in a) s += v; return s / a.Length; }

    // Linear below 0.7, smooth saturation to a ceiling of 0.89 (-1.0 dBFS).
    private static float SoftClip(float x)
    {
        float a = Math.Abs(x);
        if (a <= 0.7f) return x;
        float y = 0.7f + 0.19f * MathF.Tanh((a - 0.7f) / 0.19f);
        return x < 0 ? -y : y;
    }

    // ---------------------------------------------------------------- event helpers

    private static void PanGains(double pan, out float gl, out float gr)
    {
        gl = (float)Math.Cos(pan * Math.PI * 0.5);
        gr = (float)Math.Sin(pan * Math.PI * 0.5);
    }

    /// <summary>Decaying sine "ping"/droplet with optional upward pitch glide (sweep, fraction of freq).</summary>
    private static void Ping(float[] l, float[] r, int start, double freq, double tau, double amp, double pan, double sweep = 0)
    {
        if (start < 0 || start >= Total) return;
        int n = Math.Min(Total - start, (int)(tau * Sr * 7));
        PanGains(pan, out var gl, out var gr);
        double ph = 0, att = 0.0004 * Sr;
        for (int k = 0; k < n; k++)
        {
            double tt = k / Sr;
            double f = freq * (1 + sweep * (1 - Math.Exp(-tt / tau)));
            ph += 2 * Math.PI * f / Sr;
            double env = Math.Exp(-tt / tau) * (1 - Math.Exp(-k / att));
            float y = (float)(Math.Sin(ph) * env * amp);
            l[start + k] += y * gl; r[start + k] += y * gr;
        }
    }

    /// <summary>Exponentially decaying band-passed noise burst (crackle / tick).</summary>
    private static void Burst(NoiseRng rng, float[] l, float[] r, int start, double tau, double fc, double q, double amp, double pan)
    {
        if (start < 0 || start >= Total) return;
        int n = Math.Min(Total - start, (int)(tau * Sr * 7));
        PanGains(pan, out var gl, out var gr);
        var f = new NoiseSvf(); f.Set(fc, q, Sr);
        double inv = 1.0 / (tau * Sr);
        for (int k = 0; k < n; k++)
        {
            f.Process(rng.White() * Math.Exp(-k * inv));
            float y = (float)(f.Bp * amp * Math.Sqrt(q));
            l[start + k] += y * gl; r[start + k] += y * gr;
        }
    }

    /// <summary>Hann-windowed band-passed noise grain.</summary>
    private static void Grain(NoiseRng rng, float[] l, float[] r, int start, double dur, double fc, double q, double amp, double pan)
    {
        int n = Math.Min(Total - start, (int)(dur * Sr));
        if (start < 0 || n < 4) return;
        PanGains(pan, out var gl, out var gr);
        var f = new NoiseSvf(); f.Set(fc, q, Sr);
        for (int k = 0; k < n; k++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (k + 0.5) / n);
            f.Process(rng.White());
            float y = (float)(f.Bp * w * amp);
            l[start + k] += y * gl; r[start + k] += y * gr;
        }
    }

    // ---------------------------------------------------------------- 1 RAIN

    private static void Rain(float[] l, float[] r, NoiseRng rng)
    {
        var hp = new NoiseSvf[2]; var lp = new NoiseSvf[2]; var body = new NoiseSvf[2];
        for (int c = 0; c < 2; c++) { hp[c].Set(650, 0.7, Sr); lp[c].Set(7500, 0.6, Sr); body[c].Set(2200, 0.5, Sr); }
        var mod = new PeriodicMod(rng, Period, 1, 2, 3, 5, 8);
        for (int j = 0; j < Total; j++)
        {
            double g = 1 + 0.12 * mod.Eval(T(j));
            for (int c = 0; c < 2; c++)
            {
                double w = rng.White();
                hp[c].Process(w); lp[c].Process(hp[c].Hp);
                body[c].Process(w);
                double y = (lp[c].Lp * 0.6 + body[c].Bp * 0.5) * g;
                if (c == 0) l[j] += (float)y; else r[j] += (float)y;
            }
        }
        // dense tiny droplets
        for (double t = rng.Exp(420); t < Total / Sr; t += rng.Exp(420))
        {
            double u = rng.U();
            Ping(l, r, (int)(t * Sr), rng.LogRange(2200, 9500), rng.Range(0.0012, 0.0045),
                0.05 + 0.9 * u * u * u * u, rng.U(), 0.3);
        }
        // medium plops
        for (double t = rng.Exp(7); t < Total / Sr; t += rng.Exp(7))
            Ping(l, r, (int)(t * Sr), rng.LogRange(1100, 3200), rng.Range(0.006, 0.016), rng.Range(0.25, 0.6), rng.U(), 0.9);
        // occasional larger drops
        for (double t = rng.Exp(1.2); t < Total / Sr; t += rng.Exp(1.2))
            Ping(l, r, (int)(t * Sr), rng.LogRange(500, 1100), rng.Range(0.014, 0.03), rng.Range(0.4, 0.8), rng.U(), 1.2);
    }

    // ---------------------------------------------------------------- 2 FIRE

    private static void Fire(float[] l, float[] r, NoiseRng rng)
    {
        var lp = new NoiseSvf[2]; var lp2 = new NoiseSvf[2]; var hs = new NoiseSvf[2];
        for (int c = 0; c < 2; c++) { lp[c].Set(420, 0.7, Sr); lp2[c].Set(1300, 0.6, Sr); hs[c].Set(5200, 0.7, Sr); }
        double[] brown = new double[3];
        var roarMod = new PeriodicMod(rng, Period, 1, 2, 3, 5);
        var flick = new PeriodicMod(rng, Period, 3, 5, 7, 11, 13);
        var hissMod = new PeriodicMod(rng, Period, 2, 4, 9);
        for (int j = 0; j < Total; j++)
        {
            double t = T(j);
            double am = (1 + 0.3 * roarMod.Eval(t)) * (1 + 0.25 * flick.Eval(t));
            double hiss = 0.035 * (1 + 0.6 * hissMod.Eval(t));
            double wShared = rng.White();
            for (int c = 0; c < 2; c++)
            {
                double w = 0.5 * wShared + 0.5 * rng.White();
                brown[c] = 0.996 * brown[c] + 0.09 * w;
                lp[c].Process(brown[c]);
                lp2[c].Process(w);
                hs[c].Process(rng.White());
                double y = (lp[c].Lp * 1.0 + lp2[c].Lp * 0.10) * am + hs[c].Hp * hiss;
                if (c == 0) l[j] += (float)y; else r[j] += (float)y;
            }
        }
        // clustered crackles
        for (double t = rng.Exp(3.2); t < Total / Sr; t += rng.Exp(3.2))
        {
            int count = 1 + (int)(rng.U() * rng.U() * 9);
            double pan = rng.U(), x = t;
            for (int k = 0; k < count; k++)
            {
                x += rng.Range(0.004, 0.05);
                int s = (int)(x * Sr);
                double amp = rng.Range(0.15, 0.7);
                Burst(rng, l, r, s, rng.Range(0.0004, 0.0025), rng.LogRange(900, 6500), rng.Range(1.0, 3.0), amp,
                    Math.Clamp(pan + rng.Range(-0.1, 0.1), 0, 1));
                if (rng.U() < 0.25)
                    Ping(l, r, s, rng.LogRange(110, 380), rng.Range(0.006, 0.016), amp * 0.9, pan, -0.0);
            }
        }
        // sharper isolated pops
        for (double t = rng.Exp(1.1); t < Total / Sr; t += rng.Exp(1.1))
        {
            int s = (int)(t * Sr); double pan = rng.U();
            Burst(rng, l, r, s, rng.Range(0.0008, 0.003), rng.LogRange(1500, 4500), 1.5, rng.Range(0.6, 1.4), pan);
            Ping(l, r, s, rng.LogRange(140, 320), rng.Range(0.01, 0.025), rng.Range(0.4, 0.9), pan);
        }
    }

    // ---------------------------------------------------------------- 3 RMBL

    private static void Rumble(float[] l, float[] r, NoiseRng rng)
    {
        var a = new NoiseSvf[2]; var b = new NoiseSvf[2]; var hp = new NoiseSvf[2];
        double[] brown = new double[2];
        for (int c = 0; c < 2; c++) { a[c].Set(105, 0.7, Sr); b[c].Set(140, 0.7, Sr); hp[c].Set(22, 0.7, Sr); }
        var swell = new PeriodicMod(rng, Period, 1, 2, 3);
        var wob = new PeriodicMod(rng, Period, 5, 7, 11);
        var raw = new float[2][] { new float[Total], new float[Total] };
        for (int j = 0; j < Total; j++)
        {
            double t = T(j);
            double s = 0.55 + 0.45 * swell.Eval(t);
            double am = s * (1 + 0.12 * wob.Eval(t));
            for (int c = 0; c < 2; c++)
            {
                brown[c] = 0.997 * brown[c] + 0.08 * rng.White();
                a[c].Process(brown[c]); b[c].Process(a[c].Lp); hp[c].Process(b[c].Lp);
                raw[c][j] = (float)(hp[c].Hp * am);
            }
        }
        // distant thunder-like growls: noise through a 70 Hz lowpass under a slow skewed envelope
        for (double t0 = rng.Exp(0.4); t0 < Total / Sr; t0 += rng.Exp(0.4))
        {
            int s = (int)(t0 * Sr);
            double dur = rng.Range(2.0, 4.5), amp = rng.Range(0.5, 1.1);
            double wf = rng.Range(1.5, 3.5), wp = rng.U() * 6.28;
            int n = Math.Min(Total - s, (int)(dur * Sr));
            for (int c = 0; c < 2; c++)
            {
                var f1 = new NoiseSvf(); var f2 = new NoiseSvf();
                f1.Set(rng.Range(55, 85), 0.8, Sr); f2.Set(90, 0.7, Sr);
                double br = 0;
                for (int k = 0; k < n; k++)
                {
                    double x = (double)k / n;
                    double e = Math.Pow(Math.Sin(Math.PI * Math.Pow(x, 0.55)), 2);
                    br = 0.996 * br + 0.09 * rng.White();
                    f1.Process(br); f2.Process(f1.Lp);
                    raw[c][s + k] += (float)(f2.Lp * e * amp * (1 + 0.3 * Math.Sin(2 * Math.PI * wf * k / Sr + wp)) * 1.6);
                }
            }
        }
        for (int j = 0; j < Total; j++)
        {
            l[j] = 0.8f * raw[0][j] + 0.2f * raw[1][j];
            r[j] = 0.8f * raw[1][j] + 0.2f * raw[0][j];
        }
    }

    // ---------------------------------------------------------------- 4 FRST

    private static void Frost(float[] l, float[] r, NoiseRng rng)
    {
        var hp = new NoiseSvf[2]; var lp = new NoiseSvf[2]; var air = new NoiseSvf[2];
        double[] brown = new double[2];
        var lo = new NoiseSvf[2];
        for (int c = 0; c < 2; c++) { hp[c].Set(5800, 0.7, Sr); lp[c].Set(13500, 0.6, Sr); air[c].Set(9000, 0.9, Sr); lo[c].Set(160, 0.7, Sr); }
        var mod = new PeriodicMod(rng, Period, 1, 2, 3, 4);
        var mod2 = new PeriodicMod(rng, Period, 2, 3, 5);
        for (int j = 0; j < Total; j++)
        {
            double t = T(j);
            double g = 1 + 0.25 * mod.Eval(t);
            double g2 = 1 + 0.4 * mod2.Eval(t);
            for (int c = 0; c < 2; c++)
            {
                double w = rng.White();
                hp[c].Process(w); lp[c].Process(hp[c].Hp);
                air[c].Process(rng.White());
                brown[c] = 0.995 * brown[c] + 0.08 * rng.White(); lo[c].Process(brown[c]);
                double y = lp[c].Lp * 0.5 * g + air[c].Bp * 0.18 * g2 + lo[c].Lp * 0.05;
                if (c == 0) l[j] += (float)y; else r[j] += (float)y;
            }
        }
        // glassy tinkles: inharmonic resonant pings, sometimes in short rising runs
        for (double t = rng.Exp(2.6); t < Total / Sr; t += rng.Exp(2.6))
        {
            int notes = rng.U() < 0.4 ? 2 + (int)(rng.U() * 3) : 1;
            double f = rng.LogRange(3200, 9000), pan = rng.U(), x = t;
            double tau = rng.Range(0.04, 0.16);
            for (int k = 0; k < notes; k++)
            {
                double amp = rng.Range(0.12, 0.4);
                int s = (int)(x * Sr);
                Ping(l, r, s, f, tau, amp, pan);
                Ping(l, r, s, f * 2.76, tau * 0.6, amp * 0.45, pan);
                Ping(l, r, s, f * 5.4, tau * 0.3, amp * 0.15, pan);
                x += rng.Range(0.05, 0.11);
                f *= rng.Range(1.05, 1.3);
                if (f > 13000) f = rng.LogRange(3200, 6000);
            }
        }
        // ice crackles
        for (double t = rng.Exp(5); t < Total / Sr; t += rng.Exp(5))
        {
            int count = 1 + (int)(rng.U() * 4); double x = t, pan = rng.U();
            for (int k = 0; k < count; k++)
            {
                x += rng.Range(0.003, 0.03);
                Burst(rng, l, r, (int)(x * Sr), rng.Range(0.0003, 0.0012), rng.LogRange(5500, 12500), rng.Range(2, 5), rng.Range(0.1, 0.4), pan);
            }
        }
    }

    // ---------------------------------------------------------------- 5 SEA

    private static void Sea(float[] l, float[] r, NoiseRng rng)
    {
        const double WavePeriod = 4.0; // two waves per 8 s
        var body = new NoiseSvf[2]; var foam = new NoiseSvf[2]; var lo = new NoiseSvf[2];
        double[] brown = new double[2];
        for (int c = 0; c < 2; c++) { body[c].Set(300, 0.8, Sr); foam[c].Set(2400, 0.7, Sr); lo[c].Set(130, 0.7, Sr); }
        var wash = new PeriodicMod(rng, Period, 6, 9, 13);
        for (int j = 0; j < Total; j++)
        {
            double t = T(j);
            for (int c = 0; c < 2; c++)
            {
                double tt = t - (c == 0 ? 0.0 : 0.14);          // right channel lags: the wave "travels"
                tt = ((tt % Period) + Period) % Period;
                int idx = tt < WavePeriod ? 0 : 1;
                double u = (tt - idx * WavePeriod) / WavePeriod;
                double amp = idx == 0 ? 1.0 : 0.8;
                double crest = idx == 0 ? 0.40 : 0.46;
                double swell, bodyEnv, foamEnv;
                if (u < crest)
                {
                    double x = u / crest;
                    swell = Math.Pow(Math.Sin(Math.PI * 0.5 * x), 2.4);
                    bodyEnv = swell; foamEnv = 0.45 * x * x * x;
                }
                else
                {
                    double d = u - crest;
                    swell = Math.Exp(-d * 6.5);
                    bodyEnv = swell;
                    foamEnv = Math.Exp(-d * 4.2) * (0.45 + 0.55 * Math.Exp(-d * 30));
                }
                double bright = 180 + 2600 * Math.Pow(swell, 1.2);
                double w = rng.White();
                body[c].Set(bright, 0.8, Sr); body[c].Process(w);
                foam[c].Process(rng.White());
                brown[c] = 0.996 * brown[c] + 0.08 * rng.White(); lo[c].Process(brown[c]);
                double fm = foamEnv * (1 + 0.35 * wash.Eval(t + c * 0.37));
                double y = amp * (body[c].Lp * bodyEnv * 1.6 + foam[c].Hp * fm * 0.55 + lo[c].Lp * (0.25 + 0.5 * bodyEnv));
                if (c == 0) l[j] += (float)y; else r[j] += (float)y;
            }
        }
    }

    // ---------------------------------------------------------------- 6 WIND

    private static void Wind(float[] l, float[] r, NoiseRng rng)
    {
        var main = new NoiseSvf[2]; var whistle = new NoiseSvf[2]; var lo = new NoiseSvf[2]; var pre = new NoiseSvf[2];
        for (int c = 0; c < 2; c++) { lo[c].Set(220, 0.7, Sr); pre[c].Set(2500, 0.6, Sr); }
        var wander = new PeriodicMod[2]; var gust = new PeriodicMod[2]; var wf = new PeriodicMod[2];
        for (int c = 0; c < 2; c++)
        {
            wander[c] = new PeriodicMod(rng, Period, 1, 2, 3, 5);
            gust[c] = new PeriodicMod(rng, Period, 1, 2, 3, 4, 6);
            wf[c] = new PeriodicMod(rng, Period, 1, 3, 4);
        }
        for (int j = 0; j < Total; j++)
        {
            double t = T(j);
            for (int c = 0; c < 2; c++)
            {
                double gm = 0.5 + 0.5 * gust[c].Eval(t);
                double gu = 0.1 + 0.9 * Math.Pow(Math.Clamp(gm * 1.3 - 0.1, 0, 1), 1.5);
                double fc = 420 * Math.Pow(2, 1.3 * wander[c].Eval(t) + 0.4 * gu);
                double w = rng.White();
                pre[c].Process(w);
                main[c].Set(fc, 1.3, Sr); main[c].Process(pre[c].Lp);
                lo[c].Process(w);
                double wg = Math.Max(0, gu - 0.5) * 2.0;
                double wfc = 1500 * Math.Pow(2, 0.6 * wf[c].Eval(t));
                whistle[c].Set(wfc, 35, Sr); whistle[c].Process(w);
                double y = main[c].Bp * gu * 1.6 + lo[c].Lp * gu * 0.25 + whistle[c].Bp * wg * wg * 3.0;
                if (c == 0) l[j] += (float)y; else r[j] += (float)y;
            }
        }
    }

    // ---------------------------------------------------------------- 7 WODS

    private static void Woods(float[] l, float[] r, NoiseRng rng)
    {
        var dens = new PeriodicMod(rng, Period, 1, 2, 3, 5);
        var bedMod = new PeriodicMod(rng, Period, 1, 2, 4);
        var bed = new NoiseSvf[2];
        for (int c = 0; c < 2; c++) bed[c].Set(1400, 0.6, Sr);
        for (int j = 0; j < Total; j++)
        {
            double g = 0.6 + 0.4 * bedMod.Eval(T(j));
            for (int c = 0; c < 2; c++)
            {
                bed[c].Process(rng.White());
                double y = bed[c].Bp * 0.12 * g;
                if (c == 0) l[j] += (float)y; else r[j] += (float)y;
            }
        }
        // rustling leaves: thinned Poisson process of short noise grains
        const double MaxRate = 110;
        for (double t = rng.Exp(MaxRate); t < Total / Sr; t += rng.Exp(MaxRate))
        {
            double d = 0.5 + 0.5 * dens.Eval(t - Pre / Sr);
            if (rng.U() > 0.15 + 0.85 * d * d) continue;
            Grain(rng, l, r, (int)(t * Sr), rng.Range(0.004, 0.025), rng.LogRange(1500, 7000), rng.Range(1.2, 3.0),
                rng.Range(0.12, 0.5), rng.U());
        }
        // distant bird chirps
        for (double t = rng.Exp(0.55); t < Total / Sr; t += rng.Exp(0.55))
        {
            int notes = 1 + (int)(rng.U() * 4);
            double f0 = rng.LogRange(2200, 4300), pan = rng.U(), x = t, amp = rng.Range(0.12, 0.26);
            double sweep = rng.Range(-0.5, 0.6), vib = rng.Range(25, 55), depth = rng.Range(0.0, 0.05);
            double dur = rng.Range(0.05, 0.13), gap = rng.Range(0.08, 0.16);
            for (int k = 0; k < notes; k++)
            {
                Chirp(l, r, (int)(x * Sr), dur, f0 * (1 + 0.08 * k * Math.Sign(sweep)), sweep, vib, depth, amp * (1 - 0.1 * k), pan);
                x += gap;
            }
        }
    }

    private static void Chirp(float[] l, float[] r, int start, double dur, double f0, double sweep, double vibHz, double vibDepth, double amp, double pan)
    {
        int n = Math.Min(Total - start, (int)(dur * Sr));
        if (start < 0 || n < 4) return;
        PanGains(pan, out var gl, out var gr);
        double ph = 0;
        for (int k = 0; k < n; k++)
        {
            double x = (double)k / n;
            double f = f0 * (1 + sweep * (x - 0.5)) * (1 + vibDepth * Math.Sin(2 * Math.PI * vibHz * k / Sr));
            ph += 2 * Math.PI * f / Sr;
            double env = Math.Pow(Math.Sin(Math.PI * x), 1.5);
            float y = (float)(Math.Sin(ph) * env * amp);
            l[start + k] += y * gl; r[start + k] += y * gr;
        }
    }

    // ---------------------------------------------------------------- 8 FLUT

    private static void Flute(float[] l, float[] r, NoiseRng rng)
    {
        const int Partials = 5;
        double[] pa = { 1.0, 0.5, 0.34, 0.14, 0.07 };
        const double Q = 45;
        var res = new NoiseSvf[2][];
        var breath = new NoiseSvf[2];
        for (int c = 0; c < 2; c++)
        {
            res[c] = new NoiseSvf[Partials];
            for (int p = 0; p < Partials; p++) res[c][p].Set(FlutRootHz * (p + 1), Q, Sr);
            breath[c].Set(3200, 0.7, Sr);
        }
        var br = new PeriodicMod(rng, Period, 1, 2);
        var shimmer = new PeriodicMod(rng, Period, 3, 5);
        for (int j = 0; j < Total; j++)
        {
            double t = T(j);
            double amp = 0.5 + 0.5 * (0.5 + 0.5 * br.Eval(t));       // slow breathing
            double bright = 1 + 0.2 * shimmer.Eval(t);
            for (int c = 0; c < 2; c++)
            {
                double w = rng.White();
                double y = 0;
                for (int p = 0; p < Partials; p++)
                {
                    res[c][p].Process(w);
                    // constant noise bandwidth compensation (BW ~ f/Q) -> relative amplitude pa[p]
                    y += res[c][p].Bp * pa[p] / Math.Sqrt(p + 1) * Math.Pow(amp, 0.5 * p);
                }
                breath[c].Process(rng.White());
                y = y * amp * bright * 5.0 + breath[c].Bp * 0.05 * amp;
                if (c == 0) l[j] += (float)y; else r[j] += (float)y;
            }
        }
    }
}
