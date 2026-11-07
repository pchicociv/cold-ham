namespace ColdHam.Core;

public sealed record UpdateSafetyState(
    bool Busy, bool EnforcementActive, bool WebRulesRemoved, bool HasErrors,
    bool HasUnsavedInput, bool Exiting);

public static class UpdateRestartPolicy
{
    public static readonly TimeSpan ScheduleMargin = TimeSpan.FromMinutes(5);

    public static bool CanRestart(BlockConfiguration configuration, TimeProvider clock, UpdateSafetyState safety)
    {
        if (safety.Busy || safety.EnforcementActive || !safety.WebRulesRemoved || safety.HasErrors
            || safety.HasUnsavedInput || safety.Exiting) return false;

        var state = BlockStateEvaluator.Evaluate(configuration, clock);
        if (state.DesiredActive || state.SessionExpired) return false;
        return state.NextScheduleTransition is not { } transition || transition - clock.GetUtcNow() > ScheduleMargin;
    }
}
