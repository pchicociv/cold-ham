using ColdHam.Core;
using System.IO.Pipes;
using System.IO;
using System.Security.Principal;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ColdHam.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private CancellationTokenSource? _activationCancellation;
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;
    private string _pipeName = string.Empty;

    internal bool TrayAvailable => _tray is not null;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UpdateLanguageResources();
        _pipeName = BuildPipeName();
        if (!TryAcquireSingleInstance())
        {
            await SignalExistingInstanceAsync();
            Shutdown();
            return;
        }

        _mainWindow = new MainWindow();
        _mainWindow.Show();
        try
        {
            _tray = new TrayIconService(_mainWindow);
        }
        catch (Exception exception)
        {
            MessageBox.Show(_mainWindow,
                LocalizedText.Format("CouldNotCreateTheTrayIconClosing", exception.Message),
                "Cold Ham", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _activationCancellation = new CancellationTokenSource();
        _ = ListenForActivationAsync(_activationCancellation.Token);
    }

    internal void ExitApplication()
    {
        _tray?.Dispose();
        _activationCancellation?.Cancel();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        Shutdown();
    }

    internal void UpdateLanguageResources()
    {
        foreach (var key in LocalizedText.Keys)
        {
            Resources[key] = LocalizedText.Get(key);
        }
    }

    internal void ReleaseInstanceForElevation()
    {
        _activationCancellation?.Cancel();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }

    internal bool ReacquireInstanceAfterElevationCancellation()
    {
        _activationCancellation?.Dispose();
        _activationCancellation = new CancellationTokenSource();
        if (!TryAcquireSingleInstance()) return false;
        _ = ListenForActivationAsync(_activationCancellation.Token);
        return true;
    }

    private bool TryAcquireSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, $"Local\\{_pipeName}", out var createdNew);
        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
        return createdNew;
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe);
                if (await reader.ReadLineAsync(cancellationToken) == "SHOW")
                {
                    await Dispatcher.InvokeAsync(() => _mainWindow?.ShowFromTray());
                }
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { }
        }
    }

    private async Task SignalExistingInstanceAsync()
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(750);
            await using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync("SHOW");
        }
        catch (IOException) { }
    }

    private static string BuildPipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? Environment.UserName;
        return "ColdHam_" + string.Concat(sid.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
    }
}
