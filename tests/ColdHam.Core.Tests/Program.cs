using ColdHam.Core;
using ColdHam.Windows;

var tests = new (string Name, Func<Task> Run)[]
{
    ("applies and consumes installation language without losing settings", LocalizationTests.InstallationLanguage),
    ("keeps installation language pending when persistence fails", LocalizationTests.InstallationLanguageRetry),
    ("defers updates until sessions, recovery and edits are settled", UpdateTests.RestartSafety),
    ("defers updates near scheduled and overnight blocks", UpdateTests.ScheduleSafety),
    ("downloads before authorizing a safe single restart", UpdateTests.DownloadAndRestart),
    ("recovers download and updater failures with bounded retries", UpdateTests.FailuresAndRetry),
    ("handles offline checks, cadence and unpackaged builds", UpdateTests.OfflineAndCadence),
    ("serializes update checks and cancels interrupted downloads", UpdateTests.ConcurrentChecksAndCancellation),
    ("normalizes website URLs", NormalizeWebsite),
    ("rejects wildcard websites", RejectWildcard),
    ("normalizes executable names", NormalizeExecutable),
    ("protects Windows shell", ProtectSystemExecutable),
    ("evaluates daytime schedules", EvaluateDaytimeSchedule),
    ("evaluates overnight schedules", EvaluateOvernightSchedule),
    ("replaces only managed hosts section", TransformHostsFile),
    ("rejects hosts-file rule injection", RejectHostsInjection),
    ("round-trips configuration", RoundTripConfiguration),
    ("preserves hosts content exactly", ReliabilityTests.ExactHostsContent),
    ("rejects ambiguous hosts markers", ReliabilityTests.InvalidMarkers),
    ("preserves hosts bytes and encoding", ReliabilityTests.HostsBytes),
    ("leaves locked hosts unchanged and retries", ReliabilityTests.LockedHosts),
    ("does not rewrite a read-only unmanaged hosts file", ReliabilityTests.ReadOnlyHosts),
    ("recovers corrupted settings from backup without activation", ReliabilityTests.RecoverSettings),
    ("preserves invalid settings without a usable backup", ReliabilityTests.InvalidSettings),
    ("serializes concurrent configuration saves", ReliabilityTests.ConcurrentSaves),
    ("requires consent for every new session and rule set", ReliabilityTests.Consent),
    ("rejects unsafe executable rules", ReliabilityTests.UnsafeExecutables),
    ("evaluates exact schedule boundaries", ReliabilityTests.ScheduleBoundaries),
    ("evaluates timed and unlimited sessions", ReliabilityTests.ManualSessions),
    ("keeps schedule and session independent", ReliabilityTests.SessionScheduleOverlap),
    ("migrates legacy manual state safely", ReliabilityTests.LegacySessionMigration),
    ("preserves future configuration versions", ReliabilityTests.FutureConfigurationVersion),
    ("normalizes whitelist and gives exceptions priority", WhitelistTests.Priority),
    ("limits exceptions to exact domains and www variants", WhitelistTests.ExactDomains),
    ("normalizes subdomain exceptions and respects domain boundaries", WhitelistTests.SubdomainRules),
    ("applies and reverses subdomain exceptions without changing unrelated hosts", WhitelistTests.SubdomainHosts),
    ("persists subdomain opt-in and migrates exact rules unchanged", WhitelistTests.SubdomainPersistence),
    ("rejects invalid whitelist rules", WhitelistTests.InvalidRules),
    ("preserves unrelated hosts rules when allowing sites", WhitelistTests.HostsContent),
    ("applies and reverses whitelist changes", WhitelistTests.ApplyChanges),
    ("persists whitelist and migrates older configurations", WhitelistTests.Persistence),
    ("recovers invalid whitelist without activation", WhitelistTests.InvalidConfiguration),
    ("builds bounded exact-host browser allow policies", BrowserPolicyTests.Patterns),
    ("applies bounded subdomain browser policies and removes broader permissions", BrowserPolicyTests.SubdomainPolicies),
    ("applies deny-all and updates allowed sites reversibly", BrowserPolicyTests.Lifecycle),
    ("refuses existing user and machine browser policies", BrowserPolicyTests.ExistingPolicies),
    ("rolls back failed policy writes and retries cleanup", BrowserPolicyTests.Failures),
    ("preserves externally changed browser policies", BrowserPolicyTests.ExternalChanges),
    ("rejects corrupted policy recovery journals", BrowserPolicyTests.InvalidJournal),
    ("persists and validates the website blocking mode", BrowserPolicyTests.ModePersistence),
    ("persists language with Spanish defaults and safe fallback", LocalizationTests.Persistence),
    ("provides complete Spanish and English resources and formats", LocalizationTests.Resources)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static Task NormalizeWebsite()
{
    Assert(RuleNormalizer.TryNormalizeDomain("https://Reddit.com/r/dotnet", out var domain, out _), "URL should be accepted.");
    Equal("reddit.com", domain);
    return Task.CompletedTask;
}

static Task RejectWildcard()
{
    Assert(!RuleNormalizer.TryNormalizeDomain("*.example.com", out _, out _), "Wildcard should be rejected.");
    return Task.CompletedTask;
}

static Task NormalizeExecutable()
{
    Assert(RuleNormalizer.TryNormalizeApplication("game", out var executable, out _), "Name should be accepted.");
    Equal("game.exe", executable);
    return Task.CompletedTask;
}

static Task ProtectSystemExecutable()
{
    Assert(!RuleNormalizer.TryNormalizeApplication("explorer.exe", out _, out _), "Explorer must be protected.");
    Assert(!RuleNormalizer.TryNormalizeApplication("ColdHam.exe", out _, out _), "Installed Cold Ham launcher must be protected.");
    Assert(!RuleNormalizer.TryNormalizeApplication("ColdHam.App.exe", out _, out _), "Cold Ham must be protected.");
    return Task.CompletedTask;
}

static Task EvaluateDaytimeSchedule()
{
    var schedule = new WeeklySchedule
    {
        Enabled = true,
        Days = [DayOfWeek.Monday],
        Start = new TimeOnly(9, 0),
        End = new TimeOnly(17, 0)
    };
    var monday = new DateTimeOffset(new DateTime(2026, 8, 31, 10, 0, 0, DateTimeKind.Local));
    Assert(ScheduleEvaluator.IsActive(schedule, monday), "Monday at 10:00 should be active.");
    Assert(!ScheduleEvaluator.IsActive(schedule, monday.AddHours(8)), "Monday at 18:00 should be inactive.");
    return Task.CompletedTask;
}

static Task EvaluateOvernightSchedule()
{
    var schedule = new WeeklySchedule
    {
        Enabled = true,
        Days = [DayOfWeek.Friday],
        Start = new TimeOnly(22, 0),
        End = new TimeOnly(2, 0)
    };
    var friday = new DateTime(2026, 9, 4, 23, 0, 0, DateTimeKind.Local);
    var saturday = new DateTime(2026, 9, 5, 1, 0, 0, DateTimeKind.Local);
    Assert(ScheduleEvaluator.IsActive(schedule, new DateTimeOffset(friday)), "Friday night should be active.");
    Assert(ScheduleEvaluator.IsActive(schedule, new DateTimeOffset(saturday)), "Saturday spillover should be active.");
    Assert(!ScheduleEvaluator.IsActive(schedule, new DateTimeOffset(saturday.AddHours(2))), "Saturday at 03:00 should be inactive.");
    return Task.CompletedTask;
}

static Task TransformHostsFile()
{
    var original = string.Join(Environment.NewLine,
        "127.0.0.1 localhost",
        HostsFileManager.BeginMarker,
        "0.0.0.0 old.example",
        HostsFileManager.EndMarker,
        "10.0.0.2 intranet") + Environment.NewLine;
    var updated = HostsFileManager.BuildContent(original, ["reddit.com"]);
    Assert(updated.Contains("127.0.0.1 localhost", StringComparison.Ordinal), "Existing prefix must remain.");
    Assert(updated.Contains("10.0.0.2 intranet", StringComparison.Ordinal), "Existing suffix must remain.");
    Assert(!updated.Contains("old.example", StringComparison.Ordinal), "Old managed rule must be removed.");
    Assert(updated.Contains("0.0.0.0 reddit.com", StringComparison.Ordinal), "New domain must be added.");
    Assert(updated.Contains("0.0.0.0 www.reddit.com", StringComparison.Ordinal), "www variant must be added.");
    Equal(1, Count(updated, HostsFileManager.BeginMarker));
    return Task.CompletedTask;
}

static Task RejectHostsInjection()
{
    try
    {
        HostsFileManager.BuildContent("127.0.0.1 localhost", ["example.com\r\n1.2.3.4 injected.test"]);
        throw new InvalidOperationException("A hosts-file injection should be rejected.");
    }
    catch (InvalidDataException)
    {
        return Task.CompletedTask;
    }
}

static async Task RoundTripConfiguration()
{
    var directory = Path.Combine(Path.GetTempPath(), "ColdHam.Tests", Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "settings.json");
    try
    {
        var store = new JsonConfigurationStore(path);
        await store.SaveAsync(new BlockConfiguration
        {
            Websites = ["example.com"],
            Applications = ["game.exe"],
            ManualSession = new ManualSession { StartedAtUtc = DateTimeOffset.UtcNow }
        });
        var loaded = await store.LoadAsync();
        Equal("example.com", loaded.Websites.Single());
        Equal("game.exe", loaded.Applications.Single());
        Assert(loaded.ManualSession is not null, "Manual session should persist.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

static int Count(string source, string value)
{
    var count = 0;
    var index = 0;
    while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += value.Length;
    }

    return count;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
