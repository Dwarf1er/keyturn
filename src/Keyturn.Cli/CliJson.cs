using System.Text.Json.Serialization;

namespace Keyturn.Cli;

// Public JSON shapes of the CLI. Part of the contract: keep property names stable.
internal sealed record SecretDto(string Name, string Schedule, string? Description, string State, DateTimeOffset? LastSetAt, DateTimeOffset? ExpiresAt);

internal sealed record GetDto(string Name, string State, DateTimeOffset? ExpiresAt, string Value);

internal sealed record NextExpiryDto(string Name, DateTimeOffset At);

internal sealed record SnoozedDto(string Name, DateTimeOffset Until);

internal sealed record WatchStatusDto(bool Installed, bool Running, int? Pid, DateTimeOffset? Since, NextExpiryDto? Next, List<SnoozedDto> Snoozed, string Log);

// Source-generated, so it survives trimming. Two contexts because indentation is fixed at generation time.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SecretDto))]
[JsonSerializable(typeof(List<SecretDto>))]
[JsonSerializable(typeof(GetDto))]
[JsonSerializable(typeof(WatchStatusDto))]
internal partial class IndentedJson : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(SecretDto))]
[JsonSerializable(typeof(List<SecretDto>))]
[JsonSerializable(typeof(GetDto))]
[JsonSerializable(typeof(WatchStatusDto))]
internal partial class CompactJson : JsonSerializerContext;
