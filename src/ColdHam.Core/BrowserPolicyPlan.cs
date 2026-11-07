namespace ColdHam.Core;

public static class BrowserPolicyPlan
{
    public const int MaximumEntries = 1000;

    public static string[] BuildAllowlist(IEnumerable<string> websites)
    {
        // Preserve internal browser pages. Website exceptions cover only HTTP(S)
        // and exact hosts; a leading dot disables implicit subdomain matching.
        var patterns = new[] { "chrome://*", "edge://*", "chrome-untrusted://*" }
            .Concat(WebsiteRules.ExpandDomains(websites)
                .SelectMany(domain => new[] { $"http://.{domain}", $"https://.{domain}" }))
            .ToArray();
        if (patterns.Length > MaximumEntries)
            throw new InvalidDataException(LocalizedText.Get("TheListExceedsTheChromeAndEdge"));
        return patterns;
    }
}
