using System;
using System.Security.Cryptography;
using System.Text;

namespace WinLens.Services;

/// <summary>
/// Encrypts API keys for settings.json with DPAPI, so the file only decrypts for the same
/// Windows user on the same PC (a copied or synced settings.json doesn't leak the key).
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WinLens.ApiKey.v1");

    public static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        try
        {
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The key, or null if there is none or it can't be decrypted (another user or PC).</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
