using System.Diagnostics;
using Microsoft.Win32;

namespace ColdHam.Windows;

public enum PolicyBrowser { Chrome, Edge }

/// <summary>Opens the browser's own policy page; does not claim to reload or verify policies.</summary>
public static class BrowserPolicyPage
{
    public static string Address(PolicyBrowser browser) => browser switch
    {
        PolicyBrowser.Chrome => "chrome://policy",
        PolicyBrowser.Edge => "edge://policy",
        _ => throw new ArgumentOutOfRangeException(nameof(browser))
    };

    public static void Open(PolicyBrowser browser)
    {
        using var process = Process.Start(CreateStartInfo(browser, FindCandidates(browser)));
        if (process is null) throw new InvalidOperationException("The browser could not be started.");
    }

    public static ProcessStartInfo CreateStartInfo(PolicyBrowser browser, IEnumerable<string?> candidates)
    {
        var address = Address(browser);
        var name = browser == PolicyBrowser.Chrome ? "chrome.exe" : "msedge.exe";
        var executable = candidates.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().Trim('"'))
            .FirstOrDefault(path => Path.IsPathFullyQualified(path)
                && string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)
                && File.Exists(path));
        if (executable is null) throw new FileNotFoundException(name);

        // Use the selected browser, never the default URL handler, PATH or a shell command.
        // No profile flags: let the browser reuse its normal session and keep existing tabs.
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--new-tab");
        start.ArgumentList.Add(address);
        return start;
    }

    private static IEnumerable<string?> FindCandidates(PolicyBrowser browser)
    {
        var name = browser == PolicyBrowser.Chrome ? "chrome.exe" : "msedge.exe";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? registered = null;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                registered = key?.GetValue(null) as string;
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            { /* Try the conventional installation directories if registration is inaccessible. */ }
            yield return registered;
        }
        var relative = browser == PolicyBrowser.Chrome ? @"Google\Chrome\Application\chrome.exe" : @"Microsoft\Edge\Application\msedge.exe";
        foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, relative);
        }
    }
}
