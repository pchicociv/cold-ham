using System.Diagnostics;
using System.Text;
using ColdHam.Core;

namespace ColdHam.Windows;

public sealed class HostsFileManager
{
    public const string BeginMarker = "# Cold Ham BEGIN";
    public const string EndMarker = "# Cold Ham END";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _flushDns;

    public HostsFileManager(string? hostsPath = null)
    {
        _flushDns = hostsPath is null;
        HostsPath = hostsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    }

    public string HostsPath { get; }

    public Task ApplyAsync(IEnumerable<string> domains, CancellationToken cancellationToken = default)
        => ApplyAsync(domains, [], cancellationToken);

    public Task ApplyAsync(IEnumerable<string> domains, IEnumerable<string> allowedDomains, CancellationToken cancellationToken = default)
    {
        var rules = domains.ToArray();
        var exceptions = allowedDomains.ToArray();
        return UpdateAsync(content => BuildContent(content, rules, exceptions), cancellationToken);
    }

    public Task RemoveAsync(CancellationToken cancellationToken = default) => UpdateAsync(RemoveManagedSection, cancellationToken);

    private async Task UpdateAsync(Func<string, string> transform, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var observed = await File.ReadAllBytesAsync(HostsPath, cancellationToken);
            var (observedEncoding, observedPreamble) = DetectEncoding(observed);
            var observedText = observedEncoding.GetString(observed, observedPreamble, observed.Length - observedPreamble);
            if (transform(observedText) == observedText) return;
            // Keep the file exclusively open throughout the read/modify/write operation.
            // A missing system hosts file is an actionable error, never silently recreated.
            await using var stream = new FileStream(HostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var originalBuffer = new MemoryStream();
            await stream.CopyToAsync(originalBuffer, cancellationToken);
            var original = originalBuffer.ToArray();
            var (encoding, preambleLength) = DetectEncoding(original);
            var existing = encoding.GetString(original, preambleLength, original.Length - preambleLength);
            var updated = transform(existing);
            if (updated == existing)
            {
                return;
            }

            var payload = encoding.GetBytes(updated);
            var replacement = original[..preambleLength].Concat(payload).ToArray();
            // A recovery copy is written before touching the original. Never restore this
            // entire copy over later edits by another application.
            var temporaryPath = HostsPath + ".coldham-recovery";
            await File.WriteAllBytesAsync(temporaryPath, original, cancellationToken);
            try
            {
                stream.Position = 0;
                await stream.WriteAsync(replacement, cancellationToken);
                stream.SetLength(replacement.Length);
                stream.Flush(true);
            }
            catch
            {
                stream.Position = 0;
                await stream.WriteAsync(original, CancellationToken.None);
                stream.SetLength(original.Length);
                stream.Flush(true);
                throw;
            }
        }
        finally
        {
            // Keep the recovery copy for diagnosis after a crash or partial disk failure.
            _gate.Release();
        }

        if (_flushDns) FlushDnsCache();
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
        {
            throw new InvalidDataException("El archivo hosts usa UTF-32. Conviértelo a UTF-8 antes de aplicar reglas.");
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, false, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, false, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(false, true), 3);
        // Latin-1 is a reversible byte mapping; generated rules and markers are ASCII.
        return (Encoding.Latin1, 0);
    }

    public static string BuildContent(string existing, IEnumerable<string> domains)
        => BuildContent(existing, domains, []);

    public static string BuildContent(string existing, IEnumerable<string> domains, IEnumerable<string> allowedDomains)
    {
        var range = FindManagedSection(existing);
        var normalized = WebsiteRules.GetBlockedDomains(domains, allowedDomains);
        if (normalized.Length == 0) return RemoveManagedSection(existing);
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var rules = normalized.SelectMany(domain => new[] { $"0.0.0.0 {domain}", $"::1 {domain}" });
        var section = string.Join(newline, new[] { BeginMarker }.Concat(rules).Concat(new[] { EndMarker }));
        if (range is { } owned)
        {
            return existing[..owned.Start] + section + existing[owned.End..];
        }
        // Do not add an unowned separator that could not be removed exactly later.
        if (existing.Length > 0 && existing[^1] != '\n')
        {
            return section + newline + existing;
        }
        return existing + section + newline;
    }

    public static string RemoveManagedSection(string content)
    {
        var range = FindManagedSection(content);
        if (range is not { } owned) return content;
        var end = owned.End;
        if (end < content.Length && content[end] == '\r') end++;
        if (end < content.Length && content[end] == '\n') end++;
        return content[..owned.Start] + content[end..];
    }

    private static (int Start, int End)? FindManagedSection(string content)
    {
        int? begin = null;
        int? end = null;
        var position = 0;
        foreach (var line in content.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Contains(BeginMarker, StringComparison.Ordinal) || text.Contains(EndMarker, StringComparison.Ordinal))
            {
                if (text == BeginMarker && begin is null && end is null) begin = position;
                else if (text == EndMarker && begin is not null && end is null) end = position + text.Length;
                else throw InvalidMarkers();
            }
            position += line.Length + 1;
        }
        if (begin is null && end is null) return null;
        if (begin is null || end is null) throw InvalidMarkers();
        return (begin.Value, end.Value);
    }

    private static InvalidDataException InvalidMarkers() => new(
        "La sección de Cold Ham en hosts tiene marcadores incompletos, duplicados o ambiguos. Revisa # Cold Ham BEGIN y # Cold Ham END; no se ha modificado el archivo.");

    private static void FlushDnsCache()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe"),
                Arguments = "/flushdns", CreateNoWindow = true, UseShellExecute = false
            });
            process?.WaitForExit(3000);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Cache flushing is best effort; the file itself has been written successfully.
        }
    }
}
