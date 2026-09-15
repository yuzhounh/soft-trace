using System.Text;

namespace SoftTrace.Core;

public sealed partial class ActivityStore
{
    private static readonly HashSet<string> GenericHostProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "conhost", "cscript", "dllhost", "dotnet", "java", "javaw", "mmc",
        "mshta", "node", "powershell", "pwsh", "rundll32", "svchost", "wscript"
    };

    private static IReadOnlyList<UsageSummary> ConsolidateUsageSummaries(
        IReadOnlyCollection<UsageSummary> summaries)
    {
        var consolidated = new List<UsageSummary>();
        foreach (var processGroup in summaries.GroupBy(
                     summary => summary.ProcessName,
                     StringComparer.OrdinalIgnoreCase))
        {
            var nameGroups = processGroup
                .GroupBy(summary => NormalizeDisplayLabel(summary.AppName))
                .Select(group => AggregateSummaries(group))
                .ToArray();
            if (nameGroups.Length == 1)
            {
                consolidated.Add(nameGroups[0]);
                continue;
            }

            var knownPaths = processGroup
                .Select(summary => summary.ExecutablePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (knownPaths.Length == 1 &&
                !IsTemporaryPath(knownPaths[0]!) &&
                !GenericHostProcesses.Contains(processGroup.Key))
            {
                consolidated.Add(AggregateSummaries(nameGroups));
                continue;
            }

            var normalizedProcessName = NormalizeDisplayLabel(processGroup.Key);
            var explicitNames = nameGroups
                .Where(summary => NormalizeDisplayLabel(summary.AppName) != normalizedProcessName)
                .ToArray();
            var fallbackNames = nameGroups
                .Where(summary => NormalizeDisplayLabel(summary.AppName) == normalizedProcessName)
                .ToArray();

            if (fallbackNames.Length > 0 && explicitNames.Length == 1)
            {
                consolidated.Add(AggregateSummaries(nameGroups, explicitNames[0]));
            }
            else
            {
                consolidated.AddRange(nameGroups);
            }
        }

        return consolidated
            .OrderByDescending(summary => summary.Duration)
            .ThenBy(summary => summary.AppName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static UsageSummary AggregateSummaries(
        IEnumerable<UsageSummary> source,
        UsageSummary? preferredRepresentative = null)
    {
        var summaries = source.ToArray();
        var representative = preferredRepresentative ?? summaries
            .OrderByDescending(summary => summary.Duration)
            .ThenByDescending(summary => !string.IsNullOrWhiteSpace(summary.ExecutablePath) && File.Exists(summary.ExecutablePath))
            .ThenByDescending(summary => !string.IsNullOrWhiteSpace(summary.ExecutablePath))
            .First();
        var executablePath = summaries
            .Where(summary => !string.IsNullOrWhiteSpace(summary.ExecutablePath) && File.Exists(summary.ExecutablePath))
            .OrderByDescending(summary => summary.Duration)
            .Select(summary => summary.ExecutablePath)
            .FirstOrDefault()
            ?? summaries
            .Where(summary => !string.IsNullOrWhiteSpace(summary.ExecutablePath))
            .OrderByDescending(summary => summary.Duration)
            .Select(summary => summary.ExecutablePath)
            .FirstOrDefault();
        return new UsageSummary(
            representative.AppName,
            representative.ProcessName,
            executablePath,
            TimeSpan.FromTicks(summaries.Sum(summary => summary.Duration.Ticks)),
            summaries.Sum(summary => summary.SegmentCount));
    }

    private static string NormalizeDisplayLabel(string value)
    {
        value = value.Trim();
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        var normalized = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
        }
        return normalized.ToString();
    }

    private static bool IsTemporaryPath(string path) =>
        path.Contains("\\AppData\\Local\\Temp\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\Windows\\Temp\\", StringComparison.OrdinalIgnoreCase);
}
