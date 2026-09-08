using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace ColdHam.Core;

public static partial class RuleNormalizer
{
    private static readonly HashSet<string> ProtectedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "coldham.app.exe", "system.exe", "idle.exe", "registry.exe", "smss.exe", "csrss.exe",
        "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "svchost.exe", "dwm.exe",
        "explorer.exe", "taskmgr.exe", "conhost.exe", "fontdrvhost.exe", "sihost.exe",
        "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe"
    };

    public static bool TryNormalizeDomain(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        var value = input?.Trim() ?? string.Empty;

        if (value.Length == 0 || value.Any(char.IsControl))
        {
            error = "Enter a domain, for example reddit.com.";
            return false;
        }

        if (value.Contains('*'))
        {
            error = "Wildcards are not supported by Windows hosts-file blocking.";
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = $"https://{value}";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "The website address is not valid.";
            return false;
        }

        var host = uri.Host.TrimEnd('.');
        if (IPAddress.TryParse(host, out _))
        {
            error = "Enter a domain name rather than an IP address.";
            return false;
        }

        try
        {
            host = new IdnMapping().GetAscii(host).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            error = "The domain contains unsupported characters.";
            return false;
        }

        if (host.Length > 253 || !DomainRegex().IsMatch(host))
        {
            error = "The domain name is not valid.";
            return false;
        }

        normalized = host;
        return true;
    }

    public static bool TryNormalizeApplication(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        var value = (input ?? string.Empty).Trim().Trim('"');

        if (value.IndexOfAny(['*', '?', '<', '>', '|']) >= 0 || value.Any(char.IsControl))
        {
            error = "The executable path is not valid.";
            return false;
        }

        if (value.Length == 0)
        {
            error = "Enter an executable name or choose an .exe file.";
            return false;
        }

        try
        {
            if (value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
            {
                value = Path.GetFullPath(value);
            }
            else if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value += ".exe";
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The executable path is not valid.";
            return false;
        }

        if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only Windows .exe applications can be blocked.";
            return false;
        }

        var fileName = Path.GetFileName(value);
        if (ProtectedExecutables.Contains(fileName))
        {
            error = $"{fileName} is protected and cannot be added.";
            return false;
        }

        normalized = value;
        return true;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex DomainRegex();
}
