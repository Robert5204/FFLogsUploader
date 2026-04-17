using System;
using Dalamud.Configuration;
using FFLogsPlugin.Helpers;

namespace FFLogsPlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Credentials (password encrypted — DPAPI on Windows, AES on Linux/Wine)
    public string Email { get; set; } = string.Empty;
    public byte[]? EncryptedPassword { get; set; } = null;
    public byte[]? EncryptionSalt { get; set; } = null; // Random per-install salt for AES key derivation
    public bool RememberCredentials { get; set; } = false;

    // FFLogs API v2 client credentials (for fetching parses/rankings)
    public string ApiClientId { get; set; } = string.Empty;
    public string ApiClientSecret { get; set; } = string.Empty;

    // Default settings
    public string LogDirectory { get; set; } = string.Empty;
    public int Region { get; set; } = 1; // serverOrRegion API value: 1=NA, 2=EU, 3=JP, 6=OC
    public int Visibility { get; set; } = 1; // 0=Public, 1=Private, 2=Unlisted
    public string? SelectedGuildId { get; set; } = null;

    /// <summary>
    /// Gets or sets the password, encrypting/decrypting transparently.
    /// Uses DPAPI on Windows, AES with a per-install salt on Linux/Wine.
    /// Not serialized — the encrypted form (EncryptedPassword) is what gets saved.
    /// </summary>
    private string? cachedPassword;

    [Newtonsoft.Json.JsonIgnore]
    public string Password
    {
        get
        {
            cachedPassword ??= CredentialHelper.Decrypt(EncryptedPassword, EncryptionSalt);
            return cachedPassword ?? string.Empty;
        }
        set
        {
            cachedPassword = value;
            if (string.IsNullOrEmpty(value))
            {
                EncryptedPassword = null;
            }
            else
            {
                // Ensure we have a salt for AES (no-op if DPAPI is used, but needed for the fallback)
                EncryptionSalt ??= CredentialHelper.GenerateSalt();
                EncryptedPassword = CredentialHelper.Encrypt(value, EncryptionSalt);
            }
        }
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
