using System.IO.Compression;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckMeetingExports()
    {
        var settings = Settings(); var board = settings.Dashboard.Layout.Boards[0];
        board.Widgets.Clear();
        var onlyNew = new List<DashboardScopeFilter> { new() { Kind = "distribution", SourceKey = "status", Value = "Nouveau" } };
        board.Widgets.Add(new() { Id = "volume", Kind = "metric", SourceKey = "total", Title = "Volume choisi", Filters = onlyNew, ShowMiniHistory = false });
        board.Widgets.Add(new() { Id = "groups", Kind = "distribution", SourceKey = "group", Title = "Tous les groupes choisis", Filters = onlyNew, TopCount = 3 });
        board.Widgets.Add(new() { Id = "missing", Kind = "metric", SourceKey = "resolution-mean", Title = "Valeur indisponible", ShowMiniHistory = false });
        board.Widgets.Add(new() { Id = "note", Kind = "note", Title = "=FORMULE_TITRE", Note = "=HYPERLINK(\"https://attacker.invalid\"); <script>alert(1)</script><img src=x onerror=alert(1)>" });
        board.Widgets.Add(new() { Id = "omitted", Kind = "note", Title = "BLOC_OMIS_SECRET", Note = "CONTENU_OMIS_SECRET" });
        board.Widgets.Add(new() { Id = "hidden", Kind = "note", Title = "MASQUE_SECRET", Hidden = true });
        board.Sections.Add(new() { Key = "hidden-section", Title = "Section masquée", Hidden = true });
        board.Widgets.Add(new() { Id = "section-hidden", Kind = "note", Title = "SECTION_MASQUEE_SECRET", Section = "hidden-section" });
        var host = new AnalyticsTestHost();
        await using var app = new PilotApplication(host, (_, _) => { }, settings, snapshots: new SnapshotStore(Path.Combine(_root, "meeting-history")));
        var source = Data(0, Enumerable.Range(1, 75).Select(index => Row(index.ToString(), group: "Groupe " + index.ToString("D3"), title: "Ticket choisi " + index))
            .Append(Row("999", "Résolu", group: "GROUPE_EXCLU_SECRET", title: "TICKET_EXCLU_SECRET")).ToArray());
        host.OpenPath = Path.Combine(_root, "source-reunion.csv"); ExportService.WriteCsv(source, host.OpenPath);
        await Call(app, "records.import");
        var dashboard = await Call(app, "dashboard.get"); var runId = Str(dashboard, "runId"); var revision = app.Revision;
        Check(!app.IsTestMode && dashboard.GetProperty("fileSource").GetBoolean(), "Meeting reports run on an actual imported CSV source without entering demonstration mode.");
        var ids = new[] { "groups", "volume", "note", "missing" };
        var preview = await Call(app, "reports.preview", new { runId, revision, selectedWidgetIds = ids, includeOverview = false });
        var widgets = preview.GetProperty("widgets");
        Check(widgets.EnumerateArray().Select(widget => Str(widget, "id")).SequenceEqual(ids), "The meeting preview preserves the exact selected widget order.");
        Check(Widget(preview, "groups").GetProperty("tables")[0].GetProperty("rows").GetArrayLength() == 75
            && Widget(dashboard, "groups").GetProperty("content").GetProperty("buckets").GetArrayLength() == 3,
            "Selected tables include all 75 groups despite a three-row on-screen widget limit.");
        Check(Str(Widget(preview, "volume"), "metricValue") == "75"
            && Str(Widget(preview, "volume"), "filters").Contains("Nouveau")
            && !preview.GetRawText().Contains("GROUPE_EXCLU_SECRET") && !preview.GetRawText().Contains("CONTENU_OMIS_SECRET"),
            "The selected metric and tables use the widget filter, without omitted blocks or out-of-scope ticket values.");
        Check(preview.GetProperty("context").EnumerateArray().Any(pair => Str(pair, "value") == "Fichier CSV / Excel")
            && preview.GetRawText().Contains("source-reunion.csv") && preview.GetRawText().Contains("Date du relev"),
            "Meeting provenance explicitly includes the file source, original filename, observation date and freshness.");
        foreach (var format in new[] { "html", "csv", "xlsx" })
        {
            host.SavePath = Path.Combine(_root, "meeting-selection." + format);
            await Call(app, "reports.export", new { runId, revision, format, selectedWidgetIds = ids, includeOverview = false });
            string content;
            if (format == "xlsx")
            {
                using var archive = ZipFile.OpenRead(host.SavePath);
                using var reader = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open()); content = reader.ReadToEnd();
                Check(!content.Contains("<f>") && content.Contains("inlineStr"), "Meeting XLSX stores formula-like user text as strings, never spreadsheet formulas.");
            }
            else content = File.ReadAllText(host.SavePath);
            Check(content.Contains("Groupe 075") && content.IndexOf("Tous les groupes choisis", StringComparison.Ordinal) < content.IndexOf("Volume choisi", StringComparison.Ordinal)
                && !content.Contains("CONTENU_OMIS_SECRET") && !content.Contains("BLOC_OMIS_SECRET") && !content.Contains("GROUPE_EXCLU_SECRET") && !content.Contains("TICKET_EXCLU_SECRET"),
                "Meeting " + format.ToUpperInvariant() + " contains complete selected tables in order and no unselected data.");
            if (format == "html")
                Check(content.Contains("&lt;script&gt;") && !content.Contains("<script") && !content.Contains("<img src=x")
                    && content.Contains("default-src 'none'") && content.Contains("<svg") && !content.Contains("<link") && !content.Contains("<details")
                    && content.Contains("size:A4 landscape") && content.Contains("table-layout:fixed"),
                    "Meeting HTML escapes hostile text, embeds its graphics and print styles, and exposes all table rows without network resources or collapsed details.");
            if (format == "csv") Check(content.Contains("'=FORMULE_TITRE") && content.Contains("'=HYPERLINK"), "Meeting CSV neutralizes formula prefixes in widget titles and notes.");
        }
        Check(Str(Widget(preview, "missing"), "metricValue") == "Indisponible"
            && Widget(preview, "missing").GetProperty("tables")[0].GetProperty("rows")[0][1].GetString() == "Indisponible",
            "An unavailable selected metric does not become a numeric zero.");
        foreach (var bad in new[] { Array.Empty<string>(), new[] { "hidden" }, new[] { "section-hidden" }, new[] { "unknown" }, new[] { "volume", "volume" } })
            await Reject<InvalidDataException>(async () => await Call(app, "reports.export", new { runId, revision, format = "html", selectedWidgetIds = bad, includeOverview = false }),
                "Invalid, hidden, empty or duplicate meeting selections are rejected before export: " + string.Join(',', bad));
        await Reject<InvalidDataException>(async () => await Call(app, "reports.export", new { runId, revision, format = "html", selectedWidgetIds = ids, includeOverview = true }),
            "A selected report cannot implicitly append a full-dashboard overview.");
        await Reject<InvalidDataException>(async () => await Call(app, "reports.export", new { runId, revision, format = "png", selectedWidgetIds = ids }),
            "A multi-block selection cannot silently degrade to a cropped viewport PNG.");
        await Reject<InvalidOperationException>(async () => await Call(app, "reports.export", new { runId, revision, format = "pdf", selectedWidgetIds = ids }),
            "Selected PDF exports require a matching prepared preview.");
        await Reject<InvalidOperationException>(async () => await Call(app, "reports.export", new { runId, revision, format = "pdf", selectedWidgetIds = ids.Reverse().ToArray(), selectionToken = Str(preview, "selectionToken") }),
            "A prepared PDF token cannot authorize a different widget order.");
        await Reject<NotSupportedException>(async () => await Call(app, "reports.export", new { runId, revision, format = "pdf", selectedWidgetIds = ids, selectionToken = Str(preview, "selectionToken") }),
            "A valid selected PDF uses the real print host and propagates host failure.");
        File.Delete(host.OpenPath);
        await Call(app, "cache.restore");
        var cached = await Call(app, "dashboard.get");
        var cachedPreview = await Call(app, "reports.preview", new { runId = Str(cached, "runId"), revision = app.Revision, selectedWidgetIds = ids, includeOverview = false });
        Check(app.IsCachedSource && Str(Widget(cachedPreview, "volume"), "metricValue") == "75"
            && cachedPreview.GetProperty("context").EnumerateArray().Any(pair => Str(pair, "value").StartsWith("Cache local hors connexion", StringComparison.Ordinal))
            && cachedPreview.GetProperty("context").EnumerateArray().Any(pair => Str(pair, "key") == "Copie conservée le")
            && cachedPreview.GetProperty("context").EnumerateArray().Any(pair => Str(pair, "key") == "Récupération du cache"),
            "A report restored without the original CSV keeps its filtered values and explicitly dates and labels the offline cache.");
        app.LoadFixture(Data(1, Row("1000")));
        await Reject<InvalidOperationException>(async () => await Call(app, "reports.preview", new { runId, revision, selectedWidgetIds = ids }),
            "A stale meeting selection cannot mix data from a later import.");
    }

    private static async Task CheckMeetingWidgetKinds()
    {
        var settings = Settings(); var board = settings.Dashboard.Layout.Boards[0]; board.Widgets.Clear();
        var keys = new Dictionary<string, string> { ["metric"] = "total", ["distribution"] = "group", ["weather"] = "overall", ["matrix"] = "group-status",
            ["histogram"] = "age", ["boxplot"] = "age-assignee", ["scatter"] = "age-deadline", ["history"] = "total", ["oldest"] = "groups", ["participation"] = "comparison", ["sites"] = "sites" };
        foreach (var kind in DashboardWidgetSettings.Kinds) board.Widgets.Add(new() { Id = kind, Kind = kind, SourceKey = keys.GetValueOrDefault(kind, "active"), Title = "Bloc " + kind, TopCount = 3 });
        var data = Data(0, Enumerable.Range(1, 12).Select(index => Row(index.ToString(), person: "Tech " + index, group: "Groupe " + index)).ToArray());
        await using var app = new PilotApplication(new AnalyticsTestHost(), (_, _) => { }, settings, snapshots: new SnapshotStore(Path.Combine(_root, "meeting-kinds-history")));
        app.LoadFixture(data, Locations(data, settings)); var dashboard = await Call(app, "dashboard.get");
        var preview = await Call(app, "reports.preview", new { runId = Str(dashboard, "runId"), revision = app.Revision, selectedWidgetIds = board.Widgets.Select(widget => widget.Id).ToArray(), includeOverview = false });
        Check(preview.GetProperty("widgets").GetArrayLength() == DashboardWidgetSettings.Kinds.Length,
            "Every supported dashboard widget kind has a selected-report projection.");
        int Rows(string id, int table = 0) => Widget(preview, id).GetProperty("tables")[table].GetProperty("rows").GetArrayLength();
        Check(Rows("oldest") == 12 && Widget(preview, "participation").GetProperty("tables")[0].GetProperty("rows").EnumerateArray().Count(row => row[0].GetString()!.StartsWith("Tech ", StringComparison.Ordinal)) == 12 && Rows("sites", 1) == 12 && Rows("attention") == 12,
            "Selected oldest, participation, site-ticket and attention tables include all rows beyond their three-item screen previews.");
        Check(Rows("matrix") == Int(Widget(dashboard, "matrix").GetProperty("content"), "total") && Rows("matrix") >= 12
            && Rows("boxplot") == 12 && Rows("scatter") == 12 && Rows("map") == 1,
            $"Matrix, boxplot, scatter and map exports retain their complete selected series ({Rows("matrix")}/{Rows("boxplot")}/{Rows("scatter")}/{Rows("map")}).");
        Check(Widget(preview, "timeline").GetProperty("tables")[0].GetProperty("rows").EnumerateArray().All(row => row[2].GetString() == "Indisponible" && row[3].GetString() == "Indisponible"),
            "Missing resolution and closure columns remain unavailable in each exported timeline day.");
        var invalid = Data(0, Row("99")); invalid.Rows[0][5] = "date inconnue"; app.LoadFixture(invalid);
        var invalidRun = await Call(app, "dashboard.get");
        var unavailable = await Call(app, "reports.preview", new { runId = Str(invalidRun, "runId"), revision = app.Revision, selectedWidgetIds = new[] { "timeline" } });
        Check(Widget(unavailable, "timeline").GetProperty("tables")[0].GetProperty("rows").EnumerateArray().All(row => row[1].GetString() == "Indisponible"),
            "An opening column containing only invalid dates is unavailable instead of implying zero creations.");
    }

    private static async Task CheckMeetingHtmlPrint()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1120, Height = 850 } });
        var requests = new List<string>(); page.Request += (_, request) => requests.Add(request.Url);
        await page.SetContentAsync(await File.ReadAllTextAsync(Path.Combine(_root, "meeting-selection.html")));
        await page.EmulateMediaAsync(new() { Media = Media.Print });
        Check(await page.Locator(".report-section").CountAsync() == 4 && await page.Locator(".report-section").First.Locator("tbody tr").CountAsync() == 75
            && await page.Locator("script,img,iframe").CountAsync() == 0 && requests.Count == 0,
            "The exported HTML renders offline with all selected rows and without executing injected markup or fetching resources.");
        Check(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth && [...document.querySelectorAll('td,th,svg')].every(node => node.getBoundingClientRect().right <= innerWidth + 1)"),
            "Printed report tables and graphics fit the landscape content width without horizontal clipping.");
        var pdf = await page.PdfAsync(new() { PreferCSSPageSize = true, PrintBackground = true });
        Check(pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8) && System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b").Count > 1,
            "The complete 75-row meeting report prints to a real multi-page Chromium PDF.");
    }
}
