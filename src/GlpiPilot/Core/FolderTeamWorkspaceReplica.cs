using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>Shared-folder protocol with a bounded-memory, disposable index on the LOCAL disk.</summary>
public sealed class FolderTeamWorkspaceReplica : ITeamWorkspaceBatchReplica
{
    private readonly SharedFolderWorkspace _workspace;
    private readonly SharedFolderJournal _journal = new("team");
    private readonly LocalFolderReplicaIndex _index;
    internal string LocalIndexPath => _index.DatabasePath;
    public FolderTeamWorkspaceReplica(string folder, string? cacheFolder = null)
    { _workspace = new(folder); _index = new(_workspace.Folder, "team", cacheFolder); }
    private static Task<string> ReplayAsync(LocalFolderReplicaIndex.Session index, string kind, JsonElement data) => kind switch
    {
        "single" => ReplaySingleAsync(index, data),
        "batch" => ReplayBatchAsync(index, data),
        _ => throw new InvalidDataException("Type de transaction d’équipe non pris en charge.")
    };
    private static async Task<string> ReplaySingleAsync(LocalFolderReplicaIndex.Session index, JsonElement data)
    {
        var request = data.Deserialize<TeamWorkspaceMutation>(SharedFolderWorkspace.Json) ?? throw new InvalidDataException("Mutation partagée vide.");
        return SharedFolderJournal.Digest(await LocalFolderReplicaIndex.ApplyTeamAsync(index, request).ConfigureAwait(false));
    }
    private static async Task<string> ReplayBatchAsync(LocalFolderReplicaIndex.Session index, JsonElement data)
    {
        var request = data.Deserialize<TeamWorkspaceBatchMutation>(SharedFolderWorkspace.Json) ?? throw new InvalidDataException("Lot partagé vide.");
        return SharedFolderJournal.Digest(await LocalFolderReplicaIndex.ApplyTeamBatchAsync(index, request).ConfigureAwait(false));
    }
    private Task<T> InFolderAsync<T>(Func<SharedFolderWorkspace.Lease, LocalFolderReplicaIndex.Session, Task<T>> operation, CancellationToken ct)
        => _workspace.LockedAsync(lease => _index.RunAsync(lease, async index =>
        {
            await _journal.RefreshAsync(lease, index, (kind, data) => ReplayAsync(index, kind, data)).ConfigureAwait(false);
            return await operation(lease, index).ConfigureAwait(false);
        }), ct);
    public Task<string> InitializeAsync(CancellationToken ct = default) => InFolderAsync((lease, _) => Task.FromResult(lease.Identity), ct);
    public Task<TeamWorkspaceReceipt> PushAsync(TeamWorkspaceMutation mutation, CancellationToken ct = default)
        => InFolderAsync(async (lease, index) =>
        {
            using var transaction = index.Begin();
            var known = await index.ScalarAsync("SELECT EXISTS(SELECT 1 FROM gp_folder_receipts WHERE id=@id)", ("@id", "single:" + mutation.MutationId)).ConfigureAwait(false);
            var receipt = await LocalFolderReplicaIndex.ApplyTeamAsync(index, mutation).ConfigureAwait(false);
            if (Convert.ToInt64(known) == 0) await _journal.AppendAsync(lease, index, "single", mutation, SharedFolderJournal.Digest(receipt)).ConfigureAwait(false);
            index.Commit(); return receipt;
        }, ct);
    public Task<TeamWorkspaceBatchReceipt> PushBatchAsync(TeamWorkspaceBatchMutation mutation, CancellationToken ct = default)
        => InFolderAsync(async (lease, index) =>
        {
            using var transaction = index.Begin();
            var known = await index.ScalarAsync("SELECT EXISTS(SELECT 1 FROM gp_folder_receipts WHERE id=@id)", ("@id", "batch:" + mutation.MutationId)).ConfigureAwait(false);
            var receipt = await LocalFolderReplicaIndex.ApplyTeamBatchAsync(index, mutation).ConfigureAwait(false);
            if (Convert.ToInt64(known) == 0) await _journal.AppendAsync(lease, index, "batch", mutation, SharedFolderJournal.Digest(receipt)).ConfigureAwait(false);
            index.Commit(); return receipt;
        }, ct);
    public Task<IReadOnlyList<TeamWorkspaceChange>> ChangesAsync(long cursor, int limit, CancellationToken ct = default)
        => InFolderAsync((_, index) => LocalFolderReplicaIndex.TeamChangesAsync(index, cursor, limit), ct);
}
