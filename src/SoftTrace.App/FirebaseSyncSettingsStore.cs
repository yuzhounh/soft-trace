using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoftTrace.App;

public sealed class FirebaseReauthorizationRequiredException(Exception inner)
    : InvalidOperationException("Windows 无法解密原登录凭据。请点击“重新授权”，继续使用当前 Google 账号；本地记录和设置会保留。", inner);

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
        try
        {
            var protectedBytes = Convert.FromBase64String(protectedRefreshToken);
            var plaintext = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new FirebaseReauthorizationRequiredException(exception);
        }
    }
}
