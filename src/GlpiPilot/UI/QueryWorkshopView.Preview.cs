using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class QueryWorkshopView
{
    private readonly TextBlock _previewValue = Text("—", 36, "TextBrush", FontWeights.SemiBold);
    private readonly TextBlock _previewSample = Text("Aucun calcul demandé", 12, "MutedBrush");
    private readonly TextBlock _previewMessage = Text("Prévisualisez pour vérifier la requête avant de l’enregistrer.", 13, "MutedBrush");
    private readonly TextBlock _previewExplanation = Text("", 12, "MutedBrush");
    private readonly TextBlock _bucketTitle = Text("Répartition des lignes retenues", 14, "TextBrush", FontWeights.SemiBold);
    private readonly TextBlock _bucketNote = Text("", 12, "MutedBrush");
    private readonly TextBlock _bucketSummary = Text("", 12, "MutedBrush");
    private readonly Grid _bucketPanel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _distributionEmpty = Text("Prévisualisez une requête avec un regroupement pour consulter ici toutes ses catégories.", 14, "MutedBrush");
    private readonly DataGrid _buckets = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Single,
        SelectionUnit = DataGridSelectionUnit.FullRow, MinHeight = 0, RowHeight = 36, Margin = new Thickness(0, 9, 0, 10)
    };
    private readonly Button _previewRows = Command("Voir les lignes retenues"), _contributionRows = Command("Voir les contributions au calcul"), _bucketRows = Command("Voir les lignes de cette catégorie");
    private QueryResult? _previewResult;
    private ExtractionResult? _previewSource;
    private string? _previewQueryId;
    private long _previewGeneration = -1;

    private FrameworkElement BuildPreview()
    {
        var panel = new StackPanel();
        var heading = new DockPanel();
        DockPanel.SetDock(_cancelPreview, Dock.Right); heading.Children.Add(_cancelPreview);
        heading.Children.Add(Text("Prévisualisation", 18, "TextBrush", FontWeights.SemiBold)); panel.Children.Add(heading);
        _sourceInfo.Margin = new Thickness(0, 6, 0, 8); panel.Children.Add(_sourceInfo);
        panel.Children.Add(Text("Calcul explicite sur le dernier relevé chargé. Modifier une condition ou changer de relevé invalide le résultat précédent.", 12, "MutedBrush", margin: new Thickness(0, 3, 0, 12)));
        panel.Children.Add(_previewValue); panel.Children.Add(_previewSample);
        _previewMessage.Margin = new Thickness(0, 10, 0, 0); _previewExplanation.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_previewMessage); panel.Children.Add(_previewExplanation);
        _bucketNote.Margin = new Thickness(0, 6, 0, 0); panel.Children.Add(_bucketNote);
        var rowActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) }; rowActions.Children.Add(_previewRows); rowActions.Children.Add(_contributionRows); panel.Children.Add(rowActions);
        var openDistribution = Command("Consulter la répartition →");
        openDistribution.Click += (_, _) => _sections.SelectedItem = _distributionTab; panel.Children.Add(openDistribution);
        Section(panel, "Utiliser cette requête dans un dashboard", "Le widget reprend la requête enregistrée. Son résultat se recalculera avec les prochains relevés et le périmètre du dashboard.");
        _widgetScope.MaxWidth = 300; _widgetScope.HorizontalAlignment = HorizontalAlignment.Stretch; panel.Children.Add(_widgetScope);
        var widgets = new WrapPanel(); widgets.Children.Add(_addMetric); widgets.Children.Add(_addDistribution); panel.Children.Add(widgets); panel.Children.Add(_widgetHint);
        return FormViewport(Card(panel));
    }

    private FrameworkElement BuildDistribution()
    {
        var panel = new Grid();
        _distributionEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _distributionEmpty.VerticalAlignment = VerticalAlignment.Center;
        _distributionEmpty.MaxWidth = 440; _distributionEmpty.Margin = new Thickness(18);
        panel.Children.Add(_distributionEmpty);
        _bucketPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _bucketPanel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _bucketPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _bucketTitle.TextWrapping = TextWrapping.NoWrap; _bucketTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        var heading = new StackPanel(); heading.Children.Add(_bucketTitle); heading.Children.Add(_bucketSummary);
        _bucketPanel.Children.Add(heading); Grid.SetRow(_buckets, 1); _bucketPanel.Children.Add(_buckets);
        Grid.SetRow(_bucketRows, 2); _bucketPanel.Children.Add(_bucketRows); panel.Children.Add(_bucketPanel);
        _buckets.Columns.Add(new DataGridTextColumn { Header = "Catégorie", Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 200,
            Binding = new Binding(nameof(DashboardBucket.Label)) { Mode = BindingMode.OneWay } });
        _buckets.Columns.Add(new DataGridTextColumn { Header = "Lignes", Width = 130,
            Binding = new Binding(nameof(DashboardBucket.Count)) { Mode = BindingMode.OneWay, StringFormat = "N0" } });
        VirtualizingPanel.SetIsVirtualizing(_buckets, true); VirtualizingPanel.SetVirtualizationMode(_buckets, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(_buckets, true);
        AutomationProperties.SetName(_buckets, "Répartition complète des résultats de la requête, double-cliquer pour voir les lignes");
        return panel;
    }

    private void AttachPreviewHandlers()
    {
        _previewRows.Click += (_, _) =>
        {
            if (PreviewIsCurrent() && _previewResult!.Available) RowsRequested?.Invoke(_previewResult.Rows, "Requête · " + SelectedQuery!.Name + " · lignes retenues");
        };
        _contributionRows.Click += (_, _) =>
        {
            if (PreviewIsCurrent() && _previewResult!.Available && _previewResult.Metric.Value.HasValue)
                RowsRequested?.Invoke(_previewResult.Metric.Rows, "Requête · " + SelectedQuery!.Name + " · contributions au calcul");
        };
        _bucketRows.Click += (_, _) => OpenSelectedBucket();
        _buckets.SelectionChanged += (_, _) => UpdatePreviewActions();
        _buckets.MouseDoubleClick += (_, args) =>
        {
            if (args.ChangedButton == MouseButton.Left && args.OriginalSource is DependencyObject element
                && ItemsControl.ContainerFromElement(_buckets, element) is DataGridRow { Item: DashboardBucket bucket }) OpenBucket(bucket);
        };
        _buckets.KeyDown += (_, args) => { if (args.Key == Key.Enter && _buckets.IsKeyboardFocusWithin) { OpenSelectedBucket(); args.Handled = true; } };
    }

    private bool PreviewIsCurrent() => !_disposed && !_busy && !_previewRunning && _previewResult != null
        && ReferenceEquals(_source, _previewSource) && _previewGeneration == _generation && _selectedId == _previewQueryId;

    private void UpdatePreviewActions()
    {
        var valid = PreviewIsCurrent();
        _previewRows.IsEnabled = valid && _previewResult!.Available;
        _contributionRows.IsEnabled = valid && _previewResult!.Available && _previewResult.Metric.Value.HasValue;
        _bucketRows.IsEnabled = valid && _previewResult!.Distribution?.Available == true && _buckets.SelectedItem is DashboardBucket;
    }

    private void OpenSelectedBucket()
    {
        if (_buckets.SelectedItem is DashboardBucket bucket) OpenBucket(bucket);
    }

    private void OpenBucket(DashboardBucket bucket)
    {
        if (!PreviewIsCurrent() || _previewResult!.Distribution is not { Available: true } distribution
            || !distribution.Buckets.Any(item => ReferenceEquals(item, bucket))) return;
        RowsRequested?.Invoke(bucket.Rows, "Requête · " + SelectedQuery!.Name + " · " + bucket.Label);
    }

    private void InvalidatePreview(string message)
    {
        _generation++; _previewCancellation?.Cancel(); _previewCancellation = null; _previewRunning = false;
        _previewResult = null; _previewSource = null; _previewQueryId = null; _previewGeneration = -1;
        _previewValue.Text = "—"; _previewSample.Text = _source == null ? "Aucun relevé chargé" : "Résultat à recalculer";
        _previewRows.Content = "Voir les lignes retenues"; _contributionRows.Content = "Voir les contributions au calcul";
        _previewMessage.Text = message; _previewMessage.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); _previewExplanation.Text = "";
        _bucketNote.Text = ""; _bucketSummary.Text = "";
        _buckets.ItemsSource = null; _bucketPanel.Visibility = Visibility.Collapsed; _distributionEmpty.Visibility = Visibility.Visible; UpdateEnabled();
    }

    private void StartPreview()
    {
        if (_busy || _disposed || _source == null || _sourceSettings == null || _previewRunning) return;
        _sections.SelectedItem = _resultTab;
        try
        {
            CommitDraft(); if (SelectedQuery is not { } current) return;
            current.Validate(); var query = current.Copy(); var source = _source; var settings = _sourceSettings.Copy();
            var cities = _cities != null && ReferenceEquals(_cities.Data, source) ? _cities : null;
            InvalidatePreview("Calcul de la requête en cours…");
            var generation = _generation; var cancellation = new CancellationTokenSource(); _previewCancellation = cancellation; _previewRunning = true;
            _previewValue.Text = "…"; _previewSample.Text = $"Lecture de {source.Rows.Count:N0} lignes"; UpdateEnabled();
            var task = CalculatePreviewAsync(source, settings, cities, query, generation, cancellation);
            _pending.Add(task); _ = RemoveCompletedAsync(task);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            _previewMessage.Text = error.Message; _previewMessage.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        }
    }

    private async Task RemoveCompletedAsync(Task task)
    {
        try { await task; }
        finally { _pending.Remove(task); }
    }

    private async Task CalculatePreviewAsync(ExtractionResult source, DashboardSettings settings, CityEnrichmentResult? cities,
        SavedQuery query, long generation, CancellationTokenSource cancellation)
    {
        try
        {
            var token = cancellation.Token;
            var result = await Task.Run(() =>
            {
                var snapshot = DashboardService.Calculate(source, settings, ct: token);
                if (cities != null) CityDetectionService.AppendDashboardProbes(snapshot, cities, token);
                return QueryService.Evaluate(source, settings, snapshot, query, token);
            }, token);
            if (_disposed || token.IsCancellationRequested || generation != _generation || !ReferenceEquals(source, _source) || query.Id != _selectedId) return;
            _previewResult = result; _previewSource = source; _previewQueryId = query.Id; _previewGeneration = generation;
            var metric = result.Metric;
            _previewValue.Text = result.Available && metric.Value is double value && double.IsFinite(value)
                ? value.ToString("G", CultureInfo.CurrentCulture) + (string.IsNullOrWhiteSpace(metric.Unit) ? "" : " " + metric.Unit) : "—";
            _previewSample.Text = result.Available
                ? $"{result.Rows.Length:N0} lignes retenues · échantillon du calcul : "
                    + (metric.Sample.HasValue ? metric.Sample.Value.ToString("N0", CultureInfo.CurrentCulture) : "non précisé") + $" · {DateTime.Now:HH:mm:ss}"
                : $"Échantillon indisponible · {DateTime.Now:HH:mm:ss}";
            _previewMessage.Text = result.Message; _previewMessage.SetResourceReference(TextBlock.ForegroundProperty, result.Available ? "MutedBrush" : "WarningBrush");
            _previewExplanation.Text = metric.Explanation;
            _previewRows.Content = result.Available ? $"Voir les {result.Rows.Length:N0} lignes retenues" : "Voir les lignes retenues";
            _contributionRows.Content = result.Available ? $"Voir les {metric.Rows.Length:N0} contributions au calcul" : "Voir les contributions au calcul";
            if (result.Distribution is { } distribution)
            {
                _bucketPanel.Visibility = Visibility.Visible; _distributionEmpty.Visibility = Visibility.Collapsed; _bucketTitle.Text = distribution.Title;
                _bucketTitle.ToolTip = distribution.Title;
                _bucketSummary.Text = distribution.Available
                    ? $"{distribution.Buckets.Count:N0} catégories · base : {distribution.Denominator:N0} lignes · détails dans Résultat"
                    : "Répartition indisponible · détails dans Résultat";
                _bucketNote.Text = distribution.Available
                    ? distribution.Note + $" · {distribution.Buckets.Count:N0} catégories · base : {distribution.Denominator:N0} lignes"
                    : "Répartition indisponible. " + distribution.Note;
                _buckets.ItemsSource = distribution.Available ? distribution.Buckets : null;
                _buckets.Visibility = distribution.Available ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_disposed && generation == _generation)
            {
                _previewValue.Text = "—"; _previewSample.Text = "Calcul non disponible";
                _previewMessage.Text = "La prévisualisation n’a pas abouti : " + error.Message;
                _previewMessage.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
            if (!_disposed && generation == _generation) { _previewRunning = false; UpdateEnabled(); }
        }
    }
}
