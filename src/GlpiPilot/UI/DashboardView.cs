using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>Native dashboard builder. Layout is separate from the immutable calculated snapshot.</summary>
public sealed partial class DashboardView : UserControl
{
    private const string DragFormat = "GlpiPilot.DashboardWidget";
    private readonly StackPanel _body = new();
    private readonly List<(Button Button, bool Enabled)> _commands = new();
    private readonly List<(Thumb Thumb, bool Enabled)> _resizers = new();
    private readonly Stack<DashboardLayoutSettings> _undo = new(), _redo = new();
    private DashboardLayoutSettings _layout = new();
    private DashboardSnapshot? _data;
    private DashboardBoardPanel? _boardPanel;
    private FrameworkElement? _toolbar, _editor;
    private TextBlock? _saveLabel;
    private TextBlock? _contextHelp;
    private string? _selectedWidget;
    private string _editorMode = "library", _emptyMessage = "Lancez Extraire tout pour calculer vos indicateurs.";
    private string _libraryQuery = "", _libraryCategory = "all";
    private int _libraryPage;
    private readonly HashSet<string> _libraryFavorites = new(StringComparer.Ordinal);
    private bool _libraryAvailableOnly, _libraryFavoritesOnly;
    private bool _busy, _editing, _saveSuccess = true;
    private string _saveMessage = "Disposition enregistrée sur ce poste";
    private DashboardBoardSettings Board => _layout.Boards.First(x => x.Id == _layout.ActiveBoardId);
    public event Action<int[], string>? RowsRequested;
    public event Action? RefreshRequested;
    public event Action? ExportRequested;
    public event Action? SettingsRequested;
    public event Action<DashboardLayoutSettings>? LayoutChanged;
    public DashboardLayoutSettings GetLayout() => _layout.Copy();

