using System.Text.Json;

namespace ColdHam.Core;

public sealed class JsonConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public JsonConfigurationStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ColdHam",
            "settings.json");
    }

    public string FilePath { get; }

    public async Task<BlockConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new BlockConfiguration();
        }

        await using var stream = File.OpenRead(FilePath);
        return await JsonSerializer.DeserializeAsync<BlockConfiguration>(stream, SerializerOptions, cancellationToken)
            ?? new BlockConfiguration();
    }

    public async Task SaveAsync(BlockConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = FilePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, configuration, SerializerOptions, cancellationToken);
        }

        File.Move(temporaryPath, FilePath, true);
    }
}

