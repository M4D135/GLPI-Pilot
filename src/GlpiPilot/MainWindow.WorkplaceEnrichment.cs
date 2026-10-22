using System.Data;
using System.IO;
using System.Text.Json;
using System.Windows;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot;

public partial class MainWindow
{
    private TicketEnrichmentQueue _ticketEnrichmentQueue = new();
    private CancellationTokenSource? _ticketEnrichmentCancellation;
    private static string TicketEnrichmentPath => Path.Combine(AppPaths.DataRoot, "ticket-enrichment.json");
    private void InitializeTicketEnrichment()
    {
        var view = _ticketWorkplaceView!.Enrichment;
        try { _ticketEnrichmentQueue = TicketEnrichmentService.Load(TicketEnrichmentPath); }
        catch (Exception error) { AddLog("ENRICHISSEMENT · file non restaurée : " + AppPaths.SafeError(error)); }
        view.Show(_ticketEnrichmentQueue, "Une file restaurée ne se lance jamais automatiquement.");
        view.StartRequested += StartTicketEnrichment;
        view.CancelRequested += () => _ticketEnrichmentCancellation?.Cancel();
        view.ResetRequested += () => { if (_busy || _closing) return; _ticketEnrichmentQueue.Results.Clear(); PersistTicketEnrichment(); view.Show(_ticketEnrichmentQueue, "Résultats réinitialisés. Le prochain lancement relira la file."); };
        view.TicketRequested += ticket => QueuePersonalTicketReference(ticket, false);
        view.PrioritizeRequested += key =>
        {
            if (_busy || _closing) return;
            _ticketEnrichmentQueue.Tickets = _ticketEnrichmentQueue.Tickets.OrderBy(ticket => WorkplaceStore.TicketKey(ticket) == key ? 0 : 1).ToList();
            PersistTicketEnrichment(); view.Show(_ticketEnrichmentQueue);
        };
        foreach (var (label, selection) in new[] { ("Enrichir la sélection", true), ("Enrichir la requête", false) })
        {
            var button = WorkplaceControls.Command(label, () => PreparePreviewEnrichment(selection));
            button.MinHeight = 32; button.FontSize = 12; button.Padding = new Thickness(8, 4, 8, 4); PreviewActions.Children.Add(button);
        }
        Closing += (_, _) => _ticketEnrichmentCancellation?.Cancel();
    }
    private void PreparePreviewEnrichment(bool selection)
    {
        if (_closing || _busy && !_ticketLoading || _result == null) return;
        try
        {
            var source = _result; var settings = ReadSettings();
            var indices = selection ? ResultGrid.SelectedItems.OfType<DataRowView>().Select(item => GetPreviewSourceIndex(item.Row)).Where(index => index.HasValue).Select(index => index!.Value).Distinct().ToArray()
                : ReferenceEquals(_previewSource, source) ? _previewQueryResult?.SourceIndices ?? Array.Empty<int>() : Array.Empty<int>();
            var references = new List<TicketReference>(); var skipped = 0;
            foreach (var index in indices.Take(TicketEnrichmentService.MaximumCandidates))
            {
                try { references.Add(TicketReferenceResolver.Resolve(source, source.Rows[index], settings)); }
                catch (InvalidOperationException) { skipped++; }
            }
            var coverage = (selection ? "Sélection" : "Requête affichée") + $" · {skipped} références non exploitables";
            if (indices.Length > TicketEnrichmentService.MaximumCandidates || !selection && _previewQueryResult?.IsLimited == true) coverage += " · candidats tronqués à la présentation, maximum 1 000";
            EnrichTicketReferences(references, coverage);
        }
        catch (Exception error) { SetStatus(AppPaths.SafeError(error)); }
    }

