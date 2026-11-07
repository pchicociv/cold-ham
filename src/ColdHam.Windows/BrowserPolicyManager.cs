using System.Text.Json;
using ColdHam.Core;
using Microsoft.Win32;

namespace ColdHam.Windows;

/// <summary>Owns only newly created URL-policy values, recorded before writing them.</summary>
public sealed class BrowserPolicyManager
{
    public static readonly string[] PolicyPaths =
    [
        @"SOFTWARE\Policies\Google\Chrome\URLBlocklist",
        @"SOFTWARE\Policies\Google\Chrome\URLAllowlist",
        @"SOFTWARE\Policies\Microsoft\Edge\URLBlocklist",
        @"SOFTWARE\Policies\Microsoft\Edge\URLAllowlist"
    ];

    private readonly IBrowserPolicyRegistry _registry;
    private readonly string _journalPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BrowserPolicyManager(IBrowserPolicyRegistry? registry = null, string? journalPath = null)
    {
        _registry = registry ?? new BrowserPolicyRegistry();
        _journalPath = journalPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ColdHam", "browser-policy-journal.json");
    }

    public async Task ApplyAsync(IEnumerable<string> allowedWebsites)
    {
        var allowlist = BrowserPolicyPlan.BuildAllowlist(allowedWebsites);
        await _gate.WaitAsync();
        try
        {
            var previous = await ReadJournalAsync();
            foreach (var path in PolicyPaths)
            {
                if (_registry.HasMachinePolicy(path)) throw Conflict(path);
                var current = _registry.Read(path);
                var owned = previous.GetValueOrDefault(path) ?? [];
                if (current.Any(pair => !owned.TryGetValue(pair.Key, out var value) || !Equals(pair.Value, value)))
                    throw Conflict(path);
            }

            var next = PolicyPaths.ToDictionary(path => path, path =>
                (path.EndsWith("URLBlocklist", StringComparison.Ordinal) ? new[] { "*" } : allowlist)
                .Select((value, index) => (value, name: (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToDictionary(pair => pair.name, pair => pair.value));

            // Remove the old owned values first. On any failure, the journal remains
            // available for Stop/Retry or the next application startup.
            await RemoveOwnedAsync(previous);
            await WriteJournalAsync(next);
            try
            {
                // Install allow entries before enabling deny-all.
                foreach (var path in PolicyPaths.OrderBy(path => path.EndsWith("URLBlocklist", StringComparison.Ordinal)))
                {
                    if (_registry.Read(path).Count != 0 || _registry.HasMachinePolicy(path)) throw Conflict(path);
                    foreach (var (name, value) in next[path]) _registry.Write(path, name, value);
                }
            }
            catch
            {
                // Best-effort rollback. Never discard the journal if cleanup fails.
                try { await RemoveOwnedAsync(next); }
                catch { /* Stop/Retry will retry cleanup with the persisted journal. */ }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync()
    {
        await _gate.WaitAsync();
        try { await RemoveOwnedAsync(await ReadJournalAsync()); }
        finally { _gate.Release(); }
    }

    private Task RemoveOwnedAsync(Dictionary<string, Dictionary<string, string>> journal)
    {
        var conflicts = new List<string>();
        // Remove deny-all first, so a partial cleanup never leaves only a deny rule.
        foreach (var (path, values) in journal.OrderByDescending(pair => pair.Key.EndsWith("URLBlocklist", StringComparison.Ordinal)))
        {
            foreach (var (name, expected) in values)
            {
                var current = _registry.Read(path);
                if (!current.TryGetValue(name, out var actual)) continue;
                if (!Equals(actual, expected)) { conflicts.Add(path); continue; }
                _registry.Delete(path, name, expected);
            }
        }
        if (conflicts.Count > 0) throw Conflict(string.Join(", ", conflicts.Distinct()));
        if (File.Exists(_journalPath)) File.Delete(_journalPath);
        return Task.CompletedTask;
    }

    private async Task<Dictionary<string, Dictionary<string, string>>> ReadJournalAsync()
    {
        if (!File.Exists(_journalPath)) return [];
        try
        {
            var journal = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(await File.ReadAllBytesAsync(_journalPath));
            if (journal is null || journal.Any(pair => !PolicyPaths.Contains(pair.Key, StringComparer.Ordinal)
                || pair.Value is null || pair.Value.Any(value => !int.TryParse(value.Key, out var index)
                    || index < 1 || index > BrowserPolicyPlan.MaximumEntries || value.Value is null)))
                throw new InvalidDataException();
            return journal;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new InvalidDataException(LocalizedText.Format("ThePolicyRecoveryJournalIsInvalidBrowser", _journalPath), exception);
        }
    }

    private async Task WriteJournalAsync(Dictionary<string, Dictionary<string, string>> journal)
    {
        var directory = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = _journalPath + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, journal);
            stream.Flush(true);
        }
        File.Move(temporary, _journalPath, true);
    }

    private static InvalidOperationException Conflict(string path) => new(
        LocalizedText.Format("BrowserPoliciesNotOwnedByColdHam", path));
}

public interface IBrowserPolicyRegistry
{
    IReadOnlyDictionary<string, object?> Read(string path);
    bool HasMachinePolicy(string path);
    void Write(string path, string name, string value);
    void Delete(string path, string name, string expected);
}

internal sealed class BrowserPolicyRegistry : IBrowserPolicyRegistry
{
    public IReadOnlyDictionary<string, object?> Read(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValueNames().ToDictionary(name => name, name => key.GetValue(name)) ?? [];
    }

    public bool HasMachinePolicy(string path)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(path);
            if (key?.ValueCount > 0) return true;
        }
        return false;
    }

    public void Write(string path, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        if (key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new IOException(LocalizedText.Get("PolicyChangedDuringApply"));
        key.SetValue(name, value, RegistryValueKind.String);
        key.Flush();
    }

    public void Delete(string path, string name, string expected)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        if (key.GetValueKind(name) != RegistryValueKind.String || !Equals(key.GetValue(name), expected))
            throw new IOException(LocalizedText.Get("PolicyChangedDuringRemoval"));
        key.DeleteValue(name, throwOnMissingValue: false);
        key.Flush();
    }
}
