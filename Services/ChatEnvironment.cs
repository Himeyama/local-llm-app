using System.Globalization;

namespace LocalLlm.Gui.Services;

internal static class ChatEnvironment
{
    public static string DisplayLanguage()
    {
        var culture = CultureInfo.CurrentUICulture;
        return culture.Equals(CultureInfo.InvariantCulture)
            ? "OS display language: unknown."
            : $"OS display language: {culture.EnglishName} ({culture.Name}).";
    }
}
