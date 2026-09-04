namespace ColdHam.Core;

public static class ScheduleEvaluator
{
    public static bool IsActive(WeeklySchedule schedule, DateTimeOffset now)
    {
        if (!schedule.Enabled || schedule.Days.Count == 0)
        {
            return false;
        }

        var local = now.LocalDateTime;
        var time = TimeOnly.FromDateTime(local);

        if (schedule.Start == schedule.End)
        {
            return schedule.Days.Contains(local.DayOfWeek);
        }

        if (schedule.Start < schedule.End)
        {
            return schedule.Days.Contains(local.DayOfWeek)
                && time >= schedule.Start
                && time < schedule.End;
        }

        if (schedule.Days.Contains(local.DayOfWeek) && time >= schedule.Start)
        {
            return true;
        }

        var previousDay = local.AddDays(-1).DayOfWeek;
        return schedule.Days.Contains(previousDay) && time < schedule.End;
    }
}

