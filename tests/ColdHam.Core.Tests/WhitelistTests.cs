using ColdHam.Core;
using ColdHam.Windows;

internal static class WhitelistTests
{
    public static Task Priority()
    {
        var actual = WebsiteRules.GetBlockedDomains(
            ["example.com", "WWW.EXAMPLE.COM", "other.com"],
            ["https://EXAMPLE.com/path", "example.com"]);
        Check(actual.SequenceEqual(new[] { "other.com", "www.other.com" }), "Allowed domains and generated www variants must win over explicit block rules.");
        Check(WebsiteRules.GetBlockedDomains([], ["example.com"]).Length == 0, "An exception alone must not block anything.");
        Check(WebsiteRules.GetBlockedDomains(["xn--bcher-kva.de"], ["https://bücher.de/"]).Length == 0, "IDNs must match their ASCII form.");
        return Task.CompletedTask;
    }

    public static Task ExactDomains()
    {
        var rules = WebsiteRules.GetBlockedDomains(["example.com", "news.example.com", "notexample.com"], ["www.example.com"]);
        Check(rules.Contains("example.com") && !rules.Contains("www.example.com"), "Allowing only www must not allow the bare domain.");
        rules = WebsiteRules.GetBlockedDomains(["example.com", "news.example.com", "notexample.com"], ["example.com"]);
        Check(rules.SequenceEqual(new[] { "news.example.com", "notexample.com", "www.news.example.com", "www.notexample.com" }), "Exceptions must not use suffix matching or implicitly allow other subdomains.");
        return Task.CompletedTask;
    }

    public static Task InvalidRules()
    {
        foreach (var invalid in new[] { "", "*", "*example.com", "foo.*.com", "*.*.example.com", "*.com", "*.127.0.0.1", "*.https://example.com", "*.example.com/path", "127.0.0.1", "example.com\n0.0.0.0 other.com" })
        {
            ExpectInvalid(() => WebsiteRules.GetBlockedDomains(["example.com"], [invalid]));
            ExpectInvalid(() => HostsFileManager.BuildContent("127.0.0.1 localhost\n", [], [invalid]));
        }
        return Task.CompletedTask;
    }

    public static Task SubdomainRules()
    {
        Check(RuleNormalizer.TryNormalizeAllowedDomain("  *.BÜCHER.de.  ", out var normalized, out _)
            && normalized == "*.xn--bcher-kva.de", "Wildcard rules must normalize IDNs, case and trailing dots.");
        Check(RuleNormalizer.TryNormalizeAllowedDomain("https://EXAMPLE.com/path", out normalized, out _, includeSubdomains: true)
            && normalized == "*.example.com", "The checkbox must accept the same URLs as exact rules.");
        Check(RuleNormalizer.TryNormalizeAllowedDomain("*.EXAMPLE.com", out normalized, out _, includeSubdomains: true)
            && normalized == "*.example.com", "Using both the checkbox and wildcard must not duplicate the prefix.");
        Check(!RuleNormalizer.TryNormalizeDomain("*.example.com", out _, out _), "Wildcard block rules must remain unsupported.");
        var rules = WebsiteRules.GetBlockedDomains(
            ["example.com", "a.b.example.com", "notexample.com", "example.com.other.net"], ["*.EXAMPLE.com"]);
        Check(rules.SequenceEqual(new[] { "example.com.other.net", "notexample.com", "www.example.com.other.net", "www.notexample.com" }),
            "Subdomain exceptions must include the root and nested subdomains, but never similarly named domains.");
        rules = WebsiteRules.GetBlockedDomains(["example.com", "files.example.com", "deep.files.example.com", "other.example.com"], ["*.files.example.com"]);
        Check(rules.SequenceEqual(new[] { "example.com", "other.example.com", "www.example.com", "www.other.example.com" }),
            "An allowed subdomain tree must not allow its parent or siblings.");
        Check(WebsiteRules.GetBlockedDomains(["cdn.xn--bcher-kva.de"], ["*.bücher.de"]).Length == 0,
            "Internationalized subdomains must match their normalized parent.");
        return Task.CompletedTask;
    }

