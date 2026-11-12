using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace ColdHam.Core;

public static partial class RuleNormalizer
{
    private static readonly HashSet<string> ProtectedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "coldham.app.exe", "coldham.exe", "system.exe", "idle.exe", "registry.exe", "smss.exe", "csrss.exe",
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
            error = LocalizedText.Get("EnterADomainForExampleRedditCom");
            return false;
        }

        if (value.Contains('*'))
        {
            error = LocalizedText.Get("WildcardsAreNotSupportedByWindowsHosts");
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = $"https://{value}";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = LocalizedText.Get("TheWebsiteAddressIsNotValid");
            return false;
        }

        var host = uri.Host.TrimEnd('.');
        if (IPAddress.TryParse(host, out _))
        {
            error = LocalizedText.Get("EnterADomainNameRatherThanAn");
            return false;
        }

        try
        {
            host = new IdnMapping().GetAscii(host).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            error = LocalizedText.Get("TheDomainContainsUnsupportedCharacters");
            return false;
        }

        if (host.Length > 253 || !DomainRegex().IsMatch(host))
        {
            error = LocalizedText.Get("TheDomainNameIsNotValid");
            return false;
        }

        normalized = host;
        return true;
    }

    public static bool TryNormalizeAllowedDomain(string? input, out string normalized, out string error,
        bool includeSubdomains = false)
    {
        var value = input?.Trim() ?? string.Empty;
        if (value.StartsWith("*.", StringComparison.Ordinal))
        {
            includeSubdomains = true;
            value = value[2..];
            // The wildcard form accepts a domain only, never a URL or credentials.
            if (value.IndexOfAny(['/', '\\', ':', '?', '#', '@']) >= 0)
            {
                normalized = string.Empty;
                error = LocalizedText.Get("InvalidAllowedDomainPattern");
                return false;
            }
        }
        if (value.Contains('*'))
        {
            normalized = string.Empty;
            error = LocalizedText.Get("InvalidAllowedDomainPattern");
            return false;
        }
        if (!TryNormalizeDomain(value, out normalized, out error)) return false;
        if (includeSubdomains) normalized = "*." + normalized;
        return true;
    }

    public static bool TryNormalizeApplication(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        var value = (input ?? string.Empty).Trim().Trim('"');

        if (value.IndexOfAny(['*', '?', '<', '>', '|']) >= 0 || value.Any(char.IsControl))
        {
            error = LocalizedText.Get("TheExecutablePathIsNotValid");
            return false;
        }

        if (value.Length == 0)
        {
            error = LocalizedText.Get("EnterAnExecutableNameOrChooseAn");
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
            error = LocalizedText.Get("TheExecutablePathIsNotValid");
            return false;
        }

        if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = LocalizedText.Get("OnlyWindowsExeApplicationsCanBeBlocked");
            return false;
        }

        var fileName = Path.GetFileName(value);
        if (ProtectedExecutables.Contains(fileName))
        {
            error = LocalizedText.Format("IsProtectedAndCannotBeAdded", fileName);
            return false;
        }

        normalized = value;
        return true;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex DomainRegex();
}
