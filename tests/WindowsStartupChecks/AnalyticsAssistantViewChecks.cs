using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot.WindowsStartupChecks;

internal static partial class Program
{
    private static async Task RunAnalyticsAssistantViewChecksAsync(TabControl tabs)
    {
        _stage = "language assistant explicit prepare generate preview and draft flow";
        var data = new ExtractionResult
        {
            Headers = new() { "ID", "Titre", "Statut", "Catégorie", "Ville" },
            Rows = new() { new[] { "1", "Incident privé", "Nouveau", "Réseau", "Lyon" }, new[] { "2", "Poste", "Clos", "Matériel", "Paris" }, new[] { "3", "VPN", "Nouveau", "Réseau", "Lyon" } },
            ExtractedAt = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), Complete = true, ExpectedTotal = 3, Scope = "Recherche synthétique vérifiée"
        };
        var settings = new DashboardSettings(); var snapshot = DashboardService.Calculate(data, settings, data.ExtractedAt);
        var previewCalls = 0; var accepted = 0; var current = true; int[]? citedRows = null;
        using var workshop = new QueryWorkshopView(); workshop.LoadSettings(new QueryLibrarySettings()); workshop.Show(data, settings);
        var stored = false; workshop.SaveRequested += _ => stored = true;
        using var assistant = new LanguageAssistantView(data, settings, snapshot, () => current,
            (query, ct) => { previewCalls++; return Task.FromResult(QueryService.Evaluate(data, settings, snapshot, query, ct)); },
            new LanguageAssistantStore(Path.Combine(TemporaryRoot, "assistant-ui.local.json")));
        assistant.DraftAccepted += query => { accepted++; workshop.AddAssistedDraft(query); };
        assistant.RowsRequested += (rows, _) => citedRows = rows;
        var page = new TabItem { Header = "Analytics · contrôles", Content = assistant }; tabs.Items.Add(page); tabs.SelectedItem = page;
        try
        {
            await FlushLayoutAsync();
            Require(previewCalls == 0 && accepted == 0 && !AnalyticsField<Button>(assistant, "_generate").IsEnabled, "Opening the assistant generated or executed a proposal.");
            Click(assistant, "Préparer le contenu"); await assistant.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
            var prepared = AnalyticsField<TextBox>(assistant, "_preparation").Text;
            Require(prepared.Contains("aucun envoi réseau") && !prepared.Contains("Incident privé") && previewCalls == 0 && accepted == 0,
                "Preparing a query sent ticket content or executed a draft implicitly.");
            Click(assistant, "Générer la proposition"); await assistant.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
            Require(AnalyticsField<TextBox>(assistant, "_queryJson").Text.Contains("role:category") && previewCalls == 0 && accepted == 0 && !AnalyticsField<Button>(assistant, "_accept").IsEnabled,
                "Local generation failed to produce a draft or bypassed the explicit preview gate.");
            Click(assistant, "Prévisualiser sur le relevé"); await assistant.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
            Require(previewCalls == 1 && AnalyticsField<QueryResult>(assistant, "_previewed").Rows.SequenceEqual(new[] { 0, 2 }) && accepted == 0 && AnalyticsField<Button>(assistant, "_accept").IsEnabled,
                "The explicit assistant preview did not use the real local query engine or added a draft automatically.");
            Click(assistant, "Ajouter au brouillon dans l’atelier");
            Require(accepted == 1 && !stored && AdvancedQueryField<QueryLibrarySettings>(workshop, "_working").Queries.Single().Category == "Assistant",
                "The assistant failed to add an isolated unsaved draft on explicit command.");
            CaptureView("completion-assistant-query");
            Pass("Language assistant prepares schema only, generates without execution, previews on explicit command and adds only an unsaved workshop draft");

            _stage = "language assistant citation navigation and stale source gate";
            AnalyticsField<ComboBox>(assistant, "_operation").SelectedValue = "summary";
            Require(!AnalyticsField<Button>(assistant, "_generate").IsEnabled && AnalyticsField<TextBox>(assistant, "_preparation").Text.Length == 0,
                "Changing the operation retained a previous prepared request.");
            Click(assistant, "Préparer le contenu"); await assistant.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
            Click(assistant, "Générer la proposition"); await assistant.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
            var summary = AnalyticsField<StackPanel>(assistant, "_summaryResult");
            var references = Descendants<Expander>(summary).ToArray(); Require(references.Length == 2, "Local status summary did not render its two observed groups.");
            foreach (var expander in references) expander.IsExpanded = true; await FlushLayoutAsync();
            var citation = Descendants<Button>(summary).Single(button => button.Content is string text && text.StartsWith("#1 · status", StringComparison.Ordinal));
            current = false; citation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Require(citedRows is null, "Stale source indices remained navigable from a summary citation.");
            current = true; citation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Require(citedRows?.SequenceEqual(new[] { 0 }) == true && previewCalls == 1, "Summary citation lost its observed source row or executed a query.");
            Pass("Summary references render exact observed excerpts, navigate to the original source row and reject stale-source navigation");

            _stage = "dated site population UI validates before applying";
            var history = new HistoryWorkspaceView(); page.Content = history; await FlushLayoutAsync();
            var site = new SiteDefinition { Id = Guid.NewGuid().ToString("N"), Name = "Site synthétique" };
            history.ShowPopulations("source-synthetic", new[] { site }, Array.Empty<SitePopulationEntry>());
            AnalyticsField<TabControl>(history, "_tabs").SelectedIndex = 4; await FlushLayoutAsync();
            IReadOnlyList<SitePopulationEntry>? population = null; var saves = 0;
            history.PopulationSaveRequested += entries => { saves++; population = entries; history.ShowPopulations("source-synthetic", new[] { site }, entries); };
            AnalyticsField<ComboBox>(history, "_populationSite").SelectedValue = site.Id;
            AnalyticsField<TextBox>(history, "_populationCount").Text = "100";
            AnalyticsField<TextBox>(history, "_populationFrom").Text = "2026-10-01";
            AnalyticsField<TextBox>(history, "_populationUntil").Text = "2026-09-30";
            AnalyticsField<TextBox>(history, "_populationReference").Text = "Inventaire fictif";
            Click(history, "Ajouter ou remplacer cette période"); Require(saves == 0, "An inverted population validity period was emitted for persistence.");
            AnalyticsField<TextBox>(history, "_populationUntil").Text = "2026-12-31";
            Click(history, "Ajouter ou remplacer cette période");
            Require(saves == 1 && population?.Single() is { SourceKey: "source-synthetic", Count: 100, FromDate: "2026-10-01", UntilDate: "2026-12-31" }, "The dated population editor lost its source, dates or explicit count.");
            var table = AnalyticsField<DataGrid>(history, "_populationTable"); table.SelectedIndex = 0;
            AnalyticsField<TextBox>(history, "_populationCount").Text = "125"; Click(history, "Ajouter ou remplacer cette période");
            Require(saves == 2 && population?.Count == 1 && population[0].Count == 125, "Editing an exact period appended a conflicting denominator instead of replacing the selected period.");
            Pass("Dated site population editor rejects inverted dates and emits only explicit source-bound additions or exact-period replacements");

            _stage = "offline map links to other widgets and native visual export";
            var board = new DashboardBoardSettings { Name = "Carte test", CrossFilterOnClick = true, Widgets = new()
            {
                new() { Kind = "map", SourceKey = "all", Title = "Carte locale", Span = 12, Height = 600 },
                new() { Kind = "metric", SourceKey = "total", Title = "Lignes", Span = 6 }
            } };
            var dashboard = new DashboardView(); dashboard.SetLayout(new() { ActiveBoardId = board.Id, Boards = new() { board } });
            void Geography(DashboardSnapshot value)
            {
                var scopeRows = value.ScopeRows.ToHashSet();
                var points = new[] { new DashboardGeoPoint("lyon", "Lyon", 45.76, 4.84, new[] { 0, 2 }, new[] { 0, 2 }), new DashboardGeoPoint("paris", "Paris", 48.86, 2.35, new[] { 1 }, Array.Empty<int>()) };
                value.Geography.AddRange(points.Select(p => p with { Rows = p.Rows.Where(scopeRows.Contains).ToArray(), ActiveRows = p.ActiveRows.Where(scopeRows.Contains).ToArray() }).Where(p => p.Rows.Length > 0));
                value.Distributions.Add(new("geography-points-all", "Localisations", true, value.RowCount, "Coordonnées synthétiques", value.Geography.Select(p => new DashboardBucket(p.Label, p.Rows.Length, p.Rows)).ToArray()));
            }
            Geography(snapshot);
            snapshot.RecalculateScope = (rows, label, ct) => { var scoped = DashboardService.CalculateSubset(data, settings, rows, label, data.ExtractedAt, ct); Geography(scoped); return scoped; };
            dashboard.Show(snapshot); page.Content = dashboard; await FlushLayoutAsync();
            var mapCanvas = Descendants<Canvas>(dashboard).Single(canvas => canvas.Children.OfType<Button>().Count() == 2);
            var zoom = (ScaleTransform)mapCanvas.LayoutTransform; FindButton(dashboard, "+").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Require(zoom.ScaleX > 1, "Offline map zoom did not change its view.");
            var changed = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            dashboard.SnapshotChanged += value => { if (value.RowCount == 2) changed.TrySetResult(value); };
            var marker = mapCanvas.Children.OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Lyon");
            marker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); var filtered = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
            Require(filtered.Metric("total")?.Value == 2 && filtered.ScopeRows.SequenceEqual(new[] { 0, 2 }) && dashboard.ExportCriteria().Contains("geography-points-all = Lyon"),
                "Clicking a map point did not apply its real rows to the other widgets and exported criteria.");
            var caption = dashboard.ExportCaption(); Require(caption.Contains("Extraction :") && caption.Contains("calcul :") && caption.Contains("complétude :") && caption.Contains("Périmètre :") && caption.Contains("geography-points-all = Lyon"), "Visual export cartouche omitted dates, completeness or the effective cross-filter.");
            var png = Path.Combine(TemporaryRoot, "dashboard-native.png"); var pdf = Path.Combine(TemporaryRoot, "dashboard-native.pdf");
            dashboard.ExportVisualTo(png, false); dashboard.ExportVisualTo(pdf, true);
            using (var stream = File.OpenRead(png)) { var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Single(); Require(bitmap.PixelWidth >= 480 && bitmap.PixelHeight > 200, "Native PNG did not render the live WPF dashboard and cartouche."); }
            Require(new FileInfo(pdf).Length > 1000 && Encoding.ASCII.GetString(File.ReadAllBytes(pdf).Take(8).ToArray()).StartsWith("%PDF-1.4", StringComparison.Ordinal), "Native visual PDF was not produced from the WPF rendering.");
            CaptureView("completion-offline-map");
            Pass("Offline map zoom and point selection recalculate other widgets; native PNG/PDF render the live board with dated scope and completeness cartouche");
        }
        finally { tabs.Items.Remove(page); }
    }

    private static T AnalyticsField<T>(object instance, string name) where T : class
        => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance) as T
            ?? throw new InvalidOperationException("Analytics UI field not available: " + name);
}
