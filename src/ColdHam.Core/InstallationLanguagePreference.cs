namespace ColdHam.Core;

/// <summary>Transfers an explicit setup choice without letting setup rewrite application settings.</summary>
public sealed class InstallationLanguagePreference
{
    public InstallationLanguagePreference(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ColdHam", "installation-language.txt");
    }

    public string FilePath { get; }

    public async Task WriteAsync(string language)
    {
        if (language is not ("es" or "en")) throw new ArgumentException("Unsupported installation language.", nameof(language));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, language);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task ApplyAsync(JsonConfigurationStore store, BlockConfiguration configuration)
    {
        string language;
        try { language = (await File.ReadAllTextAsync(FilePath)).Trim(); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        // A malformed setup preference must not reset otherwise valid configuration.
        if (language is not ("es" or "en")) return;

        var previous = configuration.Language;
        configuration.Language = language;
        try { await store.SaveAsync(configuration); }
        catch
        {
            configuration.Language = previous;
            throw;
        }
        // Only consume the preference after settings are durable; a failed save is retried next launch.
        File.Delete(FilePath);
    }
}
