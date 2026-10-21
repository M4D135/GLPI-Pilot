using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;
using GlpiPilot.UI;
using Microsoft.Win32;

namespace GlpiPilot;

public partial class MainWindow
{
    private DashboardView? _dashboardView;
    private DashboardSnapshot? _dashboard;
    private CancellationTokenSource? _dashboardCancellation;
    private readonly Dictionary<string, ComboBox> _dashboardColumns = new();
    private TextBox? _dashboardStaleBox, _dashboardOldBox, _dashboardDueBox;
    private ComboBox? _dashboardDateCulture;
    private StackPanel? _dashboardSettingsPanel;
    private int[]? _dashboardPreviewRows;
    private string _dashboardPreviewLabel = "";

    private void InitializeDashboardWorkspace()
    {
        StartupDiagnostics.Step("Dashboard · construction de la vue principale");
        _dashboardView = new DashboardView();
        DashboardHost.Content = _dashboardView;
        _dashboardView.RowsRequested += ShowDashboardRows;
        _dashboardView.RefreshRequested += () => RefreshDashboardFromFields();
        _dashboardView.SettingsRequested += () => { MainTabs.SelectedItem = SettingsTab; SettingsDashboardHost.BringIntoView(); };
        _dashboardView.ExportRequested += async () => await ExportDashboardAsync();
        _dashboardView.LayoutChanged += layout => SaveDashboardLayout(layout, _dashboardView);
        _dashboardSettingsPanel = new StackPanel();
        _dashboardSettingsPanel.Children.Add(DashboardLabel("Laissez une correspondance vide pour la détection automatique. Si plusieurs colonnes conviennent, choisissez le titre exact. Les dates, seuils et correspondances suivent la configuration partageable."));
        var fields = new WrapPanel();
        foreach (var role in DashboardService.Roles)
        {
            var panel = new StackPanel { Width = 280, Margin = new Thickness(0, 0, 18, 8) };
            panel.Children.Add(DashboardLabel(role.Label));
            var box = new ComboBox { IsEditable = true, IsTextSearchEnabled = false, MinWidth = 250, ToolTip = "Vide : détection automatique. Sinon : intitulé exact de la colonne." };
            panel.Children.Add(box); fields.Children.Add(panel); _dashboardColumns[role.Key] = box;
        }
        _dashboardSettingsPanel.Children.Add(fields);
        var thresholds = new WrapPanel();
        _dashboardStaleBox = DashboardNumberField(thresholds, "Sans modification depuis (jours)");
        _dashboardOldBox = DashboardNumberField(thresholds, "Ticket ancien à partir de (jours)");
        _dashboardDueBox = DashboardNumberField(thresholds, "Échéance proche sous (heures)");
        var dates = new StackPanel { Width = 250, Margin = new Thickness(0, 0, 18, 8) };
        dates.Children.Add(DashboardLabel("Format des dates du relevé"));
        _dashboardDateCulture = new ComboBox();
        foreach (var (label, tag) in new[] { ("Français · jour/mois/année", "fr-FR"), ("Anglais UK · jour/mois/année", "en-GB"), ("Anglais US · mois/jour/année", "en-US"), ("ISO uniquement · année-mois-jour", "invariant") })
            _dashboardDateCulture.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        dates.Children.Add(_dashboardDateCulture); thresholds.Children.Add(dates);
        _dashboardSettingsPanel.Children.Add(thresholds);
        _dashboardSettingsPanel.Children.Add(DashboardLabel("Les heures sont comparées à l’heure locale du poste. Les horodatages ISO avec fuseau sont convertis. Les durées affichées sont calendaires ; les pauses et calendriers SLA ne peuvent pas être reconstruits à partir d’un simple relevé."));
        SettingsDashboardHost.Content = _dashboardSettingsPanel;
        StartupDiagnostics.Step("Techniciens · construction de l’espace personnel");
        InitializeTechnicianWorkspace();
        StartupDiagnostics.Step("Dashboard et techniciens construits");
    }

