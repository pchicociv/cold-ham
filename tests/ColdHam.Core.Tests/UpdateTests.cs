using ColdHam.Core;

internal static class UpdateTests
{
    public static Task RestartSafety()
    {
        var clock = new UpdateClock();
        var configuration = new BlockConfiguration();
        var safe = new UpdateSafetyState(false, false, true, false, false, false);
        Check(UpdateRestartPolicy.CanRestart(configuration, clock, safe), "Inactive, recovered app should restart.");
        foreach (var unsafeState in new[]
        {
            safe with { Busy = true }, safe with { EnforcementActive = true },
            safe with { WebRulesRemoved = false }, safe with { HasErrors = true },
            safe with { HasUnsavedInput = true }, safe with { Exiting = true }
        })
            Check(!UpdateRestartPolicy.CanRestart(configuration, clock, unsafeState), "Unsafe app state allowed a restart.");

        configuration.ManualSession = BlockStateEvaluator.CreateUnlimitedSession(clock);
        Check(!UpdateRestartPolicy.CanRestart(configuration, clock, safe), "Unlimited session must block updates.");
        configuration.ManualSession.EndsAtUtc = clock.Now.AddMinutes(1);
        Check(!UpdateRestartPolicy.CanRestart(configuration, clock, safe), "Timed session must block updates.");
        clock.Now = clock.Now.AddMinutes(1);
        Check(!UpdateRestartPolicy.CanRestart(configuration, clock, safe), "Expired session must be saved/cleaned first.");
        configuration.ManualSession = null;
        Check(UpdateRestartPolicy.CanRestart(configuration, clock, safe), "Cleaned expired session may update.");
        return Task.CompletedTask;
    }

    public static Task ScheduleSafety()
    {
        var clock = new UpdateClock();
        var safe = new UpdateSafetyState(false, false, true, false, false, false);
        var configuration = new BlockConfiguration
        {
            Schedule = new WeeklySchedule
            {
                Enabled = true, Days = [DayOfWeek.Monday], Start = new(10, 0), End = new(11, 0)
            }
        };
        clock.Now = new DateTimeOffset(new DateTime(2026, 11, 9, 9, 54, 59, DateTimeKind.Local));
        Check(UpdateRestartPolicy.CanRestart(configuration, clock, safe), "More than five minutes should allow restart.");
        clock.Now = clock.Now.AddSeconds(1);
        Check(!UpdateRestartPolicy.CanRestart(configuration, clock, safe), "Exact five-minute margin must defer.");
        clock.Now = clock.Now.AddMinutes(5);
        Check(!UpdateRestartPolicy.CanRestart(configuration, clock, safe), "Active schedule must defer even before UI timer runs.");
        clock.Now = clock.Now.AddHours(1);
        Check(UpdateRestartPolicy.CanRestart(configuration, clock, safe), "Ended schedule may update after cleanup.");
        configuration.Schedule.Start = new(23, 0);
        configuration.Schedule.End = new(2, 0);
        clock.Now = new DateTimeOffset(new DateTime(2026, 11, 10, 1, 0, 0, DateTimeKind.Local));
        Check(!UpdateRestartPolicy.CanRestart(configuration, clock, safe), "Overnight schedule must defer.");
        return Task.CompletedTask;
    }

    public static async Task DownloadAndRestart()
    {
        var backend = new FakeBackend();
        var coordinator = new UpdateCoordinator(backend, new UpdateClock());
        var stages = new List<UpdateStage>();
        coordinator.Changed += (_, _) => stages.Add(coordinator.Stage);
        await coordinator.CheckAsync();
        Check(stages.SequenceEqual([UpdateStage.Checking, UpdateStage.Downloading, UpdateStage.Ready]), "Check must download before making restart available.");
        Check(!coordinator.TryPrepareRestart(() => false) && backend.Restarts == 0, "Unsafe restart must not launch updater.");
        await coordinator.CheckAsync(force: true);
        Check(backend.Checks == 1, "Ready update must not download twice.");
        Check(coordinator.TryPrepareRestart(() => true) && backend.Restarts == 1, "Safe restart must launch once.");
        Check(!coordinator.TryPrepareRestart(() => true) && backend.Restarts == 1, "Repeated restart must be ignored.");
    }

