using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

internal sealed partial class LocalFolderReplicaIndex
{
    internal static async Task<TicketLibraryPushReceipt> ApplyTicketAsync(Session index, TicketLibraryMutation mutation)
    {
        if (!Guid.TryParseExact(mutation.Id, "N", out _)) throw new InvalidDataException("Identifiant de mutation de bibliothèque invalide.");
        TicketLibraryRules.Validate(mutation.Document); var hash = TicketRequestHash(mutation);
        if (await index.ReceiptAsync("ticket:" + mutation.Id).ConfigureAwait(false) is { } cached)
        {
            if (cached.RequestHash != hash) throw new InvalidDataException("Une mutation de bibliothèque ne peut pas être réutilisée pour un autre contenu.");
            var known = Session.Deserialize<TicketLibraryPushReceipt>(cached.Json); TicketLibraryRules.Validate(known.Document); return known;
        }
        var json = await index.ReadDocumentAsync(mutation.Document.Key).ConfigureAwait(false);
        var current = json is null ? null : TicketLibraryRules.Deserialize(json);
        var merge = TicketLibraryRules.Merge(current, mutation.Document);
        var conflict = merge.Conflict && current is not null
            && await index.RememberConflictAsync(TicketLibraryStore.ConflictId(current, mutation.Document, merge.Outcome)).ConfigureAwait(false);
        var changed = current is null || current.ContentHash != merge.Document.ContentHash || current.DetailsCurrent != merge.Document.DetailsCurrent
            || current.ConfirmationPending != merge.Document.ConfirmationPending || current.ConfirmedContentHash != merge.Document.ConfirmedContentHash || current.ConfirmationId != merge.Document.ConfirmationId
            || current.ReadAt != merge.Document.ReadAt || current.ReaderSignature != merge.Document.ReaderSignature;
        var revision = current?.CentralRevision ?? 0;
        if (changed || conflict)
        {
            revision = await index.NextRevisionAsync().ConfigureAwait(false); merge.Document.LocalRevision = 0; merge.Document.CentralRevision = revision; merge.Document.Pending = false;
            await index.PutDocumentAsync(merge.Document.Key, TicketLibraryRules.Serialize(merge.Document)).ConfigureAwait(false);
            await index.SaveChangeAsync(revision, new TicketLibraryChange(revision, merge.Document, conflict ? mutation.Document : null, merge.Outcome)).ConfigureAwait(false);
        }
        var receipt = new TicketLibraryPushReceipt(revision, merge.Outcome, conflict, merge.Document);
        await index.SaveReceiptAsync("ticket:" + mutation.Id, hash, receipt).ConfigureAwait(false);
        return receipt;
    }
    internal static async Task<IReadOnlyList<TicketLibraryChange>> TicketChangesAsync(Session index, long cursor, int limit)
    {
        if (cursor < 0) throw new InvalidDataException("Le curseur partagé de bibliothèque est invalide.");
        var result = new List<TicketLibraryChange>(); long bytes = 0;
        await using var command = index.Command("SELECT sequence,length(CAST(json AS BLOB)),json,payload_hash FROM gp_folder_changes WHERE sequence>@cursor ORDER BY sequence LIMIT @limit",
            ("@cursor", cursor), ("@limit", Math.Clamp(limit, 1, 100)));
        await using var reader = await command.ExecuteReaderAsync(index.Cancellation).ConfigureAwait(false);
        while (await reader.ReadAsync(index.Cancellation).ConfigureAwait(false))
        {
            var length = reader.GetInt64(1);
            if (result.Count > 0 && bytes + length > 32L * 1024 * 1024) break;
            if (length > SharedFolderWorkspace.MaximumEnvelopeBytes) throw new InvalidDataException("Une fiche partagée dépasse 64 Mio.");
            var json = Session.VerifyPayload(reader.GetString(2), reader.GetString(3));
            var change = Session.Deserialize<TicketLibraryChange>(json);
            TicketLibraryRules.Validate(change.Document); if (change.Incoming is not null) TicketLibraryRules.Validate(change.Incoming);
            if (change.Revision != reader.GetInt64(0) || change.Document.CentralRevision != change.Revision
                || change.Incoming is not null && change.Incoming.Key != change.Document.Key)
                throw new InvalidIndexException("Le flux local de bibliothèque doit être reconstruit.");
            bytes += length; result.Add(change);
        }
        return result;
    }
    private static string TicketRequestHash(TicketLibraryMutation mutation)
    {
        var request = mutation.RequestJson ?? TicketLibraryRules.Serialize(mutation.Document);
        if (mutation.RequestJson is not null)
        {
            try
            {
                var original = TicketLibraryRules.Deserialize(request); var current = TicketLibraryRules.Deserialize(TicketLibraryRules.Serialize(mutation.Document));
                if (TicketLibraryRules.Serialize(original) != TicketLibraryRules.Serialize(current)) throw new InvalidDataException("La requête immuable ne correspond pas à la fiche partagée.");
            }
            catch (JsonException error) { throw new InvalidDataException("La requête immuable de bibliothèque est illisible.", error); }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request)));
    }
}
