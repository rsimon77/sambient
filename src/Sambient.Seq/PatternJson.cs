using System.Globalization;
using System.Text;
using System.Text.Json;
using Sambient.Engine;

namespace Sambient.Seq;

/// <summary>
/// Versioned JSON format of a pattern palette. Parameters are stored by NAME (so reordering / extending the enums never breaks
/// old files), steps sparsely. Loading is tolerant: unknown names and fields are ignored, missing ones keep their defaults, values
/// are clamped to their ranges, and a broken file yields null instead of an exception.
/// <code>
/// { "format": "sambient-pattern", "version": 1, "name": "DRIFT", "patternBars": 0,
///   "globals": { "Bpm": 60, ... }, "layers": [ { "Wave": 3, ... }, x4 ],
///   "tracks": [ { "steps": [ { "i": 0, "notes": [ { "n": 60, "v": 100, "t": 4 } ], "prob": 100, "locks": { "Harmonic": 70 } } ] }, x4 ] }
/// </code>
/// </summary>
public static class PatternJson
{
    public const string FormatId = "sambient-pattern";
    public const int Version = 1;

    public static string Serialize(PatternPalette p)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", FormatId);
            w.WriteNumber("version", Version);
            w.WriteString("name", p.Name);
            w.WriteNumber("patternBars", p.PatternBars);
            w.WriteStartObject("globals");
            foreach (var g in PatternPalette.PatternGlobals) w.WriteNumber(g.ToString(), p.Globals[(int)g]);
            w.WriteEndObject();
            w.WriteStartArray("layers");
            for (int l = 0; l < SeqLimits.Tracks; l++)
            {
                w.WriteStartObject();
                foreach (var id in Enum.GetValues<ParamId>()) w.WriteNumber(id.ToString(), p.Layers[l][(int)id]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("tracks");
            for (int t = 0; t < SeqLimits.Tracks; t++)
            {
                w.WriteStartObject();
                w.WriteStartArray("steps");
                for (int s = 0; s < SeqLimits.MaxSteps; s++)
                {
                    var st = p.Tracks[t].Steps[s];
                    if (st.IsEmpty) continue;
                    w.WriteStartObject();
                    w.WriteNumber("i", s);
                    w.WriteStartArray("notes");
                    foreach (var n in st.Notes)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("n", n.Note); w.WriteNumber("v", n.Velocity); w.WriteNumber("t", n.Tie);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteNumber("prob", st.Probability);
                    if (st.Locks.Count > 0)
                    {
                        w.WriteStartObject("locks");
                        foreach (var kv in st.Locks) w.WriteNumber(kv.Key.ToString(), kv.Value);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Parses a palette; returns null when the text is not JSON or not a pattern file at all.</summary>
    public static PatternPalette? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() != FormatId) return null;
            var p = new PatternPalette();
            if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) p.Name = n.GetString() ?? "";
            if (root.TryGetProperty("patternBars", out var pb) && TryNum(pb, out double bars)) p.PatternBars = Math.Clamp((int)bars, 0, 512);
            if (root.TryGetProperty("globals", out var g) && g.ValueKind == JsonValueKind.Object)
                foreach (var prop in g.EnumerateObject())
                    if (Enum.TryParse<GlobalParamId>(prop.Name, out var gid) && Enum.IsDefined(gid) && TryNum(prop.Value, out double gv))
                        p.Globals[(int)gid] = ParamTable.Clamp(gid, (float)gv);
            if (root.TryGetProperty("layers", out var ls) && ls.ValueKind == JsonValueKind.Array)
            {
                int l = 0;
                foreach (var le in ls.EnumerateArray())
                {
                    if (l >= SeqLimits.Tracks) break;
                    if (le.ValueKind == JsonValueKind.Object)
                        foreach (var prop in le.EnumerateObject())
                            if (Enum.TryParse<ParamId>(prop.Name, out var id) && Enum.IsDefined(id) && TryNum(prop.Value, out double v))
                                p.Layers[l][(int)id] = ClampLayer(l, id, (float)v);
                    l++;
                }
            }
            if (root.TryGetProperty("tracks", out var ts) && ts.ValueKind == JsonValueKind.Array)
            {
                int t = 0;
                foreach (var te in ts.EnumerateArray())
                {
                    if (t >= SeqLimits.Tracks) break;
                    if (te.ValueKind == JsonValueKind.Object && te.TryGetProperty("steps", out var ss) && ss.ValueKind == JsonValueKind.Array)
                        foreach (var se in ss.EnumerateArray()) ReadStep(p.Tracks[t], se);
                    t++;
                }
            }
            return p;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static float ClampLayer(int layer, ParamId id, float v)
    {
        v = ParamTable.Clamp(id, v);
        return id == ParamId.Wave ? Math.Min(v, ParamTable.MaxWave(layer)) : v;
    }

    private static bool TryNum(JsonElement e, out double v)
    {
        v = 0;
        if (e.ValueKind == JsonValueKind.Number) return e.TryGetDouble(out v) && double.IsFinite(v);
        if (e.ValueKind == JsonValueKind.String) return double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
        return false;
    }

    private static void ReadStep(TrackData track, JsonElement se)
    {
        if (se.ValueKind != JsonValueKind.Object) return;
        if (!se.TryGetProperty("i", out var ie) || !TryNum(ie, out double di)) return;
        int i = (int)di;
        if ((uint)i >= SeqLimits.MaxSteps) return;
        var st = track.Steps[i];
        st.Clear();
        if (se.TryGetProperty("prob", out var pe) && TryNum(pe, out double prob)) st.Probability = Math.Clamp((int)prob, SeqLimits.MinProbability, 100);
        if (se.TryGetProperty("notes", out var ns) && ns.ValueKind == JsonValueKind.Array)
            foreach (var ne in ns.EnumerateArray())
            {
                if (st.Notes.Count >= SeqLimits.MaxNotesPerStep) break;
                if (ne.ValueKind != JsonValueKind.Object || !ne.TryGetProperty("n", out var nn) || !TryNum(nn, out double note)) continue;
                double vel = 100, tie = 1;
                if (ne.TryGetProperty("v", out var ve)) TryNum(ve, out vel);
                if (ne.TryGetProperty("t", out var te)) TryNum(te, out tie);
                st.Notes.Add(StepNoteData.Make((int)note, (int)vel, (int)tie));
            }
        if (se.TryGetProperty("locks", out var lk) && lk.ValueKind == JsonValueKind.Object)
            foreach (var prop in lk.EnumerateObject())
                if (Enum.TryParse<ParamId>(prop.Name, out var id) && Enum.IsDefined(id) && LockRules.IsLockable(id) && TryNum(prop.Value, out double lv))
                    st.Locks[id] = ParamTable.Clamp(id, (float)lv);
    }
}
