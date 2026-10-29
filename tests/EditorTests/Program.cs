using System.Text;
using System.Text.Json;
using LocalLlm.Gui.Services;

var parent = Path.GetFullPath(AppContext.BaseDirectory);
var root = Path.Combine(parent, "editor-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    var empty = Path.Combine(root, "empty.txt"); File.WriteAllBytes(empty, Array.Empty<byte>());
    var document = await EditorDocument.OpenAsync(empty);
    Check(document.Encoding == new EditorEncoding(65001) && document.LineEnding == EditorLineEnding.LF && !document.IsDirty, "Empty files default to UTF-8 without BOM and LF");
    var japanese = "日本語の文字コードを自動判定します。ひらがな、カタカナ、漢字と数字123。\r\n保存した内容が失われないことを確認します。\r\n";
    foreach (var format in new[] { new EditorEncoding(65001), new EditorEncoding(65001, true), new EditorEncoding(932), new EditorEncoding(51932), new EditorEncoding(50220), new EditorEncoding(1200, true), new EditorEncoding(1201, true), new EditorEncoding(12000, true), new EditorEncoding(12001, true) }) {
        var path = Path.Combine(root, format.CodePage + "-" + format.EmitBom + ".txt");
        await File.WriteAllBytesAsync(path, format.Encode(japanese));
        var opened = await EditorDocument.OpenAsync(path);
        Check(opened.Encoding == format, $"Encoding detection failed: {format.Label} detected {opened.Encoding.Label}");
        Check(opened.Text == EditorDocument.Normalize(japanese) && opened.LineEnding == EditorLineEnding.CRLF, "Decoded content and line endings must match");
        Check(!opened.IsDirty, "Opening must not cause an autosave");
        opened.Text += "追記\n"; Check(opened.IsDirty, "Edits must mark a tab dirty");
        await opened.SaveAsync();
        Check(!opened.IsDirty && (await File.ReadAllBytesAsync(path)).SequenceEqual(format.Encode(japanese + "追記\r\n")), "Saving must preserve the detected encoding, BOM and CRLF");
    }
    foreach (var codePage in new[] { 1200, 1201, 12000, 12001 }) {
        var path = Path.Combine(root, "no-bom-" + codePage + ".txt");
        var format = new EditorEncoding(codePage); await File.WriteAllBytesAsync(path, format.Encode("ASCII header: 1234567890\n日本語の内容\n"));
        Check((await EditorDocument.OpenAsync(path)).Encoding == format, "Recognize BOM-less Unicode text with zero-byte structure");
    }
    var mixedPath = Path.Combine(root, "mixed.txt");
    var mixed = "one\ntwo\r\nthree\nfour\r"; await File.WriteAllBytesAsync(mixedPath, Encoding.UTF8.GetBytes(mixed));
    var mixedDoc = await EditorDocument.OpenAsync(mixedPath);
    Check(mixedDoc.MixedLineEndings && mixedDoc.LineEnding == EditorLineEnding.LF, "Report mixed line endings and detect their predominant form");
    await mixedDoc.SaveAsync(); Check(await File.ReadAllTextAsync(mixedPath) == mixed, "Unchanged mixed files must remain byte-for-byte unchanged");
    mixedDoc.LineEnding = EditorLineEnding.LF;
    Check(mixedDoc.IsDirty, "Explicitly selecting the predominant EOL still normalizes mixed files");
    await mixedDoc.SaveAsync();
    Check(await File.ReadAllTextAsync(mixedPath) == "one\ntwo\nthree\nfour\n", "Selecting LF normalizes mixed EOL even when LF was predominant");
    mixedDoc.LineEnding = EditorLineEnding.CR; await mixedDoc.SaveAsync();
    Check(await File.ReadAllTextAsync(mixedPath) == "one\rtwo\rthree\rfour\r", "Changing EOL must update every line without duplicating CR");
    Check(!mixedDoc.MixedLineEndings && !mixedDoc.IsDirty, "Saving clears the mixed and dirty indicators");
    document.Text = "日本語\n"; document.SetEncoding(EditorEncoding.Parse("932")); await document.SaveAsync();
    Check((await File.ReadAllBytesAsync(empty)).SequenceEqual(new EditorEncoding(932).Encode("日本語\n")), "Custom code-page input converts the saved encoding");
    document.SetEncoding(new EditorEncoding(65001, true)); await document.SaveAsync();
    Check((await File.ReadAllBytesAsync(empty)).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "An encoding-only change can add a UTF-8 BOM");
    document.Text += "😀";
    try { document.SetEncoding(new EditorEncoding(932)); throw new Exception("Lossy encoding was accepted"); } catch (EncoderFallbackException) { }
    Check(document.Encoding == new EditorEncoding(65001, true), "Rejected conversions cannot change the file's encoding");
    var forced = await EditorDocument.OpenAsync(empty, new EditorEncoding(65001));
    Check(forced.Encoding.EmitBom && forced.Text == "日本語\n", "Reopening with a chosen encoding strips and preserves an existing BOM");

    var externalPath = Path.Combine(root, "external.txt"); await File.WriteAllTextAsync(externalPath, "original\n", new UTF8Encoding(false));
    var external = await EditorDocument.OpenAsync(externalPath); external.Text = "my unsaved edit\n";
    await File.WriteAllTextAsync(externalPath, "changed externally\n");
    await RejectAsync(() => external.SaveAsync());
    Check(await File.ReadAllTextAsync(externalPath) == "changed externally\n" && external.Text == "my unsaved edit\n" && external.IsDirty, "Autosave cannot clobber external changes or drop unsaved text");
    var renamedPath = Path.Combine(root, "renamed.txt"); File.Move(empty, renamedPath); document.RemapPath(empty, renamedPath);
    await document.SaveAsync(); Check(File.Exists(renamedPath) && !File.Exists(empty), "Renaming a file preserves its editor document and save target");
    var binary = Path.Combine(root, "image.png"); await File.WriteAllBytesAsync(binary, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    await RejectAsync(async () => { await EditorDocument.OpenAsync(binary); });
    Check(!Directory.EnumerateFiles(root, ".local-llm-editor-*").Any(), "Atomic-save temporary files are cleaned up");

    var store = new ChatStore(Path.Combine(root, "history"));
    var session = new ChatSession { ConsoleOpen = true, Workspace = root, UpdatedAt = new DateTime(2026, 1, 1) };
    store.Save(session, updateTimestamp: false);
    Check(store.Load().Single().ConsoleOpen && session.UpdatedAt == new DateTime(2026, 1, 1), "Console visibility persists without changing history order");
    store.SaveConsoleVisibility(session, false);
    Check(!store.Load().Single().ConsoleOpen && !JsonSerializer.Deserialize<ChatSession>("{}")!.ConsoleOpen, "Closed and legacy console states restore correctly");
    var generation = new ChatSession { Id = session.Id, Workspace = root, ConsoleOpen = false, Messages = new() { new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = "new transcript" } } };
    store.Save(generation);
    var generationTimestamp = generation.UpdatedAt;
    store.SaveConsoleVisibility(session, true);
    Check(store.Load().Single().Messages.Count == 1 && store.Load().Single().UpdatedAt == generationTimestamp, "Saving panel state cannot replace a newer background transcript or reorder history");
    generation.ConsoleOpen = false; store.Save(generation);
    Check(store.Load().Single().ConsoleOpen, "A stale generation snapshot cannot reset the saved panel visibility");
    Check(JsonSerializer.Deserialize<Settings>("{}")!.EditorAutoSave, "Auto-save has a defined backward-compatible default");
    Check(!JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { EditorAutoSave = false }))!.EditorAutoSave, "Auto-save preference survives restart");
    Check(JsonSerializer.Deserialize<Settings>("{}")!.EditorWordWrap, "Word wrap is enabled for new and legacy settings");
    Check(!JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { EditorWordWrap = false }))!.EditorWordWrap, "Word-wrap preference survives restart");
    Check(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { LastChatId = session.Id }))!.LastChatId == session.Id, "Restore the selected chat so its saved console state is visible on restart");
    Console.WriteLine("PASS: UTF-8/BOM, Shift_JIS, EUC-JP, ISO-2022-JP, UTF-16/32; LF/CRLF/CR; lossless conversion, dirty state, atomic saves, external-change protection; console visibility and auto-save preferences");
}
finally {
    if (!string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup");
    Directory.Delete(root, true);
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task RejectAsync(Func<Task> action)
{
    try { await action(); } catch (IOException) { return; }
    throw new Exception("Expected operation rejection");
}