    public static async Task SubdomainHosts()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "hosts");
        var original = System.Text.Encoding.UTF8.GetBytes("# unrelated\r\n0.0.0.0 files.example.com\r\n127.0.0.1 localhost\r\n");
        await File.WriteAllBytesAsync(path, original);
        var hosts = new HostsFileManager(path);
        string[] blocked = ["example.com", "files.example.com", "deep.files.example.com", "other.com"];
        await hosts.ApplyAsync(blocked);
        await hosts.ApplyAsync(blocked, ["*.example.com"]);
        var content = await File.ReadAllBytesAsync(path);
        Check(content.AsSpan(0, original.Length).SequenceEqual(original), "Wildcard exceptions must preserve unrelated hosts content byte for byte.");
        var owned = System.Text.Encoding.UTF8.GetString(content[original.Length..]);
        Check(!owned.Contains("example.com", StringComparison.Ordinal) && owned.Contains("0.0.0.0 other.com", StringComparison.Ordinal),
            "Wildcard exceptions must remove every matching owned entry and keep other blocks.");
        await hosts.ApplyAsync(blocked, []);
        Check((await File.ReadAllTextAsync(path)).Contains("::1 deep.files.example.com", StringComparison.Ordinal),
            "Removing the exception must restore matching blocks.");
        await hosts.RemoveAsync();
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(original), "Stop must restore the original hosts file exactly.");
    }

    public static async Task SubdomainPersistence()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new JsonConfigurationStore(path);
        await File.WriteAllTextAsync(path, """{"Version":3,"AllowedWebsites":["example.com"],"WebsiteMode":1,"Language":"en"}""");
        var configuration = await store.LoadAsync();
        Check(configuration.Version == 4 && configuration.AllowedWebsites.SequenceEqual(["example.com"])
            && configuration.WebsiteMode == WebsiteBlockingMode.AllowListedSites && configuration.Language == "en"
            && store.RecoveryMessage is null, "Version 3 must migrate without changing scope, mode or language.");
        Check(BrowserPolicyPlan.BuildAllowlist(configuration.AllowedWebsites).Contains("https://.example.com"),
            "Migration must never opt existing domains into all subdomains.");
        configuration.AllowedWebsites.Add("*.oaiusercontent.com");
        configuration.ManualSession = BlockStateEvaluator.CreateUnlimitedSession(TimeProvider.System);
        var sessionId = configuration.ManualSession.Id;
        foreach (var mode in new[] { WebsiteBlockingMode.BlockListedSites, WebsiteBlockingMode.AllowListedSites })
        {
            configuration.WebsiteMode = mode;
            await store.SaveAsync(configuration);
            configuration = await store.LoadAsync();
            Check(configuration.AllowedWebsites.SequenceEqual(["example.com", "*.oaiusercontent.com"])
                && configuration.ManualSession?.Id == sessionId && configuration.WebsiteMode == mode
                && store.RecoveryMessage is null, "Mixed exact and subdomain rules must survive restart in both modes without losing the session.");
        }
    }

    public static Task HostsContent()
    {
        const string original = "# unrelated\r\n0.0.0.0 example.com\r\n127.0.0.1 localhost\r\n";
        var blocked = HostsFileManager.BuildContent(original, ["example.com", "other.com"]);
        var updated = HostsFileManager.BuildContent(blocked, ["example.com", "other.com"], ["example.com"]);
        Check(updated.StartsWith(original, StringComparison.Ordinal), "An external block of an allowed site must remain untouched.");
        Check(!updated[original.Length..].Contains("example.com", StringComparison.Ordinal), "Both address families and www must be omitted from the owned section.");
        Check(updated.Contains("0.0.0.0 other.com", StringComparison.Ordinal) && updated.Contains("::1 www.other.com", StringComparison.Ordinal), "Unexcepted rules must remain active.");
        Check(HostsFileManager.BuildContent(updated, ["example.com", "other.com"], ["example.com"]) == updated, "Whitelist apply must be idempotent.");
        Check(HostsFileManager.BuildContent(updated, ["example.com"], ["example.com"]) == original, "All rules excepted must remove the owned section exactly.");
        var partial = HostsFileManager.BuildContent("", ["example.com"], ["www.example.com"]);
        Check(partial.Contains("0.0.0.0 example.com", StringComparison.Ordinal) && !partial.Contains("www.example.com", StringComparison.Ordinal), "Hosts generation must not reintroduce an excluded www variant.");
        return Task.CompletedTask;
    }

    public static async Task ApplyChanges()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "hosts");
        var original = System.Text.Encoding.UTF8.GetBytes("# original\r\n127.0.0.1 localhost\r\n");
        await File.WriteAllBytesAsync(path, original);
        var hosts = new HostsFileManager(path);
        await hosts.ApplyAsync(["example.com"]);
        await hosts.ApplyAsync(["example.com"], ["example.com"]);
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(original), "Adding an exception during blocking must remove existing entries.");
        await hosts.ApplyAsync(["example.com"], []);
        Check((await File.ReadAllTextAsync(path)).Contains("::1 example.com", StringComparison.Ordinal), "Removing an exception must restore blocking.");
        await hosts.RemoveAsync();
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(original), "Stopping must reverse all owned changes.");
    }

    public static async Task Persistence()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new JsonConfigurationStore(path);
        foreach (var legacy in new[] { "{}", "{\"Version\":1}", "{\"Version\":2,\"Websites\":[\"example.com\"]}" })
        {
            await File.WriteAllTextAsync(path, legacy);
            var configuration = await store.LoadAsync();
            Check(configuration.Version == BlockConfiguration.CurrentVersion && configuration.AllowedWebsites.Count == 0, "Older configurations must migrate with no implicit exceptions.");
        }
        var saved = new BlockConfiguration
        {
            Websites = ["example.com", "other.com"],
            AllowedWebsites = ["example.com"],
            ManualSession = BlockStateEvaluator.CreateUnlimitedSession(TimeProvider.System)
        };
        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();
        Check(loaded.AllowedWebsites.SequenceEqual(saved.AllowedWebsites), "Whitelist must survive restart.");
        Check(loaded.Websites.SequenceEqual(saved.Websites) && loaded.ManualSession?.Id == saved.ManualSession.Id, "Saving exceptions must preserve block rules and session.");
        Check(WebsiteRules.GetBlockedDomains(loaded.Websites, loaded.AllowedWebsites).SequenceEqual(new[] { "other.com", "www.other.com" }), "Restored rules must retain exceptions.");
        loaded.AllowedWebsites = ["*.*.example.com"];
        var bytesBefore = await File.ReadAllBytesAsync(path);
        try { await store.SaveAsync(loaded); throw new Exception("Invalid whitelist was saved."); }
        catch (InvalidDataException) { }
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(bytesBefore), "Invalid save must leave persisted configuration intact.");
    }

    public static async Task InvalidConfiguration()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new JsonConfigurationStore(path);
        await store.SaveAsync(new BlockConfiguration { AllowedWebsites = ["example.com"], ManualSession = BlockStateEvaluator.CreateUnlimitedSession(TimeProvider.System) });
        File.Copy(path, path + ".bak");
        foreach (var invalid in new[] { "null", "[\"*.*.example.com\"]", "[null]" })
        {
            await File.WriteAllTextAsync(path, "{\"Version\":3,\"AllowedWebsites\":" + invalid + "}");
            var recovered = await store.LoadAsync();
            Check(store.RecoveryMessage is not null && recovered.ManualSession is null && !recovered.Schedule.Enabled, "Invalid whitelist must trigger recovery without activating blocking.");
            Check(recovered.AllowedWebsites.SequenceEqual(new[] { "example.com" }), "Recovery must preserve the backup whitelist.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected invalid rule to be rejected.");
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
