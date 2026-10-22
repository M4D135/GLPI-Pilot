using System.IO;
using System.Windows;
using System.Windows.Threading;
using GlpiPilot.Core;

namespace GlpiPilot;

public partial class MainWindow
{
    private readonly List<TicketActionAdapter> _ticketActionAdapters = new();
    private readonly Dictionary<string, string> _pendingTicketDrafts = new(StringComparer.Ordinal);
    private DispatcherTimer? _ticketDraftTimer;
    private string _actionProfileTicketKey = "";
    private static string TicketActionProfilesPath => Path.Combine(AppPaths.DataRoot, "ticket-action-adapters.json");

    private void InitializeTicketActions()
    {
        var view = _ticketWorkplaceView!.Actions;
        try { _ticketActionAdapters.AddRange(TicketActionProfileStore.Load(TicketActionProfilesPath)); }
        catch (Exception error) { AddLog("ACTIONS ASSISTÉES · adaptateurs non chargés : " + AppPaths.SafeError(error)); }
        view.BrowserRequested += () => TicketBrowser_Click(this, new RoutedEventArgs());
        view.InspectRequested += async () =>
        {
            if (_busy || _closing || _ticketDetail == null) return;
            var ticket = _ticketDetail.Ticket;
            await RunAsync("Inspection du formulaire GLPI ouvert…", async ct =>
            {
                try { view.ShowCandidates(await _browser.LearnTicketActionsAsync(ticket, ReadSettings(), ct)); }
                catch (Exception error) { view.SetMessage(AppPaths.SafeError(error)); throw; }
            });
        };
        view.AdapterSaveRequested += adapter =>
        {
            try
            {
                _ = TicketActionService.Validate(adapter, _ticketDetail?.Ticket);
                var copy = _ticketActionAdapters.Where(item => !(item.Installation == adapter.Installation && item.Kind == adapter.Kind && item.FormSelector == adapter.FormSelector)).Append(adapter.Copy()).ToArray();
                TicketActionProfileStore.Save(TicketActionProfilesPath, copy); _ticketActionAdapters.Clear(); _ticketActionAdapters.AddRange(copy);
                view.SetMessage("Adaptateur mémorisé pour cette installation. Il sera entièrement revalidé sur chaque ticket.");
            }
            catch (Exception error) { view.SetMessage(AppPaths.SafeError(error)); }
        };
        view.PreviewRequested += async request =>
        {
            if (_busy || _closing || _ticketDetail == null) return;
            var ticket = _ticketDetail.Ticket;
            await RunAsync("Prévisualisation de l’action GLPI…", async ct =>
            {
                try { view.ShowPreview(await _browser.PreviewTicketActionAsync(ticket, request, ReadSettings(), ct)); }
                catch (Exception error) { view.SetMessage(AppPaths.SafeError(error)); throw; }
            });
        };
        view.ConfirmRequested += async preview =>
        {
            if (_busy || _closing || _ticketDetail == null || _ticketDetail.Ticket != preview.Ticket) return;
            if (MessageBox.Show(this, preview.Text + "\n\nEnvoyer cette action dans GLPI maintenant ?", "Confirmation de l’action GLPI", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var key = WorkplaceStore.TicketKey(preview.Ticket);
            var receipt = DateTimeOffset.Now.ToString("dd/MM/yyyy HH:mm:ss") + "\n" + preview.Text;
            var document = _workplace.Copy(); var record = document.Tickets.FirstOrDefault(item => item.Key == key);
            if (record == null) record = WorkplaceStore.Remember(document, preview.Ticket, _ticketDetail.Title, false);
            record.LastActionReceipt = receipt + "\n\nEnvoi demandé ; résultat non encore confirmé. Si l’application s’est interrompue, vérifier GLPI avant toute nouvelle action.";
            if (!SavePersonalWorkspace(document)) { view.SetMessage("Le reçu local n’a pas pu être enregistré. Aucune action GLPI n’a été envoyée."); return; }
            await RunAsync("Soumission unique et relecture de contrôle…", async ct =>
            {
                TicketActionOutcome outcome;
                try { outcome = await _browser.ConfirmTicketActionAsync(preview.Token, true, ReadSettings(), ct); }
                catch (Exception error) { outcome = new(false, AppPaths.SafeError(error)); }
                var after = _workplace.Copy(); var personal = after.Tickets.FirstOrDefault(item => item.Key == key);
                if (personal != null) { personal.LastActionReceipt = receipt + "\n\n" + outcome.Message; SavePersonalWorkspace(after); }
                view.ShowOutcome(outcome); SetStatus(outcome.Message);
                AddLog("ACTION GLPI · " + (outcome.Verified ? "résultat vérifié après relecture." : "résultat non confirmé ; vérifier manuellement avant toute nouvelle action."));
                if (outcome.Verified)
                {
                    _ticketCache.Clear();
                    var fresh = await _browser.ReadTicketDetailsAsync(preview.Ticket, ReadSettings(), null, ct);
                    PresentTicketResult(fresh, false, preview.Ticket.Url + "\n" + System.Text.Json.JsonSerializer.Serialize(ReadSettings()));
                }
            });
        };
        _ticketDraftTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _ticketDraftTimer.Tick += (_, _) => FlushTicketDrafts();
        _ticketWorkplaceView.Drafts.DraftChanged += (key, text) => { _pendingTicketDrafts[key] = text; _ticketDraftTimer.Stop(); _ticketDraftTimer.Start(); };
        Closing += (_, _) => FlushTicketDrafts();
    }

    private void FlushTicketDrafts()
    {
        _ticketDraftTimer?.Stop(); if (_pendingTicketDrafts.Count == 0) return;
        var document = _workplace.Copy();
        foreach (var (key, draft) in _pendingTicketDrafts)
        {
            var record = document.Tickets.FirstOrDefault(ticket => ticket.Key == key);
            if (record != null) record.FollowupDraft = draft;
        }
        var success = SavePersonalWorkspace(document);
        if (success) _pendingTicketDrafts.Clear();
        _ticketWorkplaceView?.Drafts.Saved(success);
    }

    private void RefreshTicketActionAdapters()
    {
        var key = _ticketDetail == null ? "" : WorkplaceStore.TicketKey(_ticketDetail.Ticket);
        if (key == _actionProfileTicketKey) return;
        FlushTicketDrafts(); _actionProfileTicketKey = key;
        var installation = _ticketDetail == null ? "" : TicketActionService.Installation(_ticketDetail.Ticket);
        _ticketWorkplaceView?.Actions.ShowAdapters(_ticketActionAdapters.Where(adapter => adapter.Installation == installation));
        var receipt = _workplace.Tickets.FirstOrDefault(ticket => ticket.Key == key)?.LastActionReceipt;
        if (!string.IsNullOrEmpty(receipt)) _ticketWorkplaceView?.Actions.SetMessage("Dernier reçu local\n" + receipt);
    }
}
