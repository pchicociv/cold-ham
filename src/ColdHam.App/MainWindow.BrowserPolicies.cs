using System.Windows;
using ColdHam.Core;
using ColdHam.Windows;
using MessageBox = System.Windows.MessageBox;

namespace ColdHam.App;

public partial class MainWindow
{
    private void OpenBrowserPolicies_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }
            || !Enum.TryParse<PolicyBrowser>(name, out var browser) || !Enum.IsDefined(browser)) return;
        try { BrowserPolicyPage.Open(browser); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.IO.IOException
            or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            MessageBox.Show(this, LocalizedText.Format("BrowserPolicyOpenFailed", name, BrowserPolicyPage.Address(browser)),
                "Cold Ham", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
