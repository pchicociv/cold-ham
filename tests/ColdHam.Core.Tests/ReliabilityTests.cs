using System.Text;
using ColdHam.Core;
using ColdHam.Windows;

internal static class ReliabilityTests
{
    public static Task ExactHostsContent()
    {
        foreach (var original in new[] { "", "127.0.0.1 localhost", "# café\r\n\r\n127.0.0.1 localhost\r\n\r\n", "# header\n\n10.0.0.1 intranet\n" })
        {
            var blocked = HostsFileManager.BuildContent(original, ["example.com"]);
            Check(HostsFileManager.RemoveManagedSection(blocked) == original, "Unrelated text must round-trip exactly.");
            Check(HostsFileManager.BuildContent(blocked, ["example.com"]) == blocked, "Apply must be idempotent.");
            Check(HostsFileManager.BuildContent(blocked, []) == original, "Empty rules must remove the owned section.");
        }
        var prefix = "# header\r\n\r\n";
        var suffix = "\r\n\r\n10.0.0.2 intranet\r\n";
        var middle = prefix + HostsFileManager.BeginMarker + "\r\n0.0.0.0 old.example\r\n" + HostsFileManager.EndMarker + suffix;
        var changed = HostsFileManager.BuildContent(middle, ["example.com"]);
        Check(changed.StartsWith(prefix, StringComparison.Ordinal) && changed.EndsWith(suffix, StringComparison.Ordinal), "Both unrelated boundaries must survive.");
        return Task.CompletedTask;
    }

    public static Task InvalidMarkers()
    {
        var begin = HostsFileManager.BeginMarker;
        var end = HostsFileManager.EndMarker;
        foreach (var invalid in new[] { begin, end, end + "\n" + begin, begin + "\n" + begin + "\n" + end,
            begin + "\n" + end + "\n" + begin + "\n" + end, "# comment " + begin + "\n" + end, begin + "\n" + end + " trailing" })
        {
            Expect<InvalidDataException>(() => HostsFileManager.RemoveManagedSection(invalid));
            Expect<InvalidDataException>(() => HostsFileManager.BuildContent(invalid, ["example.com"]));
        }
        return Task.CompletedTask;
    }

