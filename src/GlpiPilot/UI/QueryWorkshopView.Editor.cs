using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class QueryWorkshopView
{
    private readonly TextBlock _noQuery = Text("Créez une requête ou choisissez-en une dans la bibliothèque. Les modifications restent dans l’atelier jusqu’à leur enregistrement.", 13, "MutedBrush", margin: new Thickness(0, 10, 0, 14));

    private FrameworkElement BuildLibrary()
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var filters = new StackPanel();
        var actions = new WrapPanel(); actions.Children.Add(_new); actions.Children.Add(_duplicate); actions.Children.Add(_delete);
        _templates.Content = "Modèles +"; _templates.ToolTip = "Ajouter les modèles manquants à ce brouillon, sans remplacer les requêtes existantes";
        actions.Children.Add(_templates);
        var edit = Command("Modifier la requête →"); edit.Click += (_, _) => _sections.SelectedItem = _definitionTab;
        edit.Content = "Modifier →"; edit.ToolTip = "Ouvrir la définition de la requête sélectionnée";
        actions.Children.Add(edit);
        _restore.Content = "Rétablir"; _restore.ToolTip = "Rétablir les réglages enregistrés et abandonner les brouillons de bibliothèque";
        actions.Children.Add(_restore); filters.Children.Add(actions);
        var searchFields = new Grid();
        searchFields.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        searchFields.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        searchFields.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var searchField = new Grid { Margin = new Thickness(0, 0, 12, 0) };
        _search.Margin = new Thickness(0); _search.ToolTip = "Rechercher dans le nom, la description ou la catégorie des requêtes";
        searchField.Children.Add(_search);
        var searchHint = Text("Rechercher…", 12, "MutedBrush", margin: new Thickness(11, 0, 11, 0));
        searchHint.IsHitTestVisible = false;
        var hintStyle = new Style(typeof(TextBlock)); hintStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        var emptySearch = new DataTrigger { Binding = new Binding(nameof(TextBox.Text)) { Source = _search }, Value = "" };
        emptySearch.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible)); hintStyle.Triggers.Add(emptySearch); searchHint.Style = hintStyle;
        searchField.Children.Add(searchHint); searchFields.Children.Add(searchField);
        _categoryFilter.Margin = new Thickness(0); _categoryFilter.ToolTip = "Filtrer la bibliothèque par catégorie";
        Grid.SetColumn(_categoryFilter, 1); searchFields.Children.Add(_categoryFilter);
        _favoritesOnly.Content = Text("Favoris", 12); _favoritesOnly.Margin = new Thickness(12, 0, 0, 0);
        _favoritesOnly.VerticalAlignment = VerticalAlignment.Center; _favoritesOnly.ToolTip = "N’afficher que les requêtes favorites";
        Grid.SetColumn(_favoritesOnly, 2); searchFields.Children.Add(_favoritesOnly);
        filters.Children.Add(searchFields); panel.Children.Add(filters);
        Grid.SetRow(_libraryList, 1); panel.Children.Add(_libraryList);
        ScrollViewer.SetHorizontalScrollBarVisibility(_libraryList, ScrollBarVisibility.Disabled);
        var itemStyle = new Style(typeof(ListBoxItem), TryFindResource(typeof(ListBoxItem)) as Style);
        itemStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(LibraryEntry.Label)) { Mode = BindingMode.OneWay }));
        _libraryList.ItemContainerStyle = itemStyle;
        _libraryList.ToolTip = "Bibliothèque partagée avec votre configuration. Sélectionnez une requête, puis ouvrez sa définition ou prévisualisez-la.";
        return panel;
    }

    private FrameworkElement BuildEditor()
    {
        var panel = new StackPanel(); panel.Children.Add(_editorTitle); panel.Children.Add(_noQuery);
        panel.Children.Add(Text("Composez vos indicateurs à partir du relevé : définissez le périmètre et le calcul ici, puis vos critères dans Conditions. Prévisualisez le résultat avant de l’enregistrer ou de l’ajouter à un dashboard.", 12, "MutedBrush", margin: new Thickness(0, 6, 0, 10)));
        var confirm = new StackPanel(); confirm.Children.Add(_confirmationText);
        var confirmActions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; confirmActions.Children.Add(_confirm); confirmActions.Children.Add(_keep);
        confirm.Children.Add(confirmActions); _confirmation.Child = confirm; panel.Children.Add(_confirmation);
        Field(_editorFields, "Nom de la requête", _name); Field(_editorFields, "Description · facultative", _description);
        var identity = new Grid(); identity.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); identity.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var categoryPanel = new StackPanel { Margin = new Thickness(0, 0, 18, 0) }; Field(categoryPanel, "Catégorie · facultative", _category); identity.Children.Add(categoryPanel);
        Grid.SetColumn(_favorite, 1); _favorite.VerticalAlignment = VerticalAlignment.Center; identity.Children.Add(_favorite); _editorFields.Children.Add(identity);
        Section(_editorFields, "1 · Choisir le périmètre", "Les conditions sont appliquées aux lignes de ce périmètre de départ.");
        Field(_editorFields, "Sonde de départ", _baseProbe);
        Section(_editorFields, "2 · Calculer et regrouper", "Le résultat indiquera les données réellement utilisables. Une colonne absente ne devient pas une valeur zéro.");
        var calculation = new Grid(); calculation.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); calculation.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var aggregatePanel = new StackPanel { Margin = new Thickness(0, 0, 14, 0) }; Field(aggregatePanel, "Calcul", _aggregate); calculation.Children.Add(aggregatePanel);
        var valuePanel = new StackPanel(); Field(valuePanel, "Champ du calcul", _aggregateField); Grid.SetColumn(valuePanel, 1); calculation.Children.Add(valuePanel); _editorFields.Children.Add(calculation);
        _editorFields.Children.Add(_aggregateHint); Field(_editorFields, "Répartition par · facultative", _groupBy);
        _editorFields.Children.Add(Text("Le regroupement compte les lignes retenues par catégorie. Il reste distinct d’une somme, d’une moyenne ou d’un autre calcul numérique.", 11, "MutedBrush"));
        Section(_editorFields, "3 · Définir les conditions", "Sans condition supplémentaire, tout le périmètre est conservé. En mode OU, une condition vraie suffit, même si une autre cellule est illisible. Une colonne requise absente rend la requête indisponible.");
        _editorFields.Children.Add(Text("Utilisez les champs sémantiques pour retrouver les colonnes configurées, ou une colonne exacte du relevé. « Est vide » et « Est renseigné » n’attendent pas de valeur. Pour les dates, vous pouvez saisir AAAA-MM-JJ ; les durées sont exprimées en jours.", 11, "MutedBrush"));
        var openConditions = Command("Définir les conditions →"); openConditions.Click += (_, _) => _sections.SelectedItem = _conditionsTab;
        openConditions.Margin = new Thickness(0, 16, 0, 0); _editorFields.Children.Add(openConditions);
        panel.Children.Add(_editorFields);
        return FormViewport(Card(panel));
    }

    private FrameworkElement BuildConditions()
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel();
        var controls = new DockPanel();
        var actions = new WrapPanel();
        _addCondition.Content = "+ Condition"; _addCondition.ToolTip = "Ajouter une condition";
        _removeCondition.Content = "Retirer"; _removeCondition.ToolTip = "Retirer les conditions sélectionnées";
        actions.Children.Add(_addCondition); actions.Children.Add(_removeCondition);
        DockPanel.SetDock(actions, Dock.Right); controls.Children.Add(actions);
        _match.Margin = new Thickness(0, 0, 12, 7); _match.ToolTip = "Combiner toutes les conditions (ET) ou accepter au moins une condition vraie (OU)";
        controls.Children.Add(_match); header.Children.Add(controls); header.Children.Add(_conditionCount);
        panel.Children.Add(header); Grid.SetRow(_conditions, 1); panel.Children.Add(_conditions);
        _conditions.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetName(_conditions, "Conditions de la requête, champs opérateurs et valeurs modifiables");
        _conditions.ToolTip = "Dates : AAAA-MM-JJ · durées : jours. Les explications complètes restent dans Définition.";
        return panel;
    }

    private static ScrollViewer FormViewport(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        Padding = new Thickness(0, 4, 8, 8)
    };

    private static void Field(Panel panel, string label, Control control)
    {
        panel.Children.Add(Text(label, 12, "MutedBrush", FontWeights.SemiBold, new Thickness(0, 6, 0, 0))); panel.Children.Add(control);
    }

    private static void Section(Panel panel, string title, string note)
    {
        panel.Children.Add(Text(title, 15, "TextBrush", FontWeights.SemiBold, new Thickness(0, 15, 0, 5)));
        panel.Children.Add(Text(note, 12, "MutedBrush", margin: new Thickness(0, 0, 0, 4)));
    }

    private void ConfigureConditionColumns()
    {
        var choices = new[]
        {
            new QueryChoice("contains", "Contient"), new QueryChoice("not-contains", "Ne contient pas"), new QueryChoice("equals", "Égal à"),
            new QueryChoice("not-equals", "Différent de"), new QueryChoice("starts-with", "Commence par"), new QueryChoice("empty", "Est vide"), new QueryChoice("not-empty", "Est renseigné"),
            new QueryChoice("number-eq", "Nombre égal à"), new QueryChoice("number-gt", "Nombre supérieur à"), new QueryChoice("number-gte", "Nombre supérieur ou égal à"),
            new QueryChoice("number-lt", "Nombre inférieur à"), new QueryChoice("number-lte", "Nombre inférieur ou égal à"),
            new QueryChoice("date-before", "Date avant le"), new QueryChoice("date-after", "Date après le"), new QueryChoice("last-days", "Dans les N derniers jours"),
            new QueryChoice("next-days", "Dans les N prochains jours"), new QueryChoice("age-at-least", "Date ancienne d’au moins N jours")
        };
        ConfigureComboColumn(_conditionFieldColumn, "Champ", nameof(ConditionRow.Field), _fields, 2);
        var operatorColumn = new DataGridComboBoxColumn(); ConfigureComboColumn(operatorColumn, "Condition", nameof(ConditionRow.Operator), choices, 2);
        _conditions.Columns.Add(_conditionFieldColumn); _conditions.Columns.Add(operatorColumn);
        var display = new Style(typeof(TextBlock)); display.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        display.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(ConditionRow.Value)) { Mode = BindingMode.OneWay }));
        var editing = new Style(typeof(TextBox), TryFindResource(typeof(TextBox)) as Style);
        editing.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3, 1, 3, 1))); editing.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 25d)); editing.Setters.Add(new Setter(TextBox.MaxLengthProperty, 500));
        foreach (var noValue in new[] { "empty", "not-empty" })
        {
            var trigger = new DataTrigger { Binding = new Binding(nameof(ConditionRow.Operator)), Value = noValue };
            trigger.Setters.Add(new Setter(UIElement.IsEnabledProperty, false)); editing.Triggers.Add(trigger);
        }
        _conditions.Columns.Add(new DataGridTextColumn { Header = "Valeur", Width = new DataGridLength(2, DataGridLengthUnitType.Star), MinWidth = 160,
            Binding = new Binding(nameof(ConditionRow.Value)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, ElementStyle = display, EditingElementStyle = editing });
    }

    private void ConfigureComboColumn(DataGridComboBoxColumn column, string header, string property, System.Collections.IEnumerable choices, double width)
    {
        column.Header = header; column.Width = new DataGridLength(width, DataGridLengthUnitType.Star); column.MinWidth = 180;
        column.DisplayMemberPath = "Label"; column.SelectedValuePath = "Key"; column.ItemsSource = choices;
        column.SelectedValueBinding = new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        var display = new Style(typeof(ComboBox), TryFindResource(typeof(ComboBox)) as Style);
        display.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, false)); display.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        display.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3, 1, 3, 1))); display.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 25d));
        var editing = new Style(typeof(ComboBox), TryFindResource(typeof(ComboBox)) as Style);
        editing.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3, 1, 3, 1))); editing.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 25d));
        editing.Setters.Add(new Setter(ComboBox.MaxDropDownHeightProperty, 300d));
        column.ElementStyle = display; column.EditingElementStyle = editing;
    }

    private void EnsureFields(IReadOnlyList<string> headers)
    {
        // Append to stable collections. Replacing ItemsSource while a condition is edited can clear its binding.
        foreach (var choice in QueryService.FieldChoices(headers))
            if (_fields.All(item => item.Key != choice.Key)) { _fields.Add(choice); _optionalFields.Add(choice); }
    }

    private void EnsureSavedFields()
    {
        foreach (var query in _working.Queries)
        {
            if (!string.IsNullOrWhiteSpace(query.BaseProbe) && _bases.All(item => item.Key != query.BaseProbe))
                _bases.Add(new QueryChoice(query.BaseProbe, query.BaseProbe + " · sonde mémorisée"));
            foreach (var field in query.Conditions.Select(condition => condition.Field).Concat(new[] { query.AggregateField, query.GroupBy }))
                if (!string.IsNullOrWhiteSpace(field) && _fields.All(item => item.Key != field))
                {
                    var choice = new QueryChoice(field, field.StartsWith("column:", StringComparison.Ordinal) ? field[7..] + " · colonne mémorisée" : field + " · champ mémorisé");
                    _fields.Add(choice); _optionalFields.Add(choice);
                }
        }
    }

    private void RebuildLibrary()
    {
        if (_loading || _rebuildingLibrary || _disposed) return;
        _rebuildingLibrary = true;
        try
        {
            var selectedCategory = Selected(_categoryFilter);
            var categories = new[] { new QueryChoice("all", "Toutes les catégories") }
                .Concat(_working.Queries.Select(query => query.Category.Trim()).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(name => new QueryChoice("category:" + name, name.Length == 0 ? "Sans catégorie" : name))).ToArray();
            _categoryFilter.ItemsSource = categories;
            _categoryFilter.SelectedValue = categories.Any(choice => choice.Key == selectedCategory) ? selectedCategory : "all";
            var category = Selected(_categoryFilter); var search = _search.Text.Trim();
            bool Matches(SavedQuery query) => (category == "all" || category == "category:" + query.Category.Trim())
                && (_favoritesOnly.IsChecked != true || query.Favorite)
                && (search.Length == 0 || CultureInfo.CurrentCulture.CompareInfo.IndexOf(query.Name + " " + query.Description + " " + query.Category,
                    search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
            var entries = _working.Queries.Where(Matches).OrderByDescending(query => query.Favorite).ThenBy(query => query.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(query => new LibraryEntry(query.Id, (query.Favorite ? "★  " : "") + (string.IsNullOrWhiteSpace(query.Name) ? "Requête sans nom" : query.Name)
                    + "\n" + (string.IsNullOrWhiteSpace(query.Category) ? "Sans catégorie" : query.Category))).ToArray();
            _libraryList.ItemsSource = entries; _libraryList.SelectedItem = entries.FirstOrDefault(entry => entry.Id == _selectedId);
            _libraryCount.Text = $"{entries.Length:N0} / {_working.Queries.Count:N0} requêtes · maximum 128";
        }
        finally { _rebuildingLibrary = false; }
        UpdateEnabled();
    }

    private void SwitchSelectedQuery()
    {
        if (_loading || _rebuildingLibrary || _busy || _disposed || _libraryList.SelectedItem is not LibraryEntry entry || entry.Id == _selectedId) return;
        try { CommitDraft(); }
        catch (InvalidDataException error) { SetStatus(error.Message, "ErrorBrush"); RebuildLibrary(); return; }
        _selectedId = entry.Id; HideConfirmation(); LoadEditor();
        InvalidatePreview("Prévisualisez cette requête sur le relevé actuel.");
    }

    private void LoadEditor()
    {
        var query = SelectedQuery; var previousLoading = _loading; _loading = true;
        try
        {
            _conditions.CancelEdit(DataGridEditingUnit.Cell); _conditions.CancelEdit(DataGridEditingUnit.Row);
            foreach (var row in _conditionRows) row.PropertyChanged -= ConditionChanged;
            _conditionRows.Clear();
            _name.Text = query?.Name ?? ""; _description.Text = query?.Description ?? ""; _category.Text = query?.Category ?? "";
            _favorite.IsChecked = query?.Favorite ?? false;
            SetSelection(_baseProbe, query?.BaseProbe ?? "total"); SetSelection(_match, query?.Match ?? "all"); SetSelection(_aggregate, query?.Aggregate ?? "count");
            _aggregateField.SelectedValue = query?.AggregateField ?? ""; _groupBy.SelectedValue = query?.GroupBy ?? "";
            foreach (var condition in query?.Conditions ?? new List<QueryCondition>()) AddConditionRow(new ConditionRow(condition));
            _editorTitle.Text = query == null ? "Votre première requête" : string.IsNullOrWhiteSpace(query.Name) ? "Requête sans nom" : query.Name;
            _editorFields.Visibility = query == null ? Visibility.Collapsed : Visibility.Visible;
            _noQuery.Visibility = query == null ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _loading = previousLoading; }
        UpdateEnabled();
    }

    private static void SetSelection(ComboBox control, string key)
    {
        var choices = control.ItemsSource?.Cast<QueryChoice>().ToArray() ?? Array.Empty<QueryChoice>();
        if (key.Length > 0 && choices.All(choice => choice.Key != key)) control.ItemsSource = choices.Append(new QueryChoice(key, key + " · valeur mémorisée")).ToArray();
        control.SelectedValue = key;
    }

    private void AddCondition()
    {
        CommitDraft(); if (SelectedQuery == null) return;
        if (_conditionRows.Count >= MaximumConditions) throw new InvalidDataException("Une requête peut contenir au maximum 20 conditions.");
        var row = new ConditionRow(new QueryCondition { Field = _fields.FirstOrDefault(choice => choice.Key == "role:title")?.Key ?? _fields.FirstOrDefault()?.Key ?? "", Operator = "contains" });
        AddConditionRow(row); _conditions.SelectedItem = row; _conditions.ScrollIntoView(row); MarkEdited();
    }

    private void RemoveConditions()
    {
        CommitDraft();
        var selected = _conditions.SelectedItems.OfType<ConditionRow>().ToArray();
        foreach (var row in selected) { row.PropertyChanged -= ConditionChanged; _conditionRows.Remove(row); }
        if (selected.Length > 0) MarkEdited();
    }

    private void AddConditionRow(ConditionRow row) { row.PropertyChanged += ConditionChanged; _conditionRows.Add(row); }
    private void ConditionChanged(object? sender, PropertyChangedEventArgs args) => MarkEdited();

    private void UpdateEnabled()
    {
        var allowed = !_busy && !_disposed; var selected = SelectedQuery;
        _new.IsEnabled = allowed && _working.Queries.Count < MaximumQueries;
        _templates.IsEnabled = allowed;
        _duplicate.IsEnabled = allowed && selected != null && _working.Queries.Count < MaximumQueries;
        _delete.IsEnabled = allowed && selected != null; _restore.IsEnabled = allowed && _dirty;
        _search.IsEnabled = _categoryFilter.IsEnabled = _favoritesOnly.IsEnabled = _libraryList.IsEnabled = allowed;
        _editorFields.IsEnabled = allowed && selected != null;
        _match.IsEnabled = _conditions.IsEnabled = allowed && selected != null;
        _conditions.IsReadOnly = !allowed;
        _addCondition.IsEnabled = allowed && selected != null && _conditionRows.Count < MaximumConditions;
        _removeCondition.IsEnabled = allowed && _conditions.SelectedItems.Count > 0;
        _save.IsEnabled = allowed; _confirm.IsEnabled = allowed;
        _conditionCount.Text = _conditionRows.Count == 0 ? "Aucune condition supplémentaire" : $"{_conditionRows.Count} / 20 conditions";
        var aggregate = Selected(_aggregate); var needsField = aggregate is not ("count" or "percent");
        _aggregateField.IsEnabled = allowed && selected != null && needsField;
        _aggregateHint.Text = aggregate switch
        {
            "distinct" => "Choisissez le champ dont vous souhaitez compter les valeurs distinctes.",
            "sum" or "average" or "min" or "max" => "Choisissez un champ numérique. Seules les valeurs interprétables participent au calcul ; l’échantillon est affiché.",
            "percent" => "La prévisualisation précisera la base du pourcentage et les lignes retenues.",
            _ => "Le calcul compte les lignes retenues. Le champ du calcul n’est pas utilisé."
        };
        _preview.IsEnabled = allowed && selected != null && _source != null && !_previewRunning;
        _cancelPreview.IsEnabled = allowed && _previewRunning;
        var canAdd = allowed && !_dirty && selected != null && _saved.Queries.Any(query => query.Id == selected.Id);
        _widgetScope.IsEnabled = allowed && selected != null;
        _addMetric.IsEnabled = canAdd; _addDistribution.IsEnabled = canAdd && !string.IsNullOrWhiteSpace(selected?.GroupBy);
        _widgetHint.Text = _dirty ? "Enregistrez les brouillons avant d’ajouter un widget."
            : selected == null ? "Choisissez une requête pour créer son widget."
            : "Le widget sera ajouté à la disposition active du dashboard choisi. Ajoutez un regroupement pour créer une répartition.";
        UpdatePreviewActions();
    }

    private sealed class ConditionRow : INotifyPropertyChanged
    {
        private string _field, _operator, _value, _value2;
        public string Field { get => _field; set => Set(ref _field, value); }
        public string Operator { get => _operator; set => Set(ref _operator, value); }
        public string Value { get => _value; set => Set(ref _value, value); }
        public string Value2 { get => _value2; set => Set(ref _value2, value); }
        public event PropertyChangedEventHandler? PropertyChanged;
        public ConditionRow(QueryCondition condition) { _field = condition.Field; _operator = condition.Operator; _value = condition.Value; _value2 = condition.Value2; }
        private void Set(ref string field, string? value, [CallerMemberName] string? property = null)
        {
            value ??= ""; if (field == value) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
