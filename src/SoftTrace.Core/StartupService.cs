using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SoftTrace.Core;

[SupportedOSPlatform("windows")]
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoftTrace";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
            var entryAssembly = Environment.GetCommandLineArgs().FirstOrDefault();
            var command = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
                          && !string.IsNullOrWhiteSpace(entryAssembly)
                ? $"\"{executable}\" \"{entryAssembly}\""
                : $"\"{executable}\"";
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
