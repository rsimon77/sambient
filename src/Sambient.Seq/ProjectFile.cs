using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sambient.Seq;

/// <summary>Outcome of reading a project file: the project, or a human-readable error. Warnings list tolerated problems.</summary>
public sealed record ProjectLoadResult(Project? Project, string? Error, IReadOnlyList<string> Warnings)
{
    public bool Ok => Project != null;
    public static ProjectLoadResult Fail(string error) => new(null, error, []);
}

/// <summary>
/// The <c>.sambient</c> project file: ONE versioned JSON document (UTF-8, indented). Palettes reuse the pattern format of
/// <see cref="PatternJson"/>; chains and settings are stored by name. Loading is tolerant (unknown fields ignored, missing
/// fields default, values clamped, unreadable palettes become INIT with a warning); a file that is not a Sambient project yields
/// an error result and never an exception.
/// <code>
/// { "format": "sambient-project", "version": 1, "name": "Evening", "created": "2026-11-27T10:00:00+00:00", "modified": "...",
///   "settings": { "bpmMode": "Pattern", "resetReverb": false, "muteMode": "Sound", "metronomeVolume": 8, "preCount": 2, "masterTuneCents": 0 },
///   "chains": [ { "name": "Song", "entries": [ { "bank": 0, "slot": 3 }, { "bank": 4, "slot": 0 } ],
///                 "loop": true, "repeats": 4, "relaxation": true, "fadeSeconds": 10, "fadeInSeconds": 0 } ],
///   "palettes": [ { "format": "sambient-pattern", "version": 1, "name": "DEEPDRFT", ... }, ... 128 entries (index = bank * 16 + slot) ... ],
///   "samples": {} }
/// </code>
/// MIGRATION PATH (samples). The "samples" section is reserved: today an empty object that is preserved verbatim
/// (<see cref="Project.SamplesJson"/>). When user noise samples arrive, version 2 adds <c>"samples": { "S01": { "name", "rate",
/// "channels", "dataBase64" | "file": "samples/S01.wav" } }</c> inside the JSON for small libraries, and when the library grows
/// past a few MB the container becomes a zip (<c>project.json</c> + <c>samples/*.wav</c>) with the same JSON as its manifest; the
/// loader sniffs the zip magic ("PK"), so version-1 JSON files keep loading unchanged.
/// </summary>
public static class ProjectFile
{
    public const string FormatId = "sambient-project";
    public const int Version = 1;
    public const string Extension = ".sambient";

    // ------------------------------------------------------------ write