    private static TextBlock DashboardLabel(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 12, 0, 6) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); return label;
    }
    private static TextBox DashboardNumberField(Panel parent, string title)
    {
        var panel = new StackPanel { Width = 250, Margin = new Thickness(0, 0, 18, 8) };
        panel.Children.Add(DashboardLabel(title)); var box = new TextBox(); panel.Children.Add(box); parent.Children.Add(panel); return box;
    }

    private void LoadDashboardSettingsFields(AppSettings settings)
    {
        var dashboard = settings.Dashboard;
        foreach (var (key, box) in _dashboardColumns)
            box.Text = dashboard.ColumnMappings.GetValueOrDefault(key) ?? "";
        _dashboardStaleBox!.Text = dashboard.StaleDays.ToString(CultureInfo.InvariantCulture);
        _dashboardOldBox!.Text = dashboard.OldDays.ToString(CultureInfo.InvariantCulture);
        _dashboardDueBox!.Text = dashboard.DueSoonHours.ToString(CultureInfo.InvariantCulture);
        _dashboardDateCulture!.SelectedItem = _dashboardDateCulture.Items.Cast<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == dashboard.DateCulture) ?? _dashboardDateCulture.Items[0];
        RefreshDashboardColumnChoices();
        _dashboardView?.SetLayout(dashboard.Layout);
        LoadTechnicianSettingsFields(settings);
    }

    private void ApplyDashboardSettingsFields(AppSettings settings)
    {
        settings.Dashboard = new DashboardSettings
        {
            ColumnMappings = _dashboardColumns.Where(p => !string.IsNullOrWhiteSpace(p.Value.Text)).ToDictionary(p => p.Key, p => p.Value.Text.Trim(), StringComparer.OrdinalIgnoreCase),
            StaleDays = Number(_dashboardStaleBox!, "Inactivité", 1, 3650),
            OldDays = Number(_dashboardOldBox!, "Ticket ancien", 1, 3650),
            DueSoonHours = Number(_dashboardDueBox!, "Échéance proche", 1, 720),
            DateCulture = (_dashboardDateCulture!.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "fr-FR",
            Layout = _dashboardView?.GetLayout() ?? _settings.Dashboard.Layout.Copy(),
            PersonalLayout = _techniciansView?.Dashboard.GetLayout() ?? _settings.Dashboard.PersonalLayout.Copy(),
            PersonalTechnician = _settings.Dashboard.PersonalTechnician,
            OpenPersonalAfterExtraction = _settings.Dashboard.OpenPersonalAfterExtraction
        };
        DashboardService.ValidateSettings(settings.Dashboard);
    }

    private void RefreshDashboardColumnChoices()
    {
        foreach (var box in _dashboardColumns.Values)
        {
            var current = box.Text;
            box.ItemsSource = new[] { "" }.Concat(_result?.Headers ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            box.Text = current;
        }
    }

    private void ResetDashboard()
    {
        _dashboardCancellation?.Cancel();
        _dashboard = null; _dashboardPreviewRows = null; _dashboardPreviewLabel = "";
        ClearDashboardFilterButton.Visibility = Visibility.Collapsed;
        _dashboardView?.ShowEmpty();
        ResetTechnicianWorkspace();
        RefreshDashboardColumnChoices();
    }

    private void RefreshDashboardFromFields()
    {
        if (_closing) return;
        try
        {
            var settings = _settings.Copy(); ApplyDashboardSettingsFields(settings);
            _settings.Dashboard = settings.Dashboard; _settings.Save();
            QueueDashboardCalculation();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void QueueDashboardCalculation()
    {
        _dashboardCancellation?.Cancel();
        var operation = new CancellationTokenSource(); _dashboardCancellation = operation;
        var source = _result;
        _dashboard = null;
        if (source == null) { _dashboardView?.ShowEmpty(); operation.Dispose(); if (_dashboardCancellation == operation) _dashboardCancellation = null; return; }
        _dashboardView?.ShowEmpty("Calcul des indicateurs, des répartitions et de la météo du relevé…");
        RefreshDashboardColumnChoices();
        try
        {
            var allSettings = _settings.Copy(); ApplyDashboardSettingsFields(allSettings);
            var settings = EffectiveDashboardSettings(allSettings.Dashboard);
            var cities = CitySourceIsCurrent() ? _cityEnrichment : null;
            var queries = allSettings.Queries.Copy();
            RefreshTechnicianWorkspace();
            RefreshCityWorkspace(settings);
            RefreshQueryWorkspace(settings);
            var snapshot = await Task.Run(() =>
            {
                var calculated = DashboardService.Calculate(source, settings, ct: operation.Token);
                AppendLocalDashboardProbes(calculated, source, settings, cities, queries, operation.Token);
                return calculated;
            }, operation.Token);
            if (operation.IsCancellationRequested || _closing || !ReferenceEquals(source, _result)) return;
            _dashboard = snapshot; _dashboardView?.Show(snapshot); _dashboardView?.SetBusy(_busy);
            AddLog($"DASHBOARD · {snapshot.RowCount:N0} lignes ; {snapshot.Columns.Count(c => c.Index.HasValue)}/{snapshot.Columns.Count} colonnes reconnues ; {snapshot.Warnings.Count} avertissement(s) ; météo={snapshot.Weather.State}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closing && !operation.IsCancellationRequested)
            { _dashboardView?.ShowEmpty("Calcul indisponible. Vérifiez les correspondances de colonnes dans Réglages."); AddLog("DASHBOARD · " + AppPaths.SafeError(ex)); }
        }
        finally { if (_dashboardCancellation == operation) _dashboardCancellation = null; operation.Dispose(); }
    }

    private void UpdateDashboardControls()
    {
        _dashboardView?.SetBusy(_busy || _closing);
        if (_dashboardSettingsPanel != null) _dashboardSettingsPanel.IsEnabled = !_busy && !_closing;
        ClearDashboardFilterButton.IsEnabled = !_busy;
        UpdateTechnicianControls();
    }

    private void ShowDashboardRows(int[] indices, string label)
    {
        if (_busy || _closing || _result == null) return;
        _dashboardPreviewRows = indices.Where(i => i >= 0 && i < _result.Rows.Count).Distinct().ToArray();
        _dashboardPreviewLabel = label;
        SearchBox.Clear();
        BuildPreview(resetDashboard: false);
        ClearDashboardFilterButton.Visibility = Visibility.Visible;
        MainTabs.SelectedItem = ExtractionTab;
        SetStatus($"{_dashboardPreviewRows.Length:N0} lignes · {label}. Les exports du relevé restent complets.");
    }

    private void ClearDashboardFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _dashboardPreviewRows = null; _dashboardPreviewLabel = "";
        ClearDashboardFilterButton.Visibility = Visibility.Collapsed;
        BuildPreview(resetDashboard: false);
        SetStatus("Toutes les lignes du relevé sont de nouveau accessibles dans l’aperçu.");
    }

    private async Task ExportDashboardAsync()
    {
        if (_dashboard == null || _busy) return;
        var snapshot = _dashboard;
        await RunAsync("Préparation du bilan du tableau de bord…", async _ =>
        {
            var settings = ReadSettings(); Directory.CreateDirectory(settings.ExportFolder);
            var csv = DashboardService.EstimatedExportRows(snapshot) > 1_048_575;
            if (csv) SetStatus("Le bilan dépasse la capacité d’une feuille Excel. Enregistrement complet proposé en CSV.");
            var dialog = new SaveFileDialog { Title = csv ? "Bilan volumineux : exporter toutes les lignes en CSV" : "Exporter le bilan du relevé",
                Filter = csv ? "Bilan complet CSV (*.csv)|*.csv" : "Classeur Excel (*.xlsx)|*.xlsx", DefaultExt = csv ? ".csv" : ".xlsx", AddExtension = true,
                InitialDirectory = settings.ExportFolder, FileName = $"GLPI-Bilan-{DateTime.Now:yyyyMMdd-HHmmss}" + (csv ? ".csv" : ".xlsx") };
            if (dialog.ShowDialog(this) != true) { SetStatus("Enregistrement annulé."); return; }
            CancelButton.IsEnabled = false;
            var exported = await Task.Run(() => { var data = DashboardService.ToExport(snapshot); return csv ? ExportService.WriteCsv(data, dialog.FileName) : ExportService.WriteXlsx(data, dialog.FileName); });
            RememberExport(exported.Path);
            SetStatus("Bilan " + (csv ? "CSV complet" : "Excel") + " enregistré : " + exported.Path);
            AddLog($"DASHBOARD · bilan {(csv ? "CSV complet" : "Excel")} enregistré ; {exported.Rows:N0} lignes de synthèse.");
        });
    }

    private void SaveDashboardLayout(DashboardLayoutSettings layout, DashboardView origin)
    {
        if (_closing) return;
        try
        {
            layout.Validate();
            if (ReferenceEquals(origin, _techniciansView?.Dashboard))
                _settings.Dashboard.PersonalLayout = layout.Copy();
            else
                _settings.Dashboard.Layout = layout.Copy();
            // Each workspace keeps its edited layout in memory even when disk
            // persistence fails. A subsequent extraction cannot restore a stale copy.
            _settings.Save();
            origin.SetSaveState(true);
        }
        catch (Exception ex)
        {
            origin.SetSaveState(false);
            AddLog("DASHBOARD · sauvegarde de la disposition impossible : " + AppPaths.SafeError(ex));
        }
    }

    private void DisposeDashboardWorkspace()
    {
        _dashboardCancellation?.Cancel();
        DisposeTechnicianWorkspace();
    }
}
