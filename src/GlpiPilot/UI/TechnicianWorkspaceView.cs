using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>Identity choice and technician navigation; every chart is supplied by the shared dashboard.</summary>
public sealed class TechnicianWorkspaceView : UserControl
{
    private const int TabsPerPage = 24;
    private readonly ComboBox _identity = new() { MinWidth = 230, MaxWidth = 390, DisplayMemberPath = "Name", SelectedValuePath = "Key", IsTextSearchEnabled = true, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _filter = new() { Width = 230, Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _openPersonalAfterExtraction = new() { IsChecked = false, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _identityNote = Label("Choisissez votre nom parmi les techniciens du relevé.", 12, "MutedBrush");
    private readonly TextBlock _rosterNote = Label("Les techniciens apparaîtront après l’extraction.", 12, "MutedBrush");
    private readonly TextBlock _tabCount = Label("", 12, "MutedBrush");
    private readonly TextBlock _selectionNote = Label("", 13, "MutedBrush");
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _tabScroll;
    private readonly Button _saveIdentity;
    private readonly Button _personal;
    private readonly Button _team;
    private readonly Button _previous, _next;
    private readonly TabControl _workspaces = new() { Margin = new Thickness(0, 18, 0, 0) };
    private readonly TabItem _managementTab, _dashboardTab;
    private readonly List<Button> _tabButtons = new();
    private TechnicianEntry[] _filteredEntries = Array.Empty<TechnicianEntry>();
    private TechnicianRoster? _roster;
    private string _personalKey = "", _selectedKey = "";
    private bool _busy, _calculating, _rosterLoading, _updatingPersonalPreference;
    private int _tabPage;

    public DashboardView Dashboard { get; } = new();
    public TechnicianManagementView Management { get; } = new();
    public event Action<string>? TechnicianRequested;
    public event Action<string>? PersonalTechnicianRequested;
    public event Action<bool>? OpenPersonalAfterExtractionRequested;
    public event Action? PersonalRequested;
    public event Action? TeamRequested;

    public TechnicianWorkspaceView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var root = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        _team = Command("Vue d’équipe", () => TeamRequested?.Invoke());
        DockPanel.SetDock(_team, Dock.Right); heading.Children.Add(_team);
        var titles = new StackPanel();
        titles.Children.Add(Label("L’équipe et votre espace", 25, "TextBrush", FontWeights.SemiBold));
        titles.Children.Add(Label("Comparez les charges du relevé, retrouvez vos favoris et ouvrez le dashboard de chaque technicien.", 13, "MutedBrush", new Thickness(0, 6, 20, 0)));
        heading.Children.Add(titles); root.Children.Add(heading);

        var identityContent = new StackPanel();
        identityContent.Children.Add(Label("MON ESPACE", 11, "MutedBrush", FontWeights.SemiBold));
        var identityRow = new WrapPanel { Margin = new Thickness(0, 9, 0, 8) };
        _identity.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        VirtualizingPanel.SetIsVirtualizing(_identity, true);
        VirtualizingPanel.SetVirtualizationMode(_identity, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_identity, true);
        AutomationProperties.SetName(_identity, "Choisir mon identité parmi les techniciens détectés");
        _identity.ToolTip = "Ce choix appartient à ce poste. Il n’est pas imposé au collègue qui importe votre configuration.";
        identityRow.Children.Add(_identity);
        _saveIdentity = Command("C’est moi", () =>
        {
            if (_identity.SelectedItem is TechnicianEntry entry) PersonalTechnicianRequested?.Invoke(entry.Key);
        });
        _saveIdentity.ToolTip = "Mémoriser le technicien choisi et ouvrir son tableau de bord.";
        identityRow.Children.Add(_saveIdentity);
        _personal = Command("Mon tableau de bord", () => PersonalRequested?.Invoke());
        _personal.SetResourceReference(BackgroundProperty, "ActionBrush");
        _personal.SetResourceReference(ForegroundProperty, "OnActionBrush");
        _personal.SetResourceReference(BorderBrushProperty, "ActionBorderBrush");
        identityRow.Children.Add(_personal);
        identityContent.Children.Add(identityRow); identityContent.Children.Add(_identityNote);
        _openPersonalAfterExtraction.Content = Label("Ouvrir mon espace après une extraction réussie", 12, "TextBrush");
        _openPersonalAfterExtraction.ToolTip = "Après Extraire tout, ouvrir le tableau de bord de l’identité mémorisée. Ce choix reste sur ce poste et ne déclenche aucune extraction au lancement.";
        AutomationProperties.SetName(_openPersonalAfterExtraction, "Ouvrir mon espace après une extraction réussie");
        identityContent.Children.Add(_openPersonalAfterExtraction);
        root.Children.Add(Card(identityContent));

        var dashboardContent = new StackPanel();
        _managementTab = new TabItem { Header = "Gestion de l’équipe", Content = Management };
        _dashboardTab = new TabItem { Header = "Dashboard du technicien", Content = dashboardContent };
        _workspaces.Items.Add(_managementTab); _workspaces.Items.Add(_dashboardTab);
        _workspaces.SelectedItem = _managementTab;
        root.Children.Add(_workspaces);

        var toolbar = new WrapPanel { Margin = new Thickness(0, 18, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(Label("TECHNICIENS", 11, "MutedBrush", FontWeights.SemiBold, new Thickness(0, 0, 16, 0)));
        toolbar.Children.Add(Label("Rechercher", 12, "MutedBrush", new Thickness(0, 0, 8, 0)));
        AutomationProperties.SetName(_filter, "Rechercher un onglet de technicien");
        _filter.ToolTip = "Filtrer les onglets par nom. Ce champ ne filtre pas les données du tableau de bord sélectionné.";
        toolbar.Children.Add(_filter); toolbar.Children.Add(_tabCount);
        _previous = Command("← Précédents", () => { if (_tabPage > 0) { _tabPage--; RenderTabs(resetScroll: true); } });
        _next = Command("Suivants →", () => { if ((_tabPage + 1) * TabsPerPage < _filteredEntries.Length) { _tabPage++; RenderTabs(resetScroll: true); } });
        _previous.Margin = new Thickness(12, 0, 8, 0);
        toolbar.Children.Add(_previous); toolbar.Children.Add(_next); dashboardContent.Children.Add(toolbar);
        _tabScroll = new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Margin = new Thickness(0, 0, 0, 8) };
        dashboardContent.Children.Add(_tabScroll);
        _rosterNote.Margin = new Thickness(0, 0, 0, 8); dashboardContent.Children.Add(_rosterNote);
        _selectionNote.Margin = new Thickness(0, 0, 0, 16); dashboardContent.Children.Add(_selectionNote);
        dashboardContent.Children.Add(Dashboard);
        Content = root;
        _identity.SelectionChanged += (_, _) => UpdateEnabled();
        _filter.TextChanged += (_, _) => { _tabPage = 0; RenderTabs(resetScroll: true); };
        _openPersonalAfterExtraction.Checked += PersonalPreference_Changed;
        _openPersonalAfterExtraction.Unchecked += PersonalPreference_Changed;
        Dashboard.ShowEmpty("Lancez Extraire tout, puis choisissez votre nom dans Mon espace. Les widgets seront calculés sur les tickets qui vous sont attribués dans le relevé.");
        UpdateEnabled();
    }

    public void ShowRoster(TechnicianRoster roster, string personalKey, string selectedKey)
    {
        _roster = roster; _rosterLoading = false; _personalKey = personalKey; _selectedKey = selectedKey;
        _identity.ItemsSource = roster.Technicians;
        _identity.SelectedItem = roster.Technicians.FirstOrDefault(entry => entry.Key == personalKey)
            ?? roster.Technicians.FirstOrDefault(entry => entry.Key == selectedKey);
        _rosterNote.Text = roster.Message;
        _rosterNote.SetResourceReference(TextBlock.ForegroundProperty, roster.Available ? "MutedBrush" : "WarningBrush");
        UpdateIdentityNote(); RenderTabs(); SetSelectedKey(selectedKey);
    }

    public void ShowLoading()
    {
        _rosterLoading = true;
        _rosterNote.Text = "Actualisation des techniciens et des charges. La vue précédente reste affichée pendant le calcul…";
        SetCalculationBusy(true);
        Management.ShowLoading();
    }

    public void ShowRefreshError(string message)
    {
        _rosterLoading = true; _rosterNote.Text = message;
        _rosterNote.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        Management.ShowRefreshError(message); SetCalculationBusy(true);
    }

    public void ShowTechnicianDashboard() => _workspaces.SelectedItem = _dashboardTab;

    public void ShowEmpty(string message)
    {
        _roster = null; _rosterLoading = false; _identity.ItemsSource = null; _tabs.Children.Clear(); _tabButtons.Clear(); _filteredEntries = Array.Empty<TechnicianEntry>();
        _tabCount.Text = "Aucun relevé"; _rosterNote.Text = message; _selectionNote.Text = "";
        SetCalculationBusy(false); UpdateIdentityNote();
        Dashboard.ShowEmpty(message);
        Management.ShowEmpty(message);
    }

    public void SetIdentity(string personalKey)
    {
        _personalKey = personalKey;
        if (_roster != null) _identity.SelectedItem = _roster.Technicians.FirstOrDefault(entry => entry.Key == personalKey);
        UpdateIdentityNote(); RenderTabs(); SetSelectedKey(_selectedKey);
    }

    public void SetOpenPersonalAfterExtraction(bool enabled)
    {
        _updatingPersonalPreference = true;
        try { _openPersonalAfterExtraction.IsChecked = enabled; }
        finally { _updatingPersonalPreference = false; }
    }

    private void PersonalPreference_Changed(object sender, RoutedEventArgs e)
    {
        if (!_updatingPersonalPreference)
            OpenPersonalAfterExtractionRequested?.Invoke(_openPersonalAfterExtraction.IsChecked == true);
    }

    public void SetSelectedKey(string key)
    {
        _selectedKey = key;
        Management.SetSelectedKey(key);
        var index = Array.FindIndex(_filteredEntries, entry => entry.Key == key);
        if (index >= 0 && index / TabsPerPage != _tabPage)
        {
            _tabPage = index / TabsPerPage;
            RenderTabs(resetScroll: true);
        }
        else ApplySelectionState();
    }

    private void ApplySelectionState()
    {
        foreach (var button in _tabButtons)
        {
            var selected = string.Equals(button.Tag as string, _selectedKey, StringComparison.Ordinal);
            button.SetResourceReference(BackgroundProperty, selected ? "SelectedBrush" : "FieldBrush");
            button.SetResourceReference(BorderBrushProperty, selected ? "AccentBrush" : "LineBrush");
            button.BorderThickness = new Thickness(selected ? 1.5 : 1);
            AutomationProperties.SetHelpText(button, selected ? "Vue sélectionnée" : "Ouvrir ce tableau de bord");
        }
        var entry = _roster?.Technicians.FirstOrDefault(item => item.Key == _selectedKey);
        _selectionNote.Text = _selectedKey.Length == 0 ? "Sélectionnez un onglet de technicien ou définissez votre espace personnel."
            : _selectedKey == _personalKey ? "Mon tableau de bord · " + (entry?.Name ?? _selectedKey)
            : "Vue sélectionnée · " + (entry?.Name ?? _selectedKey);
        UpdateEnabled();
    }

    public void SetBusy(bool busy) { _busy = busy; UpdateEnabled(); }
    public void SetCalculationBusy(bool calculating) { _calculating = calculating; UpdateEnabled(); }

    private void UpdateIdentityNote()
    {
        var entry = _roster?.Technicians.FirstOrDefault(item => item.Key == _personalKey);
        _identityNote.Text = _personalKey.Length == 0
            ? "Choisissez votre nom puis « C’est moi ». Ce choix sera repris automatiquement sur ce poste."
            : entry != null ? "Identité mémorisée : " + entry.Name
            : _roster?.Available == true ? "L’identité mémorisée n’apparaît pas dans ce relevé. Mon tableau de bord reste disponible pour vérifier son périmètre vide."
            : "Votre identité mémorisée sera reprise au prochain relevé.";
    }

    private void RenderTabs(bool resetScroll = false)
    {
        var horizontalOffset = resetScroll ? 0 : _tabScroll?.HorizontalOffset ?? 0;
        _tabs.Children.Clear(); _tabButtons.Clear();
        var query = _filter.Text.Trim();
        _filteredEntries = _roster?.Technicians.Where(entry => query.Length == 0
            || CultureInfo.CurrentCulture.CompareInfo.IndexOf(entry.Name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
            .OrderByDescending(entry => entry.Key == _personalKey).ToArray()
            ?? Array.Empty<TechnicianEntry>();
        _tabPage = Math.Clamp(_tabPage, 0, Math.Max(0, (_filteredEntries.Length - 1) / TabsPerPage));
        var entries = _filteredEntries.Skip(_tabPage * TabsPerPage).Take(TabsPerPage).ToArray();
        _tabCount.Text = _roster == null ? "" : _filteredEntries.Length == 0 ? $"0 / {_roster.Technicians.Count:N0}"
            : $"{_tabPage * TabsPerPage + 1:N0}–{_tabPage * TabsPerPage + entries.Length:N0} / {_filteredEntries.Length:N0}"
                + (query.Length > 0 ? $" · {_roster.Technicians.Count:N0} détecté(s) au total" : " techniciens");
        foreach (var entry in entries)
        {
            var content = new StackPanel { Width = 172 };
            var name = Label((entry.Key == _personalKey ? "★  " : "") + entry.Name, 13, "TextBrush", FontWeights.SemiBold);
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            content.Children.Add(name);
            content.Children.Add(Label($"{entry.Rows.Length:N0} lignes · {(entry.ActiveCount?.ToString("N0") ?? "—")} actives" + (entry.UnknownStatusCount > 0 ? " *" : ""), 11, "MutedBrush", new Thickness(0, 5, 0, 0)));
            var button = Command("", () => TechnicianRequested?.Invoke(entry.Key));
            button.Content = content; button.Tag = entry.Key; button.Padding = new Thickness(12, 10, 12, 10);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.ToolTip = entry.Name + $"\n{entry.Rows.Length:N0} lignes attribuées dans ce relevé."
                + (entry.UnknownStatusCount > 0 ? $"\n{entry.UnknownStatusCount:N0} statut(s) absent(s) ou non reconnu(s) ; volume actif partiel." : "");
            AutomationProperties.SetName(button, "Tableau de bord de " + entry.Name);
            _tabs.Children.Add(button); _tabButtons.Add(button);
        }
        if (_roster?.Available == true && entries.Length == 0)
            _tabs.Children.Add(Label(query.Length == 0 ? "Aucun technicien renseigné." : "Aucun nom ne correspond à cette recherche.", 13, "MutedBrush", new Thickness(0, 10, 0, 10)));
        _tabScroll?.ScrollToHorizontalOffset(horizontalOffset);
        ApplySelectionState();
    }

    private void UpdateEnabled()
    {
        var available = !_busy && !_rosterLoading && _roster?.Available == true;
        _identity.IsEnabled = available && _roster!.Technicians.Count > 0;
        _filter.IsEnabled = available;
        _saveIdentity.IsEnabled = available && _identity.SelectedItem is TechnicianEntry;
        _personal.IsEnabled = available && _personalKey.Length > 0;
        _openPersonalAfterExtraction.IsEnabled = !_busy;
        _team.IsEnabled = !_busy;
        _previous.IsEnabled = available && _tabPage > 0;
        _next.IsEnabled = available && (_tabPage + 1) * TabsPerPage < _filteredEntries.Length;
        _previous.Visibility = _next.Visibility = _filteredEntries.Length > TabsPerPage ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in _tabButtons) button.IsEnabled = available;
        Dashboard.SetBusy(_busy || _calculating || _rosterLoading);
        Management.SetBusy(_busy);
    }

    private static Button Command(string title, Action action)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 7, 12, 7), FontSize = 12, MinHeight = 36 };
        button.Click += (_, _) => action(); return button;
    }
    private static TextBlock Label(string text, double size, string brush, FontWeight? weight = null, Thickness? margin = null)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, brush); return label;
    }
    private static TextBlock Label(string text, double size, string brush, Thickness margin) => Label(text, size, brush, null, margin);
    private static Border Card(UIElement content)
    {
        var border = new Border { Child = content, Padding = new Thickness(18), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border;
    }
}
