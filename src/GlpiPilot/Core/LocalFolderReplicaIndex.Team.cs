using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

internal sealed partial class LocalFolderReplicaIndex
{
    internal static async Task<TeamWorkspaceReceipt> ApplyTeamAsync(Session index, TeamWorkspaceMutation mutation)
    {
        if (CalendarWorkspaceDocuments.IsKind(mutation.Item.Kind)) throw new InvalidOperationException("Le calendrier exige un lot atomique.");
        if (!Guid.TryParseExact(mutation.MutationId, "N", out _) || mutation.BaseRevision < 0) throw new InvalidDataException("Identité de mutation d’équipe invalide.");
        TeamWorkspaceCodec.Validate(mutation.Item);
        var hash = TeamWorkspaceCodec.ContentHash(mutation.Item) + ":" + mutation.BaseRevision.ToString(CultureInfo.InvariantCulture);
        if (await index.ReceiptAsync("single:" + mutation.MutationId).ConfigureAwait(false) is { } cached)
        {
            if (cached.RequestHash != hash) throw new InvalidDataException("Identité de mutation réutilisée.");
            var known = Session.Deserialize<TeamWorkspaceReceipt>(cached.Json); TeamWorkspaceCodec.Validate(known.Current); return known;
        }
        var document = await index.ReadDocumentAsync(mutation.Item.Id).ConfigureAwait(false);
        var current = document is null ? null : TeamWorkspaceCodec.Read(document);
        var same = current is not null && TeamWorkspaceCodec.ContentHash(current) == TeamWorkspaceCodec.ContentHash(mutation.Item);
        var conflict = !same && (current?.CentralRevision ?? 0) != mutation.BaseRevision;
        if (conflict && current is null) throw new InvalidDataException("Version attendue absente.");
        var next = (same || conflict ? current! : mutation.Item).Copy();
        var outcome = same ? "unchanged" : conflict ? "conflict" : "accepted";
        if (!same)
        {
            next.CentralRevision = await index.NextRevisionAsync().ConfigureAwait(false); next.Revision = 0; next.Pending = false; next.Conflicts = 0;
            await index.PutDocumentAsync(next.Id, TeamWorkspaceCodec.Write(next)).ConfigureAwait(false);
            await index.SaveChangeAsync(next.CentralRevision, new TeamWorkspaceChange(next.CentralRevision, next, conflict ? mutation.Item : null,
                conflict ? "concurrent-edit" : "")).ConfigureAwait(false);
        }
        var receipt = new TeamWorkspaceReceipt(outcome, next);
        await index.SaveReceiptAsync("single:" + mutation.MutationId, hash, receipt).ConfigureAwait(false);
        return receipt;
    }

    internal static async Task<TeamWorkspaceBatchReceipt> ApplyTeamBatchAsync(Session index, TeamWorkspaceBatchMutation mutation)
    {
        var hash = TeamWorkspaceCalendarProtocol.RequestHash(mutation);
        if (await index.ReceiptAsync("batch:" + mutation.MutationId).ConfigureAwait(false) is { } cached)
        {
            if (cached.RequestHash != hash) throw new InvalidDataException("Identité de lot réutilisée.");
            var known = Session.Deserialize<TeamWorkspaceBatchReceipt>(cached.Json); TeamWorkspaceCalendarProtocol.ValidateReceipt(mutation, known); return known;
        }
        var current = new Dictionary<string, TeamWorkspaceItem?>(StringComparer.Ordinal); long bytes = 0;
        foreach (var value in mutation.Mutations)
        {
            index.Cancellation.ThrowIfCancellationRequested();
            var json = await index.ReadDocumentAsync(value.Item.Id, SharedFolderWorkspace.MaximumEnvelopeBytes - bytes).ConfigureAwait(false);
            if (json is not null) bytes += Encoding.UTF8.GetByteCount(json);
            var item = json is null ? null : TeamWorkspaceCodec.Read(json);
            if (item is not null && item.Kind != value.Item.Kind) throw new InvalidDataException("Le type partagé ne peut pas changer.");
            current.Add(value.Item.Id, item);
        }
        bool Same(TeamWorkspaceMutation value) => current[value.Item.Id] is { } item && TeamWorkspaceCodec.ContentHash(item) == TeamWorkspaceCodec.ContentHash(value.Item);
        var conflict = mutation.Mutations.Any(value => !Same(value) && (current[value.Item.Id]?.CentralRevision ?? 0) != value.BaseRevision);
        var unchanged = !conflict && mutation.ResolvesGroups.Count == 0 && mutation.Mutations.All(Same);
        var outcome = conflict ? "conflict" : unchanged ? "unchanged" : "accepted";
        var resolved = conflict ? Array.Empty<string>() : mutation.ResolvesGroups.ToArray();
        var receipts = new List<TeamWorkspaceReceipt>();
        foreach (var value in mutation.Mutations.OrderBy(value => value.Item.Id, StringComparer.Ordinal))
        {
            index.Cancellation.ThrowIfCancellationRequested();
            var next = (conflict || unchanged ? current[value.Item.Id] ?? value.Item : value.Item).Copy();
            if (conflict && current[value.Item.Id] is null) next.Deleted = true;
            next.Revision = 0; next.Pending = false; next.Conflicts = 0;
            if (!unchanged)
            {
                next.CentralRevision = await index.NextRevisionAsync().ConfigureAwait(false);
                await index.PutDocumentAsync(next.Id, TeamWorkspaceCodec.Write(next)).ConfigureAwait(false);
                await index.SaveChangeAsync(next.CentralRevision,
                    new TeamWorkspaceChange(next.CentralRevision, next, conflict ? value.Item : null, conflict ? "calendar-batch-conflict" : "")
                    { BatchId = mutation.MutationId, BatchSize = mutation.Mutations.Count, ResolvedGroups = resolved }, mutation.MutationId, mutation.Mutations.Count).ConfigureAwait(false);
            }
            receipts.Add(new(outcome, next));
        }
        var receipt = new TeamWorkspaceBatchReceipt(outcome, receipts) { ResolvedGroups = resolved };
        TeamWorkspaceCalendarProtocol.ValidateReceipt(mutation, receipt);
        await index.SaveReceiptAsync("batch:" + mutation.MutationId, hash, receipt).ConfigureAwait(false);
        return receipt;
    }

