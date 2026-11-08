using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

public static class FieldResolutionKeys
{
    public static List<string> Read(TeamWorkspaceItem item)
    {
        if (!FieldPreparationDocuments.IsKind(item.Kind) || item.Payload is not { } payload || !payload.TryGetProperty("resolvedConflictKeys", out var keys)) return new();
        if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() > 1000) throw new InvalidDataException("Historique de résolution trop volumineux.");
        var result = new List<string>();
        foreach (var key in keys.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.String || key.GetString() is not { Length: 64 } text || text.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidDataException("Identité de conflit invalide.");
            if (!result.Contains(text, StringComparer.Ordinal)) result.Add(text);
        }
        return result;
    }
    public static void Preserve(TeamWorkspaceItem? old, TeamWorkspaceItem next, string? resolved = null)
    {
        var keys = Read(next).Concat(old is null ? Array.Empty<string>() : Read(old));
        if (resolved is not null) keys = keys.Append(resolved);
        var values = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (values.Length > 1000) throw new InvalidDataException("Cette contribution possède trop de résolutions archivées ; exportez son historique avant de poursuivre.");
        var payload = JsonNode.Parse(next.Payload!.Value.GetRawText())!.AsObject();
        payload["resolvedConflictKeys"] = JsonSerializer.SerializeToNode(values);
        next.Payload = JsonSerializer.SerializeToElement(payload);
    }
}

public sealed partial class TeamWorkspaceStore
{
    public async Task<IReadOnlyList<TeamWorkspaceItem>> FieldCalendarDocumentsAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var query = Command(db, null, """
            SELECT document_json,(SELECT COUNT(*) FROM gp_team_conflicts c WHERE c.item_id=i.id)
            FROM gp_team_items i WHERE deleted=0 AND kind='calendar-intervention' ORDER BY id LIMIT 10001
            """);
        await using var reader = await query.ExecuteReaderAsync(ct); var result = new List<TeamWorkspaceItem>(); long bytes = 0;
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(0); bytes += System.Text.Encoding.UTF8.GetByteCount(json);
            if (result.Count >= 10000 || bytes > 64 * 1024 * 1024) throw new InvalidDataException("Le planning dépasse la capacité de rapprochement ; aucun total partiel n’est annoncé.");
            var item = TeamWorkspaceCodec.Read(json); item.Conflicts = checked((int)reader.GetInt64(1)); result.Add(item);
        }
        return result;
    }

    private static async Task ApplyFieldResolutionsAsync(SqliteConnection db, SqliteTransaction tx, TeamWorkspaceItem item, CancellationToken ct)
    {
        if (!FieldPreparationDocuments.IsKind(item.Kind)) return;
        var keys = FieldResolutionKeys.Read(item); if (keys.Count == 0) return;
        await using (var schema = Command(db, tx, """
            CREATE TABLE IF NOT EXISTS gp_team_field_resolution_audit(
              id TEXT PRIMARY KEY,item_id TEXT NOT NULL,resolved_at TEXT NOT NULL,author TEXT NOT NULL,
              chosen_hash TEXT NOT NULL,current_json TEXT NOT NULL,incoming_json TEXT NOT NULL);
            """)) await schema.ExecuteNonQueryAsync(ct);
        foreach (var key in keys)
        {
            await using var archive = Command(db, tx, """
                INSERT OR IGNORE INTO gp_team_field_resolution_audit(id,item_id,resolved_at,author,chosen_hash,current_json,incoming_json)
                SELECT id,item_id,@at,@author,@hash,current_json,incoming_json FROM gp_team_conflicts WHERE id=@key AND item_id=@item;
                DELETE FROM gp_team_conflicts WHERE id=@key AND item_id=@item;
                """, ("@at", item.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)), ("@author", item.Author),
                ("@hash", TeamWorkspaceCodec.ContentHash(item)), ("@key", key), ("@item", item.Id));
            await archive.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<bool> FieldConflictWasResolvedAsync(SqliteConnection db, SqliteTransaction tx, TeamWorkspaceItem current, TeamWorkspaceItem incoming, CancellationToken ct)
    {
        if (!FieldPreparationDocuments.IsKind(current.Kind)) return false;
        var key = TeamWorkspaceCodec.ConflictKey(current, incoming);
        if (FieldResolutionKeys.Read(current).Contains(key, StringComparer.Ordinal)) return true;
        await using var query = Command(db, tx, "SELECT document_json FROM gp_team_items WHERE id=@id", ("@id", current.Id));
        var json = (string?)await query.ExecuteScalarAsync(ct);
        return json is not null && FieldResolutionKeys.Read(TeamWorkspaceCodec.Read(json)).Contains(key, StringComparer.Ordinal);
    }

    public async Task<TeamWorkspaceItem> ResolveFieldConflictAsync(string id, long expectedRevision, string conflictId, string choice, string author, CancellationToken ct = default)
    {
        if (choice is not ("current" or "incoming")) throw new InvalidDataException("Choisissez explicitement une des deux versions.");
        await using var db = await Open(ct); using var tx = db.BeginTransaction();
        var old = await ReadItem(db, tx, id, ct) ?? throw new InvalidDataException("Document de préparation introuvable.");
        if (!FieldPreparationDocuments.IsKind(old.Kind) || old.Deleted) throw new InvalidDataException("Cette résolution concerne uniquement un catalogue ou une préparation existante.");
        if (old.Revision != expectedRevision) throw new InvalidOperationException("La contribution a changé ; relisez les versions avant de confirmer votre choix.");
        string incoming;
        await using (var query = Command(db, tx, "SELECT incoming_json FROM gp_team_conflicts WHERE item_id=@id AND id=@conflict", ("@id", id), ("@conflict", conflictId)))
            incoming = (string?)await query.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Ce conflit a déjà été résolu ou n’existe plus.");
        var next = choice == "current" ? old.Copy() : TeamWorkspaceCodec.Read(incoming);
        if (next.Id != old.Id || next.Kind != old.Kind || next.Deleted) throw new InvalidDataException("La version reçue ne peut pas remplacer l’identité du document.");
        if (next.Kind == FieldPreparationDocuments.CatalogueKind)
        {
            var retained = FieldPreparationDocuments.ReadCatalogue(old);
            var selected = FieldPreparationDocuments.ReadCatalogue(next);
            // Current-only references remain archived rather than disappearing from history.
            selected.Categories.AddRange(retained.Categories.Where(value => selected.Categories.All(row => row.Id != value.Id)).Select(value => value with { Archived = true }));
            selected.References.AddRange(retained.References.Where(value => selected.References.All(row => row.Id != value.Id)).Select(value => value with { Archived = true }));
            FieldPreparationService.Validate(selected);
            var replacement = FieldPreparationDocuments.ForCatalogue(selected, author);
            FieldResolutionKeys.Preserve(next, replacement); next = replacement;
        }
        else if (FieldPreparationDocuments.ReadPlan(next).Plan.Owner != FieldPreparationDocuments.ReadPlan(old).Plan.Owner)
            throw new InvalidDataException("Les versions ne concernent pas la même intervention.");
        FieldResolutionKeys.Preserve(old, next, conflictId);
        next.CreatedAt = old.CreatedAt; next.UpdatedAt = DateTimeOffset.UtcNow; next.Author = author;
        next.Revision = old.Revision + 1; next.CentralRevision = old.CentralRevision; next.Pending = true;
        await Put(db, tx, next, ct); await Queue(db, tx, next, ct); ct.ThrowIfCancellationRequested();
        tx.Commit(); return (await GetAsync(id, ct))!;
    }
}
