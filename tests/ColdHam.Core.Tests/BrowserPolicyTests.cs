using ColdHam.Core;
using ColdHam.Windows;

internal static class BrowserPolicyTests
{
    public static Task Patterns()
    {
        var patterns = BrowserPolicyPlan.BuildAllowlist(["https://EXAMPLE.com/path", "example.com"]);
        Check(patterns.Contains("http://.example.com") && patterns.Contains("https://.www.example.com"), "Allow HTTP and HTTPS for the exact domain and www.");
        Check(patterns.Length == 8 && !patterns.Contains("*"), "Duplicates must not broaden allow rules.");
        Check(patterns.Contains("devtools://*"), "DevTools must remain accessible when a website is allowed.");
        Check(BrowserPolicyPlan.BuildAllowlist([]).SequenceEqual(new[] { "chrome://*", "edge://*", "chrome-untrusted://*", "devtools://*" }), "Empty list must preserve only internal browser pages and DevTools, without allowing websites.");
        Check(BrowserPolicyPlan.BuildAllowlist(Enumerable.Range(1, 249).Select(i => $"site{i}.com")).Length == BrowserPolicyPlan.MaximumEntries, "Policy limit must include the DevTools exception after domain expansion.");
        Expect<InvalidDataException>(() => BrowserPolicyPlan.BuildAllowlist(Enumerable.Range(1, 250).Select(i => $"site{i}.com")));
        return Task.CompletedTask;
    }

    public static async Task Lifecycle()
    {
        using var fixture = new Fixture();
        await fixture.Manager.ApplyAsync(["example.com"]);
        foreach (var path in BrowserPolicyManager.PolicyPaths)
        {
            var values = fixture.Registry.Read(path);
            Check(path.EndsWith("URLBlocklist", StringComparison.Ordinal)
                ? Equals(values["1"], "*") : values.Values.Contains("https://.example.com"), "Both browsers must receive deny-all with the allowed sites.");
        }
        await fixture.Manager.ApplyAsync(["other.com"]);
        Check(!fixture.Registry.Values.Values.SelectMany(values => values.Values).Contains("https://.example.com"), "Removed exceptions must not survive updates.");
        await fixture.Manager.ApplyAsync([]);
        Check(fixture.Registry.Values.Values.SelectMany(values => values.Values).Count(value => Equals(value, "*")) == 2, "Empty allowlist must still block all sites in both browsers.");
        foreach (var path in BrowserPolicyManager.PolicyPaths.Where(path => path.EndsWith("URLAllowlist", StringComparison.Ordinal)))
            Check(fixture.Registry.Read(path).Values.Contains("devtools://*"), "Both browsers must retain DevTools access after allowlist updates, even with no allowed websites.");
        // Recreate the manager to model process restart with persisted recovery data.
        await new BrowserPolicyManager(fixture.Registry, fixture.Journal).RemoveAsync();
        Check(fixture.Registry.Values.Values.All(values => values.Count == 0) && !File.Exists(fixture.Journal), "Stop after restart must remove every owned value and its journal.");
        await fixture.Manager.RemoveAsync();
    }

    public static async Task SubdomainPolicies()
    {
        var patterns = BrowserPolicyPlan.BuildAllowlist(["*.EXAMPLE.com", "*.example.com", "other.com"]);
        Check(patterns.Length == 10 && patterns.Contains("https://example.com") && patterns.Contains("http://example.com")
            && patterns.Contains("https://.other.com") && patterns.Contains("https://.www.other.com")
            && !patterns.Contains("https://.example.com") && !patterns.Any(value => value.Contains("*.example.com", StringComparison.Ordinal)),
            "Subdomain opt-in must use Chromium's host matching while retaining exact rules and deduplicating entries.");
        Check(BrowserPolicyPlan.BuildAllowlist(Enumerable.Range(1, 498).Select(i => $"*.site{i}.com")).Length == BrowserPolicyPlan.MaximumEntries,
            "Subdomain trees must count as two patterns each, including all internal exceptions.");
        Expect<InvalidDataException>(() => BrowserPolicyPlan.BuildAllowlist(Enumerable.Range(1, 499).Select(i => $"*.site{i}.com")));
        using var fixture = new Fixture();
        await fixture.Manager.ApplyAsync(["example.com"]);
        await fixture.Manager.ApplyAsync(["*.example.com"]);
        foreach (var path in BrowserPolicyManager.PolicyPaths.Where(path => path.EndsWith("URLAllowlist", StringComparison.Ordinal)))
            Check(fixture.Registry.Read(path).Values.Contains("https://example.com")
                && !fixture.Registry.Read(path).Values.Contains("https://.example.com"), "Both browsers must replace exact rules with subdomain rules.");
        await fixture.Manager.ApplyAsync(["example.com"]);
        foreach (var path in BrowserPolicyManager.PolicyPaths.Where(path => path.EndsWith("URLAllowlist", StringComparison.Ordinal)))
            Check(!fixture.Registry.Read(path).Values.Contains("https://example.com")
                && fixture.Registry.Read(path).Values.Contains("https://.example.com"), "Changing back must remove the broader permission.");
        await fixture.Manager.ApplyAsync(["*.example.com"]);
        await new BrowserPolicyManager(fixture.Registry, fixture.Journal).RemoveAsync();
        Check(fixture.Registry.Values.Values.All(values => values.Count == 0) && !File.Exists(fixture.Journal),
            "Subdomain policies must be removed on Stop after restart.");
    }