    public static async Task HostsBytes()
    {
        using var temp = new TestDirectory();
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), Encoding.Latin1 })
        {
            var original = encoding.GetPreamble().Concat(encoding.GetBytes("# café\r\n\r\n127.0.0.1 localhost")).ToArray();
            var path = Path.Combine(temp.Path, "hosts");
            await File.WriteAllBytesAsync(path, original);
            var manager = new HostsFileManager(path);
            await manager.ApplyAsync(["example.com"]);
            Check((await File.ReadAllBytesAsync(path + ".coldham-recovery")).SequenceEqual(original), "Recovery copy must precede the change.");
            await manager.RemoveAsync();
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(original), "Encoding, BOM and unrelated bytes must survive.");
        }
    }

    public static async Task LockedHosts()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "hosts");
        const string original = "127.0.0.1 localhost\n";
        await File.WriteAllTextAsync(path, original);
        var manager = new HostsFileManager(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await ExpectAsync<IOException>(() => manager.ApplyAsync(["example.com"]));
        }
        Check(await File.ReadAllTextAsync(path) == original, "Failed apply must leave hosts unchanged.");
        await manager.ApplyAsync(["example.com"]);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await ExpectAsync<IOException>(() => manager.RemoveAsync());
        }
        Check((await File.ReadAllTextAsync(path)).Contains(HostsFileManager.BeginMarker, StringComparison.Ordinal), "Failed removal must not report or imply removal.");
        await manager.RemoveAsync();
        Check(await File.ReadAllTextAsync(path) == original, "Removal must succeed on retry.");
    }

    public static async Task ReadOnlyHosts()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "hosts");
        await File.WriteAllTextAsync(path, "127.0.0.1 localhost\n");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var manager = new HostsFileManager(path);
            await manager.RemoveAsync();
            await ExpectAsync<UnauthorizedAccessException>(() => manager.ApplyAsync(["example.com"]));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    public static async Task RecoverSettings()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new JsonConfigurationStore(path);
        await store.SaveAsync(new BlockConfiguration { Websites = ["saved.example"], ManualSession = new() { StartedAtUtc = DateTimeOffset.UtcNow }, Schedule = new() { Enabled = true } });
        await store.SaveAsync(new BlockConfiguration { Websites = ["new.example"] });
        const string corrupt = "{broken";
        await File.WriteAllTextAsync(path, corrupt);
        var recovered = await store.LoadAsync();
        Check(recovered.Websites.Single() == "saved.example", "Last valid backup should be loaded.");
        Check(recovered.ManualSession is null && !recovered.Schedule.Enabled, "Recovery must never activate a block.");
        Check(store.RecoveryMessage is not null, "Recovery must be visible.");
        Check(await File.ReadAllTextAsync(Directory.GetFiles(temp.Path, "*.corrupt-*").Single()) == corrupt, "Damaged source must be preserved.");
        await store.SaveAsync(recovered);
        var reloaded = await new JsonConfigurationStore(path).LoadAsync();
        Check(reloaded.Websites.Single() == "saved.example", "Recovered settings must be saveable.");
    }

    public static async Task InvalidSettings()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        foreach (var json in new[] { "null", "{\"Websites\":null}", "{\"Schedule\":null}", "{\"Applications\":[\"*.exe\"]}", "{\"Schedule\":{\"Days\":[8]}}" })
        {
            await File.WriteAllTextAsync(path, json);
            await File.WriteAllTextAsync(path + ".bak", "invalid backup");
            var store = new JsonConfigurationStore(path);
            var recovered = await store.LoadAsync();
            Check(recovered.Websites.Count == 0 && recovered.ManualSession is null && !recovered.Schedule.Enabled, "Invalid data must yield a safe configuration.");
            Check(await File.ReadAllTextAsync(path) == json, "Load must not overwrite damaged data.");
            Check(store.RecoveryMessage is not null, "Invalid configuration must be reported.");
        }
    }

    public static async Task ConcurrentSaves()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new JsonConfigurationStore(path);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => store.SaveAsync(new BlockConfiguration { Websites = [$"site{i}.example"] })));
        Check((await store.LoadAsync()).Websites.Count == 1, "Concurrent saves must produce valid JSON.");
        Check((await new JsonConfigurationStore(path + ".bak").LoadAsync()).Websites.Count == 1, "Backup must also be valid.");
    }

    public static Task Consent()
    {
        var consent = new ApplicationBlockConsent();
        string[] rules = ["game.exe"];
        Check(consent.Evaluate(true, rules) == ApplicationConsentStatus.NeedsConfirmation, "Startup and scheduled activation must need consent.");
        consent.Decide(rules, false);
        Check(consent.Evaluate(true, rules) == ApplicationConsentStatus.Denied, "Timer must not prompt repeatedly or treat rejection as approval.");
        consent.Reset();
        Check(consent.Evaluate(true, rules) == ApplicationConsentStatus.NeedsConfirmation, "Explicit retry permits a new decision.");
        consent.Decide(rules, true);
        Check(consent.Evaluate(true, ["GAME.exe"]) == ApplicationConsentStatus.Approved, "Unchanged rules retain consent.");
        Check(consent.Evaluate(true, ["game.exe", "editor.exe"]) == ApplicationConsentStatus.NeedsConfirmation, "Adding a rule requires consent.");
        consent.Evaluate(false, rules);
        Check(consent.Evaluate(true, rules) == ApplicationConsentStatus.NeedsConfirmation, "A new session requires consent again.");
        return Task.CompletedTask;
    }

    public static Task UnsafeExecutables()
    {
        foreach (var rule in new[] { "", "*.exe", "?.exe", "game\n.exe", "ColdHam.App.exe", "svchost.exe", @"C:\Windows\System32\explorer.exe" })
            Check(!RuleNormalizer.TryNormalizeApplication(rule, out _, out _), $"Unsafe executable accepted: {rule}");
        return Task.CompletedTask;
    }

    public static Task ScheduleBoundaries()
    {
        var schedule = new WeeklySchedule { Enabled = true, Days = [DayOfWeek.Monday], Start = new(9, 0), End = new(17, 0) };
        var start = new DateTimeOffset(new DateTime(2026, 8, 31, 9, 0, 0, DateTimeKind.Local));
        Check(!ScheduleEvaluator.IsActive(schedule, start.AddTicks(-1)), "Start is inclusive.");
        Check(ScheduleEvaluator.IsActive(schedule, start), "Exact start must activate.");
        Check(!ScheduleEvaluator.IsActive(schedule, start.AddHours(8)), "End is exclusive.");
        schedule.Start = schedule.End;
        Check(ScheduleEvaluator.IsActive(schedule, start), "Equal hours mean the entire selected day.");
        Check(!ScheduleEvaluator.IsActive(schedule, start.AddDays(1)), "An unselected day stays inactive.");
        var next = ScheduleEvaluator.FindNextTransition(schedule, start);
        Check(next?.LocalDateTime == new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Local), "All-day schedules must transition at midnight.");
        return Task.CompletedTask;
    }

    public static Task ManualSessions()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero));
        Check(!BlockStateEvaluator.TryCreateTimedSession(clock, 0, out _, out _), "Zero minutes must be rejected.");
        Check(!BlockStateEvaluator.TryCreateTimedSession(clock, 1441, out _, out _), "Durations above one day must be rejected.");
        Check(BlockStateEvaluator.TryCreateTimedSession(clock, 25, out var session, out _), "25 minutes must be accepted.");
        var configuration = new BlockConfiguration { ManualSession = session };
        var active = BlockStateEvaluator.Evaluate(configuration, clock);
        Check(active.ManualActive && active.DesiredActive && active.Remaining == TimeSpan.FromMinutes(25), "Timed session must be active with its exact remaining time.");
        clock.Advance(TimeSpan.FromMinutes(25));
        var expired = BlockStateEvaluator.Evaluate(configuration, clock);
        Check(expired.SessionExpired && !expired.ManualActive && !expired.DesiredActive && expired.Remaining is null, "Exact deadline must expire the session.");
        configuration.ManualSession = BlockStateEvaluator.CreateUnlimitedSession(clock);
        var unlimited = BlockStateEvaluator.Evaluate(configuration, clock);
        Check(unlimited.ManualActive && unlimited.Remaining is null, "Unlimited session must not expose a deadline.");
        return Task.CompletedTask;
    }

    public static Task SessionScheduleOverlap()
    {
        var monday = new DateTimeOffset(new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Local));
        var clock = new FakeTimeProvider(monday.ToUniversalTime());
        var configuration = new BlockConfiguration
        {
            ManualSession = new ManualSession { StartedAtUtc = clock.GetUtcNow(), EndsAtUtc = clock.GetUtcNow().AddMinutes(1) },
            Schedule = new WeeklySchedule { Enabled = true, Days = [DayOfWeek.Monday], Start = new(9, 0), End = new(17, 0) }
        };
        Check(BlockStateEvaluator.Evaluate(configuration, clock).DesiredActive, "Both sources should activate blocking.");
        clock.Advance(TimeSpan.FromMinutes(1));
        var state = BlockStateEvaluator.Evaluate(configuration, clock);
        Check(state.SessionExpired && state.ScheduledActive && state.DesiredActive, "A schedule must keep blocking after a session expires.");
        configuration.ManualSession = null;
        Check(BlockStateEvaluator.Evaluate(configuration, clock).DesiredActive, "Ending a manual session must not disable an active schedule.");
        return Task.CompletedTask;
    }

    public static async Task LegacySessionMigration()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{\"Websites\":[],\"Applications\":[],\"Schedule\":{\"Enabled\":false,\"Days\":[],\"Start\":\"09:00:00\",\"End\":\"17:00:00\"},\"ManualBlockEnabled\":true}");
        var store = new JsonConfigurationStore(path);
        var migrated = await store.LoadAsync();
        Check(migrated.Version == BlockConfiguration.CurrentVersion && migrated.ManualSession is not null, "Legacy true must become an unlimited session.");
        await store.SaveAsync(migrated);
        var saved = await File.ReadAllTextAsync(path);
        Check(!saved.Contains("ManualBlockEnabled", StringComparison.Ordinal), "New settings must not retain the legacy field.");
    }

    public static async Task FutureConfigurationVersion()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        const string future = "{\"Version\":99,\"Websites\":[],\"Applications\":[],\"Schedule\":{\"Enabled\":false,\"Days\":[],\"Start\":\"09:00:00\",\"End\":\"17:00:00\"}}";
        await File.WriteAllTextAsync(path, future);
        await ExpectAsync<UnsupportedConfigurationVersionException>(() => new JsonConfigurationStore(path).LoadAsync());
        Check(await File.ReadAllTextAsync(path) == future, "A future configuration must remain untouched.");
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

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
