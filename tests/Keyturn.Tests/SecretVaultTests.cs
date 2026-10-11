using Keyturn;

namespace Keyturn.Tests;

public sealed class SecretVaultTests : IDisposable
{
    sealed class MemoryBackend : ISecretBackend
    {
        public readonly Dictionary<string, string> Values = new();
        public bool FailSet;
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) =>
            Values[name] = FailSet ? throw new IOException("locked") : value;
        public void Remove(string name) => Values.Remove(name);
    }

    readonly string _dir = Directory.CreateTempSubdirectory("keyturn-tests").FullName;
    readonly MemoryBackend _backend = new();
    DateTimeOffset _now = DateTimeOffset.Parse("2026-01-01T10:00:00+00:00");

    SecretVault Vault() => new(_backend, Path.Combine(_dir, "secrets.json"), () => _now);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void NewSecretIsMissingUntilSet()
    {
        var v = Vault();
        v.Define("vpn", "12h");
        Assert.Equal(SecretState.Missing, v.Status("vpn").State);
        Assert.Single(v.Due());
    }

    [Fact]
    public void GoesFreshThenStale()
    {
        var v = Vault();
        v.Define("vpn", "12h");
        v.Set("vpn", "pw1");
        Assert.Equal(SecretState.Fresh, v.Status("vpn").State);
        Assert.Empty(v.Due());

        _now = _now.AddHours(11);
        Assert.Equal(SecretState.Fresh, v.Status("vpn").State);
        _now = _now.AddHours(1);
        Assert.Equal(SecretState.Stale, v.Status("vpn").State);
        Assert.Equal("pw1", v.Get("vpn")); // stale values remain readable
    }

    [Fact]
    public void SettingResetsClock()
    {
        var v = Vault();
        v.Define("vpn", "1h");
        v.Set("vpn", "a");
        _now = _now.AddHours(2);
        v.Set("vpn", "b");
        Assert.Equal(SecretState.Fresh, v.Status("vpn").State);
        Assert.Equal("b", v.Get("vpn"));
    }

    [Fact]
    public void ValueMissingFromStoreCountsAsMissing()
    {
        var v = Vault();
        v.Define("vpn", "1d");
        v.Set("vpn", "a");
        _backend.Values.Clear(); // e.g. user deleted it in Keychain Access
        Assert.Equal(SecretState.Missing, v.Status("vpn").State);
    }

    [Fact]
    public void FailedStoreDoesNotResetClock()
    {
        var v = Vault();
        v.Define("vpn", "1h");
        v.Set("vpn", "a");
        _now = _now.AddHours(2);
        _backend.FailSet = true;
        Assert.Throws<IOException>(() => v.Set("vpn", "b"));
        Assert.Equal(SecretState.Stale, v.Status("vpn").State);
    }

    [Fact]
    public void RedefiningKeepsValueAndLastSet()
    {
        var v = Vault();
        v.Define("vpn", "1h", "VPN password");
        v.Set("vpn", "a");
        v.Define("vpn", "daily@06:00");
        var d = v.Find("vpn")!;
        Assert.Equal("daily@06:00", d.Schedule);
        Assert.Equal("VPN password", d.Description);
        Assert.NotNull(d.LastSetAt);
        Assert.Equal("a", v.Get("vpn"));
    }

    [Fact]
    public void NamesAreCaseInsensitiveAndValidated()
    {
        var v = Vault();
        v.Define("VPN", "1h");
        v.Set("vpn", "a");
        Assert.Equal("a", v.Get("Vpn"));
        Assert.Throws<ArgumentException>(() => v.Define("bad name", "1h"));
        Assert.Throws<FormatException>(() => v.Define("ok", "soon"));
        Assert.Throws<KeyNotFoundException>(() => v.Set("nope", "x"));
        Assert.Throws<ArgumentException>(() => v.Set("vpn", ""));
    }

    [Fact]
    public void RemoveDeletesValueAndDefinition()
    {
        var v = Vault();
        v.Define("vpn", "1h");
        v.Set("vpn", "a");
        Assert.True(v.Remove("vpn"));
        Assert.Empty(v.List());
        Assert.Empty(_backend.Values);
        Assert.False(v.Remove("vpn"));
    }

    [Fact]
    public void ConfigFileNeverContainsSecretValues()
    {
        var v = Vault();
        v.Define("vpn", "1h");
        v.Set("vpn", "super-secret-value");
        Assert.DoesNotContain("super-secret-value", File.ReadAllText(Path.Combine(_dir, "secrets.json")));
    }
}
