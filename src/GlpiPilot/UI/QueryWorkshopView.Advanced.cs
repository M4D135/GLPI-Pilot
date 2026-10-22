using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class QueryWorkshopView
{
    private readonly ComboBox _groupBySecondary = Selector("Deuxième niveau de regroupement"), _groupByTertiary = Selector("Troisième niveau de regroupement");
    private readonly ComboBox _groupDateBucket = Selector("Période du premier regroupement"), _secondaryDateBucket = Selector("Période du deuxième regroupement"), _tertiaryDateBucket = Selector("Période du troisième regroupement");
    private readonly TextBox _percentile = Input("Percentile entre zéro et cent", 20);
    private readonly TextBox _logicExpression = Input("Expression des conditions numérotées", 1024);
    private readonly TextBlock _logicPreview = Text("", 12, "MutedBrush");
    private readonly ObservableCollection<ParameterRow> _parameterRows = new();
    private readonly ObservableCollection<ComputedRow> _computedRows = new();
    private readonly DataGrid _parameterGrid = EditableGrid(), _computedGrid = EditableGrid();
    private readonly TabControl _advancedSections = new();
    private readonly TabItem _advancedTab = new() { Header = "Avancé" };
    private readonly Dictionary<string, Dictionary<string, string>> _parameterOverrides = new(StringComparer.Ordinal);
    private readonly TextBox _explainTicket = Input("Identifiant GLPI du ticket ou numéro de ligne précédé de dièse", 120);
    private readonly TextBlock _explainMessage = Text("Choisissez un ticket pour comprendre chacune des conditions de la requête.", 13, "MutedBrush");
    private readonly DataGrid _explainGrid = EditableGrid();
    private readonly Button _explain = Command("Expliquer ce ticket");
    private readonly Button _addParameter = Command("+ Paramètre"), _removeParameter = Command("Retirer"), _addComputed = Command("+ Champ calculé"), _removeComputed = Command("Retirer");

    private void ConfigureAdvanced()
    {
        _groupBySecondary.ItemsSource = _optionalFields; _groupByTertiary.ItemsSource = _optionalFields;
        var buckets = new[] { new QueryChoice("none", "Valeur exacte"), new QueryChoice("day", "Jour"), new QueryChoice("week", "Semaine ISO"),
            new QueryChoice("month", "Mois"), new QueryChoice("quarter", "Trimestre"), new QueryChoice("year", "Année") };
        foreach (var combo in new[] { _groupDateBucket, _secondaryDateBucket, _tertiaryDateBucket }) combo.ItemsSource = buckets;
        _logicExpression.TextWrapping = TextWrapping.Wrap; _logicExpression.AcceptsReturn = false;
        _parameterGrid.ItemsSource = _parameterRows; _computedGrid.ItemsSource = _computedRows;
    }
    private void AttachAdvancedHandlers()
    {
        foreach (var combo in new[] { _groupBySecondary, _groupByTertiary, _groupDateBucket, _secondaryDateBucket, _tertiaryDateBucket })
            combo.SelectionChanged += (_, _) => MarkEdited();
        _percentile.TextChanged += (_, _) => MarkEdited();
        _logicExpression.TextChanged += (_, _) => { ShowLogic(); MarkEdited(); };
        _addParameter.Click += (_, _) => EditAction(() =>
        {
            if (_parameterRows.Count >= 12) throw new InvalidDataException("Une requête accepte au maximum douze paramètres.");
            var key = UniqueAdvancedKey("parametre", _parameterRows.Select(row => row.Key));
            var row = new ParameterRow(new() { Key = key, Label = "Paramètre " + (_parameterRows.Count + 1) }, "");
            row.PropertyChanged += ParameterChanged; _parameterRows.Add(row); _parameterGrid.SelectedItem = row; MarkEdited();
        });
        _removeParameter.Click += (_, _) => EditAction(() =>
        {
            foreach (var row in _parameterGrid.SelectedItems.OfType<ParameterRow>().ToArray()) { row.PropertyChanged -= ParameterChanged; _parameterRows.Remove(row); }
            MarkEdited();
        });
        _addComputed.Click += (_, _) => EditAction(() =>
        {
            if (_computedRows.Count >= 12) throw new InvalidDataException("Une requête accepte au maximum douze champs calculés.");
            var row = new ComputedRow(new() { Key = UniqueAdvancedKey("calcul", _computedRows.Select(item => item.Key)), Label = "Âge calendaire", Unit = "jours" });
            row.PropertyChanged += ComputedChanged; _computedRows.Add(row); _computedGrid.SelectedItem = row; MarkEdited();
        });
        _removeComputed.Click += (_, _) => EditAction(() =>
        {
            foreach (var row in _computedGrid.SelectedItems.OfType<ComputedRow>().ToArray()) { row.PropertyChanged -= ComputedChanged; _computedRows.Remove(row); }
            MarkEdited();
        });
        _explain.Click += (_, _) => { var task = ExplainTicketAsync(); _pending.Add(task); _ = RemoveCompletedAsync(task); };
    }
    private static string UniqueAdvancedKey(string basis, IEnumerable<string> existing)
    {
        var used = existing.ToHashSet(StringComparer.Ordinal); var number = 1; var key = basis;
        while (used.Contains(key)) key = basis + ++number; return key;
    }
    private FrameworkElement BuildGroupingOptions()
    {
        var panel = new StackPanel();
        var rows = new[] { ("Période du premier niveau", _groupDateBucket, (ComboBox?)null),
            ("Deuxième niveau", _secondaryDateBucket, (ComboBox?)_groupBySecondary), ("Troisième niveau", _tertiaryDateBucket, (ComboBox?)_groupByTertiary) };
        foreach (var (label, bucket, field) in rows)
        {
            var line = new Grid(); line.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            if (field != null) Field(left, label, field); else left.Children.Add(Text(label, 12, "MutedBrush", margin: new Thickness(0, 12, 0, 0)));
            line.Children.Add(left); var right = new StackPanel(); Field(right, "Dates", bucket); Grid.SetColumn(right, 1); line.Children.Add(right); panel.Children.Add(line);
        }
        Field(panel, "Percentile · utilisé pour le calcul Percentile", _percentile);
        panel.Children.Add(Text("Chaque groupe utilise le calcul choisi : somme, moyenne, médiane… Les champs calculés et paramètres se définissent dans Avancé. Les périodes nécessitent un champ date.", 11, "MutedBrush"));
        return panel;
    }
    private FrameworkElement BuildLogicEditor()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("Expression facultative · exemples : 1 ET (2 OU 3), NON (1 OU 2)", 12, "MutedBrush"));
        panel.Children.Add(_logicExpression); panel.Children.Add(_logicPreview);
        panel.Children.Add(Text("Laissez vide pour utiliser le mode ET/OU ci-dessus. Chaque numéro correspond à une ligne de condition ; toutes les conditions doivent être utilisées. NON conserve l’état indéterminé des valeurs illisibles.", 11, "MutedBrush", margin: new Thickness(0, 3, 0, 7)));
        return panel;
    }
    private FrameworkElement BuildOperandEditor()
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var sources = new[] { new QueryChoice("literal", "Valeur saisie"), new QueryChoice("field", "Autre champ"), new QueryChoice("parameter", "Paramètre") };
        ComboBox Source(string property, string label)
        {
            var combo = Selector(label); combo.ItemsSource = sources; combo.MinWidth = 140;
            combo.SetBinding(ComboBox.SelectedValueProperty, new Binding("SelectedItem." + property) { Source = _conditions, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); return combo;
        }
        var first = new StackPanel { Margin = new Thickness(0, 0, 14, 0) }; Field(first, "Source de la valeur", Source(nameof(ConditionRow.ValueSource), "Source de la valeur")); panel.Children.Add(first);
        var upper = Input("Borne haute de l’intervalle", 500); upper.MinWidth = 170;
        upper.SetBinding(TextBox.TextProperty, new Binding("SelectedItem.Value2") { Source = _conditions, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var second = new StackPanel { Margin = new Thickness(0, 0, 14, 0) }; Field(second, "Borne haute · pour un intervalle", upper); panel.Children.Add(second);
        var source2 = new StackPanel { Margin = new Thickness(0, 0, 14, 0) }; Field(source2, "Source de la borne haute", Source(nameof(ConditionRow.Value2Source), "Source de la borne haute")); panel.Children.Add(source2);
        var picker = Selector("Champ de comparaison"); picker.ItemsSource = _fields; picker.Width = 240;
        var use = Command("Comparer à ce champ"); use.Click += (_, _) =>
        {
            if (_conditions.SelectedItem is ConditionRow row && Selected(picker).Length > 0) { row.ValueSource = "field"; row.Value = Selected(picker); }
        };
        var fields = new StackPanel(); fields.Children.Add(picker); fields.Children.Add(use); panel.Children.Add(fields);
        var root = new StackPanel(); root.Children.Add(panel);
        root.Children.Add(Text("Pour un paramètre, la valeur contient son identifiant (ex. ville). Pour un champ, utilisez le sélecteur ou sa référence exacte (role:due, column:Échéance, calc:age). « Appartient à » sépare les valeurs par ; ou retour à la ligne.", 11, "MutedBrush"));
        return root;
    }
    private void ShowLogic()
    {
        try { _logicPreview.Text = "Interprétation : " + QueryLogic.Parse(_logicExpression.Text, _conditionRows.Count, Selected(_match).Length == 0 ? "all" : Selected(_match)).Describe(); _logicPreview.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); }
        catch (InvalidDataException error) { _logicPreview.Text = error.Message; _logicPreview.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush"); }
    }
    private FrameworkElement BuildAdvanced()
    {
        _advancedSections.Items.Add(new TabItem { Header = "Paramètres", Content = BuildParameters() });
        _advancedSections.Items.Add(new TabItem { Header = "Champs calculés", Content = BuildComputedFields() });
        _advancedSections.Items.Add(new TabItem { Header = "Expliquer un ticket", Content = BuildExplanation() });
        return _advancedSections;
    }
    private FrameworkElement BuildParameters()
    {
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var top = new StackPanel(); top.Children.Add(Text("Valeurs réutilisables", 16, "TextBrush", FontWeights.SemiBold));
        top.Children.Add(Text("La valeur par défaut est enregistrée et utilisée par les widgets. La valeur d’essai s’applique uniquement à cette prévisualisation. Un paramètre requis sans valeur rend le calcul indisponible.", 12, "MutedBrush"));
        var actions = new WrapPanel(); actions.Children.Add(_addParameter); actions.Children.Add(_removeParameter); top.Children.Add(actions); panel.Children.Add(top);
        AddTextColumn(_parameterGrid, "Identifiant", nameof(ParameterRow.Key), 1, 40); AddTextColumn(_parameterGrid, "Libellé", nameof(ParameterRow.Label), 1.5, 120);
        var type = new DataGridComboBoxColumn(); ConfigureComboColumn(type, "Type", nameof(ParameterRow.Type), new[] { new QueryChoice("text", "Texte"), new QueryChoice("number", "Nombre"), new QueryChoice("date", "Date") }, 1); type.MinWidth = 100; _parameterGrid.Columns.Add(type);
        AddTextColumn(_parameterGrid, "Valeur par défaut", nameof(ParameterRow.DefaultValue), 1.5, 500); AddTextColumn(_parameterGrid, "Valeur d’essai", nameof(ParameterRow.RuntimeValue), 1.5, 500);
        Grid.SetRow(_parameterGrid, 1); panel.Children.Add(_parameterGrid); return panel;
    }
    private FrameworkElement BuildComputedFields()
    {
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new StackPanel(); top.Children.Add(Text("Calculs locaux contrôlés", 16, "TextBrush", FontWeights.SemiBold));
        top.Children.Add(Text("Référence : calc:identifiant. Les divisions par zéro, dates illisibles et résultats non finis restent indisponibles. Les différences de dates sont calendaires, en jours ; aucune durée de travail ou SLA n’est déduite.", 12, "MutedBrush"));
        var actions = new WrapPanel(); actions.Children.Add(_addComputed); actions.Children.Add(_removeComputed); top.Children.Add(actions); panel.Children.Add(top);
        AddTextColumn(_computedGrid, "Identifiant", nameof(ComputedRow.Key), 1, 40); AddTextColumn(_computedGrid, "Libellé", nameof(ComputedRow.Label), 1.5, 120);
        var functions = new[] { new QueryChoice("age-days", "Âge en jours"), new QueryChoice("days-between", "Jours : champ 2 − champ 1"),
            new QueryChoice("add", "Addition"), new QueryChoice("subtract", "Soustraction"), new QueryChoice("multiply", "Multiplication"), new QueryChoice("divide", "Division"),
            new QueryChoice("length", "Longueur du texte"), new QueryChoice("lower", "Texte en minuscules"), new QueryChoice("concat", "Concaténation"), new QueryChoice("coalesce", "Remplacer un vide") };
        var function = new DataGridComboBoxColumn(); ConfigureComboColumn(function, "Fonction", nameof(ComputedRow.Function), functions, 2); _computedGrid.Columns.Add(function);
        AddTextColumn(_computedGrid, "Unité", nameof(ComputedRow.Unit), 1, 40);
        Grid.SetRow(_computedGrid, 1); panel.Children.Add(_computedGrid);
        var inputs = new WrapPanel();
        foreach (var (property, label, optional) in new[] { (nameof(ComputedRow.Field), "Premier champ", false), (nameof(ComputedRow.OtherField), "Deuxième champ · facultatif", true) })
        {
            var choice = Selector(label); choice.ItemsSource = optional ? _optionalFields : _fields; choice.Width = 270;
            choice.SetBinding(ComboBox.SelectedValueProperty, new Binding("SelectedItem." + property) { Source = _computedGrid, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            var section = new StackPanel { Margin = new Thickness(0, 0, 14, 0) }; Field(section, label, choice); inputs.Children.Add(section);
        }
        var constant = Input("Constante ou texte de remplacement ou séparateur", 500); constant.Width = 220;
        constant.SetBinding(TextBox.TextProperty, new Binding("SelectedItem.Constant") { Source = _computedGrid, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var constantPanel = new StackPanel(); Field(constantPanel, "Constante / remplacement / séparateur", constant); inputs.Children.Add(constantPanel);
        Grid.SetRow(inputs, 2); panel.Children.Add(inputs); return panel;
    }
    private void AddTextColumn(DataGrid grid, string label, string property, double width, int maximum)
    {
        var editing = new Style(typeof(TextBox), TryFindResource(typeof(TextBox)) as Style); editing.Setters.Add(new Setter(TextBox.MaxLengthProperty, maximum));
        grid.Columns.Add(new DataGridTextColumn { Header = label, Width = new DataGridLength(width, DataGridLengthUnitType.Star), MinWidth = 105,
            Binding = new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, EditingElementStyle = editing });
    }
    private FrameworkElement BuildExplanation()
    {
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var top = new StackPanel(); top.Children.Add(Text("Pourquoi ce ticket est-il retenu ?", 16, "TextBrush", FontWeights.SemiBold));
        top.Children.Add(Text("Saisissez l’identifiant GLPI, ou # suivi du numéro de ligne source (ex. #12). Une ligne hors du périmètre reste exclue, même si ses conditions sont vraies.", 12, "MutedBrush"));
        var input = new DockPanel(); _explainTicket.MinWidth = 180;
        var cancel = Command("Annuler"); cancel.Click += (_, _) => InvalidatePreview("Explication annulée. Le brouillon est conservé."); DockPanel.SetDock(cancel, Dock.Right); input.Children.Add(cancel); DockPanel.SetDock(_explain, Dock.Right); input.Children.Add(_explain); input.Children.Add(_explainTicket); top.Children.Add(input); top.Children.Add(_explainMessage); panel.Children.Add(top);
        _explainGrid.IsReadOnly = true;
        foreach (var (label, property, width) in new[] { ("N°", "Number", .4), ("Champ", "Field", 1.2), ("Valeur du ticket", "Value", 1.5), ("Opérateur", "Operator", 1.2), ("Comparaison", "Comparison", 1.5), ("Résultat", "Decision", 1d) })
            _explainGrid.Columns.Add(new DataGridTextColumn { Header = label, Width = new DataGridLength(width, DataGridLengthUnitType.Star), MinWidth = 55, Binding = new Binding(property) { Mode = BindingMode.OneWay } });
        Grid.SetRow(_explainGrid, 1); panel.Children.Add(_explainGrid); return panel;
    }
    private async Task ExplainTicketAsync()
    {
        if (_busy || _disposed || _source == null || _sourceSettings == null || _previewRunning) return;
        try
        {
            CommitDraft(); if (SelectedQuery is not { } current) return; current.Validate();
            var query = current.Copy(); var source = _source; var settings = _sourceSettings.Copy(); var parameters = RuntimeParameters();
            var cities = _cities != null && ReferenceEquals(_cities.Data, source) ? _cities : null;
            var identifier = _explainTicket.Text.Trim(); var generation = _generation;
            var cancellation = new CancellationTokenSource(); _previewCancellation = cancellation; _previewRunning = true; UpdateEnabled();
            _explainMessage.Text = "Lecture des conditions…"; _explainGrid.ItemsSource = null;
            try
            {
                var result = await Task.Run(() =>
                {
                    var snapshot = DashboardService.Calculate(source, settings, ct: cancellation.Token);
                    if (cities != null) CityDetectionService.AppendDashboardProbes(snapshot, cities, cancellation.Token);
                    int row;
                    if (identifier.StartsWith('#') && int.TryParse(identifier[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var line) && line >= 1 && line <= source.Rows.Count) row = line - 1;
                    else
                    {
                        var column = snapshot.Columns.FirstOrDefault(value => value.Role == "id")?.Index;
                        if (!column.HasValue || identifier.Length == 0) throw new InvalidDataException("Saisissez un identifiant disponible ou un numéro de ligne source précédé de #.");
                        var rows = Enumerable.Range(0, source.Rows.Count).Where(index => column.Value < source.Rows[index].Length && string.Equals(source.Rows[index][column.Value]?.Trim(), identifier, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                        if (rows.Length != 1) throw new InvalidDataException(rows.Length == 0 ? "Cet identifiant n’est pas présent dans le relevé." : "Cet identifiant apparaît plusieurs fois ; indiquez le numéro de ligne avec #.");
                        row = rows[0];
                    }
                    return QueryService.Explain(source, settings, snapshot, query, row, cancellation.Token, parameters);
                }, cancellation.Token);
                if (_disposed || cancellation.IsCancellationRequested || generation != _generation || !ReferenceEquals(source, _source)) return;
                _explainMessage.Text = $"Ligne {result.Row + 1:N0} · {result.Message}\nLogique : {result.Logic}"; _explainGrid.ItemsSource = result.Conditions;
            }
            finally
            {
                if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
                cancellation.Dispose(); if (!_disposed && generation == _generation) { _previewRunning = false; UpdateEnabled(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { _explainMessage.Text = error.Message; }
    }
    private void CaptureAdvancedDraft(SavedQuery query)
    {
        query.ConditionExpression = _logicExpression.Text; query.GroupBySecondary = Selected(_groupBySecondary); query.GroupByTertiary = Selected(_groupByTertiary);
        query.GroupDateBucket = Selected(_groupDateBucket); query.SecondaryDateBucket = Selected(_secondaryDateBucket); query.TertiaryDateBucket = Selected(_tertiaryDateBucket);
        query.Percentile = QueryText.TryNumber(_percentile.Text, out var percentile) ? percentile : double.NaN;
        query.Parameters = _parameterRows.Select(row => row.Definition()).ToList(); query.ComputedFields = _computedRows.Select(row => row.Definition()).ToList();
        if (_selectedId != null) _parameterOverrides[_selectedId] = RuntimeParameters();
        EnsureComputedChoices(query); ShowLogic();
    }
    private void LoadAdvancedDraft(SavedQuery? query)
    {
        foreach (var row in _parameterRows) row.PropertyChanged -= ParameterChanged; _parameterRows.Clear();
        foreach (var row in _computedRows) row.PropertyChanged -= ComputedChanged; _computedRows.Clear();
        _logicExpression.Text = query?.ConditionExpression ?? ""; _percentile.Text = (query?.Percentile ?? 90).ToString("G", CultureInfo.CurrentCulture);
        if (query != null) EnsureComputedChoices(query);
        SetSelection(_groupBySecondary, query?.GroupBySecondary ?? ""); SetSelection(_groupByTertiary, query?.GroupByTertiary ?? "");
        SetSelection(_groupDateBucket, query?.GroupDateBucket ?? "none"); SetSelection(_secondaryDateBucket, query?.SecondaryDateBucket ?? "none"); SetSelection(_tertiaryDateBucket, query?.TertiaryDateBucket ?? "none");
        foreach (var parameter in query?.Parameters ?? new())
        {
            var value = query != null && _parameterOverrides.TryGetValue(query.Id, out var values) && values.TryGetValue(parameter.Key, out var saved) ? saved : "";
            var row = new ParameterRow(parameter, value); row.PropertyChanged += ParameterChanged; _parameterRows.Add(row);
        }
        foreach (var field in query?.ComputedFields ?? new()) { var row = new ComputedRow(field); row.PropertyChanged += ComputedChanged; _computedRows.Add(row); }
        _parameterGrid.SelectedIndex = _parameterRows.Count > 0 ? 0 : -1; _computedGrid.SelectedIndex = _computedRows.Count > 0 ? 0 : -1;
        ShowLogic();
    }
    private void EnsureComputedChoices(SavedQuery query)
    {
        foreach (var computed in query.ComputedFields.Where(field => QueryText.Identifier(field.Key)))
        {
            var key = "calc:" + computed.Key;
            if (_fields.All(choice => choice.Key != key)) { var choice = new QueryChoice(key, computed.Label + " · calcul local"); _fields.Add(choice); _optionalFields.Add(choice); }
        }
    }
    private Dictionary<string, string> RuntimeParameters() => _parameterRows.Where(row => QueryText.Identifier(row.Key) && row.RuntimeValue.Length > 0)
        .GroupBy(row => row.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().RuntimeValue, StringComparer.Ordinal);
    private void ParameterChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_loading) return;
        if (args.PropertyName == nameof(ParameterRow.RuntimeValue)) { InvalidatePreview("Le paramètre d’essai a changé. Prévisualisez de nouveau."); return; }
        MarkEdited();
    }
    private void ComputedChanged(object? sender, PropertyChangedEventArgs args) => MarkEdited();
    private void UpdateAdvancedEnabled(bool allowed, bool selected)
    {
        _advancedSections.IsEnabled = allowed && selected; _logicExpression.IsEnabled = allowed && selected;
        _percentile.IsEnabled = allowed && selected && Selected(_aggregate) == "percentile";
        _addParameter.IsEnabled = allowed && selected && _parameterRows.Count < 12; _addComputed.IsEnabled = allowed && selected && _computedRows.Count < 12;
        _explain.IsEnabled = allowed && selected && _source != null && !_previewRunning;
    }
    private abstract class AdvancedRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Change(ref string target, string? value, [CallerMemberName] string? property = null)
        { value ??= ""; if (target == value) return; target = value; PropertyChanged?.Invoke(this, new(property)); }
    }
    private sealed class ParameterRow : AdvancedRow
    {
        private string _key, _label, _type, _defaultValue, _runtimeValue;
        public string Key { get => _key; set => Change(ref _key, value); }
        public string Label { get => _label; set => Change(ref _label, value); }
        public string Type { get => _type; set => Change(ref _type, value); }
        public string DefaultValue { get => _defaultValue; set => Change(ref _defaultValue, value); }
        public string RuntimeValue { get => _runtimeValue; set => Change(ref _runtimeValue, value); }
        public ParameterRow(QueryParameter value, string runtime) { _key = value.Key; _label = value.Label; _type = value.Type; _defaultValue = value.DefaultValue; _runtimeValue = runtime; }
        public QueryParameter Definition() => new() { Key = Key, Label = Label, Type = Type, DefaultValue = DefaultValue };
    }
    private sealed class ComputedRow : AdvancedRow
    {
        private string _key, _label, _function, _field, _other, _constant, _unit;
        public string Key { get => _key; set => Change(ref _key, value); }
        public string Label { get => _label; set => Change(ref _label, value); }
        public string Function { get => _function; set => Change(ref _function, value); }
        public string Field { get => _field; set => Change(ref _field, value); }
        public string OtherField { get => _other; set => Change(ref _other, value); }
        public string Constant { get => _constant; set => Change(ref _constant, value); }
        public string Unit { get => _unit; set => Change(ref _unit, value); }
        public ComputedRow(QueryComputedField value) { _key = value.Key; _label = value.Label; _function = value.Function; _field = value.Field; _other = value.OtherField; _constant = value.Constant; _unit = value.Unit; }
        public QueryComputedField Definition() => new() { Key = Key, Label = Label, Function = Function, Field = Field, OtherField = OtherField, Constant = Constant, Unit = Unit };
    }
}
