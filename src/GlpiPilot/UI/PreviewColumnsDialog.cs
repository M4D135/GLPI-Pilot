using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>An isolated working copy: cancelling never changes the extraction view or local preferences.</summary>
public sealed class PreviewColumnsDialog : UserControl
{
    private readonly string[] _headers;
    private readonly ObservableCollection<ColumnChoice> _choices = new();
    private readonly TextBox _search = new() { MinWidth = 200 };
    private readonly TextBlock _count = Label("", 12, "MutedBrush");
    private readonly ListBox _list = new() { MinHeight = 120, SelectionMode = SelectionMode.Single };
    private readonly TextBlock _selectionHint = Label("", 12, "MutedBrush");
    private readonly Button _moveUp, _moveDown, _autoWidth, _fixedWidth, _apply;
    private ICollectionView? _view;
    private bool _updating;

    public PreviewColumnPreference[]? Preferences { get; private set; }
    public event Action<bool>? Completed;

    public PreviewColumnsDialog(IReadOnlyList<string> headers, IEnumerable<PreviewColumnPreference> current)
    {
        _headers = headers.ToArray();
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(Label("Votre vue, vos colonnes", 24, "TextBrush", FontWeights.SemiBold));
        heading.Children.Add(Label("Choisissez les informations affichées, leur ordre et leur largeur. Ces réglages restent sur ce poste.", 13, "MutedBrush", margin: new Thickness(0, 7, 0, 0)));
        root.Children.Add(heading);

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition());
        searchRow.Children.Add(Label("Rechercher une colonne", 12, "MutedBrush", margin: new Thickness(0, 0, 12, 0)));
        _search.ToolTip = "Filtrer cette liste par nom de colonne. Les choix hors de la recherche sont conservés.";
        AutomationProperties.SetName(_search, "Rechercher une colonne dans la liste");
        Grid.SetColumn(_search, 1); searchRow.Children.Add(_search); top.Children.Add(searchRow);
        var tools = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        tools.Children.Add(Command("Tout afficher", ShowAll, "Rendre toutes les colonnes visibles, même celles masquées par la recherche."));
        tools.Children.Add(Command("Tout ajuster", AutoSizeAll, "Ajuster toutes les largeurs au contenu visible, dans la limite de 1 200 pixels."));
        tools.Children.Add(Command("Réinitialiser", Reset, "Rétablir toutes les colonnes, leur ordre d’origine et une largeur de 165 pixels."));
        _count.Margin = new Thickness(6, 0, 0, 0); tools.Children.Add(_count); top.Children.Add(tools);
        Grid.SetRow(top, 1); root.Children.Add(top);

        _list.SetResourceReference(BackgroundProperty, "PanelBrush");
        _list.SetResourceReference(BorderBrushProperty, "LineBrush");
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(_list, "Colonnes, dans l’ordre d’affichage");
        _list.ItemTemplate = MakeColumnTemplate();
        Grid.SetRow(_list, 2); root.Children.Add(_list);

        var selection = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        var selectionTools = new WrapPanel();
        _moveUp = Command("↑ Monter", () => MoveSelected(-1), "Déplacer la colonne sélectionnée d’une position dans la liste complète.");
        _moveDown = Command("↓ Descendre", () => MoveSelected(1), "Déplacer la colonne sélectionnée d’une position dans la liste complète.");
        _autoWidth = Command("Largeur auto", () => SetSelectedWidth(true), "Ajuster la largeur de la colonne sélectionnée au contenu de l’aperçu.");
        _fixedWidth = Command("Largeur 165 px", () => SetSelectedWidth(false), "Rétablir la largeur standard de la colonne sélectionnée.");
        selectionTools.Children.Add(_moveUp); selectionTools.Children.Add(_moveDown);
        selectionTools.Children.Add(_autoWidth); selectionTools.Children.Add(_fixedWidth);
        selection.Children.Add(selectionTools);
        _selectionHint.Margin = new Thickness(0, 8, 0, 0); selection.Children.Add(_selectionHint);
        Grid.SetRow(selection, 3); root.Children.Add(selection);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = Label("Au moins une colonne reste visible. La recherche et les exports continuent de prendre en compte toutes les colonnes.", 12, "MutedBrush", margin: new Thickness(0, 0, 16, 0));
        footer.Children.Add(note);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var cancel = Command("Annuler", () => Completed?.Invoke(false), "Fermer sans modifier la vue.");
        actions.Children.Add(cancel);
        _apply = Command("Appliquer", Apply, "Appliquer et mémoriser les colonnes pour les relevés ayant les mêmes en-têtes.");
        _apply.IsDefault = true;
        _apply.SetResourceReference(BackgroundProperty, "ActionBrush");
        _apply.SetResourceReference(ForegroundProperty, "OnActionBrush");
        _apply.SetResourceReference(BorderBrushProperty, "ActionBorderBrush");
        _apply.Margin = new Thickness(0); actions.Children.Add(_apply);
        Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        Grid.SetRow(footer, 4); root.Children.Add(footer);
        Content = root;

