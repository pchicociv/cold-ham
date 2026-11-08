using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace ColdHam.Setup;

public partial class SetupWindow : Window
{
    private readonly InstallerService _installer = new();
    private bool _busy;
    private bool _installed;
    private string LanguageCode => LanguageSelector.SelectedValue as string ?? "es";
    private string Text(string spanish, string english) => LanguageCode == "en" ? english : spanish;

    public SetupWindow()
    {
        InitializeComponent();
        LanguageSelector.SelectedValue = "es";
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        Title = Heading.Text = Text("Instalar Cold Ham", "Install Cold Ham");
        Description.Text = Text(
            "Elige el idioma de la instalación y de la aplicación. Podrás cambiarlo después en Cold Ham. La instalación se realiza para tu usuario.",
            "Choose the setup and application language. You can change it later in Cold Ham. Setup installs for your user account.");
        CancelButton.Content = Text("Cancelar", "Cancel");
        InstallButton.Content = Text("Instalar", "Install");
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installed) { Close(); return; }
        _busy = true;
        LanguageSelector.IsEnabled = CancelButton.IsEnabled = InstallButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        Status.Text = Text("Instalando Cold Ham…", "Installing Cold Ham…");
        try
        {
            await _installer.InstallAsync(LanguageCode);
            _installed = true;
            Status.Text = Text("Instalación completada. Abriendo Cold Ham…", "Installation complete. Opening Cold Ham…");
            await _installer.LaunchAsync();
            _busy = false;
            Close();
        }
        catch (Exception exception)
        {
            var reason = exception is AppStillRunningException
                ? Text("Cierra Cold Ham desde el menú de la bandeja y vuelve a intentarlo.", "Exit Cold Ham from its tray menu and try again.")
                : exception.Message;
            Status.Text = (_installed
                ? Text("Cold Ham está instalado. Puedes abrirlo desde Inicio. ", "Cold Ham is installed. You can open it from Start. ")
                : Text("No se ha podido completar la instalación. ", "Setup could not be completed. ")) + reason;
        }
        finally
        {
            _busy = false;
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
            LanguageSelector.IsEnabled = !_installed;
            CancelButton.IsEnabled = InstallButton.IsEnabled = true;
            InstallButton.Content = _installed ? Text("Cerrar", "Close") : Text("Reintentar", "Retry");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e) => e.Cancel = _busy;
}