    /// <summary>Serializes the project. <paramref name="overlay"/> substitutes one palette (autosave: the live, not yet saved edit).</summary>
    public static string Serialize(Project p, (int Index, PatternPalette Palette)? overlay = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", FormatId);
            w.WriteNumber("version", Version);
            w.WriteString("name", p.Name);
            w.WriteString("created", p.Created.ToString("o", CultureInfo.InvariantCulture));
            w.WriteString("modified", p.Modified.ToString("o", CultureInfo.InvariantCulture));

            var s = p.Settings;
            w.WriteStartObject("settings");
            w.WriteString("bpmMode", s.BpmMode.ToString());
            w.WriteBoolean("resetReverb", s.ResetReverbOnChange);
            w.WriteString("muteMode", s.MuteMode.ToString());
            w.WriteNumber("metronomeVolume", s.MetronomeVolume);
            w.WriteNumber("preCount", s.PreCount);
            w.WriteNumber("masterTuneCents", s.MasterTuneCents);
            w.WriteEndObject();

            w.WriteStartArray("chains");
            foreach (var c in p.GetChains())
            {
                w.WriteStartObject();
                w.WriteString("name", c.Name);
                w.WriteStartArray("entries");
                foreach (var e in c.Entries) { w.WriteStartObject(); w.WriteNumber("bank", e.Bank); w.WriteNumber("slot", e.Slot); w.WriteEndObject(); }
                w.WriteEndArray();
                w.WriteBoolean("loop", c.Loop);
                w.WriteNumber("repeats", c.Repeats);
                w.WriteBoolean("relaxation", c.Relaxation);
                w.WriteNumber("fadeSeconds", c.FadeSeconds);
                w.WriteNumber("fadeInSeconds", c.FadeInSeconds);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("palettes");
            for (int i = 0; i < Project.SlotCount; i++)
            {
                var pal = overlay is { } o && o.Index == i ? o.Palette : p.GetPalette(i / PatternBank.SlotsPerBank, i % PatternBank.SlotsPerBank);
                using var doc = JsonDocument.Parse(PatternJson.Serialize(pal));
                doc.RootElement.WriteTo(w);
            }
            w.WriteEndArray();

            w.WritePropertyName("samples");
            if (!string.IsNullOrWhiteSpace(p.SamplesJson))
            {
                try { using var sd = JsonDocument.Parse(p.SamplesJson); sd.RootElement.WriteTo(w); }
                catch (JsonException) { w.WriteStartObject(); w.WriteEndObject(); }
            }
            else { w.WriteStartObject(); w.WriteEndObject(); }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Atomic write: the text goes to "file.tmp" first, the previous version (if any) is copied to "file.bak" (when
    /// <paramref name="keepBackup"/>), then the temp file replaces the target. Throws IOException / UnauthorizedAccessException.
    /// </summary>
    public static void WriteAtomic(string path, string text, bool keepBackup)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        try
        {
            if (keepBackup && File.Exists(path)) File.Copy(path, Path.ChangeExtension(path, ".bak"), overwrite: true);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Saves to <paramref name="path"/> (atomic, previous version kept as .bak). On success the project's file path is updated, its
    /// modified time stamped and the dirty flag cleared. Returns null on success or the error text.
    /// </summary>
    public static string? Save(Project p, string path, (int Index, PatternPalette Palette)? overlay = null)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var prev = p.Modified;
            p.Modified = now;
            string json = Serialize(p, overlay);
            try { WriteAtomic(path, json, keepBackup: true); }
            catch { p.Modified = prev; throw; }
            p.FilePath = Path.GetFullPath(path);
            p.MarkClean();
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return e.Message;
        }
    }

    // ------------------------------------------------------------ read

    public static ProjectLoadResult Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ProjectLoadResult.Fail($"Cannot read \"{Path.GetFileName(path)}\": {e.Message}");
        }
        var r = Parse(text);
        if (r.Project != null) { r.Project.FilePath = Path.GetFullPath(path); r.Project.MarkClean(); }
        return r;
    }