        // All controls exist before any event can update enabled states or refresh the collection view.
        _search.TextChanged += (_, _) => { _view?.Refresh(); UpdateState(); };
        _list.SelectionChanged += (_, _) => UpdateState();
        _list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject origin && ItemsControl.ContainerFromElement(_list, origin) is ListBoxItem item) item.IsSelected = true;
        };
        Loaded += (_, _) => _search.Focus();
        LoadChoices(PreviewViewPreferences.Normalize(current, _headers.Length));
    }

    private void LoadChoices(PreviewColumnPreference[] columns)
    {
        _updating = true;
        try
        {
            foreach (var choice in _choices) choice.PropertyChanged -= ChoiceChanged;
            _choices.Clear();
            var duplicates = _headers.GroupBy(header => header ?? "", StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var column in columns.OrderBy(column => column.DisplayIndex))
            {
                var name = _headers[column.SourceIndex] ?? "";
                var label = string.IsNullOrWhiteSpace(name) ? $"Colonne {column.SourceIndex + 1} · sans en-tête"
                    : duplicates.Contains(name) ? $"{name} · colonne {column.SourceIndex + 1}" : name;
                var item = new ColumnChoice(column.Copy(), label);
                item.PropertyChanged += ChoiceChanged;
                _choices.Add(item);
            }
            _view = CollectionViewSource.GetDefaultView(_choices);
            _view.Filter = item => item is ColumnChoice choice && choice.Label.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            _list.ItemsSource = _view;
        }
        finally { _updating = false; }
        UpdateState();
    }

    private void ChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updating && e.PropertyName == nameof(ColumnChoice.Visible)) UpdateState();
    }

    private void UpdateState()
    {
        if (_updating) return;
        var visible = _choices.Count(choice => choice.Visible);
        foreach (var choice in _choices) choice.CanToggle = !choice.Visible || visible > 1;
        var matched = _view?.Cast<ColumnChoice>().Count() ?? 0;
        _count.Text = $"{visible:N0}/{_choices.Count:N0} visibles" + (_search.Text.Trim().Length > 0 ? $" · {matched:N0} trouvées" : "");
        var selected = _list.SelectedItem as ColumnChoice;
        var position = selected == null ? -1 : _choices.IndexOf(selected);
        _moveUp.IsEnabled = position > 0;
        _moveDown.IsEnabled = position >= 0 && position + 1 < _choices.Count;
        _autoWidth.IsEnabled = _fixedWidth.IsEnabled = selected != null;
        _apply.IsEnabled = visible > 0;
        _selectionHint.Text = selected == null ? "Sélectionnez une colonne pour la déplacer ou ajuster sa largeur."
            : $"Position {position + 1:N0}/{_choices.Count:N0} · {selected.WidthLabel}. Vous pouvez aussi redimensionner les colonnes directement dans le tableau.";
    }

    private void ShowAll()
    {
        _updating = true;
        try { foreach (var choice in _choices) choice.Visible = true; }
        finally { _updating = false; }
        UpdateState();
    }

    private void AutoSizeAll()
    {
        foreach (var choice in _choices) choice.SetWidth(true);
        UpdateState();
    }

    private void Reset() => LoadChoices(PreviewViewPreferences.Defaults(_headers.Length));

    private void SetSelectedWidth(bool auto)
    {
        if (_list.SelectedItem is not ColumnChoice choice) return;
        choice.SetWidth(auto); UpdateState();
    }

    private void MoveSelected(int direction)
    {
        if (_list.SelectedItem is not ColumnChoice choice) return;
        var position = _choices.IndexOf(choice);
        var target = position + direction;
        if (target < 0 || target >= _choices.Count) return;
        _choices.Move(position, target);
        for (var index = 0; index < _choices.Count; index++) _choices[index].Preference.DisplayIndex = index;
        _list.SelectedItem = choice;
        _list.ScrollIntoView(choice);
        UpdateState();
    }

    private void Apply()
    {
        if (!_choices.Any(choice => choice.Visible)) return;
        for (var index = 0; index < _choices.Count; index++) _choices[index].Preference.DisplayIndex = index;
        Preferences = PreviewViewPreferences.Normalize(_choices.Select(choice => choice.Preference), _headers.Length);
        Completed?.Invoke(true);
    }

    private static DataTemplate MakeColumnTemplate()
    {
        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 6, 2, 6));
        var width = new FrameworkElementFactory(typeof(TextBlock));
        width.SetValue(DockPanel.DockProperty, Dock.Right);
        width.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 6, 0));
        width.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        width.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        width.SetBinding(TextBlock.TextProperty, new Binding(nameof(ColumnChoice.WidthLabel)));
        row.AppendChild(width);
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(ColumnChoice.Visible)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetBinding(IsEnabledProperty, new Binding(nameof(ColumnChoice.CanToggle)));
        check.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ColumnChoice.Label)));
        check.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ColumnChoice.Label)));
        check.SetValue(ToolTipProperty, "Afficher ou masquer cette colonne. La dernière colonne visible reste activée.");
        row.AppendChild(check);
        return new DataTemplate(typeof(ColumnChoice)) { VisualTree = row };
    }

    private static Button Command(string label, Action action, string hint)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 8, 12, 8), ToolTip = hint };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock Label(string text, double size, string brush, FontWeight? weight = null, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    public sealed class ColumnChoice : INotifyPropertyChanged
    {
        private bool _canToggle = true;
        public PreviewColumnPreference Preference { get; }
        public string Label { get; }
        public string WidthLabel => Preference.AutoWidth ? "Auto" : Preference.Width.ToString("0.#", CultureInfo.CurrentCulture) + " px";
        public bool Visible
        {
            get => Preference.Visible;
            set { if (Preference.Visible == value) return; Preference.Visible = value; PropertyChanged?.Invoke(this, new(nameof(Visible))); }
        }
        public bool CanToggle
        {
            get => _canToggle;
            set { if (_canToggle == value) return; _canToggle = value; PropertyChanged?.Invoke(this, new(nameof(CanToggle))); }
        }
        public ColumnChoice(PreviewColumnPreference preference, string label) { Preference = preference; Label = label; }
        public void SetWidth(bool auto)
        {
            Preference.AutoWidth = auto;
            Preference.Width = PreviewViewPreferences.DefaultWidth;
            PropertyChanged?.Invoke(this, new(nameof(WidthLabel)));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
