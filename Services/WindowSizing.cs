namespace LocalLlm.Gui.Services;

internal static class WindowSizing
{
    // XAML uses DIPs; AppWindow uses physical pixels. Leave room for the frame
    // and taskbar rather than imposing a minimum that exceeds a small display.
    internal static (int Width, int Height) InitialClientSize(double scale, int workWidth, int workHeight)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (workWidth <= 0 || workHeight <= 0) throw new ArgumentOutOfRangeException(nameof(workWidth));
        return ((int)Math.Max(1, Math.Min(Math.Round(1380 * scale), Math.Floor(workWidth * 0.9))),
                (int)Math.Max(1, Math.Min(Math.Round(900 * scale), Math.Floor(workHeight * 0.9))));
    }
}