    public static ProjectLoadResult Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { return ProjectLoadResult.Fail("The file is damaged (not valid JSON): " + e.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ProjectLoadResult.Fail("The file is not a Sambient project.");
            bool hasFormat = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String;
            if (hasFormat && f.GetString() != FormatId) return ProjectLoadResult.Fail($"The file is not a Sambient project (format \"{f.GetString()}\").");
            if (!hasFormat && !root.TryGetProperty("palettes", out _)) return ProjectLoadResult.Fail("The file is not a Sambient project.");

            var warnings = new List<string>();
            try { return new ProjectLoadResult(Read(root, warnings), null, warnings); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
            {
                return ProjectLoadResult.Fail("The project file is damaged: " + e.Message);
            }
        }
    }

    private static Project Read(JsonElement root, List<string> warnings)
    {
        var proj = Project.CreateNew();
        int version = ProjectFile.Version;
        if (root.TryGetProperty("version", out var ve) && Num(ve, out double v)) version = (int)v;
        if (version > Version) warnings.Add($"Created by a newer Sambient (format version {version}); unknown parts are ignored.");
        proj.LoadedFormatVersion = version;

        string name = root.TryGetProperty("name", out var ne) && ne.ValueKind == JsonValueKind.String ? ne.GetString() ?? "" : "";
        var settings = new ProjectSettings();
        if (root.TryGetProperty("settings", out var se) && se.ValueKind == JsonValueKind.Object) settings = ReadSettings(se);
        proj.LoadMeta(name, settings);

        if (root.TryGetProperty("created", out var ce) && ce.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(ce.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)) proj.Created = created;
        if (root.TryGetProperty("modified", out var me) && me.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(me.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var modified)) proj.Modified = modified;

        var chains = new List<SongChain>();
        if (root.TryGetProperty("chains", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var ce2 in cs.EnumerateArray()) if (ce2.ValueKind == JsonValueKind.Object) chains.Add(ReadChain(ce2));
        proj.LoadChains(chains);

        int bad = 0, seen = 0;
        if (root.TryGetProperty("palettes", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var pe in ps.EnumerateArray())
            {
                if (seen >= Project.SlotCount) break;
                int bank = seen / PatternBank.SlotsPerBank, slot = seen % PatternBank.SlotsPerBank;
                seen++;
                var pal = pe.ValueKind == JsonValueKind.Object ? PatternJson.TryParse(pe.GetRawText()) : null;
                if (pal == null) { bad++; pal = new PatternPalette("INIT"); }
                proj.LoadSlot(bank, slot, pal);
            }
        }
        for (; seen < Project.SlotCount; seen++)
            proj.LoadSlot(seen / PatternBank.SlotsPerBank, seen % PatternBank.SlotsPerBank, new PatternPalette("INIT"));
        int missing = Project.SlotCount - Math.Min(Project.SlotCount, CountArray(root, "palettes"));
        if (bad > 0) warnings.Add($"{bad} palette(s) could not be read and were replaced by INIT.");
        if (missing > 0) warnings.Add($"{missing} palette(s) were missing and were replaced by INIT.");

        if (root.TryGetProperty("samples", out var sm) && sm.ValueKind == JsonValueKind.Object && sm.EnumerateObject().Any())
            proj.SamplesJson = sm.GetRawText();
        return proj;
    }

    private static int CountArray(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0;

    private static ProjectSettings ReadSettings(JsonElement e)
    {
        var s = new ProjectSettings();
        if (e.TryGetProperty("bpmMode", out var b))
        {
            if (b.ValueKind == JsonValueKind.String && Enum.TryParse<BpmMode>(b.GetString(), true, out var bm)) s = s with { BpmMode = bm };
            else if (Num(b, out double bn)) s = s with { BpmMode = bn >= 1 ? BpmMode.Global : BpmMode.Pattern };
        }
        if (e.TryGetProperty("resetReverb", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False) s = s with { ResetReverbOnChange = r.GetBoolean() };
        if (e.TryGetProperty("muteMode", out var m))
        {
            if (m.ValueKind == JsonValueKind.String && Enum.TryParse<MuteMode>(m.GetString(), true, out var mm)) s = s with { MuteMode = mm };
            else if (Num(m, out double mn)) s = s with { MuteMode = mn >= 1 ? MuteMode.Sequence : MuteMode.Sound };
        }
        if (e.TryGetProperty("metronomeVolume", out var mv) && Num(mv, out double mvn)) s = s with { MetronomeVolume = (int)mvn };
        if (e.TryGetProperty("preCount", out var pc) && Num(pc, out double pcn)) s = s with { PreCount = (int)pcn };
        if (e.TryGetProperty("masterTuneCents", out var mt) && Num(mt, out double mtn)) s = s with { MasterTuneCents = (float)mtn };
        return s.Normalized();
    }

    private static SongChain ReadChain(JsonElement e)
    {
        var c = new SongChain();
        if (e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) c.Name = n.GetString() ?? "Song";
        if (e.TryGetProperty("entries", out var es) && es.ValueKind == JsonValueKind.Array)
            foreach (var en in es.EnumerateArray())
            {
                if (en.ValueKind != JsonValueKind.Object) continue;
                if (en.TryGetProperty("bank", out var b) && Num(b, out double bn) && en.TryGetProperty("slot", out var sl) && Num(sl, out double sn))
                {
                    var slot = new ChainSlot((int)bn, (int)sn);
                    if (slot.IsValid) c.Entries.Add(slot);
                }
            }
        if (e.TryGetProperty("loop", out var lp) && lp.ValueKind is JsonValueKind.True or JsonValueKind.False) c.Loop = lp.GetBoolean();
        if (e.TryGetProperty("repeats", out var rp) && Num(rp, out double rn)) c.Repeats = (int)rn;
        if (e.TryGetProperty("relaxation", out var rx) && rx.ValueKind is JsonValueKind.True or JsonValueKind.False) c.Relaxation = rx.GetBoolean();
        if (e.TryGetProperty("fadeSeconds", out var fs) && Num(fs, out double fsn)) c.FadeSeconds = (int)fsn;
        if (e.TryGetProperty("fadeInSeconds", out var fi) && Num(fi, out double fin)) c.FadeInSeconds = (int)fin;
        return c.Normalized();
    }

    private static bool Num(JsonElement e, out double v)
    {
        v = 0;
        if (e.ValueKind == JsonValueKind.Number) return e.TryGetDouble(out v) && double.IsFinite(v);
        if (e.ValueKind == JsonValueKind.String) return double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
        return false;
    }
}
