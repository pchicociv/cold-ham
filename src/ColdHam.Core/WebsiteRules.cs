namespace ColdHam.Core;

public static class WebsiteRules
{
    /// <summary>Expands blocked hosts, then excludes exact or subdomain allow rules.</summary>
    public static string[] GetBlockedDomains(IEnumerable<string> websites, IEnumerable<string> allowedWebsites)
    {
        var blocked = ExpandDomains(websites);
        var allowed = allowedWebsites.Select(AllowedWebsiteRule.Parse).ToArray();
        return blocked.Where(domain => !allowed.Any(rule => rule.Matches(domain))).ToArray();
    }

    public static string[] ExpandDomains(IEnumerable<string> websites) => websites
        .Select(domain => RuleNormalizer.TryNormalizeDomain(domain, out var value, out var error)
            ? value : throw new InvalidDataException(LocalizedText.Format("InvalidWebRule", error)))
        .SelectMany(domain => domain.StartsWith("www.", StringComparison.Ordinal)
            ? new[] { domain } : new[] { domain, $"www.{domain}" })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
