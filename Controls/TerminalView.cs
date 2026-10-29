using System.Text.Json;
using LocalLlm.Gui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;

namespace LocalLlm.Gui.Controls;

// WinUI owns the tab strip and lifecycle; xterm.js renders the terminal surfaces only.
internal sealed class TerminalView : Grid, IDisposable
{
    private const string PageUrl = "https://terminal.local/index.html";
    private readonly WebView2 browser = new();
    private readonly TabView tabView = new() {
        Margin = new Thickness(0, 8, 0, 0),
        TabWidthMode = TabViewWidthMode.SizeToContent,
        IsAddTabButtonVisible = true,
        BorderThickness = new Thickness(0), Padding = new Thickness(0)
    };
    private readonly Dictionary<string, TabSession> tabs = new();
    private readonly Dictionary<long, (TabSession Tab, TaskCompletionSource Complete)> writes = new();
    private long writeId;
    private long nextTabId;
    private bool pageReady;
    private bool changingTabs;
    private bool disposed;
    public string Directory { get; }
    public bool IsFaulted { get; private set; }
    public event Action<string>? Failed;

    private sealed class TabSession(int columns, int rows)
    {
        public int Columns = columns, Rows = rows;
        public PowerShellConsole? Shell;
        public readonly CancellationTokenSource Lifetime = new();
        public readonly Queue<string> PendingInput = new();
        public int PendingCharacters;
        public bool Ready, Closed;
    }

    public TerminalView(string directory)
    {
        Directory = Path.GetFullPath(directory);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SetRow(browser, 1);
        Children.Add(tabView);
        Children.Add(browser);
        tabView.AddTabButtonClick += (_, _) => AddTab();
        tabView.TabCloseRequested += (_, args) => CloseNativeTab(args.Tab);
        tabView.SelectionChanged += (_, _) => { if (!changingTabs) Activate(); };
        tabView.PreviewKeyDown += OnTabKeyDown;
        AddTab();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(browser, "PowerShell ターミナル");
    }

    public async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", Path.Combine(Settings.DataDirectory, "TerminalWebView2"), null);
        if (disposed) return;
        await browser.EnsureCoreWebView2Async(environment);
        if (disposed) return;
        var core = browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping("terminal.local", Path.Combine(AppContext.BaseDirectory, "Terminal"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += (_, args) => { if (args.Uri != PageUrl) args.Cancel = true; };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, _) => {
            if (disposed) return;
            IsFaulted = true;
            pageReady = false;
            CloseAllTabs();
            Failed?.Invoke("ターミナルの表示プロセスが終了しました。コンソールを開き直してください。");
        };
        core.Navigate(PageUrl);
    }

    public void Activate()
    {
        if (!pageReady || tabView.SelectedItem is not TabViewItem { Tag: string id }) return;
        browser.Focus(FocusState.Programmatic);
        Post(new { type = "activate", tabId = id });
    }

