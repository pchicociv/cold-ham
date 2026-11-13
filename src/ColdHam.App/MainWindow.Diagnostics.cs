using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using ColdHam.Core;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace ColdHam.App;

public partial class MainWindow
{
    private bool _diagnosing;
    private bool _resourceBusy;
    private string? _diagnosticSignature;
    private string? _resourceScope;
    private ResourceAnalysis? _resourceAnalysis;

    private string DiagnosticSignature() => JsonSerializer.Serialize(_configuration) + GetState().DesiredActive
        + _configurationError + _hostsError + _applicationError + _recoveryMessage + LocalizedText.Language;

    private void InvalidateTroubleshooting()
    {
        if (DiagnosticReport is null) return;
        if (_diagnosticSignature != DiagnosticSignature())
            DiagnosticReport.Text = LocalizedText.Get("DiagnosticRefreshNeeded");
        var scope = RuleKey(_configuration.AllowedWebsites) + _configuration.WebsiteMode + LocalizedText.Language;
        if (_resourceScope != scope)
        {
            _resourceScope = scope;
            if (_resourceAnalysis is not null) ShowResourceAnalysis(_resourceAnalysis);
        }
    }

    private async void RefreshDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosing || _applying || _saving) return;
        _diagnosing = true;
        RefreshDiagnosticButton.IsEnabled = false;
        var signature = DiagnosticSignature();
        var active = GetState().DesiredActive;
        var allowOnly = active && _configuration.WebsiteMode == WebsiteBlockingMode.AllowListedSites;
        var expectedHosts = active && !allowOnly ? _configuration.Websites.ToArray() : [];
        var allowed = _configuration.AllowedWebsites.ToArray();
        var report = new StringBuilder();
        report.AppendLine(LocalizedText.Format("DiagnosticTime", DateTime.Now.ToString("g", LocalizedText.Culture)));
        report.AppendLine(LocalizedText.Get(active ? "DiagnosticActive" : "DiagnosticInactive"));
        report.AppendLine(LocalizedText.Get(_configuration.WebsiteMode == WebsiteBlockingMode.AllowListedSites
            ? "WebModeAllowedWebsitesOnlyInChrome" : "WebModeBlockedWebsiteList"));
        report.AppendLine(LocalizedText.Get(_configurationError is null && !_loadFailed ? "DiagnosticSaved" : "DiagnosticSaveFailed"));
        if (_loadFailed) report.AppendLine(LocalizedText.Get("DiagnosticLoadFailed"));
        foreach (var error in new[] { _configurationError, _hostsError, _applicationError, _recoveryMessage })
            if (error is not null) report.AppendLine(error);
        try
        {
            try
            {
                var hosts = await _hosts.InspectAsync(expectedHosts);
                report.AppendLine();
                report.AppendLine(LocalizedText.Format("DiagnosticHosts", _hosts.HostsPath));
                report.AppendLine(LocalizedText.Get(hosts.MatchesExpected ? "DiagnosticHostsMatch" : "DiagnosticHostsMismatch"));
                report.AppendLine(LocalizedText.Get(hosts.ManagedSectionPresent ? "DiagnosticHostsPresent" : "DiagnosticHostsAbsent"));
                report.AppendLine(LocalizedText.Format("DiagnosticExternalHosts", hosts.ExternalMappingLines));
            }
            catch (Exception error) { report.AppendLine(LocalizedText.Format("DiagnosticReadFailed", "hosts", error.Message)); }
            try
            {
                var policies = await _browserPolicies.InspectAsync(allowOnly, allowed);
                report.AppendLine();
                foreach (var policy in policies.Entries)
                {
                    var browser = policy.Path.Contains("Google", StringComparison.Ordinal) ? "Chrome" : "Edge";
                    var name = policy.Path[(policy.Path.LastIndexOf('\\') + 1)..];
                    var status = policy.ForeignValues > 0 || policy.MachinePolicy ? "DiagnosticPolicyConflict"
                        : policy.MatchesExpected ? (allowOnly ? "DiagnosticPolicyWritten" : "DiagnosticPolicyAbsent")
                        : "DiagnosticPolicyMismatch";
                    report.AppendLine($"{browser} · {name}: {LocalizedText.Get(status)}");
                    report.AppendLine(LocalizedText.Format("DiagnosticPolicyCounts", policy.ValueCount, policy.ForeignValues,
                        LocalizedText.Get(policy.MachinePolicy ? "DiagnosticYes" : "DiagnosticNo")));
                }
                report.AppendLine(LocalizedText.Get(policies.RecoveryJournalPresent
                    ? (allowOnly ? "DiagnosticJournalActive" : "DiagnosticJournalPending") : "DiagnosticJournalAbsent"));
            }
            catch (Exception error) { report.AppendLine(LocalizedText.Format("DiagnosticReadFailed", "Chrome / Edge", error.Message)); }
            report.AppendLine();
            report.AppendLine(LocalizedText.Get("DiagnosticNextSteps"));
            report.AppendLine(LocalizedText.Get("DiagnosticLimits"));
            _diagnosticSignature = signature;
            DiagnosticReport.Text = signature == DiagnosticSignature() ? report.ToString() : LocalizedText.Get("DiagnosticRefreshNeeded");
        }
        finally { _diagnosing = false; RefreshDiagnosticButton.IsEnabled = true; }
    }

    private void AnalyzeResourceUrls_Click(object sender, RoutedEventArgs e)
    {
        if (_resourceBusy) return;
        try
        {
            ShowResourceAnalysis(WebResourceAnalysis.FromUrls(ResourceUrls.Text));
            ResourceUrls.Clear();
        }
        catch (InvalidDataException error) { ShowResourceError(error.Message); }
    }

    private async void ImportResourceHar_Click(object sender, RoutedEventArgs e)
    {
        if (_resourceBusy) return;
        var dialog = new OpenFileDialog { Filter = "HAR (*.har)|*.har", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _resourceBusy = true;
        try
        {
            // Bound the allocation before reading; FileShare.Read prevents concurrent writes.
            await using var stream = File.OpenRead(dialog.FileName);
            if (stream.Length > WebResourceAnalysis.MaximumBytes) throw new InvalidDataException(LocalizedText.Get("ResourceTooLarge"));
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes);
            var analysis = await Task.Run(() => WebResourceAnalysis.FromHar(bytes));
            ShowResourceAnalysis(analysis);
            ResourceUrls.Clear();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { ShowResourceError(error is InvalidDataException ? error.Message : LocalizedText.Get("ResourceReadFailed")); }
        finally { _resourceBusy = false; }
    }

    private void ShowResourceError(string message)
    {
        _resourceAnalysis = null;
        ResourceResultList.ItemsSource = null;
        ResourceSummary.Text = message;
    }

    private void ShowResourceAnalysis(ResourceAnalysis analysis)
    {
        _resourceAnalysis = analysis;
        var allowed = _configuration.AllowedWebsites.Select(AllowedWebsiteRule.Parse).ToArray();
        var rows = analysis.Domains.Select(domain => new ResourceCandidateRow(domain,
            allowed.Any(rule => rule.Matches(domain.Domain)))).ToArray();
        ResourceResultList.ItemsSource = rows;
        ResourceSummary.Text = LocalizedText.Format("ResourceSummary", rows.Length, analysis.Ignored)
            + " " + LocalizedText.Get(_configuration.WebsiteMode == WebsiteBlockingMode.AllowListedSites
                ? "ResourceCaution" : "ResourceModeDisabled");
    }

    private void ClearResourceAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (_resourceBusy) return;
        _resourceAnalysis = null;
        ResourceResultList.ItemsSource = null;
        ResourceSummary.Text = string.Empty;
        ResourceUrls.Clear();
    }

    private async void AddResourceDomains_Click(object sender, RoutedEventArgs e)
    {
        if (_resourceBusy || _saving || _applying || _loadFailed) return;
        var selected = (ResourceResultList.ItemsSource as IEnumerable<ResourceCandidateRow> ?? [])
            .Where(row => row.Selected && row.CanSelect).Select(row => row.Domain).ToArray();
        if (selected.Length == 0) { ResourceSummary.Text = LocalizedText.Get("ResourceSelectFirst"); return; }
        try
        {
            var next = RuleEditing.AddAllowed(_allowedWebsites.Select(row => row.Value), selected);
            if (MessageBox.Show(this, LocalizedText.Format("ResourceConfirm", string.Join("\n", selected)),
                LocalizedText.Get("ResourceAssistantTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (await CommitRuleRowsAsync(_allowedWebsites, next, RuleKind.AllowedWebsite))
            {
                AddActivity(LocalizedText.Format("ResourceAdded", selected.Length));
                if (_resourceAnalysis is not null) ShowResourceAnalysis(_resourceAnalysis);
            }
        }
        catch (InvalidDataException error) { ResourceSummary.Text = error.Message; }
    }
}

public sealed class ResourceCandidateRow(ResourceDomain resource, bool covered)
{
    public string Domain => resource.Domain;
    public bool Selected { get; set; }
    public bool CanSelect => !covered;
    public string Detail => LocalizedText.Format("ResourceDetail", resource.Requests,
        LocalizedText.Get(covered ? "ResourceCovered" : "ResourceNotCovered"),
        LocalizedText.Get(resource.HasNetworkFailure && resource.HasHttpError ? "ResourceMixedFailure"
            : resource.HasNetworkFailure ? "ResourceNetworkFailure" : resource.HasHttpError ? "ResourceHttpError" : "ResourceManualUrl"));
}
