using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>A local query editor with isolated drafts and explicitly requested, cancellable previews.</summary>
public sealed partial class QueryWorkshopView : UserControl, IDisposable
{
    private const int MaximumQueries = QueryLibrarySettings.MaximumQueries, MaximumConditions = SavedQuery.MaximumConditions;
    private readonly TextBox _search = Input("Rechercher dans mes requêtes", 250);
    private readonly ComboBox _categoryFilter = Selector("Filtrer les requêtes par catégorie");
    private readonly CheckBox _favoritesOnly = Check("Favoris seulement");
    private readonly ListBox _libraryList = new() { MinHeight = 0, DisplayMemberPath = nameof(LibraryEntry.Label), Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _libraryCount = Text("Aucune requête", 12, "MutedBrush");
    private readonly TextBlock _sourceInfo = Text("Chargez un relevé pour prévisualiser vos requêtes. Vous pouvez déjà les préparer.", 12, "MutedBrush");
    private readonly TextBlock _status = Text("", 12, "MutedBrush");
    private readonly TextBlock _editorTitle = Text("Votre première requête", 18, "TextBrush", FontWeights.SemiBold);
    private readonly TextBox _name = Input("Nom de la requête", 120);
    private readonly TextBox _description = Input("Description de la requête", 1200);
    private readonly TextBox _category = Input("Catégorie de la requête", 80);
    private readonly CheckBox _favorite = Check("Favori de la bibliothèque");
    private readonly ComboBox _baseProbe = Selector("Périmètre de départ de la requête");
    private readonly ComboBox _match = Selector("Toutes les conditions ou au moins une condition");
    private readonly ComboBox _aggregate = Selector("Calcul de la requête");
    private readonly ComboBox _aggregateField = Selector("Champ utilisé par le calcul");
    private readonly ComboBox _groupBy = Selector("Champ de regroupement facultatif");
    private readonly ComboBox _widgetScope = Selector("Dashboard de destination du widget");
    private readonly TextBlock _aggregateHint = Text("", 12, "MutedBrush");
    private readonly TextBlock _conditionCount = Text("Aucune condition supplémentaire", 12, "MutedBrush");
    private readonly TextBlock _widgetHint = Text("Enregistrez la bibliothèque avant d’ajouter un widget.", 12, "MutedBrush");
    private readonly StackPanel _editorFields = new();
    private readonly TabControl _sections = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TabItem _definitionTab = new() { Header = "Définition" }, _conditionsTab = new() { Header = "Conditions" }, _resultTab = new() { Header = "Résultat" }, _distributionTab = new() { Header = "Répartition" };
    private readonly DataGrid _conditions = EditableGrid();
    private readonly ObservableCollection<ConditionRow> _conditionRows = new();
    private readonly ObservableCollection<QueryChoice> _fields = new(), _optionalFields = new(), _bases = new();
    private readonly DataGridComboBoxColumn _conditionFieldColumn = new();
    private readonly Button _new = Command("Nouvelle"), _duplicate = Command("Dupliquer"), _delete = Command("Supprimer");
    private readonly Button _templates = Command("Ajouter les modèles manquants");
    private readonly Button _addCondition = Command("Ajouter une condition"), _removeCondition = Command("Retirer la sélection");
    private readonly Button _save = Command("Enregistrer la bibliothèque"), _restore = Command("Rétablir les réglages enregistrés");
    private readonly Button _preview = Command("Prévisualiser"), _cancelPreview = Command("Annuler le calcul");
    private readonly Button _addMetric = Command("Ajouter l’indicateur"), _addDistribution = Command("Ajouter la répartition");
    private readonly Border _confirmation = new() { Padding = new Thickness(14), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 10, 0, 10), Visibility = Visibility.Collapsed };
    private readonly TextBlock _confirmationText = Text("", 13, "TextBrush");
    private readonly Button _confirm = Command("Confirmer"), _keep = Command("Conserver");
    private QueryLibrarySettings _working = new(), _saved = new();
    private string? _selectedId;
    private bool _busy, _loading, _rebuildingLibrary, _dirty, _savePending, _disposed;
    private Action? _confirmationAction;
    private ExtractionResult? _source;
    private DashboardSettings? _sourceSettings;
    private CityEnrichmentResult? _cities;
    private CancellationTokenSource? _previewCancellation;
    private readonly HashSet<Task> _pending = new();
    private long _generation;
    private bool _previewRunning;
    private SavedQuery? SelectedQuery => _working.Queries.FirstOrDefault(query => query.Id == _selectedId);

