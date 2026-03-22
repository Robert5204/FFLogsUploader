namespace FFLogsPlugin.Models;

/// <summary>
/// Parse result for a single character in a specific fight.
/// </summary>
public class FightParse
{
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string Job { get; set; } = string.Empty;
    public string Spec { get; set; } = string.Empty;

    /// <summary>
    /// Parse percentile for this specific pull (0–100), or null if not ranked.
    /// </summary>
    public float? ParsePercent { get; set; }

    /// <summary>
    /// Regular DPS (damage per second) for this pull.
    /// </summary>
    public float? Dps { get; set; }

    /// <summary>
    /// rDPS (raid-contributed DPS) for this pull.
    /// </summary>
    public float? Rdps { get; set; }

    /// <summary>
    /// aDPS (adjusted personal DPS accounting for buffs received) for this pull.
    /// </summary>
    public float? Adps { get; set; }

    /// <summary>
    /// Whether this character is a tank/healer/dps — used for sort ordering.
    /// </summary>
    public string Role { get; set; } = string.Empty;
}
