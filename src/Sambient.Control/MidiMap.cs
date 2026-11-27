using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sambient.Control;

/// <summary>Which port a binding listens to ("Any" matches both).</summary>
public enum MapSource { Any, Keys, Daw }

/// <summary>
/// One CC binding. <see cref="Channel"/> is 1-based (1..16) or null for any channel. <see cref="Target"/> is an
/// application parameter id (e.g. "Harmonic", "Cutoff", "MasterVolume"); this library does not interpret it.
/// </summary>
public sealed record MidiBinding(MapSource Source, int? Channel, int Cc, string Target);

/// <summary>
/// CC -> parameter map, persisted as JSON (default ~/Library/Application Support/Sambient/midimap.json):
/// <code>
/// { "version": 1, "bindings": [ { "source": "Any", "channel": null, "cc": 21, "target": "Harmonic" } ] }
/// </code>
/// Reads (<see cref="TryResolve"/>) happen on the MIDI thread, edits on the UI thread: the binding list is an immutable
/// array swapped atomically (copy-on-write), so no locks are needed.
/// </summary>
public sealed class MidiMap
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private MidiBinding[] _bindings;

    public MidiMap(IEnumerable<MidiBinding>? bindings = null) => _bindings = bindings?.ToArray() ?? [];

    public IReadOnlyList<MidiBinding> Bindings => Volatile.Read(ref _bindings);

    public static string DefaultPath => AppDataPaths.File("midimap.json");

    /// <summary>
    /// PROVISIONAL defaults: CC 21..28 on any channel. The CC numbers the Launchkey MK3 knobs really send in its default
    /// (non-DAW) mode are not confirmed yet (DAW mode follows in M6.5); use MIDI learn / edit midimap.json to correct them.
    /// </summary>
    public static MidiMap CreateDefault()
    {
        string[] targets = ["Harmonic", "Cutoff", "Resonance", "Attack", "Release", "Level", "Pan", "Velocity"];
        return new MidiMap(targets.Select((t, i) => new MidiBinding(MapSource.Any, null, 21 + i, t)));
    }

    /// <summary>First binding (in list order) matching the source, channel (0-based event channel) and CC number.</summary>
    public bool TryResolve(ControllerSource source, int channel0, int cc, out string target)
    {
        foreach (var b in Volatile.Read(ref _bindings))
        {
            if (b.Cc != cc) continue;
            if (b.Source != MapSource.Any && (int)b.Source - 1 != (int)source) continue;
            if (b.Channel is { } ch && ch - 1 != channel0) continue;
            target = b.Target;
            return true;
        }
        target = "";
        return false;
    }

    /// <summary>Binds a CC to a target, replacing any other binding of the same target or of the same source/channel/CC.</summary>
    public void Bind(MapSource source, int? channel, int cc, string target)
    {
        var list = Bindings.Where(b => b.Target != target && !(b.Cc == cc && b.Source == source && b.Channel == channel)).ToList();
        list.Add(new MidiBinding(source, channel, cc, target));
        Volatile.Write(ref _bindings, list.ToArray());
    }

    /// <summary>Removes every binding of <paramref name="target"/>.</summary>
    public void Clear(string target)
        => Volatile.Write(ref _bindings, Bindings.Where(b => b.Target != target).ToArray());

    /// <summary>The first binding of a target, or null.</summary>
    public MidiBinding? Find(string target) => Bindings.FirstOrDefault(b => b.Target == target);

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public List<MidiBinding> Bindings { get; set; } = new();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { Bindings = Bindings.ToList() }, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Loads the map; a missing file creates the defaults on disk, a corrupt/unreadable file yields the defaults in memory
    /// (the broken file is left untouched until the next Save).
    /// </summary>
    public static MidiMap LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
            {
                var def = CreateDefault();
                try { def.Save(path); } catch { /* read-only disk etc.: keep in memory */ }
                return def;
            }
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), Json);
            if (model?.Bindings == null) return CreateDefault();
            return new MidiMap(model.Bindings.Where(b => b is { Target.Length: > 0 } && b.Cc is >= 0 and <= 127
                                                         && (b.Channel is null || b.Channel is >= 1 and <= 16)));
        }
        catch
        {
            return CreateDefault();
        }
    }
}

/// <summary>
/// MIDI learn: after <see cref="Arm"/>, the next incoming CC (except mod wheel CC1 and sustain CC64, which have fixed
/// roles) is bound to the armed target on the port it came from (any channel). Thread-safe (armed target is swapped atomically).
/// </summary>
public sealed class MidiLearn
{
    private readonly MidiMap _map;
    private string? _armed;

    public MidiLearn(MidiMap map) => _map = map;

    public string? ArmedTarget => Volatile.Read(ref _armed);

    public void Arm(string target) => Volatile.Write(ref _armed, target);
    public void Cancel() => Volatile.Write(ref _armed, null);

    /// <summary>Call for every incoming CC. Returns the bound target if this CC was consumed by learn, otherwise null.</summary>
    public string? TryCapture(ControllerCc cc)
    {
        if (cc.Controller is 1 or 64) return null;
        var target = Interlocked.Exchange(ref _armed, null);
        if (target == null) return null;
        _map.Bind(cc.Source == ControllerSource.Keys ? MapSource.Keys : MapSource.Daw, null, cc.Controller, target);
        return target;
    }
}
