using System.Text;

namespace LocalLlm.Gui.Services;

// Detect repeated output, not factual accuracy. Ignore whitespace so blank lines
// between identical sentences do not conceal a generation loop.
internal static class ChatRepetitionGuard
{
    public static bool TrimRepeatedTail(StringBuilder text)
    {
        const int minimum = 24, maximum = 400, copies = 4;
        if (text.Length < minimum * copies) return false;
        var normalized = new StringBuilder();
        var positions = new List<int>();
        int start = Math.Max(0, text.Length - maximum * copies * 3);
        for (int i = start; i < text.Length; i++)
            if (!char.IsWhiteSpace(text[i])) { normalized.Append(text[i]); positions.Add(i); }
        string value = normalized.ToString();
        for (int size = minimum; size <= Math.Min(maximum, value.Length / copies); size++)
        {
            int first = value.Length - size * copies;
            var block = value.AsSpan(first, size);
            if (block.SequenceEqual(value.AsSpan(first + size, size)) &&
                block.SequenceEqual(value.AsSpan(first + size * 2, size)) &&
                block.SequenceEqual(value.AsSpan(first + size * 3, size)))
            {
                text.Length = positions[first + size];
                return true;
            }
        }
        return false;
    }

    public const string Notice = "\n\n[同じ内容の繰り返しを検知したため、生成を中断しました。回答を確認し、必要に応じて再試行してください。]";
}
