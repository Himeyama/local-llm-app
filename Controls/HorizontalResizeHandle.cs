using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace LocalLlm.Gui.Controls;

public sealed class HorizontalResizeHandle : UserControl
{
    private readonly Thumb handle = new() { IsTabStop = true, UseSystemFocusVisuals = true };
    public event DragStartedEventHandler? DragStarted;
    public event DragDeltaEventHandler? DragDelta;
    public ControlTemplate HandleTemplate { get => handle.Template; set => handle.Template = value; }

    public HorizontalResizeHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = handle;
        handle.DragStarted += (_, args) => DragStarted?.Invoke(this, args);
        handle.DragDelta += (_, args) => DragDelta?.Invoke(this, args);
    }
}
