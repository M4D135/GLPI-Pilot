using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class CitySettingsView
{
    private readonly ObservableCollection<BuildingRow> _buildings = new();
    private readonly DataGrid _buildingGrid = ReferenceGrid();
    private readonly ObservableCollection<SourceKind> _sourceKinds = new();
    private readonly ListBox _priorityList = new() { DisplayMemberPath = "Label", MinWidth = 240, MaxHeight = 170 };
    private readonly TextBox _locationField = new() { MaxLength = 120, MinWidth = 220, MaxWidth = 450 };
    private readonly TextBox _correctionReason = new() { MaxLength = 1000, MinWidth = 200, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _promoteTerm = new() { MaxLength = 120, Width = 180 }, _promoteContext = new() { MaxLength = 120, Width = 180 }, _promoteColumn = new() { MaxLength = 120, Width = 180 };
    private readonly TextBlock _highlightTitle = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private FrameworkElement BuildTerritorySources()
    {
        var panel = new StackPanel();
        panel.Children.Add(Label("Sources de localisation dans leur ordre de priorité", 15, "TextBrush"));
        panel.Children.Add(Label("Le premier résultat reconnu ou ambigu arrête la recherche. Les sources vides ou sans correspondance laissent la suivante décider. Le titre complet combine villes, sites et bâtiments ; un champ GLPI peut utiliser la même liste de références.", 12, "MutedBrush"));
        _priorityList.ItemsSource = _sourceKinds; panel.Children.Add(_priorityList);
        var commands = new WrapPanel();
        var choices = new ComboBox { Width = 220, DisplayMemberPath = "Label", ItemsSource = new[] { "manual", "location", "site", "building", "title", "combined" }.Select(key => new SourceKind(key, CityDetectionService.SourceLabel(key))).ToArray(), SelectedIndex = 0 };
        commands.Children.Add(choices);
        commands.Children.Add(AdvancedCommand("Ajouter la source", () => { if (choices.SelectedItem is SourceKind kind && !_sourceKinds.Any(item => item.Key == kind.Key)) { _sourceKinds.Add(kind); MarkDirty(); } }));
        commands.Children.Add(AdvancedCommand("↑", () => MoveSource(-1))); commands.Children.Add(AdvancedCommand("↓", () => MoveSource(1)));
        commands.Children.Add(AdvancedCommand("Retirer", () => { if (_priorityList.SelectedItem is SourceKind item && _sourceKinds.Count > 1) { _sourceKinds.Remove(item); MarkDirty(); } })); panel.Children.Add(commands);
        panel.Children.Add(Label("Nom exact de la colonne GLPI de localisation", 12, "MutedBrush")); panel.Children.Add(_locationField); _locationField.TextChanged += (_, _) => { if (!_loading) MarkDirty(); };
        panel.Children.Add(Label("Bâtiments rattachés aux sites", 15, "TextBrush", margin: new Thickness(0, 12, 0, 4)));
        _buildingGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Site", ItemsSource = _siteRows, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValueBinding = new Binding(nameof(BuildingRow.SiteId)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 180 });
        RefColumn(_buildingGrid, "Bâtiment", nameof(BuildingRow.Name), 180); RefColumn(_buildingGrid, "Alias · séparés par ;", nameof(BuildingRow.Aliases), 220); RefColumn(_buildingGrid, "Étage / zone", nameof(BuildingRow.Floor), 140);
        _buildingGrid.ItemsSource = _buildings; _buildingGrid.CellEditEnding += (_, _) => MarkDirty(); panel.Children.Add(_buildingGrid);
        var buildings = new WrapPanel(); buildings.Children.Add(AdvancedCommand("Ajouter un bâtiment", () => { if (_buildings.Count >= 5000) throw new InvalidDataException("La limite est de 5 000 bâtiments."); var row = new BuildingRow { SiteId = (_sitesGrid.SelectedItem as SiteRow)?.Id ?? _siteRows.FirstOrDefault()?.Id ?? "" }; _buildings.Add(row); _buildingGrid.SelectedItem = row; MarkDirty(); }));
        buildings.Children.Add(AdvancedCommand("Retirer le bâtiment", () => { if (_buildingGrid.SelectedItem is BuildingRow row) { _buildings.Remove(row); MarkDirty(); } })); panel.Children.Add(buildings);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private FrameworkElement BuildPromotion()
    {
        var panel = new StackPanel(); panel.Children.Add(Label("Généraliser une correction · proposer une règle au brouillon", 13, "TextBrush"));
        var fields = new WrapPanel();
        foreach (var (label, field) in new[] { ("Terme à reconnaître", _promoteTerm), ("Contexte requis facultatif", _promoteContext), ("Colonne du contexte · vide = titre", _promoteColumn) })
        { var group = new StackPanel { Margin = new Thickness(0, 5, 10, 5) }; group.Children.Add(Label(label, 11, "MutedBrush")); group.Children.Add(field); fields.Children.Add(group); }
        panel.Children.Add(fields);
        panel.Children.Add(AdvancedCommand("Proposer cette règle", () =>
        {
            if (_unlocatedGrid.SelectedItem is not ReviewRow || _correctionCity.SelectedItem is not CityRow city) throw new InvalidDataException("Sélectionnez le ticket et sa ville de correction.");
            var settings = CityDetectionReview.Promote(ReadSettings(), city.Id, _promoteTerm.Text, _promoteContext.Text, _promoteColumn.Text);
            LoadSettings(settings); MarkDirty(); SetStatus("Règle ajoutée au brouillon. L’enregistrement présentera son impact sur l’ensemble du relevé avant application.", "MutedBrush");
        })); return panel;
    }
    private void MoveSource(int delta)
    {
        var index = _priorityList.SelectedIndex; if (index < 0 || index + delta < 0 || index + delta >= _sourceKinds.Count) return;
        _sourceKinds.Move(index, index + delta); _priorityList.SelectedIndex = index + delta; MarkDirty();
    }
    private void LoadTerrainSettings(CitySettings settings)
    {
        _sourceKinds.Clear(); foreach (var key in settings.SourcePriority) _sourceKinds.Add(new(key, CityDetectionService.SourceLabel(key)));
        _priorityList.SelectedIndex = 0; _locationField.Text = settings.LocationColumn; _buildings.Clear();
        foreach (var building in settings.Buildings) _buildings.Add(new() { Id = building.Id, SiteId = building.SiteId, Name = building.Name, Aliases = string.Join(" ; ", building.Aliases), Floor = building.Floor });
    }
    private List<BuildingDefinition> ReadBuildings() => _buildings.Select(row => new BuildingDefinition { Id = row.Id, SiteId = row.SiteId, Name = row.Name.Trim(), Aliases = ParseAliases(row.Aliases), Floor = row.Floor.Trim() }).ToList();
    private void ShowHighlight(string title, IReadOnlyList<CityMatchEvidence> proof)
    {
        _highlightTitle.Inlines.Clear(); var cursor = 0;
        foreach (var range in CityDetectionReview.Highlights(title, proof))
        {
            if (range.Start > cursor) _highlightTitle.Inlines.Add(new Run(title[cursor..range.Start]));
            var run = new Run(title.Substring(range.Start, range.Length)) { FontWeight = FontWeights.Bold }; run.SetResourceReference(TextElement.BackgroundProperty, "FieldSelectionBrush");
            run.SetResourceReference(TextElement.ForegroundProperty, "TextBrush"); _highlightTitle.Inlines.Add(run); cursor = range.Start + range.Length;
        }
        if (cursor < title.Length) _highlightTitle.Inlines.Add(new Run(title[cursor..]));
    }
    private static readonly string[] DayNames = { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" };
    private static string FormatOpenings(IEnumerable<WeeklyOpening> openings) => string.Join(" ; ", openings.Select(value => DayNames[(int)value.Day] + " " + value.From + "-" + value.Until));
    private static List<WeeklyOpening> ParseOpenings(string text)
    {
        var result = new List<WeeklyOpening>();
        foreach (var value in text.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries); var day = parts.Length == 2 ? Array.FindIndex(DayNames, name => name.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) : -1;
            var times = parts.Length == 2 ? parts[1].Split('-') : Array.Empty<string>();
            if (day < 0 || times.Length != 2) throw new InvalidDataException("Ouverture attendue : lundi 08:00-12:00 ; lundi 13:00-17:00.");
            var opening = new WeeklyOpening { Day = (DayOfWeek)day, From = times[0], Until = times[1] }; if (!opening.IsValid) throw new InvalidDataException("Les horaires d’ouverture doivent être ordonnés au format HH:mm."); result.Add(opening);
        }
        return result;
    }
    private sealed record SourceKind(string Key, string Label);
    private sealed class BuildingRow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N"); public string SiteId { get; set; } = "";
        public string Name { get; set; } = ""; public string Aliases { get; set; } = ""; public string Floor { get; set; } = "";
    }
}
