using HarfBuzzSharp;
using SkiaSharp;

namespace Keyturn.App;

/// <summary>
/// <c>Keyturn.App --self-test</c>: loads the bundled Skia and HarfBuzz native libraries without showing a window,
/// so CI can verify a single-file build on every OS. (On Windows this is a GUI-subsystem exe with no console, so
/// the result is also written to the file named by KEYTURN_SELFTEST_LOG, and always reflected in the exit code.)
/// </summary>
static class SelfTest
{
    public static int Run()
    {
        string message;
        int code;
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(8, 8)) ?? throw new InvalidOperationException("Skia could not create a surface");
            surface.Canvas.Clear(SKColors.Red);
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf8("keyturn");
            message = "self-test: ok (Skia and HarfBuzz native libraries load)";
            code = 0;
        }
        catch (Exception ex)
        {
            message = $"self-test: FAILED: {ex.GetType().Name}: {ex.Message}";
            code = 1;
        }

        (code == 0 ? Console.Out : Console.Error).WriteLine(message);
        if (Environment.GetEnvironmentVariable("KEYTURN_SELFTEST_LOG") is { Length: > 0 } log) File.AppendAllText(log, message + Environment.NewLine);
        return code;
    }
}
