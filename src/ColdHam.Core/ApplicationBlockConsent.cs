namespace ColdHam.Core;

public enum ApplicationConsentStatus { NotRequired, NeedsConfirmation, Denied, Approved }

/// <summary>Consent lasts only for the current active session and exact set of rules.</summary>
public sealed class ApplicationBlockConsent
{
    private string? _decidedRules;
    private bool _approved;

    public ApplicationConsentStatus Evaluate(bool active, IEnumerable<string> rules)
    {
        var key = Key(rules);
        if (!active || key.Length == 0)
        {
            Reset();
            return ApplicationConsentStatus.NotRequired;
        }
        if (_decidedRules != key) return ApplicationConsentStatus.NeedsConfirmation;
        return _approved ? ApplicationConsentStatus.Approved : ApplicationConsentStatus.Denied;
    }

    public void Decide(IEnumerable<string> rules, bool approved)
    {
        _decidedRules = Key(rules);
        _approved = approved;
    }

    public void Reset()
    {
        _decidedRules = null;
        _approved = false;
    }

    private static string Key(IEnumerable<string> rules) => string.Join("\n",
        rules.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)).ToLowerInvariant();
}
