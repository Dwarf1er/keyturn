namespace Keyturn.Cli;

/// <summary>Tiny argument bag: pull flags and options out, then read what is left as positionals.</summary>
sealed class Args
{
    readonly List<string> _items;
    public Args(IEnumerable<string> items) => _items = items.ToList();

    public bool Flag(string name) => _items.Remove(name);

    public string? Option(string name)
    {
        var i = _items.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= _items.Count) throw new ArgumentException($"{name} needs a value.");
        var value = _items[i + 1];
        _items.RemoveRange(i, 2);
        return value;
    }

    public List<string> Options(string name)
    {
        var all = new List<string>();
        while (Option(name) is { } v) all.Add(v);
        return all;
    }

    public string Positional(int index, string usage) =>
        index < _items.Count ? _items[index] : throw new ArgumentException($"usage: keyturn {usage}");

    public IReadOnlyList<string> Rest(int from = 0) => _items.Skip(from).ToList();
    public int Count => _items.Count;
}
