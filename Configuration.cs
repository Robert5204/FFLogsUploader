using Dalamud.Configuration;
using System;

namespace FFLogsPlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Credentials (saved locally)
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberCredentials { get; set; } = false;

    // Default settings
    public string LogDirectory { get; set; } = string.Empty;
    public int Region { get; set; } = 1; // 1=NA, 2=EU, 3=JP, 4=CN, 5=KR
    public int Visibility { get; set; } = 1; // 0=Public, 1=Private, 2=Unlisted
    public string? SelectedGuildId { get; set; } = null;

    // Session data
    public string? SessionCookie { get; set; } = null;
    public string? XsrfToken { get; set; } = null;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
