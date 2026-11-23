using LocalLlm.Gui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.System;

namespace LocalLlm.Gui.Controls;

public sealed partial class FileEditorView
{
    private TabViewItem? webTab;
    private Grid? webPanel;
    private WebView2? webView;
    private TextBox? address;
    private Button? back, forward;
    private Task? webInitialization;
    private readonly HashSet<string> browserFileUrls = new(StringComparer.OrdinalIgnoreCase);

    public async Task OpenBrowserFileAsync(string path)
    {
        if (!InteractionEnabled) return;
        path = Path.GetFullPath(path);
        if (!BrowserFiles.CanPreview(path)) throw new ArgumentException("ブラウザで表示できないファイル形式です。");
        if (!File.Exists(path)) throw new FileNotFoundException("ファイルが見つかりません。", path);
        var url = new Uri(path).AbsoluteUri;
        await OpenBrowserAsync();
        browserFileUrls.Add(url);
        webView!.CoreWebView2.Navigate(url);
    }

    public async Task OpenBrowserAsync()
    {
        if (stopped) throw new ObjectDisposedException(nameof(FileEditorView));
        if (webTab == null) {
            webTab = new TabViewItem { Header = "ブラウザ", IsClosable = true };
            webPanel = new Grid();
            webPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            webPanel.RowDefinitions.Add(new RowDefinition());
            var toolbar = new Grid { ColumnSpacing = 4, Margin = new Thickness(4) };
            for (int i = 0; i < 3; i++) toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition());
            Button Add(string label, string glyph, int column, Action action) {
                var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Width = 36, Height = 36, Padding = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
                ToolTipService.SetToolTip(button, label);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
                button.Click += (_, _) => { try { action(); } catch (Exception ex) { Report(ex); } };
                SetColumn(button, column); toolbar.Children.Add(button); return button;
            }
            back = Add("戻る", "\uE72B", 0, () => { if (webView?.CanGoBack == true) webView.GoBack(); });
            forward = Add("進む", "\uE72A", 1, () => { if (webView?.CanGoForward == true) webView.GoForward(); });
            Add("更新", "\uE72C", 2, () => webView?.Reload());
            address = new TextBox { PlaceholderText = "URL を入力して Enter", MinWidth = 80 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(address, "URL");
            address.KeyDown += async (_, e) => {
                if (e.Key != VirtualKey.Enter) return;
                e.Handled = true;
                try { await OpenBrowserAsync(); webView!.CoreWebView2.Navigate(browserFileUrls.Contains(address.Text.Trim()) ? address.Text.Trim() : BrowserUrl(address.Text)); }
                catch (Exception ex) { Report(ex); }
            };
            SetColumn(address, 3); toolbar.Children.Add(address); webPanel.Children.Add(toolbar);
            webView = new WebView2(); SetRow(webView, 1); webPanel.Children.Add(webView);
            SetRow(webPanel, 2); Children.Add(webPanel);
            tabs.TabItems.Add(webTab);
        }
        tabs.SelectedItem = webTab; UpdateBrowserVisibility(); Changed?.Invoke();
        await (webInitialization ??= InitializeBrowserAsync());
    }

