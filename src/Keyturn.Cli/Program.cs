using Keyturn;
using Keyturn.Cli;

var io = new CliIo(
    Console.Out, Console.Error, Console.In,
    Console.IsInputRedirected, Console.IsOutputRedirected,
    ReadHidden);

try
{
    return new KeyturnCli(new SecretVault(), io, new ProcessPromptLauncher()).Run(args);
}
catch (Exception ex)
{
    // Constructing the vault touches the credential store; keep failures out of stack-trace territory.
    Console.Error.WriteLine($"keyturn: credential store error: {ex.Message.Split('\n')[0]}");
    return KeyturnCli.StoreError;
}

static string ReadHidden()
{
    var buf = new System.Text.StringBuilder();
    while (Console.ReadKey(intercept: true) is var k && k.Key != ConsoleKey.Enter)
    {
        if (k.Key == ConsoleKey.Backspace) { if (buf.Length > 0) buf.Length--; }
        else if (!char.IsControl(k.KeyChar)) buf.Append(k.KeyChar);
    }
    return buf.ToString();
}
