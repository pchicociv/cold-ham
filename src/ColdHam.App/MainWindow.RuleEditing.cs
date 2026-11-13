using System.Collections.ObjectModel;
using System.Windows;
using ColdHam.Core;
using MessageBox = System.Windows.MessageBox;

namespace ColdHam.App;

public partial class MainWindow
{
    private async void EditWebsiteItem_Click(object sender, RoutedEventArgs e) =>
        await EditRuleAsync(sender, _websites, RuleKind.BlockedWebsite);
    private async void EditAllowedWebsiteItem_Click(object sender, RoutedEventArgs e) =>
        await EditRuleAsync(sender, _allowedWebsites, RuleKind.AllowedWebsite);
    private async void EditApplicationItem_Click(object sender, RoutedEventArgs e) =>
        await EditRuleAsync(sender, _applications, RuleKind.Application);

    private async Task EditRuleAsync(object sender, ObservableCollection<RuleRow> rows, RuleKind kind)
    {
        if (_saving || _applying || _loading || _loadFailed || sender is not FrameworkElement { DataContext: RuleRow selected }) return;
        var index = rows.IndexOf(selected);
        if (index < 0) return;
        var editor = new RuleEditorWindow(kind, rows.Select(row => row.Value).ToArray(), index) { Owner = this };
        if (editor.ShowDialog() != true || editor.EditedRules is not { } next || next[index] == selected.Value) return;
        if (MessageBox.Show(this, LocalizedText.Format("RuleEditConfirm", selected.Value, next[index]),
            LocalizedText.Get("RuleEditTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (await CommitRuleRowsAsync(rows, next, kind))
            AddActivity(LocalizedText.Format("RuleEditApplied", selected.Value, next[index]));
    }

    private async Task<bool> CommitRuleRowsAsync(ObservableCollection<RuleRow> rows, string[] next, RuleKind kind)
    {
        if (_saving || _applying || _loading || _loadFailed) return false;
        var previous = rows.ToArray();
        rows.Clear();
        foreach (var value in next) rows.Add(kind == RuleKind.Application ? RuleRow.ForApplication(value) : RuleRow.ForWebsite(value));
        if (!await SaveAsync())
        {
            rows.Clear();
            foreach (var row in previous) rows.Add(row);
            CopyRulesToConfiguration();
            UpdateStatus();
            return false;
        }
        // The ordinary enforcement path handles active sessions, failures and process consent.
        await ApplyDesiredStateAsync(forceHosts: _effectiveActive);
        _diagnosticSignature = null;
        DiagnosticReport.Text = LocalizedText.Get("DiagnosticRefreshNeeded");
        return true;
    }
}
