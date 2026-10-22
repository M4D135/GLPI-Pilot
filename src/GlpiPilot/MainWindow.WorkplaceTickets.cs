using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot;

public partial class MainWindow
{
    private WorkplaceDocument _workplace = new();
    private TicketWorkplaceView? _ticketWorkplaceView;
    private DispatcherTimer? _personalReminderTimer;
    private readonly Dictionary<string, TicketReadChanges> _personalReadChanges = new(StringComparer.Ordinal);
    private readonly HashSet<string> _remindersAnnounced = new(StringComparer.Ordinal);
    private bool _workplaceUnreadable;
    private DesktopReminderNotifier? _desktopReminderNotifier;
    private static string WorkplacePath => Path.Combine(AppPaths.DataRoot, "workplace.json");
    private static string TicketOfflineDirectory => Path.Combine(AppPaths.DataRoot, "TicketCache");

    private void InitializePersonalTicketWorkspace()
    {
        try { _workplace = WorkplaceStore.Load(WorkplacePath); }
        catch (Exception error)
        {
            _workplaceUnreadable = true;
            AddLog("ESPACE PERSONNEL · lecture impossible ; une copie du fichier sera conservée avant un nouvel enregistrement. " + AppPaths.SafeError(error));
        }
        var original = TicketDetailTab.Content as UIElement;
        if (original == null) return;
        TicketDetailTab.Content = null;
        _ticketWorkplaceView = new TicketWorkplaceView(original);
        TicketDetailTab.Content = _ticketWorkplaceView;
        _ticketWorkplaceView.TicketRequested += OpenPersonalTicket;
        _ticketWorkplaceView.TicketClosed += ClosePersonalTicket;
        _ticketWorkplaceView.Intelligence.TicketRequested += ticket => QueuePersonalTicketReference(ticket, false);
        _ticketWorkplaceView.Intelligence.RecurrencesRequested += ReadTicketRecurrences;
        _ticketWorkplaceView.Editor.SaveRequested += SavePersonalTicket;
        _ticketWorkplaceView.FiltersChanged += () => { if (_ticketDetail != null) { BuildTicketSectionPreview(); QueueReadingPositionSave(); } };
        _ticketWorkplaceView.GroupByDayChanged += value =>
        {
            var copy = _workplace.Copy(); copy.GroupTimelineByDay = value; SavePersonalWorkspace(copy);
        };
        _ticketWorkplaceView.SearchHitRequested += hit =>
        {
            if (_ticketDetail == null || hit.SectionIndex < 0 || hit.SectionIndex >= _ticketDetail.Sections.Count) return;
            var section = _ticketDetail.Sections[hit.SectionIndex];
            PresentTicketNavigationEntry(new("search-hit", section.Name, "Résultat dans les sections lues", section));
            TicketSearchBox.Text = _ticketWorkplaceView.SearchText;
        };
        _ticketWorkplaceView.FavoriteSectionRequested += () =>
        {
            if (TicketNavigation.SelectedItem is not TicketNavigationEntry entry) return;
            var copy = _workplace.Copy();
            if (!copy.FavoriteSections.Remove(entry.Key)) copy.FavoriteSections.Add(entry.Key);
            if (SavePersonalWorkspace(copy)) { BuildTicketNavigation(entry.Key); SetStatus("Favoris de rubriques enregistrés sur ce poste."); }
        };
        _ticketWorkplaceView.DefaultSectionRequested += () =>
        {
            if (TicketNavigation.SelectedItem is not TicketNavigationEntry entry) return;
            var copy = _workplace.Copy(); copy.DefaultSection = entry.Key;
            if (SavePersonalWorkspace(copy)) SetStatus("Rubrique d’ouverture mémorisée : " + entry.Label);
        };
        InitializeReadingPersistence();
        InitializeTicketLinks();
        InitializeTicketActions();
        InitializeTicketEnrichment();
        _personalReminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _personalReminderTimer.Tick += (_, _) => AnnouncePersonalReminders(); _personalReminderTimer.Start();
        RefreshPersonalTicketPresentation();
    }

    private bool SavePersonalWorkspace(WorkplaceDocument document)
    {
        try
        {
            if (_workplaceUnreadable && File.Exists(WorkplacePath))
            {
                File.Copy(WorkplacePath, WorkplacePath + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: false);
                _workplaceUnreadable = false;
            }
            var validated = WorkplaceStore.Validate(document);
            WorkplaceStore.Save(WorkplacePath, validated); _workplace = validated;
            RefreshPersonalDay(); return true;
        }
        catch (Exception error)
        {
            _ticketWorkplaceView?.Editor.SetMessage("L’enregistrement personnel a échoué ; la saisie reste disponible. " + AppPaths.SafeError(error), true);
            AddLog("ESPACE PERSONNEL · enregistrement impossible. " + AppPaths.SafeError(error)); return false;
        }
    }

