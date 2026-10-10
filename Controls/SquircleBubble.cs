using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LocalLlm.Gui.Controls;

public sealed class SquircleBubble : Grid
{
    public SquircleBubble(UIElement content, Brush fill)
    {
        Children.Add(new SquircleSurface { Fill = fill, CornerRadius = new CornerRadius(16), IsHitTestVisible = false });
        Children.Add(new Border { Child = content, Padding = new Thickness(16, 12, 16, 12) });
    }
}
