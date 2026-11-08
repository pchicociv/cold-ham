using System.Diagnostics;
using System.IO;
using ColdHam.Core;

namespace ColdHam.Setup;

internal sealed class InstallerService
{
    private static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ColdHam");

    public async Task InstallAsync(string language)
    {
        // Do not allow the embedded installer's silent mode to terminate a running blocker.
        var processes = Process.GetProcessesByName("ColdHam.App");
        try { if (processes.Length > 0) throw new AppStillRunningException(); }
        finally { foreach (var process in processes) process.Dispose(); }

        using var payload = typeof(InstallerService).Assembly.GetManifestResourceStream("ColdHam.Setup.Payload.exe")
            ?? throw new InvalidOperationException(language == "en"
                ? "This development build has no installer payload. Use a packaged release."
                : "Esta compilación de desarrollo no contiene el instalador. Utiliza una entrega empaquetada.");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ColdHam-Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var installer = Path.Combine(temporaryRoot, "Setup.exe");
        try
        {
            await using (var output = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await payload.CopyToAsync(output);
            var start = new ProcessStartInfo(installer) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--silent");
            start.ArgumentList.Add("--installto");
            start.ArgumentList.Add(InstallRoot);
            using var process = Process.Start(start) ?? throw new IOException("Could not start setup.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new IOException(language == "en" ? $"Installer returned error {process.ExitCode}." : $"El instalador devolvió el error {process.ExitCode}.");

            // Setup never rewrites settings.json: the application merges this choice on its next start.
            await new InstallationLanguagePreference().WriteAsync(language);
        }
        finally
        {
            // Delete only the exact file/directory created above, never a recursive or inferred install path.
            try { File.Delete(installer); Directory.Delete(temporaryRoot); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task LaunchAsync()
    {
        var start = new ProcessStartInfo(Path.Combine(InstallRoot, "Update.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("start");
        start.ArgumentList.Add("ColdHam.App.exe");
        using var process = Process.Start(start) ?? throw new IOException("Could not start Cold Ham.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"Cold Ham: {process.ExitCode}");
    }
}

internal sealed class AppStillRunningException : Exception;
