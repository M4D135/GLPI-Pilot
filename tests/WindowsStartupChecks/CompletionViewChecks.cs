using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot.WindowsStartupChecks;

internal static partial class Program
{
    private static async Task ExerciseCompletionViewsAsync(TabControl tabs)
    {
        _stage = "source mapping and configuration review";
        var sample = Sample(); var raw = sample.Rows.Select(row => row.ToArray()).ToArray();
        var mapping = new MappingAssistantView(sample, Settings);
        var page = new TabItem { Header = "Correspondances · contrôles", Content = mapping }; tabs.Items.Add(page); tabs.SelectedItem = page;
        try
        {
            await FlushLayoutAsync(); DashboardSettings? applied = null; mapping.ApplyRequested += settings => applied = settings;
            Click(mapping, "Appliquer après vérification"); await FlushLayoutAsync();
            Require(applied == null, "Mapping must display its interpretation preview before emitting a configuration.");
            Require(Descendants<DataGrid>(mapping).Single().Items.Count > 0, "The mapping preview did not render observed raw/interpreted values.");
            Click(mapping, "Appliquer après vérification");
            Require(applied != null && raw.SelectMany(row => row).SequenceEqual(sample.Rows.SelectMany(row => row)), "Mapping application changed source cells or lost its explicit apply event.");
            Pass("Source mapping previews raw/interpreted cells before explicit application and preserves every source value");

            var localMapping = new MappingAssistantView(sample, Settings, applyLabel: "Appliquer au relevé local uniquement", scopeHint: "Relevé local pour cette session ; aucun profil GLPI modifié.");
            page.Content = localMapping; await FlushLayoutAsync(); DashboardSettings? localApplied = null; localMapping.ApplyRequested += settings => localApplied = settings;
            Click(localMapping, "Appliquer au relevé local uniquement"); await FlushLayoutAsync(); Require(localApplied == null, "Local mapping bypassed its interpretation preview.");
            Click(localMapping, "Appliquer au relevé local uniquement"); Require(localApplied != null, "Explicit local mapping destination was not actionable.");
            Pass("Local-result mapping exposes its explicit destination and requires the interpretation preview before application");

            var archiveMapping = new MappingAssistantView(sample, Settings, readOnly: true, scopeHint: "Archive en lecture seule");
            page.Content = archiveMapping; await FlushLayoutAsync();
            Require(!Descendants<Button>(archiveMapping).Any(button => button.Content?.ToString()?.StartsWith("Appliquer", StringComparison.Ordinal) == true), "Archived interpretation exposes an apply action.");
            Require(Descendants<DataGrid>(archiveMapping).Single().Items.Count > 0, "Read-only archive interpretation did not display its preview.");
            Descendants<TabControl>(archiveMapping).Single().SelectedIndex = 0; await FlushLayoutAsync();
            Require(!Descendants<DataGrid>(archiveMapping).Single().IsEnabled, "Archived column mappings remain editable.");
            Pass("Archived mapping renders its recorded interpretation without an edit or apply action");

            var current = new AppSettings { PortalUrl = sample.SourceUrl, Sources = new() { new() { Name = "Source locale", Url = sample.SourceUrl } } }; current.Normalize();
            var incoming = current.Copy(); incoming.Sources[0].Name = "Source importée";
            var review = new ConfigurationReviewView(current, incoming); page.Content = review; await FlushLayoutAsync();
            var sections = Descendants<TabControl>(review).Single();
            var merge = Descendants<CheckBox>(review).Single(box => box.Content?.ToString()?.StartsWith("Fusionner les listes", StringComparison.Ordinal) == true); merge.IsChecked = true;
            sections.SelectedIndex = 1; await FlushLayoutAsync();
            var conflicts = Descendants<DataGrid>(review).Single();
            foreach (var conflict in conflicts.Items.OfType<ConfigurationConflict>()) conflict.Choice = "both";
            Click(review, "Actualiser l’aperçu détaillé"); await FlushLayoutAsync();
            Require(Descendants<DataGrid>(review).Single().Items.Count > 0, "Configuration review did not render the chosen field-level changes.");
            AppSettings? result = null; review.ApplyRequested += (parts, merging) => result = ConfigurationReview.Apply(current, incoming, parts, merging, review.Decisions);
            Click(review, "Appliquer les sections cochées");
            Require(result?.Sources.Count == 2 && result.Sources.Select(s => s.Id).Distinct().Count() == 2 && current.Sources.Single().Name == "Source locale", "Conflict review lost an explicit keep-both choice or mutated the current configuration.");
            Pass("Configuration review renders exact proposed changes and applies an explicit keep-both conflict decision without mutating current settings");

            _stage = "table reading scopes and persisted column layout";
            var path = Path.Combine(TemporaryRoot, "completion-table-layouts.json");
            var grid = new DataGrid { Name = "CompletionGrid", AutoGenerateColumns = false, IsReadOnly = true };
            grid.Columns.Add(new DataGridTextColumn { Header = "ID", SortMemberPath = "Id", Binding = new Binding("Id"), Width = 110 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Nom", SortMemberPath = "Name", Binding = new Binding("Name"), Width = 200 });
            page.Content = grid; var rowsA = new[] { new { Id = "42", Name = "Installation A" } }; grid.ItemsSource = rowsA; await FlushLayoutAsync();
            using (var memory = new GridWorkspaceMemory(path))
            {
                memory.SetScope(grid, "installation-a/ticket"); memory.Attach(page); grid.SelectedIndex = 0;
                grid.Columns[1].DisplayIndex = 0; grid.Columns[0].Width = 175;
                memory.SetScope(grid, "installation-b/ticket"); grid.ItemsSource = new[] { new { Id = "42", Name = "Installation B" } }; await FlushLayoutAsync();
                Require(grid.SelectedItem == null, "A matching ID from another GLPI installation inherited a previous source selection.");
                memory.SetScope(grid, "installation-a/ticket"); grid.ItemsSource = rowsA.ToArray(); await FlushLayoutAsync();
                Require(grid.SelectedItem == rowsA[0], "Returning to the same source did not restore its selected object.");

                var localA = new ExtractionResult { Provenance = new() { SourceKind = "file" } };
                var localB = new ExtractionResult { Provenance = new() { SourceKind = "demo" } };
                memory.SetScope(grid, localA); grid.ItemsSource = rowsA.ToArray(); await FlushLayoutAsync();
                Require(grid.SelectedItem == null, "An unverified local result inherited a remote selection."); grid.SelectedIndex = 0;
                memory.SetScope(grid, localB); grid.ItemsSource = new[] { new { Id = "42", Name = "Autre fichier ou démonstration" } }; await FlushLayoutAsync();
                Require(grid.SelectedItem == null, "Two unverified results shared the empty source scope.");
                memory.SetScope(grid, localA); grid.ItemsSource = rowsA.ToArray(); await FlushLayoutAsync();
                Require(grid.SelectedItem == rowsA[0], "Returning to the same local result lost its selected object.");
            }
            grid.Columns[1].DisplayIndex = 1; grid.Columns[0].Width = 110;
            using (var memory = new GridWorkspaceMemory(path)) { memory.Attach(page); await FlushLayoutAsync(); Require(grid.Columns[1].DisplayIndex == 0 && grid.Columns[0].Width.Value == 175, "Column order/width did not survive a new memory service."); }
            Pass("Table selection stays isolated between installations and unverified local results; returning restores its object and saved column order/width survive reload");

            _stage = "real Windows monitor placement";
            var before = new InterfacePreferences(); MonitorPlacement.Remember(_host!, before);
            try
            {
                var monitors = MonitorPlacement.Available().Where(m => m.Id.Length > 0).ToArray(); Require(monitors.Length > 0, "No Windows work area was enumerated.");
                var chosen = monitors[0]; var outside = new InterfacePreferences { PreferredMonitor = chosen.Id, WindowPixelBounds = new double[] { -100000, -100000, 900, 600 } };
                MonitorPlacement.Restore(_host!, outside); await FlushLayoutAsync(); MonitorPlacement.Remember(_host!, outside);
                var bounds = outside.WindowPixelBounds;
                Require(bounds.Length == 4 && bounds[0] >= chosen.Left && bounds[1] >= chosen.Top && bounds[0] + bounds[2] <= chosen.Left + chosen.Width && bounds[1] + bounds[3] <= chosen.Top + chosen.Height,
                    "An off-screen window was not moved into the selected available work area.");
                Pass("Windows enumerates actual work areas and restores an off-screen window inside the selected available monitor");
            }
            finally { MonitorPlacement.Restore(_host!, before); await FlushLayoutAsync(); }
        }
        finally { tabs.Items.Remove(page); }
        await RunAnalyticsAssistantViewChecksAsync(tabs);
    }
}
