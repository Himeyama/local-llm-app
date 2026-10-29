using LocalLlm.Gui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace LocalLlm.Gui.Controls;

public sealed partial class WorkspaceExplorerView
{
    private const string ClipboardMarker = "LocalLlmGui.ExplorerTransfer";
    private bool operationRunning;

    private async Task RunCommandAsync(Func<Task> command)
    {
        if (operationRunning) return;
        operationRunning = true;
        tree.IsEnabled = NewFileButton.IsEnabled = NewFolderButton.IsEnabled = RefreshButton.IsEnabled = false;
        status.Visibility = Visibility.Collapsed;
        try { await command(); }
        catch (Exception ex) {
            // A multi-item transfer can fail after some entries moved; reflect the actual disk state.
            if (ex is IOException or UnauthorizedAccessException) await ReloadAsync();
            ShowStatus(ex.Message);
        }
        finally {
            operationRunning = false;
            tree.IsEnabled = NewFileButton.IsEnabled = NewFolderButton.IsEnabled = RefreshButton.IsEnabled = true;
        }
    }

    private string CreationDirectory()
    {
        if (tree.SelectedNode?.Content is not WorkspaceEntry entry) return workspace;
        return entry.IsDirectory ? entry.Path : Path.GetDirectoryName(entry.Path)!;
    }

    private async Task CreateItemAsync(bool folder, string directory)
    {
        var operations = new WorkspaceFileOperations(workspace);
        var created = await EditNameAsync(folder ? "新しいフォルダー" : "新しいファイル", folder ? "新しいフォルダー" : "新しいファイル.txt", directory,
            name => Task.Run(() => operations.Create(directory, name, folder)));
        if (created != null) {
            await ReloadAsync(created);
            if (!folder) FileOpenRequested?.Invoke(created);
        }
    }

    private async Task RenameItemAsync(WorkspaceEntry entry)
    {
        var operations = new WorkspaceFileOperations(workspace);
        if (operations.IsRoot(entry.Path)) throw new IOException("作業フォルダー自体の名前は変更できません。");
        var renamed = await EditNameAsync("名前の変更", entry.Name, Path.GetDirectoryName(entry.Path)!,
            name => Task.Run(() => operations.Rename(entry.Path, name)));
        if (renamed != null) { PathRenamed?.Invoke(entry.Path, renamed); await ReloadAsync(renamed); }
    }

