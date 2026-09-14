using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoftTrace.App;

public sealed record FirebaseSyncSettings(
    bool Enabled,
    string? Email,
    string? DisplayName,
    string? PhotoUrl,
    string? UserId,
    string? ProtectedRefreshToken)
{
    public static FirebaseSyncSettings Empty { get; } = new(false, null, null, null, null, null);

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(UserId) &&
        !string.IsNullOrWhiteSpace(ProtectedRefreshToken);
}

public sealed class FirebaseSyncSettingsStore(string settingsPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath { get; } = settingsPath;

    public FirebaseSyncSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<FirebaseSyncSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                    ?? FirebaseSyncSettings.Empty
                : FirebaseSyncSettings.Empty;
        }
        catch
        {
            return FirebaseSyncSettings.Empty;
        }
    }

    public void Save(FirebaseSyncSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public static string ProtectRefreshToken(string refreshToken)
    {
        var plaintext = Encoding.UTF8.GetBytes(refreshToken);
        var protectedBytes = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectRefreshToken(string protectedRefreshToken)
    {
        var protectedBytes = Convert.FromBase64String(protectedRefreshToken);
        var plaintext = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plaintext);
    }
}
