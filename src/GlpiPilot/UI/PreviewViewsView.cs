using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class PreviewViewsView : UserControl
{
    private readonly IReadOnlyList<string> _headers;
    private readonly NamedPreviewStore _store;
    private readonly CultureInfo _culture;
    private readonly ObservableCollection<PreviewColumnFilter> _filters = new();
    private readonly ObservableCollection<PreviewSortRule> _sorts = new();
    private readonly DataGrid _filterGrid = PilotControls.Table(editable: true), _sortGrid = PilotControls.Table(editable: true);
    private readonly ComboBox _saved = new() { DisplayMemberPath = "Name", MinWidth = 200, MaxWidth = 420, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _name = PilotControls.Input("Ma vue"), _search = PilotControls.Input();
    private readonly TextBlock _state = PilotControls.Text("", 12, true);
    private PreviewColumnPreference[] _columns;
    private string _id = Guid.NewGuid().ToString("N");
    public event Action<NamedPreview>? Applied;

    public PreviewViewsView(IReadOnlyList<string> headers, PreviewTransform current, string search, PreviewColumnPreference[] columns, CultureInfo culture, NamedPreviewStore? store = null)
    {
        _headers = headers.ToArray(); _columns = columns.Select(column => column.Copy()).ToArray(); _culture = culture; _store = store ?? new();
        var choices = headers.Select((header, index) => new Field(index, $"{index + 1}. {header}")).ToArray();
        Column(_filterGrid, "Colonne", "Column", choices, "Index", "Label", -1);
        Column(_filterGrid, "Condition", "Operator", new[] { new Choice("contains", "Contient"), new Choice("not-contains", "Ne contient pas"), new Choice("eq", "Égal à"), new Choice("ne", "Différent de"), new Choice("empty", "Vide"), new Choice("not-empty", "Renseigné"), new Choice("gt", "Nombre supérieur à"), new Choice("lt", "Nombre inférieur à"), new Choice("before", "Date avant"), new Choice("after", "Date après") }, "Key", "Label", 190);
        PilotControls.Column(_filterGrid, "Valeur", "Value", -1); _filterGrid.ItemsSource = _filters;
        Column(_sortGrid, "Colonne", "Column", choices, "Index", "Label", -1);
        Column(_sortGrid, "Interprétation", "Type", new[] { new Choice("text", "Texte"), new Choice("number", "Nombre"), new Choice("date", "Date") }, "Key", "Label", 160);
        _sortGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "Décroissant", Binding = new Binding("Descending"), Width = 120 }); _sortGrid.ItemsSource = _sorts;
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Filtres de colonnes", Content = WithActions(_filterGrid, () => { if (_filters.Count < 12) _filters.Add(new()); }, () => { if (_filterGrid.SelectedItem is PreviewColumnFilter filter) _filters.Remove(filter); }) });
        tabs.Items.Add(new TabItem { Header = "Ordre des lignes", Content = WithActions(_sortGrid, () => { if (_sorts.Count < 4) _sorts.Add(new() { Column = choices.FirstOrDefault(choice => !_sorts.Any(sort => sort.Column == choice.Index))?.Index ?? 0 }); }, () => { if (_sortGrid.SelectedItem is PreviewSortRule sort) _sorts.Remove(sort); }, () => { if (_sortGrid.SelectedItem is PreviewSortRule sort && _sorts.IndexOf(sort) > 0) _sorts.Move(_sorts.IndexOf(sort), _sorts.IndexOf(sort) - 1); }) });
        var top = new StackPanel(); var presets = new WrapPanel(); presets.Children.Add(_saved);
        presets.Children.Add(PilotControls.Button("Charger la vue", LoadSelected)); presets.Children.Add(PilotControls.Button("Supprimer la vue", () => Safe(() => { if (_saved.SelectedItem is NamedPreview view) { _store.Remove(view.Id); RefreshList(); _state.Text = "Vue supprimée."; } })));
        presets.Children.Add(PilotControls.Button("Remettre à zéro les filtres et tris", () => { _filters.Clear(); _sorts.Clear(); _search.Text = ""; _id = Guid.NewGuid().ToString("N"); _name.Text = "Ma vue"; }));
        top.Children.Add(presets); top.Children.Add(PilotControls.Text("Nom de la vue", 12, true)); top.Children.Add(_name); top.Children.Add(PilotControls.Text("Recherche dans toutes les colonnes", 12, true)); top.Children.Add(_search);
        top.Children.Add(PilotControls.Text("Les filtres se cumulent (ET). Le tri porte sur toutes les lignes correspondantes avant la limite d’affichage. Les valeurs vides ou illisibles viennent en dernier. Les dates suivent la culture du relevé : " + culture.Name + ".", 12, true)); top.Children.Add(_state);
        var body = new DockPanel(); DockPanel.SetDock(top, Dock.Top); body.Children.Add(top); body.Children.Add(tabs);
        var actions = new WrapPanel(); actions.Children.Add(PilotControls.Button("Appliquer", () => Safe(() => Applied?.Invoke(Read()))));
        actions.Children.Add(PilotControls.Button("Mémoriser et appliquer", () => Safe(() => { var view = Read(); _store.Save(view); Applied?.Invoke(view); })));
        Content = PilotControls.Page("Vues du relevé", "Filtres, tris, recherche et disposition des colonnes mémorisés pour ce format de relevé.", body, actions);
        foreach (var item in current.Filters) _filters.Add(item.Copy()); foreach (var item in current.Sorts) _sorts.Add(item.Copy()); _search.Text = search;
        Safe(RefreshList);
    }
    private NamedPreview Read()
    {
        _filterGrid.CommitEdit(DataGridEditingUnit.Cell, true); _filterGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _sortGrid.CommitEdit(DataGridEditingUnit.Cell, true); _sortGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var transform = new PreviewTransform { Filters = _filters.Select(value => value.Copy()).ToList(), Sorts = _sorts.Select(value => value.Copy()).ToList() }; transform.Validate(_headers.Count);
        foreach (var filter in transform.Filters)
        {
            if ((filter.Operator is "gt" or "lt") && !decimal.TryParse(filter.Value, NumberStyles.Number, _culture, out _)) throw new InvalidOperationException("Indiquez un nombre valide pour le filtre numérique.");
            if ((filter.Operator is "before" or "after") && !DateTime.TryParse(filter.Value, _culture, DateTimeStyles.AllowWhiteSpaces, out _)) throw new InvalidOperationException("Indiquez une date valide pour le filtre de dates.");
        }
        if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text.Length > 120) throw new InvalidOperationException("Le nom de la vue doit contenir de 1 à 120 caractères.");
        return new() { Id = _id, Name = _name.Text.Trim(), Search = _search.Text.Trim(), HeaderFingerprint = PreviewViewPreferences.Fingerprint(_headers), ColumnsCount = _headers.Count, Transform = transform, Columns = _columns.Select(column => column.Copy()).ToArray() };
    }
    private void RefreshList() => _saved.ItemsSource = _store.List(PreviewViewPreferences.Fingerprint(_headers));
    private void LoadSelected() => Safe(() =>
    {
        if (_saved.SelectedItem is not NamedPreview view) return;
        _id = view.Id; _name.Text = view.Name; _search.Text = view.Search; _columns = view.Columns.Select(column => column.Copy()).ToArray();
        _filters.Clear(); _sorts.Clear(); foreach (var item in view.Transform.Filters) _filters.Add(item.Copy()); foreach (var item in view.Transform.Sorts) _sorts.Add(item.Copy());
        _state.Text = "Vue chargée. Appliquez pour retrouver aussi ses colonnes.";
    });
    private void Safe(Action action) { try { action(); } catch (Exception error) { _state.Text = AppPaths.SafeError(error); } }
    private static DockPanel WithActions(DataGrid grid, Action add, Action remove, Action? move = null)
    {
        var actions = new WrapPanel(); actions.Children.Add(PilotControls.Button("Ajouter", add)); actions.Children.Add(PilotControls.Button("Retirer", remove)); if (move != null) actions.Children.Add(PilotControls.Button("Monter le niveau", move));
        var panel = new DockPanel(); DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions); panel.Children.Add(grid); return panel;
    }
    private static void Column(DataGrid grid, string title, string field, System.Collections.IEnumerable items, string key, string label, double width)
        => grid.Columns.Add(new DataGridComboBoxColumn { Header = title, SelectedValueBinding = new Binding(field) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, SelectedValuePath = key, DisplayMemberPath = label, ItemsSource = items, Width = width < 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new(width) });
    private sealed record Field(int Index, string Label);
    private sealed record Choice(string Key, string Label);
}
