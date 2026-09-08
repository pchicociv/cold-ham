using System.Text.Json;

namespace ColdHam.Core;

public sealed class JsonConfigurationStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string? RecoveryMessage { get; private set; }
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
        RecoveryMessage = null;
        try
        {
            return await ReadValidatedAsync(FilePath, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new BlockConfiguration();
        }
        catch (UnsupportedConfigurationVersionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            var preserved = FilePath + $".corrupt-{Guid.NewGuid():N}";
            File.Copy(FilePath, preserved);
            var recovered = new BlockConfiguration();
            var usedBackup = false;
            if (File.Exists(FilePath + ".bak"))
            {
                try
                {
                    recovered = await ReadValidatedAsync(FilePath + ".bak", cancellationToken);
                    usedBackup = true;
                }
                catch (Exception backupException) when (backupException is JsonException or InvalidDataException)
                {
                    // Preserve both files; invalid backup data is never activated.
                }
            }
            recovered.ManualSession = null;
            recovered.ManualBlockEnabled = null;
            recovered.Schedule.Enabled = false;
            RecoveryMessage = $"Configuración dañada conservada en {preserved}. " +
                (usedBackup ? "Se recuperó la copia anterior. " : "Se inició una configuración vacía. ") +
                "La activación manual y el horario están desactivados. Comprueba el estado de retirada de las reglas web y revisa la configuración antes de activarla.";
            return recovered;
        }
    }

    private static async Task<BlockConfiguration> ReadValidatedAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("La configuración debe ser un objeto JSON.");
        }
        if (document.RootElement.TryGetProperty("Version", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.GetInt32() > BlockConfiguration.CurrentVersion)
        {
            throw new UnsupportedConfigurationVersionException();
        }

        var configuration = JsonSerializer.Deserialize<BlockConfiguration>(bytes, SerializerOptions)
            ?? throw new InvalidDataException("La configuración está vacía.");
        Migrate(configuration);
        Validate(configuration);
        return configuration;
    }

    private static void Migrate(BlockConfiguration configuration)
    {
        if (configuration.Version is < 1 or > BlockConfiguration.CurrentVersion)
        {
            throw new InvalidDataException("La versión de configuración no es válida.");
        }

        if (configuration.ManualSession is null && configuration.ManualBlockEnabled == true)
        {
            configuration.ManualSession = new ManualSession { StartedAtUtc = DateTimeOffset.UtcNow };
        }

        if (configuration.Version < BlockConfiguration.CurrentVersion)
        {
            configuration.Version = BlockConfiguration.CurrentVersion;
        }

        configuration.ManualBlockEnabled = null;
    }

    private static void Validate(BlockConfiguration configuration)
    {
        if (configuration.Version != BlockConfiguration.CurrentVersion || configuration.Websites is null || configuration.Applications is null || configuration.Schedule?.Days is null
            || configuration.Schedule.Days.Any(day => !Enum.IsDefined(day))
            || configuration.ManualSession is { Id.Length: 0 }
            || configuration.ManualSession is { StartedAtUtc: var sessionStart } && sessionStart == default
            || configuration.ManualSession is { StartedAtUtc: var started, EndsAtUtc: var ends } && ends is not null && ends <= started
            || configuration.Websites.Any(value => !RuleNormalizer.TryNormalizeDomain(value, out _, out _))
            || configuration.Applications.Any(value => !RuleNormalizer.TryNormalizeApplication(value, out _, out _)))
        {
            throw new InvalidDataException("La configuración contiene reglas o un horario no válidos.");
        }
    }

    public async Task SaveAsync(BlockConfiguration configuration, CancellationToken cancellationToken = default)
    {
        Validate(configuration);
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(configuration, SerializerOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temporaryPath = FilePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await stream.WriteAsync(snapshot, cancellationToken);
                stream.Flush(true);
            }

            if (File.Exists(FilePath) && RecoveryMessage is null)
            {
                // Back up the previous file before replacing it. File.Move avoids
                // ReplaceFile's extra permission requirements for merging ACL metadata.
                var backupTemporaryPath = FilePath + ".bak.tmp";
                File.Copy(FilePath, backupTemporaryPath, true);
                File.Move(backupTemporaryPath, FilePath + ".bak", true);
            }
            File.Move(temporaryPath, FilePath, true);
            RecoveryMessage = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class UnsupportedConfigurationVersionException : Exception
{
    public UnsupportedConfigurationVersionException()
        : base("Esta configuración fue creada por una versión más nueva de Cold Ham. Actualiza la aplicación; no se modificó el archivo.") { }
}
