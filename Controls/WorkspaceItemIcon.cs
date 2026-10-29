using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LocalLlm.Gui.Controls;

// Matched fill/outline glyphs share the same font, size, and placement for color layering.
public sealed class WorkspaceItemIcon : Grid
{
    public static readonly DependencyProperty IsDirectoryProperty = DependencyProperty.Register(
        nameof(IsDirectory), typeof(bool), typeof(WorkspaceItemIcon),
        new PropertyMetadata(false, (sender, _) => ((WorkspaceItemIcon)sender).Draw()));

    public bool IsDirectory { get => (bool)GetValue(IsDirectoryProperty); set => SetValue(IsDirectoryProperty, value); }

    public WorkspaceItemIcon()
    {
        Width = Height = 16;
        Draw();
    }

    private void Draw()
    {
        Children.Clear();
        // Segoe MDL2 Assets supplies matching filled/outline pairs for tree folders and pages.
        AddLayer(IsDirectory ? "\uED42" : "\uE729", IsDirectory ? Color.FromArgb(255, 245, 196, 81) : Color.FromArgb(255, 217, 233, 255));
        AddLayer(IsDirectory ? "\uED41" : "\uE7C3", IsDirectory ? Color.FromArgb(255, 164, 106, 20) : Color.FromArgb(255, 57, 121, 191));
    }

    private void AddLayer(string glyph, Color color) => Children.Add(new FontIcon {
        FontFamily = new FontFamily("Segoe MDL2 Assets"), Glyph = glyph,
        FontSize = 16, Width = 16, Height = 16, IsTextScaleFactorEnabled = false,
        Foreground = new SolidColorBrush(color)
    });
}