    private void RememberPersonalTicketRead(TicketDetailResult detail, bool fresh)
    {
        try
        {
            var document = _workplace.Copy();
            var record = WorkplaceStore.Remember(document, detail.Ticket, detail.Title);
            if (fresh)
            {
                _personalReadChanges[record.Key] = WorkplaceStore.ObserveRead(record, detail);
                if (record.Offline) WorkplaceStore.SaveOffline(TicketOfflineDirectory, detail);
            }
            SavePersonalWorkspace(document);
        }
        catch (Exception error) { AddLog("ESPACE PERSONNEL · la lecture reste disponible, mais ses repères n’ont pas été mémorisés. " + AppPaths.SafeError(error)); }
    }

    private void RefreshPersonalTicketPresentation()
    {
        if (_ticketWorkplaceView == null) return;
        var key = _selectedTicket == null ? _workplace.ActiveTicketKey : WorkplaceStore.TicketKey(_selectedTicket);
        var record = _workplace.Tickets.FirstOrDefault(item => item.Key == key);
        _ticketWorkplaceView.Show(_workplace, record, _ticketDetail, _personalReadChanges.GetValueOrDefault(key));
        RefreshTicketIntelligence();
        RefreshTicketActionAdapters();
    }

    private void SavePersonalTicket(PersonalTicket edited)
    {
        void Reject(string message)
        {
            _ticketWorkplaceView?.Editor.RejectSave(edited, message);
            _techniciansView?.Day.RejectSave(edited, message);
            if (!_closing) SetStatus(message);
        }
        if (_busy || _closing) { Reject("Enregistrement différé : attendez la fin du travail en cours puis réessayez. Votre saisie reste disponible."); return; }
        try
        {
            var document = _workplace.Copy(); var index = document.Tickets.FindIndex(item => item.Key == edited.Key);
            if (index < 0)
            {
                WorkplaceStore.Remember(document, new(edited.Id, edited.Url), edited.Title, open: false);
                index = document.Tickets.FindIndex(item => item.Key == edited.Key);
            }
            var before = document.Tickets[index];
            // Note/queue editors do not own newer reading markers, drafts or action receipts.
            edited.LastReadAt = before.LastReadAt; edited.LastOpenedAt = before.LastOpenedAt; edited.PreferredSection = before.PreferredSection;
            edited.ReadingPosition = before.ReadingPosition.Copy(); edited.RowFingerprints = before.RowFingerprints.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);
            edited.RowsFullyCompared = before.RowsFullyCompared; edited.SectionFingerprints = new(before.SectionFingerprints, StringComparer.Ordinal);
            edited.FollowupDraft = before.FollowupDraft; edited.LastActionReceipt = before.LastActionReceipt;
            if (edited.ReminderAt == before.ReminderAt) edited.ReminderAcknowledgedAt = before.ReminderAcknowledgedAt;
            document.Tickets[index] = edited.Copy();
            _ = WorkplaceStore.Validate(document);
            if (edited.Offline && _ticketDetail != null && WorkplaceStore.TicketKey(_ticketDetail.Ticket) == edited.Key)
                WorkplaceStore.SaveOffline(TicketOfflineDirectory, _ticketDetail);
            if (!SavePersonalWorkspace(document))
            { Reject("L’enregistrement personnel a échoué. Votre saisie reste disponible ; consultez le journal puis réessayez."); return; }
            _ticketWorkplaceView?.Editor.AcceptSaved(edited);
            _techniciansView?.Day.AcceptSaved(edited);
            if (before.Offline && !edited.Offline) WorkplaceStore.RemoveOffline(TicketOfflineDirectory, new(edited.Id, edited.Url));
            RefreshPersonalTicketPresentation();
            _ticketWorkplaceView?.Editor.SetMessage("Travail personnel enregistré. Les sections hors connexion conservent leur date de lecture.");
            SetStatus("Notes, file de travail et rappels personnels enregistrés.");
        }
        catch (Exception error) { Reject(AppPaths.SafeError(error)); }
    }

    private void OpenPersonalTicket(PersonalTicket ticket)
    {
        if (_busy || _closing) return;
        try
        {
            MainTabs.SelectedItem = TicketDetailTab;
            var reference = new TicketReference(ticket.Id, ticket.Url);
            var settings = ReadSettings(); var cacheKey = reference.Url + "\n" + JsonSerializer.Serialize(settings);
            InvalidateTicketRequest();
            if (GetCachedTicket(cacheKey) is TicketDetailResult memory)
            { PresentTicketResult(memory, true, cacheKey); _ticketWorkplaceView?.ShowReader(); return; }
            if (ticket.Offline && WorkplaceStore.LoadOffline(TicketOfflineDirectory, reference) is TicketDetailResult offline)
            {
                PresentTicketResult(offline, true, cacheKey);
                TicketStateText.Text = $"Copie hors connexion lue le {offline.ReadAt.LocalDateTime:dd/MM/yyyy HH:mm}. Actualiser relit GLPI.";
                _ticketWorkplaceView?.ShowReader(); return;
            }
            QueuePersonalTicketReference(reference, false);
        }
        catch (Exception error) { ReportTicketError(error); }
    }

    private void QueuePersonalTicketReference(TicketReference ticket, bool force)
    {
        if (_closing || (_busy && !_ticketLoading)) return;
        try
        {
            var settings = ReadSettings();
            if (!Uri.TryCreate(settings.PortalUrl, UriKind.Absolute, out var portal)
                || !Uri.TryCreate(ticket.Url, UriKind.Absolute, out var target)
                || portal.Scheme != target.Scheme || portal.Host != target.Host || portal.Port != target.Port)
                throw new InvalidOperationException("Ce ticket appartient à une autre source. Sélectionnez son portail pour le relire, ou utilisez sa copie hors connexion.");
            InvalidateTicketRequest(); MainTabs.SelectedItem = TicketDetailTab; _selectedTicket = ticket;
            ClearTicketPresentation("Ticket " + ticket.Id, "Lecture du ticket conservé en préparation…");
            _pendingTicket = new(ticket, settings, ticket.Url + "\n" + JsonSerializer.Serialize(settings), _ticketGeneration, force, DateTimeOffset.UtcNow);
            ArmTicketTimer(); UpdateTicketControls();
        }
        catch (Exception error) { ReportTicketError(error); }
    }

    private void ClosePersonalTicket(string key)
    {
        if (_busy || _closing) return;
        var document = _workplace.Copy(); document.OpenTicketKeys.Remove(key);
        if (document.ActiveTicketKey == key) document.ActiveTicketKey = document.OpenTicketKeys.LastOrDefault() ?? "";
        if (!SavePersonalWorkspace(document)) return;
        var next = document.Tickets.FirstOrDefault(ticket => ticket.Key == document.ActiveTicketKey);
        if (next != null) OpenPersonalTicket(next);
        else { InvalidateTicketRequest(); _selectedTicket = null; ClearTicketPresentation("Détail du ticket", "Sélectionnez un ticket ou ouvrez un ticket épinglé depuis Ma journée."); }
        RefreshPersonalTicketPresentation();
    }

    private void AnnouncePersonalReminders()
    {
        if (_closing) return;
        if (!NotificationPolicy.CanNotify()) { RefreshPersonalDay(); return; }
        var due = _workplace.Tickets.Where(ticket => ticket.ReminderAt <= DateTimeOffset.Now
            && (!ticket.ReminderAcknowledgedAt.HasValue || ticket.ReminderAcknowledgedAt < ticket.ReminderAt)).ToArray();
        var newlyDue = due.Where(ticket => !_remindersAnnounced.Contains(ticket.Key + "|" + ticket.ReminderAt?.ToUnixTimeSeconds())).ToArray();
        if (newlyDue.Length > 0)
        {
            if (!_busy) SetStatus($"{due.Length:N0} rappel(s) personnel(s) à consulter dans Techniciens → Ma journée.");
            AddLog($"ESPACE PERSONNEL · {newlyDue.Length:N0} rappel(s) arrivé(s) à échéance ; ouvrir Ma journée.");
            if (_workplace.DesktopReminders)
            {
                try
                {
                    _desktopReminderNotifier ??= new DesktopReminderNotifier(this, () =>
                    {
                        if (_closing) return;
                        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                        Activate(); MainTabs.SelectedItem = TechniciansTab; _techniciansView?.ShowPersonalDay();
                    });
                    if (!_desktopReminderNotifier.Show(due.Length)) AddLog("RAPPELS · notification Windows indisponible ; les rappels restent consultables dans Ma journée.");
                }
                catch (Exception error) { AddLog("RAPPELS · notification Windows non affichée : " + AppPaths.SafeError(error)); }
            }
        }
        if (NotificationPolicy.CanNotify())
            foreach (var ticket in newlyDue) _remindersAnnounced.Add(ticket.Key + "|" + ticket.ReminderAt?.ToUnixTimeSeconds());
        RefreshPersonalDay();
    }
}
