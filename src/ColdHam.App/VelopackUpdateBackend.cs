using ColdHam.Core;
using Velopack;
using Velopack.Sources;
using System.Reflection;

namespace ColdHam.App;

internal sealed class VelopackUpdateBackend : IUpdateBackend
{
    // Public release assets only. Never ship a GitHub access token in a desktop client.
    private readonly UpdateManager? _manager;
    private UpdateInfo? _update;

    public VelopackUpdateBackend()
    {
        var repository = typeof(VelopackUpdateBackend).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "UpdateRepositoryUrl")?.Value;
        if (!string.IsNullOrWhiteSpace(repository))
            _manager = new UpdateManager(new GithubSource(repository, null, false));
    }

    public bool IsInstalled => _manager?.IsInstalled == true;

    public async Task<bool> CheckAsync()
    {
        if (_manager is null) return false;
        _update = await _manager.CheckForUpdatesAsync();
        return _update is not null;
    }

    public Task DownloadAsync(CancellationToken cancellationToken) =>
        Manager.DownloadUpdatesAsync(_update ?? throw new InvalidOperationException("No update selected."), cancelToken: cancellationToken);

    private UpdateManager Manager => _manager ?? throw new InvalidOperationException("No update repository configured.");

    public void PrepareRestart() => Manager.WaitExitThenApplyUpdates(
        (_update ?? throw new InvalidOperationException("No update selected.")).TargetFullRelease,
        silent: true, restart: true);
}
