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
    private static readonly CultureInfo SpanishCulture = CultureInfo.GetCultureInfo("es-ES");
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
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _configuration = await _store.LoadAsync();
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
            AddActivity($"Configuración cargada desde {_store.FilePath}");
            await ApplyDesiredStateAsync(forceHosts: true);
            _timer.Start();
        }
        catch (Exception exception)
        {
            _loading = false;
            _configurationError = "No se pudo cargar la configuración. Revisa el archivo y reinicia Cold Ham. " + exception.Message;
            _loadFailed = true;
            ShowError("No se pudo cargar la configuración.", exception);
        }

        UpdateStatus();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _configuration.ManualSession = BlockStateEvaluator.CreateUnlimitedSession(_clock);
        await StartSessionAsync("Sesión sin límite iniciada.");
    }

    private async void Start25Button_Click(object sender, RoutedEventArgs e) => await StartTimedSessionAsync(25);

    private async void Start50Button_Click(object sender, RoutedEventArgs e) => await StartTimedSessionAsync(50);

    private async void StartCustomButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SessionMinutesInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            MessageBox.Show("Introduce una duración en minutos entre 1 y 1440.", "Duración no válida", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await StartTimedSessionAsync(minutes);
    }

    private async Task StartTimedSessionAsync(int minutes)
    {
        if (!BlockStateEvaluator.TryCreateTimedSession(_clock, minutes, out var session, out var error))
        {
            MessageBox.Show(error, "Duración no válida", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _configuration.ManualSession = session;
        await StartSessionAsync($"Sesión de {minutes} minutos iniciada.");
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
                    ? "La sesión terminará, pero el bloqueo continuará porque el horario está activo. ¿Continuar?"
                    : "La sesión terminará y se retirarán las reglas si el horario no está activo. ¿Continuar?",
                "Terminar sesión", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _configuration.ManualSession = null;
        _consent.Reset();
        await SaveAsync();
        AddActivity("Sesión manual terminada.");
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
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException("No se encuentra la ruta de la aplicación."),
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
                _configurationError = "No se pudo recuperar la instancia de Cold Ham después de cancelar la elevación.";
            }
            AddActivity("Se canceló el reinicio como administrador.");
        }
        catch (Exception exception)
        {
            app.ReacquireInstanceAfterElevationCancellation();
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
            MessageBox.Show(TranslateValidation(error), "Sitio web no válido", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_allowedWebsites.Any(item => string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Ese sitio web ya está en la lista de permitidos.", "Sitio duplicado", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try { BrowserPolicyPlan.BuildAllowlist(_allowedWebsites.Select(item => item.Value).Append(normalized)); }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(exception.Message, "Lista de permitidos completa", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _allowedWebsites.Add(RuleRow.ForWebsite(normalized));
        AllowedWebsiteInput.Clear();
        await RulesChangedAsync();
        AddActivity($"Sitio permitido añadido: {normalized}");
    }

    private async void WebsiteModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var allowOnly = OnlyAllowedWebsitesToggle.IsChecked == true;
        if (allowOnly && MessageBox.Show(this,
                "Durante una sesión o un horario activo se bloquearán los sitios no permitidos en Chrome y Edge. Una lista vacía bloquea todos los sitios. Otros navegadores y los perfiles personales de Microsoft en Edge quedan fuera de este modo. Reinicia el navegador para cargar o retirar las políticas. ¿Activar este modo?",
                "Solo sitios permitidos", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            OnlyAllowedWebsitesToggle.IsChecked = false;
            return;
        }
        _configuration.WebsiteMode = allowOnly ? WebsiteBlockingMode.AllowListedSites : WebsiteBlockingMode.BlockListedSites;
        await RulesChangedAsync();
        AddActivity(allowOnly ? "Modo web: solo sitios permitidos en Chrome y Edge." : "Modo web: lista de sitios bloqueados.");
    }

    private async void RemoveAllowedWebsiteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RuleRow selected }) return;
        if (MessageBox.Show(this,
                $"¿Eliminar {selected.Value} de los sitios permitidos? Dejará de estar permitido cuando esté activo el modo Solo sitios permitidos.",
                "Eliminar sitio permitido", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _allowedWebsites.Remove(selected);
        await RulesChangedAsync();
        AddActivity($"Sitio permitido eliminado: {selected.Value}");
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
                AddActivity("La sesión manual terminó por tiempo.");
                RaiseNotification("Sesión terminada", state.ScheduledActive
                    ? "La sesión ha terminado."
                    : "La sesión ha terminado; el horario puede mantener el bloqueo activo.");
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
                    AddActivity(allowOnly ? "Políticas de solo permitidos escritas para Chrome y Edge. Reinicia el navegador y comprueba chrome://policy o edge://policy."
                        : webRules.Length > 0
                        ? $"Se escribieron {webRules.Length} regla(s) web en hosts."
                        : "Se verificó la retirada de las reglas web de Cold Ham. Reinicia el navegador si usaste Solo sitios permitidos.");
                }
                catch (Exception exception)
                {
                    _hostsError = exception is UnauthorizedAccessException || exception is AggregateException aggregate && aggregate.InnerExceptions.Any(error => error is UnauthorizedAccessException)
                        ? "No se pudieron aplicar o retirar las reglas web. Si faltan permisos de hosts, reinicia como administrador y pulsa Reintentar. Si las políticas están administradas por tu organización, consulta a su administrador."
                        : $"No se pudieron aplicar o retirar las reglas web: {exception.Message}";
                    ReportOnce(_hostsError);
                }
            }

            _effectiveActive = desired;
            if (stateChanged) AddActivity(desired ? "Se solicitó activar el bloqueo." : "Se solicitó detener el bloqueo.");
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
                        "Se cerrarán las aplicaciones que coincidan con estas reglas, incluidas las que abras durante este bloqueo:\n\n"
                        + string.Join("\n", applications)
                        + "\n\nGuarda tu trabajo antes de continuar. ¿Autorizas estos cierres? Si eliges No, las webs seguirán su estado indicado y las aplicaciones quedarán pendientes.",
                        "Confirmar bloqueo de aplicaciones", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                        MessageBoxResult.No) == MessageBoxResult.Yes;
                    _consent.Decide(applications, _applicationsApproved);
                    if (!_applicationsApproved)
                    {
                        RaiseNotification("Cierres pendientes", "Cold Ham no cerrará aplicaciones hasta que autorices la sesión.");
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
                        if (sweep.Terminated > 0) AddActivity($"Se cerraron {sweep.Terminated} aplicación(es) bloqueada(s).");
                        if (sweep.Errors.Count > 0)
                        {
                            _applicationError = string.Join("\n", sweep.Errors.Distinct());
                            ReportOnce(_applicationError);
                        }
                    }
                    catch (Exception exception)
                    {
                        _applicationError = $"No se pudo comprobar el bloqueo de aplicaciones: {exception.Message}";
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
            _configurationError = $"La sesión ha terminado, pero no se pudo guardar su finalización: {exception.Message}";
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
            _configurationError = $"No se pudo guardar la configuración. Pulsa Reintentar tras resolver el problema: {exception.Message}";
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
        StatusTitleText.Text = hasError ? "Bloqueo con incidencias" : pendingConsent ? "Bloqueo pendiente de confirmación"
            : _effectiveActive ? "Bloqueo activo" : "Bloqueo detenido";
        StatusText.Text = hasError ? "Revisa el estado de webs y aplicaciones antes de dar el bloqueo por aplicado o retirado." : _effectiveActive
            ? scheduled && !state.ManualActive
                ? "Activado por el horario semanal"
                : scheduled ? "Activado por una sesión y el horario semanal" : "Activado por una sesión manual"
            : "Las reglas están preparadas pero no se están aplicando";

        var transition = state.NextScheduleTransition;
        if (state.ManualActive && _configuration.ManualSession?.EndsAtUtc is { } endsAt)
        {
            var remaining = state.Remaining ?? TimeSpan.Zero;
            var scheduleContinuation = scheduled ? " El horario también está activo." : string.Empty;
            NextChangeText.Text = $"La sesión termina {FormatRemaining(remaining)} ({FormatTransition(endsAt.LocalDateTime)}).{scheduleContinuation}";
        }
        else if (state.ManualActive)
        {
            NextChangeText.Text = "La sesión manual no tiene hora de finalización.";
        }
        else if (transition is not null)
        {
            NextChangeText.Text = scheduled
                ? $"Finaliza {FormatTransition(transition.Value.LocalDateTime)}."
                : $"Próximo bloqueo {FormatTransition(transition.Value.LocalDateTime)}.";
        }
        else
        {
            NextChangeText.Text = _configuration.Schedule.Enabled
                ? "Revisa los días y horas de la programación."
                : "No hay ningún horario activo.";
        }

        StartButton.IsEnabled = !_applying && !_saving && !_loadFailed && !state.ManualActive;
        Start25Button.IsEnabled = StartButton.IsEnabled;
        Start50Button.IsEnabled = StartButton.IsEnabled;
        StartCustomButton.IsEnabled = StartButton.IsEnabled;
        SessionMinutesInput.IsEnabled = StartButton.IsEnabled;
        EndSessionButton.IsEnabled = !_applying && !_saving && state.ManualActive;
        StopButton.IsEnabled = !_applying && !_saving && (state.ManualActive || _configuration.Schedule.Enabled || _effectiveActive || _hostsError is not null || _loadFailed);
        MainTabs.IsEnabled = !_applying && !_saving && !_loadFailed;
        AdminBanner.IsEnabled = !_applying && !_saving;
        RecoveryText.Text = string.Join("\n", new[] { _configurationError, _hostsError, _applicationError, _recoveryMessage,
            pendingConsent ? "Cierres de aplicaciones sin autorizar. Pulsa Reintentar cuando hayas guardado tu trabajo." : null }
            .Where(message => message is not null));
        AdminBanner.Visibility = RecoveryText.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        EnforcementText.Text = "Webs: " + (_hostsError is not null ? "estado sin confirmar; pueden quedar reglas activas"
            : _appliedWebRules is null ? "sin comprobar" : _appliedWebRules.StartsWith("allow\n", StringComparison.Ordinal) ? "solo permitidos: políticas escritas; reinicia Chrome/Edge"
            : _appliedWebRules.Length > 0 ? "reglas escritas en hosts" : "sin reglas activas; reinicia el navegador si usaste Solo permitidos")
            + " · Aplicaciones: " + (_configurationError is not null ? "vigilancia suspendida"
                : _applicationError is not null ? "error de comprobación"
                : pendingConsent ? "pendientes de autorización" : _effectiveActive && _applicationsApproved && _applications.Count > 0 ? "vigilando" : "sin vigilancia");

        var websiteLabel = _websites.Count == 1 ? "1 sitio web" : $"{_websites.Count} sitios web";
        var applicationLabel = _applications.Count == 1 ? "1 aplicación" : $"{_applications.Count} aplicaciones";
        SummaryText.Text = _configuration.WebsiteMode == WebsiteBlockingMode.AllowListedSites
            ? $"Modo Solo sitios permitidos (Chrome y Edge): {_allowedWebsites.Count} sitios permitidos. {applicationLabel} en la lista de bloqueo."
            : $"{websiteLabel} y {applicationLabel} en la lista de bloqueo.";
        WebsiteCountText.Text = _websites.Count == 1 ? "1 regla" : $"{_websites.Count} reglas";
        AllowedWebsiteCountText.Text = _allowedWebsites.Count == 1 ? "1 sitio" : $"{_allowedWebsites.Count} sitios";
        ApplicationCountText.Text = _applications.Count == 1 ? "1 regla" : $"{_applications.Count} reglas";
        WebsiteEmptyText.Visibility = _websites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AllowedWebsiteEmptyText.Visibility = _allowedWebsites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplicationEmptyText.Visibility = _applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateRuleStatuses();
        UpdateScheduleSummary();
        SessionSummaryText.Text = state.ManualActive
            ? _configuration.ManualSession?.EndsAtUtc is { } end
                ? $"Sesión en curso: {FormatRemaining(state.Remaining ?? TimeSpan.Zero)} restantes; termina {FormatTransition(end.LocalDateTime)}."
                : "Sesión manual en curso sin hora de finalización."
            : scheduled
                ? "No hay sesión manual; el horario mantiene el bloqueo activo."
                : "No hay una sesión manual en curso.";
        TrayStatus = StatusTitleText.Text + " · " + NextChangeText.Text;
        BlockStateChanged?.Invoke(this, EventArgs.Empty);
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
                website.SetStatus("Sin confirmar", "#B54708");
            }
            else if (allowOnly)
            {
                website.SetStatus("No se usa en este modo", "#667085");
            }
            else if (!_effectiveActive)
            {
                website.SetStatus("Preparado", "#667085");
            }
            else if (_appliedWebRules == effectiveKey)
            {
                website.SetStatus("En hosts", "#067647");
            }
            else
            {
                website.SetStatus("Pendiente", "#B54708");
            }
        }

        foreach (var website in _allowedWebsites)
        {
            var status = _hostsError is not null || _configurationError is not null ? "Sin confirmar"
                : !allowOnly ? "Modo desactivado" : !_effectiveActive ? "Preparado"
                : _appliedWebRules == "allow\n" + RuleKey(_configuration.AllowedWebsites) ? "En políticas" : "Pendiente";
            website.SetStatus(status, status is "Sin confirmar" or "Pendiente" ? "#B54708" : "#667085");
        }

        foreach (var application in _applications)
        {
            var status = _configurationError is not null ? "Sin vigilancia" : !_effectiveActive ? "Preparado" : !_applicationsApproved ? "Sin autorizar"
                : _applicationError is not null ? "Revisar" : "Vigilando";
            application.SetStatus(status, status == "Vigilando" ? "#067647" : "#667085");
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

    private static string FormatRemaining(TimeSpan remaining)
    {
        var rounded = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, remaining.TotalSeconds)));
        return rounded.TotalHours >= 1
            ? $"en {(int)rounded.TotalHours}:{rounded.Minutes:D2}:{rounded.Seconds:D2}"
            : $"en {rounded.Minutes:D2}:{rounded.Seconds:D2}";
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
            RaiseNotification("Cold Ham necesita revisión", message);
        }
    }

    private void RaiseNotification(string title, string message) =>
        NotificationRaised?.Invoke(this, new TrayNotificationEventArgs(title, message));

    private void ShowError(string heading, Exception exception)
    {
        AddActivity($"{heading} {exception.Message}");
        MessageBox.Show($"{heading}\n\n{exception.Message}", "Cold Ham", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    ? "Al salir se terminará la sesión y se retirarán las reglas web. El horario quedará guardado, pero no funcionará mientras Cold Ham esté cerrado. ¿Salir?"
                    : "El horario quedará guardado, pero no funcionará mientras Cold Ham esté cerrado. ¿Salir?",
                "Salir de Cold Ham", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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
            "Las reglas se han retirado, pero no se pudo guardar el fin de la sesión. ¿Salir de todos modos?",
            "Configuración sin guardar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
            "Se terminará la sesión y se desactivará el horario. ¿Quieres continuar?",
            "Detener todo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _configuration.ManualSession = null;
        _configuration.Schedule.Enabled = false;
        ScheduleEnabled.IsChecked = false;
        _consent.Reset();
        await SaveAsync();
        AddActivity("Se detuvo la sesión y se desactivó el horario.");
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
                "Cold Ham seguirá funcionando desde el icono de la bandeja. Ábrelo desde allí para terminar una sesión o salir.",
                "Cold Ham sigue activo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Hide();
        AddActivity("La ventana se ocultó en la bandeja del sistema.");
    }

}

public sealed class TrayNotificationEventArgs(string title, string message) : EventArgs
{
    public string Title { get; } = title;
    public string Message { get; } = message;
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
