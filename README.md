# sambient
Ambient Synth for Mac

A standalone macOS ambient synthesizer (C# / .NET 11, Avalonia), played with a Novation Launchkey MK3 49.
Inspired by four-layer ambient generators: drone / pad / atmos wavetable layers plus a noise/sample layer,
shimmer reverb and a step sequencer. See [PLAN.md](PLAN.md) for the full plan (in Hungarian).

## Status
- **M0** – solution skeleton, PortAudio output, Avalonia window, Launchkey MK3 port detection ✅

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
| `tools/` | MIDI probe; offline Python tools (wavetable generation) |
