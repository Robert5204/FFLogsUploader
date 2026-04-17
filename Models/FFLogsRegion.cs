namespace FFLogsPlugin.Models;

/// <summary>
/// FFLogs server regions. Values match the serverOrRegion IDs used in the API.
/// </summary>
public enum FFLogsRegion
{
    NA = 1,
    EU = 2,
    JP = 3,
    OC = 6
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
        FFLogsRegion.OC => "OC",
        _ => "NA"
    };

    /// <summary>
    /// Display names for UI combo boxes, indexed by combo position.
    /// </summary>
    public static readonly string[] DisplayNames = { "NA", "EU", "JP", "OC" };

    /// <summary>
    /// Maps combo index to the serverOrRegion API value.
    /// </summary>
    public static readonly int[] ApiValues = { 1, 2, 3, 6 };

    /// <summary>
    /// Converts a combo index to the API region value.
    /// </summary>
    public static int ComboIndexToApiValue(int index)
    {
        if (index >= 0 && index < ApiValues.Length)
            return ApiValues[index];
        return 1;
    }

    /// <summary>
    /// Converts an API region value to a combo index.
    /// </summary>
    public static int ApiValueToComboIndex(int apiValue)
    {
        for (int i = 0; i < ApiValues.Length; i++)
        {
            if (ApiValues[i] == apiValue)
                return i;
        }
        return 0;
    }
}
