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

        // Check before Avalonia starts: once Avalonia.Native has half-initialized, exiting can trip a runtime assert.
        if (OperatingSystem.IsMacOS() && IsMainDisplayAsleep())
        {
            Console.Error.WriteLine(DisplayAsleepMessage);
            return 2;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (InvalidOperationException ex) when (IsRenderTimerFailure(ex))
        {
            // Avalonia.Native cannot start its render timer while the display is asleep (native error -6661).
            Console.Error.WriteLine(DisplayAsleepMessage);
            return 2;
        }
    }

    private const string DisplayAsleepMessage = "Display is asleep or unavailable – wake the screen and start Sambient again";

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern uint CGMainDisplayID();

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGDisplayIsAsleep(uint display);

    private static bool IsMainDisplayAsleep()
    {
        try { return CGDisplayIsAsleep(CGMainDisplayID()) != 0; }
        catch { return false; } // never block startup because of the check itself
    }

    private static bool IsRenderTimerFailure(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
            if (ex.Message.Contains("RenderTimer", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("-6661", StringComparison.Ordinal))
                return true;
        return false;
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
