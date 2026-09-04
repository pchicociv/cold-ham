using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ColdHam.Core;
using ColdHam.Windows;
using Microsoft.Win32;

namespace ColdHam.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _websites = [];
    private readonly ObservableCollection<string> _applications = [];
    private readonly ObservableCollection<string> _activity = [];
    private readonly JsonConfigurationStore _store = new();
    private readonly HostsFileManager _hosts = new();
    private readonly ProcessBlocker _processBlocker = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private BlockConfiguration _configuration = new();
    private bool _effectiveActive;
    private bool _loading = true;
    private bool _allowClose;
    private string? _lastError;

    public MainWindow()
    {
        InitializeComponent();
        WebsiteList.ItemsSource = _websites;
        ApplicationList.ItemsSource = _applications;
        ActivityList.ItemsSource = _activity;
        _timer.Tick += Timer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _configuration = await _store.LoadAsync();
            foreach (var website in _configuration.Websites)
            {
                _websites.Add(website);
            }

            foreach (var application in _configuration.Applications)
            {
                _applications.Add(application);
            }

            PopulateSchedule(_configuration.Schedule);
            _loading = false;
            AddActivity($"Configuration loaded from {_store.FilePath}");
            await ApplyDesiredStateAsync(forceHosts: true);
            _timer.Start();
        }
        catch (Exception exception)
        {
            _loading = false;
            ShowError("Cold Ham could not load its configuration.", exception);
        }

        UpdateStatus();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_applications.Count > 0)
        {
            var result = MessageBox.Show(
                "Starting the block will terminate matching applications and discard their unsaved work. Continue?",
                "Start block",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _configuration.ManualBlockEnabled = true;
        await SaveAsync();
        await ApplyDesiredStateAsync(forceHosts: true);
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _configuration.ManualBlockEnabled = false;
        _configuration.Schedule.Enabled = false;
        ScheduleEnabled.IsChecked = false;
        await SaveAsync();
        await ApplyDesiredStateAsync(forceHosts: true);
    }

    private async void ElevateButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveAsync();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unavailable."),
                UseShellExecute = true,
                Verb = "runas"
            });
            _allowClose = true;
            Application.Current.Shutdown();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            AddActivity("Administrator restart was cancelled.");
        }
        catch (Exception exception)
        {
            ShowError("Cold Ham could not restart as administrator.", exception);
        }
    }

    private async void AddWebsiteButton_Click(object sender, RoutedEventArgs e) => await AddWebsiteAsync();

    private async void WebsiteInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await AddWebsiteAsync();
        }
    }

    private async Task AddWebsiteAsync()
    {
        if (!RuleNormalizer.TryNormalizeDomain(WebsiteInput.Text, out var normalized, out var error))
        {
            MessageBox.Show(error, "Invalid website", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_websites.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show("That website is already in the list.", "Duplicate website", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _websites.Add(normalized);
        WebsiteInput.Clear();
        await RulesChangedAsync();
    }

    private async void RemoveWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebsiteList.SelectedItem is string selected)
        {
            _websites.Remove(selected);
            await RulesChangedAsync();
        }
    }

    private async void AddApplicationButton_Click(object sender, RoutedEventArgs e) => await AddApplicationAsync();

    private async void ApplicationInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await AddApplicationAsync();
        }
    }

    private async void BrowseApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Windows applications (*.exe)|*.exe",
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
            MessageBox.Show(error, "Invalid application", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_applications.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show("That application is already in the list.", "Duplicate application", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _applications.Add(normalized);
        ApplicationInput.Clear();
        await RulesChangedAsync();
    }

    private async void RemoveApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicationList.SelectedItem is string selected)
        {
            _applications.Remove(selected);
            await RulesChangedAsync();
        }
    }

    private async void SaveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSchedule(out var schedule, out var error))
        {
            MessageBox.Show(error, "Invalid schedule", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _configuration.Schedule = schedule;
        await SaveAsync();
        AddActivity("Schedule saved.");
        await ApplyDesiredStateAsync(forceHosts: true);
    }

    private void ScheduleChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            UpdateStatus();
        }
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
        await SaveAsync();
        await ApplyDesiredStateAsync(forceHosts: _effectiveActive);
    }

    private async Task ApplyDesiredStateAsync(bool forceHosts = false)
    {
        var scheduled = ScheduleEvaluator.IsActive(_configuration.Schedule, DateTimeOffset.Now);
        var desired = _configuration.ManualBlockEnabled || scheduled;
        var stateChanged = desired != _effectiveActive;

        if (desired && (stateChanged || forceHosts))
        {
            if (_configuration.Websites.Count > 0)
            {
                try
                {
                    await _hosts.ApplyAsync(_configuration.Websites);
                    AddActivity($"Applied {_configuration.Websites.Count} website rule(s).");
                    _lastError = null;
                }
                catch (UnauthorizedAccessException)
                {
                    ReportOnce("Website rules need administrator rights. Use ‘Run as administrator’.");
                }
                catch (Exception exception)
                {
                    ReportOnce($"Website rules failed: {exception.Message}");
                }
            }

            AddActivity(scheduled && !_configuration.ManualBlockEnabled
                ? "Scheduled block started."
                : "Block started.");
        }
        else if (!desired && (stateChanged || forceHosts))
        {
            try
            {
                await _hosts.RemoveAsync();
                AddActivity("Website rules removed.");
                _lastError = null;
            }
            catch (UnauthorizedAccessException)
            {
                ReportOnce("Removing website rules needs administrator rights. Restart as administrator, then stop the block.");
            }
            catch (Exception exception)
            {
                ReportOnce($"Could not remove website rules: {exception.Message}");
            }

            if (stateChanged)
            {
                AddActivity("Block stopped.");
            }
        }

        _effectiveActive = desired;
        if (desired && _configuration.Applications.Count > 0)
        {
            var sweep = _processBlocker.Sweep(_configuration.Applications);
            if (sweep.Terminated > 0)
            {
                AddActivity($"Stopped {sweep.Terminated} blocked application(s).");
            }

            foreach (var error in sweep.Errors)
            {
                ReportOnce(error);
            }
        }

        UpdateStatus();
    }

    private void PopulateSchedule(WeeklySchedule schedule)
    {
        ScheduleEnabled.IsChecked = schedule.Enabled;
        StartTimeInput.Text = schedule.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        EndTimeInput.Text = schedule.End.ToString("HH:mm", CultureInfo.InvariantCulture);
        MondayCheck.IsChecked = schedule.Days.Contains(DayOfWeek.Monday);
        TuesdayCheck.IsChecked = schedule.Days.Contains(DayOfWeek.Tuesday);
        WednesdayCheck.IsChecked = schedule.Days.Contains(DayOfWeek.Wednesday);
        ThursdayCheck.IsChecked = schedule.Days.Contains(DayOfWeek.Thursday);
        FridayCheck.IsChecked = schedule.Days.Contains(DayOfWeek.Friday);
        SaturdayCheck.IsChecked = schedule.Days.Contains(DayOfWeek.Saturday);
        SundayCheck.IsChecked = schedule.Days.Contains(DayOfWeek.Sunday);
    }

    private bool TryReadSchedule(out WeeklySchedule schedule, out string error)
    {
        schedule = new WeeklySchedule();
        error = string.Empty;

        if (!TimeOnly.TryParseExact(StartTimeInput.Text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(EndTimeInput.Text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            error = "Enter start and end times in 24-hour HH:mm format.";
            return false;
        }

        var days = new List<DayOfWeek>();
        AddDayIfChecked(MondayCheck, DayOfWeek.Monday, days);
        AddDayIfChecked(TuesdayCheck, DayOfWeek.Tuesday, days);
        AddDayIfChecked(WednesdayCheck, DayOfWeek.Wednesday, days);
        AddDayIfChecked(ThursdayCheck, DayOfWeek.Thursday, days);
        AddDayIfChecked(FridayCheck, DayOfWeek.Friday, days);
        AddDayIfChecked(SaturdayCheck, DayOfWeek.Saturday, days);
        AddDayIfChecked(SundayCheck, DayOfWeek.Sunday, days);

        if (ScheduleEnabled.IsChecked == true && days.Count == 0)
        {
            error = "Choose at least one day for an enabled schedule.";
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

    private static void AddDayIfChecked(CheckBox checkBox, DayOfWeek day, ICollection<DayOfWeek> days)
    {
        if (checkBox.IsChecked == true)
        {
            days.Add(day);
        }
    }

    private void CopyRulesToConfiguration()
    {
        _configuration.Websites = _websites.ToList();
        _configuration.Applications = _applications.ToList();
    }

    private async Task SaveAsync()
    {
        CopyRulesToConfiguration();
        await _store.SaveAsync(_configuration);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var scheduled = ScheduleEvaluator.IsActive(_configuration.Schedule, DateTimeOffset.Now);
        var elevated = IsAdministrator() ? "administrator" : "standard user";
        StatusText.Text = _effectiveActive
            ? $"Blocking is active ({(scheduled && !_configuration.ManualBlockEnabled ? "schedule" : "manual")}) · {elevated}"
            : $"Blocking is stopped · {elevated}";
        StartButton.IsEnabled = !_configuration.ManualBlockEnabled;
        StopButton.IsEnabled = _configuration.ManualBlockEnabled || _configuration.Schedule.Enabled || _effectiveActive;
        SummaryText.Text = $"{_websites.Count} website rule(s), {_applications.Count} application rule(s), schedule {(_configuration.Schedule.Enabled ? "enabled" : "disabled")}.";
    }

    private void AddActivity(string message)
    {
        _activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (_activity.Count > 100)
        {
            _activity.RemoveAt(_activity.Count - 1);
        }
    }

    private void ReportOnce(string message)
    {
        if (!string.Equals(_lastError, message, StringComparison.Ordinal))
        {
            _lastError = message;
            AddActivity(message);
        }
    }

    private void ShowError(string heading, Exception exception)
    {
        AddActivity($"{heading} {exception.Message}");
        MessageBox.Show($"{heading}\n\n{exception.Message}", "Cold Ham", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_effectiveActive)
        {
            return;
        }

        e.Cancel = true;
        var result = MessageBox.Show(
            "Application blocking only works while Cold Ham is open. Stop all blocking and exit?",
            "Exit Cold Ham",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _configuration.ManualBlockEnabled = false;
        _configuration.Schedule.Enabled = false;
        await SaveAsync();
        await ApplyDesiredStateAsync(forceHosts: true);
        _allowClose = true;
        Close();
    }
}

