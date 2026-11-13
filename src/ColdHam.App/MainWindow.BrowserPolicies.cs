using System.Windows;
using Clipboard = System.Windows.Clipboard;

namespace ColdHam.App;

public partial class MainWindow
{
    private void CopyBrowserPolicyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string browser }) return;
        var address = browser switch { "Chrome" => "chrome://policy", "Edge" => "edge://policy", _ => null };
        if (address is null) return;
        var statusKey = "BrowserPolicy" + browser + "Copied";
        try { Clipboard.SetText(address); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // A busy clipboard must leave a usable manual path, not a false success message.
            statusKey = "BrowserPolicy" + browser + "CopyFailed";
        }
        AllowedPolicyCopyStatus.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, statusKey);
        DiagnosticPolicyCopyStatus.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, statusKey);
    }
}
