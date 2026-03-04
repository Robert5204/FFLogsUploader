namespace FFLogsPlugin.Models;

/// <summary>
/// FFLogs server regions. Values match the integer IDs used in config and API payloads.
/// </summary>
public enum FFLogsRegion
{
    NA = 1,
    EU = 2,
    JP = 3,
    CN = 4,
    KR = 5
}

public static class FFLogsRegionExtensions
{
    /// <summary>
    /// Converts the region enum to the two-letter code expected by the FFLogs parser.
    /// </summary>
    public static string ToCode(this FFLogsRegion region) => region switch
    {
        FFLogsRegion.NA => "NA",
        FFLogsRegion.EU => "EU",
        FFLogsRegion.JP => "JP",
        FFLogsRegion.CN => "CN",
        FFLogsRegion.KR => "KR",
        _ => "NA"
    };

    /// <summary>
    /// Display names for UI combo boxes, indexed by (int)region - 1.
    /// </summary>
    public static readonly string[] DisplayNames = { "NA", "EU", "JP", "CN", "KR" };
}