    internal static async Task<IReadOnlyList<TeamWorkspaceChange>> TeamChangesAsync(Session index, long cursor, int limit)
    {
        if (cursor < 0 || limit is < 1 or > 200) throw new InvalidDataException("Curseur invalide.");
        var result = new List<TeamWorkspaceChange>(); long bytes = 0;
        // Read at most a page plus the complete final atomic group. Payloads for
        // all other historic transactions remain in the local index on disk.
        await using (var command = index.Command("SELECT sequence,batch_id,batch_size,length(CAST(json AS BLOB)),json,payload_hash FROM gp_folder_changes WHERE sequence>@cursor ORDER BY sequence LIMIT @limit", ("@cursor", cursor), ("@limit", limit)))
        await using (var reader = await command.ExecuteReaderAsync(index.Cancellation).ConfigureAwait(false))
        while (await reader.ReadAsync(index.Cancellation).ConfigureAwait(false))
        {
            var length = reader.GetInt64(3); var batchId = reader.GetString(1);
            if (result.Count > 0 && bytes + length > TeamWorkspaceCodec.MaximumBatchBytes && (batchId.Length == 0 || batchId != result[^1].BatchId)) break;
            if (bytes + length > SharedFolderWorkspace.MaximumEnvelopeBytes)
            { if (result.Count > 0) break; throw new InvalidDataException("Une page de calendrier dépasse 64 Mio."); }
            var json = Session.VerifyPayload(reader.GetString(4), reader.GetString(5)); var change = Session.Deserialize<TeamWorkspaceChange>(json);
            VerifyTeamChange(change, reader.GetInt64(0), batchId, reader.GetInt32(2)); bytes += length; result.Add(change);
        }
        if (result.LastOrDefault() is { BatchId.Length: > 0 } last && result.Count(value => value.BatchId == last.BatchId) < last.BatchSize)
        {
            result.RemoveAll(value => value.BatchId == last.BatchId);
            var group = new List<TeamWorkspaceChange>(); long groupBytes = 0;
            await using var command = index.Command("SELECT sequence,batch_size,length(CAST(json AS BLOB)),json,payload_hash FROM gp_folder_changes WHERE batch_id=@batch ORDER BY sequence", ("@batch", last.BatchId));
            await using var reader = await command.ExecuteReaderAsync(index.Cancellation).ConfigureAwait(false);
            while (await reader.ReadAsync(index.Cancellation).ConfigureAwait(false))
            {
                groupBytes += reader.GetInt64(2);
                if (group.Count >= 200 || groupBytes > SharedFolderWorkspace.MaximumEnvelopeBytes) throw new InvalidDataException("Lot reçu du calendrier supérieur à 64 Mio.");
                var json = Session.VerifyPayload(reader.GetString(3), reader.GetString(4)); var change = Session.Deserialize<TeamWorkspaceChange>(json);
                VerifyTeamChange(change, reader.GetInt64(0), last.BatchId, reader.GetInt32(1)); group.Add(change);
            }
            TeamWorkspaceCalendarProtocol.ValidateChanges(group);
            // If adding the group would exceed the bounded page, return the
            // preceding documents first. A group alone remains indivisible.
            var precedingBytes = result.Sum(value => (long)Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, SharedFolderWorkspace.Json)));
            if (result.Count == 0 || precedingBytes + groupBytes <= SharedFolderWorkspace.MaximumEnvelopeBytes) result.AddRange(group);
        }
        foreach (var group in result.Where(value => value.BatchId.Length > 0).GroupBy(value => value.BatchId, StringComparer.Ordinal))
            TeamWorkspaceCalendarProtocol.ValidateChanges(group.ToArray());
        return result;
    }
    private static void VerifyTeamChange(TeamWorkspaceChange change, long sequence, string batchId, int batchSize)
    {
        TeamWorkspaceCodec.Validate(change.Item); if (change.Incoming is not null) TeamWorkspaceCodec.Validate(change.Incoming);
        if (change.Sequence != sequence || change.Item.CentralRevision != sequence || change.BatchId != batchId || change.BatchSize != batchSize
            || change.Incoming is not null && change.Incoming.Id != change.Item.Id)
            throw new InvalidIndexException("Le flux local partagé doit être reconstruit.");
    }
}
