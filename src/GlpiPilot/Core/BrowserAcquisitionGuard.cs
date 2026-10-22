using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed record BrowserScopeEvidence(string Fingerprint, bool Known);

public sealed partial class BrowserService
{
    public async Task<TableSnapshot> ReadVerificationSampleAsync(TableChoice table, CancellationToken ct)
    {
        var (_, snapshot, _) = await ReadAsync(table, "", ct);
        return snapshot;
    }

    public async Task<BrowserScopeEvidence> CaptureScopeEvidenceAsync(TableChoice table, string requestedUrl, CancellationToken ct)
    {
        var frame = await ResolveFrameAsync(table, ct);
        var explicitCriteria = new Uri(requestedUrl).Query.TrimStart('?').Split('&').Any(part =>
            Uri.UnescapeDataString(part.Split('=', 2)[0]).StartsWith("criteria[", StringComparison.OrdinalIgnoreCase)
            || Uri.UnescapeDataString(part.Split('=', 2)[0]).StartsWith("metacriteria[", StringComparison.OrdinalIgnoreCase));
        try
        {
            var json = await frame.EvaluateAsync<string>("""
                () => {
                  const controls = Array.from(document.querySelectorAll('input[name], select[name], textarea[name]'));
                  const fields = controls.filter(e => /^(meta)?criteria\[/.test(e.name));
                  const context = controls.filter(e => /^(active_entity|entities_id|newprofile|profiles_id)$/.test(e.name));
                  const read = nodes => nodes.slice(0,500).filter(e => !['checkbox','radio'].includes(e.type) || e.checked)
                    .map(e => [e.name, e.tagName === 'SELECT' ? Array.from(e.selectedOptions).map(o => o.value).sort().join('|') : e.value])
                    .sort((a,b) => JSON.stringify(a).localeCompare(JSON.stringify(b)));
                  return JSON.stringify({known: fields.length > 0 && fields.length <= 500 && context.length <= 500, overflow: fields.length > 500 || context.length > 500, fields: read(fields), context: read(context)});
                }
                """).WaitAsync(ct);
            if (json.Length > 128000) return new("", false);
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.GetProperty("overflow").GetBoolean()) return new("", false);
            var known = parsed.RootElement.GetProperty("known").GetBoolean() || explicitCriteria;
            // GLPI can materialize an already visible criterion in the URL when
            // submitting its pager. Ignore only URL pairs with an identical DOM
            // control value; a changed URL criterion or an unseen filter remains
            // part of the fingerprint and cannot be hidden by a stale form.
            var represented = new HashSet<string>(StringComparer.Ordinal);
            foreach (var collection in new[] { "fields", "context" })
                foreach (var pair in parsed.RootElement.GetProperty(collection).EnumerateArray())
                    represented.Add(QueryPair(pair[0].GetString() ?? "", pair[1].GetString() ?? ""));
            var context = ScopeLocation(frame.Url, represented) + "\n" + ScopeLocation(table.Page!.Url, represented) + "\n" + json;
            return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context))), known);
        }
        catch (Exception error) when (error is PlaywrightException or JsonException)
        { return new("", false); }
    }

    internal static string ScopeLocation(string address, IReadOnlySet<string>? representedControls = null)
    {
        var uri = TargetUrlPolicy.Validate(TargetUrlPolicy.PersistedTarget(address));
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Select(parts => (Name: DecodeQuery(parts[0]), Value: parts.Length > 1 ? DecodeQuery(parts[1]) : ""))
            // These controls alter order or page size, not membership. Filters
            // such as is_deleted or unrecognized plugin parameters remain.
            .Where(pair => pair.Name.ToLowerInvariant() is not ("start" or "list_limit" or "glpilist_limit" or "sort" or "order")
                && !pair.Name.StartsWith("sort[", StringComparison.OrdinalIgnoreCase)
                && !pair.Name.StartsWith("order[", StringComparison.OrdinalIgnoreCase))
            .Select(pair => QueryPair(pair.Name, pair.Value))
            .Where(pair => representedControls?.Contains(pair) != true)
            .Order(StringComparer.Ordinal);
        return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', query);
    }

    private static string DecodeQuery(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    private static string QueryPair(string name, string value) => Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
}
