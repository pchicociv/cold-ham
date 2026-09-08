using System.Text.Json.Serialization;

namespace ColdHam.Core;

public sealed class BlockConfiguration
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public List<string> Websites { get; set; } = [];

    public List<string> Applications { get; set; } = [];

    public WeeklySchedule Schedule { get; set; } = new();

    /// <summary>The current manual session. Null means manual blocking is off.</summary>
    public ManualSession? ManualSession { get; set; }

    // Kept only to read version 1 settings. The store clears it before its next save.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ManualBlockEnabled { get; set; }
}

public sealed class ManualSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>Null represents an explicit unlimited manual session.</summary>
    public DateTimeOffset? EndsAtUtc { get; set; }
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
