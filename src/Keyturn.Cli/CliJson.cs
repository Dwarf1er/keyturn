using System.Text.Json.Serialization;

namespace Keyturn.Cli;

// Public JSON shapes of the CLI. Part of the contract: keep property names stable.
internal sealed record SecretDto(string Name, string Schedule, string? Description, string State, DateTimeOffset? LastSetAt, DateTimeOffset? ExpiresAt);

internal sealed record GetDto(string Name, string State, DateTimeOffset? ExpiresAt, string Value);

// Source-generated (NativeAOT-safe). Two contexts because indentation is fixed at generation time.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SecretDto))]
[JsonSerializable(typeof(List<SecretDto>))]
[JsonSerializable(typeof(GetDto))]
internal partial class IndentedJson : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(SecretDto))]
[JsonSerializable(typeof(List<SecretDto>))]
[JsonSerializable(typeof(GetDto))]
internal partial class CompactJson : JsonSerializerContext;