    // Shared entry point for current selections, query scopes and historical absences. It only prepares a reviewable queue.
    private void EnrichTicketReferences(IEnumerable<TicketReference> references, string label)
    {
        if (_closing || _busy && !_ticketLoading) return;
        try
        {
            InvalidateTicketRequest();
            var candidates = references.Take(TicketEnrichmentService.MaximumCandidates).Select(TicketEnrichmentService.ValidateReference).DistinctBy(WorkplaceStore.TicketKey).ToList();
            if (candidates.Count == 0) { SetStatus("Aucune référence de ticket exploitable à enrichir."); return; }
            var active = _selectedTicket == null ? "" : WorkplaceStore.TicketKey(_selectedTicket);
            _ticketEnrichmentQueue = new() { Label = label, Limit = 20, Tickets = candidates.OrderBy(ticket => WorkplaceStore.TicketKey(ticket) == active ? 0 : 1).ToList() };
            PersistTicketEnrichment(); _ticketWorkplaceView!.Enrichment.Show(_ticketEnrichmentQueue, "Choisissez la limite globale et lancez la lecture quand la file vous convient.");
            MainTabs.SelectedItem = TicketDetailTab; _ticketWorkplaceView.ShowEnrichment();
        }
        catch (Exception error) { SetStatus(AppPaths.SafeError(error)); }
    }
    private async void StartTicketEnrichment(int limit)
    {
        if (_closing || _busy || _ticketEnrichmentQueue.Tickets.Count == 0) return;
        var queue = _ticketEnrichmentQueue; var settings = ReadSettings();
        queue.Limit = limit; var completed = queue.Results.Where(result => result.State == "Lu").Select(result => WorkplaceStore.TicketKey(result.Ticket)).ToHashSet(StringComparer.Ordinal);
        var planned = TicketEnrichmentService.Plan(queue.Tickets.Where(ticket => !completed.Contains(WorkplaceStore.TicketKey(ticket))), limit);
        if (planned.Count == 0) { _ticketWorkplaceView!.Enrichment.Show(queue, "Toutes les fiches ont été lues. Réinitialisez la file pour les relire."); return; }
        PersistTicketEnrichment();
        _ticketWorkplaceView!.Enrichment.SetBusy(true);
        try
        {
            await RunAsync("Lecture ciblée des fiches de la file…", async ct =>
            {
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct); _ticketEnrichmentCancellation = operation;
                var read = new List<TicketDetailResult>();
                var acceptingProgress = true;
                var progress = new Progress<TicketEnrichmentOutcome>(result =>
                {
                    if (!acceptingProgress || _closing || !ReferenceEquals(queue, _ticketEnrichmentQueue)) return;
                    queue.Results.RemoveAll(item => item.Ticket == result.Ticket); queue.Results.Add(result); PersistTicketEnrichment();
                    _ticketWorkplaceView.Enrichment.Show(queue, "Lecture en cours · la recherche GLPI reste ouverte.");
                });
                try
                {
                    var outcomes = await TicketEnrichmentService.ExecuteAsync(planned, (ticket, token) => _browser.ReadTicketDetailsAsync(ticket, settings, null, token), detail => read.Add(detail), progress, operation.Token);
                    acceptingProgress = false;
                    var processed = outcomes.Select(outcome => WorkplaceStore.TicketKey(outcome.Ticket)).ToHashSet(StringComparer.Ordinal);
                    queue.Results = queue.Results.Where(outcome => !processed.Contains(WorkplaceStore.TicketKey(outcome.Ticket))).Concat(outcomes).ToList();
                    var personal = _workplace.Copy();
                    foreach (var detail in read)
                    {
                        CacheTicket(detail.Ticket.Url + "\n" + JsonSerializer.Serialize(settings), detail);
                        var record = WorkplaceStore.Remember(personal, detail.Ticket, detail.Title, open: false);
                        _personalReadChanges[record.Key] = WorkplaceStore.ObserveRead(record, detail);
                        if (record.Offline) WorkplaceStore.SaveOffline(TicketOfflineDirectory, detail);
                    }
                    SavePersonalWorkspace(personal); PersistTicketEnrichment();
                    _ticketWorkplaceView.Enrichment.Show(queue, $"Fin de la file : {outcomes.Count(item => item.State == "Lu")} fiches lues sur {planned.Count} prévues pour ce lancement ; {queue.Tickets.Count - queue.Results.Count(item => item.State == "Lu")} restent sans lecture réussie.");
                    SetStatus("File d’enrichissement terminée ou interrompue ; résultats datés disponibles dans la fiche → Enrichissement.");
                }
                finally { acceptingProgress = false; if (ReferenceEquals(_ticketEnrichmentCancellation, operation)) _ticketEnrichmentCancellation = null; }
            });
        }
        finally { _ticketWorkplaceView.Enrichment.SetBusy(false); }
    }
    private void PersistTicketEnrichment()
    {
        try { TicketEnrichmentService.Save(TicketEnrichmentPath, _ticketEnrichmentQueue); }
        catch (Exception error) { AddLog("ENRICHISSEMENT · file non enregistrée : " + AppPaths.SafeError(error)); }
    }
}
