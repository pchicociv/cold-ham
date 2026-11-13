using System.Text;
using ColdHam.Core;
using ColdHam.Windows;

internal static class TroubleshootingTests
{
    public static Task Editing()
    {
        string[] original = ["example.com", "other.com"];
        var broadened = RuleEditing.Replace(original, 0, "https://EXAMPLE.com/path", RuleKind.AllowedWebsite, true);
        Check(original[0] == "example.com" && broadened.SequenceEqual(["*.example.com", "other.com"]), "Edit must create a replacement without changing the original.");
        var narrowed = RuleEditing.Replace(broadened, 0, "example.com", RuleKind.AllowedWebsite);
        Check(narrowed.SequenceEqual(original), "Removing the checkbox must narrow the rule again.");
        ExpectInvalid(() => RuleEditing.Replace(original, 0, "OTHER.COM", RuleKind.AllowedWebsite));
        ExpectInvalid(() => RuleEditing.Replace(original, 0, "*.example.com", RuleKind.BlockedWebsite));
        ExpectInvalid(() => RuleEditing.Replace(["game.exe"], 0, "ColdHam.App.exe", RuleKind.Application));
        ExpectInvalid(() => RuleEditing.Replace(["game.exe"], 0, "svchost.exe", RuleKind.Application));
        Check(RuleEditing.Replace(["game.exe"], 0, "othergame", RuleKind.Application)[0] == "othergame.exe", "Application edits must use the executable normalizer.");
        var full = Enumerable.Range(1, 498).Select(i => $"*.site{i}.com").ToArray();
        ExpectInvalid(() => RuleEditing.Replace(full, 0, "site1.com", RuleKind.AllowedWebsite));
        Check(full[0] == "*.site1.com", "A policy-limit error must leave existing rules intact.");
        ExpectInvalid(() => RuleEditing.AddAllowed(full, ["new.com"]));
        Check(RuleEditing.AddAllowed(original, ["https://EXAMPLE.com/path", "files.example.com"]).SequenceEqual(["example.com", "other.com", "files.example.com"]),
            "Batch additions must normalize and deduplicate without altering existing scope.");
        return Task.CompletedTask;
    }

