using System.Diagnostics;
using System.Text;
using ColdHam.Core;

namespace ColdHam.Windows;

public sealed class HostsFileManager
{
    public const string BeginMarker = "# Cold Ham BEGIN";
    public const string EndMarker = "# Cold Ham END";

    public HostsFileManager(string? hostsPath = null)
    {
        HostsPath = hostsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers",
            "etc",
            "hosts");
    }

    public string HostsPath { get; }

    public async Task ApplyAsync(IEnumerable<string> domains, CancellationToken cancellationToken = default)
    {
        var existing = File.Exists(HostsPath)
            ? await File.ReadAllTextAsync(HostsPath, cancellationToken)
            : string.Empty;
        var updated = BuildContent(existing, domains);
        await File.WriteAllTextAsync(HostsPath, updated, new UTF8Encoding(false), cancellationToken);
        FlushDnsCache();
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(HostsPath))
        {
            return;
        }

        var existing = await File.ReadAllTextAsync(HostsPath, cancellationToken);
        var updated = RemoveManagedSection(existing);
        if (!string.Equals(existing, updated, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(HostsPath, updated, new UTF8Encoding(false), cancellationToken);
            FlushDnsCache();
        }
    }

    public static string BuildContent(string existing, IEnumerable<string> domains)
    {
        var clean = RemoveManagedSection(existing).TrimEnd('\r', '\n');
        var normalizedDomains = new List<string>();
        foreach (var domain in domains)
        {
            if (!RuleNormalizer.TryNormalizeDomain(domain, out var normalized, out var error))
            {
                throw new InvalidDataException($"Invalid website rule '{domain}': {error}");
            }

            normalizedDomains.Add(normalized);
        }

        var rules = normalizedDomains
            .SelectMany(domain => domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? new[] { domain }
                : new[] { domain, $"www.{domain}" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .SelectMany(domain => new[] { $"0.0.0.0 {domain}", $"::1 {domain}" });

        var section = string.Join(Environment.NewLine, new[] { BeginMarker }
            .Concat(rules)
            .Concat(new[] { EndMarker }));

        return clean.Length == 0
            ? section + Environment.NewLine
            : clean + Environment.NewLine + Environment.NewLine + section + Environment.NewLine;
    }

    public static string RemoveManagedSection(string content)
    {
        var begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = content.IndexOf(EndMarker, StringComparison.Ordinal);

        if (begin < 0 && end < 0)
        {
            return content;
        }

        if (begin < 0 || end < begin)
        {
            throw new InvalidDataException("The Cold Ham section in the hosts file is incomplete. Repair the marker pair before continuing.");
        }

        var afterEnd = end + EndMarker.Length;
        if (afterEnd < content.Length && content[afterEnd] == '\r')
        {
            afterEnd++;
        }

        if (afterEnd < content.Length && content[afterEnd] == '\n')
        {
            afterEnd++;
        }

        var before = content[..begin].TrimEnd('\r', '\n');
        var after = content[afterEnd..].TrimStart('\r', '\n');

        if (before.Length == 0)
        {
            return after;
        }

        if (after.Length == 0)
        {
            return before + Environment.NewLine;
        }

        return before + Environment.NewLine + after;
    }

    private static void FlushDnsCache()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ipconfig.exe",
                Arguments = "/flushdns",
                CreateNoWindow = true,
                UseShellExecute = false
            })?.WaitForExit(3000);
        }
        catch
        {
            // The hosts change still takes effect; DNS cache flushing is best-effort.
        }
    }
}
