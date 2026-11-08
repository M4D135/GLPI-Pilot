using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record FieldDraft(FieldPreparationDocument Document, JsonElement? SourceRequest, long SourceRevision,
        long ExpectedRevision, DateTimeOffset Expires);
    private readonly Dictionary<string, FieldDraft> _fieldDrafts = new(StringComparer.Ordinal);

    private void RegisterFieldPreparationOperations()
    {
        Register("field.get", FieldGetAsync);
        Register("field.open", FieldOpenAsync);
        Register("field.save", FieldSaveAsync);
        Register("field.close", (a, ct) => { ct.ThrowIfCancellationRequested(); _fieldDrafts.Remove(Text(a, "token", 64)); return Task.FromResult<object?>(new { closed = true }); });
        Register("field.catalogue.get", async (a, ct) => await FieldCatalogueReplyAsync(ct).ConfigureAwait(false));
        Register("field.catalogue.save", FieldCatalogueSaveAsync);
        Register("field.report", async (a, ct) => new { html = await FieldReportAsync(a, ct).ConfigureAwait(false) });
        Register("field.export", FieldExportAsync);
        Register("field.versions", FieldVersionsAsync);
        RegisterFieldCompletionOperations();
    }
    private async Task<TeamWorkspaceItem> FieldRequireItemAsync(string id, CancellationToken ct)
    {
        var item = await TeamWorkspace.GetAsync(id, ct).ConfigureAwait(false);
        if (item is null || item.Deleted || item.Kind != FieldPreparationDocuments.PlanKind)
            throw new InvalidDataException("Cette préparation n’existe plus. Actualisez la liste.");
        return item;
    }
    private async Task<(FieldMaterialCatalogue Catalogue, TeamWorkspaceItem? Item)> FieldCatalogueAsync(CancellationToken ct)
    {
        var item = await TeamWorkspace.GetAsync(FieldPreparationDocuments.CatalogueId, ct).ConfigureAwait(false);
        if (item is { Deleted: true }) throw new InvalidOperationException("Le catalogue a été retiré de l’espace partagé. Examinez ses versions avant de continuer.");
        return (item is null ? FieldMaterialDefaults.Create() : FieldPreparationDocuments.ReadCatalogue(item), item);
    }
    private async Task<object> FieldCatalogueReplyAsync(CancellationToken ct)
    {
        var current = await FieldCatalogueAsync(ct).ConfigureAwait(false);
        return new { catalogue = current.Catalogue, revision = current.Item?.Revision ?? 0, pending = current.Item?.Pending ?? false,
            conflicts = current.Item?.Conflicts ?? 0, readOnly = current.Item?.Conflicts > 0, id = FieldPreparationDocuments.CatalogueId };
    }
    private async Task<object?> FieldCatalogueSaveAsync(JsonElement a, CancellationToken ct)
    {
        var catalogue = CalendarArgument<FieldMaterialCatalogue>(a, "catalogue");
        var expected = WorkspaceExpectedRevision(a, required: true)!.Value;
        await TeamWorkspace.SaveFieldCatalogueAsync(catalogue, expected, CalendarAuthor, ct).ConfigureAwait(false);
        Publish("field.changed", new { catalogue = true }); Publish("library.changed", new { team = true });
        return await FieldCatalogueReplyAsync(ct).ConfigureAwait(false);
    }
    private static string FieldShort(string? value, int maximum) => value is null ? "" : value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
    private FieldTicketDetail FieldTicketSnapshot(CalendarTicketRow ticket)
    {
        string Cell(string role, int maximum)
        {
            if (Result is null || ticket.RowIndex < 0 || ticket.RowIndex >= Result.Rows.Count) return "";
            foreach (var column in DashboardService.ResolveColumns(Result.Headers, EffectiveSettings.Dashboard))
                if (column.Role == role && column.Index is int index && index >= 0 && index < Result.Rows[ticket.RowIndex].Length)
                    return FieldShort(Result.Rows[ticket.RowIndex][index], maximum);
            return "";
        }
        return new(ticket.Key, ticket.Id, ticket.Title, Cell("description", 200000), FieldShort(ticket.Status, 300),
            FieldShort(ticket.Type, 300), Cell("requester", 2000), Result?.ExtractedAt);
    }
    private async Task<object?> FieldOpenAsync(JsonElement a, CancellationToken ct)
    {
        FieldPreparationDocument document; JsonElement? source = null; TeamWorkspaceItem? stored; string contextMessage = ""; bool sourceBlocked = false;
        var id = Text(a, "id", 32);
        if (id.Length > 0)
        {
            stored = await FieldRequireItemAsync(id, ct).ConfigureAwait(false);
            var observation = await FieldObserveAsync(FieldPreparationDocuments.ReadPlan(stored), ct).ConfigureAwait(false);
            document = observation.Document; contextMessage = observation.Message;
            if (observation.Verified) source = FieldSourceRequest(document.Plan);
            sourceBlocked = !observation.Verified && document.Plan.Owner.Kind == "intervention";
        }
        else
        {
            CheckRevision(a);
            var range = CalendarRange(a);
            source = JsonSerializer.SerializeToElement(new { ticketKey = Text(a, "ticketKey", 300), occurrenceKey = Text(a, "occurrenceKey", 300),
                from = range.FromDate, to = range.ToDate, timeZoneId = range.Zone }, Json);
            document = await FieldSourceAsync(source.Value, ct).ConfigureAwait(false);
            stored = await TeamWorkspace.GetAsync(document.Plan.Id, ct).ConfigureAwait(false);
            if (stored is { Deleted: true }) throw new InvalidOperationException("Cette préparation a été retirée. Sa restauration doit être explicite.");
            if (stored is not null)
            {
                var previous = FieldPreparationDocuments.ReadPlan(stored);
                document.Plan.Lines = FieldPreparationService.Copy(previous.Plan.Lines);
                document.Plan.NoMaterialsRequired = previous.Plan.NoMaterialsRequired;
                document.Plan.PreparationNote = previous.Plan.PreparationNote;
                document.ResolvedConflictKeys = previous.ResolvedConflictKeys.ToList();
            }
        }
        var material = await FieldCatalogueAsync(ct).ConfigureAwait(false);
        foreach (var key in _fieldDrafts.Where(pair => pair.Value.Expires < DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) _fieldDrafts.Remove(key);
        if (_fieldDrafts.Count >= 32) throw new InvalidOperationException("Trop de préparations sont ouvertes. Fermez un éditeur avant de continuer.");
        var token = Guid.NewGuid().ToString("N");
        _fieldDrafts.Add(token, new(FieldPreparationService.Copy(document), source, Revision, stored?.Revision ?? 0, DateTimeOffset.UtcNow.AddMinutes(30)));
        return new { token, document, expectedRevision = stored?.Revision ?? 0, sourceRevision = Revision, catalogue = material.Catalogue,
            catalogueRevision = material.Item?.Revision ?? 0, pending = stored?.Pending ?? false, conflicts = stored?.Conflicts ?? 0,
            readOnly = sourceBlocked || stored?.Conflicts > 0 || material.Item?.Conflicts > 0 || document.Plan.State != "planned",
            snapshotOnly = source is null,
            message = contextMessage.Length > 0 ? contextMessage : source is null ? "Contexte conservé à la dernière préparation : dates et détails ne sont pas une nouvelle lecture de GLPI ou du planning. Rouvrez depuis le ticket ou la visite pour les actualiser."
                : "Le contexte affiché sera conservé à l’enregistrement. Aucune modification du ticket GLPI ni du stock." };
    }
    private async Task<object?> FieldSaveAsync(JsonElement a, CancellationToken ct)
    {
        var token = Text(a, "token", 64);
        if (!_fieldDrafts.TryGetValue(token, out var draft) || draft.Expires < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Cette préparation doit être rouverte avant enregistrement. Votre saisie n’a pas été envoyée.");
        if (draft.SourceRevision != Revision) throw new InvalidOperationException("Le relevé ou le contexte a changé. Rouvrez la préparation avant d’enregistrer ; votre saisie est conservée.");
        if (draft.Document.Plan.Owner.Kind == "intervention" && draft.SourceRequest is null)
            throw new InvalidOperationException("La visite n’est plus vérifiable dans le planning. Sa préparation reste conservée sans pouvoir être modifiée.");
        if (draft.Document.Plan.State != "planned") throw new InvalidOperationException("Rouvrez d’abord l’intervention dans le planning avant de modifier son matériel.");
        if (draft.SourceRequest is { } source)
        {
            var current = await FieldSourceAsync(source, ct).ConfigureAwait(false);
            if (current.Plan.Id != draft.Document.Plan.Id || current.SourceStamp != draft.Document.SourceStamp)
                throw new InvalidOperationException("L’intervention ou ses participants ont changé. Rouvrez la visite avant d’enregistrer ; votre saisie est conservée.");
        }
        var edit = CalendarArgument<FieldPreparationPlan>(a, "plan"); FieldPreparationService.Validate(edit);
        if (edit.Id != draft.Document.Plan.Id || edit.Owner != draft.Document.Plan.Owner)
            throw new InvalidDataException("La saisie ne correspond pas à la préparation ouverte.");
        var document = FieldPreparationService.Copy(draft.Document);
        // Metadata and ticket snapshots come only from the server-owned draft, never from arbitrary page fields.
        document.Plan.Lines = FieldPreparationService.Copy(edit.Lines);
        document.Plan.NoMaterialsRequired = edit.NoMaterialsRequired; document.Plan.PreparationNote = edit.PreparationNote;
        var saved = await TeamWorkspace.SaveFieldPreparationAsync(document, draft.ExpectedRevision, CalendarAuthor, ct).ConfigureAwait(false);
        _fieldDrafts.Remove(token); Publish("field.changed", new { id = saved.Id }); Publish("library.changed", new { team = true });
        return new { saved = true, saved.Id, saved.Revision, saved.Pending };
    }
    private async Task<object?> FieldExportAsync(JsonElement a, CancellationToken ct)
    {
        var html = await FieldReportAsync(a, ct).ConfigureAwait(false);
        var path = await Host.SaveFileAsync("Exporter les fiches de préparation", "Fiches HTML|*.html", "GLPI-Pilot-preparation-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".html").ConfigureAwait(false);
        if (path is null) return new { cancelled = true };
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(full), ".html", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choisissez un fichier HTML pour les fiches.");
        var database = Path.GetFullPath(LibrarySettings.DatabasePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (new[] { database, database + "-wal", database + "-shm", database + "-journal" }.Any(active => string.Equals(full, active, comparison)))
            throw new InvalidDataException("Les fiches ne peuvent pas remplacer la base locale ni ses journaux.");
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, html, new UTF8Encoding(false), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new { cancelled = false, path = full };
    }
    private async Task<object?> FieldVersionsAsync(JsonElement a, CancellationToken ct)
    {
        var id = Text(a, "id", 32); var item = await TeamWorkspace.GetAsync(id, ct).ConfigureAwait(false);
        if (item is null || !FieldPreparationDocuments.IsKind(item.Kind)) throw new InvalidDataException("Document de préparation introuvable.");
        var offset = Number(a, "offset", 0, 0, 1000000);
        var versions = await TeamWorkspace.ConflictsAsync(id, ct, offset, 5).ConfigureAwait(false);
        return new { items = versions, current = item, expectedRevision = item.Revision, offset, total = await TeamWorkspace.ConflictCountAsync(id, ct).ConfigureAwait(false),
            message = "Les deux versions sont conservées. Aucun choix automatique ne remplace une préparation en conflit." };
    }
}
