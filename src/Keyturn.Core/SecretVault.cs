using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keyturn;

/// <summary>
/// The public API: secret definitions (names, schedules, last-set times) in a JSON file,
/// secret values in an <see cref="ISecretBackend"/>.
/// </summary>
public sealed class SecretVault
{
    readonly ISecretBackend _backend;
    readonly string _configPath;
    readonly Func<DateTimeOffset> _now;

    public SecretVault(ISecretBackend? backend = null, string? configPath = null, Func<DateTimeOffset>? now = null)
    {
        _backend = backend ?? SecretBackends.CreateDefault();
        _configPath = configPath ?? DefaultConfigPath;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetEnvironmentVariable("KEYTURN_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "keyturn"),
        "secrets.json");

    /// <summary>The definitions file.</summary>
    public string ConfigPath => _configPath;

    /// <summary>Its directory also holds the refresh locks, the snooze file and the watcher's lock, pid, stop and log files.</summary>
    public string ConfigDirectory => Path.GetDirectoryName(_configPath)!;

    /// <summary>The lock held while a prompt for this secret is open, so only one window ever asks for it.</summary>
    public string RefreshLockPath(string name) => Path.Combine(ConfigDirectory, $"refresh-{name}.lock");

    public IReadOnlyList<SecretDefinition> List() => Load();

    public SecretDefinition? Find(string name) =>
        Load().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public SecretStatus Status(string name) =>
        StatusOf(Find(name) ?? throw new KeyNotFoundException($"No secret named '{name}'."));

    public IReadOnlyList<SecretStatus> Statuses() => Load().Select(StatusOf).ToList();

    public IReadOnlyList<SecretStatus> Due() => Statuses().Where(s => s.NeedsAttention).ToList();

    /// <summary>
    /// What is due according to the definitions alone (never set, or past its schedule). Unlike <see cref="Due"/> it never
    /// reads the credential store, so the watcher can call it every few seconds without touching the keyring.
    /// </summary>
    public IReadOnlyList<SecretStatus> Expired()
    {
        var now = _now();
        return Load().Select(d => FromDefinition(d, now)).Where(s => s.NeedsAttention).ToList();
    }

    /// <summary>The fresh secret that goes stale first, or null when nothing is scheduled to.</summary>
    public SecretStatus? NextToExpire()
    {
        var now = _now();
        return Load().Select(d => FromDefinition(d, now)).Where(s => s.State == SecretState.Fresh).MinBy(s => s.ExpiresAt);
    }

    /// <summary>The moment the next fresh secret goes stale, or null when nothing is scheduled to.</summary>
    public DateTimeOffset? NextExpiry() => NextToExpire()?.ExpiresAt;

    /// <summary>Defines a new secret, or updates the schedule/description of an existing one.</summary>
    public SecretDefinition Define(string name, string schedule, string? description = null)
    {
        if (!SecretDefinition.IsValidName(name))
            throw new ArgumentException("Names may contain letters, digits, '-', '_' and '.' (max 64 chars).", nameof(name));
        Schedule.Parse(schedule); // validate

        var all = Load();
        var existing = all.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var def = (existing ?? new SecretDefinition { Name = name, Schedule = schedule, CreatedAt = _now() }) with
        {
            Schedule = schedule,
            Description = description ?? existing?.Description,
        };
        all.RemoveAll(s => ReferenceEquals(s, existing));
        all.Add(def);
        Save(all);
        return def;
    }

    /// <summary>Stores a new value and resets its rotation clock.</summary>
    public void Set(string name, string value)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("Secret value cannot be empty.", nameof(value));
        var all = Load();
        var i = all.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new KeyNotFoundException($"No secret named '{name}'. Define it first.");

        // Write the value first: if it fails, the clock is not reset.
        _backend.Set(all[i].Name, value);
        all[i] = all[i] with { LastSetAt = _now() };
        Save(all);
    }

    /// <summary>Returns the stored value, or null if it was never set. Stale values are still returned.</summary>
    public string? Get(string name)
    {
        var def = Find(name) ?? throw new KeyNotFoundException($"No secret named '{name}'.");
        return _backend.Get(def.Name);
    }

    public bool Remove(string name)
    {
        var all = Load();
        var removed = all.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        _backend.Remove(name);
        Save(all);
        return true;
    }

    SecretStatus FromDefinition(SecretDefinition def, DateTimeOffset now)
    {
        if (def.LastSetAt is not { } lastSet) return new SecretStatus(def, SecretState.Missing, null);
        var expires = def.ParsedSchedule.ExpiresAfter(lastSet);
        return new SecretStatus(def, now >= expires ? SecretState.Stale : SecretState.Fresh, expires);
    }

    SecretStatus StatusOf(SecretDefinition def)
    {
        if (def.LastSetAt is not { } lastSet || _backend.Get(def.Name) is null)
            return new SecretStatus(def, SecretState.Missing, null);

        var expires = def.ParsedSchedule.ExpiresAfter(lastSet);
        return new SecretStatus(def, _now() >= expires ? SecretState.Stale : SecretState.Fresh, expires);
    }

    List<SecretDefinition> Load()
    {
        if (!File.Exists(_configPath)) return [];
        using var stream = File.OpenRead(_configPath);
        return JsonSerializer.Deserialize(stream, VaultJson.Default.ListSecretDefinition) ?? [];
    }

    void Save(List<SecretDefinition> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        var tmp = _configPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(), VaultJson.Default.ListSecretDefinition));
        File.Move(tmp, _configPath, overwrite: true);
    }
}

/// <summary>Source-generated serializer for secrets.json, so the file works under trimming.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<SecretDefinition>))]
internal partial class VaultJson : JsonSerializerContext;
