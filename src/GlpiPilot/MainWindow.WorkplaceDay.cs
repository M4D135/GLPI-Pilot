using GlpiPilot.Core;
using GlpiPilot.UI;
using System.Data;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;

namespace GlpiPilot;

public partial class MainWindow
{
    private TechnicianWorkloadSnapshot? _personalWorkload;
    private long _personalDayGeneration;
    private CancellationTokenSource? _personalDayCancellation;
    private bool _personalDayInitialized;

    private void InitializePersonalDayWorkspace()
    {
        if (_techniciansView == null || _personalDayInitialized) return;
        _personalDayInitialized = true;
        var addToDay = WorkplaceControls.Command("Dans ma journée", AddPreviewSelectionToPersonalDay);
        addToDay.MinHeight = 32; addToDay.FontSize = 12; addToDay.Padding = new Thickness(10, 5, 10, 5); addToDay.Margin = new Thickness(0, 0, 8, 0);
        addToDay.ToolTip = "Ajouter les tickets sélectionnés à ma file personnelle. Ctrl ou Maj permet de choisir plusieurs lignes.";
        AutomationProperties.SetAutomationId(addToDay, "Preview.AddPersonalQueue");
        PreviewActions.Children.Insert(1, addToDay);
        addToDay.SetBinding(IsEnabledProperty, new Binding("IsEnabled") { Source = OpenTicketButton });
        try { _techniciansView.Planning.SetScenario(TechnicianPlanningService.Load(System.IO.Path.Combine(AppPaths.DataRoot, "dispatch-scenario.json"))); }
        catch (Exception error) { AddLog("ORGANISATION · scénario local non chargé. " + AppPaths.SafeError(error)); }
        _techniciansView.Day.TicketRequested += OpenPersonalTicket;
        _techniciansView.Day.SaveRequested += SavePersonalTicket;
        _techniciansView.Day.DesktopRemindersChanged += value =>
        {
            if (_closing) return;
            var copy = _workplace.Copy(); copy.DesktopReminders = value;
            if (SavePersonalWorkspace(copy) && !value) { _desktopReminderNotifier?.Dispose(); _desktopReminderNotifier = null; }
        };
        _techniciansView.Day.MoveRequested += (key, direction) =>
        {
            if (_busy || _closing) return;
            var copy = _workplace.Copy(); WorkplaceStore.MoveQueue(copy, key, direction); SavePersonalWorkspace(copy);
        };
        _techniciansView.Day.TemplateSaveRequested += (name, lines) =>
        {
            if (_busy || _closing) return;
            try
            {
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Donnez un nom au modèle de liste.");
                var copy = _workplace.Copy(); copy.ChecklistTemplates[name] = lines;
                if (SavePersonalWorkspace(copy)) SetStatus("Modèle de vérification personnel enregistré.");
            }
            catch (Exception error) { SetStatus(AppPaths.SafeError(error)); }
        };
        _techniciansView.Day.TemplateDeleteRequested += name =>
        {
            if (_busy || _closing) return;
            var copy = _workplace.Copy(); copy.ChecklistTemplates.Remove(name); SavePersonalWorkspace(copy);
        };
        _techniciansView.Planning.RowsRequested += (rows, label) =>
        {
            if (!_busy && !_closing && TechnicianSourceIsCurrent()) ShowDashboardRows(rows, label);
        };
        _techniciansView.RestoreWorkspace(_workplace.ActiveWorkspace);
        _techniciansView.WorkspaceChanged += key =>
        {
            if (_closing || key == _workplace.ActiveWorkspace) return;
            var copy = _workplace.Copy(); copy.ActiveWorkspace = key;
            if (key == "day") copy.LastDayViewedAt = DateTimeOffset.Now;
            SavePersonalWorkspace(copy);
        };
        RefreshPersonalDay();
    }

    private void AddPreviewSelectionToPersonalDay()
    {
        if (_busy && !_ticketLoading || _closing || _result == null) return;
        try
        {
            InvalidateTicketRequest();
            var source = _result; var settings = ReadSettings();
            var indices = ResultGrid.SelectedItems.OfType<DataRowView>().Select(item => GetPreviewSourceIndex(item.Row))
                .Where(index => index.HasValue).Select(index => index!.Value).Distinct().ToArray();
            if (indices.Length == 0) { SetStatus("Sélectionnez un ou plusieurs tickets du relevé courant."); return; }
            if (indices.Length > WorkplaceStore.MaximumTickets) throw new InvalidOperationException("La file personnelle accepte au maximum 500 tickets conservés. Réduisez cette sélection.");
            var document = _workplace.Copy(); var added = 0; var skipped = 0; string? failure = null;
            var titleColumn = TechnicianSourceIsCurrent() ? _personalWorkload?.Global.Column("title")?.Index : null;
            titleColumn ??= source.Headers.FindIndex(header => string.Equals(header, settings.Dashboard.ColumnMappings.GetValueOrDefault("title"), StringComparison.Ordinal)
                || string.Equals(header, "Titre", StringComparison.OrdinalIgnoreCase) || string.Equals(header, "Title", StringComparison.OrdinalIgnoreCase));
            var next = document.Tickets.Where(ticket => ticket.QueueOrder.HasValue).Select(ticket => ticket.QueueOrder!.Value).DefaultIfEmpty(-1).Max() + 1;
            foreach (var index in indices)
            {
                TicketReference reference;
                try { reference = TicketReferenceResolver.Resolve(source, source.Rows[index], settings); }
                catch (InvalidOperationException error) { skipped++; failure ??= AppPaths.SafeError(error); continue; }
                var row = source.Rows[index]; var title = titleColumn >= 0 && titleColumn < row.Length ? row[titleColumn.Value] : "Ticket " + reference.Id;
                var record = WorkplaceStore.Remember(document, reference, title, open: false);
                if (!record.QueueOrder.HasValue) { record.QueueOrder = next++; added++; }
            }
            if (added == 0 && skipped == indices.Length) throw new InvalidOperationException(failure ?? "Les identifiants de la sélection ne sont pas exploitables.");
            var queued = document.Tickets.Where(ticket => ticket.QueueOrder.HasValue).OrderBy(ticket => ticket.QueueOrder).ToArray();
            for (var order = 0; order < queued.Length; order++) queued[order].QueueOrder = order;
            if (!SavePersonalWorkspace(document)) return;
            MainTabs.SelectedItem = TechniciansTab; _techniciansView?.ShowPersonalDay();
            SetStatus($"{added:N0} ticket(s) ajouté(s) à ma journée · {indices.Length - added - skipped:N0} déjà présents · {skipped:N0} référence(s) non exploitable(s).");
            if (skipped > 0) AddLog("MA JOURNÉE · certaines références n’ont pas été ajoutées : " + failure);
        }
        catch (Exception error) { SetStatus(AppPaths.SafeError(error)); }
    }

