using System.IO;

namespace GlpiPilot.Core;

public sealed partial class TeamWorkspaceStore
{
    public async Task<IReadOnlyList<TeamWorkspaceItem>> BusinessMetadataAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var query = Command(db, null, "SELECT id,kind,revision,central_revision,deleted FROM gp_team_items WHERE kind IN ('team-configuration','team-tour','team-technician','team-dispatch','team-dataset','team-location','team-order') ORDER BY kind,id LIMIT 25001");
        await using var reader = await query.ExecuteReaderAsync(ct); var items = new List<TeamWorkspaceItem>();
        while (await reader.ReadAsync(ct))
        {
            if (items.Count >= 25000) throw new InvalidDataException("L’espace commun dépasse 25 000 documents métier.");
            items.Add(new() { Id = reader.GetString(0), Kind = reader.GetString(1), Revision = reader.GetInt64(2), CentralRevision = reader.GetInt64(3), Deleted = reader.GetInt64(4) != 0 });
        }
        return items;
    }
    public async Task<IReadOnlyList<SharedDatasetSummary>> BusinessDatasetsAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var query = Command(db, null, """
            SELECT id,revision,json_extract(document_json,'$.payload.data.sourceKey'),json_extract(document_json,'$.payload.data.title'),
              json_extract(document_json,'$.payload.data.observedAt'),json_extract(document_json,'$.payload.data.rows'),json_extract(document_json,'$.payload.data.columns'),json_extract(document_json,'$.payload.data.urlKey')
            FROM gp_team_items WHERE kind='team-dataset' AND deleted=0 ORDER BY updated_at DESC,id LIMIT 201
            """);
        await using var reader = await query.ExecuteReaderAsync(ct); var items = new List<SharedDatasetSummary>();
        while (await reader.ReadAsync(ct)) items.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture), reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7)));
        return items;
    }
    public async Task<IReadOnlyList<SharedBusinessConflictSummary>> BusinessConflictsAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var query = Command(db, null, """
            SELECT i.id,c.id,i.revision,json_extract(i.document_json,'$.title'),i.kind,c.recorded_at
            FROM gp_team_conflicts c JOIN gp_team_items i ON i.id=c.item_id
            WHERE i.kind IN ('team-configuration','team-tour','team-technician','team-dispatch','team-dataset','team-location','team-order')
              AND NOT EXISTS(SELECT 1 FROM json_each(i.document_json,'$.payload.acknowledgedConflicts') a WHERE a.value=c.id)
            ORDER BY c.recorded_at DESC,c.id LIMIT 101
            """);
        await using var reader = await query.ExecuteReaderAsync(ct); var items = new List<SharedBusinessConflictSummary>();
        while (await reader.ReadAsync(ct)) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
            SharedBusinessDocuments.Label(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
        return items;
    }
}
public sealed record SharedDatasetSummary(string Id, long Revision, string SourceKey, string Title, DateTimeOffset ObservedAt, int Rows, int Columns, string UrlKey);
public sealed record SharedBusinessConflictSummary(string Id, string ConflictId, long Revision, string Title, string Kind, string Label, DateTimeOffset RecordedAt);
