using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

public sealed partial class TeamWorkspaceStore
{
    private static async Task InitializeCalendarSyncAsync(SqliteConnection db, CancellationToken ct)
    {
        await using var schema = Command(db, null, """
            CREATE TABLE IF NOT EXISTS gp_team_calendar_outbox(item_id TEXT PRIMARY KEY,batch_id TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS gp_team_calendar_batch ON gp_team_calendar_outbox(batch_id);
            CREATE TABLE IF NOT EXISTS gp_team_calendar_resolutions(batch_id TEXT NOT NULL,group_id TEXT NOT NULL,PRIMARY KEY(batch_id,group_id));
            CREATE TABLE IF NOT EXISTS gp_team_calendar_conflict_groups(group_id TEXT NOT NULL,item_id TEXT NOT NULL,conflict_id TEXT NOT NULL,recorded_at TEXT NOT NULL,resolved INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(group_id,item_id));
            """);
        await schema.ExecuteNonQueryAsync(ct);
        using var tx = db.BeginTransaction(); await EnsureCalendarMembershipAsync(db, tx, ct); tx.Commit();
    }
    private static async Task EnsureCalendarMembershipAsync(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        await using (var stale = Command(db, tx, "DELETE FROM gp_team_calendar_outbox WHERE item_id NOT IN(SELECT item_id FROM gp_team_outbox)")) await stale.ExecuteNonQueryAsync(ct);
        var missing = new List<string>();
        await using (var query = Command(db, tx, "SELECT item_id FROM gp_team_outbox WHERE json_extract(document_json,'$.kind') IN('calendar-intervention','calendar-resource') AND item_id NOT IN(SELECT item_id FROM gp_team_calendar_outbox)"))
        await using (var reader = await query.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) missing.Add(reader.GetString(0));
        foreach (var id in missing)
        { await using var save = Command(db, tx, "INSERT INTO gp_team_calendar_outbox(item_id,batch_id) VALUES(@id,@batch)", ("@id", id), ("@batch", Guid.NewGuid().ToString("N"))); await save.ExecuteNonQueryAsync(ct); }
    }
    /// <summary>Pending edits that touch a member of an earlier batch merge with that whole batch; an older in-flight receipt cannot split the new transaction.</summary>
    private static async Task QueueCalendarBatchAsync(SqliteConnection db, SqliteTransaction tx, IReadOnlyCollection<string> writtenIds, CancellationToken ct)
    {
        if (writtenIds.Count == 0) return;
        var ids = writtenIds.ToHashSet(StringComparer.Ordinal); var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in writtenIds)
        {
            await using var query = Command(db, tx, "SELECT batch_id FROM gp_team_calendar_outbox WHERE item_id=@id", ("@id", id));
            if ((string?)await query.ExecuteScalarAsync(ct) is { } group) groups.Add(group);
        }
        foreach (var group in groups)
        {
            await using var query = Command(db, tx, "SELECT b.item_id FROM gp_team_calendar_outbox b INNER JOIN gp_team_outbox o ON o.item_id=b.item_id WHERE batch_id=@batch", ("@batch", group));
            await using var reader = await query.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        }
        if (ids.Count > 200) throw new InvalidDataException("Ce lot de calendrier fusionne plus de 200 modifications en attente. Synchronisez les lots précédents avant de poursuivre.");
        var batch = Guid.NewGuid().ToString("N"); long bytes = 0;
        foreach (var id in ids)
        {
            await using var query = Command(db, tx, "SELECT document_json FROM gp_team_outbox WHERE item_id=@id", ("@id", id)); var json = (string?)await query.ExecuteScalarAsync(ct);
            if (json is null) continue;
            var item = TeamWorkspaceCodec.Read(json); if (!CalendarWorkspaceDocuments.IsKind(item.Kind)) throw new InvalidDataException("Un lot du calendrier ne peut contenir que ses interventions et ressources.");
            bytes += Encoding.UTF8.GetByteCount(json);
            if (bytes > TeamWorkspaceCodec.MaximumBatchBytes) throw new InvalidDataException("Le lot de calendrier dépasse 32 Mio ; divisez les modifications avant d’enregistrer.");
            await using var save = Command(db, tx, "INSERT INTO gp_team_calendar_outbox(item_id,batch_id) VALUES(@id,@batch) ON CONFLICT(item_id) DO UPDATE SET batch_id=excluded.batch_id", ("@id", id), ("@batch", batch));
            await save.ExecuteNonQueryAsync(ct);
        }
        foreach (var group in groups)
        {
            await using var move = Command(db, tx, "INSERT OR IGNORE INTO gp_team_calendar_resolutions(batch_id,group_id) SELECT @next,group_id FROM gp_team_calendar_resolutions WHERE batch_id=@old; DELETE FROM gp_team_calendar_resolutions WHERE batch_id=@old", ("@next", batch), ("@old", group)); await move.ExecuteNonQueryAsync(ct);
        }
    }
    private static async Task ResetCalendarBatchIdentityAsync(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        await EnsureCalendarMembershipAsync(db, tx, ct); var groups = new List<string>();
        await using (var query = Command(db, tx, "SELECT DISTINCT batch_id FROM gp_team_calendar_outbox"))
        await using (var reader = await query.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) groups.Add(reader.GetString(0));
        foreach (var group in groups)
        { await using var save = Command(db, tx, "UPDATE gp_team_calendar_outbox SET batch_id=@next WHERE batch_id=@old; UPDATE gp_team_calendar_resolutions SET batch_id=@next WHERE batch_id=@old", ("@next", Guid.NewGuid().ToString("N")), ("@old", group)); await save.ExecuteNonQueryAsync(ct); }
        var families = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var query = Command(db, tx, "SELECT item_id,COALESCE(NULLIF(json_extract(document_json,'$.payload.seriesId'),''),item_id) FROM gp_team_outbox WHERE json_extract(document_json,'$.kind')='calendar-intervention'"))
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) { var family = reader.GetString(1); if (!families.TryGetValue(family, out var ids)) families[family] = ids = new(); ids.Add(reader.GetString(0)); }
        foreach (var family in families.Values.Where(ids => ids.Count > 1)) await QueueCalendarBatchAsync(db, tx, family, ct);
    }
    internal async Task<TeamWorkspaceBatchMutation?> PendingCalendarBatchAsync(CancellationToken ct)
    {
        await using var db = await Open(ct); using var tx = db.BeginTransaction(); await EnsureCalendarMembershipAsync(db, tx, ct);
        await using var first = Command(db, tx, "SELECT batch_id FROM gp_team_calendar_outbox ORDER BY batch_id LIMIT 1"); var id = (string?)await first.ExecuteScalarAsync(ct);
        if (id is null) { tx.Commit(); return null; }
        await using var query = Command(db, tx, "SELECT o.mutation_id,o.base_revision,o.document_json FROM gp_team_calendar_outbox b INNER JOIN gp_team_outbox o ON o.item_id=b.item_id WHERE b.batch_id=@batch ORDER BY o.item_id", ("@batch", id));
        var values = new List<TeamWorkspaceMutation>();
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) values.Add(new(reader.GetString(0), reader.GetInt64(1), TeamWorkspaceCodec.Read(reader.GetString(2))));
        var resolves = new List<string>();
        await using (var resolutions = Command(db, tx, "SELECT group_id FROM gp_team_calendar_resolutions WHERE batch_id=@batch ORDER BY group_id", ("@batch", id)))
        await using (var reader = await resolutions.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) resolves.Add(reader.GetString(0));
        var mutation = new TeamWorkspaceBatchMutation(id, values) { ResolvesGroups = resolves }; TeamWorkspaceCalendarProtocol.Validate(mutation); tx.Commit(); return mutation;
    }
    internal async Task AcknowledgeCalendarBatchAsync(TeamWorkspaceBatchMutation batch, TeamWorkspaceBatchReceipt receipt, CancellationToken ct)
    {
        TeamWorkspaceCalendarProtocol.ValidateReceipt(batch, receipt);
        await using var db = await Open(ct); using var tx = db.BeginTransaction();
        foreach (var mutation in batch.Mutations)
        {
            await using var pending = Command(db, tx, "SELECT o.mutation_id,b.batch_id FROM gp_team_outbox o INNER JOIN gp_team_calendar_outbox b ON b.item_id=o.item_id WHERE o.item_id=@id", ("@id", mutation.Item.Id));
            await using var reader = await pending.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != mutation.MutationId || reader.GetString(1) != batch.MutationId) return;
        }
        foreach (var mutation in batch.Mutations)
        {
            var current = receipt.Receipts.Single(value => value.Current.Id == mutation.Item.Id); var old = await ReadItem(db, tx, mutation.Item.Id, ct);
            var next = current.Current.Copy(); next.Revision = (old?.Revision ?? 0) + 1; next.Pending = false;
            if (receipt.Outcome == "conflict") await RecordCalendarConflictAsync(db, tx, batch.MutationId, next, mutation.Item, ct);
            await Put(db, tx, next, ct);
            await using var clear = Command(db, tx, "DELETE FROM gp_team_outbox WHERE item_id=@id AND mutation_id=@mutation; DELETE FROM gp_team_calendar_outbox WHERE item_id=@id AND batch_id=@batch", ("@id", next.Id), ("@mutation", mutation.MutationId), ("@batch", batch.MutationId));
            await clear.ExecuteNonQueryAsync(ct);
        }
        foreach (var group in receipt.ResolvedGroups)
        { await using var resolve = Command(db, tx, "UPDATE gp_team_calendar_conflict_groups SET resolved=1 WHERE group_id=@id", ("@id", group)); await resolve.ExecuteNonQueryAsync(ct); }
        await using (var clear = Command(db, tx, "DELETE FROM gp_team_calendar_resolutions WHERE batch_id=@id", ("@id", batch.MutationId))) await clear.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }
    internal async Task<int> ApplyCalendarChangesAsync(IReadOnlyList<TeamWorkspaceChange> changes, CancellationToken ct)
    {
        TeamWorkspaceCalendarProtocol.ValidateChanges(changes); var conflicts = 0;
        await using var db = await Open(ct); using var tx = db.BeginTransaction();
        await EnsureCalendarMembershipAsync(db, tx, ct);
        var pendingGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var old = await ReadItem(db, tx, change.Item.Id, ct);
            if (old is not { Pending: true } || old.CentralRevision >= change.Item.CentralRevision || TeamWorkspaceCodec.ContentHash(old) == TeamWorkspaceCodec.ContentHash(change.Item)) continue;
            await using var query = Command(db, tx, "SELECT batch_id FROM gp_team_calendar_outbox WHERE item_id=@id", ("@id", old.Id));
            if ((string?)await query.ExecuteScalarAsync(ct) is { } pendingGroup) pendingGroups.Add(pendingGroup);
        }
        foreach (var pendingGroup in pendingGroups)
        {
            var pending = new List<TeamWorkspaceItem>();
            await using (var query = Command(db, tx, "SELECT o.document_json FROM gp_team_outbox o INNER JOIN gp_team_calendar_outbox b ON b.item_id=o.item_id WHERE b.batch_id=@batch ORDER BY o.item_id", ("@batch", pendingGroup)))
            await using (var reader = await query.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) pending.Add(TeamWorkspaceCodec.Read(reader.GetString(0)));
            foreach (var proposal in pending)
            {
                var retained = changes.FirstOrDefault(change => change.Item.Id == proposal.Id)?.Item ?? await ReadItem(db, tx, proposal.Id, ct)
                    ?? throw new InvalidDataException("Un membre du lot local en attente a disparu.");
                if (await RecordCalendarConflictAsync(db, tx, pendingGroup, retained, proposal, ct)) conflicts++;
            }
        }
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested(); var old = await ReadItem(db, tx, change.Item.Id, ct);
            if (change.Incoming is { } incoming && await RecordCalendarConflictAsync(db, tx, change.BatchId, change.Item, incoming, ct)) conflicts++;
            if (old is null || old.CentralRevision < change.Item.CentralRevision)
            {
                var next = change.Item.Copy(); next.Revision = (old?.Revision ?? 0) + 1; next.Pending = old?.Pending ?? false; await Put(db, tx, next, ct);
            }
        }
        foreach (var group in changes[0].ResolvedGroups)
        { await using var resolve = Command(db, tx, "UPDATE gp_team_calendar_conflict_groups SET resolved=1 WHERE group_id=@id", ("@id", group)); await resolve.ExecuteNonQueryAsync(ct); }
        await using var cursor = Command(db, tx, "INSERT INTO gp_team_meta(name,value) VALUES('cursor',@cursor) ON CONFLICT(name) DO UPDATE SET value=excluded.value", ("@cursor", changes[^1].Sequence.ToString(CultureInfo.InvariantCulture)));
        await cursor.ExecuteNonQueryAsync(ct); tx.Commit(); return conflicts;
    }
    private static async Task<bool> RecordCalendarConflictAsync(SqliteConnection db, SqliteTransaction tx, string groupId, TeamWorkspaceItem current, TeamWorkspaceItem incoming, CancellationToken ct)
    {
        var added = await Conflict(db, tx, current, incoming, "Lot de calendrier concurrent : toutes les versions sont conservées (" + groupId + ").", ct);
        await using var group = Command(db, tx, "INSERT INTO gp_team_calendar_conflict_groups(group_id,item_id,conflict_id,recorded_at,resolved) VALUES(@group,@item,@conflict,@at,0) ON CONFLICT(group_id,item_id) DO UPDATE SET conflict_id=excluded.conflict_id",
            ("@group", groupId), ("@item", current.Id), ("@conflict", TeamWorkspaceCodec.ConflictKey(current, incoming)), ("@at", DateTimeOffset.UtcNow.ToString("O")));
        await group.ExecuteNonQueryAsync(ct); return added;
    }
    public async Task<IReadOnlyList<CalendarConflictGroupSummary>> CalendarConflictGroupsAsync(int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        if (offset is < 0 or > 1000000 || limit is < 1 or > 100) throw new InvalidDataException("Pagination des versions du calendrier invalide.");
        await using var db = await Open(ct); await using var query = Command(db, null, "SELECT group_id,MIN(recorded_at),COUNT(*),MIN(resolved) FROM gp_team_calendar_conflict_groups GROUP BY group_id HAVING MIN(resolved)=0 ORDER BY MIN(recorded_at) DESC,group_id LIMIT @limit OFFSET @offset", ("@limit", limit), ("@offset", offset));
        var result = new List<CalendarConflictGroupSummary>(); await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), checked((int)reader.GetInt64(2)), reader.GetInt64(3) != 0));
        return result;
    }
    public async Task<long> CalendarConflictGroupCountAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct); await using var query = Command(db, null, "SELECT COUNT(DISTINCT group_id) FROM gp_team_calendar_conflict_groups WHERE resolved=0");
        return Convert.ToInt64(await query.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
    public async Task<CalendarConflictGroup?> CalendarConflictGroupAsync(string id, CancellationToken ct = default)
    { await using var db = await Open(ct); return await ReadCalendarConflictGroupAsync(db, null, id, ct); }
    private static async Task<CalendarConflictGroup?> ReadCalendarConflictGroupAsync(SqliteConnection db, SqliteTransaction? tx, string id, CancellationToken ct)
    {
        if (id.Length != 32) throw new InvalidDataException("Identité du lot concurrent invalide.");
        await using var query = Command(db, tx, "SELECT c.id,c.item_id,c.reason,c.recorded_at,c.current_json,c.incoming_json,g.resolved FROM gp_team_calendar_conflict_groups g INNER JOIN gp_team_conflicts c ON c.id=g.conflict_id WHERE g.group_id=@id ORDER BY c.item_id", ("@id", id));
        var members = new List<TeamWorkspaceConflict>(); var resolved = true; long bytes = 0;
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var current = reader.GetString(4); var incoming = reader.GetString(5); bytes += Encoding.UTF8.GetByteCount(current) + Encoding.UTF8.GetByteCount(incoming);
                if (members.Count >= 200 || bytes > 2L * TeamWorkspaceCodec.MaximumBatchBytes) throw new InvalidDataException("Lot de versions trop volumineux.");
                members.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), TeamWorkspaceCodec.Read(current), TeamWorkspaceCodec.Read(incoming)));
                resolved &= reader.GetInt64(6) != 0;
            }
        if (members.Count == 0) return null; var latest = new List<TeamWorkspaceItem>();
        foreach (var member in members) latest.Add(await ReadItem(db, tx, member.ItemId, ct) ?? throw new InvalidDataException("Une identité du lot a disparu du registre local."));
        return new(id, members.Min(value => value.RecordedAt), members, latest, resolved);
    }
    public async Task<IReadOnlyList<TeamWorkspaceItem>> ResolveCalendarConflictAsync(string id, string choice, IReadOnlyDictionary<string, long> expectedRevisions, CancellationToken ct = default, string actor = "")
    {
        if (choice is not ("current" or "incoming")) throw new InvalidDataException("Choisissez la version actuelle ou la proposition complète.");
        await using var db = await Open(ct); using var tx = db.BeginTransaction(); var group = await ReadCalendarConflictGroupAsync(db, tx, id, ct) ?? throw new InvalidDataException("Lot de versions introuvable.");
        if (group.Resolved) throw new InvalidOperationException("Ce lot a déjà été rapproché. Rechargez les versions.");
        if (!group.Latest.Select(value => value.Id).ToHashSet(StringComparer.Ordinal).SetEquals(expectedRevisions.Keys)
            || group.Latest.Any(value => !expectedRevisions.TryGetValue(value.Id, out var revision) || revision != value.Revision))
            throw new InvalidOperationException("Une intervention du lot a changé depuis l’aperçu. Rechargez toutes les versions avant de choisir.");
        var saved = new List<TeamWorkspaceItem>();
        foreach (var member in group.Members)
        {
            ct.ThrowIfCancellationRequested(); var old = group.Latest.Single(value => value.Id == member.ItemId); var next = (choice == "incoming" ? member.Incoming : old).Copy();
            if (next.Kind != old.Kind) throw new InvalidDataException("Le type d’un membre du lot a changé.");
            if (next.Kind == CalendarWorkspaceDocuments.InterventionKind)
            {
                var intervention = CalendarWorkspaceDocuments.ReadIntervention(next);
                intervention = InterventionCalendarService.AddHistory(intervention, actor, "shared-conflict-resolved", choice == "incoming" ? "La proposition complète du lot a été retenue." : "La version actuelle complète du lot a été conservée.");
                next.Payload = System.Text.Json.JsonSerializer.SerializeToElement(intervention, TeamWorkspaceCodec.Json);
            }
            if (actor.Length > 0) next.Author = actor;
            next.CreatedAt = old.CreatedAt; next.UpdatedAt = DateTimeOffset.UtcNow; next.CentralRevision = old.CentralRevision;
            next.Revision = old.Revision + 1; next.Pending = true; next.Conflicts = old.Conflicts;
            await Put(db, tx, next, ct); await Queue(db, tx, next, ct); saved.Add(next);
        }
        await QueueCalendarBatchAsync(db, tx, saved.Select(value => value.Id).ToArray(), ct);
        await using (var mark = Command(db, tx, "INSERT OR IGNORE INTO gp_team_calendar_resolutions(batch_id,group_id) SELECT batch_id,@group FROM gp_team_calendar_outbox WHERE item_id=@item", ("@group", id), ("@item", saved[0].Id))) await mark.ExecuteNonQueryAsync(ct);
        await using var resolve = Command(db, tx, "UPDATE gp_team_calendar_conflict_groups SET resolved=1 WHERE group_id=@id", ("@id", id)); await resolve.ExecuteNonQueryAsync(ct);
        tx.Commit(); return saved;
    }

}