    public static async Task ExistingPolicies()
    {
        using var fixture = new Fixture();
        var path = BrowserPolicyManager.PolicyPaths[1];
        fixture.Registry.Values[path] = new() { ["7"] = "existing.example" };
        await ExpectAsync<InvalidOperationException>(() => fixture.Manager.ApplyAsync(["example.com"]));
        Check(fixture.Registry.Writes == 0 && Equals(fixture.Registry.Read(path)["7"], "existing.example"), "Existing policies must be detected before any write.");
        fixture.Registry.Values.Clear();
        fixture.Registry.MachinePolicy = true;
        await ExpectAsync<InvalidOperationException>(() => fixture.Manager.ApplyAsync(["example.com"]));
        Check(fixture.Registry.Writes == 0 && !File.Exists(fixture.Journal), "Machine policies must not be shadowed.");
    }

    public static async Task Failures()
    {
        using var fixture = new Fixture();
        fixture.Registry.FailAtWrite = 3;
        await ExpectAsync<IOException>(() => fixture.Manager.ApplyAsync(["example.com"]));
        Check(fixture.Registry.Values.Values.All(values => values.Count == 0), "Partial apply must roll back written entries.");
        fixture.Registry.FailAtWrite = null;
        await fixture.Manager.ApplyAsync(["example.com"]);
        fixture.Registry.FailDelete = true;
        await ExpectAsync<UnauthorizedAccessException>(() => fixture.Manager.RemoveAsync());
        Check(File.Exists(fixture.Journal), "Failed removal must retain recovery ownership.");
        fixture.Registry.FailDelete = false;
        await new BrowserPolicyManager(fixture.Registry, fixture.Journal).RemoveAsync();
        Check(fixture.Registry.Values.Values.All(values => values.Count == 0), "Recovery must succeed when access is restored.");
    }

    public static async Task ExternalChanges()
    {
        using var fixture = new Fixture();
        await fixture.Manager.ApplyAsync(["example.com"]);
        var path = BrowserPolicyManager.PolicyPaths[0];
        fixture.Registry.Values[path]["1"] = "external.example";
        fixture.Registry.Values[path]["999"] = "other.example";
        await ExpectAsync<InvalidOperationException>(() => fixture.Manager.RemoveAsync());
        Check(Equals(fixture.Registry.Values[path]["1"], "external.example") && Equals(fixture.Registry.Values[path]["999"], "other.example"), "Changed and newly added third-party values must survive removal.");
        Check(File.Exists(fixture.Journal), "Unresolved ownership must remain visible to recovery.");
        fixture.Registry.Values[path].Remove("1");
        await fixture.Manager.RemoveAsync();
        Check(fixture.Registry.Values[path].Count == 1, "Retry must preserve unrelated values after the conflict is resolved.");
    }

    public static async Task InvalidJournal()
    {
        using var fixture = new Fixture();
        foreach (var json in new[] { "{broken", "null", "{\"SOFTWARE\\\\Unrelated\":{\"1\":\"data\"}}" })
        {
            await File.WriteAllTextAsync(fixture.Journal, json);
            await ExpectAsync<InvalidDataException>(() => fixture.Manager.RemoveAsync());
            Check(fixture.Registry.Writes == 0 && await File.ReadAllTextAsync(fixture.Journal) == json, "An invalid journal must not cause arbitrary registry edits or be discarded.");
        }
    }

