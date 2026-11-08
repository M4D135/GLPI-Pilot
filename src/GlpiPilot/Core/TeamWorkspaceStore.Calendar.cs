using System.IO;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

public sealed partial class TeamWorkspaceStore
{
    private static async Task InitializeCalendarIndicesAsync(SqliteConnection db, CancellationToken ct)
    {
        // Additive indices keep the existing v1 shared-document schema and sync protocol compatible.
        await using var command = Command(db, null, """
            CREATE INDEX IF NOT EXISTS gp_team_calendar_window ON gp_team_items(kind,deleted,
              json_extract(document_json,'$.payload.startLocal'),json_extract(document_json,'$.payload.endLocal'));
            """);
        await command.ExecuteNonQueryAsync(ct);
    }
    /// <summary>One bounded read with payloads; recurring and moved occurrences are filtered exactly by the calendar engine.</summary>
    public async Task<IReadOnlyList<TeamWorkspaceItem>> CalendarItemsAsync(string fromLocal, string toLocal, CancellationToken ct = default)
    {
        if (fromLocal.Length != 16 || toLocal.Length != 16 || string.CompareOrdinal(fromLocal, toLocal) >= 0) throw new InvalidDataException("Période de calendrier invalide.");
        await using var db = await Open(ct);
        await using var query = Command(db, null, """
            SELECT document_json,EXISTS(SELECT 1 FROM gp_team_outbox o WHERE o.item_id=i.id),
              (SELECT COUNT(*) FROM gp_team_conflicts c WHERE c.item_id=i.id)
            FROM gp_team_items i WHERE deleted=0 AND kind='calendar-intervention' AND (
              (json_extract(document_json,'$.payload.startLocal') < @to AND json_extract(document_json,'$.payload.endLocal') > @from)
              OR json_type(document_json,'$.payload.recurrence')='object'
              OR json_array_length(document_json,'$.payload.exceptions')>0)
            ORDER BY id LIMIT 10001
            """, ("@from", fromLocal), ("@to", toLocal));
        return await ReadCalendarRowsAsync(query, 10000, ct);
    }
    public async Task<IReadOnlyList<TeamWorkspaceItem>> CalendarResourcesAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var query = Command(db, null, """
            SELECT document_json,EXISTS(SELECT 1 FROM gp_team_outbox o WHERE o.item_id=i.id),
              (SELECT COUNT(*) FROM gp_team_conflicts c WHERE c.item_id=i.id)
            FROM gp_team_items i WHERE deleted=0 AND kind='calendar-resource' ORDER BY id LIMIT 2001
            """);
        return await ReadCalendarRowsAsync(query, 2000, ct);
    }
    public async Task<long> CalendarPendingCountAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var query = Command(db, null, "SELECT COUNT(*) FROM gp_team_outbox WHERE json_extract(document_json,'$.kind') IN('calendar-intervention','calendar-resource')");
        return Convert.ToInt64(await query.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
    private static async Task<IReadOnlyList<TeamWorkspaceItem>> ReadCalendarRowsAsync(SqliteCommand query, int maximum, CancellationToken ct)
    {
        await using var reader = await query.ExecuteReaderAsync(ct); var items = new List<TeamWorkspaceItem>(); long bytes = 0;
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(0); bytes += System.Text.Encoding.UTF8.GetByteCount(json);
            if (items.Count == maximum || bytes > 64 * 1024 * 1024) throw new InvalidDataException("Le calendrier dépasse la capacité de cette vue. Archivez les anciennes séries avant de continuer ; aucun résultat partiel n’est affiché.");
            var item = TeamWorkspaceCodec.Read(json); item.Pending = reader.GetInt64(1) != 0; item.Conflicts = checked((int)reader.GetInt64(2)); items.Add(item);
        }
        return items;
    }
    /// <summary>Atomic series split / multi-edit: every expected revision is checked before the first queued mutation.</summary>
    public async Task<IReadOnlyList<TeamWorkspaceItem>> SaveCalendarBatchAsync(IReadOnlyList<(TeamWorkspaceItem Item, long? ExpectedRevision)> proposed, CancellationToken ct = default)
    {
        if (proposed.Count is < 1 or > 200 || proposed.Select(entry => entry.Item.Id).Distinct(StringComparer.Ordinal).Count() != proposed.Count
            || proposed.Any(entry => !CalendarWorkspaceDocuments.IsKind(entry.Item.Kind))) throw new InvalidDataException("Lot de modifications du calendrier invalide.");
        var candidates = proposed.Select(entry => (Item: entry.Item.Copy(), entry.ExpectedRevision)).ToArray();
        await using var db = await Open(ct); using var tx = db.BeginTransaction(); var previous = new Dictionary<string, TeamWorkspaceItem?>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested(); var old = await ReadItem(db, tx, candidate.Item.Id, ct); previous.Add(candidate.Item.Id, old);
            if (old is not null && candidate.ExpectedRevision != old.Revision || old is null && candidate.ExpectedRevision is not (null or 0))
                throw new InvalidOperationException("Le planning a changé sur ce poste. Rechargez l’intervention avant d’enregistrer ; votre saisie est conservée.");
            if (old is not null && old.Kind != candidate.Item.Kind) throw new InvalidOperationException("Le type de document de calendrier ne peut pas changer.");
        }
        var saved = new List<TeamWorkspaceItem>(); var writtenIds = new List<string>();
        foreach (var candidate in candidates)
        {
            var next = candidate.Item; var old = previous[next.Id];
            next.CreatedAt = old?.CreatedAt ?? DateTimeOffset.UtcNow; next.UpdatedAt = DateTimeOffset.UtcNow;
            next.CentralRevision = old?.CentralRevision ?? 0; next.Revision = (old?.Revision ?? 0) + 1; next.Pending = true; next.Conflicts = old?.Conflicts ?? 0;
            if (old is not null && TeamWorkspaceCodec.ContentHash(old) == TeamWorkspaceCodec.ContentHash(next)) { saved.Add(old); continue; }
            await Put(db, tx, next, ct); await Queue(db, tx, next, ct); saved.Add(next); writtenIds.Add(next.Id);
        }
        if (writtenIds.Count > 0) await QueueCalendarBatchAsync(db, tx, writtenIds, ct);
        tx.Commit(); return saved;
    }
}
