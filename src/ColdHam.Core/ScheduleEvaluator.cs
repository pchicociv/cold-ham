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

    public static DateTimeOffset? FindNextTransition(WeeklySchedule schedule, DateTimeOffset now)
    {
        if (!schedule.Enabled || schedule.Days.Count == 0)
        {
            return null;
        }

        var localNow = now.LocalDateTime;
        var active = IsActive(schedule, now);
        var boundaries = new List<DateTime>();
        for (var offset = -1; offset <= 8; offset++)
        {
            var date = localNow.Date.AddDays(offset);
            if (!schedule.Days.Contains(date.DayOfWeek)) continue;
            if (schedule.Start == schedule.End)
            {
                boundaries.Add(date);
                boundaries.Add(date.AddDays(1));
            }
            else
            {
                boundaries.Add(date.Add(schedule.Start.ToTimeSpan()));
                boundaries.Add((schedule.End <= schedule.Start ? date.AddDays(1) : date).Add(schedule.End.ToTimeSpan()));
            }
        }

        foreach (var boundary in boundaries.Where(value => value > localNow).Distinct().Order())
        {
            if (IsActive(schedule, new DateTimeOffset(boundary.AddTicks(1))) != active)
            {
                return new DateTimeOffset(boundary);
            }
        }

        return null;
    }
}
