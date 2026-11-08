using System.Windows;

namespace ColdHam.Setup;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Local\\ColdHam.Setup", out var created);
        if (!created)
        {
            _mutex.Dispose();
            _mutex = null;
            MessageBox.Show("El instalador ya está abierto. / Setup is already running.", "Cold Ham");
            Shutdown(1);
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
