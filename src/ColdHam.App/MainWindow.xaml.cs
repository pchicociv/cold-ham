using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ColdHam.Core;
using ColdHam.Windows;
using Microsoft.Win32;

namespace ColdHam.App;

public partial class MainWindow : Window
{
    private static readonly CultureInfo SpanishCulture = CultureInfo.GetCultureInfo("es-ES");
    private readonly ObservableCollection<RuleRow> _websites = [];
    private readonly ObservableCollection<RuleRow> _applications = [];
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
                _websites.Add(RuleRow.ForWebsite(website));
            }

            foreach (var application in _configuration.Applications)
            {
                _applications.Add(RuleRow.ForApplication(application));
            }

            PopulateSchedule(_configuration.Schedule);
            _loading = false;
            AddActivity($"Configuración cargada desde {_store.FilePath}");
            await ApplyDesiredStateAsync(forceHosts: true);
            _timer.Start();
        }
        catch (Exception exception)
        {
            _loading = false;
            ShowError("No se pudo cargar la configuración.", exception);
        }

        UpdateStatus();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_applications.Count > 0)
        {
            var result = MessageBox.Show(
                "El bloqueo cerrará las aplicaciones coincidentes y podría descartar cambios sin guardar. ¿Quieres continuar?",
                "Iniciar bloqueo",
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
        var result = MessageBox.Show(
            "Se detendrá el bloqueo manual y se desactivará el horario. ¿Quieres continuar?",
            "Detener todo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

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
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException("No se encuentra la ruta de la aplicación."),
                UseShellExecute = true,
                Verb = "runas"
            });
            _allowClose = true;
            Application.Current.Shutdown();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            AddActivity("Se canceló el reinicio como administrador.");
        }
        catch (Exception exception)
        {
            ShowError("No se pudo reiniciar como administrador.", exception);
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
            MessageBox.Show(TranslateValidation(error), "Sitio web no válido", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_websites.Any(item => string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Ese sitio web ya está en la lista.", "Sitio duplicado", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _websites.Add(RuleRow.ForWebsite(normalized));
        WebsiteInput.Clear();
        await RulesChangedAsync();
        AddActivity($"Sitio añadido: {normalized}");
    }

    private async void RemoveWebsiteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RuleRow selected })
        {
            return;
        }

        var result = MessageBox.Show(
            $"¿Eliminar {selected.Value} de la lista?",
            "Eliminar sitio web",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _websites.Remove(selected);
        await RulesChangedAsync();
        AddActivity($"Sitio eliminado: {selected.Value}");
    }

    private async void AddApplicationButton_Click(object sender, RoutedEventArgs e) => await AddApplicationAsync();

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
            Filter = "Aplicaciones de Windows (*.exe)|*.exe",
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
            MessageBox.Show(TranslateValidation(error), "Aplicación no válida", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_applications.Any(item => string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Esa aplicación ya está en la lista.", "Aplicación duplicada", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _applications.Add(RuleRow.ForApplication(normalized));
        ApplicationInput.Clear();
        await RulesChangedAsync();
        AddActivity($"Aplicación añadida: {Path.GetFileName(normalized)}");
    }

    private async void RemoveApplicationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RuleRow selected })
        {
            return;
        }

        var result = MessageBox.Show(
            $"¿Eliminar {selected.DisplayName} de la lista?",
            "Eliminar aplicación",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _applications.Remove(selected);
        await RulesChangedAsync();
        AddActivity($"Aplicación eliminada: {selected.DisplayName}");
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
        if (_loading)
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
        await SaveAsync();
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
            try
            {
                if (_configuration.Websites.Count > 0)
                {
                    await _hosts.ApplyAsync(_configuration.Websites);
                    AddActivity($"Se aplicaron {_configuration.Websites.Count} regla(s) web.");
                }
                else
                {
                    await _hosts.RemoveAsync();
                }

                _lastError = null;
            }
            catch (UnauthorizedAccessException)
            {
                ReportOnce("Las reglas web necesitan permisos de administrador.");
            }
            catch (Exception exception)
            {
                ReportOnce($"No se pudieron aplicar las reglas web: {exception.Message}");
            }

            if (stateChanged)
            {
                AddActivity(scheduled && !_configuration.ManualBlockEnabled
                    ? "Comenzó el bloqueo programado."
                    : "Comenzó el bloqueo manual.");
            }
        }
        else if (!desired && (stateChanged || forceHosts))
        {
            try
            {
                await _hosts.RemoveAsync();
                AddActivity("Se retiraron las reglas web.");
                _lastError = null;
            }
            catch (UnauthorizedAccessException)
            {
                ReportOnce("Se necesitan permisos de administrador para retirar las reglas web.");
            }
            catch (Exception exception)
            {
                ReportOnce($"No se pudieron retirar las reglas web: {exception.Message}");
            }

            if (stateChanged)
            {
                AddActivity("El bloqueo se detuvo.");
            }
        }

        _effectiveActive = desired;
        if (desired && _configuration.Applications.Count > 0)
        {
            var sweep = _processBlocker.Sweep(_configuration.Applications);
            if (sweep.Terminated > 0)
            {
                AddActivity($"Se cerraron {sweep.Terminated} aplicación(es) bloqueada(s).");
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
            error = "Introduce las horas en formato de 24 horas HH:mm.";
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
            error = "Selecciona al menos un día para activar el horario.";
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
        _configuration.Applications = _applications.Select(item => item.Value).ToList();
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
        var elevated = IsAdministrator();

        StatusDot.Fill = new SolidColorBrush(_effectiveActive
            ? Color.FromRgb(18, 183, 106)
            : Color.FromRgb(152, 162, 179));
        StatusTitleText.Text = _effectiveActive ? "Bloqueo activo" : "Bloqueo detenido";
        StatusText.Text = _effectiveActive
            ? scheduled && !_configuration.ManualBlockEnabled
                ? "Activado por el horario semanal"
                : "Activado manualmente"
            : "Las reglas están preparadas pero no se están aplicando";

        var transition = FindNextScheduleTransition(_configuration.Schedule, DateTime.Now);
        if (_configuration.ManualBlockEnabled)
        {
            NextChangeText.Text = "El bloqueo manual no tiene hora de finalización.";
        }
        else if (transition is not null)
        {
            NextChangeText.Text = scheduled
                ? $"Finaliza {FormatTransition(transition.Value)}."
                : $"Próximo bloqueo {FormatTransition(transition.Value)}.";
        }
        else
        {
            NextChangeText.Text = _configuration.Schedule.Enabled
                ? "Revisa los días y horas de la programación."
                : "No hay ningún horario activo.";
        }

        StartButton.IsEnabled = !_configuration.ManualBlockEnabled;
        StopButton.IsEnabled = _configuration.ManualBlockEnabled || _configuration.Schedule.Enabled || _effectiveActive;
        AdminBanner.Visibility = _effectiveActive && _websites.Count > 0 && !elevated
            ? Visibility.Visible
            : Visibility.Collapsed;

        var websiteLabel = _websites.Count == 1 ? "1 sitio web" : $"{_websites.Count} sitios web";
        var applicationLabel = _applications.Count == 1 ? "1 aplicación" : $"{_applications.Count} aplicaciones";
        SummaryText.Text = $"{websiteLabel} y {applicationLabel} en la lista de bloqueo.";
        WebsiteCountText.Text = _websites.Count == 1 ? "1 regla" : $"{_websites.Count} reglas";
        ApplicationCountText.Text = _applications.Count == 1 ? "1 regla" : $"{_applications.Count} reglas";
        WebsiteEmptyText.Visibility = _websites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplicationEmptyText.Visibility = _applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateRuleStatuses(elevated);
        UpdateScheduleSummary();
    }

    private void UpdateRuleStatuses(bool elevated)
    {
        foreach (var website in _websites)
        {
            if (!_effectiveActive)
            {
                website.SetStatus("Preparado", "#667085");
            }
            else if (elevated)
            {
                website.SetStatus("Aplicado", "#067647");
            }
            else
            {
                website.SetStatus("Pendiente", "#B54708");
            }
        }

        foreach (var application in _applications)
        {
            application.SetStatus(_effectiveActive ? "Vigilando" : "Preparado", _effectiveActive ? "#067647" : "#667085");
        }
    }

    private void UpdateScheduleSummary()
    {
        if (!TryReadSchedule(out var schedule, out _))
        {
            ScheduleSummaryText.Text = "La programación contiene datos pendientes de corregir.";
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
            return "El horario está desactivado.";
        }

        var weekdays = new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
            DayOfWeek.Thursday, DayOfWeek.Friday
        };
        string daysText;
        if (schedule.Days.Count == 7)
        {
            daysText = "Todos los días";
        }
        else if (schedule.Days.Count == 5 && weekdays.All(schedule.Days.Contains))
        {
            daysText = "De lunes a viernes";
        }
        else
        {
            daysText = string.Join(", ", schedule.Days
                .OrderBy(DayOrder)
                .Select(day => SpanishCulture.DateTimeFormat.GetAbbreviatedDayName(day).TrimEnd('.')));
        }

        return schedule.Start == schedule.End
            ? $"{daysText}, durante todo el día."
            : $"{daysText}, de {schedule.Start:HH\\:mm} a {schedule.End:HH\\:mm}.";
    }

    private static int DayOrder(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    private static DateTime? FindNextScheduleTransition(WeeklySchedule schedule, DateTime now)
    {
        if (!schedule.Enabled || schedule.Days.Count == 0)
        {
            return null;
        }

        var currentState = ScheduleEvaluator.IsActive(schedule, new DateTimeOffset(now));
        var boundaries = new List<DateTime>();
        for (var offset = -1; offset <= 8; offset++)
        {
            var date = now.Date.AddDays(offset);
            if (!schedule.Days.Contains(date.DayOfWeek))
            {
                continue;
            }

            var start = date.Add(schedule.Start.ToTimeSpan());
            var endDate = schedule.End <= schedule.Start ? date.AddDays(1) : date;
            var end = endDate.Add(schedule.End.ToTimeSpan());
            boundaries.Add(start);
            boundaries.Add(end);
        }

        foreach (var boundary in boundaries.Where(value => value > now).Distinct().Order())
        {
            var stateAfterBoundary = ScheduleEvaluator.IsActive(schedule, new DateTimeOffset(boundary.AddSeconds(1)));
            if (stateAfterBoundary != currentState)
            {
                return boundary;
            }
        }

        return null;
    }

    private static string FormatTransition(DateTime transition)
    {
        if (transition.Date == DateTime.Today)
        {
            return $"hoy a las {transition:HH:mm}";
        }

        if (transition.Date == DateTime.Today.AddDays(1))
        {
            return $"mañana a las {transition:HH:mm}";
        }

        return $"el {transition.ToString("dddd", SpanishCulture)} a las {transition:HH:mm}";
    }

    private void AddActivity(string message)
    {
        _activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (_activity.Count > 100)
        {
            _activity.RemoveAt(_activity.Count - 1);
        }

        ActivityHeaderText.Text = _activity.Count == 1 ? "Actividad · 1 evento" : $"Actividad · {_activity.Count} eventos";
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

    private static string TranslateValidation(string message)
    {
        if (message.Contains("wildcard", StringComparison.OrdinalIgnoreCase))
        {
            return "Los comodines no son compatibles con el bloqueo mediante el archivo hosts.";
        }

        if (message.Contains("protected", StringComparison.OrdinalIgnoreCase))
        {
            return "Ese proceso pertenece al sistema o a Cold Ham y no puede bloquearse.";
        }

        if (message.Contains("executable", StringComparison.OrdinalIgnoreCase))
        {
            return "Introduce el nombre de un ejecutable o selecciona un archivo .exe válido.";
        }

        return "El valor introducido no es válido.";
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_effectiveActive)
        {
            return;
        }

        e.Cancel = true;
        var result = MessageBox.Show(
            "El bloqueo de aplicaciones solo funciona mientras Cold Ham está abierto. ¿Quieres detener todo y salir?",
            "Salir de Cold Ham",
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

public sealed class RuleRow : INotifyPropertyChanged
{
    private string _status = "Preparado";
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
