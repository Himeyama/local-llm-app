using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LocalLlm.Gui.Controls;

public sealed class WorkspaceItemIcon : Grid
{
    public static readonly DependencyProperty IsDirectoryProperty = DependencyProperty.Register(
        nameof(IsDirectory), typeof(bool), typeof(WorkspaceItemIcon),
        new PropertyMetadata(false, (sender, _) => ((WorkspaceItemIcon)sender).Draw()));

    public bool IsDirectory { get => (bool)GetValue(IsDirectoryProperty); set => SetValue(IsDirectoryProperty, value); }

    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(WorkspaceItemIcon),
        new PropertyMetadata(null, (sender, _) => ((WorkspaceItemIcon)sender).Draw()));

    public string? Path { get => (string?)GetValue(PathProperty); set => SetValue(PathProperty, value); }

    public WorkspaceItemIcon()
    {
        Width = Height = 16;
        Draw();
    }

    private void Draw()
    {
        Children.Clear();
        if (!string.IsNullOrEmpty(Path))
        {
            var icon = ShellFileIcons.Create(Path, IsDirectory);
            icon.Width = icon.Height = 16;
            Children.Add(icon);
            return;
        }
        var name = IsDirectory ? "folder_20_filled.svg" : "document_text_20_color.svg";
        Children.Add(new Image { Source = new SvgImageSource(new Uri($"ms-appx:///Assets/{name}")), Width = 16, Height = 16 });
    }
}
