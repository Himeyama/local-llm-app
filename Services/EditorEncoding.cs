using System.Text;
using UtfUnknown;

namespace LocalLlm.Gui.Services;

public sealed record EditorEncoding(int CodePage, bool EmitBom = false)
{
    static EditorEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public Encoding StrictEncoding => Encoding.GetEncoding(CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    public string Label => CodePage switch {
        65001 => EmitBom ? "UTF-8 (BOM)" : "UTF-8",
        932 => "Shift_JIS (CP932)", 51932 => "EUC-JP", 50220 or 50221 or 50222 => "ISO-2022-JP",
        1200 => "UTF-16 LE" + (EmitBom ? " (BOM)" : ""), 1201 => "UTF-16 BE" + (EmitBom ? " (BOM)" : ""),
        12000 => "UTF-32 LE" + (EmitBom ? " (BOM)" : ""), 12001 => "UTF-32 BE" + (EmitBom ? " (BOM)" : ""),
        _ => StrictEncoding.WebName
    };
    public static IReadOnlyList<EditorEncoding> Common { get; } = new[] {
        new EditorEncoding(65001), new EditorEncoding(65001, true), new EditorEncoding(932), new EditorEncoding(51932), new EditorEncoding(50220),
        new EditorEncoding(1200, true), new EditorEncoding(1201, true), new EditorEncoding(12000, true), new EditorEncoding(12001, true),
        new EditorEncoding(1252), new EditorEncoding(20127), new EditorEncoding(28591), new EditorEncoding(54936), new EditorEncoding(950), new EditorEncoding(949)
    };

    public static EditorEncoding Parse(string name)
    {
        var encoding = int.TryParse(name.Trim(), out var codePage) ? Encoding.GetEncoding(codePage) : Encoding.GetEncoding(name.Trim());
        return new EditorEncoding(encoding.CodePage, encoding.CodePage is 1200 or 1201 or 12000 or 12001);
    }

    public byte[] Encode(string text)
    {
        var body = StrictEncoding.GetBytes(text);
        if (!EmitBom) return body;
        byte[] prefix = CodePage switch {
            65001 => new byte[] { 0xEF, 0xBB, 0xBF }, 1200 => new byte[] { 0xFF, 0xFE }, 1201 => new byte[] { 0xFE, 0xFF },
            12000 => new byte[] { 0xFF, 0xFE, 0, 0 }, 12001 => new byte[] { 0, 0, 0xFE, 0xFF }, _ => Array.Empty<byte>()
        };
        return prefix.Concat(body).ToArray();
    }

    internal static (EditorEncoding Encoding, int Offset) Detect(byte[] bytes, EditorEncoding? forced = null)
    {
        (EditorEncoding Encoding, int Offset)? bom = bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }) ? (new(12001, true), 4) :
            bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) ? (new(12000, true), 4) :
            bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? (new(65001, true), 3) :
            bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) ? (new(1200, true), 2) :
            bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }) ? (new(1201, true), 2) : null;
        if (forced != null) return (forced with { EmitBom = bom?.Encoding.CodePage == forced.CodePage }, bom?.Encoding.CodePage == forced.CodePage ? bom.Value.Offset : 0);
        if (bom != null) return bom.Value;
        if (bytes.Length == 0) return (new(65001), 0);
        // ISO-2022-JP is 7-bit and must be recognized before the UTF-8/ASCII fast path.
        if (bytes.AsSpan().IndexOf(new byte[] { 0x1B, 0x24 }) >= 0 || bytes.AsSpan().IndexOf(new byte[] { 0x1B, 0x28, 0x4A }) >= 0)
            return (new(50220), 0);
        if (bytes.Length >= 4 && bytes.Length % 2 == 0) {
            var sample = Math.Min(bytes.Length, 4096);
            var zeros = new int[4]; var totals = new int[4];
            for (int index = 0; index < sample; index++) { totals[index % 4]++; if (bytes[index] == 0) zeros[index % 4]++; }
            double Ratio(int index) => (double)zeros[index] / Math.Max(1, totals[index]);
            if (bytes.Length % 4 == 0 && Ratio(2) > 0.8 && Ratio(3) > 0.8 && Ratio(0) < 0.5) return (new(12000), 0);
            if (bytes.Length % 4 == 0 && Ratio(0) > 0.8 && Ratio(1) > 0.8 && Ratio(3) < 0.5) return (new(12001), 0);
            if ((Ratio(1) + Ratio(3)) / 2 > 0.4 && (Ratio(0) + Ratio(2)) / 2 < 0.1) return (new(1200), 0);
            if ((Ratio(0) + Ratio(2)) / 2 > 0.4 && (Ratio(1) + Ratio(3)) / 2 < 0.1) return (new(1201), 0);
        }
        try { _ = new UTF8Encoding(false, true).GetString(bytes); return (new(65001), 0); }
        catch (DecoderFallbackException) { }
        var detected = CharsetDetector.DetectFromBytes(bytes).Detected;
        if (detected?.Encoding != null) {
            var candidate = new EditorEncoding(detected.Encoding.CodePage);
            try { _ = candidate.StrictEncoding.GetString(bytes); return (candidate, 0); }
            catch (DecoderFallbackException) { }
        }
        throw new IOException("文字コードを判定できませんでした。別の文字コードを指定して開いてください。");
    }
}