    private void AddTab()
    {
        if (disposed) return;
        var id = (++nextTabId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var item = new TabViewItem { Header = "PowerShell", Tag = id, IsClosable = true, IconSource = new FontIconSource { Glyph = "\uE756" } };
        changingTabs = true;
        tabView.TabItems.Add(item); tabView.SelectedItem = item;
        changingTabs = false;
        if (pageReady) { Post(new { type = "create", tabId = id }); Activate(); }
    }

    private void CloseNativeTab(TabViewItem item)
    {
        if (disposed || !tabView.TabItems.Contains(item) || item.Tag is not string id) return;
        var index = tabView.TabItems.IndexOf(item);
        var wasSelected = ReferenceEquals(tabView.SelectedItem, item);
        changingTabs = true;
        tabView.TabItems.Remove(item);
        if (tabView.TabItems.Count > 0 && (wasSelected || tabView.SelectedItem == null))
            tabView.SelectedItem = tabView.TabItems[Math.Min(index, tabView.TabItems.Count - 1)];
        changingTabs = false;
        CloseTab(id);
        if (pageReady) Post(new { type = "close", tabId = id });
        if (tabView.TabItems.Count == 0) AddTab();
        else Activate();
    }

    private void OnTabKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var modifiers = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;
        if (!modifiers(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
            !modifiers(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        if (args.Key == Windows.System.VirtualKey.T) { args.Handled = true; AddTab(); }
        else if (args.Key == Windows.System.VirtualKey.W && tabView.SelectedItem is TabViewItem selected) {
            args.Handled = true; CloseNativeTab(selected);
        }
    }

    private async void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (disposed || args.Source != PageUrl) return;
        string? tabId = null;
        try {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            var root = message.RootElement;
            tabId = root.TryGetProperty("tabId", out var id) ? id.GetString() : null;
            switch (root.GetProperty("type").GetString()) {
                case "ready":
                    CloseAllTabs(); pageReady = true;
                    foreach (TabViewItem item in tabView.TabItems) Post(new { type = "create", tabId = (string)item.Tag });
                    Activate();
                    break;
                case "new-tab": AddTab(); break;
                case "close-tab":
                    if (tabView.SelectedItem is TabViewItem selected) CloseNativeTab(selected);
                    break;
                case "open":
                    if (tabId == null || !tabView.TabItems.OfType<TabViewItem>().Any(item => (string)item.Tag == tabId)) return;
                    CloseTab(tabId);
                    await OpenTabAsync(tabId, root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
                    break;
                case "input":
                    if (tabId == null || !tabs.TryGetValue(tabId, out var inputTab)) return;
                    var data = root.GetProperty("data").GetString() ?? "";
                    if (inputTab.Ready) await inputTab.Shell!.SendAsync(data);
                    else {
                        if (inputTab.PendingCharacters + data.Length > 1_000_000) throw new InvalidOperationException("PowerShell の起動完了後に入力してください。");
                        inputTab.PendingInput.Enqueue(data); inputTab.PendingCharacters += data.Length;
                    }
                    break;
                case "resize":
                    if (tabId == null || !tabs.TryGetValue(tabId, out var resizeTab)) return;
                    resizeTab.Columns = root.GetProperty("cols").GetInt32(); resizeTab.Rows = root.GetProperty("rows").GetInt32();
                    resizeTab.Shell?.Resize(resizeTab.Columns, resizeTab.Rows);
                    break;
                case "ack":
                    if (writes.Remove(root.GetProperty("id").GetInt64(), out var pending)) pending.Complete.TrySetResult();
                    break;
                case "copy":
                    var package = new DataPackage(); package.SetText(root.GetProperty("data").GetString() ?? "");
                    Clipboard.SetContent(package);
                    break;
                case "paste":
                    var clipboard = Clipboard.GetContent();
                    if (clipboard.Contains(StandardDataFormats.Text)) {
                        var text = await clipboard.GetTextAsync();
                        if (!disposed && tabId != null && tabs.ContainsKey(tabId)) Post(new { type = "paste", tabId, data = text });
                    }
                    break;
            }
        }
        catch (Exception ex) {
            if (!disposed && ex is not OperationCanceledException and not System.Threading.Channels.ChannelClosedException &&
                (tabId == null || tabs.ContainsKey(tabId))) {
                Post(new { type = "status", tabId, text = "\r\n" + ex.Message + "\r\n" });
                Failed?.Invoke("コンソール: " + ex.Message);
            }
        }
    }

    private async Task OpenTabAsync(string tabId, int columns, int rows)
    {
        var tab = new TabSession(columns, rows);
        tabs.Add(tabId, tab);
        PowerShellConsole created;
        try { created = await Task.Run(() => new PowerShellConsole(Directory, columns, rows, bytes => WriteOutputAsync(tabId, tab, bytes))); }
        catch when (disposed || tab.Closed) { return; }
        if (disposed || tab.Closed) { created.Dispose(); return; }
        tab.Shell = created;
        created.Resize(tab.Columns, tab.Rows);
        created.Exited += code => DispatcherQueue.TryEnqueue(() => {
            if (!disposed && !tab.Closed) Post(new { type = "status", tabId, text = $"\r\n[PowerShell 終了: {code} — 新しいタブを開いてください]\r\n" });
        });
        while (!tab.Closed && tab.PendingInput.TryDequeue(out var input)) await created.SendAsync(input);
        tab.PendingCharacters = 0; tab.Ready = true;
    }

    private async Task WriteOutputAsync(string tabId, TabSession tab, byte[] bytes)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => {
            if (disposed || tab.Closed) { complete.TrySetCanceled(); return; }
            var id = ++writeId;
            writes.Add(id, (tab, complete));
            try { Post(new { type = "output", tabId, data = Convert.ToBase64String(bytes), id }); }
            catch (Exception ex) { writes.Remove(id); complete.TrySetException(ex); }
        })) complete.TrySetCanceled();
        await complete.Task.WaitAsync(tab.Lifetime.Token);
    }

    private void CloseTab(string tabId)
    {
        if (!tabs.Remove(tabId, out var tab)) return;
        tab.Closed = true; tab.Lifetime.Cancel(); tab.Shell?.Dispose(); tab.PendingInput.Clear();
        foreach (var pending in writes.Where(item => item.Value.Tab == tab).ToArray()) {
            writes.Remove(pending.Key); pending.Value.Complete.TrySetCanceled();
        }
    }

    private void CloseAllTabs() { foreach (var id in tabs.Keys.ToArray()) CloseTab(id); }
    private void Post(object message) => browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; CloseAllTabs();
        browser.Close(); Children.Clear();
    }
}
