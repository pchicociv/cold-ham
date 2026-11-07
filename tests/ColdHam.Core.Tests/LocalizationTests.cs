using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using ColdHam.Core;

internal static class LocalizationTests
{
    public static async Task Persistence()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var store = new JsonConfigurationStore(path);
        Check((await store.LoadAsync()).Language == "es", "New settings must default to Spanish.");
        await File.WriteAllTextAsync(path, """{"Version":3,"Websites":["example.com"]}""");
        var configuration = await store.LoadAsync();
        Check(configuration.Language == "es", "Existing settings without language must use Spanish.");
        configuration.ManualSession = new ManualSession { StartedAtUtc = DateTimeOffset.UtcNow };
        var sessionId = configuration.ManualSession.Id;
        foreach (var language in new[] { "en", "es" })
        {
            configuration.Language = language;
            await store.SaveAsync(configuration);
            configuration = await store.LoadAsync();
            Check(configuration.Language == language, "Language must survive reopening.");
            Check(configuration.Websites.SequenceEqual(["example.com"]) && configuration.ManualSession?.Id == sessionId,
                "Changing language must preserve rules and the active session.");
        }
        foreach (var value in new[] { "null", "\"fr\"", "\"\"" })
        {
            await File.WriteAllTextAsync(path, "{\"Language\":" + value + ",\"Websites\":[\"example.com\"]}");
            configuration = await store.LoadAsync();
            Check(configuration.Language == "es" && configuration.Websites.Count == 1 && store.RecoveryMessage is null,
                "Unsupported language must fall back without resetting valid configuration.");
        }
    }

    public static Task Resources()
    {
        var manager = new ResourceManager("ColdHam.Core.Strings", typeof(LocalizedText).Assembly);
        var spanish = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var english = manager.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, false)
            ?? throw new InvalidOperationException("English satellite resources must be shipped.");
        var spanishKeys = spanish.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet();
        var englishKeys = english!.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet();
        Check(spanishKeys.SetEquals(englishKeys), "Every resource must exist in both languages.");
        foreach (var key in spanishKeys)
        {
            var es = spanish.GetString(key)!;
            var en = english.GetString(key)!;
            Check(!string.IsNullOrWhiteSpace(es) && !string.IsNullOrWhiteSpace(en), $"Empty text: {key}");
            var esFormat = CompositeFormat.Parse(es);
            var enFormat = CompositeFormat.Parse(en);
            Check(esFormat.MinimumArgumentCount == enFormat.MinimumArgumentCount, $"Mismatched arguments: {key}");
        }
        try
        {
            LocalizedText.SetLanguage("es");
            Check(LocalizedText.Get("EndSession") == "Terminar sesión", "Spanish text missing.");
            Check(LocalizedText.Format("ScheduleTimeRange", "Lun", new TimeOnly(9, 0), new TimeOnly(17, 0))
                == "Lun, de 09:00 a 17:00.", "Spanish time format must remain valid.");
            LocalizedText.SetLanguage("EN");
            Check(LocalizedText.Get("EndSession") == "End session", "English text missing.");
            Check(LocalizedText.Format("ScheduleTimeRange", "Mon", new TimeOnly(9, 0), new TimeOnly(17, 0))
                == "Mon, from 09:00 to 17:00.", "English schedule format must remain valid.");
            Check(LocalizedText.Culture.DateTimeFormat.GetDayName(DayOfWeek.Monday) == "Monday", "Dates must follow the selected language.");
            Check(!RuleNormalizer.TryNormalizeDomain("*", out _, out var error) && error.StartsWith("Wildcards", StringComparison.Ordinal),
                "Validation must follow the selected language.");
            LocalizedText.SetLanguage("unsupported");
            Check(LocalizedText.Get("EndSession") == "Terminar sesión", "Fallback must be Spanish regardless of Windows language.");
        }
        finally { LocalizedText.SetLanguage("es"); }
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ColdHam.Tests"));
        public string Path { get; }

        public TestDirectory()
        {
            Path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (!System.IO.Path.GetFullPath(Path).StartsWith(_root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its root.");
            Directory.Delete(Path, true);
        }
    }
}

