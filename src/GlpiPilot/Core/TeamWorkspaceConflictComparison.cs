using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record TeamWorkspaceDifference(string Path, string Change, string? Current, string? Incoming,
    string? CurrentType, string? IncomingType, bool Truncated);
public sealed record TeamWorkspaceComparison(int DifferencesTotal, bool ScanComplete, bool Truncated,
    IReadOnlyList<TeamWorkspaceDifference> Differences);

/// <summary>Bounded, read-only comparison of the actual business fields, including typed payloads.</summary>
public static class TeamWorkspaceConflictComparison
{
    public const int MaximumNodes = 12000;
    public const int MaximumDifferences = 500;
    public const int MaximumValueCharacters = 4096;
    public const int MaximumPreviewCharacters = 400000;
    private sealed record Value(string Type, string Text);

    public static TeamWorkspaceComparison Compare(TeamWorkspaceItem current, TeamWorkspaceItem incoming, CancellationToken ct = default)
    {
        var left = Flatten(current, ct); var right = Flatten(incoming, ct);
        var rows = new List<TeamWorkspaceDifference>(); var total = 0; var budget = MaximumPreviewCharacters; var truncated = false;
        foreach (var path in left.Values.Keys.Union(right.Values.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            left.Values.TryGetValue(path, out var a); right.Values.TryGetValue(path, out var b);
            if (a == b) continue;
            total++;
            if (rows.Count >= MaximumDifferences || budget <= 0) { truncated = true; continue; }
            var cut = false;
            string? Preview(Value? value)
            {
                if (value is null) return null;
                var length = Math.Min(value.Text.Length, Math.Min(MaximumValueCharacters, budget));
                // Keep complete UTF-16 scalar pairs at the boundary.
                if (length > 0 && length < value.Text.Length && char.IsHighSurrogate(value.Text[length - 1])) length--;
                budget -= length;
                if (length < value.Text.Length) cut = true;
                return value.Text[..length];
            }
            var before = Preview(a); var after = Preview(b);
            rows.Add(new(path, a is null ? "added" : b is null ? "removed" : "changed", before, after, a?.Type, b?.Type, cut));
            truncated |= cut;
        }
        return new(total, left.Complete && right.Complete, truncated || !left.Complete || !right.Complete, rows);
    }

    private static (Dictionary<string, Value> Values, bool Complete) Flatten(TeamWorkspaceItem item, CancellationToken ct)
    {
        var values = new Dictionary<string, Value>(StringComparer.Ordinal); var complete = true; var nodes = 0;
        void Add(string path, Value value)
        {
            if (++nodes > MaximumNodes || path.Length > 2000) { complete = false; return; }
            if (!values.TryAdd(path, value)) complete = false;
        }
        void Visit(string path, JsonElement node)
        {
            ct.ThrowIfCancellationRequested();
            if (nodes >= MaximumNodes || path.Length > 2000) { complete = false; return; }
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    Add(path, new("object", "Objet"));
                    foreach (var property in node.EnumerateObject())
                    {
                        if (nodes >= MaximumNodes) { complete = false; break; }
                        Visit(path + "/" + property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal), property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    Add(path, new("array", "Liste · " + node.GetArrayLength())); var index = 0;
                    foreach (var child in node.EnumerateArray())
                    {
                        if (nodes >= MaximumNodes) { complete = false; break; }
                        Visit(path + "/" + index++, child);
                    }
                    break;
                case JsonValueKind.String: Add(path, new("text", node.GetString() ?? "")); break;
                case JsonValueKind.Number: Add(path, new("number", node.GetRawText())); break;
                case JsonValueKind.True: Add(path, new("boolean", "true")); break;
                case JsonValueKind.False: Add(path, new("boolean", "false")); break;
                case JsonValueKind.Null: Add(path, new("null", "")); break;
                default: complete = false; break;
            }
        }
        foreach (var field in new[] { ("kind", item.Kind), ("title", item.Title), ("body", item.Body),
            ("ticketKey", item.TicketKey), ("assignee", item.Assignee), ("status", item.Status), ("author", item.Author) })
            Add("/" + field.Item1, new("text", field.Item2));
        Add("/deleted", new("boolean", item.Deleted ? "true" : "false"));
        if (item.Payload is { } payload) Visit("/payload", payload);
        return (values, complete);
    }
}
