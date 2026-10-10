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
        // Desktop OAuth identifies a public installed app; user tokens remain DPAPI-protected.
        using var stream = typeof(FirebaseConfiguration).Assembly.GetManifestResourceStream(
            "SoftTrace.GoogleDesktopClientSecret")
            ?? throw new InvalidOperationException("当前程序缺少内置 Google 登录配置，请安装完整发布版。");
        using var reader = new StreamReader(stream);
        var clientSecret = reader.ReadToEnd().Trim();
        if (string.IsNullOrWhiteSpace(clientSecret) ||
            clientSecret.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("当前程序的内置 Google 登录配置无效，请安装完整发布版。");
        }
        return clientSecret;
    }
}
