using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;
using Microsoft.Win32;

namespace GlpiPilot.UI;

public sealed partial class CitySettingsView
{
    private readonly DataGrid _rulesGrid = ReferenceGrid(), _sitesGrid = ReferenceGrid(), _proofGrid = ReferenceGrid(true), _unlocatedGrid = ReferenceGrid(true), _importGrid = ReferenceGrid(true);
    private readonly ObservableCollection<SiteRow> _siteRows = new();
    private readonly TextBlock _selectedCityLabel = Label("Choisissez une ville dans le dictionnaire.", 13, "MutedBrush");
    private readonly StackPanel _cityMetadata = new(), _ruleDetails = new(), _siteDetails = new();
    private readonly TextBox _testTitle = new() { MaxLength = 20_000, MinHeight = 56, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _testContexts = new() { MaxLength = 4000, MinHeight = 52, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _testResult = Label("Le test utilise le brouillon actuel sans enregistrer votre titre.", 12, "MutedBrush");
    private readonly TextBlock _importNote = Label("Importez le modèle CSV pour comparer les ajouts et modifications avant leur application au brouillon. Les lignes absentes du CSV sont conservées.", 12, "MutedBrush");
    private readonly ComboBox _correctionCity = new() { MinWidth = 160, MaxWidth = 260, DisplayMemberPath = "Name", SelectedValuePath = "Id", Margin = new Thickness(0, 0, 8, 6) };
    private readonly ComboBox _correctionSite = new() { MinWidth = 160, MaxWidth = 260, DisplayMemberPath = "Name", SelectedValuePath = "Id", Margin = new Thickness(0, 0, 8, 6) };
    private readonly List<Button> _advancedButtons = new();
    private Button? _applyImport;
    private CityEnrichmentResult? _advancedReport;
    private CityReferencePreview? _csvPreview;
    private bool _loadingAdvanced;
    public event Action<int, string, string>? LocalCorrectionRequested;

    private FrameworkElement BuildAdvancedReference()
    {
        var tabs = new TabControl { MinHeight = 440, Margin = new Thickness(0, 14, 0, 0) };
        var rules = new Grid(); rules.RowDefinitions.Add(new() { Height = GridLength.Auto }); rules.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); rules.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); header.Children.Add(_selectedCityLabel);
        var hierarchy = new WrapPanel();
        foreach (var (name, label) in new[] { (nameof(CityDefinition.Region), "Région"), (nameof(CityDefinition.Department), "Département"), (nameof(CityDefinition.Sector), "Secteur") }) hierarchy.Children.Add(ReferenceField(label, name, 190));
        _cityMetadata.Children.Add(hierarchy); _cityMetadata.Children.Add(ReferenceField("Exclusions de la ville · termes séparés par ;", "Excluded", 580));
        header.Children.Add(_cityMetadata);
        header.Children.Add(Label("Un terme présent dans les règles remplace son alias simple. Tous les contextes requis doivent être présents ; une exclusion suffit à refuser. La priorité la plus élevée départage les villes ; à égalité, le lieu reste ambigu.", 12, "MutedBrush"));
        var commands = new WrapPanel { Margin = new Thickness(0, 7, 0, 4) };
        commands.Children.Add(AdvancedCommand("Ajouter une règle", () =>
        {
            if (_grid.SelectedItem is not CityRow city) throw new InvalidDataException("Sélectionnez d’abord une ville.");
            if (city.Advanced.Rules.Count >= CitySettings.MaximumRules) throw new InvalidDataException("Une ville accepte au maximum 16 règles.");
            var rule = new CityAliasRule(); city.Advanced.Rules.Add(rule); LoadSelectedCityAdvanced(); _rulesGrid.SelectedIndex = city.Advanced.Rules.Count - 1; MarkDirty();
        }));
        commands.Children.Add(AdvancedCommand("Retirer la règle", () =>
        {
            if (_grid.SelectedItem is CityRow city && _rulesGrid.SelectedItem is RuleRow rule) { city.Advanced.Rules.Remove(rule.Definition); LoadSelectedCityAdvanced(); MarkDirty(); }
        })); header.Children.Add(commands); rules.Children.Add(header);
        RefColumn(_rulesGrid, "Terme / alias", nameof(RuleRow.Term), 200); RefColumn(_rulesGrid, "Priorité (0–100)", nameof(RuleRow.Priority), 115); RefColumn(_rulesGrid, "Colonne de contexte · vide = titre", nameof(RuleRow.ContextColumn), 270);
        Grid.SetRow(_rulesGrid, 1); rules.Children.Add(_rulesGrid);
        _ruleDetails.Children.Add(ReferenceField("Tous ces termes requis dans le contexte · séparés par ;", nameof(RuleRow.Required), 620));
        _ruleDetails.Children.Add(ReferenceField("Aucun de ces termes dans le contexte · séparés par ;", nameof(RuleRow.Excluded), 620));
        Grid.SetRow(_ruleDetails, 2); rules.Children.Add(_ruleDetails); _rulesGrid.SelectionChanged += (_, _) => _ruleDetails.DataContext = _rulesGrid.SelectedItem;
        tabs.Items.Add(new TabItem { Header = "Règles et hiérarchie", Content = rules });

        var sites = new Grid(); sites.RowDefinitions.Add(new() { Height = GridLength.Auto }); sites.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); sites.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var siteHeader = new StackPanel(); siteHeader.Children.Add(Label("Un site appartient à une ville. Ses noms et alias peuvent localiser un ticket. Des sites homonymes ou plusieurs sites dans un titre restent signalés comme ambigus.", 12, "MutedBrush"));
        var siteActions = new WrapPanel { Margin = new Thickness(0, 7, 0, 4) };
        siteActions.Children.Add(AdvancedCommand("Ajouter un site", () => { if (_siteRows.Count >= CitySettings.MaximumSites) throw new InvalidDataException("La limite est de 2 000 sites."); var row = new SiteRow { CityId = (_grid.SelectedItem as CityRow)?.Id ?? _rows.FirstOrDefault()?.Id ?? "" }; _siteRows.Add(row); _sitesGrid.SelectedItem = row; MarkDirty(); }));
        siteActions.Children.Add(AdvancedCommand("Supprimer le site", () => { if (_sitesGrid.SelectedItem is SiteRow row) { _siteRows.Remove(row); MarkDirty(); } })); siteHeader.Children.Add(siteActions); sites.Children.Add(siteHeader);
        _sitesGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Ville", ItemsSource = _rows, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValueBinding = new Binding(nameof(SiteRow.CityId)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 180 });
        RefColumn(_sitesGrid, "Nom du site", nameof(SiteRow.Name), 210); RefColumn(_sitesGrid, "Alias · séparés par ;", nameof(SiteRow.Aliases), 240);
        _sitesGrid.ItemsSource = _siteRows; Grid.SetRow(_sitesGrid, 1); sites.Children.Add(_sitesGrid);
        _siteDetails.Children.Add(ReferenceField("Adresse de référence", nameof(SiteRow.Address), 630));
        var coordinates = new WrapPanel(); coordinates.Children.Add(ReferenceField("Latitude", nameof(SiteRow.Latitude), 210)); coordinates.Children.Add(ReferenceField("Longitude", nameof(SiteRow.Longitude), 210)); _siteDetails.Children.Add(coordinates);
        Grid.SetRow(_siteDetails, 2); sites.Children.Add(_siteDetails); _sitesGrid.SelectionChanged += (_, _) => _siteDetails.DataContext = _sitesGrid.SelectedItem;
        tabs.Items.Add(new TabItem { Header = "Sites", Content = sites });

