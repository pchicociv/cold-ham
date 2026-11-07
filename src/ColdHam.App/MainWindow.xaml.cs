using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ColdHam.Core;
using ColdHam.Windows;
using Microsoft.Win32;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Color = System.Windows.Media.Color;

namespace ColdHam.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<RuleRow> _websites = [];
    private readonly ObservableCollection<RuleRow> _allowedWebsites = [];
    private readonly ObservableCollection<RuleRow> _applications = [];
    private readonly ObservableCollection<string> _activity = [];
    private readonly JsonConfigurationStore _store = new();
    private readonly HostsFileManager _hosts = new();
    private readonly BrowserPolicyManager _browserPolicies = new();
    private readonly ProcessBlocker _processBlocker = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TimeProvider _clock = TimeProvider.System;
    private BlockConfiguration _configuration = new();
    private bool _effectiveActive;
    private bool _loading = true;
    private bool _allowClose;
    private string? _lastError;
    private string? _hostsError;
    private string? _applicationError;
    private string? _configurationError;
    private string? _recoveryMessage;
    private string? _appliedWebRules;
    private string? _attemptedWebRules;
    private readonly ApplicationBlockConsent _consent = new();
    private bool _applicationsApproved;
    private bool _applying;
    private bool _loadFailed;
    private bool _saving;
    private bool _suppressEnforcementForExit;
    private bool _hideExplanationShown;

    public event EventHandler? BlockStateChanged;
    public event EventHandler<TrayNotificationEventArgs>? NotificationRaised;

    public string TrayStatus { get; private set; } = "Cold Ham";

    public MainWindow()
    {
        InitializeComponent();
        WebsiteList.ItemsSource = _websites;
        AllowedWebsiteList.ItemsSource = _allowedWebsites;
        ApplicationList.ItemsSource = _applications;
        ActivityList.ItemsSource = _activity;
        _timer.Tick += Timer_Tick;
        InitializeUpdates();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _configuration = await _store.LoadAsync();
            ApplyLanguage(_configuration.Language);
            _loadFailed = false;
            _configurationError = null;
            _websites.Clear();
            _allowedWebsites.Clear();
            _applications.Clear();
            _recoveryMessage = _store.RecoveryMessage;
            if (_recoveryMessage is not null) AddActivity(_recoveryMessage);
            foreach (var website in _configuration.Websites)
            {
                _websites.Add(RuleRow.ForWebsite(website));
            }

            foreach (var application in _configuration.Applications)
            {
                _applications.Add(RuleRow.ForApplication(application));
            }

            foreach (var website in _configuration.AllowedWebsites)
            {
                _allowedWebsites.Add(RuleRow.ForWebsite(website));
            }

            PopulateSchedule(_configuration.Schedule);
            OnlyAllowedWebsitesToggle.IsChecked = _configuration.WebsiteMode == WebsiteBlockingMode.AllowListedSites;
            _loading = false;
            AddActivity(LocalizedText.Format("ConfigurationLoadedFrom", _store.FilePath));
            await ApplyDesiredStateAsync(forceHosts: true);
            _timer.Start();
        }
        catch (Exception exception)
        {
            _loading = false;
            _configurationError = LocalizedText.Get("CouldNotLoadTheConfigurationCheckThe") + exception.Message;
            _loadFailed = true;
            ShowError(LocalizedText.Get("CouldNotLoadTheConfiguration"), exception);
        }

        UpdateStatus();
        await StartUpdateChecksAsync();
    }

    private void ApplyLanguage(string language)
    {
        LocalizedText.SetLanguage(language);
        ((App)Application.Current).UpdateLanguageResources();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(LocalizedText.Culture.Name);
        LanguageSelector.SelectedValue = LocalizedText.Language;
    }

    private async void LanguageSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || _saving || _applying || _loadFailed
            || LanguageSelector.SelectedValue is not string language || language == _configuration.Language) return;

        var previousLanguage = _configuration.Language;
        _configuration.Language = language;
        ApplyLanguage(language);
        if (!await SaveAsync())
        {
            _configuration.Language = previousLanguage;
            ApplyLanguage(previousLanguage);
        }

        if (ScheduleValidationText.Text.Length > 0)
        {
            TryReadSchedule(out _, out var error);
            ScheduleValidationText.Text = error;
        }
        UpdateStatus();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _configuration.ManualSession = BlockStateEvaluator.CreateUnlimitedSession(_clock);
        await StartSessionAsync(LocalizedText.Get("UnlimitedSessionStarted"));
    }

    private async void Start25Button_Click(object sender, RoutedEventArgs e) => await StartTimedSessionAsync(25);

    private async void Start50Button_Click(object sender, RoutedEventArgs e) => await StartTimedSessionAsync(50);

    private async void StartCustomButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SessionMinutesInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            MessageBox.Show(LocalizedText.Get("EnterADurationBetweenAndMinutes"), LocalizedText.Get("InvalidDuration"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await StartTimedSessionAsync(minutes);
    }

    private async Task StartTimedSessionAsync(int minutes)
    {
        if (!BlockStateEvaluator.TryCreateTimedSession(_clock, minutes, out var session, out var error))
        {
            MessageBox.Show(error, LocalizedText.Get("InvalidDuration"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _configuration.ManualSession = session;
        await StartSessionAsync(LocalizedText.Format("MinuteSessionStarted", minutes));
    }

    private async Task StartSessionAsync(string activity)
    {
        if (!await SaveAsync()) return;
        AddActivity(activity);
        await ApplyDesiredStateAsync(forceHosts: true);
    }

    private async void EndSessionButton_Click(object sender, RoutedEventArgs e) => await EndSessionAsync(confirm: true);

    private async Task EndSessionAsync(bool confirm)
    {
        if (_configuration.ManualSession is null) return;
        var state = GetState();
        if (confirm && MessageBox.Show(this,
                state.ScheduledActive
                    ? LocalizedText.Get("TheSessionWillEndButBlockingWill")
                    : LocalizedText.Get("TheSessionWillEndAndRulesWill"),
                LocalizedText.Get("EndSession"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _configuration.ManualSession = null;
        _consent.Reset();
        await SaveAsync();
        AddActivity(LocalizedText.Get("ManualSessionEnded"));
        await ApplyDesiredStateAsync(forceHosts: true, stopping: !state.ScheduledActive);
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopAllAsync(confirm: true);

    private async void ElevateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await SaveAsync()) return;
        var app = (App)Application.Current;
        app.ReleaseInstanceForElevation();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException(LocalizedText.Get("TheApplicationPathCouldNotBeFound")),
                UseShellExecute = true,
                Verb = "runas"
            });
            _allowClose = true;
            app.ExitApplication();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            if (!app.ReacquireInstanceAfterElevationCancellation())
            {
                _configurationError = LocalizedText.Get("CouldNotRecoverTheColdHamInstance");
            }
            AddActivity(LocalizedText.Get("RestartAsAdministratorWasCancelled"));
        }
        catch (Exception exception)
        {
            app.ReacquireInstanceAfterElevationCancellation();
            ShowError(LocalizedText.Get("CouldNotRestartAsAdministrator"), exception);
        }
    }

    private async void AddWebsiteButton_Click(object sender, RoutedEventArgs e) => await AddWebsiteAsync();

    private async void WebsiteInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await AddWebsiteAsync();
        }
    }

    private async Task AddWebsiteAsync()
    {
        if (!RuleNormalizer.TryNormalizeDomain(WebsiteInput.Text, out var normalized, out var error))
        {
            MessageBox.Show(error, LocalizedText.Get("InvalidWebsite"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_websites.Any(item => string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(LocalizedText.Get("ThatWebsiteIsAlreadyOnTheList"), LocalizedText.Get("DuplicateWebsite"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _websites.Add(RuleRow.ForWebsite(normalized));
        WebsiteInput.Clear();
        await RulesChangedAsync();
        AddActivity(LocalizedText.Format("WebsiteAdded", normalized));
    }

    private async void RemoveWebsiteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RuleRow selected })
        {
            return;
        }

        var result = MessageBox.Show(
            LocalizedText.Format("RemoveFromTheList", selected.Value),
            LocalizedText.Get("RemoveWebsite"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _websites.Remove(selected);
        await RulesChangedAsync();
        AddActivity(LocalizedText.Format("WebsiteRemoved", selected.Value));
    }

    private async void AddApplicationButton_Click(object sender, RoutedEventArgs e) => await AddApplicationAsync();

    private async void AddAllowedWebsiteButton_Click(object sender, RoutedEventArgs e) => await AddAllowedWebsiteAsync();

    private async void AllowedWebsiteInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddAllowedWebsiteAsync();
    }

    private async Task AddAllowedWebsiteAsync()
    {
        if (!RuleNormalizer.TryNormalizeDomain(AllowedWebsiteInput.Text, out var normalized, out var error))
        {
            MessageBox.Show(error, LocalizedText.Get("InvalidWebsite"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_allowedWebsites.Any(item => string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(LocalizedText.Get("ThatWebsiteIsAlreadyOnTheAllowed"), LocalizedText.Get("DuplicateWebsite"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try { BrowserPolicyPlan.BuildAllowlist(_allowedWebsites.Select(item => item.Value).Append(normalized)); }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(exception.Message, LocalizedText.Get("AllowedListFull"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _allowedWebsites.Add(RuleRow.ForWebsite(normalized));
        AllowedWebsiteInput.Clear();
        await RulesChangedAsync();
        AddActivity(LocalizedText.Format("AllowedWebsiteAdded", normalized));
    }

    private async void WebsiteModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var allowOnly = OnlyAllowedWebsitesToggle.IsChecked == true;
        if (allowOnly && MessageBox.Show(this,
                LocalizedText.Get("DuringASessionOrActiveScheduleWebsites"),
                LocalizedText.Get("AllowedWebsitesOnly"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            OnlyAllowedWebsitesToggle.IsChecked = false;
            return;
        }
        _configuration.WebsiteMode = allowOnly ? WebsiteBlockingMode.AllowListedSites : WebsiteBlockingMode.BlockListedSites;
        await RulesChangedAsync();
        AddActivity(allowOnly ? LocalizedText.Get("WebModeAllowedWebsitesOnlyInChrome") : LocalizedText.Get("WebModeBlockedWebsiteList"));
    }

    private async void RemoveAllowedWebsiteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RuleRow selected }) return;
        if (MessageBox.Show(this,
                LocalizedText.Format("RemoveFromTheAllowedWebsitesItWill", selected.Value),
                LocalizedText.Get("RemoveAllowedWebsite"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _allowedWebsites.Remove(selected);
        await RulesChangedAsync();
        AddActivity(LocalizedText.Format("AllowedWebsiteRemoved", selected.Value));
    }

    private async void ApplicationInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await AddApplicationAsync();
        }
    }

    private async void BrowseApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = LocalizedText.Get("WindowsApplicationsExeExe"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            ApplicationInput.Text = dialog.FileName;
            await AddApplicationAsync();
        }
    }

    private async Task AddApplicationAsync()
    {
        if (!RuleNormalizer.TryNormalizeApplication(ApplicationInput.Text, out var normalized, out var error))
        {
            MessageBox.Show(error, LocalizedText.Get("InvalidApplication"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_applications.Any(item => string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(LocalizedText.Get("ThatApplicationIsAlreadyOnTheList"), LocalizedText.Get("DuplicateApplication"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _applications.Add(RuleRow.ForApplication(normalized));
        ApplicationInput.Clear();
        await RulesChangedAsync();
        AddActivity(LocalizedText.Format("ApplicationAdded", Path.GetFileName(normalized)));
    }

    private async void RemoveApplicationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RuleRow selected })
        {
            return;
        }

        var result = MessageBox.Show(
            LocalizedText.Format("RemoveFromTheList", selected.DisplayName),
            LocalizedText.Get("RemoveApplication"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _applications.Remove(selected);
        await RulesChangedAsync();
        AddActivity(LocalizedText.Format("ApplicationRemoved", selected.DisplayName));
    }

    private async void ScheduleControlChanged(object sender, RoutedEventArgs e)
    {
        await SaveScheduleFromControlsAsync();
    }

    private async void ScheduleTime_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        await SaveScheduleFromControlsAsync();
    }

    private async void ScheduleTime_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SaveScheduleFromControlsAsync();
            Keyboard.ClearFocus();
        }
    }

    private async Task SaveScheduleFromControlsAsync()
    {
        if (_loading || _saving || _applying)
        {
            return;
        }

        if (!TryReadSchedule(out var schedule, out var error))
        {
            ScheduleValidationText.Text = error;
            UpdateScheduleSummary();
            return;
        }

        ScheduleValidationText.Text = string.Empty;
        _configuration.Schedule = schedule;
        if (!await SaveAsync()) return;
        await ApplyDesiredStateAsync(forceHosts: true);
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        _timer.Stop();
        try
        {
            await ApplyDesiredStateAsync();
        }
        finally
        {
            _timer.Start();
        }
    }

    private async Task RulesChangedAsync()
    {
        CopyRulesToConfiguration();
        if (!await SaveAsync()) return;
        await ApplyDesiredStateAsync(forceHosts: _effectiveActive);
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loadFailed)
        {
            Window_Loaded(sender, e);
            return;
        }
        if (!await SaveAsync()) return;
        _recoveryMessage = null;
        _consent.Reset();
        await ApplyDesiredStateAsync(forceHosts: true);
    }

    private static string RuleKey(IEnumerable<string> rules) =>
        string.Join("\n", rules.Order(StringComparer.OrdinalIgnoreCase)).ToLowerInvariant();

    private async Task ApplyDesiredStateAsync(bool forceHosts = false, bool stopping = false)
    {
        if (_applying || _saving || _loading || (_configurationError is not null && !stopping)) return;
        _applying = true;
        LanguageSelector.IsEnabled = false;
        MainTabs.IsEnabled = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        AdminBanner.IsEnabled = false;
        try
        {
            var state = GetState();
            if (state.SessionExpired)
            {
                _configuration.ManualSession = null;
                _consent.Reset();
                await SaveExpiredSessionAsync();
                AddActivity(LocalizedText.Get("TheManualSessionExpired"));
                RaiseNotification(LocalizedText.Get("SessionEnded"), state.ScheduledActive
                    ? LocalizedText.Get("TheSessionHasEnded")
                    : LocalizedText.Get("TheSessionHasEndedTheScheduleMay"));
                state = GetState();
            }

            var scheduled = state.ScheduledActive;
            var desired = !_suppressEnforcementForExit && state.DesiredActive;
            var stateChanged = desired != _effectiveActive;
            var allowOnly = desired && _configuration.WebsiteMode == WebsiteBlockingMode.AllowListedSites;
            var webRules = desired && !allowOnly ? WebsiteRules.GetBlockedDomains(_configuration.Websites, []) : [];
            var webKey = allowOnly ? "allow\n" + RuleKey(_configuration.AllowedWebsites) : RuleKey(webRules);
            if (forceHosts || _attemptedWebRules != webKey)
            {
                _attemptedWebRules = webKey;
                try
                {
                    var failures = new List<Exception>();
                    try
                    {
                        if (webRules.Length > 0) await _hosts.ApplyAsync(_configuration.Websites);
                        else await _hosts.RemoveAsync();
                    }
                    catch (Exception exception) { failures.Add(exception); }
                    try
                    {
                        if (allowOnly) await _browserPolicies.ApplyAsync(_configuration.AllowedWebsites);
                        else await _browserPolicies.RemoveAsync();
                    }
                    catch (Exception exception) { failures.Add(exception); }
                    if (failures.Count > 0) throw new AggregateException(failures);
                    _appliedWebRules = webKey;
                    _hostsError = null;
                    AddActivity(allowOnly ? LocalizedText.Get("AllowOnlyPoliciesWrittenForChromeAnd")
                        : webRules.Length > 0
                        ? LocalizedText.Format("WroteWebRuleSToHosts", webRules.Length)
                        : LocalizedText.Get("RemovalOfColdHamWebRulesVerified"));
                }
                catch (Exception exception)
                {
                    _hostsError = exception is UnauthorizedAccessException || exception is AggregateException aggregate && aggregate.InnerExceptions.Any(error => error is UnauthorizedAccessException)
                        ? LocalizedText.Get("CouldNotApplyOrRemoveWebRules")
                        : LocalizedText.Format("WebRulesErrorDetails", exception.Message);
                    ReportOnce(_hostsError);
                }
            }

            _effectiveActive = desired;
            if (stateChanged) AddActivity(desired ? LocalizedText.Get("BlockingActivationRequested") : LocalizedText.Get("BlockingDeactivationRequested"));
            _applicationError = null;
            var consentStatus = _consent.Evaluate(desired, _configuration.Applications);
            if (!desired || _configuration.Applications.Count == 0)
            {
                _applicationsApproved = false;
            }
            else if (_configuration.Applications.Count > 0)
            {
                var applications = _configuration.Applications.ToArray();
                _applicationsApproved = consentStatus == ApplicationConsentStatus.Approved;
                if (consentStatus == ApplicationConsentStatus.NeedsConfirmation)
                {
                    _applicationsApproved = MessageBox.Show(this,
                        LocalizedText.Get("ApplicationsMatchingTheseRulesWillBeClosed")
                        + string.Join("\n", applications)
                        + LocalizedText.Get("SaveYourWorkBeforeContinuingAuthorizeThese"),
                        LocalizedText.Get("ConfirmApplicationBlocking"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                        MessageBoxResult.No) == MessageBoxResult.Yes;
                    _consent.Decide(applications, _applicationsApproved);
                    if (!_applicationsApproved)
                    {
                        RaiseNotification(LocalizedText.Get("ClosuresPending"), LocalizedText.Get("ColdHamWillNotCloseApplicationsUntil"));
                    }
                }
                // A scheduled session can end while the confirmation dialog is open.
                if (!GetState().DesiredActive)
                {
                    _consent.Reset();
                    _applicationsApproved = false;
                    return;
                }
                if (_applicationsApproved)
                {
                    try
                    {
                        var sweep = await Task.Run(() => _processBlocker.Sweep(applications));
                        if (sweep.Terminated > 0) AddActivity(LocalizedText.Format("ClosedBlockedApplicationS", sweep.Terminated));
                        if (sweep.Errors.Count > 0)
                        {
                            _applicationError = string.Join("\n", sweep.Errors.Distinct());
                            ReportOnce(_applicationError);
                        }
                    }
                    catch (Exception exception)
                    {
                        _applicationError = LocalizedText.Format("CouldNotCheckApplicationBlocking", exception.Message);
                        ReportOnce(_applicationError);
                    }
                }
            }
        }
        finally
        {
            _applying = false;
            MainTabs.IsEnabled = true;
            AdminBanner.IsEnabled = true;
            UpdateStatus();
        }
    }

    private void PopulateSchedule(WeeklySchedule schedule)
    {
        ScheduleEnabled.IsChecked = schedule.Enabled;
        StartTimeInput.Text = schedule.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        EndTimeInput.Text = schedule.End.ToString("HH:mm", CultureInfo.InvariantCulture);
        MondayToggle.IsChecked = schedule.Days.Contains(DayOfWeek.Monday);
        TuesdayToggle.IsChecked = schedule.Days.Contains(DayOfWeek.Tuesday);
        WednesdayToggle.IsChecked = schedule.Days.Contains(DayOfWeek.Wednesday);
        ThursdayToggle.IsChecked = schedule.Days.Contains(DayOfWeek.Thursday);
        FridayToggle.IsChecked = schedule.Days.Contains(DayOfWeek.Friday);
        SaturdayToggle.IsChecked = schedule.Days.Contains(DayOfWeek.Saturday);
        SundayToggle.IsChecked = schedule.Days.Contains(DayOfWeek.Sunday);
    }

    private bool TryReadSchedule(out WeeklySchedule schedule, out string error)
    {
        schedule = new WeeklySchedule();
        error = string.Empty;

        if (!TimeOnly.TryParseExact(StartTimeInput.Text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(EndTimeInput.Text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            error = LocalizedText.Get("EnterTimesInHourHHMmFormat");
            return false;
        }

        var days = new List<DayOfWeek>();
        AddDayIfChecked(MondayToggle, DayOfWeek.Monday, days);
        AddDayIfChecked(TuesdayToggle, DayOfWeek.Tuesday, days);
        AddDayIfChecked(WednesdayToggle, DayOfWeek.Wednesday, days);
        AddDayIfChecked(ThursdayToggle, DayOfWeek.Thursday, days);
        AddDayIfChecked(FridayToggle, DayOfWeek.Friday, days);
        AddDayIfChecked(SaturdayToggle, DayOfWeek.Saturday, days);
        AddDayIfChecked(SundayToggle, DayOfWeek.Sunday, days);

        if (ScheduleEnabled.IsChecked == true && days.Count == 0)
        {
            error = LocalizedText.Get("SelectAtLeastOneDayToEnable");
            return false;
        }

        schedule = new WeeklySchedule
        {
            Enabled = ScheduleEnabled.IsChecked == true,
            Days = days,
            Start = start,
            End = end
        };
        return true;
    }

    private static void AddDayIfChecked(ToggleButton toggle, DayOfWeek day, ICollection<DayOfWeek> days)
    {
        if (toggle.IsChecked == true)
        {
            days.Add(day);
        }
    }

    private void CopyRulesToConfiguration()
    {
        _configuration.Websites = _websites.Select(item => item.Value).ToList();
        _configuration.AllowedWebsites = _allowedWebsites.Select(item => item.Value).ToList();
        _configuration.Applications = _applications.Select(item => item.Value).ToList();
    }

    private BlockState GetState() => BlockStateEvaluator.Evaluate(_configuration, _clock);

    private async Task SaveExpiredSessionAsync()
    {
        try
        {
            CopyRulesToConfiguration();
            await _store.SaveAsync(_configuration);
            _configurationError = null;
        }
        catch (Exception exception)
        {
            _configurationError = LocalizedText.Format("TheSessionHasEndedButItsEnd", exception.Message);
            ReportOnce(_configurationError);
        }
    }

    private async Task<bool> SaveAsync()
    {
        if (_loadFailed || _saving) return false;
        _saving = true;
        UpdateStatus();
        CopyRulesToConfiguration();
        try
        {
            await _store.SaveAsync(_configuration);
            _configurationError = null;
            return true;
        }
        catch (Exception exception)
        {
            _configurationError = LocalizedText.Format("CouldNotSaveTheConfigurationClickRetry", exception.Message);
            ReportOnce(_configurationError);
            return false;
        }
        finally { _saving = false; UpdateStatus(); }
    }

    private void UpdateStatus()
    {
        var state = GetState();
        var scheduled = state.ScheduledActive;
        var pendingConsent = _effectiveActive && _applications.Count > 0 && !_applicationsApproved;
        var hasError = _hostsError is not null || _applicationError is not null || _configurationError is not null;

        StatusDot.Fill = new SolidColorBrush(hasError || pendingConsent ? Color.FromRgb(181, 71, 8) : _effectiveActive
            ? Color.FromRgb(18, 183, 106)
            : Color.FromRgb(152, 162, 179));
        StatusTitleText.Text = hasError ? LocalizedText.Get("BlockingHasIssues") : pendingConsent ? LocalizedText.Get("BlockingAwaitsConfirmation")
            : _effectiveActive ? LocalizedText.Get("BlockingActive") : LocalizedText.Get("BlockingStopped");
        StatusText.Text = hasError ? LocalizedText.Get("CheckWebsiteAndApplicationStatusBeforeAssuming") : _effectiveActive
            ? scheduled && !state.ManualActive
                ? LocalizedText.Get("ActivatedByTheWeeklySchedule")
                : scheduled ? LocalizedText.Get("ActivatedByASessionAndTheWeekly") : LocalizedText.Get("ActivatedByAManualSession")
            : LocalizedText.Get("RulesAreReadyButAreNotBeing");

        var transition = state.NextScheduleTransition;
        if (state.ManualActive && _configuration.ManualSession?.EndsAtUtc is { } endsAt)
        {
            var remaining = state.Remaining ?? TimeSpan.Zero;
            var scheduleContinuation = scheduled ? LocalizedText.Get("TheScheduleIsAlsoActive") : string.Empty;
            NextChangeText.Text = LocalizedText.Format("SessionEnds", FormatRemaining(remaining), FormatTransition(endsAt.LocalDateTime), scheduleContinuation);
        }
        else if (state.ManualActive)
        {
            NextChangeText.Text = LocalizedText.Get("TheManualSessionHasNoEndTime");
        }
        else if (transition is not null)
        {
            NextChangeText.Text = scheduled
                ? LocalizedText.Format("Ends", FormatTransition(transition.Value.LocalDateTime))
                : LocalizedText.Format("NextBlock", FormatTransition(transition.Value.LocalDateTime));
        }
        else
        {
            NextChangeText.Text = _configuration.Schedule.Enabled
                ? LocalizedText.Get("CheckTheScheduledDaysAndTimes")
                : LocalizedText.Get("NoScheduleIsActive");
        }

        StartButton.IsEnabled = !_applying && !_saving && !_loadFailed && !state.ManualActive;
        Start25Button.IsEnabled = StartButton.IsEnabled;
        Start50Button.IsEnabled = StartButton.IsEnabled;
        StartCustomButton.IsEnabled = StartButton.IsEnabled;
        SessionMinutesInput.IsEnabled = StartButton.IsEnabled;
        EndSessionButton.IsEnabled = !_applying && !_saving && state.ManualActive;
        StopButton.IsEnabled = !_applying && !_saving && (state.ManualActive || _configuration.Schedule.Enabled || _effectiveActive || _hostsError is not null || _loadFailed);
        MainTabs.IsEnabled = !_applying && !_saving && !_loadFailed;
        LanguageSelector.IsEnabled = !_loading && !_applying && !_saving && !_loadFailed;
        AdminBanner.IsEnabled = !_applying && !_saving;
        RecoveryText.Text = string.Join("\n", new[] { _configurationError, _hostsError, _applicationError, _recoveryMessage,
            pendingConsent ? LocalizedText.Get("ApplicationClosuresAreNotAuthorizedClickRetry") : null }
            .Where(message => message is not null));
        AdminBanner.Visibility = RecoveryText.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        EnforcementText.Text = LocalizedText.Get("WebsitesStatusPrefix") + (_hostsError is not null ? LocalizedText.Get("UnconfirmedStatusRulesMayStillBeActive")
            : _appliedWebRules is null ? LocalizedText.Get("NotChecked") : _appliedWebRules.StartsWith("allow\n", StringComparison.Ordinal) ? LocalizedText.Get("AllowedOnlyPoliciesWrittenRestartChromeEdge")
            : _appliedWebRules.Length > 0 ? LocalizedText.Get("RulesWrittenToHosts") : LocalizedText.Get("NoActiveRulesRestartTheBrowserIf"))
            + LocalizedText.Get("ApplicationsStatusPrefix") + (_configurationError is not null ? LocalizedText.Get("MonitoringSuspended")
                : _applicationError is not null ? LocalizedText.Get("CheckFailed")
                : pendingConsent ? LocalizedText.Get("AwaitingAuthorization") : _effectiveActive && _applicationsApproved && _applications.Count > 0 ? LocalizedText.Get("MonitoringDetail") : LocalizedText.Get("NotMonitoring"));

        var websiteLabel = _websites.Count == 1 ? LocalizedText.Get("WebsiteCountSingular") : LocalizedText.Format("WebsiteCountPlural", _websites.Count);
        var applicationLabel = _applications.Count == 1 ? LocalizedText.Get("ApplicationCountSingular") : LocalizedText.Format("ApplicationCountPlural", _applications.Count);
        SummaryText.Text = _configuration.WebsiteMode == WebsiteBlockingMode.AllowListedSites
            ? LocalizedText.Format("AllowedWebsitesOnlyModeChromeAndEdge", _allowedWebsites.Count, applicationLabel)
            : LocalizedText.Format("AndOnTheBlockList", websiteLabel, applicationLabel);
        WebsiteCountText.Text = _websites.Count == 1 ? LocalizedText.Get("Rule") : LocalizedText.Format("Rules", _websites.Count);
        AllowedWebsiteCountText.Text = _allowedWebsites.Count == 1 ? LocalizedText.Get("AllowedWebsiteCountSingular") : LocalizedText.Format("AllowedWebsiteCountPlural", _allowedWebsites.Count);
        ApplicationCountText.Text = _applications.Count == 1 ? LocalizedText.Get("Rule") : LocalizedText.Format("Rules", _applications.Count);
        WebsiteEmptyText.Visibility = _websites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AllowedWebsiteEmptyText.Visibility = _allowedWebsites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplicationEmptyText.Visibility = _applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateRuleStatuses();
        UpdateScheduleSummary();
        UpdateActivityHeader();
        SessionSummaryText.Text = state.ManualActive
            ? _configuration.ManualSession?.EndsAtUtc is { } end
                ? LocalizedText.Format("SessionInProgressEnds", FormatRemaining(state.Remaining ?? TimeSpan.Zero), FormatTransition(end.LocalDateTime))
                : LocalizedText.Get("ManualSessionInProgressWithNoEnd")
            : scheduled
                ? LocalizedText.Get("NoManualSessionTheScheduleKeepsBlocking")
                : LocalizedText.Get("NoManualSessionIsInProgress");
        TrayStatus = StatusTitleText.Text + " · " + NextChangeText.Text;
        BlockStateChanged?.Invoke(this, EventArgs.Empty);
        UpdateUpdateStatus();
    }

    private void UpdateRuleStatuses()
    {
        var allowOnly = _configuration.WebsiteMode == WebsiteBlockingMode.AllowListedSites;
        var effectiveDomains = WebsiteRules.GetBlockedDomains(_configuration.Websites, []);
        var effectiveKey = RuleKey(effectiveDomains);
        foreach (var website in _websites)
        {
            if (_hostsError is not null || _configurationError is not null)
            {
                website.SetStatus(LocalizedText.Get("Unconfirmed"), "#B54708");
            }
            else if (allowOnly)
            {
                website.SetStatus(LocalizedText.Get("NotUsedInThisMode"), "#667085");
            }
            else if (!_effectiveActive)
            {
                website.SetStatus(LocalizedText.Get("Ready"), "#667085");
            }
            else if (_appliedWebRules == effectiveKey)
            {
                website.SetStatus(LocalizedText.Get("InHosts"), "#067647");
            }
            else
            {
                website.SetStatus(LocalizedText.Get("Pending"), "#B54708");
            }
        }

        foreach (var website in _allowedWebsites)
        {
            var status = _hostsError is not null || _configurationError is not null ? LocalizedText.Get("Unconfirmed")
                : !allowOnly ? LocalizedText.Get("ModeDisabled") : !_effectiveActive ? LocalizedText.Get("Ready")
                : _appliedWebRules == "allow\n" + RuleKey(_configuration.AllowedWebsites) ? LocalizedText.Get("InPolicies") : LocalizedText.Get("Pending");
            website.SetStatus(status, status == LocalizedText.Get("Unconfirmed") || status == LocalizedText.Get("Pending") ? "#B54708" : "#667085");
        }

        foreach (var application in _applications)
        {
            var status = _configurationError is not null ? LocalizedText.Get("NotMonitoringStatus") : !_effectiveActive ? LocalizedText.Get("Ready") : !_applicationsApproved ? LocalizedText.Get("NotAuthorized")
                : _applicationError is not null ? LocalizedText.Get("Review") : LocalizedText.Get("Monitoring");
            application.SetStatus(status, status == LocalizedText.Get("Monitoring") ? "#067647" : "#667085");
        }
    }

    private void UpdateScheduleSummary()
    {
        if (!TryReadSchedule(out var schedule, out _))
        {
            ScheduleSummaryText.Text = LocalizedText.Get("TheScheduleContainsValuesThatNeedCorrecting");
            OverviewScheduleText.Text = ScheduleSummaryText.Text;
            return;
        }

        var summary = FormatSchedule(schedule);
        ScheduleSummaryText.Text = summary;
        OverviewScheduleText.Text = summary;
    }

    private static string FormatSchedule(WeeklySchedule schedule)
    {
        if (!schedule.Enabled)
        {
            return LocalizedText.Get("TheScheduleIsDisabled");
        }

        var weekdays = new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
            DayOfWeek.Thursday, DayOfWeek.Friday
        };
        string daysText;
        if (schedule.Days.Count == 7)
        {
            daysText = LocalizedText.Get("EveryDay");
        }
        else if (schedule.Days.Count == 5 && weekdays.All(schedule.Days.Contains))
        {
            daysText = LocalizedText.Get("MondayToFriday");
        }
        else
        {
            daysText = string.Join(", ", schedule.Days
                .OrderBy(DayOrder)
                .Select(day => LocalizedText.Culture.DateTimeFormat.GetAbbreviatedDayName(day).TrimEnd('.')));
        }

        return schedule.Start == schedule.End
            ? LocalizedText.Format("AllDay", daysText)
            : LocalizedText.Format("ScheduleTimeRange", daysText, schedule.Start, schedule.End);
    }

    private static int DayOrder(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    private static string FormatTransition(DateTime transition)
    {
        if (transition.Date == DateTime.Today)
        {
            return LocalizedText.Format("TodayAtTime", transition);
        }

        if (transition.Date == DateTime.Today.AddDays(1))
        {
            return LocalizedText.Format("TomorrowAtTime", transition);
        }

        return LocalizedText.Format("WeekdayAtTime", transition.ToString("dddd", LocalizedText.Culture), transition);
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        var rounded = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, remaining.TotalSeconds)));
        return rounded.TotalHours >= 1
            ? LocalizedText.Format("RemainingHours", (int)rounded.TotalHours, rounded.Minutes, rounded.Seconds)
            : LocalizedText.Format("RemainingMinutes", rounded.Minutes, rounded.Seconds);
    }

    private void AddActivity(string message)
    {
        _activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (_activity.Count > 100)
        {
            _activity.RemoveAt(_activity.Count - 1);
        }

        UpdateActivityHeader();
    }

    private void UpdateActivityHeader() =>
        ActivityHeaderText.Text = _activity.Count == 1 ? LocalizedText.Get("ActivityEvent") : LocalizedText.Format("ActivityEvents", _activity.Count);

    private void ReportOnce(string message)
    {
        if (!string.Equals(_lastError, message, StringComparison.Ordinal))
        {
            _lastError = message;
            AddActivity(message);
            RaiseNotification(LocalizedText.Get("ColdHamNeedsAttention"), message);
        }
    }

    private void RaiseNotification(string title, string message) =>
        NotificationRaised?.Invoke(this, new TrayNotificationEventArgs(title, message));

    private void ShowError(string heading, Exception exception)
    {
        AddActivity($"{heading} {exception.Message}");
        MessageBox.Show($"{heading}\n\n{exception.Message}", "Cold Ham", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public async Task EndSessionFromTrayAsync() => await EndSessionAsync(confirm: true);

    public async Task StopAllFromTrayAsync() => await StopAllAsync(confirm: true);

    public async Task RequestExitFromTrayAsync()
    {
        if (_applying || _saving) return;
        var state = GetState();
        if (MessageBox.Show(this,
                state.DesiredActive || _hostsError is not null
                    ? LocalizedText.Get("ExitingWillEndTheSessionAndRemove")
                    : LocalizedText.Get("TheScheduleWillBeSavedButWill"),
                LocalizedText.Get("ExitColdHam"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _configuration.ManualSession = null;
        var saved = await SaveAsync();
        _suppressEnforcementForExit = true;
        _timer.Stop();
        await ApplyDesiredStateAsync(forceHosts: true, stopping: true);
        if (_hostsError is not null)
        {
            _suppressEnforcementForExit = false;
            _timer.Start();
            await ApplyDesiredStateAsync(forceHosts: true);
            ShowFromTray();
            return;
        }

        if (!saved && MessageBox.Show(this,
            LocalizedText.Get("RulesHaveBeenRemovedButTheEnd"),
            LocalizedText.Get("UnsavedConfiguration"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            _suppressEnforcementForExit = false;
            _timer.Start();
            return;
        }

        _allowClose = true;
        ((App)Application.Current).ExitApplication();
    }

    private async Task StopAllAsync(bool confirm)
    {
        if (confirm && MessageBox.Show(this,
            LocalizedText.Get("TheSessionWillEndAndTheSchedule"),
            LocalizedText.Get("StopAll"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _configuration.ManualSession = null;
        _configuration.Schedule.Enabled = false;
        ScheduleEnabled.IsChecked = false;
        _consent.Reset();
        await SaveAsync();
        AddActivity(LocalizedText.Get("TheSessionWasStoppedAndTheSchedule"));
        await ApplyDesiredStateAsync(forceHosts: true, stopping: true);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (!((App)Application.Current).TrayAvailable)
        {
            _ = RequestExitFromTrayAsync();
            return;
        }
        if (!_hideExplanationShown)
        {
            _hideExplanationShown = true;
            MessageBox.Show(this,
                LocalizedText.Get("ColdHamWillKeepRunningFromThe"),
                LocalizedText.Get("ColdHamIsStillRunning"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Hide();
        AddActivity(LocalizedText.Get("TheWindowWasHiddenInTheSystem"));
    }

}

public sealed class TrayNotificationEventArgs(string title, string message) : EventArgs
{
    public string Title { get; } = title;
    public string Message { get; } = message;
}

public sealed class RuleRow : INotifyPropertyChanged
{
    private string _status = LocalizedText.Get("Ready");
    private string _statusBrush = "#667085";

    private RuleRow(string value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Value { get; }

    public string DisplayName { get; }

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public string StatusBrush
    {
        get => _statusBrush;
        private set
        {
            _statusBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
        }
    }

    public static RuleRow ForWebsite(string value) => new(value, value);

    public static RuleRow ForApplication(string value) => new(value, Path.GetFileName(value));

    public void SetStatus(string status, string brush)
    {
        if (!string.Equals(Status, status, StringComparison.Ordinal))
        {
            Status = status;
        }

        if (!string.Equals(StatusBrush, brush, StringComparison.Ordinal))
        {
            StatusBrush = brush;
        }
    }
}
