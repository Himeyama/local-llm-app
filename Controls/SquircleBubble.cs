using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LocalLlm.Gui.Controls;

public sealed class SquircleBubble : Grid
{
    public SquircleBubble(UIElement content, Brush fill)
    {
        SquircleSurface surface = new()
        {
            Fill = fill,
            CornerRadius = new CornerRadius(16),
            IsHitTestVisible = false
        };
        surface.Opacity = ActualTheme == ElementTheme.Dark ? 0.32 : 0.14;
        ActualThemeChanged += (sender, _) => surface.Opacity = sender.ActualTheme == ElementTheme.Dark ? 0.32 : 0.14;
        Children.Add(surface);
        Children.Add(new Border { Child = content, Padding = new Thickness(16, 12, 16, 12) });
    }
}
