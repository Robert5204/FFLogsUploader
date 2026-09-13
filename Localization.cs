using System;
using System.Globalization;

namespace FFLogsPlugin;

/// <summary>Small, dependency-free UI localization helper. English is the fallback.</summary>
internal static class Loc
{
    private static bool IsChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string T(string english, string chinese) => IsChinese ? chinese : english;
}
