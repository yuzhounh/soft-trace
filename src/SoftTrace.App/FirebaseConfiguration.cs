using System.IO;

namespace SoftTrace.App;

internal static class FirebaseConfiguration
{
    public const string ProjectId = "soft-trace";
    public const string ApiKey = "AIzaSyCyYpWB4oDmRBjyh6Q89RHlZmjVXMbqhw0";
    public const string GoogleDesktopClientId =
        "858706061748-e439iq9q1773pco9q4u465nvjq8hbakj.apps.googleusercontent.com";

    public static string GetGoogleDesktopClientSecret()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoftTrace",
            "firebase-oauth-secret.txt");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("缺少 Google OAuth 客户端配置，请重新配置桌面登录。");
        }

        var storedValue = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            throw new InvalidOperationException("Google OAuth 客户端配置无效。");
        }

        const string protectedPrefix = "dpapi:";
        if (storedValue.StartsWith(protectedPrefix, StringComparison.Ordinal))
        {
            return FirebaseSyncSettingsStore.UnprotectRefreshToken(
                storedValue[protectedPrefix.Length..]);
        }

        File.WriteAllText(
            path,
            protectedPrefix + FirebaseSyncSettingsStore.ProtectRefreshToken(storedValue));
        return storedValue;
    }
}