    public DashboardView()
    {
        Content = _body; HorizontalContentAlignment = HorizontalAlignment.Stretch;
        FocusVisualStyle = null; _layout.Normalize(); Render();
    }
    public void SetLayout(DashboardLayoutSettings layout, bool clearHistory = true)
    {
        ClearDetailView();
        _layout = layout.Copy(); _layout.Normalize();
        if (clearHistory) { _undo.Clear(); _redo.Clear(); }
        Render();
    }
    public void SetSaveState(bool success)
    {
        _saveSuccess = success;
        _saveMessage = success ? "Disposition enregistrée · " + DateTime.Now.ToString("HH:mm") : "Sauvegarde impossible · cliquez Enregistrer pour réessayer";
        if (_saveLabel is not null) { _saveLabel.Text = _saveMessage; _saveLabel.SetResourceReference(TextBlock.ForegroundProperty, success ? "MutedBrush" : "ErrorBrush"); }
    }
    public void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var (button, enabled) in _commands) button.IsEnabled = !busy && enabled;
        foreach (var (thumb, enabled) in _resizers) thumb.IsEnabled = !busy && enabled;
        if (_toolbar != null) _toolbar.IsEnabled = !busy;
        if (_editor != null) _editor.IsEnabled = !busy;
    }
    public void ShowEmpty(string message = "Lancez Extraire tout. Votre disposition est conservée ; les widgets se rempliront avec le relevé.")
    { ClearDetailView(); _data = null; _emptyMessage = message; Render(); }
    public void Show(DashboardSnapshot data) { ClearDetailView(); _data = data; Render(); }

    public bool AddQueryWidget(string sourceKey, string title, bool distribution)
    {
        if (Board.Widgets.Count >= 60) return false;
        var prefix = distribution ? "query-group:" : "query:";
        if (string.IsNullOrWhiteSpace(sourceKey) || !sourceKey.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParse(sourceKey[prefix.Length..], out var queryId) || queryId == Guid.Empty)
            throw new ArgumentException("La source du widget doit désigner une requête enregistrée.", nameof(sourceKey));
        if (string.IsNullOrWhiteSpace(title) || title.Length > 180)
            throw new ArgumentException("Le titre du widget doit contenir de 1 à 180 caractères.", nameof(title));
        var widget = new DashboardWidgetSettings
        {
            Kind = distribution ? "distribution" : "metric", SourceKey = sourceKey, Title = title,
            Span = distribution ? 6 : 3, Height = distribution ? 340 : 200
        };
        Change(() =>
        {
            Board.Widgets.Add(widget); _selectedWidget = widget.Id; _editorMode = "widget"; _editing = true;
        });
        return true;
    }

    private void Render()
    {
        _body.Children.Clear(); _commands.Clear(); _resizers.Clear(); _layout.Normalize();
        _contextHelp = _saveLabel = null; _toolbar = _editor = null;
        if (_selectedWidget != null && Board.Widgets.All(widget => widget.Id != _selectedWidget)) _selectedWidget = null;
        BuildHeader();
        if (_detailBuilder != null)
        {
            BuildDetailView(); SetBusy(_busy); return;
        }
        BuildToolbar();
        var workspace = new Grid(); workspace.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); workspace.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto }); workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _boardPanel = new DashboardBoardPanel { Board = Board, AllowDrop = _editing, VerticalAlignment = VerticalAlignment.Top };
        if (_data != null || _editing)
            foreach (var widget in Board.Widgets.Where(x => !x.Hidden)) _boardPanel.Children.Add(WidgetCard(widget));
        _boardPanel.DragOver += (_, e) => { e.Effects = CanDrop(e) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; AutoScroll(e); };
        _boardPanel.Drop += (_, e) => { if (CanDrop(e)) MoveWidget((string)e.Data.GetData(DragFormat), null); e.Handled = true; };
        FrameworkElement boardSurface = _data is null && !_editing ? EmptyDataState()
            : Board.Widgets.All(x => x.Hidden) ? EmptyBoardState() : _boardPanel;
        workspace.Children.Add(boardSurface);
        if (_editing)
        {
            var editor = BuildEditor();
            _editor = editor; Grid.SetColumn(editor, 1); workspace.Children.Add(editor);
            workspace.SizeChanged += (_, _) =>
            {
                var narrow = workspace.ActualWidth < 1040;
                Grid.SetColumn(editor, narrow ? 0 : 1); Grid.SetRow(editor, 0);
                Grid.SetRow(boardSurface, narrow ? 1 : 0);
                editor.Width = narrow ? double.NaN : 340;
                editor.Margin = narrow ? new Thickness(0, 0, 0, 16) : new Thickness(18, 0, 0, 0);
            };
        }
        else _editor = null;
        _body.Children.Add(workspace);
        if (_data != null) _body.Children.Add(Text("Cliquez sur un indicateur, une barre ou une légende pour retrouver ses lignes. Les listes complètes s’ouvrent depuis chaque carte. Les colonnes absentes restent signalées.", 11, "MutedBrush", margin: new Thickness(0, 14, 0, 8)));
        SetBusy(_busy);
    }

    private FrameworkElement WidgetCard(DashboardWidgetSettings widget)
    {
        var frame = new Border { Tag = widget.Id, Padding = new Thickness(14), CornerRadius = new CornerRadius(widget.Style == "minimal" ? 0 : 14), BorderThickness = new Thickness(widget.Style == "outline" ? 2 : 1) };
        AutomationProperties.SetAutomationId(frame, "DashboardWidget." + widget.Kind + "." + widget.Id);
        frame.SetResourceReference(Border.BackgroundProperty, widget.Style == "minimal" ? "WindowBrush" : "PanelBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, widget.Style == "minimal" ? "WindowBrush" : "LineBrush");
        if (widget.Style is "outline" or "accent") frame.BorderBrush = Accent(widget.Accent);
        if (widget.Style == "accent") frame.BorderThickness = new Thickness(4, 1, 1, 1);
        if (_editing && _selectedWidget == widget.Id) frame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        var content = new Grid(); content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
        if (_editing)
        {
            var edit = Mini("Modifier", "Ouvrir les options de ce widget dans le panneau de personnalisation.", () => OpenEditor("widget", widget.Id)); DockPanel.SetDock(edit, Dock.Right); top.Children.Add(edit);
            var handle = new Border { Child = Text(widget.Locked ? "▣" : "⠿", 20, "MutedBrush"), Padding = new Thickness(0, 0, 9, 0), Cursor = widget.Locked ? Cursors.Arrow : Cursors.SizeAll, ToolTip = widget.Locked ? "Position verrouillée" : "Glisser pour déplacer le widget" };
            DockPanel.SetDock(handle, Dock.Left); top.Children.Add(handle);
            Point start = default;
            handle.MouseLeftButtonDown += (_, e) => start = e.GetPosition(handle);
            handle.MouseMove += (_, e) =>
            {
                if (_busy || widget.Locked || e.LeftButton != MouseButtonState.Pressed) return;
                var point = e.GetPosition(handle);
                if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                DragDrop.DoDragDrop(handle, new DataObject(DragFormat, widget.Id), DragDropEffects.Move); e.Handled = true;
            };
            frame.AllowDrop = true;
            frame.DragOver += (_, e) => { e.Effects = CanDrop(e) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; AutoScroll(e); };
            frame.Drop += (_, e) => { if (CanDrop(e)) MoveWidget((string)e.Data.GetData(DragFormat), widget.Id); e.Handled = true; };
        }
        var title = Text(WidgetTitle(widget), 14, weight: FontWeights.SemiBold); title.ToolTip = WidgetTitle(widget); top.Children.Add(title);
        content.Children.Add(top);
        var inner = WidgetContent(widget); Grid.SetRow(inner, 1); content.Children.Add(inner);
        if (_editing)
        {
            var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var resize = new Thumb { Width = 21, Height = 21, Cursor = widget.Locked ? Cursors.Arrow : Cursors.SizeNWSE, IsEnabled = !widget.Locked, ToolTip = "Glisser pour ajuster la largeur ; un déplacement vertical définit une hauteur minimale.", Background = Brushes.Transparent };
            AutomationProperties.SetName(resize, "Redimensionner le widget");
            _resizers.Add((resize, !widget.Locked));
            var factory = new FrameworkElementFactory(typeof(TextBlock)); factory.SetValue(TextBlock.TextProperty, "↘"); factory.SetValue(TextBlock.FontSizeProperty, 19d); factory.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); resize.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = factory };
            DockPanel.SetDock(resize, Dock.Right); footer.Children.Add(resize);
            var order = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 5, 0) };
            order.Children.Add(Mini("↑", "Déplacer ce widget avant le précédent.", () => MoveBy(widget, -1), !widget.Locked && Board.Widgets.IndexOf(widget) > 0));
            order.Children.Add(Mini("↓", "Déplacer ce widget après le suivant.", () => MoveBy(widget, 1), !widget.Locked && Board.Widgets.IndexOf(widget) < Board.Widgets.Count - 1));
            DockPanel.SetDock(order, Dock.Right); footer.Children.Add(order);
            footer.Children.Add(Text((widget.Locked ? "Verrouillé · " : $"{100d * widget.Span / 12:0.#} % · ")
                + (widget.AutoHeight ? "hauteur auto" : $"min. {widget.Height}px"), 10, "MutedBrush"));
            DashboardLayoutSettings? before = null; double dx = 0, dy = 0; var originalSpan = widget.Span; var originalHeight = widget.Height;
            resize.DragStarted += (_, _) =>
            {
                if (_busy) return; before = _layout.Copy(); dx = dy = 0; originalSpan = widget.Span;
                originalHeight = widget.AutoHeight ? (int)Math.Clamp(Math.Ceiling(frame.ActualHeight), 160, 960) : widget.Height;
            };
            resize.DragDelta += (_, e) =>
            {
                if (_busy || before is null || widget.Locked) return;
                dx += e.HorizontalChange; dy += e.VerticalChange;
                widget.Span = Math.Clamp(originalSpan + (int)Math.Round(dx / Math.Max(40, (_boardPanel?.ActualWidth ?? 1000) / 12)), 3, 12);
                if (Math.Abs(dy) >= 10)
                {
                    widget.AutoHeight = false;
                    widget.Height = Math.Clamp(originalHeight + (int)Math.Round(dy / 20) * 20, 160, 960);
                }
                _boardPanel?.InvalidateMeasure();
            };
            resize.DragCompleted += (_, e) => { if (before is null) return; if (e.Canceled || _busy) _layout = before; else { PushUndo(before); Persist(); } Render(); };
            Grid.SetRow(footer, 2); content.Children.Add(footer);
        }
        frame.Child = content; return frame;
    }

    private void OpenEditor(string mode, string? widgetId = null)
    {
        _editing = true; _editorMode = mode;
        if (widgetId != null) _selectedWidget = widgetId;
        Render();
        // Only an explicit edit command scrolls to the editor; data refreshes do not.
        Dispatcher.BeginInvoke(new Action(() => _editor?.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private bool CanDrop(DragEventArgs e) => _editing && !_busy && e.Data.GetDataPresent(DragFormat) && e.Data.GetData(DragFormat) is string id && Board.Widgets.Any(w => w.Id == id && !w.Locked);
    private void AutoScroll(DragEventArgs e)
    {
        DependencyObject? current = this;
        while (current is not null && current is not ScrollViewer) current = VisualTreeHelper.GetParent(current);
        if (current is not ScrollViewer scroll) return;
        var y = e.GetPosition(scroll).Y; if (y < 55) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 22); else if (y > scroll.ActualHeight - 55) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 22);
    }
    private void MoveWidget(string id, string? beforeId)
    {
        if (id == beforeId || !Board.Widgets.Any(x => x.Id == id && !x.Locked)) return;
        Change(() => DashboardLayoutEngine.Move(Board, id, beforeId));
    }
    private void MoveBy(DashboardWidgetSettings widget, int delta)
    {
        var index = Board.Widgets.IndexOf(widget); var target = index + delta;
        if (widget.Locked || target < 0 || target >= Board.Widgets.Count) return;
        Change(() => { Board.Widgets.RemoveAt(index); Board.Widgets.Insert(target, widget); });
    }
    private void Change(Action mutation) { PushUndo(_layout.Copy()); mutation(); _layout.Validate(); Persist(); Render(); }
    private void PushUndo(DashboardLayoutSettings before)
    {
        _undo.Push(before); if (_undo.Count > 30) { var keep = _undo.Take(30).Reverse().ToArray(); _undo.Clear(); foreach (var item in keep) _undo.Push(item); } _redo.Clear();
    }
    private void Undo() { if (_undo.Count == 0) return; _redo.Push(_layout.Copy()); _layout = _undo.Pop(); Persist(); Render(); }
    private void Redo() { if (_redo.Count == 0) return; _undo.Push(_layout.Copy()); _layout = _redo.Pop(); Persist(); Render(); }
    private void Persist() => LayoutChanged?.Invoke(_layout.Copy());
    private string WidgetTitle(DashboardWidgetSettings w) => !string.IsNullOrWhiteSpace(w.Title) ? w.Title : w.Kind switch
    {
        "metric" => _data?.Metric(w.SourceKey)?.Label ?? Catalog().FirstOrDefault(x => x.Kind == w.Kind && x.Key == w.SourceKey)?.Label ?? w.SourceKey,
        "distribution" => _data?.Distributions.FirstOrDefault(x => x.Key == w.SourceKey)?.Title ?? Catalog().FirstOrDefault(x => x.Kind == w.Kind && x.Key == w.SourceKey)?.Label ?? w.SourceKey,
        "weather" => "Météo du relevé", "timeline" => "Activité datée · 30 jours", "attention" => "À regarder en priorité", "quality" => "Qualité et périmètre", "note" => "Note", _ => "Widget"
    };

    private Button Command(object content, Action action, bool enabled = true)
    {
        var button = new Button { Content = content, Margin = new Thickness(0, 0, 8, 7), IsEnabled = !_busy && enabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => { if (!_busy && enabled) action(); }; _commands.Add((button, enabled)); return button;
    }
    private Button Mini(string text, string tip, Action action, bool enabled = true)
    { var button = Command(text, action, enabled); button.Padding = new Thickness(7, 3, 7, 3); button.MinHeight = 25; button.Margin = new Thickness(3, 0, 0, 0); Help(button, tip, tip); return button; }
    private static TextBlock Text(string text, double size = 13, string brush = "TextBrush", FontWeight? weight = null, Thickness? margin = null)
    { var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) }; block.SetResourceReference(TextBlock.ForegroundProperty, brush); return block; }
    private static Border Card(UIElement content)
    { var border = new Border { Child = content, Padding = new Thickness(18), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) }; border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border; }
    private static Brush Accent(string name)
    {
        // Medium chroma decorative accents remain legible in both palettes; body text uses theme resources.
        var color = name switch { "teal" => "#248F97", "green" => "#4A9569", "orange" => "#BA7A2E", "red" => "#C65B5B", "purple" => "#8B71C6", "slate" => "#7A8797", _ => "#4887DB" };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;
    }
}
