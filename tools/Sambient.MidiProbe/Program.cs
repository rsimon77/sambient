using Sambient.Control;

int listenSeconds = 0;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--listen")
    {
        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out listenSeconds) || listenSeconds <= 0)
        {
            Console.Error.WriteLine("usage: MidiProbe [--listen <seconds>]");
            return 2;
        }
    }
}

var ins = MidiPortCatalog.ListInputs();
var outs = MidiPortCatalog.ListOutputs();

Console.WriteLine($"MIDI inputs ({ins.Count}):");
foreach (var p in ins) Console.WriteLine($"  [{p.Kind}] {p.Name}");
Console.WriteLine($"MIDI outputs ({outs.Count}):");
foreach (var p in outs) Console.WriteLine($"  [{p.Kind}] {p.Name}");

var found = LaunchkeyMk3Detector.Detect(ins.Select(p => p.Name), outs.Select(p => p.Name));
Console.WriteLine("Launchkey MK3 detection:");
Console.WriteLine($"  Model      : {(found.Model?.ToString() ?? "(unknown)")}");
Console.WriteLine($"  MIDI In    : {found.MidiIn ?? "(none)"}");
Console.WriteLine($"  MIDI Out   : {found.MidiOut ?? "(none)"}");
Console.WriteLine($"  DAW In     : {found.DawIn ?? "(none)"}");
Console.WriteLine($"  DAW Out    : {found.DawOut ?? "(none)"}");
Console.WriteLine($"  IsComplete : {found.IsComplete}");

if (listenSeconds > 0)
{
    if (found.MidiIn == null && found.DawIn == null)
    {
        Console.WriteLine("No Launchkey input ports detected; nothing to listen to.");
        return 0;
    }
    var t0 = DateTime.Now;
    string Ts() => (DateTime.Now - t0).TotalSeconds.ToString("0.000");
    using var input = new ControllerInput(found);
    input.NoteOn += e => Console.WriteLine($"{Ts()} NoteOn  {e.Source} ch{e.Channel} note={e.Note} vel={e.Velocity}");
    input.NoteOff += e => Console.WriteLine($"{Ts()} NoteOff {e.Source} ch{e.Channel} note={e.Note} vel={e.Velocity}");
    input.ControlChange += e => Console.WriteLine($"{Ts()} CC      {e.Source} ch{e.Channel} cc={e.Controller} val={e.Value}");
    input.PitchBend += e => Console.WriteLine($"{Ts()} Bend    {e.Source} ch{e.Channel} val={e.Value}");
    input.ChannelPressure += e => Console.WriteLine($"{Ts()} Press   {e.Source} ch{e.Channel} val={e.Pressure}");
    Console.WriteLine($"Listening for {listenSeconds}s (receive only)...");
    Thread.Sleep(TimeSpan.FromSeconds(listenSeconds));
    Console.WriteLine("Done.");
}
return 0;
