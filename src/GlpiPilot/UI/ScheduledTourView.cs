using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GlpiPilot.Core;
using Microsoft.Win32;

namespace GlpiPilot.UI;

/// <summary>Local dated itineraries with a reviewable ticket basket and an explicit saved draft.</summary>
public sealed partial class ScheduledTourView : UserControl, IDisposable
{
    private readonly string _path;
    private readonly ComboBox _saved = new() { MinWidth = 220, MaxWidth = 380, DisplayMemberPath = "Name", SelectedValuePath = "Id", Margin = new Thickness(0, 0, 10, 6) };
    private readonly TextBox _name = new() { MinWidth = 240, MaxWidth = 440, MaxLength = 120 };
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today, Width = 150 };
    private readonly TextBox _startTime = new() { Text = "08:30", Width = 85, MaxLength = 5 }, _endTime = new() { Text = "17:30", Width = 85, MaxLength = 5 }, _returnMinutes = new() { Text = "0", Width = 90, MaxLength = 4 };
    private readonly TextBox _startLabel = new() { Width = 220, MaxLength = 120 }, _startLatitude = new() { Width = 100, MaxLength = 30 }, _startLongitude = new() { Width = 100, MaxLength = 30 };
    private readonly TextBox _endLabel = new() { Width = 220, MaxLength = 120 }, _endLatitude = new() { Width = 100, MaxLength = 30 }, _endLongitude = new() { Width = 100, MaxLength = 30 };
    private readonly ComboBox _startPlace = new() { Width = 210, DisplayMemberPath = "Label", SelectedValuePath = "Key" }, _endPlace = new() { Width = 210, DisplayMemberPath = "Label", SelectedValuePath = "Key" };
    private readonly TextBox _notes = new() { MaxLength = 4000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 100 };
    private readonly TextBlock _status = Label("Sélectionnez des tickets, puis préparez une tournée datée.", 12, "MutedBrush");
    private readonly TextBlock _scheduleSummary = Label("Les durées de trajet sont des estimations saisies. Le calcul tient compte de vos fenêtres et de l’ordre des étapes.", 12, "MutedBrush");
    private readonly TextBlock _compareSummary = Label("Copiez une tournée pour comparer différentes répartitions, dates ou ordres de passage.", 12, "MutedBrush");
    private readonly TextBlock _sourceNote = Label("", 12, "MutedBrush");
    private readonly DataGrid _basket = Table(), _stops = Table(true), _reconciliation = Table(true), _loads = Table(true);
    private readonly StackPanel _stopEditor = new();
    private readonly ComboBox _stopTechnician = new() { MinWidth = 180, MaxWidth = 300, DisplayMemberPath = "Name", SelectedValuePath = "Key" };
    private readonly ListBox _distributionTechnicians = new() { SelectionMode = SelectionMode.Multiple, DisplayMemberPath = "Name", MaxHeight = 160, MinHeight = 70 };
    private readonly ComboBox _comparison = new() { MinWidth = 250, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly CheckBox _removeCompleted = new() { Content = "Retirer les tickets résolus ou clos lors de l’actualisation", Margin = new Thickness(0, 6, 0, 10) };
    private readonly List<Button> _buttons = new();
    private readonly TabControl _tabs = new();
    private Button _save = null!, _calendar = null!, _briefing = null!;
    private ScheduledTourStore _store = new();
    private ScheduledTour? _draft;
    private TourSchedule? _schedule;
    private TourPlanningContext? _context;
    private CancellationTokenSource? _operation;
    private Task _work = Task.CompletedTask;
    private long _generation;
    private bool _loading, _externalBusy, _working, _disposed, _storeBroken, _dirty;
    public Task WhenIdle => _work;
    public ScheduledTour? Draft => _draft?.Copy();
    public event Action<string>? Log;
    public event Action? SavedToursChanged;

    public ScheduledTourView(string path)
    {
        _path = path; HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new WrapPanel(); toolbar.Children.Add(_saved);
        toolbar.Children.Add(Command("Nouvelle tournée", () => { SaveBeforeSwitch(); _draft = null; _schedule = null; _name.Text = "Tournée du " + DateTime.Today.ToString("dd/MM"); _date.SelectedDate = DateTime.Today; _stops.ItemsSource = null; _reconciliation.ItemsSource = null; _dirty = false; _saved.SelectedIndex = -1; _tabs.SelectedIndex = 0; UpdateEnabled(); }));
        _save = Command("Enregistrer la tournée", SaveDraft); _save.SetResourceReference(Button.StyleProperty, "PrimaryButton"); toolbar.Children.Add(_save);
        toolbar.Children.Add(Command("Créer un scénario", () => { var draft = ReadDraft(); var name = draft.Name.Length > 105 ? draft.Name[..105] : draft.Name; LoadTour(ScheduledTourService.Duplicate(draft, name + " · scénario"), true); }));
        toolbar.Children.Add(Command("Retirer du registre", () =>
        {
            if (_draft == null || _storeBroken) return; var draft = ReadDraft(); var updated = _store.Without(draft.Id); updated.Save(_path); _store = updated; _dirty = false; RefreshSavedChoices(); SavedToursChanged?.Invoke();
            Status("Tournée retirée du registre. Le brouillon reste affiché et peut être enregistré à nouveau.");
        }));
        root.Children.Add(toolbar); Grid.SetRow(_tabs, 1); root.Children.Add(_tabs); _status.Margin = new Thickness(0, 6, 0, 0); Grid.SetRow(_status, 2); root.Children.Add(_status);
        BuildBasket(); BuildSchedule(); BuildReconciliation(); BuildScenarios(); BuildTerrainServices(); Content = root;
        try { _store = ScheduledTourStore.Load(_path); RefreshSavedChoices(); } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _storeBroken = true; Status("Les tournées mémorisées ne peuvent pas être lues : " + error.Message + " Le fichier existant est conservé.", true); }
        _saved.SelectionChanged += (_, _) =>
        {
            if (_loading || _working || _saved.SelectedItem is not ScheduledTour selected || selected.Id == _draft?.Id) return;
            Guard(() => { SaveBeforeSwitch(); LoadTour(selected); });
        };
        foreach (var text in new[] { _name, _startTime, _endTime, _returnMinutes, _startLabel, _startLatitude, _startLongitude, _endLabel, _endLatitude, _endLongitude, _notes }) text.TextChanged += (_, _) => Changed();
        _date.SelectedDateChanged += (_, _) => Changed();
        _startPlace.SelectionChanged += (_, _) => SelectPoint(true); _endPlace.SelectionChanged += (_, _) => SelectPoint(false);
        _stops.SelectionChanged += (_, _) =>
        {
            var loading = _loading; _loading = true; try { _stopEditor.DataContext = (_stops.SelectedItem as StopDisplay)?.Stop; } finally { _loading = loading; }
        };
        UpdateEnabled();
    }

    private void BuildBasket()
    {
        var grid = Page(); var header = new StackPanel(); header.Children.Add(Label("Panier d’intervention", 18, "TextBrush", FontWeights.SemiBold)); header.Children.Add(_sourceNote);
        header.Children.Add(Label("Cochez les tickets qui demandent une intervention. Chaque ID doit être unique ; une ville inférée ne constitue pas à elle seule une demande de déplacement. Les tickets sans lieu restent dans le panier à localiser.", 12, "MutedBrush"));
        var commands = new WrapPanel { Margin = new Thickness(0, 8, 0, 3) };
        commands.Children.Add(Command("Tout sélectionner", () => { foreach (var row in _basket.Items.OfType<BasketRow>()) row.Selected = true; Status($"{_basket.Items.Count:N0} tickets sélectionnés ; la préparation est limitée à 2 000 tickets par tournée."); }));
        commands.Children.Add(Command("Vider la sélection", () => { foreach (var row in _basket.Items.OfType<BasketRow>()) row.Selected = false; }));
        commands.Children.Add(Command("Préparer les tickets cochés", CreateFromBasket)); header.Children.Add(commands); grid.Children.Add(header);
        _basket.Columns.Add(new DataGridCheckBoxColumn { Header = "Choisir", Binding = new Binding(nameof(BasketRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 65 });
        Column(_basket, "ID", "Id", 90); Column(_basket, "Titre", "Title", 360); Column(_basket, "Lieu", "Place", 230); Column(_basket, "Statut", "Status", 160); Column(_basket, "Priorité", "Priority", 110);
        Grid.SetRow(_basket, 1); grid.Children.Add(_basket); _tabs.Items.Add(new TabItem { Header = "Panier", Content = grid });
    }
    private void BuildSchedule()
    {
        var page = Page(true); var header = new StackPanel(); var fields = new WrapPanel(); fields.Children.Add(Field("Nom", _name)); fields.Children.Add(Field("Date", _date)); fields.Children.Add(Field("Départ", _startTime)); fields.Children.Add(Field("Fin de journée", _endTime)); fields.Children.Add(Field("Retour · minutes", _returnMinutes)); header.Children.Add(fields);
        var points = new StackPanel(); points.Children.Add(PointEditor("Point de départ", _startPlace, _startLabel, _startLatitude, _startLongitude)); points.Children.Add(PointEditor("Point de retour", _endPlace, _endLabel, _endLatitude, _endLongitude)); points.Children.Add(Field("Consignes générales", _notes)); points.Children.Add(BuildLifecycle());
        header.Children.Add(new Expander { Header = "Départ, retour et consignes", Content = points, Margin = new Thickness(0, 4, 0, 5) });
        var commands = new WrapPanel(); commands.Children.Add(Command("Calculer le planning", QueueCalculation)); commands.Children.Add(Command("↑ Monter", () => Move(-1))); commands.Children.Add(Command("↓ Descendre", () => Move(1)));
        commands.Children.Add(Command("Proximité indicative", () => { if (_context == null) return; LoadTour(ScheduledTourService.OrderByProximity(ReadDraft(), _context), true); QueueCalculation(); }));
        _calendar = Command("Exporter calendrier ICS…", () => ExportSchedule(true)); _briefing = Command("Briefing imprimable…", () => ExportSchedule(false)); commands.Children.Add(_calendar); commands.Children.Add(_briefing); header.Children.Add(commands); header.Children.Add(_scheduleSummary); page.Children.Add(header);
        Column(_stops, "N°", "Number", 50); Column(_stops, "Lieu", "Place", 240); Column(_stops, "Tickets", "Tickets", 70); Column(_stops, "Technicien", "Technician", 170); Column(_stops, "Date", "Date", 110); Column(_stops, "Début", "Start", 90); Column(_stops, "Fin", "Finish", 90); Column(_stops, "Verrou", "Locked", 75); Column(_stops, "Signal", "Warning", 270);
        Grid.SetRow(_stops, 1); page.Children.Add(_stops);
        var values = new WrapPanel(); _stopTechnician.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(ScheduledTourStop.TechnicianKey)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); _stopTechnician.SelectionChanged += (_, _) => Changed();
        values.Children.Add(Field("Technicien de l’étape", _stopTechnician));
        foreach (var (label, property, width) in new[] { ("Intervention · min", nameof(ScheduledTourStop.DurationMinutes), 95d), ("Trajet avant · min", nameof(ScheduledTourStop.TravelMinutes), 95d), ("Date · AAAA-MM-JJ", nameof(ScheduledTourStop.Date), 115d), ("Au plus tôt", nameof(ScheduledTourStop.Earliest), 85d), ("Fin au plus tard", nameof(ScheduledTourStop.Latest), 85d) })
        {
            var input = new TextBox { Width = width, MaxLength = property == nameof(ScheduledTourStop.Date) ? 10 : 5 }; input.SetBinding(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true }); input.TextChanged += (_, _) => Changed(); values.Children.Add(Field(label, input));
        }
        var locked = new CheckBox { Content = "Verrouiller", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) }; locked.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(ScheduledTourStop.Locked)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); locked.Checked += (_, _) => Changed(); locked.Unchecked += (_, _) => Changed(); values.Children.Add(locked);
        _stopEditor.Children.Add(values); var note = new TextBox { MaxLength = 2000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 64 }; note.SetBinding(TextBox.TextProperty, new Binding(nameof(ScheduledTourStop.Notes)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); note.TextChanged += (_, _) => Changed(); _stopEditor.Children.Add(Field("Consignes de l’étape sélectionnée", note));
        _stopEditor.Children.Add(BuildFieldDetails());
        Grid.SetRow(_stopEditor, 2); page.Children.Add(_stopEditor); _tabs.Items.Add(new TabItem { Header = "Planning daté", Content = page });
    }
    private void BuildReconciliation()
    {
        var page = Page(); var header = new StackPanel(); header.Children.Add(Label("Vérifier le nouveau relevé", 18, "TextBrush", FontWeights.SemiBold));
        header.Children.Add(Label("Les rapprochements utilisent l’installation GLPI et l’ID unique. Les absences restent distinctes des tickets clos. La reconstruction conserve les réglages des étapes restantes ; les étapes verrouillées doivent être vérifiées et déverrouillées si leurs tickets changent de lieu.", 12, "MutedBrush")); header.Children.Add(_removeCompleted);
        var actions = new WrapPanel(); actions.Children.Add(Command("Actualiser le rapprochement", QueueCalculation)); actions.Children.Add(Command("Actualiser les étapes déverrouillées", () => { if (_context == null) return; LoadTour(ScheduledTourService.RebuildUnlocked(ReadDraft(), _context, _removeCompleted.IsChecked == true), true); QueueCalculation(); })); header.Children.Add(actions); page.Children.Add(header);
        Column(_reconciliation, "ID", "TicketId", 100); Column(_reconciliation, "État", "State", 190); Column(_reconciliation, "Détail", "Detail", 560); Grid.SetRow(_reconciliation, 1); page.Children.Add(_reconciliation);
        _tabs.Items.Add(new TabItem { Header = "Rapprocher", Content = page });
    }
    private void BuildScenarios()
    {
        var page = Page(); var header = new StackPanel(); header.Children.Add(Label("Répartir et comparer", 18, "TextBrush", FontWeights.SemiBold));
        header.Children.Add(Label("La répartition équilibre les minutes d’intervention et les trajets saisis entre les techniciens choisis. Elle conserve les étapes verrouillées. C’est une proposition de planning local ; compétences, congés et routes ne sont pas supposés.", 12, "MutedBrush")); header.Children.Add(_distributionTechnicians);
        var span = new WrapPanel(); span.Children.Add(Field("Nombre de jours · 1 à 14", _distributionDays)); span.Children.Add(Command("Répartir selon disponibilités et compétences", AllocateAcrossDays)); header.Children.Add(span);
        var actions = new WrapPanel { Margin = new Thickness(0, 7, 0, 5) }; actions.Children.Add(Command("Répartir entre les techniciens choisis", () => { var keys = _distributionTechnicians.SelectedItems.OfType<TourTechnicianChoice>().Select(item => item.Key).ToArray(); LoadTour(ScheduledTourService.Distribute(ReadDraft(), keys), true); QueueCalculation(); }));
        actions.Children.Add(_comparison); actions.Children.Add(Command("Comparer au scénario enregistré", CompareScenario)); header.Children.Add(actions); header.Children.Add(_compareSummary); page.Children.Add(header);
        Column(_loads, "Date", "Date", 110); Column(_loads, "Technicien", "Technician", 190); Column(_loads, "Étapes", "Stops", 75); Column(_loads, "Tickets", "Tickets", 75); Column(_loads, "Intervention · min", "InterventionMinutes", 140); Column(_loads, "Trajets · min", "TravelMinutes", 110); Column(_loads, "Attente · min", "WaitMinutes", 110); Column(_loads, "Retour estimé", "Finish", 165, "dd/MM HH:mm"); Column(_loads, "Dépassement", "OutsideDay", 115);
        Grid.SetRow(_loads, 1); page.Children.Add(_loads); _tabs.Items.Add(new TabItem { Header = "Répartition et scénarios", Content = page });
    }

    public void Show(TourPlan plan)
    {
        if (_disposed) return;
        var sameSource = _context != null && CityOverrideStore.SourceKey(_context.Source.SourceUrl) == CityOverrideStore.SourceKey(plan.Context.Source.SourceUrl);
        var selected = sameSource ? _basket.Items.OfType<BasketRow>().Where(row => row.Selected).Select(row => row.Id).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        _context = plan.Context; var context = _context; var generation = BeginOperation(); var token = _operation!.Token;
        _work = PopulateAsync(plan, context, selected, generation, token);
    }
    private async Task PopulateAsync(TourPlan plan, TourPlanningContext context, HashSet<string> selected, long generation, CancellationToken ct)
    {
        try
        {
            var rows = await Task.Run(() => plan.SelectedRows.Select(index =>
            {
                ct.ThrowIfCancellationRequested(); var row = context.Rows[index]; var city = row.CityId != null ? context.Cities.GetValueOrDefault(row.CityId)?.Name ?? "" : "";
                var site = row.SiteId != null ? context.Sites.GetValueOrDefault(row.SiteId)?.Name ?? "" : ""; var id = ScheduledTourService.Cell(context, index, "id").Trim();
                return new BasketRow { Index = index, Id = id, Title = ScheduledTourService.Cell(context, index, "title"), Status = ScheduledTourService.Cell(context, index, "status"), Priority = ScheduledTourService.Cell(context, index, "priority"),
                    Place = site.Length > 0 ? site + " · " + city : city.Length > 0 ? city : "À localiser", Selected = selected.Contains(id) };
            }).ToArray(), ct);
            if (!Current(generation, ct)) return;
            _basket.ItemsSource = rows; _sourceNote.Text = plan.ScopeLabel + " · " + plan.StatusLabel + $" · {rows.Length:N0} tickets proposés. Les paniers et tournées restent locaux au poste.";
            _loading = true;
            try
            {
                var points = ScheduledTourService.PlaceChoices(context).Select(choice => new PlaceChoice(choice.Key, choice.Label)).Prepend(new("", "Point libre / non renseigné")).ToArray();
                _startPlace.ItemsSource = points; _endPlace.ItemsSource = points; _stopTechnician.ItemsSource = context.Technicians.Prepend(new TourTechnicianChoice("", "Affectation de la tournée", 0)).ToArray();
                _distributionTechnicians.ItemsSource = context.Technicians;
            }
            finally { _loading = false; }
            if (_draft != null)
            {
                var draft = ReadDraft(); var schedule = await Task.Run(() => ScheduledTourService.Calculate(draft, context, ct), ct);
                if (Current(generation, ct)) PublishSchedule(schedule);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Current(generation, ct)) Status("Préparation des tournées datées : " + error.Message, true); }
        finally { FinishOperation(generation); }
    }
    private void CreateFromBasket()
    {
        if (_context == null) return;
        if (!_basket.CommitEdit(DataGridEditingUnit.Cell, true) || !_basket.CommitEdit(DataGridEditingUnit.Row, true)) throw new InvalidDataException("Terminez la sélection du panier.");
        var selected = _basket.Items.OfType<BasketRow>().Where(row => row.Selected).Select(row => row.Index).ToArray();
        SaveBeforeSwitch(); var date = DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today); var name = string.IsNullOrWhiteSpace(_name.Text) ? "Tournée du " + date.ToString("dd/MM/yyyy") : _name.Text.Trim();
        LoadTour(ScheduledTourService.Create(_context, selected, name, date), true); _tabs.SelectedIndex = 1; QueueCalculation();
    }
    private void LoadTour(ScheduledTour tour, bool changed = false, bool preserveState = false)
    {
        if (changed && !preserveState) tour = ScheduledTourService.PrepareEdit(tour, "Modification du planning après validation");
        tour.Validate(); _loading = true;
        try
        {
            _draft = tour.Copy(); _schedule = null; _name.Text = tour.Name; _date.SelectedDate = DateTime.ParseExact(tour.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture); _startTime.Text = tour.StartTime; _endTime.Text = tour.EndTime; _returnMinutes.Text = tour.ReturnMinutes.ToString(CultureInfo.InvariantCulture);
            _startLabel.Text = tour.Start.Label; _startLatitude.Text = Number(tour.Start.Latitude); _startLongitude.Text = Number(tour.Start.Longitude);
            _endLabel.Text = tour.End.Label; _endLatitude.Text = Number(tour.End.Latitude); _endLongitude.Text = Number(tour.End.Longitude); _notes.Text = tour.Notes; _stateLabel.Text = "État : " + ScheduledTourService.StateLabel(tour.State); _stateHistory.ItemsSource = tour.StateHistory; _weatherGrid.ItemsSource = tour.Weather;
            _saved.SelectedValue = tour.Id; PopulateStops(); _reconciliation.ItemsSource = null; _loads.ItemsSource = null; _dirty = changed;
        }
        finally { _loading = false; }
        Status(changed ? "Brouillon modifié. Calculez le planning puis enregistrez la tournée." : "Tournée chargée. Le rapprochement sera recalculé sur le relevé courant."); UpdateEnabled();
    }
    private ScheduledTour ReadDraft()
    {
        if (_draft == null) throw new InvalidDataException("Préparez les tickets du panier ou chargez une tournée enregistrée.");
        if (HasErrors(_stopEditor)) throw new InvalidDataException("Corrigez les champs signalés dans l’étape sélectionnée.");
        if (_date.SelectedDate is not DateTime date) throw new InvalidDataException("Renseignez une date de tournée valide.");
        if (!int.TryParse(_returnMinutes.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var back)) throw new InvalidDataException("La durée du retour doit être un entier en minutes.");
        var copy = _draft.Copy(); copy.Name = _name.Text.Trim(); copy.Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); copy.StartTime = _startTime.Text.Trim(); copy.EndTime = _endTime.Text.Trim(); copy.ReturnMinutes = back; copy.Notes = _notes.Text;
        copy.Start = new() { Label = _startLabel.Text.Trim(), Latitude = Coordinate(_startLatitude.Text), Longitude = Coordinate(_startLongitude.Text) };
        copy.End = new() { Label = _endLabel.Text.Trim(), Latitude = Coordinate(_endLatitude.Text), Longitude = Coordinate(_endLongitude.Text) }; copy.Validate(); return copy;
    }
    private void SaveBeforeSwitch() { if (_dirty && _draft != null) SaveDraft(); }
    private void SaveDraft()
    {
        if (_storeBroken) throw new InvalidDataException("Le registre existant est illisible ; il est conservé. Corrigez ce fichier avant d’enregistrer une nouvelle tournée.");
        var draft = ReadDraft(); var store = _store.With(draft); store.Save(_path); _store = store; _draft = draft; _dirty = false;
        var loading = _loading; _loading = true; try { PopulateStops(); } finally { _loading = loading; }
        RefreshSavedChoices(); SavedToursChanged?.Invoke(); Status("Tournée enregistrée localement avec ses tickets, lieux, horaires et consignes."); Log?.Invoke($"TOURNÉE DATÉE · {draft.Tickets.Count} tickets et {draft.Stops.Count} étapes mémorisés localement.");
    }
    private void RefreshSavedChoices()
    {
        var loading = _loading; _loading = true;
        try { var list = _store.Tours.OrderByDescending(tour => tour.Date, StringComparer.Ordinal).ThenBy(tour => tour.Name).ToArray(); _saved.ItemsSource = list; _comparison.ItemsSource = list; _saved.SelectedValue = _draft?.Id; }
        finally { _loading = loading; }
    }
    private void QueueCalculation()
    {
        if (_context == null) return; var draft = ReadDraft(); var context = _context; var generation = BeginOperation(); var ct = _operation!.Token;
        _work = CalculateAsync(draft, context, generation, ct);
    }
    private async Task CalculateAsync(ScheduledTour draft, TourPlanningContext context, long generation, CancellationToken ct)
    {
        try { var result = await Task.Run(() => { RefreshDeclaredAvailability(draft); return ScheduledTourService.Calculate(draft, context, ct); }, ct); if (Current(generation, ct)) { _draft = draft; PublishSchedule(result); } }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Current(generation, ct)) Status("Le planning ne peut pas être calculé : " + error.Message, true); }
        finally { FinishOperation(generation); }
    }
    private void PublishSchedule(TourSchedule schedule)
    {
        _schedule = schedule; var loading = _loading; _loading = true;
        try { PopulateStops(); _reconciliation.ItemsSource = schedule.Reconciliation; _loads.ItemsSource = schedule.Technicians; }
        finally { _loading = loading; }
        _scheduleSummary.Text = $"{schedule.Visits.Count} étapes · {schedule.Tour.Tickets.Count} tickets · {schedule.Technicians.Count} parcours · fuseau {schedule.Tour.TimeZoneId}. " + string.Join(" ", schedule.Warnings);
        Status("Planning recalculé. Vérifiez les fenêtres et le rapprochement avant export."); UpdateEnabled();
    }
    private void PopulateStops()
    {
        var id = (_stops.SelectedItem as StopDisplay)?.Stop.Id;
        var rows = _draft?.Stops.Select((stop, index) =>
        {
            var visit = _schedule?.Visits.FirstOrDefault(item => item.Stop.Id == stop.Id);
            var place = _context == null ? stop.PlaceKey : ScheduledTourService.PlaceName(_context, stop);
            var technician = _context?.Technicians.FirstOrDefault(item => item.Key == stop.TechnicianKey)?.Name ?? (stop.TechnicianKey.Length > 0 ? stop.TechnicianKey : "À attribuer");
            return new StopDisplay(index + 1, stop, place, technician, visit?.Start.ToString("HH:mm") ?? "—", visit?.Finish.ToString("HH:mm") ?? "—", visit?.Warning ?? "À calculer", stop.Date.Length == 0 ? _draft.Date : stop.Date);
        }).ToArray(); _stops.ItemsSource = rows; _stops.SelectedItem = rows?.FirstOrDefault(row => row.Stop.Id == id) ?? rows?.FirstOrDefault();
    }
    private void Move(int direction) { if (_stops.SelectedItem is StopDisplay row) { LoadTour(ScheduledTourService.Move(ReadDraft(), row.Stop.Id, direction), true); QueueCalculation(); } }
    private void CompareScenario()
    {
        if (_context == null || _comparison.SelectedItem is not ScheduledTour comparison) throw new InvalidDataException("Choisissez un scénario enregistré à comparer.");
        var draft = ReadDraft(); var current = ScheduledTourService.CompareFacts(draft, _context); var other = ScheduledTourService.CompareFacts(comparison, _context);
        string Summary(TourScenarioComparison facts) => $"{facts.Name} : {facts.Tickets} tickets · {facts.Stops} étapes · {facts.InterventionMinutes} min d’intervention · {facts.TravelMinutes} min de trajet · {facts.WaitingMinutes} min d’attente · {facts.MissedWindows} fenêtres dépassées · {facts.ClosedSites} sites fermés · {facts.MissedDueDates} étapes après échéance · {facts.UnknownDueDates} tickets sans échéance · routes {(facts.RoadKm.HasValue ? facts.RoadKm.Value.ToString("N1") + " km" : "incomplètes ou non calculées")}.";
        var sameBasket = draft.SourceKey == comparison.SourceKey && draft.Tickets.Select(ticket => ticket.TicketId).ToHashSet(StringComparer.Ordinal).SetEquals(comparison.Tickets.Select(ticket => ticket.TicketId));
        _compareSummary.Text = Summary(current) + "\n" + Summary(other) + "\n" + (sameBasket ? "Même panier : arbitrez les échéances, les fenêtres et les kilomètres réellement disponibles." : "Paniers différents : ces volumes ne mesurent pas le même travail.");
        _loads.ItemsSource = ScheduledTourService.Calculate(draft, _context).Technicians;
    }
    private void ExportSchedule(bool calendar)
    {
        if (_schedule == null || _context == null) throw new InvalidDataException("Calculez le planning après la dernière modification.");
        var content = calendar ? ScheduledTourService.ToCalendar(_schedule) : ScheduledTourService.ToBriefingHtml(_schedule, _context);
        var dialog = new SaveFileDialog { Title = calendar ? "Exporter les rendez-vous de la tournée" : "Enregistrer le briefing imprimable", Filter = calendar ? "Calendrier ICS (*.ics)|*.ics" : "Feuille de route HTML (*.html)|*.html", DefaultExt = calendar ? ".ics" : ".html", FileName = "GLPI-Tournee-" + _schedule.Tour.Date + (calendar ? ".ics" : ".html") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return; File.WriteAllText(dialog.FileName, content, new UTF8Encoding(false)); Status(calendar ? "Calendrier enregistré ; importez-le dans votre agenda." : "Briefing enregistré. Ouvrez-le pour imprimer ou enregistrer en PDF.");
    }
    private void SelectPoint(bool start)
    {
        if (_loading || _context == null || (start ? _startPlace : _endPlace).SelectedItem is not PlaceChoice choice || choice.Key.Length == 0) return;
        var point = ScheduledTourService.PointFor(_context, choice.Key); (start ? _startLabel : _endLabel).Text = point.Label; (start ? _startLatitude : _endLatitude).Text = Number(point.Latitude); (start ? _startLongitude : _endLongitude).Text = Number(point.Longitude); Changed();
    }
    private void Changed()
    {
        if (_loading || _draft == null) return;
        if (_draft.State != "draft") { _draft.StateHistory.Add(new(_draft.State, "draft", DateTimeOffset.Now, "Modification du planning après validation")); _draft.StateHistory = _draft.StateHistory.TakeLast(100).ToList(); _draft.State = "draft"; _stateLabel.Text = "État : Brouillon"; }
        _dirty = true; _schedule = null; _scheduleSummary.Text = "Modifications en attente de calcul. Les exports seront disponibles après recalcul du planning."; UpdateEnabled();
    }
    public void SetBusy(bool busy) { _externalBusy = busy; UpdateEnabled(); }
    public void ShowEmpty()
    {
        _operation?.Cancel(); ++_generation; _context = null; _working = false; _basket.ItemsSource = null; _schedule = null; _reconciliation.ItemsSource = null; _loads.ItemsSource = null;
        Status("Extrayez un relevé pour préparer ou rapprocher les tournées mémorisées."); UpdateEnabled();
    }
    private long BeginOperation() { _operation?.Cancel(); _operation?.Dispose(); _operation = new(); _working = true; _schedule = null; var generation = ++_generation; UpdateEnabled(); return generation; }
    private bool Current(long generation, CancellationToken ct) => !_disposed && !ct.IsCancellationRequested && generation == _generation;
    private void FinishOperation(long generation) { if (generation == _generation && !_disposed) { _working = false; UpdateEnabled(); } }
    private void UpdateEnabled()
    {
        var enabled = !_externalBusy && !_working && !_disposed && _context != null;
        foreach (var button in _buttons) button.IsEnabled = enabled; _saved.IsEnabled = enabled; _basket.IsEnabled = enabled; _stopEditor.IsEnabled = enabled && _draft != null;
        _stops.IsEnabled = enabled; _date.IsEnabled = enabled; _startPlace.IsEnabled = _endPlace.IsEnabled = enabled; _distributionTechnicians.IsEnabled = _comparison.IsEnabled = enabled;
        foreach (var text in new[] { _name, _startTime, _endTime, _returnMinutes, _startLabel, _startLatitude, _startLongitude, _endLabel, _endLatitude, _endLongitude, _notes }) text.IsReadOnly = !enabled;
        if (_save != null) _save.IsEnabled = enabled && _draft != null && !_storeBroken;
        if (_calendar != null) _calendar.IsEnabled = enabled && _schedule?.CanExportCalendar == true;
        if (_briefing != null) _briefing.IsEnabled = enabled && _schedule != null;
        _allowCoordinates.IsEnabled = enabled; _routingEndpoint.IsReadOnly = _weatherEndpoint.IsReadOnly = _routingProfile.IsReadOnly = !enabled; _distributionDays.IsReadOnly = !enabled; _stateReason.IsReadOnly = !enabled; _terrainCancel.IsEnabled = _working && !_disposed;
    }
    private void Status(string text, bool warning = false) { _status.Text = text; _status.SetResourceReference(TextBlock.ForegroundProperty, warning ? "WarningBrush" : "MutedBrush"); }
    private void Guard(Action action) { try { action(); } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { Status(error.Message, true); } }
    private Button Command(string label, Action action) { var button = new Button { Content = label, Padding = new Thickness(11, 7, 11, 7), Margin = new Thickness(0, 0, 8, 5), FontSize = 12 }; button.Click += (_, _) => { if (!_externalBusy && !_working) Guard(action); }; _buttons.Add(button); return button; }
    private static Grid Page(bool footer = false) { var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); if (footer) grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); return grid; }
    private static DataGrid Table(bool readOnly = false) => new() { AutoGenerateColumns = false, IsReadOnly = readOnly, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, EnableRowVirtualization = true, EnableColumnVirtualization = true, RowHeight = 34, MinHeight = 0, Margin = new Thickness(0, 6, 0, 5) };
    private static void Column(DataGrid grid, string title, string path, double width, string? format = null) => grid.Columns.Add(new DataGridTextColumn { Header = title, Width = width, IsReadOnly = true, Binding = new Binding(path) { StringFormat = format, Mode = BindingMode.OneWay, TargetNullValue = "—" } });
    private static TextBlock Label(string text, double size, string brush, FontWeight? weight = null) { var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 4) }; block.SetResourceReference(TextBlock.ForegroundProperty, brush); return block; }
    private static FrameworkElement Field(string label, FrameworkElement content) { var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 5) }; panel.Children.Add(Label(label, 11, "MutedBrush")); panel.Children.Add(content); return panel; }
    private static FrameworkElement PointEditor(string label, ComboBox choices, TextBox name, TextBox latitude, TextBox longitude) { var panel = new WrapPanel(); panel.Children.Add(Field(label + " · référence", choices)); panel.Children.Add(Field("Libellé libre", name)); panel.Children.Add(Field("Latitude", latitude)); panel.Children.Add(Field("Longitude", longitude)); return panel; }
    private static string Number(double? value) => value?.ToString("G", CultureInfo.CurrentCulture) ?? "";
    private static double? Coordinate(string text) { if (string.IsNullOrWhiteSpace(text)) return null; if (double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) return value; throw new InvalidDataException("Les coordonnées doivent être des nombres finis, avec un point ou une virgule décimale."); }
    private static bool HasErrors(DependencyObject element) { if (Validation.GetHasError(element)) return true; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) if (HasErrors(VisualTreeHelper.GetChild(element, i))) return true; return false; }
    public void Dispose() { if (_disposed) return; _disposed = true; ++_generation; _operation?.Cancel(); _operation?.Dispose(); }
    private sealed record PlaceChoice(string Key, string Label);
    private sealed record StopDisplay(int Number, ScheduledTourStop Stop, string Place, string Technician, string Start, string Finish, string Warning, string Date) { public int Tickets => Stop.TicketIds.Count; public string Locked => Stop.Locked ? "Oui" : "Non"; }
    private sealed class BasketRow : INotifyPropertyChanged
    {
        private bool _selected; public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
        public int Index { get; init; } public string Id { get; init; } = ""; public string Title { get; init; } = ""; public string Place { get; init; } = ""; public string Status { get; init; } = ""; public string Priority { get; init; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
