using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FFLogsPlugin.Helpers;

/// <summary>
/// Encrypts and decrypts credentials using the best available platform mechanism.
///
/// - Windows: DPAPI (ProtectedData), bound to the current user account.
/// - Linux / macOS (Wine, native .NET): AES-256-CBC with a key derived from
///   a per-install random salt stored in the config alongside EncryptedPassword.
///   Not as strong as DPAPI but prevents plaintext passwords in the config file
///   and is stable regardless of Wine prefix or username changes.
/// </summary>
public static class CredentialHelper
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FFLogsPlugin_v1");

    // Magic prefix bytes to identify which encryption method was used.
    // This lets Decrypt auto-detect the format, so migrating between platforms
    // or upgrading from an older version "just works" (worst case: re-auth).
    private static readonly byte[] AesPrefix = Encoding.ASCII.GetBytes("AE");
    private static readonly byte[] DpPrefix = Encoding.ASCII.GetBytes("DP");

    /// <summary>
    /// Encrypt a plaintext string using the best available platform mechanism.
    /// The salt parameter is used for AES key derivation on non-Windows platforms.
    /// </summary>
    public static byte[] Encrypt(string plaintext, byte[] aesSalt)
    {
        if (string.IsNullOrEmpty(plaintext))
            return Array.Empty<byte>();

        var data = Encoding.UTF8.GetBytes(plaintext);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                // Prefix with "DP" so Decrypt knows this is DPAPI-encrypted
                var result = new byte[DpPrefix.Length + encrypted.Length];
                DpPrefix.CopyTo(result, 0);
                encrypted.CopyTo(result, DpPrefix.Length);
                return result;
            }
            catch (PlatformNotSupportedException)
            {
                // Wine reports as Windows but may not support DPAPI — fall through to AES
            }
        }

        return AesEncrypt(data, aesSalt);
    }

    /// <summary>
    /// Decrypt credentials back to plaintext. Auto-detects DPAPI vs AES format.
    /// Returns empty string on failure (e.g. migrated config from another machine).
    /// </summary>
    public static string Decrypt(byte[]? encrypted, byte[]? aesSalt)
    {
        if (encrypted == null || encrypted.Length == 0)
            return string.Empty;

        try
        {
            // Check prefix to determine encryption method
            if (encrypted.Length > 2 && encrypted[0] == DpPrefix[0] && encrypted[1] == DpPrefix[1])
            {
                // DPAPI-encrypted
                var ciphertext = new byte[encrypted.Length - DpPrefix.Length];
                Array.Copy(encrypted, DpPrefix.Length, ciphertext, 0, ciphertext.Length);
                var data = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            else if (encrypted.Length > 2 && encrypted[0] == AesPrefix[0] && encrypted[1] == AesPrefix[1])
            {
                // AES-encrypted — requires a valid salt
                if (aesSalt == null || aesSalt.Length == 0)
                {
                    Plugin.Log.Warning("[CredentialHelper] AES-encrypted credentials found but no salt — re-authentication required.");
                    return string.Empty;
                }
                return AesDecrypt(encrypted, aesSalt);
            }
            else
            {
                // Legacy format (pre-prefix DPAPI from before this change) — try DPAPI directly
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var data = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(data);
                }

                Plugin.Log.Warning("[CredentialHelper] Unrecognized credential format — re-authentication required.");
                return string.Empty;
            }
        }
        catch (CryptographicException)
        {
            Plugin.Log.Warning("[CredentialHelper] Failed to decrypt saved credentials — re-authentication required.");
            return string.Empty;
        }
        catch (PlatformNotSupportedException)
        {
            // DPAPI not available (e.g. Wine without DPAPI support, or cross-platform migration)
            Plugin.Log.Warning("[CredentialHelper] DPAPI unavailable on this platform — re-authentication required.");
            return string.Empty;
        }
    }

    /// <summary>
    /// Generate a cryptographically random salt for AES key derivation.
    /// Should be called once per install and stored in config.
    /// </summary>
    public static byte[] GenerateSalt()
    {
        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    // --- AES fallback for non-Windows platforms ---

    /// <summary>
    /// Derives a 256-bit key from the per-install salt.
    /// The salt is randomly generated once and stored in config, so the key
    /// is stable regardless of Wine prefix name, username, or hostname changes.
    /// </summary>
    private static byte[] DeriveAesKey(byte[] salt)
    {
        // PBKDF2 with a high iteration count for proper key stretching
        return Rfc2898DeriveBytes.Pbkdf2(
            Entropy, salt, iterations: 100_000, HashAlgorithmName.SHA256, outputLength: 32);
    }

    /// <summary>
    /// Encrypts plaintext using AES-256-CBC with PKCS7 padding.
    /// CBC mode and PKCS7 padding are the Aes.Create() defaults but
    /// stated explicitly here for clarity.
    /// </summary>
    private static byte[] AesEncrypt(byte[] plaintext, byte[] salt)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveAesKey(salt);
        aes.Mode = CipherMode.CBC;       // explicit (default)
        aes.Padding = PaddingMode.PKCS7;  // explicit (default)
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        // Format: "AE" (2 bytes) + IV (16 bytes) + ciphertext
        var result = new byte[AesPrefix.Length + aes.IV.Length + ciphertext.Length];
        AesPrefix.CopyTo(result, 0);
        aes.IV.CopyTo(result, AesPrefix.Length);
        ciphertext.CopyTo(result, AesPrefix.Length + aes.IV.Length);
        return result;
    }

    /// <summary>
    /// Decrypts AES-256-CBC encrypted data with PKCS7 padding.
    /// </summary>
    private static string AesDecrypt(byte[] encrypted, byte[] salt)
    {
        // Format: "AE" (2) + IV (16) + ciphertext
        const int ivLength = 16;
        var offset = AesPrefix.Length;

        if (encrypted.Length < offset + ivLength + 1)
            throw new CryptographicException("AES ciphertext too short");

        var iv = new byte[ivLength];
        Array.Copy(encrypted, offset, iv, 0, ivLength);

        var ciphertext = new byte[encrypted.Length - offset - ivLength];
        Array.Copy(encrypted, offset + ivLength, ciphertext, 0, ciphertext.Length);

        using var aes = Aes.Create();
        aes.Key = DeriveAesKey(salt);
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;       // explicit (default)
        aes.Padding = PaddingMode.PKCS7;  // explicit (default)

        using var decryptor = aes.CreateDecryptor();
        var plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Encoding.UTF8.GetString(plaintext);
    }
}
