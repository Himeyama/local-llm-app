using LocalLlm.Gui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using Windows.System;

namespace LocalLlm.Gui.Controls;

public sealed partial class FileEditorView : Grid
{
    // Use one small inset instead of combining the pane inset with TabView's default padding.
    private readonly TabView tabs = new() { IsAddTabButtonVisible = false, TabWidthMode = TabViewWidthMode.SizeToContent, Padding = new Thickness(0), Margin = new Thickness(0, 4, 0, 0) };
    private readonly InfoBar notice = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly DispatcherTimer autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, long> opening = new(StringComparer.OrdinalIgnoreCase);
    // One browser and one Monaco editor serve all native tabs. Each file has its own model.
    private WebView2? browser;
    private Task? initialization;
    private bool pageReady, stopped;
    private const string PageUrl = "https://editor.local/index.html";
    private long openRequest;
    private bool autoSaveEnabled = true, autoSaving, dialogOpen;
    private bool wordWrapEnabled = true;
    private bool interactionEnabled = true;
    public event Action? Changed;
    public event Action<string>? Failed;
    public int TabCount => tabs.TabItems.Count;
    public bool HasPendingChanges => Documents.Any(tab => tab.Document.IsDirty || tab.PendingSave != null);
    public bool InteractionEnabled
    {
        get => interactionEnabled;
        set { interactionEnabled = value; tabs.IsEnabled = value; PostReadOnly(); }
    }
    public EditorDocument? ActiveDocument => Active?.Document;
    private EditorTab? Active => (tabs.SelectedItem as TabViewItem)?.Tag as EditorTab;
    private IEnumerable<EditorTab> Documents => tabs.TabItems.OfType<TabViewItem>().Select(item => (EditorTab)item.Tag);

