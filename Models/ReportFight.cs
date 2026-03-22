using System;

namespace FFLogsPlugin.Models;

/// <summary>
/// Represents a single fight within an FFLogs report.
/// </summary>
public class ReportFight
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Kill { get; set; }
    public long StartTime { get; set; }
    public long EndTime { get; set; }

    /// <summary>
    /// Boss HP % remaining at wipe (0 = kill, e.g. 29.0 means 29% left). Null if unknown.
    /// </summary>
    public float? BossPercentage { get; set; }

    /// <summary>
    /// Wall-clock time when the fight started (computed from report startTime + fight offset).
    /// </summary>
    public DateTimeOffset? WallClockStart { get; set; }

    /// <summary>
    /// Fight duration formatted as m:ss.
    /// </summary>
    public string DurationString
    {
        get
        {
            var duration = TimeSpan.FromMilliseconds(EndTime - StartTime);
            return $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}";
        }
    }

    /// <summary>
    /// Display string: "Boss Name (4:32) 8:08 PM ✓" or "Boss Name (4:32) 8:08 PM 29%"
    /// </summary>
    public string DisplayString
    {
        get
        {
            string status;
            if (Kill)
            {
                status = "✓";
            }
            else if (BossPercentage.HasValue)
            {
                status = $"{BossPercentage.Value:F0}%";
            }
            else
            {
                status = "wipe";
            }

            var timeStr = WallClockStart.HasValue
                ? WallClockStart.Value.ToLocalTime().ToString("h:mm tt")
                : "";
            return $"{Name} ({DurationString}) {timeStr} {status}";
        }
    }
}
