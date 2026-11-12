namespace ColdHam.Core;

public static class BrowserPolicyPlan
{
    public const int MaximumEntries = 1000;

    public static string[] BuildAllowlist(IEnumerable<string> websites)
    {
        // Preserve internal browser pages, including the DevTools frontend, which
        // uses its own scheme and would otherwise be blocked by URLBlocklist = *.
        // Website exceptions cover only HTTP(S), with subdomains only when opted in.
        var patterns = new[] { "chrome://*", "edge://*", "chrome-untrusted://*", "devtools://*" }
            .Concat(websites.Select(AllowedWebsiteRule.Parse)
                .SelectMany(rule => rule.BrowserHostFilters)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .SelectMany(host => new[] { $"http://{host}", $"https://{host}" }))
            .ToArray();
        if (patterns.Length > MaximumEntries)
            throw new InvalidDataException(LocalizedText.Get("TheListExceedsTheChromeAndEdge"));
        return patterns;
    }
}