    public static Task Resources()
    {
        var analysis = WebResourceAnalysis.FromUrls("https://user:secret@EXAMPLE.com/path?token=secret\nhttps://example.com/other\nhttps://bücher.de/a\nblob:https://other.com/id\ndata:text/plain,secret\nfile:///secret\nhttps://127.0.0.1/x\nnot-a-url");
        Check(analysis.Domains.Length == 2 && analysis.Ignored == 5, "Only HTTP(S) domain URLs may become candidates.");
        Check(analysis.Domains[0].Domain == "example.com" && analysis.Domains[0].Requests == 2
            && analysis.Domains[1].Domain == "xn--bcher-kva.de", "Candidates must be normalized and deduplicated.");
        Check(!System.Text.Json.JsonSerializer.Serialize(analysis).Contains("secret", StringComparison.Ordinal), "Results must never retain paths, credentials or queries.");
        var har = """
            {"log":{"entries":[
              {"request":{"url":"https://ok.example.com/a"},"response":{"status":200}},
              {"request":{"url":"https://cdn.example.com/a?token=secret","headers":[{"name":"Authorization","value":"secret"}]},"response":{"status":0}},
              {"request":{"url":"https://cdn.example.com/b"},"response":{"status":403}},
              {"request":{"url":"https://other.com/x"},"_error":"net::ERR_BLOCKED_BY_ADMINISTRATOR"},
              {"request":{"url":"blob:https://other.com/id"},"response":{"status":0}},
              {"request":null,"response":{"status":500}},
              {"request":{"url":"https://redirect.com"},"response":{"status":302}},
              {"request":{"url":"https://unknown.com"},"response":{"status":"bad"}},
              null
            ]}}
            """;
        analysis = WebResourceAnalysis.FromHar(Encoding.UTF8.GetBytes(har));
        Check(analysis.Domains.Length == 2 && analysis.Ignored == 6, "HAR analysis must skip successes, redirects, malformed entries and non-web URLs.");
        Check(analysis.Domains[0] is { Domain: "cdn.example.com", Requests: 2, HasNetworkFailure: true, HasHttpError: true }, "Failure categories must be grouped without pretending to know the cause.");
        Check(!System.Text.Json.JsonSerializer.Serialize(analysis).Contains("secret", StringComparison.Ordinal), "HAR headers and tokens must not be retained.");
        Check(WebResourceAnalysis.FromHar(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(har)).ToArray()).Domains.Length == 2, "UTF-8 BOM exports must be accepted.");
        foreach (var bad in new[] { "{", "null", "[]", "{}", "{\"log\":{\"entries\":{}}}" })
            ExpectInvalid(() => WebResourceAnalysis.FromHar(Encoding.UTF8.GetBytes(bad)));
        ExpectInvalid(() => WebResourceAnalysis.FromHar(new byte[WebResourceAnalysis.MaximumBytes + 1]));
        ExpectInvalid(() => WebResourceAnalysis.FromUrls(string.Join('\n', Enumerable.Repeat("https://example.com", WebResourceAnalysis.MaximumEntries + 1))));
        ExpectInvalid(() => WebResourceAnalysis.FromHar(Encoding.UTF8.GetBytes("{\"log\":{\"entries\":[" + string.Join(',', Enumerable.Repeat("null", WebResourceAnalysis.MaximumEntries + 1)) + "]}}")));
        return Task.CompletedTask;
    }

    public static async Task PersistenceAndHosts()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ColdHam.Tests"));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonConfigurationStore(Path.Combine(directory, "settings.json"));
            var settings = new BlockConfiguration { AllowedWebsites = ["example.com"], WebsiteMode = WebsiteBlockingMode.AllowListedSites,
                Language = "en", ManualSession = BlockStateEvaluator.CreateUnlimitedSession(TimeProvider.System) };
            var sessionId = settings.ManualSession.Id;
            settings.Schedule.Enabled = true;
            settings.Schedule.Start = new TimeOnly(11, 15);
            await store.SaveAsync(settings);
            settings.AllowedWebsites = RuleEditing.Replace(settings.AllowedWebsites, 0, "example.com", RuleKind.AllowedWebsite, true).ToList();
            await store.SaveAsync(settings);
            settings = await store.LoadAsync();
            Check(settings.AllowedWebsites.SequenceEqual(["*.example.com"]) && settings.ManualSession?.Id == sessionId
                && settings.Schedule.Enabled && settings.Schedule.Start == new TimeOnly(11, 15) && settings.Language == "en", "Editing must persist without changing session, schedule or language.");
            var path = Path.Combine(directory, "hosts");
            var content = "# external\r\n127.0.0.1 localhost\r\n0.0.0.0 external.com\r\n";
            await File.WriteAllTextAsync(path, content);
            var hosts = new HostsFileManager(path);
            var before = await File.ReadAllBytesAsync(path);
            var inspection = await hosts.InspectAsync([]);
            Check(inspection is { ManagedSectionPresent: false, MatchesExpected: true, ExternalMappingLines: 2 }, "Inactive diagnostics must report external mappings without treating them as owned.");
            Check(!(await hosts.InspectAsync(["example.com"])).MatchesExpected, "Missing expected hosts section must be reported.");
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(before) && !File.Exists(path + ".coldham-recovery"), "Inspection must never modify hosts or create recovery files.");
            await hosts.ApplyAsync(["example.com"]);
            before = await File.ReadAllBytesAsync(path);
            Check((await hosts.InspectAsync(["example.com"])).MatchesExpected && !(await hosts.InspectAsync([])).MatchesExpected, "Diagnostics must distinguish applied and stale blocks.");
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(before), "Inspecting an active block must not modify it.");
            await File.WriteAllTextAsync(path, HostsFileManager.BeginMarker);
            try { await hosts.InspectAsync([]); throw new Exception("Expected invalid markers"); }
            catch (InvalidDataException) { }
            Check(await File.ReadAllTextAsync(path) == HostsFileManager.BeginMarker, "Malformed hosts inspection must preserve content.");
        }
        finally
        {
            if (!Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup");
            Directory.Delete(directory, true);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected invalid input to be rejected.");
    }
}
