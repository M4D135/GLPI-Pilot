using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlpiPilot.Core;
using Microsoft.Win32;

namespace GlpiPilot.UI;

public sealed partial class DashboardView
{
    private string DescribeFilters(IEnumerable<DashboardScopeFilter> filters) => string.Join(" · ", filters.Select(x =>
        (x.Exclude ? "sauf " : "") + (x.Kind == "date" ? x.SourceKey + " " + x.Value + " → " + x.Value2
            : x.Kind == "metric" ? _baseline?.Metric(x.SourceKey)?.Label ?? x.SourceKey : x.SourceKey + " = " + x.Value)));

    private async void RecalculateScopes()
    {
        var baseline = _baseline;
        if (baseline?.RecalculateScope == null) return;
        var common = DashboardScopeFilter.CopyList(Board.Filters.Concat(_crossFilters));
        var widgetFilters = Board.Widgets.Where(x => !x.Hidden && x.Filters.Count > 0).ToDictionary(x => x.Id, x => DashboardScopeFilter.CopyList(x.Filters));
        var compare = Board.ComparisonEnabled;
        var comparisonFilters = DashboardScopeFilter.CopyList(Board.ComparisonFilters);
        var signature = JsonSerializer.Serialize(new { generation = _sourceGeneration, board = Board.Id, common, widgetFilters, compare, comparisonFilters });
        if (_scopeSignature == signature) return;
        _scopeSignature = signature; _scopeOperation?.Cancel();
        if (common.Count == 0 && widgetFilters.Count == 0 && !compare)
        {
            _data = baseline; _comparison = null; _widgetSnapshots.Clear(); _scopeCalculating = false; _analysisMessage = "";
            SnapshotChanged?.Invoke(baseline);
            return;
        }
        _scopeCalculating = true; _data = _comparison = null; _widgetSnapshots.Clear(); _analysisMessage = "";
        _emptyMessage = "Calcul des périmètres et des indicateurs… Les nouveaux résultats apparaîtront ensemble.";
        var operation = new CancellationTokenSource(); _scopeOperation = operation;
        try
        {
            var calculated = await Task.Run(() =>
            {
                var scope = DashboardService.ResolveScope(baseline, common, operation.Token);
                var current = common.Count == 0 ? baseline : baseline.RecalculateScope(scope, "Dashboard · " + DescribeFilters(common), operation.Token);
                var widgets = new Dictionary<string, DashboardSnapshot>(StringComparer.Ordinal);
                var cache = new Dictionary<string, DashboardSnapshot>(StringComparer.Ordinal);
                foreach (var pair in widgetFilters)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    var key = JsonSerializer.Serialize(pair.Value);
                    if (!cache.TryGetValue(key, out var item))
                    {
                        var rows = DashboardService.ResolveScope(baseline, pair.Value, operation.Token).Intersect(scope).Order().ToArray();
                        item = baseline.RecalculateScope(rows, "Widget · " + DescribeFilters(common.Concat(pair.Value)), operation.Token);
                        cache[key] = item;
                    }
                    widgets[pair.Key] = item;
                }
                var other = compare ? baseline.RecalculateScope(DashboardService.ResolveScope(baseline, comparisonFilters, operation.Token),
                    "Comparaison · " + (comparisonFilters.Count == 0 ? "périmètre de départ" : DescribeFilters(comparisonFilters)), operation.Token) : null;
                return (current, widgets, other);
            }, operation.Token);
            if (operation.IsCancellationRequested || !ReferenceEquals(_baseline, baseline)) return;
            _data = calculated.current; _comparison = calculated.other; _scopeCalculating = false;
            _widgetSnapshots.Clear(); foreach (var pair in calculated.widgets) _widgetSnapshots[pair.Key] = pair.Value;
            _analysisMessage = "";
            SnapshotChanged?.Invoke(_data);
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!operation.IsCancellationRequested && ReferenceEquals(_baseline, baseline))
            {
                _data = null; _comparison = null; _widgetSnapshots.Clear(); _scopeCalculating = false;
                _analysisMessage = "Périmètre non calculé · " + error.GetBaseException().Message;
                _emptyMessage = "Corrigez ou réinitialisez les filtres du dashboard. Aucun chiffre ne remplace le résultat indisponible.";
                Render();
            }
        }
        finally { if (_scopeOperation == operation) _scopeOperation = null; operation.Dispose(); }
    }

    private void BuildFilterBar()
    {
        if (_baseline == null) return;
        var content = new StackPanel();
        var top = new WrapPanel();
        top.Children.Add(Text("Périmètre commun", 13, weight: FontWeights.SemiBold, margin: new Thickness(0, 7, 12, 0)));
        top.Children.Add(Chip(_scopeCalculating ? "Calcul en cours…" : _data == null ? "Périmètre indisponible" : _data.RowCount.ToString("N0") + " / " + _baseline.RowCount.ToString("N0") + " lignes", "AccentBrush"));
        var cross = new CheckBox { Content = "Cliquer pour filtrer les widgets", IsChecked = Board.CrossFilterOnClick, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 4, 12, 7) };
        Help(cross, "Filtres croisés", "Un clic dans une catégorie filtre ce dashboard. Désactivez cette option pour ouvrir directement les lignes du relevé.");
        cross.Click += (_, _) => Change(() => Board.CrossFilterOnClick = cross.IsChecked == true);
        top.Children.Add(cross);
        top.Children.Add(DescribedCommand("Réinitialiser", "Retirer les filtres communs et les filtres croisés de cette disposition.", () => Change(() => { Board.Filters.Clear(); _crossFilters.Clear(); }), Board.Filters.Count + _crossFilters.Count > 0, true));
        top.Children.Add(DescribedCommand("Voir les lignes", "Ouvrir le relevé au périmètre actuellement calculé.", () => { if (_data != null) RowsRequested?.Invoke(_data.ScopeRows, _data.ScopeLabel); }, _data != null, true));
        content.Children.Add(top);
        if (Board.Filters.Count + _crossFilters.Count > 0) content.Children.Add(Text(DescribeFilters(Board.Filters.Concat(_crossFilters)), 11, "MutedBrush", margin: new Thickness(0, 3, 0, 8)));
        if (_analysisMessage.Length > 0) content.Children.Add(Text(_analysisMessage, 12, "WarningBrush", margin: new Thickness(0, 4, 0, 9)));
        var edit = new StackPanel();
        var readCommon = ScopeEditor(edit, Board.Filters);
        var comparison = Check(edit, "Comparer avec un second périmètre", Board.ComparisonEnabled);
        var comparisonContent = new StackPanel();
        comparisonContent.Children.Add(Text("Périmètre B · indépendant des filtres A et borné à la source de cette vue", 11, "MutedBrush"));
        var readComparison = ScopeEditor(comparisonContent, Board.ComparisonFilters);
        comparisonContent.Visibility = Board.ComparisonEnabled ? Visibility.Visible : Visibility.Collapsed;
        comparison.Click += (_, _) => comparisonContent.Visibility = comparison.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        edit.Children.Add(comparisonContent);
        var error = Text("", 11, "ErrorBrush"); edit.Children.Add(error);
        edit.Children.Add(DescribedCommand("Appliquer les périmètres", "Recalculer les indicateurs sur les filtres configurés. Les lignes d’origine sont conservées pour les détails.", () =>
        {
            try
            {
                var a = readCommon(); var b = readComparison();
                DashboardScopeFilter.ValidateList(a); DashboardScopeFilter.ValidateList(b);
                Change(() => { Board.Filters = a; Board.ComparisonFilters = b; Board.ComparisonEnabled = comparison.IsChecked == true; _crossFilters.Clear(); });
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }));
        var saved = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var presets = new ComboBox { Width = 210, DisplayMemberPath = nameof(DashboardFilterPreset.Name), ItemsSource = _layout.FilterPresets, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Contextes de consultation enregistrés" };
        saved.Children.Add(presets);
        saved.Children.Add(DescribedCommand("Charger", "Reprendre les filtres d’un contexte mémorisé, sans modifier les widgets.", () =>
        { if (presets.SelectedItem is DashboardFilterPreset preset) Change(() => { Board.Filters = DashboardScopeFilter.CopyList(preset.Filters); _crossFilters.Clear(); }); }, true, true));
        saved.Children.Add(DescribedCommand("Retirer", "Retirer le contexte choisi de la liste locale des dispositions.", () =>
        { if (presets.SelectedItem is DashboardFilterPreset preset) Change(() => _layout.FilterPresets.Remove(preset)); }, true, true));
        var name = new TextBox { Width = 190, MaxLength = 120, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Nom du nouveau contexte" }; saved.Children.Add(name);
        saved.Children.Add(DescribedCommand("Mémoriser", "Sauvegarder les filtres actuellement appliqués sous ce nom.", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = "Saisissez un nom pour le contexte."; return; }
            if (_layout.FilterPresets.Count >= 30) { error.Text = "La limite est de 30 contextes. Retirez un contexte avant d’en ajouter un."; return; }
            Change(() => _layout.FilterPresets.Add(new() { Name = name.Text.Trim(), Filters = DashboardScopeFilter.CopyList(Board.Filters.Concat(_crossFilters)) }));
        }, true, true));
        edit.Children.Add(saved);
        content.Children.Add(new Expander { Header = "Régler les filtres, comparer et mémoriser un contexte", Content = edit });
        var frame = Card(content); frame.Padding = new Thickness(14); frame.Margin = new Thickness(0, 0, 0, 14); _body.Children.Add(frame);
    }

    private Func<List<DashboardScopeFilter>> ScopeEditor(Panel host, IReadOnlyList<DashboardScopeFilter> initial)
    {
        var forms = new List<Func<DashboardScopeFilter>>();
        var list = new StackPanel(); host.Children.Add(list);
        Button add = null!;
        add = DescribedCommand("+ Condition de périmètre", "Ajouter une condition. Les conditions d’un périmètre sont combinées par ET.", () => { if (forms.Count < 12) Add(new()); }, true, true);
        host.Children.Add(add);
        void Add(DashboardScopeFilter value)
        {
            var row = new StackPanel { Margin = new Thickness(0, 6, 0, 9) };
            var line = new WrapPanel();
            var kind = new ComboBox { Width = 135, ItemsSource = new[] { new Choice("distribution", "Catégorie"), new Choice("metric", "Population"), new Choice("date", "Période datée") }, DisplayMemberPath = "Label", SelectedValuePath = "Key", SelectedValue = value.Kind, Margin = new Thickness(0, 0, 6, 5) };
            var source = new ComboBox { Width = 220, DisplayMemberPath = "Label", SelectedValuePath = "Key", Margin = new Thickness(0, 0, 6, 5) };
            var text = new ComboBox { Width = 240, IsEditable = true, IsTextSearchEnabled = true, Margin = new Thickness(0, 0, 6, 5), ToolTip = "Valeur exacte ; les dates utilisent le format AAAA-MM-JJ" };
            var end = new TextBox { Width = 130, Text = value.Value2, Margin = new Thickness(0, 0, 6, 5), ToolTip = "Date de fin incluse · AAAA-MM-JJ" };
            var exclude = new CheckBox { Content = "Exclure", IsChecked = value.Exclude, Margin = new Thickness(0, 7, 8, 0) };
            Func<DashboardScopeFilter> read = () => new() { Kind = kind.SelectedValue?.ToString() ?? "distribution", SourceKey = source.SelectedValue?.ToString() ?? "", Value = text.Text, Value2 = end.Text, Exclude = exclude.IsChecked == true };
            var remove = DescribedCommand("×", "Retirer cette condition du brouillon.", () => { list.Children.Remove(row); forms.Remove(read); add.IsEnabled = !_busy && forms.Count < 12; }, true, true);
            void FillSources()
            {
                var selected = source.SelectedValue?.ToString() ?? value.SourceKey;
                var mode = kind.SelectedValue?.ToString();
                var choices = mode == "metric" ? (_baseline?.Metrics ?? new()).Where(x => x.Rows.Length > 0 || x.Value == 0).Select(x => new Choice(x.Key, x.Label))
                    : mode == "date" ? DashboardService.Roles.Where(x => x.Key is "created" or "updated" or "resolved" or "closed" or "due").Select(x => new Choice(x.Key, x.Label))
                    : (_baseline?.Distributions ?? new()).Select(x => new Choice(x.Key, x.Title + (x.Available ? "" : " · indisponible")));
                var items = choices.ToList();
                if (!string.IsNullOrEmpty(selected) && items.All(x => x.Key != selected)) items.Add(new(selected, selected + " · indisponible"));
                source.ItemsSource = items; source.SelectedValue = items.Any(x => x.Key == selected) ? selected : items.FirstOrDefault()?.Key;
                text.Visibility = mode == "metric" ? Visibility.Collapsed : Visibility.Visible;
                end.Visibility = mode == "date" ? Visibility.Visible : Visibility.Collapsed;
            }
            void FillValues()
            {
                var previous = text.Text;
                text.ItemsSource = kind.SelectedValue?.ToString() == "distribution" ? _baseline?.Distributions.FirstOrDefault(x => x.Key == source.SelectedValue?.ToString())?.Buckets.Select(x => x.Label).Order(StringComparer.CurrentCultureIgnoreCase).ToArray() : null;
                text.Text = previous;
            }
            foreach (var control in new FrameworkElement[] { kind, source, text, end, exclude, remove }) line.Children.Add(control);
            row.Children.Add(line); list.Children.Add(row); forms.Add(read);
            FillSources(); FillValues(); text.Text = value.Value;
            kind.SelectionChanged += (_, _) => { FillSources(); FillValues(); };
            source.SelectionChanged += (_, _) => FillValues();
            add.IsEnabled = !_busy && forms.Count < 12;
        }
        foreach (var value in initial) Add(value);
        return () => forms.Select(read => read()).ToList();
    }

    private void ActivateRows(int[] rows, string label, string? distributionKey = null, string? bucketLabel = null, string? metricKey = null)
    {
        if (Board.CrossFilterOnClick && _baseline?.RecalculateScope != null && (distributionKey != null || metricKey != null))
        {
            if (_crossFilters.Count + Board.Filters.Count >= 12) { _analysisMessage = "Douze filtres sont déjà actifs. Réinitialisez ou retirez une condition."; Render(); return; }
            var filter = new DashboardScopeFilter { Kind = metricKey != null ? "metric" : "distribution", SourceKey = metricKey ?? distributionKey!, Value = bucketLabel ?? "" };
            if (_crossFilters.Any(x => x.Kind == filter.Kind && x.SourceKey == filter.SourceKey && x.Value == filter.Value)) return;
            _crossFilters.Add(filter); Render();
        }
        else RowsRequested?.Invoke(rows, label);
    }

    private FrameworkElement ComparisonContent(DashboardSnapshot a, DashboardSnapshot b)
    {
        var panel = new StackPanel(); panel.Children.Add(Text("Comparer les périmètres", 16, weight: FontWeights.SemiBold));
        panel.Children.Add(Text("A : " + a.ScopeLabel + "\nB : " + b.ScopeLabel, 11, "MutedBrush", margin: new Thickness(0, 5, 0, 12)));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        void Row(params string[] cells)
        {
            var index = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (var col = 0; col < cells.Length; col++) { var text = Text(cells[col], index == 0 ? 12 : 11, index == 0 ? "TextBrush" : "MutedBrush", index == 0 ? FontWeights.SemiBold : FontWeights.Normal, new Thickness(0, 5, 12, 5)); Grid.SetRow(text, index); Grid.SetColumn(text, col); grid.Children.Add(text); }
        }
        Row("Indicateur", "A", "B", "Écart A − B");
        var keys = Board.Widgets.Where(x => x.Kind == "metric").Select(x => x.SourceKey).Concat(new[] { "total", "active", "overdue", "high", "age-median", "inactivity-p90" }).Distinct().Take(18);
        foreach (var key in keys)
        {
            var left = a.Metric(key); var right = b.Metric(key); if (left == null && right == null) continue;
            string Value(DashboardMetric? metric) => metric?.Value is double value ? MetricNumber(value, metric.Unit) + (metric.Sample.HasValue ? " · n=" + metric.Sample : "") : "Indisponible";
            Row(left?.Label ?? right!.Label, Value(left), Value(right), left?.Value is double av && right?.Value is double bv && left.Unit == right.Unit ? MetricNumber(av - bv, left.Unit) : "—");
        }
        panel.Children.Add(grid);
        panel.Children.Add(Text("Les valeurs utilisent les mêmes définitions et la même date de calcul. Les effectifs et la couverture restent propres à chaque périmètre.", 11, "MutedBrush", margin: new Thickness(0, 10, 0, 0)));
        var frame = Card(panel); frame.Margin = new Thickness(0, 0, 0, 14); return frame;
    }

    private FrameworkElement BuildSectionedBoard()
    {
        var surface = new StackPanel();
        var groups = Board.Widgets.Where(x => !x.Hidden).GroupBy(x => x.Section, StringComparer.Ordinal).ToArray();
        foreach (var group in groups)
        {
            var board = new DashboardBoardPanel { Board = new DashboardBoardSettings { Widgets = group.ToList() }, AllowDrop = _editing, VerticalAlignment = VerticalAlignment.Top };
            _boardPanel = board;
            if (_data != null || _editing) foreach (var widget in group) board.Children.Add(WidgetCard(widget));
            board.DragOver += (_, e) => { e.Effects = CanDrop(e) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; AutoScroll(e); };
            board.Drop += (_, e) => { if (CanDrop(e)) Change(() => { var widget = Board.Widgets.Single(x => x.Id == (string)e.Data.GetData(DragFormat)); widget.Section = group.Key; DashboardLayoutEngine.Move(Board, widget.Id, null); }); e.Handled = true; };
            if (groups.Length == 1 && string.IsNullOrWhiteSpace(group.Key)) { surface.Children.Add(board); continue; }
            var header = new WrapPanel(); header.Children.Add(Text((group.Key.Length == 0 ? "Général" : group.Key) + " · " + group.Count(), 15, weight: FontWeights.SemiBold, margin: new Thickness(0, 7, 12, 6)));
            if (_editing)
            {
                var index = Array.IndexOf(groups, group);
                void MoveSection(int delta) => Change(() =>
                {
                    var ordering = groups.Select(x => x.Key).ToList(); ordering.RemoveAt(index); ordering.Insert(index + delta, group.Key);
                    Board.Widgets = Board.Widgets.OrderBy(x => ordering.IndexOf(x.Section) is var rank && rank >= 0 ? rank : int.MaxValue).ToList();
                });
                header.Children.Add(Mini("↑", "Déplacer toute cette section avant la précédente.", () => MoveSection(-1), index > 0 && !group.Any(x => x.Locked)));
                header.Children.Add(Mini("↓", "Déplacer toute cette section après la suivante.", () => MoveSection(1), index + 1 < groups.Length && !group.Any(x => x.Locked)));
            }
            var expander = new Expander { Header = header, Content = board, IsExpanded = !Board.CollapsedSections.Contains(group.Key), Margin = new Thickness(0, 0, 0, 14) };
            expander.Expanded += (_, _) => { Board.CollapsedSections.Remove(group.Key); Persist(); };
            expander.Collapsed += (_, _) => { if (!Board.CollapsedSections.Contains(group.Key)) Board.CollapsedSections.Add(group.Key); Persist(); };
            surface.Children.Add(expander);
        }
        return surface;
    }

    private void BuildBulkToolbar()
    {
        _selectedWidgets.RemoveWhere(id => Board.Widgets.All(x => x.Id != id));
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(Text(_selectedWidgets.Count + " widgets sélectionnés", 12, "MutedBrush", margin: new Thickness(0, 8, 12, 0)));
        panel.Children.Add(Mini("Tout sélectionner", "Sélectionner tous les widgets visibles.", () => { foreach (var w in Board.Widgets.Where(x => !x.Hidden)) _selectedWidgets.Add(w.Id); Render(); }));
        panel.Children.Add(Mini("Effacer", "Effacer la sélection de widgets.", () => { _selectedWidgets.Clear(); Render(); }, _selectedWidgets.Count > 0));
        if (_selectedWidgets.Count > 0)
        {
            var accent = new ComboBox { Width = 100, ItemsSource = DashboardWidgetSettings.Accents, SelectedItem = "blue", Margin = new Thickness(4, 0, 5, 0) };
            var style = new ComboBox { Width = 110, ItemsSource = DashboardWidgetSettings.Styles, SelectedItem = "surface", Margin = new Thickness(0, 0, 5, 0) };
            var span = new ComboBox { Width = 65, ItemsSource = new[] { 3, 4, 6, 8, 12 }, SelectedItem = 6, Margin = new Thickness(0, 0, 5, 0) };
            var section = new TextBox { Width = 150, MaxLength = 80, ToolTip = "Nom de section · vide pour Général", Margin = new Thickness(0, 0, 5, 0) };
            foreach (var control in new FrameworkElement[] { accent, style, span, section }) panel.Children.Add(control);
            panel.Children.Add(Mini("Appliquer au groupe", "Appliquer accent, style, largeur et section aux widgets sélectionnés non verrouillés.", () => Change(() =>
            { foreach (var widget in Board.Widgets.Where(x => _selectedWidgets.Contains(x.Id) && !x.Locked)) { widget.Accent = accent.SelectedItem?.ToString() ?? "blue"; widget.Style = style.SelectedItem?.ToString() ?? "surface"; widget.Span = (int)(span.SelectedItem ?? 6); widget.Section = section.Text.Trim(); } })));
            panel.Children.Add(Mini("Masquer", "Masquer les widgets sélectionnés. Annuler permet de les retrouver.", () => Change(() => { foreach (var widget in Board.Widgets.Where(x => _selectedWidgets.Contains(x.Id) && !x.Locked)) widget.Hidden = true; _selectedWidgets.Clear(); })));
        }
        _body.Children.Add(panel);
    }

    private void ExportDashboardImage()
    {
        try
        {
            if (_data == null || _busy) return;
            var dialog = new SaveFileDialog { Filter = "Image PNG (*.png)|*.png", DefaultExt = ".png", AddExtension = true, FileName = "GLPI-Dashboard-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var editing = _editing; _editing = false; Render(); UpdateLayout();
            try
            {
                var width = Math.Max(1, _body.ActualWidth); var height = Math.Max(1, _body.ActualHeight);
                var scale = Math.Min(1, 8192d / Math.Max(width, height));
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen())
                { drawing.DrawRectangle((Brush)FindResource("WindowBrush"), null, new Rect(0, 0, width * scale, height * scale)); drawing.PushTransform(new ScaleTransform(scale, scale)); drawing.DrawRectangle(new VisualBrush(_body), null, new Rect(0, 0, width, height)); }
                bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write); encoder.Save(stream);
                _analysisMessage = "Image enregistrée : " + dialog.FileName + (scale < 1 ? " · dimensions adaptées à 8 192 pixels" : "");
            }
            finally { _editing = editing; Render(); }
        }
        catch (Exception error) { _analysisMessage = "Export de l’image : " + error.Message; Render(); }
    }
}
