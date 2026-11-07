using System.Collections;
using System.Globalization;
using System.Resources;

namespace ColdHam.Core;

/// <summary>Shared UI and diagnostic resources. Spanish is the explicit fallback.</summary>
public static class LocalizedText
{
    private static readonly ResourceManager Resources = new("ColdHam.Core.Strings", typeof(LocalizedText).Assembly);
    private static string _language = "es";

    public static string Language => _language;

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(_language == "en" ? "en-GB" : "es-ES");

    public static string NormalizeLanguage(string? language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "es";

    public static void SetLanguage(string? language) => _language = NormalizeLanguage(language);

    public static IEnumerable<string> Keys => Resources.GetResourceSet(CultureInfo.InvariantCulture, true, true)!
        .Cast<DictionaryEntry>().Select(entry => (string)entry.Key);

    public static string Get(string key) => Resources.GetString(key, Culture)
        ?? throw new MissingManifestResourceException($"Missing text resource: {key}");

    public static string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);
}
