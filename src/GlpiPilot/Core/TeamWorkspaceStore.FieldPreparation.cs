using System.Globalization;
using System.IO;
using System.Text;

namespace GlpiPilot.Core;

public sealed partial class TeamWorkspaceStore
{
    /// <summary>A bounded, complete read. Unlike the general list, preparation payloads are retained.</summary>
    public async Task<IReadOnlyList<TeamWorkspaceItem>> FieldPreparationsAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var query = Command(db, null, """
            SELECT document_json,EXISTS(SELECT 1 FROM gp_team_outbox o WHERE o.item_id=i.id),
              (SELECT COUNT(*) FROM gp_team_conflicts c WHERE c.item_id=i.id)
            FROM gp_team_items i WHERE deleted=0 AND kind='field-preparation' ORDER BY id LIMIT 5001
            """);
        await using var reader = await query.ExecuteReaderAsync(ct);
        var items = new List<TeamWorkspaceItem>(); long bytes = 0;
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(0); bytes += Encoding.UTF8.GetByteCount(json);
            if (items.Count == FieldPreparationService.MaximumPlans || bytes > 64 * 1024 * 1024)
                throw new InvalidDataException("Les préparations dépassent la capacité de cette vue. Aucun total partiel n’est affiché.");
            var item = TeamWorkspaceCodec.Read(json); item.Pending = reader.GetInt64(1) != 0;
            item.Conflicts = checked((int)reader.GetInt64(2)); items.Add(item);
        }
        return items;
    }
    public Task<TeamWorkspaceItem> SaveFieldPreparationAsync(FieldPreparationDocument document, long expectedRevision, string author, CancellationToken ct = default)
        => SaveFieldDocumentAsync(FieldPreparationDocuments.ForPlan(document, author), expectedRevision, ct);
    public Task<TeamWorkspaceItem> SaveFieldCatalogueAsync(FieldMaterialCatalogue catalogue, long expectedRevision, string author, CancellationToken ct = default)
        => SaveFieldDocumentAsync(FieldPreparationDocuments.ForCatalogue(catalogue, author), expectedRevision, ct);

    private async Task<TeamWorkspaceItem> SaveFieldDocumentAsync(TeamWorkspaceItem proposed, long expectedRevision, CancellationToken ct)
    {
        if (expectedRevision < 0) throw new InvalidDataException("La révision attendue ne peut pas être négative.");
        var next = proposed.Copy();
        await using var db = await Open(ct); using var tx = db.BeginTransaction();
        var old = await ReadItem(db, tx, next.Id, ct);
        if ((old?.Revision ?? 0) != expectedRevision)
            throw new InvalidOperationException("Cette préparation ou ce catalogue a changé. Rechargez avant d’enregistrer ; votre saisie n’a pas été écrasée.");
        if (old is not null && (old.Deleted || old.Kind != next.Kind || old.Conflicts > 0))
            throw new InvalidOperationException("Ce document a été retiré ou possède des versions en conflit. Examinez les versions avant de le modifier.");
        if (next.Kind == FieldPreparationDocuments.CatalogueKind)
        {
            var previous = old is null ? FieldMaterialDefaults.Create() : FieldPreparationDocuments.ReadCatalogue(old);
            FieldPreparationDocuments.ValidateCatalogueChange(previous, FieldPreparationDocuments.ReadCatalogue(next));
        }
        else
        {
            if (old is null)
            {
                await using var count = Command(db, tx, "SELECT COUNT(*) FROM gp_team_items WHERE deleted=0 AND kind='field-preparation'");
                if (Convert.ToInt64(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) >= FieldPreparationService.MaximumPlans)
                    throw new InvalidDataException("Le maximum de 5 000 préparations enregistrées est atteint.");
            }
            var catalogueItem = await ReadItem(db, tx, FieldPreparationDocuments.CatalogueId, ct);
            if (catalogueItem is { Deleted: true } || catalogueItem is { Conflicts: > 0 })
                throw new InvalidOperationException("Le catalogue a été retiré ou possède des versions en conflit. Aucune nouvelle préparation n’est enregistrée.");
            var catalogue = catalogueItem is null ? FieldMaterialDefaults.Create() : FieldPreparationDocuments.ReadCatalogue(catalogueItem);
            FieldPreparationDocuments.ValidateMaterialChange(old is null ? null : FieldPreparationDocuments.ReadPlan(old).Plan,
                FieldPreparationDocuments.ReadPlan(next).Plan, catalogue);
        }
        FieldResolutionKeys.Preserve(old, next);
        if (old is not null && TeamWorkspaceCodec.ContentHash(old) == TeamWorkspaceCodec.ContentHash(next)) return old;
        next.CreatedAt = old?.CreatedAt ?? DateTimeOffset.UtcNow; next.UpdatedAt = DateTimeOffset.UtcNow;
        next.Revision = (old?.Revision ?? 0) + 1; next.CentralRevision = old?.CentralRevision ?? 0;
        next.Pending = true; next.Conflicts = 0;
        await Put(db, tx, next, ct); await Queue(db, tx, next, ct); ct.ThrowIfCancellationRequested();
        tx.Commit(); return next;
    }
}
