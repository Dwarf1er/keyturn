using Avalonia.Controls;
using Avalonia.Layout;

namespace Keyturn.App;

/// <summary>Shown when the credential store can't be reached, so a scheduled launch never fails silently.</summary>
public sealed class ErrorWindow : Window
{
    public ErrorWindow(string message)
    {
        Title = "Keyturn";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Keyturn can't reach your credential store", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                close,
            },
        };
    }
}
