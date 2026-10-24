using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private readonly Dictionary<string, List<string[]>> _simulationTimeline = new(StringComparer.Ordinal);
    private readonly Dictionary<int, long> _simulationVersions = new();
    private readonly HashSet<string> _simulationSavedAdapters = new(StringComparer.Ordinal);

    private TicketReference SimulationReference(int rowIndex)
        => _simulationReferences.TryGetValue(rowIndex, out var reference) ? reference
            : throw new InvalidOperationException("Cette ligne n’appartient pas au relevé de simulation.");

    private string SimulationCell(int rowIndex, string role)
    {
        var source = _simulationLiveData ?? RequireResult();
        var column = DashboardService.ResolveColumns(source.Headers, (_simulationLiveSettings ?? EffectiveSettings).Dashboard).FirstOrDefault(value => value.Role == role)?.Index;
        return column is int index && rowIndex >= 0 && rowIndex < source.Rows.Count && index < source.Rows[rowIndex].Length ? source.Rows[rowIndex][index] : "";
    }

    private TicketDetailResult SimulationDetail(TicketReference ticket)
    {
        WorkValidateReference(ticket); var key = WorkplaceStore.TicketKey(ticket);
        if (_simulationDetails.TryGetValue(key, out var retained)) return retained;
        var rowIndex = _simulationRows[key]; var source = _simulationLiveData ?? RequireResult(); var row = source.Rows[rowIndex];
        var stamp = DateTimeOffset.Now;
        var detail = new TicketDetailResult
        {
            Ticket = ticket, Title = SimulationCell(rowIndex, "title"), ReadAt = stamp,
            Warnings = new() { "Simulation locale : cette fiche représente la ligne " + (rowIndex + 1).ToString(CultureInfo.InvariantCulture) + " du fichier importé. Les suivis et tâches créés ici sont des essais locaux, sans lecture ni écriture GLPI." }
        };
        if (detail.Title.Length == 0) detail.Title = "Ligne importée " + (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
        if (detail.Title.Length > 10000) detail.Title = detail.Title[..10000];
        var sourceId = SimulationCell(rowIndex, "id");
        if (sourceId.Trim() != ticket.Id)
            detail.Warnings.Add("Référence interne de simulation " + ticket.Id + " ; identifiant source conservé : " + (sourceId.Length == 0 ? "absent" : sourceId) + ". Chaque occurrence reste distincte.");
        TicketDetailSection Section(string keyName, string name, string kind, List<string> headers, List<string[]> rows)
        {
            detail.Tabs.Add(new() { Key = keyName, Name = name, Category = kind == "timeline" ? "timeline" : "other", Loaded = true, IsActive = detail.Tabs.Count == 0 });
            return new()
            {
                Name = name, Kind = kind, TabKey = keyName, TabName = name,
                Data = new() { Headers = headers, Rows = rows, Complete = true, ExpectedTotal = rows.Count, ExtractedAt = stamp,
                    SourceUrl = ticket.Url, Title = name, Scope = "Copie locale de simulation", Provenance = new() { SourceKind = "simulation", Method = "simulation-locale" } }
            };
        }
        detail.Sections.Add(Section("simulation-fields", "Champs du fichier importé", "fields", new() { "Champ", "Valeur" },
            source.Headers.Select((header, index) => new[] { header, index < row.Length ? row[index] : "" }).ToList()));
        detail.Sections.Add(Section("simulation-timeline", "Suivis, tâches et solutions de simulation", "timeline", new() { "Type", "Date", "Auteur", "Contenu" },
            _simulationTimeline.GetValueOrDefault(key)?.Select(value => value.ToArray()).ToList() ?? new()));
        var importedSolution = SimulationCell(rowIndex, "solution");
        if (importedSolution.Length > 0)
            detail.Sections[1].Data.Rows.Insert(0, new[] { "Solution", SimulationCell(rowIndex, "resolved"), "Auteur absent du champ exporté", importedSolution });
        detail.Sections[1].Data.ExpectedTotal = detail.Sections[1].Data.Rows.Count;
        // This generated document and optional navigation example are explicitly
        // separate from the imported stock; neither asserts an unseen GLPI item.
        detail.ObservedItems.Add(new() { Kind = "document", Identity = "1", Title = "Export texte de cette fiche locale (simulation)",
            Url = "https://simulation.glpi-pilot.invalid/front/document.send.php?docid=1&tickets_id=" + ticket.Id,
            SourceSection = "Outils locaux de simulation", ObservedAt = stamp, SourceTabKey = "simulation-fields" });
        if (_simulationReferences.Count > 1)
        {
            var next = SimulationReference((rowIndex + 1) % _simulationReferences.Count);
            detail.ObservedItems.Add(new() { Kind = "relation", RelatedType = "ticket", Identity = next.Id, Url = next.Url,
                Title = "Exemple de navigation locale vers la ligne " + (((rowIndex + 1) % _simulationReferences.Count) + 1).ToString(CultureInfo.InvariantCulture),
                SourceSection = "Exemple de simulation · aucune relation GLPI attestée", ObservedAt = stamp, SourceTabKey = "simulation-fields" });
        }
        foreach (var entry in _simulationTimeline.GetValueOrDefault(key) ?? new())
            detail.ObservedItems.Add(new() { Kind = entry[0] == "Tâche" ? "task" : entry[0] == "Solution" ? "solution" : "followup",
                Identity = TicketActionService.Hash(string.Join('\n', entry))[..20], Title = entry[3], Date = entry[1], Author = entry[2],
                SourceSection = "Suivis, tâches et solutions de simulation", SourceTabKey = "simulation-timeline", ObservedAt = stamp });
        _simulationDetails[key] = detail; return detail;
    }

    private void RegisterSimulationTicketOperations()
    {
        ReplaceSimulationOperation("workplace.ticket.read", (a, ct) =>
        {
            ct.ThrowIfCancellationRequested(); var p = WorkPersonal(a); var detail = SimulationDetail(new(p.Id, p.Url));
            WorkAcceptDetail(detail); return Task.FromResult<object?>(SimulationReply(WorkTicketView(p.Key)));
        });
        ReplaceSimulationOperation("workplace.ticket.browser", (a, ct) =>
        {
            ct.ThrowIfCancellationRequested(); var p = WorkPersonal(a); WorkAcceptDetail(SimulationDetail(new(p.Id, p.Url)), open: true);
            return Task.FromResult<object?>(new { shown = true, simulated = true, ticketKey = p.Key, ticket = WorkTicketView(p.Key), message = "Fiche locale de simulation ouverte." });
        });
        ReplaceSimulationOperation("workplace.ticket.tab", (a, ct) =>
        {
            ct.ThrowIfCancellationRequested(); var p = WorkPersonal(a); var detail = SimulationDetail(new(p.Id, p.Url));
            if (!detail.Tabs.Any(tab => tab.Key == Text(a, "tabKey", 2048))) throw new InvalidOperationException("Onglet absent de la fiche locale.");
            WorkAcceptDetail(detail); return Task.FromResult<object?>(new { loaded = true, simulated = true, warnings = detail.Warnings, ticket = WorkTicketView(p.Key) });
        });
        ReplaceSimulationOperation("workplace.document.download", SimulationDownloadDocumentAsync);
        ReplaceSimulationOperation("workplace.relation.open", (a, ct) =>
        {
            ct.ThrowIfCancellationRequested(); var p = WorkPersonal(a); var detail = SimulationDetail(new(p.Id, p.Url));
            var index = Number(a, "index", -1, 0, Math.Max(0, detail.ObservedItems.Count - 1));
            if (index < 0 || index >= detail.ObservedItems.Count || detail.ObservedItems[index].Kind != "relation") throw new InvalidDataException("Relation locale inconnue.");
            var item = detail.ObservedItems[index]; var reference = WorkValidateReference(new(item.Identity, item.Url));
            WorkAcceptDetail(SimulationDetail(reference), open: true);
            return Task.FromResult<object?>(new { ticketKey = WorkplaceStore.TicketKey(reference), external = false, simulated = true });
        });
        ReplaceSimulationOperation("workplace.ticket.handover", async (a, ct) =>
        {
            ct.ThrowIfCancellationRequested(); var p = WorkPersonal(a);
            var text = "SIMULATION LOCALE · aucune action GLPI\n\n" + TicketReadingService.Handover(p, WorkOptionalDetail(p)).Replace("Éléments lus dans GLPI", "Éléments du fichier et des actions locales", StringComparison.Ordinal);
            if (Flag(a, "copy")) await Host.SetClipboardAsync(text).ConfigureAwait(false); return new { text, simulated = true };
        });
        ReplaceSimulationOperation("workplace.actions.get", (a, ct) => Task.FromResult<object?>(SimulationActionsState(a)));
        ReplaceSimulationOperation("workplace.actions.inspect", (a, ct) =>
        {
            ct.ThrowIfCancellationRequested(); RequireCurrentResult(); var p = WorkPersonal(a); _workplacePreviews.Clear(); _simulationActions.Clear();
            _workplaceCandidates.Clear(); _workplaceCandidateTicket = p.Key;
            foreach (var candidate in SimulationCandidates(p)) _workplaceCandidates[candidate.Adapter.Key] = candidate;
            return Task.FromResult<object?>(SimulationActionsState(a));
        });
        ReplaceSimulationOperation("workplace.actions.adapter.save", (a, ct) =>
        {
            var p = WorkPersonal(a); var key = Text(a, "adapterKey", 100);
            if (_workplaceCandidateTicket != p.Key || !_workplaceCandidates.ContainsKey(key)) throw new InvalidOperationException("Inspectez les actions locales de cette fiche avant de mémoriser un adaptateur.");
            _simulationSavedAdapters.Add(key); return Task.FromResult<object?>(SimulationActionsState(a));
        });
        ReplaceSimulationOperation("workplace.actions.adapter.remove", (a, ct) =>
        { _simulationSavedAdapters.Remove(Text(a, "adapterKey", 100)); return Task.FromResult<object?>(SimulationActionsState(a)); });
        ReplaceSimulationOperation("workplace.actions.preview", SimulationPreviewActionAsync);
        ReplaceSimulationOperation("workplace.actions.confirm", SimulationConfirmActionAsync);
        ReplaceSimulationOperation("workplace.actions.review", (a, ct) =>
        {
            var p = WorkPersonal(a); if (!Flag(a, "confirmed")) throw new InvalidOperationException("Confirmez la lecture du reçu local.");
            var receipts = WorkReceipts(); var receipt = receipts.SingleOrDefault(value => value.TicketKey == p.Key && value.Id == Text(a, "receiptId", 64)) ?? throw new InvalidOperationException("Reçu local introuvable.");
            receipt.ReviewedAt = DateTimeOffset.Now; WorkWriteReceipts(receipts); return Task.FromResult<object?>(SimulationReply(receipt));
        });
    }

    private object SimulationActionsState(JsonElement a)
    {
        var p = WorkPersonal(a); WorkValidateReference(new(p.Id, p.Url)); var candidates = SimulationCandidates(p);
        return new
        {
            adapters = candidates.Where(candidate => _simulationSavedAdapters.Contains(candidate.Adapter.Key)).Select(candidate => new { candidate.Adapter.Key, candidate.Adapter.Name, candidate.Adapter.Kind }).ToArray(),
            candidates = _workplaceCandidateTicket == p.Key ? _workplaceCandidates.Values.Select(candidate => new { candidate.Adapter.Key, candidate.Adapter.Name, candidate.Adapter.Kind, candidate.Choices, candidate.CurrentValue, candidate.OtherFields }).ToArray() : null,
            receipts = WorkReceipts().Where(receipt => receipt.TicketKey == p.Key).OrderByDescending(receipt => receipt.RequestedAt).ToArray(),
            legacyReceipt = p.LastActionReceipt, canWrite = !IsHistorical, simulated = true
        };
    }

    private TicketActionCandidate[] SimulationCandidates(PersonalTicket p)
    {
        var index = _simulationRows[p.Key];
        TicketActionCandidate Candidate(string key, string name, string kind, string role = "", IReadOnlyList<TicketActionChoice>? choices = null)
        {
            var current = role.Length > 0 ? SimulationCell(index, role) : "";
            var selected = choices?.FirstOrDefault(choice => choice.Label == current)?.Value ?? "";
            return new(new() { Key = "simulation-" + key, Name = name + " (simulation)", Kind = kind, FieldName = key, Installation = TicketActionService.Installation(new(p.Id, p.Url)) },
                choices ?? Array.Empty<TicketActionChoice>(), selected, "Copie locale uniquement ; aucune soumission GLPI.");
        }
        IReadOnlyList<TicketActionChoice> Choices(string role, string fallback)
        {
            var data = _simulationLiveData!; var column = DashboardService.ResolveColumns(data.Headers, EffectiveSettings.Dashboard).FirstOrDefault(value => value.Role == role)?.Index;
            var labels = column is int c ? data.Rows.SelectMany(row => c < row.Length ? row[c].Split(new[] { '\n', ';', '|' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>())
                .Where(value => value.Length <= 2000).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).Take(499).ToList() : new List<string>();
            if (!labels.Contains(fallback, StringComparer.Ordinal)) labels.Add(fallback);
            return new[] { new TicketActionChoice("0", "Non attribué") }.Concat(labels.Select((label, offset) => new TicketActionChoice((offset + 1).ToString(CultureInfo.InvariantCulture), label))).ToArray();
        }
        return new[]
        {
            Candidate("followup", "Ajouter un suivi", "followup"), Candidate("task", "Ajouter une tâche", "task"),
            Candidate("solution", "Documenter une solution", "followup"),
            Candidate("assignee", "Affecter un technicien", "assignment", "assignee", Choices("assignee", "Technicien exemple de simulation")),
            Candidate("group", "Affecter un groupe", "assignment", "group", Choices("group", "Groupe exemple de simulation")),
            Candidate("status", "Changer le statut", "status", "status", new[] { new TicketActionChoice("1", "Nouveau"), new TicketActionChoice("2", "En cours (attribué)"),
                new TicketActionChoice("3", "En cours (planifié)"), new TicketActionChoice("4", "En attente"), new TicketActionChoice("5", "Résolu"), new TicketActionChoice("6", "Clos") })
        };
    }

    private Task<object?> SimulationPreviewActionAsync(JsonElement a, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); RequireCurrentResult(); var p = WorkPersonal(a); var key = Text(a, "adapterKey", 100);
        var candidate = _workplaceCandidateTicket == p.Key && _workplaceCandidates.TryGetValue(key, out var observed) ? observed
            : _simulationSavedAdapters.Contains(key) ? SimulationCandidates(p).SingleOrDefault(value => value.Adapter.Key == key) : null;
        if (candidate is null) throw new InvalidOperationException("Inspectez les actions locales avant de préparer cet essai.");
        var value = Text(a, "value", 20000).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (value.Length == 0 || value.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'))) throw new InvalidDataException("Saisissez un contenu local valide.");
        var choice = candidate.Choices.FirstOrDefault(item => item.Value == value);
        if (candidate.Choices.Count > 0 && choice is null) throw new InvalidDataException("Choisissez une valeur proposée pour cette simulation.");
        var field = candidate.Adapter.FieldName; var rowIndex = _simulationRows[p.Key];
        var before = field is "status" or "assignee" or "group" ? SimulationCell(rowIndex, field) : "";
        var after = choice?.Label ?? value;
        var token = Guid.NewGuid().ToString("N"); var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        _workplacePreviews.Clear(); _simulationActions.Clear();
        _workplacePreviews[token] = new(token, new(p.Id, p.Url), candidate.Adapter.Name, before, after, "Copie locale uniquement", expires);
        _simulationActions[token] = (field, candidate.Choices.Count > 0 && value == "0" ? "" : after);
        return Task.FromResult<object?>(new { token, ticket = new TicketReference(p.Id, p.Url), operation = candidate.Adapter.Name, before, after,
            otherFields = "Aucun formulaire GLPI envoyé", expiresAt = expires, simulated = true,
            text = "SIMULATION LOCALE\n" + p.Title + "\n" + candidate.Adapter.Name + "\n\nValeur actuelle : " + before + "\nValeur à appliquer :\n" + after + "\n\nCette confirmation modifie une seule fois la copie de simulation. Le fichier original et GLPI restent intacts." });
    }

    private async Task<object?> SimulationConfirmActionAsync(JsonElement a, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); RequireCurrentResult(); var p = WorkPersonal(a); var token = Text(a, "token", 64);
        if (!Flag(a, "confirmed")) throw new InvalidOperationException("Confirmez l’aperçu avant d’appliquer cette modification locale.");
        if (!_workplacePreviews.Remove(token, out var preview) || !_simulationActions.Remove(token, out var action)
            || WorkplaceStore.TicketKey(preview.Ticket) != p.Key || preview.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Aperçu absent, périmé ou déjà appliqué. La copie locale reste inchangée.");
        var rowIndex = _simulationRows[p.Key]; var receipts = WorkReceipts();
        var receipt = new WorkActionReceipt { Id = token, TicketKey = p.Key, TicketId = p.Id, Operation = preview.Operation, Before = preview.Before,
            After = preview.After, OtherFields = preview.OtherFields, RequestedAt = DateTimeOffset.Now, Message = "Modification de la copie locale en cours." };
        receipts.Add(receipt); WorkWriteReceipts(receipts);
        var changed = JsonSerializer.Deserialize<ExtractionResult>(JsonSerializer.Serialize(_simulationLiveData, Json), Json)!;
        var timeline = _simulationTimeline.GetValueOrDefault(p.Key)?.Select(row => row.ToArray()).ToList() ?? new();
        var stamp = DateTimeOffset.Now; var actor = "Opérateur de simulation";
        void SetCell(string role, string header, string content)
        {
            var column = DashboardService.ResolveColumns(changed.Headers, EffectiveSettings.Dashboard).FirstOrDefault(value => value.Role == role)?.Index;
            if (column is null)
            {
                column = changed.Headers.Count; changed.Headers.Add(header);
                for (var index = 0; index < changed.Rows.Count; index++) changed.Rows[index] = changed.Rows[index].Append("").ToArray();
            }
            changed.Rows[rowIndex][column.Value] = content;
        }
        if (action.Kind is "status" or "assignee" or "group")
            SetCell(action.Kind, action.Kind == "status" ? "Statut" : action.Kind == "assignee" ? "Attribué à - Technicien" : "Attribué à - Groupe de techniciens", action.Value);
        else
        {
            var kind = action.Kind == "task" ? "Tâche" : action.Kind == "solution" ? "Solution" : "Suivi";
            timeline.Add(new[] { kind, stamp.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture), actor, action.Value });
            if (action.Kind is "task" or "followup")
            {
                var role = action.Kind == "task" ? "taskAuthor" : "followupAuthor";
                var original = SimulationCell(rowIndex, role);
                SetCell(role, action.Kind == "task" ? "Tâches - Rédacteur" : "Suivis - Rédacteur", string.Join('\n', new[] { original, actor }.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)));
            }
        }
        SetCell("updated", "Dernière modification", stamp.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture));
        // Keep the imported schema stable when it contains no resolution-date
        // field; status transitions are recorded by the local receipt/history.
        if (action.Kind == "status" && action.Value is "Résolu" or "Clos"
            && DashboardService.ResolveColumns(changed.Headers, EffectiveSettings.Dashboard).Single(role => role.Role == "resolved").Index.HasValue)
            SetCell("resolved", "Date de résolution", stamp.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture));
        changed.ExtractedAt = stamp; changed.Provenance.RunId = Guid.NewGuid().ToString("N");
        changed.Status = "Simulation · modification locale appliquée";
        ct.ThrowIfCancellationRequested();
        _simulationTimeline[p.Key] = timeline; _simulationVersions[rowIndex] = _simulationVersions.GetValueOrDefault(rowIndex, 1) + 1;
        _simulationLiveData = changed; _simulationDetails.Remove(p.Key); _workplaceDetails.Remove(p.Key);
        var interpretation = _simulationLiveSettings?.Copy() ?? EffectiveSettings;
        var locations = SimulationEnrich(changed, interpretation, CancellationToken.None);
        SetResult(locations.Data, locations, context: interpretation);
        var detail = SimulationDetail(new(p.Id, p.Url)); WorkAcceptDetail(detail);
        // The local state has committed. Finish its small durable receipt even if
        // cancellation arrives now; never report a successful local edit as unsent.
        receipt.State = "verified"; receipt.FinishedAt = DateTimeOffset.Now;
        receipt.Message = "Modification simulée appliquée et relue dans la copie locale. Aucun échange GLPI.";
        WorkWriteReceipts(receipts);
        var personal = WorkDocument().Copy(); personal.Tickets.Single(value => value.Key == p.Key).LastActionReceipt = receipt.Message + "\n" + preview.Operation + "\n" + preview.After; WorkSave(personal);
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await TicketLibrary.SaveDetailAsync(SimulationSeed(rowIndex), detail, SimulationCoverage(detail), SimulationReaderSignature, ct: finish.Token).ConfigureAwait(false);
        await SimulationArchiveAsync(finish.Token).ConfigureAwait(false);
        Publish("workplace.actions.stage", new { ticketKey = p.Key, stage = "verified", receiptId = token, simulated = true });
        Publish("library.changed", new { simulated = true });
        return SimulationReply(receipt);
    }

    private async Task<object?> SimulationDownloadDocumentAsync(JsonElement a, CancellationToken ct)
    {
        var p = WorkPersonal(a); var detail = SimulationDetail(new(p.Id, p.Url)); var index = Number(a, "index", -1, 0, Math.Max(0, detail.ObservedItems.Count - 1));
        if (index < 0 || index >= detail.ObservedItems.Count || detail.ObservedItems[index].Kind != "document") throw new InvalidDataException("Document local inconnu.");
        var path = await Host.SaveFileAsync("Exporter cette fiche locale de simulation", "Texte (*.txt)|*.txt", "Simulation-Ticket-" + p.Id + ".txt").ConfigureAwait(false);
        if (path is null) return new { saved = false, cancelled = true, simulated = true };
        var content = "SIMULATION LOCALE · export de la copie importée, aucun document GLPI téléchargé\n\n" + detail.Title + "\n\n"
            + string.Join("\n\n", detail.Sections.Select(section => section.Name + "\n" + string.Join('\n', section.Data.Rows.Select(row => string.Join(" : ", row)))));
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return new { saved = true, name = Path.GetFileName(path), simulated = true };
    }
}
