namespace Keyturn;

/// <summary>Non-secret description of a managed secret. The value itself lives in the OS credential store.</summary>
public sealed record SecretDefinition
{
    public required string Name { get; init; }
    public required string Schedule { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? LastSetAt { get; init; }

    /// <summary>
    /// When the secret was defined. Lets the watcher give a brand-new secret that has no value yet a short grace period
    /// before prompting for it. Null for secrets defined by earlier versions, which therefore get no grace.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Schedule ParsedSchedule => Keyturn.Schedule.Parse(Schedule);

    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

public enum SecretState
{
    /// <summary>Never set, or the value is missing from the credential store.</summary>
    Missing,
    /// <summary>Set and not yet due for rotation.</summary>
    Fresh,
    /// <summary>Set, but past its schedule.</summary>
    Stale,
}

public sealed record SecretStatus(SecretDefinition Definition, SecretState State, DateTimeOffset? ExpiresAt)
{
    public bool NeedsAttention => State != SecretState.Fresh;
}