    public event Action<QueryLibrarySettings>? SaveRequested;
    public event Action<int[], string>? RowsRequested;
    public event Action<string, string, bool, bool>? WidgetRequested;
    public Task WhenIdle => _pending.Count == 0 ? Task.CompletedTask : Task.WhenAll(_pending.ToArray());

    public QueryWorkshopView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _optionalFields.Add(new QueryChoice("", "Aucun / non utilisé"));
        foreach (var choice in QueryService.BaseProbes) _bases.Add(choice);
        EnsureFields(Array.Empty<string>());
        ConfigureAdvanced();
        _baseProbe.ItemsSource = _bases;
        _aggregateField.ItemsSource = _optionalFields; _groupBy.ItemsSource = _optionalFields;
        _match.ItemsSource = new[] { new QueryChoice("all", "Toutes les conditions · ET"), new QueryChoice("any", "Au moins une condition · OU") };
        _aggregate.ItemsSource = new[]
        {
            new QueryChoice("count", "Nombre de lignes"), new QueryChoice("percent", "Pourcentage"), new QueryChoice("distinct", "Valeurs distinctes"),
            new QueryChoice("sum", "Somme"), new QueryChoice("average", "Moyenne"), new QueryChoice("min", "Minimum"), new QueryChoice("max", "Maximum"),
            new QueryChoice("median", "Médiane"), new QueryChoice("percentile", "Percentile configurable")
        };
        _widgetScope.ItemsSource = new[] { new QueryChoice("global", "Dashboard global"), new QueryChoice("personal", "Dashboard personnel") };
        _widgetScope.SelectedValue = "global";
        _description.AcceptsReturn = true; _description.TextWrapping = TextWrapping.Wrap; _description.MinHeight = 68;
        _description.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _confirmation.SetResourceReference(Border.BackgroundProperty, "NoticeBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        var commands = new WrapPanel();
        _save.SetResourceReference(Button.StyleProperty, "PrimaryButton");
        _save.Content = "Enregistrer"; _save.ToolTip = "Enregistrer toute la bibliothèque de requêtes";
        commands.Children.Add(_save); commands.Children.Add(_preview);
        _libraryCount.Margin = new Thickness(12, 0, 0, 7);
        commands.Children.Add(_libraryCount);
        header.Children.Add(commands); root.Children.Add(header);
        AutomationProperties.SetAutomationId(_sections, "QueryWorkspaceTabs");
        _sections.Items.Add(new TabItem { Header = "Bibliothèque", Content = BuildLibrary() });
        _definitionTab.Content = BuildEditor(); _sections.Items.Add(_definitionTab);
        _conditionsTab.Content = BuildConditions(); _sections.Items.Add(_conditionsTab);
        _advancedTab.Content = BuildAdvanced(); _sections.Items.Add(_advancedTab);
        _resultTab.Content = BuildPreview(); _sections.Items.Add(_resultTab);
        _distributionTab.Content = BuildDistribution(); _sections.Items.Add(_distributionTab);
        Grid.SetRow(_sections, 1); root.Children.Add(_sections);
        _status.Margin = new Thickness(0, 8, 0, 0); _status.Visibility = Visibility.Collapsed;
        Grid.SetRow(_status, 2); root.Children.Add(_status);
        Content = root;
        ConfigureConditionColumns();
        _conditions.ItemsSource = _conditionRows;
        VirtualizingPanel.SetIsVirtualizing(_libraryList, true); VirtualizingPanel.SetVirtualizationMode(_libraryList, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_libraryList, true);
        AutomationProperties.SetName(_libraryList, "Bibliothèque de requêtes, les favoris sont marqués d’une étoile");

        // Attach all handlers after the complete form, preview and command controls exist.
        _search.TextChanged += (_, _) => RebuildLibrary();
        _categoryFilter.SelectionChanged += (_, _) => { if (!_rebuildingLibrary) RebuildLibrary(); };
        _favoritesOnly.Checked += (_, _) => RebuildLibrary(); _favoritesOnly.Unchecked += (_, _) => RebuildLibrary();
        _libraryList.SelectionChanged += (_, _) => SwitchSelectedQuery();
        foreach (var field in new[] { _name, _description, _category }) field.TextChanged += (_, _) => MarkEdited();
        _favorite.Checked += (_, _) => MarkEdited(); _favorite.Unchecked += (_, _) => MarkEdited();
        foreach (var field in new[] { _baseProbe, _match, _aggregate, _aggregateField, _groupBy }) field.SelectionChanged += (_, _) => MarkEdited();
        _conditions.SelectionChanged += (_, _) => UpdateEnabled();
        _new.Click += (_, _) => EditAction(NewQuery); _duplicate.Click += (_, _) => EditAction(DuplicateQuery);
        _templates.Click += (_, _) => EditAction(AddMissingTemplates);
        _delete.Click += (_, _) => AskDelete(); _restore.Click += (_, _) => AskRestore();
        _addCondition.Click += (_, _) => EditAction(AddCondition); _removeCondition.Click += (_, _) => EditAction(RemoveConditions);
        _save.Click += (_, _) => RequestSave();
        _confirm.Click += (_, _) => { if (_busy || _disposed) return; var action = _confirmationAction; HideConfirmation(); EditAction(() => action?.Invoke()); };
        _keep.Click += (_, _) => HideConfirmation();
        _preview.Click += (_, _) => StartPreview(); _cancelPreview.Click += (_, _) => InvalidatePreview("Calcul annulé. Votre brouillon est conservé.");
        _addMetric.Click += (_, _) => RequestWidget(false); _addDistribution.Click += (_, _) => RequestWidget(true);
        AttachPreviewHandlers(); AttachAdvancedHandlers();
        LoadEditor(); RebuildLibrary(); UpdateEnabled();
    }

