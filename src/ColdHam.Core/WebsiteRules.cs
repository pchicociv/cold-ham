namespace ColdHam.Core;

public static class WebsiteRules
{
    /// <summary>Expands exact domains and their www variants before applying exceptions.</summary>
    public static string[] GetBlockedDomains(IEnumerable<string> websites, IEnumerable<string> allowedWebsites)
    {
        var blocked = ExpandDomains(websites);
        var allowed = ExpandDomains(allowedWebsites).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return blocked.Where(domain => !allowed.Contains(domain)).ToArray();
    }

    public static string[] ExpandDomains(IEnumerable<string> websites) => websites
        .Select(domain => RuleNormalizer.TryNormalizeDomain(domain, out var value, out var error)
            ? value : throw new InvalidDataException($"Regla web no válida: {error}"))
        .SelectMany(domain => domain.StartsWith("www.", StringComparison.Ordinal)
            ? new[] { domain } : new[] { domain, $"www.{domain}" })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
