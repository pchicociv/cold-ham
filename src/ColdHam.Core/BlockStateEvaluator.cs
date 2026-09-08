namespace ColdHam.Core;

public sealed record BlockState(
    bool ManualActive,
    bool SessionExpired,
    bool ScheduledActive,
    bool DesiredActive,
    TimeSpan? Remaining,
    DateTimeOffset? NextScheduleTransition);

public static class BlockStateEvaluator
{
    public static BlockState Evaluate(BlockConfiguration configuration, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();
        var session = configuration.ManualSession;
        var expired = session?.EndsAtUtc is { } endsAt && endsAt <= now;
        var manualActive = session is not null && !expired;
        var scheduled = ScheduleEvaluator.IsActive(configuration.Schedule, now);
        return new BlockState(
            manualActive,
            expired,
            scheduled,
            manualActive || scheduled,
            manualActive && session?.EndsAtUtc is { } end ? end - now : null,
            ScheduleEvaluator.FindNextTransition(configuration.Schedule, now));
    }

    public static bool TryCreateTimedSession(TimeProvider clock, int minutes, out ManualSession? session, out string error)
    {
        session = null;
        error = string.Empty;
        if (minutes is < 1 or > 1440)
        {
            error = "La duración debe ser un número entero entre 1 y 1440 minutos.";
            return false;
        }

        var now = clock.GetUtcNow();
        session = new ManualSession { StartedAtUtc = now, EndsAtUtc = now.AddMinutes(minutes) };
        return true;
    }

    public static ManualSession CreateUnlimitedSession(TimeProvider clock) => new() { StartedAtUtc = clock.GetUtcNow() };
}
