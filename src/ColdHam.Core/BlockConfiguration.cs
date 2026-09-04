namespace ColdHam.Core;

public sealed class BlockConfiguration
{
    public List<string> Websites { get; set; } = [];

    public List<string> Applications { get; set; } = [];

    public WeeklySchedule Schedule { get; set; } = new();

    public bool ManualBlockEnabled { get; set; }
}

public sealed class WeeklySchedule
{
    public bool Enabled { get; set; }

    public List<DayOfWeek> Days { get; set; } =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday
    ];

    public TimeOnly Start { get; set; } = new(9, 0);

    public TimeOnly End { get; set; } = new(17, 0);
}

