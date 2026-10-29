using System.Security.Cryptography;

namespace LocalLlm.Gui.Services;

public enum EditorLineEnding { LF, CRLF, CR }

public sealed class EditorDocument
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private string savedText;
    private EditorEncoding savedEncoding;
    private EditorLineEnding savedLineEnding;
    private byte[] savedHash;
    private EditorLineEnding lineEnding;
    private bool normalizeLineEndings;
    public string Path { get; private set; }
    public string Text { get; set; }
    public EditorEncoding Encoding { get; private set; }
    public EditorLineEnding LineEnding
    {
        get => lineEnding;
        set { lineEnding = value; if (MixedLineEndings) normalizeLineEndings = true; }
    }
    public bool MixedLineEndings { get; private set; }
    public bool IsDirty => normalizeLineEndings || Text != savedText || Encoding != savedEncoding || LineEnding != savedLineEnding;
    public string LineEndingLabel => LineEnding + (MixedLineEndings ? " (混在)" : "");

    private EditorDocument(string path, string text, EditorEncoding encoding, EditorLineEnding lineEnding, bool mixed, byte[] hash)
    {
        Path = path; Text = savedText = text; Encoding = savedEncoding = encoding;
        LineEnding = savedLineEnding = lineEnding; MixedLineEndings = mixed; savedHash = hash;
    }

    public static async Task<EditorDocument> OpenAsync(string path, EditorEncoding? forcedEncoding = null)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var bytes = await ReadBoundedAsync(fullPath).ConfigureAwait(false);
        if (bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) || bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) ||
            bytes.AsSpan().StartsWith("GIF8"u8) || bytes.AsSpan().StartsWith("PK\x03\x04"u8) || bytes.AsSpan().StartsWith("GGUF"u8) || bytes.AsSpan().StartsWith("%PDF"u8) ||
            bytes.AsSpan().StartsWith("MZ"u8) || bytes.AsSpan().StartsWith(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }))
            throw new IOException("このファイルはバイナリ形式のためテキストエディタで開けません。");
        var format = EditorEncoding.Detect(bytes, forcedEncoding);
        var raw = format.Encoding.StrictEncoding.GetString(bytes, format.Offset, bytes.Length - format.Offset);
        if (raw.Contains('\0') || raw.Count(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t' or '\f')) > Math.Max(3, raw.Length / 100))
            throw new IOException("このファイルはバイナリ形式のためテキストエディタで開けません。");
        var counts = new int[3]; EditorLineEnding? first = null;
        for (int index = 0; index < raw.Length; index++) {
            EditorLineEnding? kind = raw[index] == '\n' ? EditorLineEnding.LF : null;
            if (raw[index] == '\r') {
                kind = index + 1 < raw.Length && raw[index + 1] == '\n' ? EditorLineEnding.CRLF : EditorLineEnding.CR;
                if (kind == EditorLineEnding.CRLF) index++;
            }
            if (kind != null) { first ??= kind; counts[(int)kind.Value]++; }
        }
        var lineEnding = first ?? EditorLineEnding.LF;
        foreach (var kind in Enum.GetValues<EditorLineEnding>()) if (counts[(int)kind] > counts[(int)lineEnding]) lineEnding = kind;
        return new EditorDocument(fullPath, Normalize(raw), format.Encoding, lineEnding, counts.Count(count => count > 0) > 1, SHA256.HashData(bytes));
    }

    public void SetEncoding(EditorEncoding encoding)
    {
        _ = encoding.Encode(Text); // Reject unrepresentable characters instead of silently writing '?'.
        Encoding = encoding;
    }

    public void RemapPath(string source, string destination)
    {
        if (Path.Equals(source, StringComparison.OrdinalIgnoreCase)) Path = destination;
        else if (Path.StartsWith(source.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            Path = System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(source, Path));
    }

    public async Task SaveAsync()
    {
        await saveGate.WaitAsync().ConfigureAwait(false);
        try {
            if (!IsDirty) return;
            var text = Text; var encoding = Encoding; var ending = LineEnding;
            var bytes = encoding.Encode(text.Replace("\n", ending switch { EditorLineEnding.CRLF => "\r\n", EditorLineEnding.CR => "\r", _ => "\n" }));
            if (bytes.Length > MaximumBytes) throw new IOException("編集できるファイルサイズの上限は 8 MiB です。");
            var target = Path;
            var current = await ReadBoundedAsync(target).ConfigureAwait(false);
            if (!SHA256.HashData(current).AsSpan().SequenceEqual(savedHash)) throw new IOException("外部でファイルが変更されています。内容を確認し、読み直してから保存してください。編集内容はタブに保持しています。");
            var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, ".local-llm-editor-" + Guid.NewGuid().ToString("N"));
            try {
                await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                var latest = await ReadBoundedAsync(target).ConfigureAwait(false);
                if (!SHA256.HashData(latest).AsSpan().SequenceEqual(savedHash)) throw new IOException("保存中に外部でファイルが変更されました。編集内容はタブに保持しています。");
                File.Replace(temporary, target, destinationBackupFileName: null);
                savedText = text; savedEncoding = encoding; savedLineEnding = ending;
                savedHash = SHA256.HashData(bytes); MixedLineEndings = false; normalizeLineEndings = false;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { saveGate.Release(); }
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static async Task<byte[]> ReadBoundedAsync(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, true);
        if (input.Length > MaximumBytes) throw new IOException("編集できるファイルサイズの上限は 8 MiB です。");
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0) {
            if (output.Length + count > MaximumBytes) throw new IOException("編集できるファイルサイズの上限は 8 MiB です。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