        var test = new Grid(); test.RowDefinitions.Add(new() { Height = GridLength.Auto }); test.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var testHeader = new StackPanel(); testHeader.Children.Add(Label("Titre à simuler", 12, "MutedBrush")); testHeader.Children.Add(_testTitle);
        testHeader.Children.Add(Label("Contextes facultatifs · une ligne En-tête exact=valeur", 12, "MutedBrush", margin: new Thickness(0, 6, 0, 3))); testHeader.Children.Add(_testContexts);
        var testActions = new WrapPanel { Margin = new Thickness(0, 6, 0, 4) }; testActions.Children.Add(AdvancedCommand("Tester le titre", TestReference));
        testActions.Children.Add(AdvancedCommand("Vérifier les conflits du dictionnaire", () => { var warnings = CityDetectionService.InspectDictionary(ReadSettings()); _testResult.Text = warnings.Count == 0 ? "Aucun terme partagé ni alias de deux caractères détecté." : string.Join("\n", warnings.Take(30)); }));
        testHeader.Children.Add(testActions); testHeader.Children.Add(_testResult); test.Children.Add(testHeader);
        RefColumn(_proofGrid, "Ville", "CityName", 150); RefColumn(_proofGrid, "Terme", "Term", 120); RefColumn(_proofGrid, "Décision", "Decision", 120); RefColumn(_proofGrid, "Preuve", "Explanation", 360);
        Grid.SetRow(_proofGrid, 1); test.Children.Add(_proofGrid); tabs.Items.Add(new TabItem { Header = "Simuler et expliquer", Content = test });