    private async void RefreshPersonalDay()
    {
        if (!_personalDayInitialized || _techniciansView == null || _closing) return;
        _personalDayCancellation?.Cancel();
        var generation = ++_personalDayGeneration;
        var document = _workplace.Copy(); var source = _result; var workload = _personalWorkload;
        var settings = _settings.Copy(); var cities = CitySourceIsCurrent() ? _cityEnrichment : null;
        var personalKey = settings.Dashboard.PersonalTechnician;
        if (source == null || workload == null || !ReferenceEquals(source, _technicianRosterSource))
        {
            _techniciansView.Day.Show(document.Tickets.Select(ticket => new PersonalDayTicket(ticket, "État non vérifié", "", "", null, false, false)), document, "", null);
            return;
        }
        var operation = new CancellationTokenSource(); _personalDayCancellation = operation;
        try
        {
            var projection = await Task.Run(() =>
            {
                var saved = document.Tickets.ToDictionary(ticket => ticket.Key, StringComparer.Ordinal);
                var current = new Dictionary<string, PersonalDayTicket>(StringComparer.Ordinal);
                var dispatch = new Dictionary<string, DispatchTicket>(StringComparer.Ordinal);
                var mine = workload.Roster.Technicians.FirstOrDefault(tech => tech.Key == personalKey)?.Rows.ToHashSet() ?? new HashSet<int>();
                var owners = new Dictionary<int, List<string>>();
                foreach (var tech in workload.Roster.Technicians)
                    foreach (var row in tech.Rows)
                    { if (!owners.TryGetValue(row, out var list)) owners[row] = list = new(); list.Add(tech.Key); }
                var cityNames = cities?.Matches.Where(match => match.CityName != null).ToDictionary(match => match.RowIndex, match => match.CityName!) ?? new Dictionary<int, string>();
                string Cell(string[] row, string role) => workload.Global.Column(role)?.Index is int column && column < row.Length ? row[column] ?? "" : "";
                for (var index = 0; index < source.Rows.Count; index++)
                {
                    if ((index & 127) == 0) operation.Token.ThrowIfCancellationRequested();
                    var row = source.Rows[index]; var status = Cell(row, "status"); var statusCode = DashboardService.Status(status);
                    TicketReference reference;
                    try { reference = TicketReferenceResolver.Resolve(source, row, settings); }
                    catch (InvalidOperationException) { continue; }
                    var key = WorkplaceStore.TicketKey(reference); var title = WorkplaceStore.DisplayTitle(Cell(row, "title"));
                    var record = saved.TryGetValue(key, out var existing) ? existing : new PersonalTicket { Key = key, Id = reference.Id, Url = reference.Url, Title = title };
                    if (record.Title.Length == 0) record.Title = title;
                    var city = cityNames.GetValueOrDefault(index, Cell(row, "location"));
                    if (mine.Contains(index) || saved.ContainsKey(key))
                        current.TryAdd(key, new(record, status, city, Cell(row, "due"), statusCode == 0 ? null : statusCode is >= 1 and <= 4, true, mine.Contains(index)));
                    if (statusCode < 5)
                        dispatch.TryAdd(key, new(key, reference.Id, title, city, Cell(row, "category"), owners.GetValueOrDefault(index)?.ToArray() ?? Array.Empty<string>(), index, statusCode is >= 1 and <= 4));
                }
                foreach (var ticket in saved.Values)
                    current.TryAdd(ticket.Key, new(ticket, "État non vérifié", "", "", null, false, false));
                return (Day: current.Values.ToArray(), Dispatch: dispatch.Values.ToArray());
            }, operation.Token);
            if (_closing || generation != _personalDayGeneration || !ReferenceEquals(source, _result)) return;
            var name = workload.Roster.Technicians.FirstOrDefault(tech => tech.Key == personalKey)?.Name ?? "";
            _techniciansView.Day.Show(projection.Day, document, name, source.ExtractedAt);
            _techniciansView.Planning.Show(workload, _technicianPreferences, projection.Dispatch);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_closing && generation == _personalDayGeneration) AddLog("ESPACE PERSONNEL · actualisation des listes impossible. " + AppPaths.SafeError(error));
        }
        finally
        {
            if (ReferenceEquals(_personalDayCancellation, operation)) _personalDayCancellation = null;
            operation.Dispose();
        }
    }
}
