using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Keyturn.App;

/// <summary>Line icons (24-unit grid, Lucide-style) styled by the "icon" classes in App.axaml.</summary>
public static class Icons
{
    public const string Key = "M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3m-3.5 3.5L19 4";
    public const string Lock = "M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2zM7 11V7a5 5 0 0 1 10 0v4";
    public const string Eye = "M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7zM12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z";
    public const string EyeOff = "M17.94 17.94A10.07 10.07 0 0 1 12 19c-7 0-10-7-10-7a18.5 18.5 0 0 1 5.06-5.94M9.9 5.24A9.12 9.12 0 0 1 12 5c7 0 10 7 10 7a18.5 18.5 0 0 1-2.16 3.19M14.12 14.12a3 3 0 1 1-4.24-4.24M2 2l20 20";
    public const string Check = "M20 6L9 17l-5-5";
    public const string Plus = "M12 5v14M5 12h14";
    public const string Trash = "M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2";
    public const string Clock = "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 7v5l3 2";
    public const string ChevronDown = "M6 9l6 6 6-6";
    public const string ChevronUp = "M18 15l-6-6-6 6";
    public const string Shield = "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z";

    public static Control Create(string data, double size = 16, params string[] classes)
    {
        var path = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data) };
        path.Classes.Add("icon");
        foreach (var c in classes) path.Classes.Add(c);
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }
}