    public static async Task FailuresAndRetry()
    {
        var clock = new UpdateClock();
        var backend = new FakeBackend { FailDownload = true };
        var coordinator = new UpdateCoordinator(backend, clock);
        await coordinator.CheckAsync();
        Check(coordinator.Stage == UpdateStage.Failed && coordinator.Error is not null, "Invalid/incomplete download must fail.");
        Check(!coordinator.TryPrepareRestart(() => true), "Failed verification must never permit installation.");
        await coordinator.CheckAsync();
        Check(backend.Checks == 1, "Failure should back off.");
        clock.Now = clock.Now.AddMinutes(30);
        backend.FailDownload = false;
        await coordinator.CheckAsync();
        Check(coordinator.Stage == UpdateStage.Ready && coordinator.Error is null, "Retry should recover.");
        backend.FailRestart = true;
        Check(!coordinator.TryPrepareRestart(() => true) && coordinator.Stage == UpdateStage.Failed, "Updater launch failure must keep app running.");
        backend.FailRestart = false;
        await coordinator.CheckAsync(force: true);
        Check(coordinator.TryPrepareRestart(() => true), "Explicit retry should recover launch failure.");
    }

    public static async Task OfflineAndCadence()
    {
        var clock = new UpdateClock();
        var backend = new FakeBackend { FailCheck = true };
        var coordinator = new UpdateCoordinator(backend, clock);
        await coordinator.CheckAsync();
        Check(coordinator.Stage == UpdateStage.Failed, "Network errors must be contained.");
        backend.FailCheck = false;
        backend.Available = false;
        await coordinator.CheckAsync(force: true);
        Check(coordinator.Stage == UpdateStage.Current && backend.Downloads == 0, "No update should not download.");
        await coordinator.CheckAsync();
        Check(backend.Checks == 2, "Automatic checks should be throttled.");
        clock.Now = clock.Now.AddHours(6);
        await coordinator.CheckAsync();
        Check(backend.Checks == 3, "Six-hour check should run.");
        var portable = new FakeBackend { IsInstalled = false };
        var portableCoordinator = new UpdateCoordinator(portable, clock);
        await portableCoordinator.CheckAsync(force: true);
        Check(portableCoordinator.Stage == UpdateStage.Unavailable && portable.Checks == 0, "Unpackaged app must not check or replace its executable.");
    }

    public static async Task ConcurrentChecksAndCancellation()
    {
        var backend = new FakeBackend { CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var coordinator = new UpdateCoordinator(backend, new UpdateClock());
        using var cancellation = new CancellationTokenSource();
        var first = coordinator.CheckAsync(cancellationToken: cancellation.Token);
        await coordinator.CheckAsync(force: true);
        Check(backend.Checks == 1, "Timer and manual checks must not overlap.");
        cancellation.Cancel();
        backend.CheckGate.SetResult(true);
        await first;
        Check(coordinator.Stage == UpdateStage.Failed && backend.Downloads == 0, "Exiting during check must prevent subsequent download/restart.");

        backend.CheckGate = null;
        backend.DownloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var downloadCancellation = new CancellationTokenSource();
        var second = coordinator.CheckAsync(force: true, downloadCancellation.Token);
        Check(coordinator.Stage == UpdateStage.Downloading, "Expected download in progress.");
        downloadCancellation.Cancel();
        await second;
        Check(!coordinator.TryPrepareRestart(() => true), "Interrupted download must not install.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class UpdateClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    private sealed class FakeBackend : IUpdateBackend
    {
        public bool IsInstalled { get; set; } = true;
        public bool Available { get; set; } = true;
        public bool FailCheck { get; set; }
        public bool FailDownload { get; set; }
        public bool FailRestart { get; set; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Restarts { get; private set; }
        public TaskCompletionSource<bool>? CheckGate { get; set; }
        public TaskCompletionSource<bool>? DownloadGate { get; set; }

        public Task<bool> CheckAsync()
        {
            Checks++;
            if (FailCheck) throw new IOException("Network unavailable.");
            return CheckGate?.Task ?? Task.FromResult(Available);
        }

        public async Task DownloadAsync(CancellationToken cancellationToken)
        {
            Downloads++;
            if (FailDownload) throw new InvalidDataException("Package checksum mismatch.");
            if (DownloadGate is not null) await DownloadGate.Task.WaitAsync(cancellationToken);
        }

        public void PrepareRestart()
        {
            if (FailRestart) throw new IOException("Updater unavailable.");
            Restarts++;
        }
    }
}
