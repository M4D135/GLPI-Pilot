using System.Globalization;
using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    // Null means no shared search; an empty set means a search with zero matches.
    private HashSet<int>? TicketSearchRows(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("ticketSearch", out var search)) return null;
        if (search.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Recherche de tickets invalide.");
        CheckRevision(search);
        var data = RequireResult(); var settings = EffectiveSettings.Dashboard;
        var indices = search.TryGetProperty("indices", out var input) ? Read<int[]>(input) : null;
        if (indices is not null && (indices.Length > 1000000 || indices.Any(i => i < 0 || i >= data.Rows.Count)))
            throw new InvalidDataException("La sélection de recherche n’existe plus. Revenez à la recherche.");
        if (Flag(search, "basketOnly"))
        {
            var basket = RecordBasketRows(data, settings);
            indices = indices is null ? basket : indices.Intersect(basket).ToArray();
        }
        indices = RecordScopedRows(search, indices, ct);
        var transform = search.TryGetProperty("transform", out var filters) ? Read<PreviewTransform>(filters) : new();
        var rows = _recordQueries.Query(data, Revision, Text(search, "search", 4000), indices, transform,
            CultureInfo.GetCultureInfo(settings.DateCulture), ct).SourceIndices.ToHashSet();
        if (search.TryGetProperty("selectedRows", out var selection))
        {
            var selected = Read<int[]>(selection);
            if (selected.Length > 1000000 || selected.Any(i => !rows.Contains(i)))
                throw new InvalidDataException("La sélection ne correspond plus à la recherche. Revenez à la liste.");
            rows.IntersectWith(selected);
        }
        return rows;
    }
    private HashSet<string>? TicketSearchKeys(JsonElement args, CancellationToken ct)
    {
        var rows = TicketSearchRows(args, ct);
        return rows is null ? null : CalendarCatalog(ct).Where(ticket => rows.Contains(ticket.RowIndex))
            .Select(ticket => ticket.Key).ToHashSet(StringComparer.Ordinal);
    }
}
