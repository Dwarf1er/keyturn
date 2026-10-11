using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keyturn;

/// <summary>A prompt that was dismissed: don't ask again until <see cref="Until"/>, for the value that was set at <see cref="SetAt"/>.</summary>
public sealed record SnoozeEntry(DateTimeOffset Until, DateTimeOffset? SetAt);

/// <summary>
/// Remembers which expired secrets were dismissed ("Later") so they aren't prompted again right away. Shared between the
/// watcher (which writes it) and the prompt windows (which read it). An entry is tied to the value it was written for:
/// rotating the secret changes <c>LastSetAt</c>, which voids the snooze, so a freshly rotated secret that expires again
/// soon is never missed.
/// </summary>
public sealed class SnoozeStore
{
    public const string FileName = "snooze.json";

    /// <summary>How long a brand-new secret that has no value yet is left alone: you are almost certainly about to set it.</summary>
    public static readonly TimeSpan NewSecretGrace = TimeSpan.FromMinutes(10);

    readonly string _path;
    readonly Func<DateTimeOffset> _now;
    readonly Dictionary<string, SnoozeEntry> _memory = new(StringComparer.OrdinalIgnoreCase); // survives a failing disk

    public SnoozeStore(string configDirectory, Func<DateTimeOffset>? now = null)
    {
        _path = Path.Combine(configDirectory, FileName);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public bool IsSnoozed(SecretDefinition definition) => SnoozedUntil(definition) is not null;

    /// <summary>
    /// Until when this secret should not be prompted for, or null if it should be prompted for now. Two reasons: it was
    /// dismissed ("Later"), or it is new and still has no value (see <see cref="NewSecretGrace"/>).
    /// </summary>
    public DateTimeOffset? SnoozedUntil(SecretDefinition definition)
    {
        var now = _now();
        DateTimeOffset? until = null;

        if (Entries().TryGetValue(definition.Name, out var entry) && entry.Until > now && entry.SetAt == definition.LastSetAt)
            until = entry.Until;

        // Only while it has no value: once it is set, the schedule decides, and a secret left without a value long after
        // it was created prompts as normal.
        if (definition.LastSetAt is null && definition.CreatedAt is { } created && created + NewSecretGrace > now)
        {
            var graceEnds = created + NewSecretGrace;
            if (until is null || graceEnds > until) until = graceEnds;
        }
        return until;
    }

    public void Snooze(SecretDefinition definition, TimeSpan duration)
    {
        var entry = new SnoozeEntry(_now() + duration, definition.LastSetAt);
        _memory[definition.Name] = entry;
        try
        {
            var all = ReadFile();
            var now = _now();
            foreach (var stale in all.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToList()) all.Remove(stale);
            all[definition.Name] = entry;
            WriteFile(all);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory copy still prevents a re-prompt loop in this process.
        }
    }

    /// <summary>Every snooze that is still in effect, soonest first.</summary>
    public IReadOnlyList<(string Name, DateTimeOffset Until)> Active() =>
        Entries().Where(kv => kv.Value.Until > _now()).OrderBy(kv => kv.Value.Until).Select(kv => (kv.Key, kv.Value.Until)).ToList();

    Dictionary<string, SnoozeEntry> Entries()
    {
        var all = ReadFile();
        foreach (var (name, entry) in _memory)
            if (!all.TryGetValue(name, out var onDisk) || onDisk.Until < entry.Until) all[name] = entry;
        return all;
    }

    Dictionary<string, SnoozeEntry> ReadFile()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            using var stream = File.OpenRead(_path);
            var parsed = JsonSerializer.Deserialize(stream, SnoozeJson.Default.Entries);
            return new Dictionary<string, SnoozeEntry>(parsed ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(StringComparer.OrdinalIgnoreCase); // a damaged or mid-write file just means "nothing snoozed"
        }
    }

    void WriteFile(Dictionary<string, SnoozeEntry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, SnoozeJson.Default.Entries));
        File.Move(tmp, _path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, SnoozeEntry>), TypeInfoPropertyName = "Entries")]
internal partial class SnoozeJson : JsonSerializerContext;
