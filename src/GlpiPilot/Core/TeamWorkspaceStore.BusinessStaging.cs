using System.IO;

namespace GlpiPilot.Core;

public sealed partial class TeamWorkspaceStore
{
    /// <summary>
    /// Stages a file-backed business edit against the version the local file was
    /// based on. A newer received SQLite document does not grant permission to
    /// overwrite that remote version: both proposals are archived and the exact
    /// older central revision remains the outbox's compare-and-swap base.
    /// </summary>
    public async Task<TeamWorkspaceItem> StageBusinessAsync(TeamWorkspaceItem proposed,
        long? expectedLocalRevision, long? expectedCentralRevision, CancellationToken ct = default)
    {
        if (!SharedBusinessDocuments.IsKind(proposed.Kind))
            throw new InvalidDataException("Ce dépôt local est réservé aux données métier communes.");
        if (expectedLocalRevision is < 0 || expectedCentralRevision is < 0
            || expectedLocalRevision.HasValue != expectedCentralRevision.HasValue)
            throw new InvalidDataException("La version de référence locale du document commun est invalide.");
        var next = proposed.Copy();
        await using var db = await Open(ct);
        using var tx = db.BeginTransaction();
        var old = await ReadItem(db, tx, next.Id, ct);
        if (old is null && (expectedLocalRevision is > 0 || expectedCentralRevision is > 0)
            || old is not null && expectedLocalRevision > old.Revision)
            throw new InvalidOperationException("Le suivi du document commun ne correspond plus à cette base locale. Actualisez le partage avant de recommencer.");
        if (old is not null && old.Kind != next.Kind)
            throw new InvalidOperationException("Le type du document commun ne peut pas être modifié.");
        if (old is not null && TeamWorkspaceCodec.ContentHash(old) == TeamWorkspaceCodec.ContentHash(next))
            return old;

        var baseline = expectedCentralRevision ?? 0;
        next.CreatedAt = old?.CreatedAt ?? DateTimeOffset.UtcNow;
        next.UpdatedAt = DateTimeOffset.UtcNow;
        next.Revision = (old?.Revision ?? 0) + 1;
        next.CentralRevision = baseline;
        next.Pending = true;
        next.Conflicts = old?.Conflicts ?? 0;
        if (old is not null && (expectedLocalRevision != old.Revision || baseline != old.CentralRevision))
        {
            if (await Conflict(db, tx, old, next,
                "Une version commune a été reçue avant l’application locale. La modification locale et la version reçue restent conservées pour comparaison.", ct))
                next.Conflicts++;
        }
        await Put(db, tx, next, ct);
        await Queue(db, tx, next, ct);
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        return next;
    }
}
