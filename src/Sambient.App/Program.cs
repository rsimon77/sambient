using System.Runtime;
using Avalonia;
using Sambient.Audio;

namespace Sambient.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        if (args.Contains("--list-audio-devices"))
            return ListAudioDevices();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int ListAudioDevices()
    {
        using var audio = new PortAudioOutput();
        foreach (var d in audio.GetOutputDevices())
        {
            Console.WriteLine(
                $"[{d.Id}] {d.Name} | default {d.DefaultSampleRate:0} Hz | max out ch {d.MaxOutputChannels}{(d.IsDefault ? " | DEFAULT" : "")}");
        }
        return 0;
    }
}
