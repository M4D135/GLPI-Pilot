using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>Immutable shared ticket transactions indexed on the local disk; historic payloads are read only when requested.</summary>
public sealed class FolderTicketLibraryReplica : ITicketLibraryReplica
{
    private readonly SharedFolderWorkspace _workspace;
    private readonly SharedFolderJournal _journal = new("tickets");
    private readonly LocalFolderReplicaIndex _index;
    internal string LocalIndexPath => _index.DatabasePath;
    public FolderTicketLibraryReplica(string folder, string? cacheFolder = null)
    { _workspace = new(folder); _index = new(_workspace.Folder, "tickets", cacheFolder); }
    private static async Task<string> ReplayAsync(LocalFolderReplicaIndex.Session index, string kind, JsonElement data)
    {
        if (kind != "ticket") throw new InvalidDataException("Type de transaction de bibliothèque non pris en charge.");
        var request = data.Deserialize<TicketLibraryMutation>(SharedFolderWorkspace.Json) ?? throw new InvalidDataException("Fiche partagée vide.");
        return SharedFolderJournal.Digest(await LocalFolderReplicaIndex.ApplyTicketAsync(index, request).ConfigureAwait(false));
    }
    private Task<T> InFolderAsync<T>(Func<SharedFolderWorkspace.Lease, LocalFolderReplicaIndex.Session, Task<T>> operation, CancellationToken ct)
        => _workspace.LockedAsync(lease => _index.RunAsync(lease, async index =>
        {
            await _journal.RefreshAsync(lease, index, (kind, data) => ReplayAsync(index, kind, data)).ConfigureAwait(false);
            return await operation(lease, index).ConfigureAwait(false);
        }), ct);
    public Task<string> InitializeAsync(CancellationToken ct = default) => InFolderAsync((lease, _) => Task.FromResult(lease.Identity), ct);
    Task<TicketLibraryPushReceipt> ITicketLibraryReplica.PushAsync(TicketLibraryMutation mutation, CancellationToken ct) => PushAsync(mutation, ct);
    internal Task<TicketLibraryPushReceipt> PushAsync(TicketLibraryMutation mutation, CancellationToken ct)
        => InFolderAsync(async (lease, index) =>
        {
            using var transaction = index.Begin();
            var known = await index.ScalarAsync("SELECT EXISTS(SELECT 1 FROM gp_folder_receipts WHERE id=@id)", ("@id", "ticket:" + mutation.Id)).ConfigureAwait(false);
            var receipt = await LocalFolderReplicaIndex.ApplyTicketAsync(index, mutation).ConfigureAwait(false);
            if (Convert.ToInt64(known) == 0) await _journal.AppendAsync(lease, index, "ticket", mutation, SharedFolderJournal.Digest(receipt)).ConfigureAwait(false);
            index.Commit(); return receipt;
        }, ct);
    Task<IReadOnlyList<TicketLibraryChange>> ITicketLibraryReplica.PullAsync(long afterRevision, int limit, CancellationToken ct) => PullAsync(afterRevision, limit, ct);
    internal Task<IReadOnlyList<TicketLibraryChange>> PullAsync(long afterRevision, int limit, CancellationToken ct)
        => InFolderAsync((_, index) => LocalFolderReplicaIndex.TicketChangesAsync(index, afterRevision, limit), ct);
}
