using ColdHam.Windows;

internal static class BrowserPolicyPageTests
{
    public static Task LaunchPlan()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ColdHam-browser-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var chrome = Path.Combine(directory, "chrome.exe");
            var edge = Path.Combine(directory, "msedge.exe");
            File.WriteAllText(chrome, "test placeholder; never executed");
            File.WriteAllText(edge, "test placeholder; never executed");
            foreach (var browser in new[] { PolicyBrowser.Chrome, PolicyBrowser.Edge })
            {
                var expected = browser == PolicyBrowser.Chrome ? chrome : edge;
                var other = browser == PolicyBrowser.Chrome ? edge : chrome;
                var start = BrowserPolicyPage.CreateStartInfo(browser,
                    [null, "", "chrome.exe", other, expected + " --incognito", '"' + expected + '"']);
                Check(start.FileName == expected && !start.UseShellExecute && start.Verb == "",
                    "Use an existing absolute executable for the requested browser, never a shell, elevation verb or other browser.");
                Check(start.ArgumentList.SequenceEqual(new[] { "--new-tab", BrowserPolicyPage.Address(browser) })
                    && start.Arguments == "", "Open only the policy page, without restart, profile or debugging flags.");
                var missing = false;
                try { BrowserPolicyPage.CreateStartInfo(browser, [other, expected + " --flag", "missing.exe"]); }
                catch (FileNotFoundException) { missing = true; }
                Check(missing, "Missing browser must fail for a manual fallback, never launch the default browser.");
            }
            var invalid = false;
            try { BrowserPolicyPage.Address((PolicyBrowser)99); }
            catch (ArgumentOutOfRangeException) { invalid = true; }
            Check(invalid, "Only the two supported internal policy pages may be opened.");
        }
        finally { Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
