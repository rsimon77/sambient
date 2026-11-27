# sambient
Ambient Synth for Mac

A standalone macOS ambient synthesizer (C# / .NET 11, Avalonia), played with a Novation Launchkey MK3 49.
Inspired by four-layer ambient generators: drone / pad / atmos wavetable layers plus a noise/sample layer,
shimmer reverb and a step sequencer. See [PLAN.md](PLAN.md) for the full plan (in Hungarian).

## Status
- **M0** – solution skeleton, PortAudio output, Avalonia window, Launchkey MK3 port detection ✅
- **M1** – BlendWave wavetable engine (FFT band-limited, 128-frame morph), first synth layer, on-screen keyboard ✅
- **M2** – 4 layers (3 synth + noise), shimmer reverb (9 types), 6 master FX, all 32 WAVEs, 8 procedural noise textures ✅
- **M3** – 6 structures (Yin/Yang, ring mod, FM), MOD + 2 LFOs (24 shapes, 17 destinations), POLY/MONO/LEGATO/UNISON/ARP, HOLD ✅
- **M4** – step sequencer (64 steps, P-locks, ties, probability, recording), pattern palettes (8×16, chain, relaxation), tuning systems ✅
- next: Launchkey MK3 DAW mode (pads = steps with LEDs) – protocol notes in [docs/launchkey-mk3-daw-protocol.md](docs/launchkey-mk3-daw-protocol.md); sampling / line in

## Build & run (Apple Silicon, .NET 11 SDK)
```bash
dotnet build Sambient.slnx
dotnet test Sambient.slnx
dotnet run --project src/Sambient.App
```

Tools:
```bash
dotnet run --project src/Sambient.App -- --list-audio-devices
dotnet run --project tools/Sambient.MidiProbe -- --listen 10
```

## Layout
| Project | Purpose |
|---|---|
| `src/Sambient.Dsp` | oscillators, filters, envelopes, effects |
| `src/Sambient.Engine` | layers, voices, audio engine |
| `src/Sambient.Seq` | step sequencer, pattern palettes |
| `src/Sambient.Audio` | PortAudio (CoreAudio) output |
| `src/Sambient.Control` | MIDI, Launchkey MK3 surface |
| `src/Sambient.App` | Avalonia UI |
| `tools/` | MIDI probe, WaveDump (wave/engine/fx/seq demos, preset loudness report), NoiseDump |