    private async Task<string?> EditNameAsync(string title, string initialName, string directory, Func<string, Task<string>> apply)
    {
        string? result = null;
        var input = new TextBox { Text = initialName, MinWidth = 300, Header = "名前" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = directory, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 });
        content.Children.Add(input); content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = content,
            PrimaryButtonText = "保存", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary };
        dialog.Opened += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        dialog.PrimaryButtonClick += async (_, args) => {
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try { result = await apply(input.Text); }
            catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        await dialog.ShowAsync();
        return result;
    }

    private async Task DeleteItemAsync(WorkspaceEntry entry)
    {
        var operations = new WorkspaceFileOperations(workspace);
        if (operations.IsRoot(entry.Path)) throw new IOException("作業フォルダー自体は削除できません。");
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = $"「{entry.Name}」を削除しますか？",
            Content = new TextBlock { Text = entry.Path + (entry.IsDirectory ? "\n\nフォルダー内の項目も含めて完全に削除します。" : "\n\n完全に削除します。") + "この操作は元に戻せません。", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "削除", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Task.Run(() => operations.Delete(entry.Path));
        await ReloadAsync(Path.GetDirectoryName(entry.Path));
    }

    private async Task CopyItemAsync(WorkspaceEntry entry, bool cut)
    {
        var operations = new WorkspaceFileOperations(workspace);
        if (operations.IsRoot(entry.Path)) throw new IOException("作業フォルダー自体は切り取り・コピーできません。");
        IStorageItem item = entry.IsDirectory ? await StorageFolder.GetFolderFromPathAsync(entry.Path) : await StorageFile.GetFileFromPathAsync(entry.Path);
        var package = new DataPackage { RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
        package.SetStorageItems(new[] { item });
        package.SetData(ClipboardMarker, Guid.NewGuid().ToString("N"));
        Clipboard.SetContent(package);
        ShowStatus((cut ? "切り取り: " : "コピー: ") + entry.Name);
    }

    private Task CopyPathAsync(WorkspaceEntry entry, bool relative)
    {
        var text = relative ? new WorkspaceFileOperations(workspace).RelativePath(entry.Path) : entry.Path;
        var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package);
        return Task.CompletedTask;
    }

    private async Task PasteAsync(WorkspaceEntry entry)
    {
        if (!entry.IsDirectory) throw new IOException("貼り付け先のフォルダーを選択してください。");
        var operations = new WorkspaceFileOperations(workspace);
        var clipboard = Clipboard.GetContent();
        if (!clipboard.Contains(StandardDataFormats.StorageItems)) throw new IOException("切り取りまたはコピーしたファイルがありません。");
        var items = await clipboard.GetStorageItemsAsync();
        var paths = items.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var cut = clipboard.RequestedOperation.HasFlag(DataPackageOperation.Move);
        var marker = clipboard.Contains(ClipboardMarker) ? await clipboard.GetDataAsync(ClipboardMarker) as string : null;
        var pasted = await Task.Run(() => operations.Paste(paths, entry.Path, cut));
        if (cut) for (int index = 0; index < paths.Length; index++) PathRenamed?.Invoke(paths[index], pasted[index]);
        // The clipboard owner can disappear during a transfer; completion notification is best effort.
        try {
            clipboard.ReportOperationCompleted(cut ? DataPackageOperation.Move : DataPackageOperation.Copy);
            if (cut && marker != null) {
                var current = Clipboard.GetContent();
                if (current.Contains(ClipboardMarker) && await current.GetDataAsync(ClipboardMarker) as string == marker) Clipboard.Clear();
            }
        }
        catch (Exception) { }
        await ReloadAsync(pasted.Count == 1 ? pasted[0] : entry.Path);
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (operationRunning) return;
        var source = args.OriginalSource as DependencyObject;
        TreeViewNode? node = null;
        while (source != null && source != tree) {
            if (source is TreeViewItem container) { node = tree.NodeFromContainer(container); break; }
            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }
        var positioned = args.TryGetPosition(tree, out var point);
        node ??= positioned ? tree.RootNodes.FirstOrDefault() : tree.SelectedNode ?? tree.RootNodes.FirstOrDefault();
        if (node?.Content is not WorkspaceEntry entry) return;
        tree.SelectedNode = node;
        bool editable = !new WorkspaceFileOperations(workspace).IsRoot(entry.Path);
        bool canPaste;
        try { canPaste = entry.CanExpand && Clipboard.GetContent().Contains(StandardDataFormats.StorageItems); }
        catch (Exception) { canPaste = false; }
        var menu = new MenuFlyout();
        if (entry.IsDirectory) {
            AddMenu(menu, "新しいファイル", "\uE7C3", () => CreateItemAsync(false, entry.Path));
            AddMenu(menu, "新しいフォルダー", "\uED41", () => CreateItemAsync(true, entry.Path));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        AddMenu(menu, "切り取り", "\uE8C6", () => CopyItemAsync(entry, true), editable, "Ctrl+X");
        AddMenu(menu, "コピー", "\uE8C8", () => CopyItemAsync(entry, false), editable, "Ctrl+C");
        AddMenu(menu, "貼り付け", "\uE77F", () => PasteAsync(entry), canPaste, "Ctrl+V");
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenu(menu, "パスのコピー", "\uE71B", () => CopyPathAsync(entry, false));
        AddMenu(menu, "相対パスのコピー", "\uE71B", () => CopyPathAsync(entry, true));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenu(menu, "名前の変更", "\uE8AC", () => RenameItemAsync(entry), editable, "F2");
        AddMenu(menu, "削除", "\uE74D", () => DeleteItemAsync(entry), editable, "Delete");
        args.Handled = true;
        if (positioned) menu.ShowAt(tree, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(tree.ContainerFromNode(node) as FrameworkElement ?? tree);
    }

    private void AddMenu(MenuFlyout menu, string label, string glyph, Func<Task> action, bool enabled = true, string accelerator = "")
    {
        var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled, KeyboardAcceleratorTextOverride = accelerator,
            Icon = new FontIcon { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"), Glyph = glyph } };
        item.Click += async (_, _) => await RunCommandAsync(action);
        menu.Items.Add(item);
    }
}
