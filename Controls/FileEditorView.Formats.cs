using LocalLlm.Gui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LocalLlm.Gui.Controls;

public sealed partial class FileEditorView
{
    public async Task ChangeEncodingAsync()
    {
        if (!InteractionEnabled || Active is not { } tab || dialogOpen) return;
        dialogOpen = true; autoSaveTimer.Stop();
        try {
            var formats = EditorEncoding.Common.ToList();
            if (!formats.Contains(tab.Document.Encoding)) formats.Add(tab.Document.Encoding);
            var choices = new ComboBox { Header = "文字コード", HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = formats.Select(format => format.Label).Append("カスタム入力…").ToList(), SelectedIndex = formats.IndexOf(tab.Document.Encoding) };
            var custom = new TextBox { Header = "文字コード名またはコードページ", PlaceholderText = "例: windows-1252 / 932 / iso-2022-jp", Visibility = Visibility.Collapsed };
            choices.SelectionChanged += (_, _) => custom.Visibility = choices.SelectedIndex == formats.Count ? Visibility.Visible : Visibility.Collapsed;
            var reopen = new CheckBox { Content = new TextBlock { Text = "選択した文字コードで読み直す（文字化けを修正）", TextWrapping = TextWrapping.Wrap } };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Spacing = 12, MinWidth = 320 };
            content.Children.Add(choices); content.Children.Add(custom); content.Children.Add(reopen); content.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "文字コードの変更", Content = content,
                PrimaryButtonText = "適用", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary };
            EditorEncoding? selected = null;
            dialog.PrimaryButtonClick += (dialogSender, args) => {
                try {
                    selected = choices.SelectedIndex == formats.Count ? EditorEncoding.Parse(custom.Text) : formats[choices.SelectedIndex];
                    _ = selected.StrictEncoding;
                    if (reopen.IsChecked != true) _ = selected.Encode(tab.Document.Text);
                }
                catch (Exception ex) { selected = null; args.Cancel = true; error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || selected == null) return;
            dialogOpen = false;
            if (reopen.IsChecked == true) await ReloadTabAsync(tab, selected);
            else { tab.Document.SetEncoding(selected); tab.AutoSaveFailed = false; UpdateTab(tab); }
        }
        catch (Exception ex) { Report(ex); }
        finally { dialogOpen = false; ScheduleAutoSave(); }
    }

    public async Task ChangeLineEndingAsync()
    {
        if (!InteractionEnabled || Active is not { } tab || dialogOpen) return;
        dialogOpen = true; autoSaveTimer.Stop();
        try {
            var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 280,
                ItemsSource = new[] { "LF", "CRLF", "CR" }, SelectedIndex = (int)tab.Document.LineEnding };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "改行コードの変更", Content = choices,
                PrimaryButtonText = "適用", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) {
                tab.Document.LineEnding = (EditorLineEnding)choices.SelectedIndex;
                tab.AutoSaveFailed = false; UpdateTab(tab);
            }
        }
        catch (Exception ex) { Report(ex); }
        finally { dialogOpen = false; ScheduleAutoSave(); }
    }

    private async Task ReloadActiveAsync()
    {
        if (Active is { } tab) await ReloadTabAsync(tab, null);
    }

    private async Task ReloadTabAsync(EditorTab tab, EditorEncoding? encoding)
    {
        if (dialogOpen) return;
        dialogOpen = true; autoSaveTimer.Stop();
        try {
            if (tab.PendingSave != null) await tab.PendingSave;
            if (tab.Document.IsDirty) {
                var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "編集内容を破棄して読み直しますか？", Content = tab.Document.Path,
                    PrimaryButtonText = "読み直す", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            }
            var document = await EditorDocument.OpenAsync(tab.Document.Path, encoding);
            tab.Document = document; tab.AutoSaveFailed = false; tab.Revision++;
            Post(new { type = "replace", id = tab.Id, text = document.Text, revision = tab.Revision });
            notice.IsOpen = false; UpdateTab(tab);
        }
        catch (Exception ex) { Report(ex); }
        finally { dialogOpen = false; ScheduleAutoSave(); }
    }
}
