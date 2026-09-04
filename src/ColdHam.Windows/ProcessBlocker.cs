using System.Diagnostics;
using ColdHam.Core;

namespace ColdHam.Windows;

public sealed record ProcessSweepResult(int Matched, int Terminated, IReadOnlyList<string> Errors);

public sealed class ProcessBlocker
{
    public ProcessSweepResult Sweep(IEnumerable<string> applicationRules)
    {
        var rules = applicationRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Select(rule => RuleNormalizer.TryNormalizeApplication(rule, out var normalized, out _)
                ? normalized
                : null)
            .Where(rule => rule is not null)
            .Select(rule => new ApplicationMatch(rule!))
            .ToArray();
        var errors = new List<string>();
        var matched = 0;
        var terminated = 0;

        if (rules.Length == 0)
        {
            return new ProcessSweepResult(0, 0, errors);
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || !Matches(process, rules))
                {
                    continue;
                }

                matched++;
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(false);
                        process.WaitForExit(1500);
                        terminated++;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    errors.Add($"Could not stop {process.ProcessName}: {exception.Message}");
                }
            }
        }

        return new ProcessSweepResult(matched, terminated, errors);
    }

    private static bool Matches(Process process, IReadOnlyList<ApplicationMatch> rules)
    {
        var processFileName = process.ProcessName + ".exe";
        foreach (var rule in rules)
        {
            if (!string.Equals(processFileName, rule.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!rule.IsFullPath)
            {
                return true;
            }

            try
            {
                if (string.Equals(process.MainModule?.FileName, rule.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Access to another process path can be denied; a full-path rule then does not match.
            }
        }

        return false;
    }

    private sealed class ApplicationMatch(string value)
    {
        public string Value { get; } = value;

        public string FileName { get; } = Path.GetFileName(value);

        public bool IsFullPath { get; } = Path.IsPathFullyQualified(value);
    }
}
