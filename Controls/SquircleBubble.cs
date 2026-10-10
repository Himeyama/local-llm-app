using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LocalLlm.Gui.Controls;

public sealed class SquircleBubble : Grid
{
    private readonly Microsoft.UI.Xaml.Shapes.Path surface;

    public SquircleBubble(UIElement content, Brush fill)
    {
        surface = new Microsoft.UI.Xaml.Shapes.Path { Fill = fill, IsHitTestVisible = false };
        // Canvas keeps the background geometry out of content measurement.
        var background = new Canvas { IsHitTestVisible = false };
        background.Children.Add(surface);
        Children.Add(background);
        Children.Add(new Border { Child = content, Padding = new Thickness(16, 12, 16, 12) });
        SizeChanged += (_, args) => UpdateOutline(args.NewSize);
    }

    private void UpdateOutline(Size size)
    {
        var width = size.Width;
        var height = size.Height;
        if (width <= 0 || height <= 0) { surface.Data = null; return; }
        var radius = Math.Min(16, Math.Min(width, height) / 2);
        var outline = new PathFigure { StartPoint = new Point(radius, 0), IsClosed = true, IsFilled = true };
        var edges = new PolyLineSegment();
        var centers = new[] {
            new Point(width - radius, radius), new Point(width - radius, height - radius),
            new Point(radius, height - radius), new Point(radius, radius)
        };
        // CSS corner-shape: squircle is superellipse(2): x^4 + y^4 = 1.
        // Sample its quarter curves in local corner coordinates, retaining flat sides.
        const int steps = 64;
        static double Coordinate(double value) => Math.Abs(value) < 1e-12 ? 0 : Math.CopySign(Math.Sqrt(Math.Abs(value)), value);
        for (var corner = 0; corner < 4; corner++)
            for (var step = 0; step <= steps; step++)
            {
                var angle = (corner - 1 + (double)step / steps) * Math.PI / 2;
                edges.Points.Add(new Point(
                    centers[corner].X + radius * Coordinate(Math.Cos(angle)),
                    centers[corner].Y + radius * Coordinate(Math.Sin(angle))));
            }
        outline.Segments.Add(edges);
        var geometry = new PathGeometry();
        geometry.Figures.Add(outline);
        surface.Data = geometry;
    }
}
