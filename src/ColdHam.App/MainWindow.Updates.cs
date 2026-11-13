using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ColdHam.Core;
using Application = System.Windows.Application;

namespace ColdHam.App;

public partial class MainWindow
{
    private readonly UpdateCoordinator _updates = new(new VelopackUpdateBackend(), TimeProvider.System);
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _updateCancellation = new();
    private bool _updateChecksStarted;

    private void InitializeUpdates()
    {
        _updates.Changed += (_, _) => UpdateUpdateStatus();
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync();
        UpdateUpdateStatus();
    }

    private async Task StartUpdateChecksAsync()
    {
        if (_updateChecksStarted) return;
        _updateChecksStarted = true;
        _updateTimer.Start();
        await CheckUpdatesAsync();
    }

    internal void StopUpdateChecks()
    {
        _updateTimer.Stop();
        _updateCancellation.Cancel();
    }

    private async Task CheckUpdatesAsync(bool force = false)
    {
        if (_updateCancellation.IsCancellationRequested) return;
        await _updates.CheckAsync(force, _updateCancellation.Token);
        if (_updateCancellation.IsCancellationRequested) return;
        UpdateUpdateStatus();
        // A visible window may contain ongoing edits or a modal confirmation.
        if (!IsVisible) TryInstallUpdate();
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(force: true);

    private void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        TryInstallUpdate();
        UpdateUpdateStatus();
    }

    private bool CanRestartForUpdate()
    {
        var unsaved = WebsiteInput.Text.Length > 0 || AllowedWebsiteInput.Text.Length > 0 || ApplicationInput.Text.Length > 0
            || ResourceUrls.Text.Length > 0 || _resourceAnalysis is not null
            || !TryReadSchedule(out var schedule, out _)
            || schedule.Enabled != _configuration.Schedule.Enabled
            || schedule.Start != _configuration.Schedule.Start || schedule.End != _configuration.Schedule.End
            || !schedule.Days.ToHashSet().SetEquals(_configuration.Schedule.Days);
        return UpdateRestartPolicy.CanRestart(_configuration, _clock, new UpdateSafetyState(
            Busy: _loading || _saving || _applying || _resourceBusy || _diagnosing || !IsEnabled,
            EnforcementActive: _effectiveActive,
            WebRulesRemoved: _appliedWebRules == string.Empty,
            HasErrors: _loadFailed || _configurationError is not null || _hostsError is not null
                || _applicationError is not null || _recoveryMessage is not null,
            HasUnsavedInput: unsaved,
            Exiting: _allowClose || _suppressEnforcementForExit || _updateCancellation.IsCancellationRequested));
    }

    private void TryInstallUpdate()
    {
        if (!_updates.TryPrepareRestart(CanRestartForUpdate)) return;
        _timer.Stop();
        _allowClose = true;
        ((App)Application.Current).ExitApplication();
    }

    private void UpdateUpdateStatus()
    {
        VersionText.Text = "Cold Ham " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        UpdateStatusText.Text = LocalizedText.Get("Update" + _updates.Stage);
        if (_updates.Stage == UpdateStage.Ready && !CanRestartForUpdate())
            UpdateStatusText.Text += " " + LocalizedText.Get("UpdateWaiting");
        UpdateStatusText.ToolTip = _updates.Error;
        CheckUpdatesButton.IsEnabled = !_updates.Busy && _updates.Stage is not (UpdateStage.Unavailable or UpdateStage.Ready);
        InstallUpdateButton.IsEnabled = _updates.Stage == UpdateStage.Ready && CanRestartForUpdate();
    }
}