    private sealed class EditorTab(EditorDocument document, TabViewItem item)
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public EditorDocument Document = document;
        public readonly TabViewItem Item = item;
        public bool AutoSaveFailed, Closing;
        public long Revision;
        public Task<bool>? PendingSave;
    }

    public FileEditorView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(notice);
        SetRow(tabs, 1); Children.Add(tabs);
        tabs.SelectionChanged += (_, _) => { ActivateEditor(); Changed?.Invoke(); };
        tabs.TabCloseRequested += async (_, args) => { if (args.Tab.Tag is EditorTab tab) await CloseTabAsync(tab); };
        tabs.PreviewKeyDown += OnEditorKeyDown;
        autoSaveTimer.Tick += async (_, _) => await AutoSaveAsync();
        ActualThemeChanged += (_, _) => SendTheme();
    }

    public bool AutoSaveEnabled
    {
        get => autoSaveEnabled;
        set { autoSaveEnabled = value; autoSaveTimer.Stop(); if (value) ScheduleAutoSave(); }
    }

    public bool WordWrapEnabled
    {
        get => wordWrapEnabled;
        set { wordWrapEnabled = value; Post(new { type = "options", wordWrap = value }); }
    }

    public async Task OpenAsync(string path)
    {
        if (!InteractionEnabled) return;
        path = Path.GetFullPath(path);
        var request = ++openRequest;
        var existing = Documents.FirstOrDefault(tab => tab.Document.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { tabs.SelectedItem = existing.Item; ActivateEditor(focus: true); Changed?.Invoke(); return; }
        if (opening.ContainsKey(path)) { opening[path] = request; return; }
        opening.Add(path, request);
        try {
            var document = await EditorDocument.OpenAsync(path);
            if (stopped) return;
            var item = new TabViewItem { Header = Path.GetFileName(path), IsClosable = true };
            var tab = new EditorTab(document, item); item.Tag = tab;
            tabs.TabItems.Add(item);
            UpdateTab(tab);
            SendModel(tab);
            if (opening[path] == openRequest) tabs.SelectedItem = item;
            Changed?.Invoke();
            await EnsureEditorAsync();
            if (opening[path] == openRequest) ActivateEditor(focus: true);
        }
        catch (Exception ex) { Report(ex); }
        finally { opening.Remove(path); }
    }

    public void RemapPaths(string source, string destination)
    {
        foreach (var tab in Documents) { tab.Document.RemapPath(source, destination); UpdateTab(tab); Post(new { type = "rename", id = tab.Id, path = tab.Document.Path }); }
    }

    private void UpdateTab(EditorTab tab)
    {
        tab.Item.Header = Path.GetFileName(tab.Document.Path) + (tab.Document.IsDirty ? " ●" : "");
        ToolTipService.SetToolTip(tab.Item, tab.Document.Path);
        Changed?.Invoke();
    }

    private void ScheduleAutoSave()
    {
        autoSaveTimer.Stop();
        if (AutoSaveEnabled && !dialogOpen && Documents.Any(tab => tab.Document.IsDirty && !tab.AutoSaveFailed && !tab.Closing)) autoSaveTimer.Start();
    }

    private async Task AutoSaveAsync()
    {
        autoSaveTimer.Stop();
        if (autoSaving || !AutoSaveEnabled) return;
        autoSaving = true;
        try {
            foreach (var tab in Documents.Where(tab => tab.Document.IsDirty && !tab.AutoSaveFailed && !tab.Closing).ToArray()) {
                if (!AutoSaveEnabled) break;
                await SaveTabAsync(tab);
            }
        }
        finally { autoSaving = false; ScheduleAutoSave(); }
    }

    private async Task<bool> SaveTabAsync(EditorTab tab)
    {
        if (tab.PendingSave != null) return await tab.PendingSave;
        var task = SaveCoreAsync(tab); tab.PendingSave = task;
        try { return await task; }
        finally { tab.PendingSave = null; }
    }

    private async Task<bool> SaveCoreAsync(EditorTab tab)
    {
        try { await tab.Document.SaveAsync(); tab.AutoSaveFailed = false; notice.IsOpen = false; return true; }
        catch (Exception ex) { tab.AutoSaveFailed = true; Report(ex); return false; }
        finally { UpdateTab(tab); }
    }

    private async void OnEditorKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && args.Key == VirtualKey.S && Active is { } save) { args.Handled = true; await SaveTabAsync(save); }
        else if (ctrl && args.Key == VirtualKey.W && Active is { } close) { args.Handled = true; await CloseTabAsync(close); }
    }

    private async Task CloseTabAsync(EditorTab tab)
    {
        if (dialogOpen || tab.Closing) return;
        tab.Closing = true;
        PostReadOnly();
        try {
            if (tab.PendingSave != null) await tab.PendingSave;
            if (AutoSaveEnabled && tab.Document.IsDirty && !tab.AutoSaveFailed) await SaveTabAsync(tab);
            if (tab.Document.IsDirty) {
                dialogOpen = true;
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "変更を保存しますか？", Content = tab.Document.Path,
                    PrimaryButtonText = "保存", SecondaryButtonText = "保存せず閉じる", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary };
                var result = await dialog.ShowAsync(); dialogOpen = false;
                if (result == ContentDialogResult.None || (result == ContentDialogResult.Primary && !await SaveTabAsync(tab))) return;
            }
            Post(new { type = "close", id = tab.Id });
            tabs.TabItems.Remove(tab.Item); ActivateEditor(); Changed?.Invoke();
        }
        catch (Exception ex) { Report(ex); }
        finally { dialogOpen = false; tab.Closing = false; PostReadOnly(); ScheduleAutoSave(); }
    }

    public async Task<bool> ConfirmCloseAsync()
    {
        if (dialogOpen) return false;
        autoSaveTimer.Stop();
        foreach (var tab in Documents.ToArray()) if (tab.PendingSave != null) await tab.PendingSave;
        if (AutoSaveEnabled) foreach (var tab in Documents.Where(tab => tab.Document.IsDirty && !tab.AutoSaveFailed).ToArray()) await SaveTabAsync(tab);
        var dirty = Documents.Where(tab => tab.Document.IsDirty).ToList();
        if (dirty.Count == 0) return true;
        dialogOpen = true;
        try {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "未保存のファイルがあります", Content = string.Join("\n", dirty.Select(tab => tab.Document.Path)),
                PrimaryButtonText = "すべて保存", SecondaryButtonText = "保存せず終了", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary) return true;
            if (result != ContentDialogResult.Primary) return false;
            foreach (var tab in dirty) if (!await SaveTabAsync(tab)) return false;
            return true;
        }
        finally { dialogOpen = false; ScheduleAutoSave(); }
    }

    private Task EnsureEditorAsync()
    {
        if (stopped) return Task.CompletedTask;
        if (initialization != null) return initialization;
        browser ??= new WebView2();
        if (!Children.Contains(browser)) { SetRow(browser, 2); Children.Add(browser); }
        return initialization = InitializeEditorAsync();
    }

    private async Task InitializeEditorAsync()
    {
        try {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", Path.Combine(Settings.DataDirectory, "EditorWebView2"), null);
            if (stopped) return;
            await browser!.EnsureCoreWebView2Async(environment);
            if (stopped) return;
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping("editor.local", Path.Combine(AppContext.BaseDirectory, "Editor"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => { if (args.Uri != PageUrl) args.Cancel = true; };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += OnEditorMessage;
            core.ProcessFailed += (_, _) => {
                pageReady = false;
                Report(new IOException("エディタの表示プロセスが終了しました。編集内容を保持したまま表示を復旧します。"));
                if (!stopped) core.Reload();
            };
            core.NavigationCompleted += (_, args) => { if (!args.IsSuccess && !stopped) Report(new IOException("Monaco Editor を読み込めませんでした。")); };
            core.Navigate(PageUrl);
        }
        catch { initialization = null; throw; }
    }

    private async void OnEditorMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (stopped || args.Source != PageUrl) return;
        try {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            var data = message.RootElement;
            var type = data.GetProperty("type").GetString();
            if (type == "ready") {
                pageReady = true;
                foreach (var document in Documents) SendModel(document);
                SendTheme(); Post(new { type = "options", wordWrap = WordWrapEnabled }); ActivateEditor(); return;
            }
            var id = data.GetProperty("id").GetString();
            var tab = Documents.FirstOrDefault(item => item.Id == id);
            if (tab == null || !InteractionEnabled || tab.Closing) return;
            switch (type) {
                case "change":
                    if (data.GetProperty("revision").GetInt64() != tab.Revision) return;
                    tab.Document.Text = EditorDocument.Normalize(data.GetProperty("text").GetString() ?? "");
                    tab.AutoSaveFailed = false; UpdateTab(tab); ScheduleAutoSave(); break;
                case "save": await SaveTabAsync(tab); break;
                case "close": await CloseTabAsync(tab); break;
                case "reload": await ReloadTabAsync(tab, null); break;
            }
        }
        catch (Exception ex) { Report(ex); }
    }

    private void SendModel(EditorTab tab) => Post(new { type = "open", id = tab.Id, path = tab.Document.Path, text = tab.Document.Text, revision = tab.Revision });
    private void ActivateEditor(bool focus = false)
    {
        Post(new { type = "activate", id = Active?.Id, focus }); PostReadOnly();
    }
    private void PostReadOnly() => Post(new { type = "readOnly", value = !InteractionEnabled || Active?.Closing == true });
    private void SendTheme()
    {
        if (browser?.CoreWebView2 is { } core) core.Profile.PreferredColorScheme = ActualTheme == ElementTheme.Dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        Post(new { type = "theme", dark = ActualTheme == ElementTheme.Dark });
    }
    private void Post(object message)
    {
        if (pageReady && !stopped) browser?.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }
    public async Task RefreshCleanFilesAsync()
    {
        foreach (var tab in Documents.ToArray()) {
            if (tab.Document.IsDirty || tab.PendingSave != null || tab.Closing) continue;
            try {
                var document = await EditorDocument.OpenAsync(tab.Document.Path, tab.Document.Encoding);
                if (tab.Document.IsDirty || tab.PendingSave != null || !Documents.Contains(tab)) continue;
                // Keep cursor position and undo history when the file has not changed.
                if (tab.Document.Text == document.Text && tab.Document.Encoding == document.Encoding && tab.Document.LineEnding == document.LineEnding) continue;
                tab.Document = document; tab.Revision++;
                Post(new { type = "replace", id = tab.Id, text = document.Text, revision = tab.Revision }); UpdateTab(tab);
            }
            catch (FileNotFoundException) { } // Branch switches can remove an open file; retain the tab's contents.
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) { Report(ex); }
        }
    }
    public void Stop() { stopped = true; autoSaveTimer.Stop(); browser?.Close(); }
    private void Report(Exception ex) { notice.Message = ex.Message; notice.IsOpen = true; Failed?.Invoke(ex.Message); }
}
