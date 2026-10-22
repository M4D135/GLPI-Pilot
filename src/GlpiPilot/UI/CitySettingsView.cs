using System.Collections.ObjectModel;
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

/// <summary>Edits an isolated city dictionary. The owner handles persistence and enrichment.</summary>
public sealed partial class CitySettingsView : UserControl
{
    private const int MaximumCities = CitySettings.MaximumCities;
    private readonly ObservableCollection<CityRow> _rows = new();
    private readonly CheckBox _enabled = new() { IsChecked = true, Margin = new Thickness(0, 16, 0, 12) };
    private readonly TextBox _titleColumn = new() { MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Stretch, MaxLength = 500 };
    private readonly TextBox _search = new() { Width = 240, MaxLength = 250, Margin = new Thickness(8, 0, 14, 6) };
    private readonly TextBox _bulk = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 112, MaxLength = 1_600_000,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 8, 0, 10)
    };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, CanUserResizeRows = false,
        Height = 360, MinHeight = 200, RowHeight = 38, FrozenColumnCount = 1, Margin = new Thickness(0, 10, 0, 10)
    };
    private readonly TextBlock _count = Label("Aucune ville", 12, "MutedBrush");
    private readonly TextBlock _status = Label("Configuration prête à être personnalisée.", 12, "MutedBrush");
    private readonly TextBlock _summaryTitle = Label("Appliquez vos correspondances à un relevé", 16, "TextBrush", FontWeights.SemiBold);
    private readonly TextBlock _summaryDetail = Label("Les résultats de la dernière application apparaîtront ici. Aucune ville n’est ajoutée à vos réglages automatiquement.", 12, "MutedBrush");
    private readonly WrapPanel _summaryCounts = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _empty;
    private readonly Button _add, _remove, _bulkAdd, _save;
    private readonly ListCollectionView _view;
    private bool _busy, _loading, _dirty;

    public event Action? SaveRequested;

    public CitySettingsView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var root = new StackPanel();
        var introduction = new StackPanel();
        introduction.Children.Add(Label("Correspondances des villes", 21, "TextBrush", FontWeights.SemiBold));
        introduction.Children.Add(Label("Plusieurs termes peuvent désigner une même ville de référence. GLPI Pilot les recherche dans le titre du ticket pour enrichir le relevé et préparer vos tournées.", 13, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
        var example = new StackPanel();
        example.Children.Add(Label("EXEMPLE DE CORRESPONDANCE", 10, "MutedBrush", FontWeights.SemiBold));
        example.Children.Add(Label("MA ; Mars ; Marseille  →  Marseille", 15, "TextBrush", FontWeights.SemiBold, new Thickness(0, 5, 0, 0)));
        example.Children.Add(Label("Exemple uniquement : ajoutez vos propres villes ci-dessous. Le nom de référence est toujours reconnu, même sans alias.", 12, "MutedBrush", margin: new Thickness(0, 5, 0, 0)));
        var exampleCard = Card(example, "NoticeBrush"); exampleCard.Margin = new Thickness(0, 14, 0, 0); introduction.Children.Add(exampleCard);
        _enabled.Content = Label("Activer les correspondances dans les relevés", 13, "TextBrush", FontWeights.SemiBold);
        AutomationProperties.SetName(_enabled, "Activer les correspondances des villes");
        introduction.Children.Add(_enabled);
        introduction.Children.Add(Label("Colonne contenant le titre du ticket", 12, "MutedBrush", FontWeights.SemiBold));
        _titleColumn.Margin = new Thickness(0, 6, 0, 6);
        _titleColumn.ToolTip = "Laissez vide pour la détection automatique, ou saisissez l’en-tête exact de la colonne à lire.";
        AutomationProperties.SetName(_titleColumn, "Colonne du titre du ticket, vide pour la détection automatique");
        introduction.Children.Add(_titleColumn);
        introduction.Children.Add(Label("Vide : détection automatique. Sinon, indiquez l’en-tête exact. La correspondance utilise des mots entiers, sans tenir compte des accents ni de la casse : Mars ne correspond pas à un mot plus long.", 12, "MutedBrush"));
        introduction.Children.Add(Label("Si plusieurs villes différentes sont reconnues, la ligne reste ambiguë. Un alias court comme MA peut aussi être un mot ordinaire : choisissez vos termes en conséquence. Aucune adresse n’est devinée.", 12, "MutedBrush", margin: new Thickness(0, 7, 0, 0)));
        root.Children.Add(Card(introduction));

        var dictionary = new StackPanel();
        var heading = new DockPanel();
        DockPanel.SetDock(_count, Dock.Right); heading.Children.Add(_count);
        heading.Children.Add(Label("Votre dictionnaire", 17, "TextBrush", FontWeights.SemiBold)); dictionary.Children.Add(heading);
        var commands = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _add = Command("Ajouter une ville"); _remove = Command("Supprimer la sélection");
        commands.Children.Add(_add); commands.Children.Add(_remove);
        commands.Children.Add(Label("Rechercher", 12, "MutedBrush", margin: new Thickness(6, 0, 0, 6)));
        AutomationProperties.SetName(_search, "Rechercher une ville, un alias ou un code postal dans le dictionnaire"); commands.Children.Add(_search);
        dictionary.Children.Add(commands);
        AddColumn("Ville de référence", nameof(CityRow.Name), 210, "Nom affiché dans le relevé. Ce nom est reconnu automatiquement.");
        AddColumn("Termes reconnus (alias)", nameof(CityRow.Aliases), 320, "Jusqu’à 12 alias, séparés par des points-virgules. Les villes composées conservent leurs espaces.");
        AddColumn("Code postal", nameof(CityRow.PostalCode), 120, "Facultatif. Texte conservé tel quel, y compris les zéros initiaux.");
        AddColumn("Latitude", nameof(CityRow.Latitude), 115, "Facultative, de −90 à 90. Saisissez aussi la longitude ; virgule ou point décimal accepté.");
        AddColumn("Longitude", nameof(CityRow.Longitude), 115, "Facultative, de −180 à 180. Saisissez aussi la latitude ; virgule ou point décimal accepté.");
        VirtualizingPanel.SetIsVirtualizing(_grid, true);
        VirtualizingPanel.SetVirtualizationMode(_grid, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_grid, true);
        AutomationProperties.SetName(_grid, "Dictionnaire éditable des villes de référence et de leurs alias");
        var gridHost = new Grid(); gridHost.Children.Add(_grid);
        var emptyContent = new StackPanel { MaxWidth = 510, VerticalAlignment = VerticalAlignment.Center };
        emptyContent.Children.Add(Label("Commencez par vos lieux habituels", 17, "TextBrush", FontWeights.SemiBold));
        emptyContent.Children.Add(Label("Ajoutez une ville et ses alias, ou collez votre liste dans « Ajouter plusieurs correspondances ». Les coordonnées restent facultatives.", 13, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
        _empty = Card(emptyContent, "FieldBrush"); _empty.Margin = new Thickness(0, 48, 0, 10); _empty.IsHitTestVisible = false;
        gridHost.Children.Add(_empty); dictionary.Children.Add(gridHost);
        dictionary.Children.Add(Label("Double-cliquez dans une cellule ou utilisez F2 pour modifier. Jusqu’à 1 000 villes et 12 alias par ville. Latitude et longitude vont ensemble ; saisies localement, sans recherche d’adresse automatique.", 12, "MutedBrush"));
        var dictionaryCard = Card(dictionary); dictionaryCard.Margin = new Thickness(0, 14, 0, 0); root.Children.Add(dictionaryCard);

        var bulkContent = new StackPanel();
        bulkContent.Children.Add(Label("Une correspondance par ligne. Trois formats acceptés :", 12, "MutedBrush"));
        bulkContent.Children.Add(Label("Marseille\nMarseille | MA ; Mars\nMA ; Mars ; Marseille = Marseille", 13, "TextBrush", margin: new Thickness(0, 7, 0, 0)));
        bulkContent.Children.Add(Label("Séparez les alias par des points-virgules, jamais par des espaces. Les termes d’une ville déjà présente seront ajoutés à sa ligne, sans créer de doublon de nom identique.", 12, "MutedBrush", margin: new Thickness(0, 7, 0, 0)));
        AutomationProperties.SetName(_bulk, "Liste de correspondances à ajouter, une ville par ligne"); bulkContent.Children.Add(_bulk);
        _bulkAdd = Command("Ajouter ces correspondances"); bulkContent.Children.Add(_bulkAdd);
        bulkContent.Children.Add(Label("Le bouton « Enregistrer et appliquer au relevé » ajoute aussi la liste collée avant de vérifier les réglages.", 12, "MutedBrush", margin: new Thickness(0, 6, 0, 0)));
        var bulkExpander = new Expander { Header = "Ajouter plusieurs correspondances", Content = bulkContent, Padding = new Thickness(0, 10, 0, 0) };
        var bulkCard = Card(bulkExpander); bulkCard.Margin = new Thickness(0, 14, 0, 0); root.Children.Add(bulkCard);

        root.Children.Add(BuildAdvancedReference());

        var footer = new WrapPanel { Margin = new Thickness(0, 16, 0, 16), VerticalAlignment = VerticalAlignment.Center };
        _save = Command("Enregistrer et appliquer au relevé"); _save.SetResourceReference(Button.StyleProperty, "PrimaryButton");
        footer.Children.Add(_save); _status.Margin = new Thickness(8, 0, 0, 6); _status.MaxWidth = 610; footer.Children.Add(_status); root.Children.Add(footer);
        var report = new StackPanel(); report.Children.Add(_summaryTitle); report.Children.Add(_summaryCounts);
        _summaryDetail.Margin = new Thickness(0, 8, 0, 0); report.Children.Add(_summaryDetail); root.Children.Add(Card(report));
        Content = root;

        _view = new ListCollectionView(_rows) { Filter = MatchesSearch };
        _grid.ItemsSource = _view;
        // Attach callbacks only after every control and collection view exists.
        _enabled.Checked += (_, _) => MarkDirty(); _enabled.Unchecked += (_, _) => MarkDirty();
        _titleColumn.TextChanged += (_, _) => MarkDirty();
        _search.TextChanged += (_, _) => ApplySearch();
        _bulk.TextChanged += (_, _) => { if (!_loading) MarkDirty(); UpdateEnabled(); };
        _grid.SelectionChanged += (_, _) => { LoadSelectedCityAdvanced(); UpdateEnabled(); };
        _add.Click += (_, _) => RunEdit(AddCity);
        _remove.Click += (_, _) => RunEdit(RemoveSelected);
        _bulkAdd.Click += (_, _) => RunEdit(AddBulkCities);
        _save.Click += (_, _) => { if (!_busy) SaveRequested?.Invoke(); };
        UpdateCount(); UpdateEnabled();
    }

    public CitySettings ReadSettings()
    {
        try
        {
            CommitGrid(); CommitAdvanced();
            if (!string.IsNullOrWhiteSpace(_bulk.Text)) AddBulkCities();
            var settings = new CitySettings
            {
                Enabled = _enabled.IsChecked == true,
                TitleColumn = string.IsNullOrWhiteSpace(_titleColumn.Text) ? "" : _titleColumn.Text,
                Cities = _rows.Select((row, index) => ReadCity(row, index + 1)).ToList(),
                Sites = ReadSites(), Buildings = ReadBuildings(), LocationColumn = _locationField.Text, SourcePriority = _sourceKinds.Select(item => item.Key).ToList()
            };
            // Validate the raw draft before Copy/Normalize can repair or bound it.
            settings.Validate();
            return settings;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            SetStatus(error.Message, "ErrorBrush"); throw;
        }
    }

    public void LoadSettings(CitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Copy();
        _loading = true;
        try
        {
            _grid.CancelEdit(DataGridEditingUnit.Cell); _grid.CancelEdit(DataGridEditingUnit.Row);
            foreach (var row in _rows) row.PropertyChanged -= RowChanged;
            _rows.Clear(); _search.Clear(); _bulk.Clear();
            _enabled.IsChecked = copy.Enabled; _titleColumn.Text = copy.TitleColumn;
            foreach (var city in copy.Cities)
                AddRow(new CityRow(city.Id, city.Name, string.Join(" ; ", city.Aliases), city.PostalCode,
                    FormatCoordinate(city.Latitude), FormatCoordinate(city.Longitude)) { Advanced = city.Copy() });
            LoadAdvancedReference(copy);
            _grid.SelectedItem = _rows.FirstOrDefault();
            _view.Refresh(); _dirty = false;
            SetStatus("Configuration chargée.", "MutedBrush");
        }
        finally { _loading = false; }
        UpdateCount(); UpdateEnabled();
    }

    public void SetBusy(bool busy)
    {
        _busy = busy; UpdateEnabled();
    }

    public void ShowSummary(CityEnrichmentResult? report)
    {
        _advancedReport = report; RefreshUnlocatedQueue();
        _summaryCounts.Children.Clear();
        if (report == null)
        {
            _summaryTitle.Text = "Appliquez vos correspondances à un relevé";
            _summaryDetail.Text = "Les résultats de la dernière application apparaîtront ici. Les modifications du dictionnaire sont conservées pendant la consultation du relevé.";
            return;
        }
        _summaryTitle.Text = report.Applied ? "Correspondances du dernier relevé" : "Correspondances non appliquées";
        if (report.Applied)
        {
            SummaryCount("reconnues", report.MatchedCount, "SuccessBrush");
            SummaryCount("sans correspondance", report.UnmatchedCount, "MutedBrush");
            SummaryCount("ambiguës", report.AmbiguousCount, "WarningBrush");
            if (report.TitleUnavailableCount > 0) SummaryCount("sans titre exploitable", report.TitleUnavailableCount, "WarningBrush");
        }
        _summaryDetail.Text = report.Message
            + (report.Applied && !string.IsNullOrWhiteSpace(report.TitleColumnName) ? $"\nColonne lue : {report.TitleColumnName}." : "");
    }

    private static CityDefinition ReadCity(CityRow row, int number)
    {
        var latitudeText = row.Latitude.Trim(); var longitudeText = row.Longitude.Trim();
        if ((latitudeText.Length == 0) != (longitudeText.Length == 0))
            throw new InvalidDataException($"Ville {number} « {row.Name} » : renseignez latitude et longitude ensemble, ou laissez les deux vides.");
        return new CityDefinition
        {
            Id = row.Id, Name = row.Name.Trim(), Aliases = ParseAliases(row.Aliases), PostalCode = row.PostalCode.Trim(),
            Latitude = ReadCoordinate(latitudeText, 90, "latitude", row.Name, number),
            Longitude = ReadCoordinate(longitudeText, 180, "longitude", row.Name, number),
            Region = row.Advanced.Region, Department = row.Advanced.Department, Sector = row.Advanced.Sector, TerritoryPath = row.Advanced.TerritoryPath,
            ExcludedTerms = row.Advanced.ExcludedTerms.ToList(), Rules = row.Advanced.Rules.Select(rule => rule.Copy()).ToList()
        };
    }

    private static double? ReadCoordinate(string text, int limit, string label, string city, int number)
    {
        if (text.Length == 0) return null;
        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < -limit || value > limit)
            throw new InvalidDataException($"Ville {number} « {city} » : {label} invalide. Saisissez un nombre entre −{limit} et {limit}, avec un point ou une virgule décimale.");
        return value;
    }

    private void AddCity()
    {
        CommitGrid();
        if (_rows.Count >= MaximumCities) throw new InvalidDataException("Le dictionnaire contient déjà 1 000 villes. Supprimez une ligne avant d’en ajouter une.");
        _search.Clear();
        var row = new CityRow(Guid.NewGuid().ToString("N"), "", "", "", "", "");
        AddRow(row); _grid.SelectedItem = row; _grid.ScrollIntoView(row);
        _grid.CurrentCell = new DataGridCellInfo(row, _grid.Columns[0]); _grid.Focus(); _grid.BeginEdit();
        MarkDirty(); UpdateCount(); UpdateEnabled();
    }

    private void RemoveSelected()
    {
        CommitGrid();
        var selected = _grid.SelectedItems.OfType<CityRow>().ToArray();
        foreach (var row in selected) { row.PropertyChanged -= RowChanged; _rows.Remove(row); }
        if (selected.Length == 0) return;
        MarkDirty(); UpdateCount(); UpdateEnabled();
        SetStatus($"{selected.Length:N0} ville(s) retirée(s) du brouillon. Enregistrez pour appliquer.", "MutedBrush");
    }

    private void AddBulkCities()
    {
        CommitGrid();
        var lines = _bulk.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > MaximumCities)
            throw new InvalidDataException("Collez au maximum 1 000 lignes à la fois. Votre liste est conservée pour être ajustée.");
        var additions = new List<CityRow>();
        var updates = new Dictionary<CityRow, List<string>>();
        var known = _rows.Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .GroupBy(row => row.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var recognizedLines = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim(); if (line.Length == 0) continue;
            var (name, aliases) = ParseBulkLine(line, index + 1); recognizedLines++;
            CityRow target;
            if (known.TryGetValue(name, out var matches))
            {
                if (matches.Count != 1) throw new InvalidDataException($"Liste, ligne {index + 1} : plusieurs lignes portent déjà le nom « {name} ». Corrigez ce doublon dans la grille.");
                target = matches[0];
            }
            else
            {
                target = new CityRow(Guid.NewGuid().ToString("N"), name, "", "", "", "");
                additions.Add(target); known.Add(name, new List<CityRow> { target });
            }
            if (!updates.TryGetValue(target, out var merged)) updates[target] = merged = ParseAliases(target.Aliases);
            foreach (var alias in aliases)
                if (!string.Equals(alias, target.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                    && !merged.Contains(alias, StringComparer.OrdinalIgnoreCase)) merged.Add(alias);
            // Validate each proposed canonical name and its merged aliases before touching any live row.
            try
            {
                new CitySettings { Cities = new List<CityDefinition> { new() { Id = target.Id, Name = target.Name.Trim(), Aliases = merged.ToList() } } }.Validate();
            }
            catch (InvalidDataException error) { throw new InvalidDataException($"Liste, ligne {index + 1} : {error.Message}", error); }
            if (_rows.Count + additions.Count > MaximumCities)
                throw new InvalidDataException("Cette liste dépasse la limite de 1 000 villes. Aucune ligne de la liste n’a été ajoutée.");
        }
        if (recognizedLines == 0) return;
        var changed = 0;
        foreach (var (row, aliases) in updates)
        {
            var text = string.Join(" ; ", aliases);
            if (ParseAliases(row.Aliases).SequenceEqual(aliases, StringComparer.Ordinal)) continue;
            row.Aliases = text; if (!additions.Contains(row)) changed++;
        }
        foreach (var row in additions) AddRow(row);
        _bulk.Clear(); _search.Clear();
        if (additions.Count > 0 || changed > 0) MarkDirty();
        UpdateCount(); UpdateEnabled();
        SetStatus($"{additions.Count:N0} ville(s) ajoutée(s), {changed:N0} correspondance(s) complétée(s). Les noms déjà présents sont regroupés.", "MutedBrush");
    }

    private static (string Name, List<string> Aliases) ParseBulkLine(string line, int number)
    {
        var pipes = line.Count(character => character == '|'); var equals = line.Count(character => character == '=');
        if (pipes + equals > 1)
            throw new InvalidDataException($"Liste, ligne {number} : utilisez « Ville | alias ; alias » ou « alias ; alias = Ville », avec un seul séparateur | ou =.");
        if (pipes == 1)
        {
            var parts = line.Split('|', 2); return (parts[0].Trim(), ParseAliases(parts[1]));
        }
        if (equals == 1)
        {
            var parts = line.Split('=', 2); return (parts[1].Trim(), ParseAliases(parts[0]));
        }
        if (line.Contains(';')) throw new InvalidDataException($"Liste, ligne {number} : précisez la ville de référence, par exemple « Marseille | MA ; Mars ».");
        return (line, new List<string>());
    }

    private static List<string> ParseAliases(string text) => text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    private static string FormatCoordinate(double? value) => value?.ToString("G", CultureInfo.CurrentCulture) ?? "";

    private void CommitGrid()
    {
        if (!_grid.CommitEdit(DataGridEditingUnit.Cell, true) || !_grid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidDataException("Terminez la cellule en cours avant de poursuivre. Votre saisie est conservée.");
    }

    private void RunEdit(Action action)
    {
        if (_busy) return;
        try { action(); }
        catch (InvalidDataException error) { SetStatus(error.Message, "ErrorBrush"); }
    }

    private void AddRow(CityRow row) { row.PropertyChanged += RowChanged; _rows.Add(row); }
    private void RowChanged(object? sender, PropertyChangedEventArgs args) => MarkDirty();
    private void MarkDirty()
    {
        if (_loading) return;
        if (_csvPreview != null)
        {
            _csvPreview = null; _importGrid.ItemsSource = null;
            _importNote.Text = "Le brouillon a changé. Rechargez le CSV pour comparer avec les valeurs actuelles.";
            UpdateAdvancedReferenceEnabled();
        }
        _dirty = true; SetStatus("Modifications non enregistrées.", "WarningBrush");
    }

    private bool MatchesSearch(object item)
    {
        if (item is not CityRow row) return false;
        var query = _search.Text.Trim(); if (query.Length == 0) return true;
        return new[] { row.Name, row.Aliases, row.PostalCode }.Any(text => CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, query,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
    }

    private void ApplySearch()
    {
        if (_loading) return;
        // Refresh only the view; original rows, their stable IDs and unfiltered drafts stay intact.
        try { CommitGrid(); _view.Refresh(); UpdateCount(); UpdateEnabled(); }
        catch (InvalidDataException error) { SetStatus(error.Message, "ErrorBrush"); }
    }

    private void UpdateCount()
    {
        var visible = _view.Cast<object>().Count();
        _count.Text = _search.Text.Trim().Length == 0 ? $"{_rows.Count:N0} / 1 000 villes" : $"{visible:N0} / {_rows.Count:N0} villes affichées";
        _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _grid.Height = _rows.Count == 0 ? 220 : 360;
    }

    private void UpdateEnabled()
    {
        _enabled.IsEnabled = !_busy; _titleColumn.IsEnabled = !_busy; _search.IsEnabled = !_busy;
        _grid.IsReadOnly = _busy; _bulk.IsReadOnly = _busy;
        _add.IsEnabled = !_busy && _rows.Count < MaximumCities;
        _remove.IsEnabled = !_busy && _grid.SelectedItems.Count > 0;
        _bulkAdd.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(_bulk.Text);
        _save.IsEnabled = !_busy; UpdateAdvancedReferenceEnabled();
    }

    private void SetStatus(string message, string brush)
    {
        _status.Text = message; _status.SetResourceReference(TextBlock.ForegroundProperty, brush);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _status.ToolTip = _dirty ? "Vos modifications sont conservées dans cette page jusqu’à leur enregistrement." : null;
    }

    private void SummaryCount(string label, int count, string brush)
    {
        var text = Label($"{count:N0} lignes {label}", 13, brush, FontWeights.SemiBold);
        var badge = Card(text, "BadgeBrush"); badge.Padding = new Thickness(11, 7, 11, 7); badge.CornerRadius = new CornerRadius(9); badge.Margin = new Thickness(0, 0, 8, 6);
        _summaryCounts.Children.Add(badge);
    }

    private void AddColumn(string header, string property, int width, string tooltip)
    {
        var display = new Style(typeof(TextBlock));
        display.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        display.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(property) { Mode = BindingMode.OneWay }));
        var editing = new Style(typeof(TextBox), TryFindResource(typeof(TextBox)) as Style);
        editing.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3, 1, 3, 1)));
        editing.Setters.Add(new Setter(Control.MinHeightProperty, 25d));
        var headerText = Label(header, 12, "HeaderTextBrush", FontWeights.SemiBold); headerText.ToolTip = tooltip;
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = headerText, Width = new DataGridLength(width), MinWidth = 90, SortMemberPath = property,
            Binding = new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            ElementStyle = display, EditingElementStyle = editing
        });
    }

    private static Button Command(string text)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(13, 8, 13, 8) };
        AutomationProperties.SetName(button, text); return button;
    }

    private static Border Card(UIElement child, string background = "PanelBrush")
    {
        var card = new Border { Child = child, Padding = new Thickness(18), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, background); card.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return card;
    }

    private static TextBlock Label(string text, double size, string brush, FontWeight? weight = null, Thickness? margin = null)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, brush); return label;
    }

    private sealed class CityRow : INotifyPropertyChanged
    {
        private string _name, _aliases, _postalCode, _latitude, _longitude;
        public CityDefinition Advanced { get; set; } = new();
        public string Id { get; }
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Aliases { get => _aliases; set => Set(ref _aliases, value); }
        public string PostalCode { get => _postalCode; set => Set(ref _postalCode, value); }
        public string Latitude { get => _latitude; set => Set(ref _latitude, value); }
        public string Longitude { get => _longitude; set => Set(ref _longitude, value); }
        public event PropertyChangedEventHandler? PropertyChanged;
        public CityRow(string id, string name, string aliases, string postalCode, string latitude, string longitude)
        { Id = id; _name = name; _aliases = aliases; _postalCode = postalCode; _latitude = latitude; _longitude = longitude; }
        private void Set(ref string field, string? value, [CallerMemberName] string? name = null)
        {
            value ??= ""; if (field == value) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