    public static async Task ModePersistence()
    {
        using var fixture = new Fixture();
        var store = new JsonConfigurationStore(Path.Combine(Path.GetDirectoryName(fixture.Journal)!, "settings.json"));
        var configuration = new BlockConfiguration { WebsiteMode = WebsiteBlockingMode.AllowListedSites, AllowedWebsites = ["example.com"] };
        await store.SaveAsync(configuration);
        Check((await store.LoadAsync()).WebsiteMode == WebsiteBlockingMode.AllowListedSites, "Restart must preserve the selected mode.");
        configuration.WebsiteMode = (WebsiteBlockingMode)99;
        await ExpectAsync<InvalidDataException>(() => store.SaveAsync(configuration));
        configuration.WebsiteMode = WebsiteBlockingMode.AllowListedSites;
        configuration.AllowedWebsites = Enumerable.Range(1, 250).Select(i => $"site{i}.com").ToList();
        await ExpectAsync<InvalidDataException>(() => store.SaveAsync(configuration));
        await File.WriteAllTextAsync(store.FilePath, "{\"Version\":2}");
        Check((await store.LoadAsync()).WebsiteMode == WebsiteBlockingMode.BlockListedSites, "Migration must never opt users into deny-all.");
    }

    public static async Task Inspection()
    {
        using var fixture = new Fixture();
        var inactive = await fixture.Manager.InspectAsync(false, []);
        Check(inactive.Entries.All(entry => entry.MatchesExpected && entry.ValueCount == 0)
            && !inactive.RecoveryJournalPresent && fixture.Registry.Writes == 0, "An inspection must not create policies or journals.");
        await fixture.Manager.ApplyAsync(["*.example.com"]);
        var journal = await File.ReadAllBytesAsync(fixture.Journal);
        var writes = fixture.Registry.Writes;
        var applied = await fixture.Manager.InspectAsync(true, ["*.example.com"]);
        Check(applied.RecoveryJournalPresent && applied.Entries.All(entry => entry.MatchesExpected && entry.ForeignValues == 0), "Matching owned policies must be identified.");
        Check((await fixture.Manager.InspectAsync(true, ["other.com"])).Entries.Any(entry => !entry.MatchesExpected), "Changed configuration must not be reported as applied.");
        Check((await fixture.Manager.InspectAsync(false, [])).Entries.All(entry => !entry.MatchesExpected), "Stopped sessions with remaining values need cleanup.");
        fixture.Registry.Values[BrowserPolicyManager.PolicyPaths[1]]["1"] = "external.com";
        fixture.Registry.MachinePolicy = true;
        var conflict = await fixture.Manager.InspectAsync(true, ["*.example.com"]);
        Check(conflict.Entries[1].ForeignValues == 1 && conflict.Entries.All(entry => entry.MachinePolicy), "Changed values and machine policies must be surfaced.");
        Check((await File.ReadAllBytesAsync(fixture.Journal)).SequenceEqual(journal) && fixture.Registry.Writes == writes
            && Equals(fixture.Registry.Values[BrowserPolicyManager.PolicyPaths[1]]["1"], "external.com"), "Inspection must preserve registry values and ownership journal.");
        await File.WriteAllTextAsync(fixture.Journal, "{broken");
        await ExpectAsync<InvalidDataException>(() => fixture.Manager.InspectAsync(false, []));
        Check(await File.ReadAllTextAsync(fixture.Journal) == "{broken", "Corrupt recovery data must remain intact during diagnosis.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ColdHam.Tests"));
        public string Journal { get; }
        public FakeRegistry Registry { get; } = new();
        public BrowserPolicyManager Manager { get; }

        public Fixture()
        {
            var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Journal = Path.Combine(directory, "journal.json");
            Manager = new BrowserPolicyManager(Registry, Journal);
        }

        public void Dispose()
        {
            var directory = Path.GetFullPath(Path.GetDirectoryName(Journal)!);
            if (!directory.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its root.");
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeRegistry : IBrowserPolicyRegistry
    {
        public Dictionary<string, Dictionary<string, object?>> Values { get; } = [];
        public bool MachinePolicy { get; set; }
        public int Writes { get; private set; }
        public int? FailAtWrite { get; set; }
        public bool FailDelete { get; set; }
        public IReadOnlyDictionary<string, object?> Read(string path) => Values.GetValueOrDefault(path) ?? [];
        public bool HasMachinePolicy(string path) => MachinePolicy;

        public void Write(string path, string name, string value)
        {
            Writes++;
            if (Writes == FailAtWrite) throw new IOException("Simulated write failure.");
            if (!Values.TryGetValue(path, out var values)) Values[path] = values = [];
            values.Add(name, value);
        }

        public void Delete(string path, string name, string expected)
        {
            if (FailDelete) throw new UnauthorizedAccessException("Simulated removal failure.");
            if (Values.TryGetValue(path, out var values) && values.TryGetValue(name, out var actual) && Equals(actual, expected)) values.Remove(name);
        }
    }
}
