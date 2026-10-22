using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class MappingAssistantView : UserControl
{
    public sealed class MappingRow { public string Role { get; set; } = ""; public string Label { get; set; } = ""; public string Header { get; set; } = ""; public string Suggested { get; set; } = ""; }
    public sealed class DictionaryRow { public string Field { get; set; } = "category"; public string Original { get; set; } = ""; public string Normalized { get; set; } = ""; }
    private readonly ExtractionResult? _source;
    private readonly DashboardSettings _settings;
    private readonly ObservableCollection<MappingRow> _mapping;
    private readonly ObservableCollection<DictionaryRow> _dictionary;
    private readonly TabControl _steps = new();
    private readonly DataGrid _mappingGrid = PilotControls.Table(editable: true), _dictionaryGrid = PilotControls.Table(editable: true), _preview = PilotControls.Table();
    private readonly TextBox _dates = PilotControls.Input("fr-FR"), _numbers = PilotControls.Input("fr-FR"), _zone = PilotControls.Input(""), _separators = PilotControls.Input(";\\n|");
    private readonly TextBlock _state = PilotControls.Text("", 12, true);
    public event Action<DashboardSettings>? ApplyRequested;
    public MappingAssistantView(ExtractionResult? source, DashboardSettings settings, SchemaChange? change = null,
        bool readOnly = false, string applyLabel = "Appliquer après vérification", string scopeHint = "")
    {
        _source = source; _settings = settings.Copy(); var headers = source?.Headers ?? new();
        _mapping = new(DashboardService.ResolveColumns(headers, settings).Select(c => new MappingRow { Role = c.Role, Label = c.Label, Header = settings.ColumnMappings.GetValueOrDefault(c.Role) ?? "", Suggested = c.Header.Length > 0 ? c.Header + " · " + c.State : "Champ non reconnu" }));
        _dictionary = new(settings.ValueDictionaries.SelectMany(pair => pair.Value.Select(value => new DictionaryRow { Field = pair.Key, Original = value.Key, Normalized = value.Value })));
        _mappingGrid.ItemsSource = _mapping; PilotControls.Column(_mappingGrid, "Rôle", "Label", 210); _mappingGrid.Columns[0].IsReadOnly = true;
        _mappingGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Colonne choisie (vide = automatique)", SelectedItemBinding = new Binding("Header"), ItemsSource = new[] { "" }.Concat(headers).Distinct().ToArray(), Width = 270 });
        PilotControls.Column(_mappingGrid, "Proposition et état", "Suggested", -1); _mappingGrid.Columns[2].IsReadOnly = true;
        var mappingPanel = new DockPanel();
        if (change?.Changed == true)
        {
            var notice = new StackPanel(); notice.Children.Add(PilotControls.Text("Structure modifiée · ajoutées : " + string.Join(", ", change.Added) + " · retirées : " + string.Join(", ", change.Removed), 12, true));
            if (change.ProposedRenames.Count > 0)
            {
                var proposals = new ComboBox { ItemsSource = change.ProposedRenames.Select(p => new RenameChoice(p, p.Previous + " → " + p.Current + $" ({p.Similarity:P0})")).ToArray(), DisplayMemberPath = "Label", SelectedIndex = 0 };
                notice.Children.Add(proposals); notice.Children.Add(PilotControls.Button("Reprendre ce renommage proposé", () =>
                { if (proposals.SelectedItem is RenameChoice choice) { foreach (var row in _mapping.Where(r => r.Header == choice.Rename.Previous || r.Suggested.StartsWith(choice.Rename.Previous + " ·", StringComparison.Ordinal))) row.Header = choice.Rename.Current; _mappingGrid.Items.Refresh(); } }));
            }
            DockPanel.SetDock(notice, Dock.Top); mappingPanel.Children.Add(notice);
        }
        mappingPanel.Children.Add(_mappingGrid); mappingPanel.IsEnabled = !readOnly; _steps.Items.Add(new TabItem { Header = "1 · Reconnaître les champs", Content = mappingPanel });
        var formats = new StackPanel();
        void Add(string label, UIElement control) { formats.Children.Add(PilotControls.Text(label, 12, true)); formats.Children.Add(control); }
        Add("Culture des dates (ex. fr-FR, en-US)", _dates); Add("Culture des nombres", _numbers); Add("Fuseau de la source (vide = dates locales sans conversion ; ex. Europe/Paris)", _zone); Add("Séparateurs de valeurs multiples (\\n = retour à la ligne, \\t = tabulation)", _separators);
        _dates.Text = settings.DateCulture; _numbers.Text = settings.NumberCulture; _zone.Text = settings.SourceTimeZone; _separators.Text = settings.MultiValueSeparators.Replace("\n", "\\n").Replace("\t", "\\t");
        _dictionaryGrid.Height = 230; _dictionaryGrid.ItemsSource = _dictionary;
        _dictionaryGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Rôle ou colonne", SelectedItemBinding = new Binding("Field"), ItemsSource = DashboardService.Roles.Select(r => r.Key).Concat(headers).Distinct().ToArray(), Width = 180 });
        PilotControls.Column(_dictionaryGrid, "Valeur observée", "Original", -1); PilotControls.Column(_dictionaryGrid, "Valeur interprétée", "Normalized", -1);
        Add("Dictionnaires explicites · les cellules d’origine ne sont jamais réécrites", _dictionaryGrid);
        var dictionaryTools = new WrapPanel(); dictionaryTools.Children.Add(PilotControls.Button("Ajouter une correspondance", () => _dictionary.Add(new()))); dictionaryTools.Children.Add(PilotControls.Button("Retirer", () => { if (_dictionaryGrid.SelectedItem is DictionaryRow row) _dictionary.Remove(row); })); formats.Children.Add(dictionaryTools);
        formats.IsEnabled = !readOnly;
        _steps.Items.Add(new TabItem { Header = "2 · Formats et dictionnaires", Content = new ScrollViewer { Content = formats, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        foreach (var column in new[] { ("Ligne", "Row", 60d), ("Rôle", "Role", 155d), ("Colonne", "Header", 160d), ("Valeur brute", "Raw", -1d), ("Valeur interprétée", "Interpreted", -1d) }) PilotControls.Column(_preview, column.Item1, column.Item2, column.Item3);
        _steps.Items.Add(new TabItem { Header = "3 · Vérifier les cellules", Content = _preview });
        _steps.SelectionChanged += (_, e) => { if (e.Source == _steps && _steps.SelectedIndex == 2) RefreshPreview(); };
        var body = new DockPanel(); DockPanel.SetDock(_state, Dock.Bottom); body.Children.Add(_state); body.Children.Add(_steps);
        var toolbar = new WrapPanel(); toolbar.Children.Add(PilotControls.Button("Précédent", () => _steps.SelectedIndex = Math.Max(0, _steps.SelectedIndex - 1))); toolbar.Children.Add(PilotControls.Button("Suivant / aperçu", () => _steps.SelectedIndex = Math.Min(2, _steps.SelectedIndex + 1)));
        if (!readOnly) toolbar.Children.Add(PilotControls.Button(applyLabel, () => { try { var draft = Read(); if (_steps.SelectedIndex != 2) { _steps.SelectedIndex = 2; _state.Text = "Relisez l’aperçu, puis cliquez de nouveau pour appliquer."; return; } ApplyRequested?.Invoke(draft); } catch (Exception error) { _state.Text = AppPaths.SafeError(error); } }));
        Content = PilotControls.Page("Correspondances et formats de la source", scopeHint.Length > 0 ? scopeHint : "Reconnaissez les rôles, fixez les conventions puis examinez un échantillon des cellules.", body, toolbar);
        if (readOnly) _steps.SelectedIndex = 2;
    }
    private DashboardSettings Read()
    {
        foreach (var grid in new[] { _mappingGrid, _dictionaryGrid }) { grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true); }
        var draft = _settings.Copy(); draft.ColumnMappings = _mapping.Where(r => r.Header.Length > 0).ToDictionary(r => r.Role, r => r.Header, StringComparer.OrdinalIgnoreCase);
        draft.DateCulture = _dates.Text.Trim(); draft.NumberCulture = _numbers.Text.Trim(); draft.SourceTimeZone = _zone.Text.Trim(); draft.MultiValueSeparators = _separators.Text.Replace("\\n", "\n").Replace("\\t", "\t");
        if (_dictionary.Count > 10000 || _dictionary.Any(r => string.IsNullOrWhiteSpace(r.Field) || string.IsNullOrWhiteSpace(r.Original) || r.Original.Length > 1000 || r.Normalized.Length > 1000)) throw new InvalidOperationException("Chaque correspondance doit préciser un champ et une valeur d’origine, avec au maximum 1 000 caractères par valeur et 10 000 correspondances.");
        if (_dictionary.Select(r => r.Field).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 60 || _dictionary.GroupBy(r => r.Field, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 2000)) throw new InvalidOperationException("Au maximum 60 dictionnaires et 2 000 correspondances par champ sont acceptés.");
        if (_dictionary.GroupBy(r => r.Field + "\u001f" + r.Original, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new InvalidOperationException("Une même valeur d’origine possède plusieurs correspondances dans un champ.");
        draft.ValueDictionaries = _dictionary.GroupBy(r => r.Field, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Original, r => r.Normalized, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        DashboardService.ValidateSettings(draft); return draft;
    }
    private void RefreshPreview()
    {
        try
        {
            var settings = Read(); var rows = new List<object>();
            if (_source != null) foreach (var mapping in DashboardService.ResolveColumns(_source.Headers, settings).Where(m => m.Index != null))
                foreach (var index in Enumerable.Range(0, Math.Min(5, _source.Rows.Count)))
                { var raw = mapping.Index!.Value < _source.Rows[index].Length ? _source.Rows[index][mapping.Index.Value] : ""; rows.Add(new { Row = index + 1, Role = mapping.Label, mapping.Header, Raw = raw, Interpreted = Interpret(raw, mapping.Role, mapping.Header, settings, _source.ExtractedAt.Offset) }); }
            _preview.ItemsSource = rows; _state.Text = _source == null ? "Aucun relevé chargé : les formats seront vérifiés sur la prochaine collecte." : "Aperçu des cinq premières lignes. Une donnée non interprétable reste affichée comme telle.";
        }
        catch (Exception error) { _state.Text = AppPaths.SafeError(error); _preview.ItemsSource = null; }
    }
    public static string Interpret(string raw, string role, string header, DashboardSettings settings, TimeSpan offset)
    {
        var value = DashboardService.NormalizeValue(raw, role, header, settings);
        if (role is "created" or "updated" or "resolved" or "closed" or "due") return DashboardService.TrySourceDate(value, settings, offset, out var date) ? date.ToString("yyyy-MM-dd HH:mm:ss") : value.Length == 0 ? "Valeur absente" : "Date non interprétable";
        if (role == "assignee") return string.Join(" | ", DashboardService.SplitTechnicianNames(value, settings.MultiValueSeparators));
        if (role == "satisfaction") return DashboardService.TrySourceNumber(value, settings, out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "Nombre non interprétable";
        return value;
    }
    private sealed record RenameChoice(HeaderRename Rename, string Label);
}
