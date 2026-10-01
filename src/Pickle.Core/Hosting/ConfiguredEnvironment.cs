using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Hosting;

/// <summary>Applies <c>shell.environment</c> and <c>shell.pathPrepend</c> to the process before the runspace opens, so every command and child sees them.</summary>
public static class ConfiguredEnvironment
{
    public static void Apply(ShellSettings shell, IPickleLogger log)
    {
        foreach (var (name, value) in shell.Environment)
        {
            if (!EnvironmentRules.IsValidName(name) || EnvironmentRules.IsProtected(name) || value.Any(char.IsControl))
            {
                log.Warn("environment", $"shell.environment: '{name}' is not applied (invalid or protected name, or control characters)");
                continue;
            }

            Environment.SetEnvironmentVariable(name, Environment.ExpandEnvironmentVariables(value));
        }

        var prepend = shell.PathPrepend
            .Select(p => Environment.ExpandEnvironmentVariables(p).Trim())
            .Where(p => p.Length > 0 && !p.Any(char.IsControl) && !p.Contains(PathList.Separator, StringComparison.Ordinal))
            .ToList();
        if (prepend.Count == 0)
        {
            return;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(PathList.Separator, StringSplitOptions.RemoveEmptyEntries);
        var front = prepend.Where(p => !current.Contains(p, StringComparer.FromComparison(comparison))).Distinct().ToList();
        if (front.Count > 0)
        {
            Environment.SetEnvironmentVariable("PATH", string.Join(PathList.Separator, front.Concat(current)));
        }
    }
}
