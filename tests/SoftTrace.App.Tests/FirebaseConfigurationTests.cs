using System.IO;
using System.Reflection;
using SoftTrace.App;

namespace SoftTrace.App.Tests;

public sealed class FirebaseConfigurationTests
{
    [Fact]
    public void DesktopLoginConfigurationIsEmbeddedAndDoesNotRequireAUserFile()
    {
        var assembly = typeof(App).Assembly;
        using var stream = assembly.GetManifestResourceStream("SoftTrace.GoogleDesktopClientSecret");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var expected = reader.ReadToEnd().Trim();
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.False(expected.StartsWith("dpapi:", StringComparison.Ordinal));

        var configuration = assembly.GetType("SoftTrace.App.FirebaseConfiguration", throwOnError: true)!;
        var getSecret = configuration.GetMethod("GetGoogleDesktopClientSecret", BindingFlags.Public | BindingFlags.Static)!;
        var actual = (string)getSecret.Invoke(null, null)!;
        // Avoid including the client value in assertion output.
        var matchesEmbeddedResource = string.Equals(expected, actual, StringComparison.Ordinal);
        Assert.True(matchesEmbeddedResource, "The login configuration must match the embedded resource.");
    }
}
