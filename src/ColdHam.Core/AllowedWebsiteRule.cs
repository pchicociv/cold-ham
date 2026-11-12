namespace ColdHam.Core;

/// <summary>An allow rule for an exact domain (plus www), or its entire subdomain tree.</summary>
public sealed record AllowedWebsiteRule(string Domain, bool IncludeSubdomains)
{
    public static AllowedWebsiteRule Parse(string value)
    {
        if (!RuleNormalizer.TryNormalizeAllowedDomain(value, out var normalized, out var error))
            throw new InvalidDataException(LocalizedText.Format("InvalidWebRule", error));
        return normalized.StartsWith("*.", StringComparison.Ordinal)
            ? new(normalized[2..], true) : new(normalized, false);
    }

    public bool Matches(string domain) =>
        string.Equals(domain, Domain, StringComparison.OrdinalIgnoreCase)
        || (IncludeSubdomains
            ? domain.EndsWith("." + Domain, StringComparison.OrdinalIgnoreCase)
            : !Domain.StartsWith("www.", StringComparison.Ordinal)
                && string.Equals(domain, "www." + Domain, StringComparison.OrdinalIgnoreCase));

    // Chromium's leading dot selects an exact host; without it, subdomains match too.
    public IEnumerable<string> BrowserHostFilters => IncludeSubdomains
        ? [Domain]
        : WebsiteRules.ExpandDomains([Domain]).Select(domain => "." + domain);
}
