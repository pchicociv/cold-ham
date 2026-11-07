namespace ColdHam.Core;

public interface IUpdateBackend
{
    bool IsInstalled { get; }
    Task<bool> CheckAsync();
    Task DownloadAsync(CancellationToken cancellationToken);
    void PrepareRestart();
}

public enum UpdateStage { Unavailable, Idle, Checking, Downloading, Current, Ready, Failed, Restarting }

/// <summary>Serializes update work without coupling enforcement to network availability.</summary>
public sealed class UpdateCoordinator(IUpdateBackend backend, TimeProvider clock)
{
    public UpdateStage Stage { get; private set; } = backend.IsInstalled ? UpdateStage.Idle : UpdateStage.Unavailable;
    public string? Error { get; private set; }
    public bool Busy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Restarting;
    public event EventHandler? Changed;
    private DateTimeOffset _nextCheck;

    public async Task CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!backend.IsInstalled || Busy || Stage == UpdateStage.Ready || (!force && clock.GetUtcNow() < _nextCheck)) return;
        Error = null;
        SetStage(UpdateStage.Checking);
        try
        {
            var available = await backend.CheckAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (available)
            {
                SetStage(UpdateStage.Downloading);
                await backend.DownloadAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            _nextCheck = clock.GetUtcNow().AddHours(6);
            SetStage(available ? UpdateStage.Ready : UpdateStage.Current);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _nextCheck = clock.GetUtcNow().AddMinutes(30);
            SetStage(UpdateStage.Failed);
        }
    }

    // The safety predicate runs immediately before starting the updater, without an await.
    public bool TryPrepareRestart(Func<bool> canRestart)
    {
        if (Stage != UpdateStage.Ready || !canRestart()) return false;
        try
        {
            backend.PrepareRestart();
            SetStage(UpdateStage.Restarting);
            return true;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _nextCheck = clock.GetUtcNow().AddMinutes(30);
            SetStage(UpdateStage.Failed);
            return false;
        }
    }

    private void SetStage(UpdateStage stage)
    {
        Stage = stage;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
