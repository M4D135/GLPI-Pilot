using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private readonly Dictionary<string, TicketActionCandidate> _workplaceCandidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TicketActionPreview> _workplacePreviews = new(StringComparer.Ordinal);
    private string _workplaceCandidateTicket = "";
    private sealed class WorkActionReceipt
    {
        public string Id { get; set; } = "";
        public string TicketKey { get; set; } = "";
        public string TicketId { get; set; } = "";
        public string Operation { get; set; } = "";
        public string Before { get; set; } = "";
        public string After { get; set; } = "";
        public string OtherFields { get; set; } = "";
        public DateTimeOffset RequestedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string State { get; set; } = "pending";
        public string Message { get; set; } = "";
        public DateTimeOffset? ReviewedAt { get; set; }
    }
    private void RegisterWorkplaceActions()
    {
        Register("workplace.document.download", WorkDownloadDocumentAsync);
        Register("workplace.relation.open", WorkOpenRelationAsync);
        WorkRegister("workplace.draft.save", a => { var doc = WorkDocument().Copy(); var p = doc.Tickets.Single(t => t.Key == WorkPersonal(a).Key); p.FollowupDraft = Text(a, "text", 20000); WorkSave(doc); return new { saved = true }; });
        WorkRegister("workplace.draft.compose", WorkComposeDraft);
        Register("workplace.draft.copy", async (a, _) => { var p = WorkPersonal(a); await Host.SetClipboardAsync(p.FollowupDraft); return new { copied = true }; });
        WorkRegister("workplace.actions.get", WorkActionsState);
        Register("workplace.actions.inspect", WorkInspectActionsAsync);
        WorkRegister("workplace.actions.adapter.save", WorkSaveAdapter);
        WorkRegister("workplace.actions.adapter.remove", a => { var p = WorkPersonal(a); var ticket = WorkValidateReference(new(p.Id, p.Url)); var key = Text(a, "adapterKey", 100); var saved = TicketActionProfileStore.Load(DataPath("ticket-action-adapters.json")); TicketActionProfileStore.Save(DataPath("ticket-action-adapters.json"), saved.Where(adapter => adapter.Key != key || adapter.Installation != TicketActionService.Installation(ticket))); return WorkActionsState(a); });
        Register("workplace.actions.preview", WorkPreviewActionAsync);
        WorkRegister("workplace.actions.invalidate", a => { _workplacePreviews.Clear(); return new { invalidated = true }; });
        Register("workplace.actions.confirm", WorkConfirmActionAsync);
        WorkRegister("workplace.actions.review", WorkReviewReceipt);
    }
    private async Task<object?> WorkDownloadDocumentAsync(JsonElement a, CancellationToken ct)
    {
        var p = WorkPersonal(a); var detail = WorkDetail(p.Key); var index = Number(a, "index", -1, 0, Math.Max(0, detail.ObservedItems.Count - 1));
        if (a.TryGetProperty("readAt", out _) && WorkOffset(a, "readAt") != detail.ReadAt) throw new InvalidOperationException("La fiche a changé ; actualisez les documents avant de choisir le transfert.");
        if (index < 0 || index >= detail.ObservedItems.Count || detail.ObservedItems[index].Kind != "document") throw new InvalidOperationException("Document non observé dans cette fiche.");
        var item = detail.ObservedItems[index]; TicketLinkPolicy.Validate(detail.Ticket, item); WorkValidateReference(detail.Ticket);
        var filename = string.Concat((item.Title.Length > 0 ? item.Title : "document-" + item.Identity).Take(160).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c));
        var path = await Host.SaveFileAsync("Enregistrer le document GLPI", "Tous les fichiers (*.*)|*.*", filename);
        if (path is null) return new { saved = false, cancelled = true };
        await Browser.DownloadTicketDocumentAsync(detail.Ticket, item, Settings.Copy(), path, ct);
        return new { saved = true, name = Path.GetFileName(path) };
    }
    private async Task<object?> WorkOpenRelationAsync(JsonElement a, CancellationToken ct)
    {
        var p = WorkPersonal(a); var detail = WorkDetail(p.Key); var index = Number(a, "index", -1, 0, Math.Max(0, detail.ObservedItems.Count - 1));
        if (a.TryGetProperty("readAt", out _) && WorkOffset(a, "readAt") != detail.ReadAt) throw new InvalidOperationException("La fiche a changé ; actualisez les relations avant de choisir un lien.");
        if (index < 0 || index >= detail.ObservedItems.Count || detail.ObservedItems[index].Kind != "relation") throw new InvalidOperationException("Relation non observée dans cette fiche.");
        var item = detail.ObservedItems[index]; var target = TicketLinkPolicy.Validate(detail.Ticket, item); WorkValidateReference(detail.Ticket);
        if (item.RelatedType == "ticket")
        {
            var reference = WorkValidateReference(new(item.Identity, target.AbsoluteUri)); var doc = WorkDocument().Copy();
            var record = WorkplaceStore.Remember(doc, reference, item.Title); WorkSave(doc);
            return new { ticketKey = record.Key, external = false };
        }
        await Browser.OpenRelatedObjectAsync(detail.Ticket, item, Settings.Copy(), ct); return new { external = true };
    }
    private object WorkComposeDraft(JsonElement a)
    {
        var p = WorkPersonal(a); var detail = WorkOptionalDetail(p); var template = Text(a, "template", 150, "Compte rendu d’intervention");
        var templates = new[] { "Compte rendu d’intervention", "Diagnostic et prochaine action", "Demande de complément", "Passation technique" };
        if (!templates.Contains(template, StringComparer.Ordinal)) throw new InvalidDataException("Modèle de brouillon inconnu.");
        var text = TicketInsightService.DraftFollowup(p, detail, template, Text(a, "findings", 6000), Text(a, "action", 6000), Text(a, "nextStep", 6000));
        var selected = a.TryGetProperty("contextRows", out var context) ? Read<List<int>>(context) : new();
        if (selected.Count > 60) throw new InvalidDataException("Sélectionnez au maximum 60 éléments de contexte.");
        var summary = detail is null ? Array.Empty<string[]>() : TicketReadingService.TreatmentSummary(detail);
        if (selected.Any(i => i < 0 || i >= summary.Count)) throw new InvalidDataException("Le contexte sélectionné a changé ; relisez la synthèse.");
        foreach (var index in selected.Distinct()) text += "\n\n" + summary[index][0] + "\n" + summary[index][1];
        if (text.Length > 20000) throw new InvalidDataException("Le brouillon dépasse 20 000 caractères. Réduisez le contexte inclus.");
        var doc = WorkDocument().Copy(); doc.Tickets.Single(t => t.Key == p.Key).FollowupDraft = text; WorkSave(doc); return new { text, saved = true };
    }
    private List<WorkActionReceipt> WorkReceipts()
    {
        var path = DataPath("workplace-action-receipts.json"); if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Le registre des reçus dépasse 16 Mio ; conservez-le avant toute autre action.");
        var receipts = JsonSerializer.Deserialize<List<WorkActionReceipt>>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Le registre des reçus est illisible.");
        if (receipts.Count > 500 || receipts.Any(r => r is null || new[] { r.Id, r.TicketKey, r.TicketId, r.Operation, r.Before, r.After, r.OtherFields, r.State, r.Message }.Any(value => value is null)
            || r.Id.Length != 32 || !r.Id.All(Uri.IsHexDigit) || r.TicketKey.Length != 64 || !r.TicketKey.All(Uri.IsHexDigit) || r.State is not ("pending" or "verified" or "unknown" or "notSent") || r.After.Length > 24000 || r.Before.Length > 24000 || r.OtherFields.Length > 20000))
            throw new InvalidDataException("Le registre des reçus contient des données invalides ; il est conservé sans modification.");
        return receipts;
    }
    private void WorkWriteReceipts(List<WorkActionReceipt> receipts)
    {
        if (receipts.Count > 500) throw new InvalidOperationException("500 reçus sont conservés. Exportez et archivez le registre avant une nouvelle action.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipts, Json); if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Le registre des reçus dépasse sa limite ; aucun nouvel envoi n’est autorisé.");
        var path = DataPath("workplace-action-receipts.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private object WorkActionsState(JsonElement a)
    {
        var p = WorkPersonal(a); var reference = WorkValidateReference(new(p.Id, p.Url));
        return new
        {
            adapters = TicketActionProfileStore.Load(DataPath("ticket-action-adapters.json")).Where(adapter => adapter.Installation == TicketActionService.Installation(reference)).Select(adapter => new { adapter.Key, adapter.Name, adapter.Kind }).ToArray(),
            candidates = _workplaceCandidateTicket == p.Key ? _workplaceCandidates.Values.Select(c => new { c.Adapter.Key, c.Adapter.Name, c.Adapter.Kind, c.Choices, c.CurrentValue, c.OtherFields }).ToArray() : null,
            receipts = WorkReceipts().Where(r => r.TicketKey == p.Key).OrderByDescending(r => r.RequestedAt).ToArray(),
            legacyReceipt = p.LastActionReceipt,
            canWrite = Result is not null && !IsHistorical && Result.Provenance is { SearchVerified: true, ScopeVerified: true } && Result.Provenance.SourceKind is not ("demo" or "local")
        };
    }
    private async Task<object?> WorkInspectActionsAsync(JsonElement a, CancellationToken ct)
    {
        RequireCurrentResult(); var p = WorkPersonal(a); var ticket = WorkValidateReference(new(p.Id, p.Url));
        var candidates = await Browser.LearnTicketActionsAsync(ticket, Settings.Copy(), ct);
        _workplaceCandidates.Clear(); _workplacePreviews.Clear(); _workplaceCandidateTicket = p.Key;
        foreach (var candidate in candidates.Take(100)) _workplaceCandidates[candidate.Adapter.Key] = candidate;
        return WorkActionsState(a);
    }
    private object WorkSaveAdapter(JsonElement a)
    {
        var p = WorkPersonal(a); var key = Text(a, "adapterKey", 100);
        if (_workplaceCandidateTicket != p.Key || !_workplaceCandidates.TryGetValue(key, out var candidate)) throw new InvalidOperationException("Inspectez le formulaire de ce ticket avant de mémoriser son adaptateur.");
        var adapter = TicketActionService.Validate(candidate.Adapter, WorkValidateReference(new(p.Id, p.Url)));
        var saved = TicketActionProfileStore.Load(DataPath("ticket-action-adapters.json"));
        TicketActionProfileStore.Save(DataPath("ticket-action-adapters.json"), saved.Where(item => !(item.Installation == adapter.Installation && item.Kind == adapter.Kind && item.FormSelector == adapter.FormSelector)).Append(adapter));
        return WorkActionsState(a);
    }
    private async Task<object?> WorkPreviewActionAsync(JsonElement a, CancellationToken ct)
    {
        RequireCurrentResult(); var p = WorkPersonal(a); var ticket = WorkValidateReference(new(p.Id, p.Url));
        if (WorkReceipts().Any(r => r.TicketKey == p.Key && r.State is "pending" or "unknown" && r.ReviewedAt is null))
            throw new InvalidOperationException("Un reçu de ce ticket reste indéterminé. Vérifiez sa référence dans GLPI puis confirmez cette vérification avant de préparer un autre envoi.");
        var key = Text(a, "adapterKey", 100);
        var adapter = _workplaceCandidateTicket == p.Key && _workplaceCandidates.TryGetValue(key, out var candidate) ? candidate.Adapter
            : TicketActionProfileStore.Load(DataPath("ticket-action-adapters.json")).SingleOrDefault(item => item.Key == key) ?? throw new InvalidOperationException("Adaptateur inconnu. Inspectez le formulaire affiché.");
        TicketActionService.Validate(adapter, ticket); _workplacePreviews.Clear();
        var preview = await Browser.PreviewTicketActionAsync(ticket, new(adapter, Text(a, "value", 20000), ""), Settings.Copy(), ct);
        _workplacePreviews[preview.Token] = preview; return preview;
    }
    private async Task<object?> WorkConfirmActionAsync(JsonElement a, CancellationToken ct)
    {
        RequireCurrentResult(); var p = WorkPersonal(a); var token = Text(a, "token", 64);
        if (!Flag(a, "confirmed")) throw new InvalidOperationException("Une confirmation explicite de cet aperçu est obligatoire.");
        if (!_workplacePreviews.Remove(token, out var preview) || WorkplaceStore.TicketKey(preview.Ticket) != p.Key || preview.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Aperçu absent, expiré ou déjà consommé. Aucun envoi n’a été effectué.");
        WorkValidateReference(preview.Ticket); var receipts = WorkReceipts();
        if (receipts.Any(r => r.TicketKey == p.Key && r.State is "pending" or "unknown" && r.ReviewedAt is null)) throw new InvalidOperationException("Un précédent envoi reste à vérifier.");
        var receipt = new WorkActionReceipt { Id = token, TicketKey = p.Key, TicketId = p.Id, Operation = preview.Operation, Before = preview.Before, After = preview.After, OtherFields = preview.OtherFields, RequestedAt = DateTimeOffset.Now, Message = "Envoi demandé ; résultat à vérifier si l’application s’interrompt. Aucune relance automatique." };
        receipts.Add(receipt); WorkWriteReceipts(receipts); // Required durable intent before any browser mutation.
        var doc = WorkDocument().Copy(); doc.Tickets.Single(t => t.Key == p.Key).LastActionReceipt = WorkClip(preview.Text + "\n\n" + receipt.Message, 39000); WorkSave(doc);
        Publish("workplace.actions.stage", new { ticketKey = p.Key, stage = "sending", receiptId = token });
        try
        {
            var outcome = await Browser.ConfirmTicketActionAsync(token, true, Settings.Copy(), ct);
            receipt.State = outcome.Verified ? "verified" : "unknown"; receipt.Message = outcome.Message;
        }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or IOException)
        {
            // BrowserService converts every exception after its one click into an unknown outcome.
            receipt.State = "notSent"; receipt.Message = "Aucune soumission déclenchée. " + AppPaths.SafeError(error);
        }
        receipt.FinishedAt = DateTimeOffset.Now; WorkWriteReceipts(receipts);
        var after = WorkDocument().Copy(); after.Tickets.Single(t => t.Key == p.Key).LastActionReceipt = WorkClip(preview.Text + "\n\n" + receipt.Message, 39000); WorkSave(after);
        _workplaceDetails.Remove(p.Key); _workplaceChanges.Remove(p.Key);
        Publish("workplace.actions.stage", new { ticketKey = p.Key, stage = receipt.State, receiptId = token });
        Log("Action GLPI : " + (receipt.State == "verified" ? "résultat vérifié après relecture." : "résultat conservé dans le reçu local ; aucun renvoi automatique."));
        return receipt;
    }
    private object WorkReviewReceipt(JsonElement a)
    {
        var p = WorkPersonal(a); if (!Flag(a, "confirmed")) throw new InvalidOperationException("Confirmez la vérification manuelle dans GLPI.");
        var receipts = WorkReceipts(); var receipt = receipts.SingleOrDefault(r => r.TicketKey == p.Key && r.Id == Text(a, "receiptId", 64)) ?? throw new InvalidOperationException("Reçu introuvable.");
        receipt.ReviewedAt = DateTimeOffset.Now; WorkWriteReceipts(receipts); return receipt;
    }
}
