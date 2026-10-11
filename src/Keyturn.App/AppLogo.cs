using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Keyturn.App;

/// <summary>The Keyturn mark for window headers: the same 256px image used as the window icon, so it always matches the logo.</summary>
static class AppLogo
{
    public static Control Create(double size)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Keyturn.App/Assets/keyturn.png"));
            var image = new Image { Source = new Bitmap(stream), Width = size, Height = size };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            return image;
        }
        catch (Exception)
        {
            return new Border { Width = size, Height = size }; // a decorative header image must never stop the prompt from appearing
        }
    }
}
