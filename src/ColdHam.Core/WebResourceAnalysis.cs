using System.Text;
using System.Text.Json;

namespace ColdHam.Core;

public sealed record ResourceDomain(string Domain, int Requests, bool HasNetworkFailure, bool HasHttpError);
public sealed record ResourceAnalysis(ResourceDomain[] Domains, int Ignored);

/// <summary>Offline analysis: retains domain names and failure categories, never URL paths or headers.</summary>
public static class WebResourceAnalysis
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    public const int MaximumEntries = 10000;

    public static ResourceAnalysis FromUrls(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw TooLarge();
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > MaximumEntries) throw TooLarge();
        return Collect(lines.Select(url => (Url: url, Network: false, Http: false)));
    }

    public static ResourceAnalysis FromHar(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw TooLarge();
        // Some exporters include a UTF-8 BOM.
        if (bytes.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            if (!TryProperty(json.RootElement, "log", out var log)
                || !TryProperty(log, "entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException(LocalizedText.Get("ResourceInvalidHar"));
            if (entries.GetArrayLength() > MaximumEntries) throw TooLarge();
            var urls = new List<(string Url, bool Network, bool Http)>();
            var ignored = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                var status = -1;
                if (TryProperty(entry, "response", out var response) && TryProperty(response, "status", out var statusValue)
                    && statusValue.ValueKind == JsonValueKind.Number && statusValue.TryGetInt32(out var number)) status = number;
                var network = status == 0 || (TryProperty(entry, "_error", out var error)
                    && error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()));
                var http = status >= 400 && status <= 599;
                if ((!network && !http) || !TryProperty(entry, "request", out var request)
                    || !TryProperty(request, "url", out var url) || url.ValueKind != JsonValueKind.String)
                { ignored++; continue; }
                urls.Add((url.GetString()!, network, http));
            }
            var result = Collect(urls);
            return result with { Ignored = result.Ignored + ignored };
        }
        catch (JsonException) { throw new InvalidDataException(LocalizedText.Get("ResourceInvalidHar")); }
    }

    private static ResourceAnalysis Collect(IEnumerable<(string Url, bool Network, bool Http)> urls)
    {
        var domains = new Dictionary<string, ResourceDomain>(StringComparer.OrdinalIgnoreCase);
        var ignored = 0;
        foreach (var item in urls)
        {
            if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")
                || !RuleNormalizer.TryNormalizeDomain(uri.Host, out var domain, out _))
            { ignored++; continue; }
            var previous = domains.GetValueOrDefault(domain);
            domains[domain] = new(domain, (previous?.Requests ?? 0) + 1,
                item.Network || previous?.HasNetworkFailure == true, item.Http || previous?.HasHttpError == true);
        }
        return new(domains.Values.OrderBy(value => value.Domain, StringComparer.Ordinal).ToArray(), ignored);
    }

    private static bool TryProperty(JsonElement value, string name, out JsonElement result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out result);
    }

    private static InvalidDataException TooLarge() => new(LocalizedText.Get("ResourceTooLarge"));
}
