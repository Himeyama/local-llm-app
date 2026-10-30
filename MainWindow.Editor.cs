using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace LocalLlm.Gui;

public sealed partial class MainWindow
{
    private double editorRatio = 0.5, editorDragWidth, editorDragDistance;
    private bool editorCloseAccepted, editorClosePending;

    private void InitializeEditor()
    {
        editorRatio = double.IsFinite(settings.EditorWidthRatio) ? Math.Clamp(settings.EditorWidthRatio, 0.05, 0.95) : 0.5;
        FileEditor.AutoSaveEnabled = settings.EditorAutoSave;
        FileEditor.WordWrapEnabled = settings.EditorWordWrap;
        FileEditor.Changed += UpdateEditorLayout;
        FileEditor.Failed += message => { if (!closing) ShowError("エディタ: " + message); };
        ExplorerPane.FileOpenRequested += path => _ = FileEditor.OpenAsync(path);
        ExplorerPane.PathRenamed += FileEditor.RemapPaths;
        ExplorerPane.ExpansionChanged += OnExplorerExpansionChanged;
        AppWindow.Closing += async (_, args) => {
            if (editorCloseAccepted || !FileEditor.HasPendingChanges) return;
            args.Cancel = true;
            if (editorClosePending) return;
            editorClosePending = true;
            FileEditor.InteractionEnabled = false;
            try {
                if (await FileEditor.ConfirmCloseAsync()) { editorCloseAccepted = true; Close(); }
            }
            catch (Exception ex) { ShowError("編集内容を保存できません: " + ex.Message); }
            finally { editorClosePending = false; if (!closing) FileEditor.InteractionEnabled = true; }
        };
        Closed += (_, _) => FileEditor.Stop();
    }

    private void UpdateEditorLayout()
    {
        bool visible = FileEditor.TabCount > 0;
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (EditorPane.Visibility != visibility) {
            EditorPane.Visibility = EditorResizeHandle.Visibility = visibility;
            EditorDividerColumn.Width = new GridLength(visible ? 8 : 0);
            if (visible) ApplyEditorRatio();
            else { EditorColumn.Width = new GridLength(0); ConversationColumn.Width = new GridLength(1, GridUnitType.Star); }
        }
        Grid.SetRowSpan(ConversationPane, visible ? 2 : 1);
        Grid.SetColumnSpan(ConsolePanel, visible ? 1 : 3);
        ApplyConsoleHeight();
        EditorStatus.Visibility = visible && page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        if (FileEditor.ActiveDocument is { } document) {
            EditorEncodingButton.Content = document.Encoding.Label;
            EditorLineEndingButton.Content = document.LineEndingLabel;
        }
    }

    private void OnChatWorkspaceSizeChanged(object sender, SizeChangedEventArgs args)
    {
        OnConsoleLayoutChanged(sender, args);
        if (FileEditor != null && EditorPane?.Visibility == Visibility.Visible) ApplyEditorRatio();
    }

    private void ApplyEditorRatio()
    {
        var available = ChatPanel.ActualWidth - 8;
        var ratio = available > 0 ? Math.Clamp(editorRatio, Math.Min(240, available * 0.4) / available, 1 - Math.Min(320, available * 0.4) / available) : editorRatio;
        EditorColumn.Width = new GridLength(ratio, GridUnitType.Star);
        ConversationColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private void OnEditorResizeStarted(object sender, DragStartedEventArgs args) { editorDragWidth = EditorPane.ActualWidth; editorDragDistance = 0; }
    private void OnEditorResizeDelta(object sender, DragDeltaEventArgs args)
    {
        editorDragDistance += args.HorizontalChange;
        var available = ChatPanel.ActualWidth - 8;
        if (available <= 0) return;
        editorRatio = Math.Clamp((editorDragWidth + editorDragDistance) / available, Math.Min(240, available * 0.4) / available, 1 - Math.Min(320, available * 0.4) / available);
        ApplyEditorRatio(); SaveWorkspacePaneLayout();
    }
    private void OnEditorResizeKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        args.Handled = true; editorRatio = Math.Clamp(editorRatio + (args.Key == VirtualKey.Right ? 0.03 : -0.03), 0.05, 0.95); ApplyEditorRatio(); SaveWorkspacePaneLayout();
    }
    private async void OnEditorEncodingClick(object sender, RoutedEventArgs args) => await FileEditor.ChangeEncodingAsync();
    private async void OnEditorLineEndingClick(object sender, RoutedEventArgs args) => await FileEditor.ChangeLineEndingAsync();
}