    public void LoadSettings(QueryLibrarySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed) return;
        settings.Validate();
        var selected = _selectedId; var confirmingSave = _savePending;
        var previousLoading = _loading; _loading = true;
        try
        {
            _saved = settings.Copy(); _working = settings.Copy();
            _dirty = false; _savePending = false;
            _selectedId = _working.Queries.Any(query => query.Id == selected) ? selected : _working.Queries.FirstOrDefault()?.Id;
            EnsureSavedFields(); HideConfirmation(); LoadEditor();
        }
        finally { _loading = previousLoading; }
        RebuildLibrary();
        InvalidatePreview("Bibliothèque chargée. Prévisualisez la requête choisie sur le relevé actuel.");
        SetStatus(confirmingSave ? "Bibliothèque enregistrée." : "", "MutedBrush"); UpdateEnabled();
    }

    public void Show(ExtractionResult source, DashboardSettings settings, CityEnrichmentResult? cities = null)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(settings);
        _source = source; _sourceSettings = settings.Copy(); _cities = cities;
        CaptureDraft();
        var aggregateField = Selected(_aggregateField); var groupBy = Selected(_groupBy);
        var conditionFields = _conditionRows.Select(row => (Row: row, row.Field)).ToArray();
        var previousLoading = _loading; _loading = true;
        try
        {
            EnsureFields(source.Headers);
            _aggregateField.SelectedValue = aggregateField; _groupBy.SelectedValue = groupBy;
            foreach (var entry in conditionFields) entry.Row.Field = entry.Field;
        }
        finally { _loading = previousLoading; }
        _sourceInfo.Text = $"{source.Rows.Count:N0} lignes · relevé du {source.ExtractedAt:dd/MM/yyyy HH:mm} · "
            + (source.Complete ? "extraction complète confirmée" : "extraction incomplète ou non confirmée")
            + ". Les prévisualisations portent sur ce relevé ; les widgets personnels appliqueront leur périmètre technicien.";
        InvalidatePreview("Le relevé ou ses réglages ont changé. Prévisualisez pour obtenir un résultat à jour.");
    }

    public void ShowEmpty(string message)
    {
        if (_disposed) return;
        _source = null; _sourceSettings = null; _cities = null; _sourceInfo.Text = message;
        InvalidatePreview("Aucun relevé disponible. Vos requêtes et leurs brouillons sont conservés.");
    }

    public void SetBusy(bool busy)
    {
        var wasBusy = _busy; _busy = busy;
        if (busy && !wasBusy) InvalidatePreview("Opération en cours. Votre brouillon est conservé.");
        else if (!busy && _savePending)
        {
            _savePending = false;
            SetStatus(_dirty ? "Les modifications ne sont pas encore enregistrées. Vous pouvez réessayer sans perdre le brouillon."
                : "L’enregistrement n’a pas été confirmé. La bibliothèque en mémoire est conservée.", "WarningBrush");
        }
        UpdateEnabled();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++; _previewCancellation?.Cancel(); _previewCancellation = null;
        _source = null; _sourceSettings = null; _cities = null;
        foreach (var row in _conditionRows) row.PropertyChanged -= ConditionChanged;
        UpdateEnabled();
    }

    private void CaptureDraft()
    {
        if (_loading || SelectedQuery is not { } query) return;
        query.Name = _name.Text; query.Description = _description.Text; query.Category = _category.Text;
        query.Favorite = _favorite.IsChecked == true; query.BaseProbe = Selected(_baseProbe);
        query.Match = Selected(_match); query.Aggregate = Selected(_aggregate);
        query.AggregateField = query.Aggregate is "count" or "percent" ? "" : Selected(_aggregateField);
        query.GroupBy = Selected(_groupBy);
        query.Conditions = _conditionRows.Select(row => new QueryCondition { Field = row.Field, Operator = row.Operator, Value = row.Value, Value2 = row.Value2, ValueSource = row.ValueSource, Value2Source = row.Value2Source }).ToList();
        CaptureAdvancedDraft(query);
    }

    private void MarkEdited()
    {
        if (_loading || _disposed || SelectedQuery == null) return;
        CaptureDraft(); _dirty = true; _savePending = false;
        _editorTitle.Text = string.IsNullOrWhiteSpace(_name.Text) ? "Requête sans nom" : _name.Text;
        SetStatus("Brouillons non enregistrés · ils restent disponibles en changeant de requête.", "WarningBrush");
        InvalidatePreview("La requête a changé. Prévisualisez pour vérifier le nouveau résultat."); RebuildLibrary();
    }

    private void CommitDraft()
    {
        if (!_conditions.CommitEdit(DataGridEditingUnit.Cell, true) || !_conditions.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidDataException("Terminez la condition en cours. Votre saisie reste conservée.");
        foreach (var grid in new[] { _parameterGrid, _computedGrid })
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true))
                throw new InvalidDataException("Terminez la saisie du paramètre ou du champ calculé. Votre brouillon est conservé.");
        CaptureDraft();
    }

    private void RequestSave()
    {
        if (_busy || _disposed) return;
        try
        {
            CommitDraft();
            foreach (var query in _working.Queries)
                try { query.Validate(); }
                catch (InvalidDataException error) { throw new InvalidDataException($"Requête « {query.Name} » : {error.Message}", error); }
            _working.Validate();
            if (SaveRequested == null) { SetStatus("L’enregistrement n’est pas relié à la fenêtre principale.", "WarningBrush"); return; }
            _savePending = true; SetStatus("Enregistrement de la bibliothèque…", "MutedBrush");
            SaveRequested.Invoke(_working.Copy());
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        { _savePending = false; SetStatus(error.Message, "ErrorBrush"); }
    }

    private void NewQuery()
    {
        CommitDraft(); if (_working.Queries.Count >= MaximumQueries) throw new InvalidDataException("La bibliothèque contient déjà 128 requêtes. Supprimez une requête avant d’en créer une autre.");
        var query = new SavedQuery { Id = Guid.NewGuid().ToString("N"), Name = UniqueName("Nouvelle requête"), Category = "Mes requêtes", BaseProbe = "total", Match = "all", Aggregate = "count" };
        _working.Queries.Add(query); SelectNew(query.Id);
    }

    private void DuplicateQuery()
    {
        CommitDraft(); if (SelectedQuery is not { } selected) return;
        if (_working.Queries.Count >= MaximumQueries) throw new InvalidDataException("La bibliothèque contient déjà 128 requêtes.");
        var query = selected.Copy(); query.Id = Guid.NewGuid().ToString("N"); query.Name = UniqueName(selected.Name + " · copie");
        _working.Queries.Add(query); SelectNew(query.Id);
    }

    private string UniqueName(string basis)
    {
        if (basis.Length > 106)
        {
            var length = char.IsHighSurrogate(basis[105]) && char.IsLowSurrogate(basis[106]) ? 105 : 106;
            basis = basis[..length];
        }
        var name = basis; var number = 2;
        while (_working.Queries.Any(query => string.Equals(query.Name, name, StringComparison.OrdinalIgnoreCase))) name = basis + " " + number++;
        return name;
    }

    private void SelectNew(string id)
    {
        _selectedId = id; _dirty = true; var previousLoading = _loading; _loading = true;
        try
        {
            _search.Clear(); _favoritesOnly.IsChecked = false; _categoryFilter.SelectedValue = "all";
            HideConfirmation(); EnsureSavedFields(); LoadEditor();
        }
        finally { _loading = previousLoading; }
        RebuildLibrary();
        InvalidatePreview("Nouvelle requête. Prévisualisez-la avant de l’enregistrer.");
        SetStatus("Brouillons non enregistrés.", "WarningBrush"); _sections.SelectedItem = _definitionTab; _name.Focus(); _name.SelectAll();
    }

    private void AskDelete()
    {
        if (_busy || _disposed || SelectedQuery is not { } query) return;
        var id = query.Id;
        AskConfirmation($"Supprimer « {query.Name} » de la bibliothèque ? Après enregistrement, les widgets qui utilisent cette requête seront signalés indisponibles. Leurs dispositions restent conservées.", "Supprimer et enregistrer", () =>
        {
            CommitDraft(); _working.Queries.RemoveAll(item => item.Id == id); _dirty = true;
            _selectedId = _working.Queries.FirstOrDefault()?.Id; LoadEditor(); RebuildLibrary();
            InvalidatePreview("Requête supprimée du brouillon de bibliothèque."); RequestSave();
        });
    }

    private void AskRestore()
    {
        if (_busy || _disposed || !_dirty) return;
        AskConfirmation("Rétablir la dernière bibliothèque enregistrée et abandonner tous les brouillons de cette page ?", "Rétablir la bibliothèque", () => LoadSettings(_saved));
    }

    private void AddMissingTemplates()
    {
        CommitDraft();
        var added = _working.AddMissingTemplates();
        if (added == 0)
        {
            SetStatus(_working.Queries.Count >= MaximumQueries
                ? "Bibliothèque pleine : aucun modèle ajouté. Vous pouvez libérer une place en supprimant une requête."
                : "Tous les modèles disponibles sont déjà présents. Vos requêtes sont conservées.", "MutedBrush");
            return;
        }
        _dirty = true; EnsureSavedFields();
        if (SelectedQuery == null) { _selectedId = _working.Queries.FirstOrDefault()?.Id; LoadEditor(); }
        RebuildLibrary();
        InvalidatePreview("Modèles ajoutés au brouillon. Prévisualisez une requête pour vérifier son résultat.");
        SetStatus($"{added:N0} modèle(s) ajouté(s). Les requêtes existantes sont conservées. Cliquez sur Enregistrer pour garder cet ajout.", "WarningBrush");
    }

    private void RequestWidget(bool distribution)
    {
        if (_busy || _disposed || _dirty || SelectedQuery is not { } query || !_saved.Queries.Any(item => item.Id == query.Id)) return;
        if (distribution && string.IsNullOrWhiteSpace(query.GroupBy)) return;
        WidgetRequested?.Invoke(distribution ? QueryService.DistributionKey(query.Id) : QueryService.MetricKey(query.Id), query.Name, distribution, Selected(_widgetScope) == "personal");
    }

    private void EditAction(Action action)
    {
        if (_busy || _disposed) return;
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { SetStatus(error.Message, "ErrorBrush"); }
    }

    private void AskConfirmation(string message, string confirm, Action action)
    {
        _confirmationAction = action; _confirmationText.Text = message; _confirm.Content = confirm;
        _sections.SelectedItem = _definitionTab;
        AutomationProperties.SetName(_confirm, confirm); _confirmation.Visibility = Visibility.Visible; _keep.Focus(); _confirmation.BringIntoView();
    }

    private void HideConfirmation() { _confirmationAction = null; _confirmation.Visibility = Visibility.Collapsed; }

    private void SetStatus(string message, string brush)
    {
        _status.Text = message; _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _status.SetResourceReference(TextBlock.ForegroundProperty, brush);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
    }

    private sealed record LibraryEntry(string Id, string Label);
    private static string Selected(ComboBox combo) => combo.SelectedValue as string ?? "";

    private static TextBlock Text(string value, double size = 13, string brush = "TextBrush", FontWeight? weight = null, Thickness? margin = null)
    {
        var block = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush); return block;
    }
    private static Border Card(UIElement content)
    {
        var border = new Border { Child = content, Padding = new Thickness(18), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border;
    }
    private static TextBox Input(string name, int maximum)
    {
        var input = new TextBox { MaxLength = maximum, Margin = new Thickness(0, 5, 0, 10) }; AutomationProperties.SetName(input, name); return input;
    }
    private static ComboBox Selector(string name)
    {
        var combo = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Key", Margin = new Thickness(0, 5, 0, 10), MaxDropDownHeight = 320 };
        VirtualizingPanel.SetIsVirtualizing(combo, true); VirtualizingPanel.SetVirtualizationMode(combo, VirtualizationMode.Recycling);
        AutomationProperties.SetName(combo, name); return combo;
    }
    private static CheckBox Check(string label)
    {
        var check = new CheckBox { Content = Text(label, 12), Margin = new Thickness(0, 6, 0, 10) }; AutomationProperties.SetName(check, label); return check;
    }
    private static Button Command(string label)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 7), Padding = new Thickness(11, 8, 11, 8) }; AutomationProperties.SetName(button, label); return button;
    }
    private static DataGrid EditableGrid()
    {
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow, MinHeight = 0, RowHeight = 40, Margin = new Thickness(0, 8, 0, 10) };
        VirtualizingPanel.SetIsVirtualizing(grid, true); VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(grid, true); return grid;
    }
}
