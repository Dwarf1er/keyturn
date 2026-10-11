namespace Keyturn;

/// <summary>Where secret values are kept. The default implementation is the OS credential store.</summary>
public interface ISecretBackend
{
    string? Get(string name);
    void Set(string name, string value);
    void Remove(string name);
}
