using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;
using Microsoft.Win32;

namespace GlpiPilot.UI;

public sealed partial class HistoryWorkspaceView
{
    private readonly ObservableCollection<SitePopulationEntry> _populations = new();
    private readonly DataGrid _populationTable = PilotControls.Table();
    private readonly ComboBox _populationSite = new() { Width = 240, DisplayMemberPath = "Name", SelectedValuePath = "Id", Margin = new Thickness(0, 0, 8, 8) };
    private readonly ComboBox _populationUnit = new() { Width = 140, DisplayMemberPath = "Label", SelectedValuePath = "Key", ItemsSource = new[] { new QueryChoice("people", "Personnes"), new QueryChoice("workstations", "Postes") }, SelectedIndex = 0, Margin = new Thickness(0, 0, 8, 8) };
    private readonly TextBox _populationFrom = PilotControls.Input(DateTime.Today.ToString("yyyy-MM-dd"), 120), _populationUntil = PilotControls.Input(DateTime.Today.AddYears(1).ToString("yyyy-MM-dd"), 120);
    private readonly TextBox _populationCount = PilotControls.Input("", 100), _populationReference = PilotControls.Input("", 280);
    private readonly TextBlock _populationStatus = PilotControls.Text("Actualisez le contexte pour renseigner les effectifs d’une recherche GLPI confirmée.", 12, true);
    private string _populationSourceKey = "";
    public event Action? PopulationRefreshRequested;
    public event Action<IReadOnlyList<SitePopulationEntry>>? PopulationSaveRequested;

    private FrameworkElement BuildPopulations()
    {
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel(); header.Children.Add(PilotControls.Text("Effectifs et postes par site", 18));
        header.Children.Add(PilotControls.Text("Inventaire local daté servant aux taux pour 100 unités. Utilisez la même recherche GLPI que le relevé analysé. Les périodes manquantes, expirées ou ambiguës ne donnent aucun dénominateur. Les données restent locales.", 12, true));
        var actions = new WrapPanel(); actions.Children.Add(PilotControls.Button("Actualiser le contexte et les effectifs", () => PopulationRefreshRequested?.Invoke()));
        actions.Children.Add(PilotControls.Button("Importer un référentiel JSON…", ImportPopulations));
        actions.Children.Add(PilotControls.Button("Exporter le référentiel…", () =>
        {
            try { var dialog = new SaveFileDialog { Filter = "Référentiel daté (*.json)|*.json", FileName = "GLPI-effectifs-sites.json" }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) AnalyticsPopulationStore.Write(dialog.FileName, _populations); }
            catch (Exception ex) { _populationStatus.Text = ex.Message; }
        })); header.Children.Add(actions);
        var form = new WrapPanel();
        void Field(string title, FrameworkElement input) { var box = new StackPanel { Margin = new Thickness(0, 6, 8, 4) }; box.Children.Add(PilotControls.Text(title, 11, true)); box.Children.Add(input); form.Children.Add(box); }
        Field("Site", _populationSite); Field("Unité", _populationUnit); Field("Effectif positif", _populationCount); Field("Valide du (ISO)", _populationFrom); Field("Au inclus (ISO)", _populationUntil); Field("Source / référence", _populationReference);
        header.Children.Add(form); var edits = new WrapPanel();
        edits.Children.Add(PilotControls.Button("Ajouter ou remplacer cette période", () =>
        {
            try
            {
                if (_populationSite.SelectedItem is not SiteDefinition site || !int.TryParse(_populationCount.Text, out var count)) throw new InvalidDataException("Choisissez un site et saisissez un effectif entier.");
                var entry = new SitePopulationEntry { SourceKey = _populationSourceKey, SiteId = site.Id, SiteLabel = site.Name, Unit = _populationUnit.SelectedValue?.ToString() ?? "people", Count = count,
                    FromDate = _populationFrom.Text.Trim(), UntilDate = _populationUntil.Text.Trim(), Reference = _populationReference.Text.Trim() };
                var proposed = _populations.Where(x => !(x.SourceKey == entry.SourceKey && x.SiteId == entry.SiteId && x.Unit == entry.Unit && x.FromDate == entry.FromDate)).Select(x => x.Copy()).ToList(); proposed.Add(entry);
                AnalyticsPopulationStore.Validate(proposed); PopulationSaveRequested?.Invoke(proposed);
            }
            catch (Exception ex) { _populationStatus.Text = ex.Message; }
        }));
        edits.Children.Add(PilotControls.Button("Retirer la période sélectionnée", () => { if (_populationTable.SelectedItem is SitePopulationEntry entry) PopulationSaveRequested?.Invoke(_populations.Where(x => !ReferenceEquals(x, entry)).Select(x => x.Copy()).ToArray()); }));
        header.Children.Add(edits); header.Children.Add(_populationStatus); panel.Children.Add(header);
        foreach (var (title, key, width) in new[] { ("Site", "SiteLabel", -1), ("Unités", "UnitLabel", 110), ("Effectif", "Count", 90), ("Du", "FromDate", 110), ("Au", "UntilDate", 110), ("Référence", "Reference", 240) }) PilotControls.Column(_populationTable, title, key, width);
        _populationTable.SelectionChanged += (_, _) => { if (_populationTable.SelectedItem is SitePopulationEntry entry) { _populationSite.SelectedValue = entry.SiteId; _populationUnit.SelectedValue = entry.Unit; _populationFrom.Text = entry.FromDate; _populationUntil.Text = entry.UntilDate; _populationCount.Text = entry.Count.ToString(); _populationReference.Text = entry.Reference; } };
        Grid.SetRow(_populationTable, 1); panel.Children.Add(_populationTable); return panel;
    }
    private void ImportPopulations()
    {
        try
        {
            var dialog = new OpenFileDialog { Filter = "Référentiel daté (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var incoming = AnalyticsPopulationStore.Read(dialog.FileName);
            var merged = _populations.Select(x => x.Copy()).ToList(); var replaced = 0;
            foreach (var entry in incoming)
            {
                replaced += merged.RemoveAll(x => x.SourceKey == entry.SourceKey && x.SiteId == entry.SiteId && x.Unit == entry.Unit && x.FromDate == entry.FromDate);
                merged.Add(entry.Copy());
            }
            AnalyticsPopulationStore.Validate(merged);
            if (MessageBox.Show(Window.GetWindow(this), $"Référentiel validé : {incoming.Count:N0} périodes, dont {replaced:N0} remplacements de même site/unité/date. Enregistrer cet inventaire local ?", "Importer les effectifs datés", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK)
                PopulationSaveRequested?.Invoke(merged);
        }
        catch (Exception ex) { _populationStatus.Text = ex.Message; }
    }
    public void ShowPopulations(string sourceKey, IReadOnlyList<SiteDefinition> sites, IReadOnlyList<SitePopulationEntry> entries)
    {
        _populationSourceKey = sourceKey; _populationSite.ItemsSource = sites; _populations.Clear(); foreach (var entry in entries) _populations.Add(entry.Copy());
        _populationTable.ItemsSource = _populations.Where(e => e.SourceKey == sourceKey).OrderBy(e => e.SiteLabel).ThenBy(e => e.FromDate).ToArray();
        _populationStatus.Text = sourceKey.Length == 0 ? "Le relevé courant n’a pas de contexte GLPI confirmé. Chargez un relevé vérifié avant d’ajouter des effectifs." : $"{sites.Count:N0} sites disponibles · {_populations.Count(e => e.SourceKey == sourceKey):N0} périodes pour le contexte courant. Les changements recalculent les dashboards.";
    }
}