    private async Task InitializeBrowserAsync()
    {
        try {
            var view = webView!;
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", Path.Combine(Settings.DataDirectory, "BrowserWebView2"), null);
            await view.EnsureCoreWebView2Async(environment);
            if (view != webView || stopped) return;
            var core = view.CoreWebView2;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.NavigationStarting += (_, e) => { if (!IsBrowserUrl(e.Uri) && !browserFileUrls.Contains(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (IsBrowserUrl(e.Uri)) core.Navigate(e.Uri); };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.SourceChanged += (_, _) => { if (address != null) address.Text = core.Source; };
            core.HistoryChanged += (_, _) => { back!.IsEnabled = core.CanGoBack; forward!.IsEnabled = core.CanGoForward; };
            core.NavigationCompleted += (_, e) => { if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) Report(new IOException("ページを読み込めません: " + e.WebErrorStatus)); };
            back!.IsEnabled = forward!.IsEnabled = false;
        }
        catch { webInitialization = null; throw; }
    }

    private static bool IsBrowserUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
    private static string BrowserUrl(string value) {
        value = value.Trim();
        if (!value.Contains("://")) value = "https://" + value;
        if (!IsBrowserUrl(value)) throw new ArgumentException("HTTP/HTTPS の URL を指定してください。");
        return new Uri(value).AbsoluteUri;
    }

    private void UpdateBrowserVisibility()
    {
        bool active = webTab != null && ReferenceEquals(tabs.SelectedItem, webTab);
        if (webPanel != null) webPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (browser != null) browser.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseBrowser()
    {
        webView?.Close(); webView = null; webInitialization = null;
        browserFileUrls.Clear();
        if (webPanel != null) Children.Remove(webPanel);
        webPanel = null;
        var item = webTab; webTab = null;
        if (item != null) tabs.TabItems.Remove(item);
        UpdateBrowserVisibility(); Changed?.Invoke();
    }

    // Chat generation runs on a worker thread; all WebView access stays on the UI thread.
    public Task<string> ExecuteBrowserAsync(JsonObject args, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () => {
            try { ct.ThrowIfCancellationRequested(); completion.TrySetResult(await BrowserActionAsync(args, ct)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new IOException("ブラウザを開けません。"));
        return completion.Task.WaitAsync(ct);
    }

    private async Task<string> BrowserActionAsync(JsonObject args, CancellationToken ct)
    {
        await OpenBrowserAsync(); ct.ThrowIfCancellationRequested();
        var core = webView!.CoreWebView2;
        var action = args["action"]?.GetValue<string>() ?? "read";
        if (action == "screenshot") {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            ct.ThrowIfCancellationRequested();
            if (stream.Size > 20 * 1024 * 1024) throw new IOException("スクリーンショットが 20 MiB を超えています。");
            stream.Seek(0);
            using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            return new JsonObject { ["url"] = core.Source, ["image_url"] = "data:image/png;base64," + Convert.ToBase64String(bytes) }.ToJsonString();
        }
        if (action is "navigate" or "back" or "forward" or "reload") {
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e) {
                if (e.IsSuccess) loaded.TrySetResult(true);
                else loaded.TrySetException(new IOException("ページの読み込みに失敗: " + e.WebErrorStatus));
            }
            core.NavigationCompleted += Completed;
            try {
                switch (action) {
                    case "navigate": core.Navigate(BrowserUrl(args["value"]?.GetValue<string>() ?? "")); break;
                    case "back": if (!core.CanGoBack) throw new IOException("戻る履歴がありません。"); core.GoBack(); break;
                    case "forward": if (!core.CanGoForward) throw new IOException("進む履歴がありません。"); core.GoForward(); break;
                    case "reload": core.Reload(); break;
                }
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            finally { core.NavigationCompleted -= Completed; }
        }
        else if (action is "click" or "type") {
            var selector = JsonSerializer.Serialize(args["selector"]?.GetValue<string>() ?? "");
            var value = JsonSerializer.Serialize(args["value"]?.GetValue<string>() ?? "");
            var script = action == "click" ? "e.click();" : $"if (!(e instanceof HTMLInputElement || e instanceof HTMLTextAreaElement)) throw Error('Input required'); const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(e), 'value').set; setter.call(e, {value}); e.dispatchEvent(new Event('input', {{bubbles:true}})); e.dispatchEvent(new Event('change', {{bubbles:true}}));";
            var result = await core.ExecuteScriptAsync($"(() => {{ const e = document.querySelector({selector}); if (!e) return 'Element not found'; {script} return 'ok'; }})()");
            if (result != "\"ok\"") throw new IOException("ブラウザ操作に失敗: " + result);
        }
        else if (action != "read") throw new ArgumentException("未対応のブラウザ操作です。");
        ct.ThrowIfCancellationRequested();
        return await core.ExecuteScriptAsync("({url:location.href,title:document.title,text:(document.body?.innerText||'').slice(0,20000),elements:Array.from(document.querySelectorAll('a,button,input,textarea,select')).slice(0,100).map(e=>({tag:e.tagName,text:(e.innerText||e.getAttribute('aria-label')||e.getAttribute('placeholder')||'').slice(0,160),href:e.href,selector:e.id?'#'+CSS.escape(e.id):(()=>{let p=[],n=e;while(n&&n!==document.documentElement){p.unshift(n.tagName.toLowerCase()+':nth-child('+(Array.from(n.parentElement.children).indexOf(n)+1)+')');n=n.parentElement;}return 'html>'+p.join('>');})()}))})");
    }
}