        var review = new Grid(); review.RowDefinitions.Add(new() { Height = GridLength.Auto }); review.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); review.RowDefinitions.Add(new() { Height = GridLength.Auto });
        review.Children.Add(Label("À vérifier : titres non localisés, ambigus, sans titre ou sites ambigus. Les corrections sont locales au poste et à l’installation GLPI ; elles ne modifient pas les tickets ni la configuration partagée.", 12, "MutedBrush"));
        RefColumn(_unlocatedGrid, "ID", "Id", 75); RefColumn(_unlocatedGrid, "Titre", "Title", 290); RefColumn(_unlocatedGrid, "État", "State", 130); RefColumn(_unlocatedGrid, "Explication", "Explanation", 360);
        Grid.SetRow(_unlocatedGrid, 1); review.Children.Add(_unlocatedGrid);
        var correction = new StackPanel(); var choices = new WrapPanel(); _correctionCity.ItemsSource = _rows; choices.Children.Add(_correctionCity); choices.Children.Add(_correctionSite);
        _correctionCity.SelectionChanged += (_, _) => { _correctionSite.ItemsSource = _siteRows.Where(site => site.CityId == (string?)_correctionCity.SelectedValue).ToArray(); _correctionSite.SelectedIndex = -1; };
        choices.Children.Add(AdvancedCommand("Corriger localement", () => { if (_unlocatedGrid.SelectedItem is not ReviewRow row || _correctionCity.SelectedItem is not CityRow city) throw new InvalidDataException("Sélectionnez une ligne et une ville."); LocalCorrectionRequested?.Invoke(row.Index, city.Id, (_correctionSite.SelectedItem as SiteRow)?.Id ?? ""); }));
        choices.Children.Add(AdvancedCommand("Revenir à la détection", () => { if (_unlocatedGrid.SelectedItem is ReviewRow row) LocalCorrectionRequested?.Invoke(row.Index, "", ""); }));
        choices.Children.Add(AdvancedCommand("Copier vers la simulation", CopyReviewToTest)); correction.Children.Add(choices);
        Grid.SetRow(correction, 2); review.Children.Add(correction); tabs.Items.Add(new TabItem { Header = "Lieux à vérifier", Content = review });

        var import = new Grid(); import.RowDefinitions.Add(new() { Height = GridLength.Auto }); import.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var importHeader = new StackPanel(); importHeader.Children.Add(_importNote); var importActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        importActions.Children.Add(AdvancedCommand("Choisir un CSV…", PreviewReferenceImport)); importActions.Children.Add(AdvancedCommand("Exporter le référentiel CSV…", ExportReference));
        _applyImport = AdvancedCommand("Appliquer ce comparatif au brouillon", () => { if (_csvPreview == null) return; var settings = _csvPreview.Settings; LoadSettings(settings); _csvPreview = null; _importGrid.ItemsSource = null; MarkDirty(); _importNote.Text = "Comparatif appliqué au brouillon. Enregistrez pour recalculer les relevés."; UpdateAdvancedReferenceEnabled(); });
        importActions.Children.Add(_applyImport); importHeader.Children.Add(importActions); import.Children.Add(importHeader);
        RefColumn(_importGrid, "Action", "Kind", 120); RefColumn(_importGrid, "Type", "Type", 90); RefColumn(_importGrid, "Nom", "Name", 200); RefColumn(_importGrid, "Changements", "Detail", 380);
        Grid.SetRow(_importGrid, 1); import.Children.Add(_importGrid); tabs.Items.Add(new TabItem { Header = "Référentiel CSV", Content = import });
        foreach (var grid in new[] { _rulesGrid, _sitesGrid }) grid.CellEditEnding += (_, _) => MarkDirty();
        return tabs;
    }

    private void LoadAdvancedReference(CitySettings settings)
    {
        _siteRows.Clear(); foreach (var site in settings.Sites) _siteRows.Add(new SiteRow { Id = site.Id, CityId = site.CityId, Name = site.Name, Aliases = string.Join(" ; ", site.Aliases), Address = site.Address, Latitude = FormatCoordinate(site.Latitude), Longitude = FormatCoordinate(site.Longitude) });
        _sitesGrid.SelectedItem = _siteRows.FirstOrDefault(); _csvPreview = null; _importGrid.ItemsSource = null; _proofGrid.ItemsSource = null;
        _testResult.Text = "Le test utilise le brouillon actuel sans enregistrer votre titre."; UpdateAdvancedReferenceEnabled();
    }
    private void LoadSelectedCityAdvanced()
    {
        _loadingAdvanced = true;
        try
        {
            var city = _grid.SelectedItem as CityRow; _selectedCityLabel.Text = city == null ? "Choisissez une ville dans le dictionnaire." : "Ville sélectionnée : " + city.Name;
            _cityMetadata.DataContext = city == null ? null : new CityMetadataRow(city.Advanced);
            _rulesGrid.ItemsSource = city?.Advanced.Rules.Select(rule => new RuleRow(rule)).ToArray(); _rulesGrid.SelectedIndex = city?.Advanced.Rules.Count > 0 ? 0 : -1;
        }
        finally { _loadingAdvanced = false; }
    }
    private void CommitAdvanced()
    {
        foreach (var grid in new[] { _rulesGrid, _sitesGrid })
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) throw new InvalidDataException("Terminez la cellule des règles ou sites avant de poursuivre.");
    }
    private List<SiteDefinition> ReadSites() => _siteRows.Select((site, index) => new SiteDefinition
    {
        Id = site.Id, CityId = site.CityId, Name = site.Name.Trim(), Aliases = ParseAliases(site.Aliases), Address = site.Address.Trim(),
        Latitude = ReadCoordinate(site.Latitude.Trim(), 90, "latitude", site.Name, index + 1), Longitude = ReadCoordinate(site.Longitude.Trim(), 180, "longitude", site.Name, index + 1)
    }).ToList();
    private void TestReference()
    {
        var contexts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in _testContexts.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('=', 2); if (parts.Length != 2 || !contexts.TryAdd(parts[0], parts[1])) throw new InvalidDataException("Chaque contexte doit être une colonne unique suivie de = puis de sa valeur.");
        }
        var match = CityDetectionService.ExplainTitle(_testTitle.Text, ReadSettings(), contexts);
        _testResult.Text = match.Explanation + (match.IsOverride ? " · correction locale" : ""); _proofGrid.ItemsSource = match.Evidence;
    }
    private void RefreshUnlocatedQueue()
    {
        var report = _advancedReport; if (report == null) { _unlocatedGrid.ItemsSource = null; return; }
        var title = report.Source.Headers.IndexOf(report.TitleColumnName);
        var id = DashboardService.ResolveColumns(report.Source.Headers, new()).First(column => column.Role == "id").Index;
        string Cell(int row, int? column) => column is int position && position >= 0 && report.Source.Rows[row] is { } cells && position < cells.Length ? cells[position] : "";
        _unlocatedGrid.ItemsSource = report.Matches.Where(match => match.State != CityMatchState.Matched || match.SiteState == "ambiguous" || match.IsOverride)
            .Select(match => new ReviewRow(match.RowIndex, Cell(match.RowIndex, id), Cell(match.RowIndex, title), match.IsOverride ? "Correction locale" : match.State.ToString(), match.Explanation)).ToArray();
    }
    private void CopyReviewToTest()
    {
        if (_unlocatedGrid.SelectedItem is not ReviewRow row || _advancedReport == null) return;
        _testTitle.Text = row.Title;
        var cells = _advancedReport.Source.Rows[row.Index]; var columns = _rows.SelectMany(city => city.Advanced.Rules).Select(rule => rule.ContextColumn).Where(column => column.Length > 0).Distinct(StringComparer.Ordinal);
        _testContexts.Text = string.Join("\n", columns.Select(column => (Column: column, Index: _advancedReport.Source.Headers.IndexOf(column)))
            .Where(item => item.Index >= 0 && item.Index < cells.Length).Select(item => item.Column + "=" + cells[item.Index].Replace('\n', ' ').Replace('\r', ' ')));
        TestReference(); SetStatus("Ligne envoyée dans « Simuler et expliquer ».", "MutedBrush");
    }
    private void PreviewReferenceImport()
    {
        var dialog = new OpenFileDialog { Title = "Comparer un référentiel de villes et sites", Filter = "CSV (*.csv)|*.csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (new FileInfo(dialog.FileName).Length > 8 * 1024 * 1024) throw new InvalidDataException("Le référentiel CSV dépasse 8 Mio.");
        var csv = NativeCsvImportService.Read(dialog.FileName, "", maxRows: CitySettings.MaximumCities + CitySettings.MaximumSites + 1);
        _csvPreview = CityReferenceService.Preview(csv, ReadSettings()); _importGrid.ItemsSource = _csvPreview.Changes;
        _importNote.Text = $"Comparatif : {_csvPreview.Changes.Count(change => change.Kind == "Ajout")} ajouts, {_csvPreview.Changes.Count(change => change.Kind == "Modification")} modifications, {_csvPreview.Changes.Count(change => change.Kind == "Identique")} identiques. Aucun enregistrement effectué.";
        UpdateAdvancedReferenceEnabled();
    }
    private void ExportReference()
    {
        var data = CityReferenceService.Export(ReadSettings());
        var dialog = new SaveFileDialog { Title = "Exporter le référentiel et son modèle de colonnes", Filter = "CSV (*.csv)|*.csv", FileName = "GLPI-Referentiel-villes-sites.csv", DefaultExt = ".csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { ExportService.WriteCsv(data, dialog.FileName); SetStatus("Référentiel CSV enregistré.", "MutedBrush"); }
    }
    private void UpdateAdvancedReferenceEnabled()
    {
        foreach (var button in _advancedButtons) button.IsEnabled = !_busy;
        if (_applyImport != null) _applyImport.IsEnabled = !_busy && _csvPreview != null;
        _rulesGrid.IsReadOnly = _sitesGrid.IsReadOnly = _busy; _cityMetadata.IsEnabled = _ruleDetails.IsEnabled = _siteDetails.IsEnabled = !_busy;
    }
    private Button AdvancedCommand(string label, Action action)
    {
        var button = Command(label); button.Click += (_, _) =>
        {
            if (_busy) return; try { action(); } catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            { SetStatus(error.Message, "ErrorBrush"); }
        }; _advancedButtons.Add(button); return button;
    }
    private FrameworkElement ReferenceField(string label, string path, double width)
    {
        var panel = new StackPanel { MinWidth = Math.Min(width, 150), MaxWidth = width, Margin = new Thickness(0, 6, 10, 6) };
        panel.Children.Add(Label(label, 12, "MutedBrush")); var text = new TextBox { MaxLength = 1800, Margin = new Thickness(0, 4, 0, 0) };
        text.SetBinding(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        text.TextChanged += (_, _) => { if (!_loadingAdvanced) MarkDirty(); }; panel.Children.Add(text); return panel;
    }
    private static DataGrid ReferenceGrid(bool readOnly = false) => new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, IsReadOnly = readOnly,
        RowHeight = 34, MinHeight = 100, MaxHeight = 300, EnableRowVirtualization = true, EnableColumnVirtualization = true,
        SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(0, 6, 0, 6)
    };
    private void RefColumn(DataGrid grid, string title, string path, double width)
    {
        var editing = new Style(typeof(TextBox), TryFindResource(typeof(TextBox)) as Style); editing.Setters.Add(new Setter(Control.MinHeightProperty, 24d)); editing.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3, 1, 3, 1)));
        grid.Columns.Add(new DataGridTextColumn { Header = title, Width = width, EditingElementStyle = editing, Binding = new Binding(path) { Mode = grid.IsReadOnly ? BindingMode.OneWay : BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true } });
    }
    private sealed class RuleRow
    {
        public CityAliasRule Definition { get; }
        public RuleRow(CityAliasRule definition) => Definition = definition;
        public string Term { get => Definition.Term; set => Definition.Term = value ?? ""; }
        public int Priority { get => Definition.Priority; set => Definition.Priority = value; }
        public string ContextColumn { get => Definition.ContextColumn; set => Definition.ContextColumn = value ?? ""; }
        public string Required { get => string.Join(" ; ", Definition.RequiredTerms); set => Definition.RequiredTerms = ParseAliases(value ?? ""); }
        public string Excluded { get => string.Join(" ; ", Definition.ExcludedTerms); set => Definition.ExcludedTerms = ParseAliases(value ?? ""); }
    }
    private sealed class CityMetadataRow
    {
        private readonly CityDefinition _city; public CityMetadataRow(CityDefinition city) => _city = city;
        public string Region { get => _city.Region; set => _city.Region = value ?? ""; }
        public string Department { get => _city.Department; set => _city.Department = value ?? ""; }
        public string Sector { get => _city.Sector; set => _city.Sector = value ?? ""; }
        public string Excluded { get => string.Join(" ; ", _city.ExcludedTerms); set => _city.ExcludedTerms = ParseAliases(value ?? ""); }
    }
    private sealed class SiteRow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N"); public string CityId { get; set; } = "";
        public string Name { get; set; } = ""; public string Aliases { get; set; } = ""; public string Address { get; set; } = "";
        public string Latitude { get; set; } = ""; public string Longitude { get; set; } = "";
    }
    private sealed record ReviewRow(int Index, string Id, string Title, string State, string Explanation);
}
