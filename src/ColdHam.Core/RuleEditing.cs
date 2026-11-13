namespace ColdHam.Core;

public enum RuleKind { BlockedWebsite, AllowedWebsite, Application }

public static class RuleEditing
{
    public static string Normalize(RuleKind kind, string input, bool includeSubdomains = false)
    {
        string normalized, error;
        var valid = kind switch
        {
            RuleKind.AllowedWebsite => RuleNormalizer.TryNormalizeAllowedDomain(input, out normalized, out error, includeSubdomains),
            RuleKind.Application => RuleNormalizer.TryNormalizeApplication(input, out normalized, out error),
            _ => RuleNormalizer.TryNormalizeDomain(input, out normalized, out error)
        };
        if (!valid) throw new InvalidDataException(error);
        return normalized;
    }

    public static string[] Replace(IReadOnlyList<string> rules, int index, string input, RuleKind kind, bool includeSubdomains = false)
    {
        if (index < 0 || index >= rules.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var value = Normalize(kind, input, includeSubdomains);
        if (rules.Where((_, i) => i != index).Any(rule =>
                string.Equals(Normalize(kind, rule), value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(LocalizedText.Get("RuleEditDuplicate"));
        var next = rules.ToArray();
        next[index] = value;
        if (kind == RuleKind.AllowedWebsite) BrowserPolicyPlan.BuildAllowlist(next);
        return next;
    }

    public static string[] AddAllowed(IEnumerable<string> existing, IEnumerable<string> additions)
    {
        var next = existing.Concat(additions.Select(value => Normalize(RuleKind.AllowedWebsite, value)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        BrowserPolicyPlan.BuildAllowlist(next);
        return next;
    }
}
