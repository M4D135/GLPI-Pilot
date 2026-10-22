using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>Local tour planning. Every asynchronous result and emitted action belongs to the current report.</summary>
public sealed class TourPlanningView : UserControl, IDisposable
{
    private readonly string _orderPreferencesPath;
    private readonly ComboBox _technician = new() { MinWidth = 180, MaxWidth = 390, DisplayMemberPath = "Name", SelectedValuePath = "Key", Margin = new Thickness(8, 0, 18, 6) };
    private readonly CheckBox _allStatuses = new() { IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = Label("Préparer les tournées", 18, "TextBrush", FontWeights.SemiBold);
    private readonly TextBlock _scopeNote = Label("Extrayez un relevé et configurez vos villes pour préparer les étapes.", 13, "MutedBrush");
    private readonly TextBlock _planContext = Label("Choisissez le périmètre dans « Périmètre et qualité ».", 12, "MutedBrush");
    private readonly TextBlock _planNote = Label("", 12, "MutedBrush");
    private readonly TextBlock _status = Label("", 12, "MutedBrush");
    private readonly TextBlock _orderNote = Label("", 12, "MutedBrush");
    private readonly TextBlock _selectionTitle = Label("Sélectionnez une étape", 19, "TextBrush", FontWeights.SemiBold);
    private readonly TextBlock _selectionNote = Label("", 12, "MutedBrush");
    private readonly TextBlock _ticketNote = Label("", 12, "MutedBrush");
    private readonly DataGrid _steps = Table();
    private readonly DataGrid _tickets = Table();
    private readonly Button _settings, _export, _allRows, _alphabetical, _priorities, _dueDates, _up, _down, _proximity;
    private readonly Button _unlocated, _ambiguous, _unknownStatus, _stopRows, _stopActive, _stopHigh, _stopOverdue, _map;
    private CityEnrichmentResult? _report, _publishedReport;
    private TourPlanningContext? _context;
    private TourPlan? _ticketPlan;
    private string _ticketCityId = "", _technicianKey = "";
    private TourOrderPreferences _orders = new();
    private readonly ScheduledTourView _scheduledView;
    private CancellationTokenSource? _operation, _detailOperation;
    private Task _workTask = Task.CompletedTask, _detailTask = Task.CompletedTask;
    private long _generation, _publishedGeneration, _detailGeneration;
    private bool _busy, _calculating, _detailLoading, _updatingControls, _ordersLoaded, _disposed;

    public TourPlan? SelectedPlan { get; private set; }
    public Task WhenIdle => Task.WhenAll(_workTask, _detailTask, _scheduledView.WhenIdle);
    public event Action<int[], string>? RowsRequested;
    public event Action<ExtractionResult>? ExportRequested;
    public event Action? SettingsRequested;
    public event Action<string, string>? MapRequested;
    public event Action<string>? Log;

    public TourPlanningView(string? orderPreferencesPath = null)
    {
        _orderPreferencesPath = orderPreferencesPath ?? Path.Combine(AppPaths.DataRoot, "tour-order.json");
        _scheduledView = new ScheduledTourView(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_orderPreferencesPath))!, "scheduled-tours.json"));
        _scheduledView.Log += message => Log?.Invoke(message);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var information = new StackPanel();
        var heading = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _summary.Margin = new Thickness(0, 0, 18, 4); heading.Children.Add(_summary);
        _settings = Command("Configurer les villes", () => { if (!_busy && !_disposed) SettingsRequested?.Invoke(); });
        heading.Children.Add(_settings); information.Children.Add(heading);
        var scope = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        scope.Children.Add(Label("Périmètre", 12, "MutedBrush"));
        _technician.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        VirtualizingPanel.SetIsVirtualizing(_technician, true);
        VirtualizingPanel.SetVirtualizationMode(_technician, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_technician, true);
        AutomationProperties.SetName(_technician, "Choisir le technicien ou toute l’équipe pour la tournée");
        scope.Children.Add(_technician);
        _allStatuses.Content = Label("Inclure tous les statuts", 12, "TextBrush");
        _allStatuses.ToolTip = "Par défaut, seuls les actifs dont le statut est reconnu entrent dans le plan. Cette option inclut aussi les résolus, clos et statuts non classés.";
        scope.Children.Add(_allStatuses); information.Children.Add(scope);
        var sections = new TabControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetAutomationId(sections, "TourWorkspaceTabs");
        root.Children.Add(sections);
        var planView = new Grid();
        planView.RowDefinitions.Add(new() { Height = GridLength.Auto });
        planView.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        planView.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sections.Items.Add(new TabItem { Header = "Plan de tournée", Content = planView });
        var detailTab = new TabItem { Header = "Étape sélectionnée" }; sections.Items.Add(detailTab);
        information.Children.Add(_scopeNote);
        information.Children.Add(Label("Une étape correspond à une ville confirmée par votre référentiel. Les tickets dont le titre mentionne plusieurs villes restent à vérifier. La ville déduite du titre ne prouve pas qu’une intervention sur place est nécessaire.", 12, "MutedBrush", margin: new Thickness(0, 12, 0, 14)));
        information.Children.Add(_planNote);

        var planHeader = new StackPanel();
        _planContext.TextWrapping = TextWrapping.NoWrap;
        _planContext.TextTrimming = TextTrimming.CharacterEllipsis;
        _planContext.Margin = new Thickness(0, 0, 0, 6); planHeader.Children.Add(_planContext);
        var orders = new WrapPanel();
        _alphabetical = Command("A → Z", () => ChangeOrder(TourOrderKind.Alphabetical));
        _priorities = Command("Priorités actives", () => ChangeOrder(TourOrderKind.Priorities));
        _dueDates = Command("Échéances actives", () => ChangeOrder(TourOrderKind.DueDates));
        _up = Command("↑ Monter", () => MoveSelected(-1));
        _down = Command("↓ Descendre", () => MoveSelected(1));
        _proximity = Command("Proximité à vol d’oiseau", ProximitySelected);
        _proximity.ToolTip = "Partir de l’étape sélectionnée, puis choisir à chaque fois la ville la plus proche à vol d’oiseau. Toutes les villes doivent avoir des coordonnées. Aucun itinéraire routier ni horaire n’est calculé.";
        foreach (var button in new[] { _alphabetical, _priorities, _dueDates, _up, _down, _proximity }) orders.Children.Add(button);
        _priorities.Content = "Priorités"; _priorities.ToolTip = "Ordonner les étapes par priorités actives";
        _dueDates.Content = "Échéances"; _dueDates.ToolTip = "Ordonner les étapes par échéances actives";
        _proximity.Content = "Proximité";
        planHeader.Children.Add(orders); planView.Children.Add(planHeader);
        _orderNote.Margin = new Thickness(0, 9, 0, 8); information.Children.Add(_orderNote);

        // Column sorting is deliberately disabled: the displayed order is the exported visit order.
        AddColumn(_steps, "Étape", nameof(StepRow.Number), 72);
        AddColumn(_steps, "Ville", nameof(StepRow.CityName), 230);
        AddColumn(_steps, "Code postal", nameof(StepRow.PostalCode), 110);
        AddColumn(_steps, "Relevé", nameof(StepRow.Count), 85, "N0");
        AddColumn(_steps, "Actifs", nameof(StepRow.ActiveCount), 82, "N0");
        AddColumn(_steps, "Priorité haute", nameof(StepRow.HighPriorityCount), 125, "N0");
        AddColumn(_steps, "Échéance dépassée", nameof(StepRow.OverdueCount), 158, "N0");
        AddColumn(_steps, "1re échéance active", nameof(StepRow.FirstActiveDue), 168, "dd/MM/yyyy HH:mm");
        AddColumn(_steps, "Statuts inconnus", nameof(StepRow.UnknownStatusCount), 140, "N0");
        _steps.FrozenColumnCount = 2;
        AutomationProperties.SetName(_steps, "Étapes de la tournée dans l’ordre de visite"); Grid.SetRow(_steps, 1); planView.Children.Add(_steps);
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        _allRows = Command("Tous les tickets du plan", () => { if (CurrentPlan() is { } plan) RaiseRows(plan.SelectedRows, plan.ScopeLabel + " · " + plan.StatusLabel); });
        _unlocated = Command("Non localisés", () => { if (CurrentPlan() is { } plan) RaiseRows(plan.UnlocatedRows, "Tournée · non localisés"); });
        _ambiguous = Command("Ambigus", () => { if (CurrentPlan() is { } plan) RaiseRows(plan.AmbiguousRows, "Tournée · localisation ambiguë"); });
        _unknownStatus = Command("Statuts non classés", () => { if (CurrentPlan() is { } plan) RaiseRows(plan.UnknownStatusRows, "Tournée · statuts non classés du périmètre"); });
        _export = Command("Exporter le plan…", BeginExport);
        _export.SetResourceReference(BackgroundProperty, "ActionBrush");
        _export.SetResourceReference(ForegroundProperty, "OnActionBrush");
        _export.SetResourceReference(BorderBrushProperty, "ActionBorderBrush");
        _allRows.Content = "Tickets du plan"; _allRows.ToolTip = "Voir tous les tickets du plan";
        _export.Content = "Exporter…"; _export.ToolTip = "Exporter le plan de tournée";
        actions.Children.Add(_allRows); actions.Children.Add(_export);
        actions.Children.Add(Command("Étape sélectionnée →", () => sections.SelectedItem = detailTab));
        Grid.SetRow(actions, 2); planView.Children.Add(actions);
        var qualityActions = new WrapPanel();
        foreach (var button in new[] { _unlocated, _ambiguous, _unknownStatus }) qualityActions.Children.Add(button);
        information.Children.Add(Label("Vérifier les lignes du périmètre", 16, "TextBrush", FontWeights.SemiBold, new Thickness(0, 12, 0, 8)));
        information.Children.Add(qualityActions);
        information.Children.Add(Label("Lire l’étape sélectionnée", 16, "TextBrush", FontWeights.SemiBold, new Thickness(0, 16, 0, 8)));
        information.Children.Add(_selectionNote);
        information.Children.Add(Label("Dans Étape sélectionnée, double-cliquez sur une ligne de ticket pour la retrouver dans l’aperçu du relevé.", 12, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
        sections.Items.Add(new TabItem { Header = "Périmètre et qualité", Content = new ScrollViewer
        {
            Content = Card(information), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(0, 4, 8, 8)
        } });
        _status.Margin = new Thickness(0, 6, 0, 0); _status.Visibility = Visibility.Collapsed;
        Grid.SetRow(_status, 1); root.Children.Add(_status);

        var detail = new Grid();
        detail.RowDefinitions.Add(new() { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        detail.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var detailHeader = new StackPanel(); detailHeader.Children.Add(_selectionTitle);
        _selectionTitle.TextWrapping = TextWrapping.NoWrap;
        _selectionTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        var stopActions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        _stopRows = Command("Tickets de l’étape", () => RaiseStopRows("total"));
        _stopActive = Command("Actifs", () => RaiseStopRows("active"));
        _stopHigh = Command("Priorités hautes", () => RaiseStopRows("high"));
        _stopOverdue = Command("Échéances dépassées", () => RaiseStopRows("overdue"));
        _map = Command("Carte de la ville ↗", OpenSelectedCityMap);
        _map.ToolTip = "Ouvrir une carte uniquement après ce clic. Seuls le nom de ville et le code postal seront utilisés, jamais le contenu des tickets.";
        _stopRows.Content = "Tous"; _stopRows.ToolTip = "Voir tous les tickets de l’étape";
        _stopHigh.Content = "Priorités"; _stopHigh.ToolTip = "Voir les priorités hautes de l’étape";
        _stopOverdue.Content = "Retards"; _stopOverdue.ToolTip = "Voir les échéances dépassées de l’étape";
        _map.Content = "Carte ↗";
        foreach (var button in new[] { _stopRows, _stopActive, _stopHigh, _stopOverdue, _map }) stopActions.Children.Add(button);
        detailHeader.Children.Add(stopActions); detail.Children.Add(detailHeader);
        AddColumn(_tickets, "ID", nameof(TourTicket.Id), 90);
        AddColumn(_tickets, "Titre", nameof(TourTicket.Title), 430);
        AddColumn(_tickets, "Statut", nameof(TourTicket.Status), 160);
        AddColumn(_tickets, "Priorité", nameof(TourTicket.Priority), 120);
        AddColumn(_tickets, "Échéance", nameof(TourTicket.Due), 170);
        _tickets.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetName(_tickets, "Tickets de l’étape sélectionnée"); Grid.SetRow(_tickets, 1); detail.Children.Add(_tickets);
        _ticketNote.Margin = new Thickness(0, 8, 0, 0); _ticketNote.Visibility = Visibility.Collapsed;
        Grid.SetRow(_ticketNote, 2); detail.Children.Add(_ticketNote);
        detailTab.Content = detail;
        sections.Items.Add(new TabItem { Header = "Tournées datées", Content = _scheduledView });
        Content = root;

        // Handlers are attached only after every referenced control has been constructed.
        _technician.SelectionChanged += (_, _) => ScopeChanged();
        _allStatuses.Checked += (_, _) => ScopeChanged();
        _allStatuses.Unchecked += (_, _) => ScopeChanged();
        _steps.SelectionChanged += (_, _) => { if (!_updatingControls) QueueSelectedDetail(); };
        _tickets.MouseDoubleClick += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(_tickets, element) is DataGridRow)
                RaiseSelectedTicket();
        };
        _tickets.KeyDown += (_, args) => { if (args.Key == Key.Enter) { RaiseSelectedTicket(); args.Handled = true; } };
        UpdateEnabled();
    }

    public void Show(CityEnrichmentResult report, CitySettings citySettings, DashboardSettings dashboardSettings)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(citySettings); ArgumentNullException.ThrowIfNull(dashboardSettings);
        _operation?.Cancel(); _detailOperation?.Cancel();
        var generation = ++_generation;
        var operation = new CancellationTokenSource(); _operation = operation;
        _report = report; _calculating = true; _context = null;
        SetStatus(SelectedPlan == null ? "Préparation du relevé et des étapes…" : "Actualisation en cours. Le plan précédent reste visible jusqu’à la fin du calcul.");
        UpdateEnabled();
        _workTask = PrepareAsync(report, citySettings, dashboardSettings, generation, operation);
    }

    private async Task PrepareAsync(CityEnrichmentResult report, CitySettings citySettings, DashboardSettings dashboardSettings,
        long generation, CancellationTokenSource operation)
    {
        try
        {
            // Clone settings before the worker consumes them; the report itself is an immutable extraction in MainWindow.
            citySettings.Validate(); var cities = citySettings.Copy(); var dashboard = dashboardSettings.Copy();
            var loadPreferences = !_ordersLoaded;
            var result = await Task.Run(() =>
            {
                TourOrderPreferences? orders = null; string? errorType = null;
                if (loadPreferences)
                {
                    try { orders = TourOrderPreferences.Load(_orderPreferencesPath); }
                    catch (Exception error) { orders = new(); errorType = error.GetType().Name; }
                }
                var context = TourPlanningService.Prepare(report, cities, dashboard, ct: operation.Token);
                return (Context: context, Orders: orders, ErrorType: errorType);
            }, operation.Token);
            if (!OperationCurrent(report, generation, operation)) return;
            _context = result.Context;
            if (loadPreferences) { _orders = result.Orders ?? new(); _ordersLoaded = true; }
            if (result.ErrorType != null) Log?.Invoke("TOURNÉES · ordre local non chargé ; ordre de session utilisé (" + result.ErrorType + ").");
            SetTechnicianChoices(result.Context);
            var key = _technicianKey; var allStatuses = _allStatuses.IsChecked == true;
            var order = _orders.Get(TourOrderPreferences.ScopeKey(key, allStatuses));
            var plan = await Task.Run(() =>
            {
                var built = TourPlanningService.Create(result.Context, key, allStatuses, operation.Token);
                return order.Length > 0 ? TourPlanningService.ApplySavedOrder(built, order) : built;
            }, operation.Token);
            if (!OperationCurrent(report, generation, operation)) return;
            PublishPlan(report, plan, generation);
            if (result.ErrorType != null) SetStatus("L’ordre mémorisé n’a pas pu être chargé. Ce plan reste utilisable ; consultez le journal.", true);
            await _detailTask;
            await _scheduledView.WhenIdle;
            if (OperationCurrent(report, generation, operation))
                Log?.Invoke($"TOURNÉES · {plan.Stops.Count:N0} étapes, {plan.SelectedRows.Count:N0} lignes retenues, {plan.UnlocatedRows.Count:N0} non localisées et {plan.AmbiguousRows.Count:N0} ambiguës.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { CalculationFailed(report, generation, operation, error); }
        finally { FinishOperation(generation, operation); }
    }

    private void SetTechnicianChoices(TourPlanningContext context)
    {
        var choices = new List<TourTechnicianChoice> { new("", "Toute l’équipe", 0) };
        choices.AddRange(context.Technicians);
        if (_technicianKey.Length > 0 && choices.All(choice => choice.Key != _technicianKey))
            choices.Add(new(_technicianKey, _technicianKey + " · absent de ce relevé", 0));
        _updatingControls = true;
        try { _technician.ItemsSource = choices; _technician.SelectedValue = _technicianKey; }
        finally { _updatingControls = false; }
    }

    private void ScopeChanged()
    {
        if (_updatingControls || _disposed || _busy || _calculating || _context == null || _report == null) return;
        _technicianKey = (_technician.SelectedItem as TourTechnicianChoice)?.Key ?? "";
        var context = _context; var report = _report; var key = _technicianKey; var allStatuses = _allStatuses.IsChecked == true;
        var order = _orders.Get(TourOrderPreferences.ScopeKey(key, allStatuses));
        BeginPlanOperation(report, token =>
        {
            var plan = TourPlanningService.Create(context, key, allStatuses, token);
            return order.Length == 0 ? plan : TourPlanningService.ApplySavedOrder(plan, order);
        }, persist: false);
    }

    private void ChangeOrder(TourOrderKind order)
    {
        if (CurrentPlan() is not { } plan || _report == null) return;
        BeginPlanOperation(_report, token => TourPlanningService.Sort(plan, order, token), persist: true);
    }

    private void MoveSelected(int direction)
    {
        if (CurrentPlan() is not { } plan || _steps.SelectedItem is not StepRow row || _report == null) return;
        BeginPlanOperation(_report, _ => TourPlanningService.Move(plan, row.Stop.CityId, direction), persist: true);
    }

    private void ProximitySelected()
    {
        if (CurrentPlan() is not { } plan || _steps.SelectedItem is not StepRow row || _report == null || !plan.CanUseProximity) return;
        BeginPlanOperation(_report, token => TourPlanningService.SortByProximity(plan, row.Stop.CityId, token), persist: true);
    }

    private void BeginPlanOperation(CityEnrichmentResult report, Func<CancellationToken, TourPlan> calculate, bool persist)
    {
        _operation?.Cancel(); _detailOperation?.Cancel();
        var generation = ++_generation; var operation = new CancellationTokenSource(); _operation = operation;
        _calculating = true; SetStatus("Calcul du périmètre et de l’ordre des étapes…"); UpdateEnabled();
        _workTask = CalculatePlanAsync(report, generation, operation, calculate, persist);
    }

    private async Task CalculatePlanAsync(CityEnrichmentResult report, long generation, CancellationTokenSource operation,
        Func<CancellationToken, TourPlan> calculate, bool persist)
    {
        try
        {
            var plan = await Task.Run(() => calculate(operation.Token), operation.Token);
            if (!OperationCurrent(report, generation, operation)) return;
            PublishPlan(report, plan, generation);
            if (persist) RememberOrder(plan);
            await _detailTask;
            await _scheduledView.WhenIdle;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { CalculationFailed(report, generation, operation, error); }
        finally { FinishOperation(generation, operation); }
    }

    private void PublishPlan(CityEnrichmentResult report, TourPlan plan, long generation)
    {
        var selectedCity = (_steps.SelectedItem as StepRow)?.Stop.CityId;
        SelectedPlan = plan; _publishedReport = report; _publishedGeneration = generation; _calculating = false;
        _scheduledView.Show(plan);
        _summary.Text = $"{plan.Stops.Count:N0} étapes · {plan.SelectedRows.Count:N0} {plan.Grain} dans le plan";
        _scopeNote.Text = plan.ScopeLabel + " · " + plan.StatusLabel + $" · relevé de {plan.SourceRowCount:N0} lignes"
            + (plan.SourceComplete ? " · source complète" : " · source partielle ou non confirmée");
        _planContext.Text = plan.ScopeLabel + " · " + plan.StatusLabel + $" · {plan.Stops.Count:N0} étapes · {plan.SelectedRows.Count:N0} {plan.Grain}";
        _planContext.ToolTip = _scopeNote.Text + "\nModifier ce périmètre dans Périmètre et qualité.";
        _orderNote.Text = plan.OrderLabel + ". L’ordre est mémorisé sur ce poste par technicien et mode de statuts. "
            + (plan.CanUseProximity ? $"Trajet entre étapes : environ {plan.StraightLineDistanceKm:N1} km à vol d’oiseau, sans départ extérieur ni retour."
                : plan.Stops.Count <= 1 ? "La proximité se calcule à partir de deux étapes."
                : "Coordonnées manquantes : utilisez l’ordre manuel ou les tris. Aucun itinéraire routier ni durée de trajet n’est calculé.");
        if (plan.CanUseProximity) _orderNote.Text += " Aucune route ni durée de trajet n’est calculée.";
        _unlocated.Content = $"Non localisés · {plan.UnlocatedRows.Count:N0}";
        _ambiguous.Content = $"Ambigus · {plan.AmbiguousRows.Count:N0}";
        _unknownStatus.Content = $"Statuts non classés · {plan.UnknownStatusRows.Count:N0}";
        _planNote.Text = plan.Message + (!plan.AllStatuses ? $" {plan.ExcludedCompletedCount:N0} résolus ou clos exclus du plan."
            + (plan.UnknownStatusRows.Count > 0 ? $" {plan.UnknownStatusRows.Count:N0} statuts inconnus exclus et accessibles dans cette rubrique." : "") : "");
        ShowPlanStatus(plan);
        _updatingControls = true;
        try
        {
            var steps = plan.Stops.Select((stop, index) => new StepRow(index + 1, stop)).ToArray();
            _steps.ItemsSource = steps;
            _steps.SelectedItem = steps.FirstOrDefault(row => row.Stop.CityId == selectedCity) ?? steps.FirstOrDefault();
        }
        finally { _updatingControls = false; }
        QueueSelectedDetail(); UpdateEnabled();
    }

    private void RememberOrder(TourPlan plan)
    {
        try
        {
            _orders = _orders.With(TourOrderPreferences.ScopeKey(plan.TechnicianKey, plan.AllStatuses), plan.Stops.Select(stop => stop.CityId));
            _orders.Save(_orderPreferencesPath);
            Log?.Invoke($"TOURNÉES · ordre local enregistré pour {plan.Stops.Count:N0} étapes ; aucun contenu de ticket enregistré dans ces préférences.");
        }
        catch (Exception error)
        {
            SetStatus("L’ordre reste appliqué pour cette session mais n’a pas pu être enregistré sur le poste. Consultez le journal.", true);
            Log?.Invoke("TOURNÉES · ordre local non enregistré (" + error.GetType().Name + ").");
        }
    }

    private void QueueSelectedDetail()
    {
        _detailOperation?.Cancel(); var generation = ++_detailGeneration;
        if (CurrentPlan() is not { } plan || _steps.SelectedItem is not StepRow row)
        {
            _selectionTitle.Text = "Aucune étape sélectionnée"; _selectionTitle.ToolTip = null;
            _selectionNote.Text = "Les lignes non localisées et ambiguës restent accessibles dans cette rubrique.";
            _tickets.ItemsSource = null; _ticketPlan = null; _ticketCityId = ""; _detailLoading = false; SetTicketNote("");
            UpdateEnabled(); return;
        }
        var stop = row.Stop;
        _selectionTitle.Text = $"Étape {row.Number} · {stop.CityName}";
        _selectionTitle.ToolTip = _selectionTitle.Text;
        _selectionNote.Text = stop.CityName + $" · {stop.Count:N0} {plan.Grain} · {stop.UnknownStatusCount:N0} statuts inconnus. "
            + $"Priorités évaluables : {stop.PrioritySample:N0} / {stop.ActiveCount?.ToString("N0") ?? "—"} actifs ; échéances lisibles : {stop.DueSample:N0} / {stop.ActiveCount?.ToString("N0") ?? "—"} actifs."
            + " Les retards sont calendaires ; les règles de suspension du SLA ne sont pas déduites.";
        var operation = new CancellationTokenSource(); _detailOperation = operation; _detailLoading = true;
        SetTicketNote("Chargement des tickets de cette étape…"); UpdateEnabled();
        _detailTask = LoadSelectedDetailAsync(plan, stop.CityId, generation, operation);
    }

    private async Task LoadSelectedDetailAsync(TourPlan plan, string cityId, long generation, CancellationTokenSource operation)
    {
        try
        {
            var tickets = await Task.Run(() => TourPlanningService.TicketsForStop(plan, cityId, operation.Token), operation.Token);
            if (_disposed || operation.IsCancellationRequested || generation != _detailGeneration || !ReferenceEquals(plan, SelectedPlan)
                || !ReferenceEquals(_report, _publishedReport) || _publishedGeneration != _generation || (_steps.SelectedItem as StepRow)?.Stop.CityId != cityId) return;
            _tickets.ItemsSource = tickets; _ticketPlan = plan; _ticketCityId = cityId;
            SetTicketNote("");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_disposed && !operation.IsCancellationRequested && generation == _detailGeneration && ReferenceEquals(plan, SelectedPlan)
                && ReferenceEquals(_report, _publishedReport) && _publishedGeneration == _generation)
            {
                _tickets.ItemsSource = null; _ticketPlan = null;
                SetTicketNote("Les tickets de cette étape ne peuvent pas être affichés. Sélectionnez de nouveau l’étape.");
                Log?.Invoke("TOURNÉES · détail d’étape indisponible (" + error.GetType().Name + ").");
            }
        }
        finally
        {
            if (ReferenceEquals(_detailOperation, operation)) { _detailOperation = null; _detailLoading = false; }
            operation.Dispose(); if (!_disposed) UpdateEnabled();
        }
    }

    private void RaiseStopRows(string metric)
    {
        if (CurrentPlan() is not { } plan || _steps.SelectedItem is not StepRow row) return;
        var label = metric switch { "active" => "Actifs", "high" => "Priorités hautes actives", "overdue" => "Échéances dépassées", _ => "Tickets de l’étape" };
        RaiseRows(TourPlanningService.RowsForStop(plan, row.Stop.CityId, metric), "Tournée · " + row.Stop.CityName + " · " + label);
    }

    private void RaiseSelectedTicket()
    {
        if (CurrentPlan() is not { } plan || _detailLoading || !ReferenceEquals(_ticketPlan, plan)
            || (_steps.SelectedItem as StepRow)?.Stop.CityId != _ticketCityId || _tickets.SelectedItem is not TourTicket ticket) return;
        RaiseRows(new[] { ticket.SourceIndex }, "Tournée · ticket de l’étape sélectionnée");
    }

    private void RaiseRows(IEnumerable<int> rows, string label)
    {
        if (CurrentPlan() == null) return;
        var copy = rows.ToArray(); if (copy.Length > 0) RowsRequested?.Invoke(copy, label);
    }

    private void OpenSelectedCityMap()
    {
        if (CurrentPlan() == null || _steps.SelectedItem is not StepRow row) return;
        MapRequested?.Invoke(row.Stop.CityName, row.Stop.PostalCode);
    }

    private void BeginExport()
    {
        if (CurrentPlan() is not { } plan || _report == null) return;
        var operation = new CancellationTokenSource(); _operation?.Cancel(); _operation = operation;
        _calculating = true; SetStatus("Préparation de l’export du plan dans l’ordre affiché…"); UpdateEnabled();
        _workTask = ExportAsync(plan, _report, _generation, operation);
    }

    private async Task ExportAsync(TourPlan plan, CityEnrichmentResult report, long generation, CancellationTokenSource operation)
    {
        try
        {
            var data = await Task.Run(() => TourPlanningService.ToExport(plan, operation.Token), operation.Token);
            if (!OperationCurrent(report, generation, operation) || !ReferenceEquals(plan, SelectedPlan)) return;
            _calculating = false; ShowPlanStatus(plan); UpdateEnabled();
            if (CurrentPlan() != null) ExportRequested?.Invoke(data);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (OperationCurrent(report, generation, operation))
            { SetStatus("L’export du plan n’a pas pu être préparé. Consultez le journal.", true); Log?.Invoke("TOURNÉES · export indisponible (" + error.GetType().Name + ")."); }
        }
        finally { FinishOperation(generation, operation); }
    }

    private TourPlan? CurrentPlan() => !_disposed && !_busy && !_calculating && SelectedPlan != null
        && _publishedGeneration == _generation && ReferenceEquals(_publishedReport, _report) ? SelectedPlan : null;

    private bool OperationCurrent(CityEnrichmentResult report, long generation, CancellationTokenSource operation)
        => !_disposed && !operation.IsCancellationRequested && generation == _generation && ReferenceEquals(report, _report);

    private void CalculationFailed(CityEnrichmentResult report, long generation, CancellationTokenSource operation, Exception error)
    {
        if (!OperationCurrent(report, generation, operation)) return;
        SetStatus("Le nouveau plan ne peut pas être calculé. Vérifiez le référentiel des villes et les correspondances du dashboard. Le plan précédent reste affiché sans action sur ses lignes.", true);
        Log?.Invoke("TOURNÉES · calcul indisponible (" + error.GetType().Name + ").");
    }

    private void FinishOperation(long generation, CancellationTokenSource operation)
    {
        var current = ReferenceEquals(_operation, operation);
        if (current) _operation = null;
        operation.Dispose();
        if (current && !_disposed && generation == _generation) { _calculating = false; UpdateEnabled(); }
    }

    public void SetBusy(bool busy)
    {
        var wasBusy = _busy; _busy = busy; UpdateEnabled();
        if (wasBusy && !busy && CurrentPlan() is { } plan && _steps.SelectedItem is StepRow row
            && (!ReferenceEquals(_ticketPlan, plan) || _ticketCityId != row.Stop.CityId)) QueueSelectedDetail();
    }

    public void ShowEmpty(string message = "Extrayez un relevé et configurez vos villes pour préparer les tournées.")
    {
        if (_disposed) return;
        _operation?.Cancel(); _detailOperation?.Cancel(); ++_generation; ++_detailGeneration;
        _report = _publishedReport = null; _context = null; SelectedPlan = null; _ticketPlan = null;
        _scheduledView.ShowEmpty();
        _calculating = _detailLoading = false;
        _updatingControls = true;
        try { _steps.ItemsSource = null; _tickets.ItemsSource = null; _technician.ItemsSource = null; }
        finally { _updatingControls = false; }
        _summary.Text = "Préparer les tournées"; _scopeNote.Text = message; _orderNote.Text = ""; _planNote.Text = "";
        _planContext.Text = "Choisissez le périmètre dans « Périmètre et qualité »."; _planContext.ToolTip = message;
        _selectionTitle.Text = "Aucune étape sélectionnée"; _selectionTitle.ToolTip = null; _selectionNote.Text = "";
        SetTicketNote(""); SetStatus(message); UpdateEnabled();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_generation; ++_detailGeneration; _operation?.Cancel(); _detailOperation?.Cancel();
        _scheduledView.Dispose();
    }

    private void SetStatus(string message, bool warning = false)
    {
        _status.Text = message; _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _status.SetResourceReference(TextBlock.ForegroundProperty, warning ? "WarningBrush" : "MutedBrush");
    }

    private void ShowPlanStatus(TourPlan plan)
    {
        SetStatus(!plan.Available ? plan.Message
            : plan.Stops.Count == 0 ? "Aucune étape localisée. Consultez Périmètre et qualité pour vérifier le relevé."
            : !plan.SourceComplete ? "Relevé partiel ou non confirmé : le plan porte uniquement sur les lignes récupérées." : "",
            !plan.Available || !plan.SourceComplete);
    }

    private void SetTicketNote(string message)
    { _ticketNote.Text = message; _ticketNote.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }

    private void UpdateEnabled()
    {
        _scheduledView.SetBusy(_busy || _calculating || _disposed);
        var plan = CurrentPlan(); var available = plan != null;
        var controls = !_busy && !_calculating && !_disposed && _context != null;
        _technician.IsEnabled = _allStatuses.IsEnabled = controls;
        _settings.IsEnabled = !_busy && !_disposed;
        _steps.IsEnabled = available; _tickets.IsEnabled = available && !_detailLoading;
        _alphabetical.IsEnabled = _priorities.IsEnabled = _dueDates.IsEnabled = available && plan!.Stops.Count > 1;
        var selected = _steps.SelectedItem as StepRow;
        _up.IsEnabled = available && selected?.Number > 1;
        _down.IsEnabled = available && selected != null && selected.Number < plan!.Stops.Count;
        _proximity.IsEnabled = available && selected != null && plan!.CanUseProximity;
        _allRows.IsEnabled = _export.IsEnabled = available && plan!.Available && plan.SelectedRows.Count > 0;
        _unlocated.IsEnabled = available && plan!.UnlocatedRows.Count > 0;
        _ambiguous.IsEnabled = available && plan!.AmbiguousRows.Count > 0;
        _unknownStatus.IsEnabled = available && plan!.UnknownStatusRows.Count > 0;
        _stopRows.IsEnabled = _map.IsEnabled = available && selected != null;
        _stopActive.IsEnabled = available && selected?.Stop.ActiveCount > 0;
        _stopHigh.IsEnabled = available && selected?.Stop.HighPriorityCount > 0;
        _stopOverdue.IsEnabled = available && selected?.Stop.OverdueCount > 0;
    }

    private static DataGrid Table()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, MinHeight = 0, RowHeight = 36,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserSortColumns = false, CanUserReorderColumns = true, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow
        };
        VirtualizingPanel.SetIsVirtualizing(grid, true); VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(grid, true); return grid;
    }
    private static void AddColumn(DataGrid grid, string title, string path, double width, string? format = null)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = title, Binding = new Binding(path) { Mode = BindingMode.OneWay, TargetNullValue = "—", StringFormat = format },
            Width = width, MinWidth = 60, IsReadOnly = true, CanUserSort = false
        });
    }
    private static Button Command(string title, Action action)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(12, 7, 12, 7), FontSize = 12, MinHeight = 36 };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => action(); return button;
    }
    private static TextBlock Label(string text, double size, string brush, FontWeight? weight = null, Thickness? margin = null)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, brush); return label;
    }
    private static Border Card(UIElement content)
    {
        var border = new Border { Child = content, Padding = new Thickness(18), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border;
    }
    public sealed record StepRow(int Number, TourStop Stop)
    {
        public string CityName => Stop.CityName;
        public string PostalCode => Stop.PostalCode;
        public int Count => Stop.Count;
        public int? ActiveCount => Stop.ActiveCount;
        public int? HighPriorityCount => Stop.HighPriorityCount;
        public int? OverdueCount => Stop.OverdueCount;
        public DateTime? FirstActiveDue => Stop.FirstActiveDue;
        public int UnknownStatusCount => Stop.UnknownStatusCount;
    }
}
