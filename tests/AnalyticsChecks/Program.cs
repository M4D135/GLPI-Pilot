using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;

var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
void Near(double? actual, double expected) => Assert(actual.HasValue && Math.Abs(actual.Value - expected) < 1e-7, $"Expected {expected}; got {actual}");
var root = Path.Combine(Path.GetTempPath(), "GLPI-Analytics-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var clock = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
string[] Row(string id, string status = "Nouveau", string assignee = "Alex", string group = "Support", string title = "Incident") => new[] { id, title, status, assignee, group };
ExtractionResult Data(int day, params string[][] rows) => new()
{
    Headers = new() { "ID", "Titre", "Statut", "Attribué à - Technicien", "Attribué à - Groupe de techniciens" }, Rows = rows.ToList(),
    ExtractedAt = clock.AddDays(day), SourceUrl = "https://glpi.example.test/front/ticket.php", Scope = "Recherche vérifiée", Complete = true, ExpectedTotal = rows.Length,
    Provenance = new() { RequestedUrl = "https://glpi.example.test/front/ticket.php?criteria%5B0%5D%5Bvalue%5D=all", SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "entity-1", Method = "native-csv" }
};
SnapshotDocument Doc(int day, params string[][] rows)
{
    var data = Data(day, rows); var dashboard = DashboardService.Calculate(data, new(), data.ExtractedAt);
    return new() { Data = data, Metadata = new() { CapturedAt = data.ExtractedAt, SourceKey = SnapshotStore.SourceKey(data), Complete = true, SearchVerified = true, ScopeVerified = true, IdentityReliable = true,
        SchemaFingerprint = SnapshotStore.SchemaFingerprint(data.Headers), RulesFingerprint = "rules", IdColumn = 0, StatusColumn = 2, AssigneeColumn = 3,
        Rows = rows.Length, Columns = data.Headers.Count, Metrics = dashboard.Metrics.ToDictionary(m => m.Key, m => m.Value) } };
}
AppSettings Settings() => new() { PortalUrl = Data(0).Provenance.RequestedUrl, Queries = new(), History = new() { RetentionDays = 3650, MaximumSnapshots = 500 } };
string Folder() { var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
SnapshotAnalyticsReport Analyze(params SnapshotDocument[] documents) => SnapshotAnalytics.Analyze(documents, TimeSpan.FromDays(1));

Test("Missing collection creates a null break rather than an interpolated zero", () =>
{
    var points = new[] { new DashboardHistoryPoint(clock, 12, 12, true, true, "a"), new DashboardHistoryPoint(clock.AddDays(3), 8, 8, true, true, "b") };
    var result = SnapshotAnalytics.WithGaps(points, TimeSpan.FromDays(1)); Assert(result.Count == 3 && result[1].Value == null && !result[1].Comparable && result[1].CapturedAt == clock.AddDays(1));
});
Test("Regular observations remain unchanged and invalid cadence is rejected", () =>
{
    var points = new[] { new DashboardHistoryPoint(clock, 0, 0, true, true, "a"), new DashboardHistoryPoint(clock.AddDays(1), 1, 1, true, true, "b") };
    Assert(SnapshotAnalytics.WithGaps(points, TimeSpan.FromDays(1)).Count == 2); Reject<ArgumentOutOfRangeException>(() => SnapshotAnalytics.WithGaps(points, TimeSpan.Zero));
});
Test("Movement balances appearances and absences without converting absence to resolution", () =>
{
    var report = Analyze(Doc(0, Row("1"), Row("2"), Row("3", "Résolu")), Doc(1, Row("1", "Résolu"), Row("3"), Row("4")));
    var move = report.Movements.Single(); Assert(move.Comparable && move.Appeared == 1 && move.Absent == 1 && move.Balance == 0 && move.Resolved == 1 && move.Reopened == 1);
});
Test("Incomplete or mismatched intervals produce no inferred events", () =>
{
    var before = Doc(0, Row("1")); var after = Doc(1, Row("2")); after.Metadata.Complete = false;
    var result = Analyze(before, after); Assert(result.BrokenPairs == 1 && result.ComparablePairs == 0 && result.Movements.Single().Appeared == 0 && result.Transfers.Count == 0);
    after.Metadata.Complete = true; after.Metadata.RulesFingerprint = "other"; Assert(Analyze(before, after).BrokenPairs == 1);
});
Test("Different GLPI contexts cannot be pooled into one historical cohort", () =>
{
    var second = Doc(1, Row("1")); second.Metadata.SourceKey = "other"; Reject<InvalidOperationException>(() => Analyze(Doc(0, Row("1")), second));
});
Test("Time coverage counts only continuous comparable intervals and calendar keeps missing days unknown", () =>
{
    var result = Analyze(Doc(0, Row("1")), Doc(1, Row("1")), Doc(4, Row("1")));
    Near(result.CoveragePercent, 25); Assert(result.Calendar.Count == 5 && result.Calendar.Count(d => d.Gap) == 2 && result.Calendar.Where(d => d.Gap).All(d => d.Note.Contains("inconnue")));
});
Test("Transfers count distinct observed tickets per exact assignment pair", () =>
{
    var result = Analyze(Doc(0, Row("1", assignee: "Alex")), Doc(1, Row("1", assignee: "Bob")), Doc(2, Row("1", assignee: "Alex")), Doc(3, Row("1", assignee: "Bob")));
    Assert(result.Transfers.Single(t => t.Before == "Alex" && t.After == "Bob").Count == 1); Assert(result.Transfers.All(t => t.Tickets.SequenceEqual(new[] { "1" })));
});
Test("Multiple assignment remains an observed cell, without invented person-to-person flows", () =>
{
    var result = Analyze(Doc(0, Row("1", assignee: "Alex ; Sam")), Doc(1, Row("1", assignee: "Sam ; Jo")));
    Assert(result.Transfers.Count == 1 && result.Transfers[0].Before == "Alex ; Sam" && result.Transfers[0].After == "Sam ; Jo");
});
Test("Cohorts distinguish observed resolution, absence and unavailable future horizons", () =>
{
    var result = Analyze(Doc(0, Row("1"), Row("2")), Doc(1, Row("1", "Résolu")));
    var day = result.Cohorts.Single(c => c.HorizonDays == 1); Assert(day.Initial == 2 && day.ResolvedOrClosed == 1 && day.Absent == 1 && day.Active == 0);
    Assert(result.Cohorts.Single(c => c.HorizonDays == 7).Active == null && result.Cohorts.Single(c => c.HorizonDays == 30).Observation == null);
});
Test("Cohort J+7 and J+30 require an entire continuous comparable chain", () =>
{
    var docs = Enumerable.Range(0, 31).Select(day => Doc(day, Row("1", day < 7 ? "Nouveau" : "Clos"))).ToArray();
    var result = Analyze(docs); Assert(result.Cohorts.Single(c => c.HorizonDays == 7).ResolvedOrClosed == 1 && result.Cohorts.Single(c => c.HorizonDays == 30).ResolvedOrClosed == 1);
    docs[4].Metadata.Complete = false; Assert(Analyze(docs).Cohorts.Single(c => c.HorizonDays == 7).ResolvedOrClosed == null);
});
Test("Status durations expose conditional bounds and retain raw uncertainty intervals", () =>
{
    var result = Analyze(Doc(0, Row("1")), Doc(1, Row("1")), Doc(2, Row("1", "Résolu")));
    var active = result.StatusDurations.Single(d => d.Status == "Nouveau"); Near(active.MinimumHours, 24); Near(active.MaximumHours, 48);
    var resolved = result.StatusDurations.Single(d => d.Status == "Résolu"); Near(resolved.MinimumHours, 0); Near(resolved.MaximumHours, 24);
    Assert(result.StatusIntervals.Count == 2 && active.Assumption.Contains("aucune transition cachée"));
});
Test("Count variation is exactly decomposed by entered and departed contributors", () =>
{
    var report = Analyze(Doc(0, Row("1"), Row("2")), Doc(1, Row("1", "Résolu"), Row("2"), Row("3"), Row("4")));
    var total = report.Variations.Single(v => v.Metric == "total"); Near(total.ExplainedDelta, 2); Assert(total.EnteredContributors == 2 && total.LeftContributors == 0);
    var active = report.Variations.Single(v => v.Metric == "active"); Near(active.ExplainedDelta, 1); Assert(active.LeftContributors == 1 && active.ChangedContributors == 1);
});
Test("Percentage variation does not claim an additive ticket contribution decomposition", () =>
{
    var result = Analyze(Doc(0, Row("1")), Doc(1, Row("1"), Row("2", "Clos")));
    Assert(result.Variations.Single(v => v.Metric == "active-share").ExplainedDelta == null);
});
Test("Forecast needs eight continuous samples and three days rather than an arbitrary trend line", () =>
{
    var docs = Enumerable.Range(0, 7).Select(day => Doc(day, Row("1"))).ToArray(); Assert(Analyze(docs).Scenarios.All(s => s.Central == null));
    var trend = Enumerable.Range(0, 10).Select(day => Doc(day, Enumerable.Range(1, 100 + day).Select(i => Row(i.ToString())).ToArray())).ToArray();
    var scenarios = Analyze(trend).Scenarios; Near(scenarios.Single(s => s.HorizonDays == 1).Central, 110); Assert(scenarios.All(s => s.Lower <= s.Central && s.Upper >= s.Central && s.Note.Contains("Scénario")));
});
Test("Forecast bands are nonzero for noisy samples and nonnegative for decreasing stock", () =>
{
    var docs = Enumerable.Range(0, 10).Select(day => Doc(day, Enumerable.Range(1, 100 - day * 3 + day % 2).Select(i => Row(i.ToString())).ToArray())).ToArray();
    var scenarios = Analyze(docs).Scenarios; Assert(scenarios.All(s => s.Lower >= 0 && s.Upper >= s.Lower)); Assert(scenarios.Single(s => s.HorizonDays == 1).Upper > scenarios.Single(s => s.HorizonDays == 1).Lower);
});
Test("Unusual variations use preceding samples only and reset at missing metric evidence", () =>
{
    var docs = Enumerable.Range(0, 9).Select(day => Doc(day, Enumerable.Range(1, day == 8 ? 200 : 100).Select(i => Row(i.ToString())).ToArray())).ToArray();
    Assert(Analyze(docs).Anomalies.Single(a => a.Metric == "total" && a.At == clock.AddDays(8)).Unusual);
    docs[5].Metadata.Metrics["total"] = null; Assert(!Analyze(docs).Anomalies.Any(a => a.Metric == "total") && Analyze(docs).Scenarios.All(s => s.Central == null));
});
Test("Historical computations honor cancellation and bounded receipt counts", () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel(); Reject<OperationCanceledException>(() => SnapshotAnalytics.Analyze(new[] { Doc(0, Row("1")), Doc(1, Row("1")) }, TimeSpan.FromDays(1), cts.Token));
    Reject<InvalidOperationException>(() => Analyze(Enumerable.Range(0, 121).Select(day => Doc(day)).ToArray()));
});
Test("Catalogue and ticket indexes are reused unchanged between reads", () =>
{
    var folder = Folder(); var store = new SnapshotStore(folder); var first = store.Save(Data(0, Row("001")), Settings()); var second = store.Save(Data(1, Row("1", "Résolu")), Settings());
    Assert(store.List().Count == 2); var catalogue = Path.Combine(folder, "catalogue-index.json"); var old = File.ReadAllBytes(catalogue); var stamp = File.GetLastWriteTimeUtc(catalogue);
    var history = store.FindTicketHistory(first.SourceKey, "0001"); Assert(history.Count == 2 && history[0].Status == "Nouveau" && history[1].Status == "Résolu");
    Assert(File.GetLastWriteTimeUtc(catalogue) == stamp && File.ReadAllBytes(catalogue).SequenceEqual(old)); Assert(File.Exists(Path.Combine(folder, second.Id + ".tickets.json")));
});
Test("Corrupt catalogue and ticket indexes reconstruct from verified authoritative archives", () =>
{
    var folder = Folder(); var store = new SnapshotStore(folder); var entry = store.Save(Data(0, Row("7")), Settings());
    File.WriteAllText(Path.Combine(folder, "catalogue-index.json"), "{broken"); File.WriteAllText(Path.Combine(folder, entry.Id + ".tickets.json"), "{\"Version\":2,\"Tickets\":null}");
    Assert(store.List().Count == 1 && store.FindTicketHistory(entry.SourceKey, "7").Count == 1);
    Assert(JsonNode.Parse(File.ReadAllText(Path.Combine(folder, entry.Id + ".tickets.json")))!["Version"]!.GetValue<int>() == 2);
});
Test("Changed payload invalidates an otherwise valid ticket index and fails hash verification", () =>
{
    var folder = Folder(); var store = new SnapshotStore(folder); var entry = store.Save(Data(0, Row("7")), Settings()); var path = Path.Combine(folder, entry.Id + ".json.gz");
    File.AppendAllText(path, "corrupt"); Reject<InvalidDataException>(() => store.FindTicketHistory(entry.SourceKey, "7"));
});
Test("Pruning removes stale ticket indexes while the newest observation remains findable", () =>
{
    var folder = Folder(); var store = new SnapshotStore(folder); var old = store.Save(Data(0, Row("1")), Settings()); store.Save(Data(1, Row("1")), Settings()); var latest = store.Save(Data(2, Row("1")), Settings());
    store.Prune(new() { RetentionDays = 3650, MaximumSnapshots = 2 }); Assert(store.List().Count == 2 && !File.Exists(Path.Combine(folder, old.Id + ".tickets.json")));
    Assert(store.FindTicketHistory(latest.SourceKey, "1").Last().SnapshotId == latest.Id);
});
Test("Saved queries and interpretation settings participate in archive rules and remain frozen", () =>
{
    var settings = Settings(); var query = new SavedQuery { Name = "Archived query", BaseProbe = "total", Aggregate = "count" }; settings.Queries.Queries.Add(query);
    var store = new SnapshotStore(Folder()); var metadata = store.Save(Data(0, Row("1")), settings); Near(metadata.Metrics[QueryService.MetricKey(query.Id)], 1);
    var loaded = store.Load(metadata.Id); Assert(loaded.Queries.Queries.Single().Name == "Archived query");
    var first = SnapshotStore.RulesFingerprint(settings); settings.Dashboard.NumberCulture = "en-US"; Assert(first != SnapshotStore.RulesFingerprint(settings));
});

var city = new CityDefinition { Name = "Lyon", Latitude = 45.75, Longitude = 4.85 };
var site = new SiteDefinition { Name = "Centre", CityId = city.Id, Latitude = 45.76, Longitude = 4.86 };
var geography = new CitySettings { Cities = new() { city }, Sites = new() { site } };
SitePopulationEntry Population(string unit = "people", int count = 200) => new() { SourceKey = SnapshotStore.SourceKey(Data(0)), SiteId = site.Id, SiteLabel = site.Name, Unit = unit, Count = count, FromDate = "2026-10-01", UntilDate = "2026-10-31", Reference = "Inventaire RH validé" };
CityEnrichmentResult Places(ExtractionResult data) => new() { Source = data, Data = data, Applied = true, Matches = new[] { new CityMatch(0, city.Id, city.Name, CityMatchState.Matched, new[] { city.Id }) { SiteId = site.Id, SiteName = site.Name }, new CityMatch(1, city.Id, city.Name, CityMatchState.Matched, new[] { city.Id }) } };
Test("Dated site inventory round-trips and rejects overlapping, unknown or nonpositive denominators", () =>
{
    var path = Path.Combine(Folder(), "population.json"); var store = new AnalyticsPopulationStore(path); store.Save(new[] { Population() }); Assert(store.Load().Single().Count == 200);
    Reject<InvalidDataException>(() => store.Save(new[] { Population(), Population(count: 300) })); var bad = Population(); bad.Count = 0; Reject<InvalidDataException>(() => store.Save(new[] { bad }));
    bad = Population(); bad.SourceKey = ""; Reject<InvalidDataException>(() => store.Save(new[] { bad })); Assert(store.Load().Single().Count == 200);
});
Test("Rates use source and date-bound denominators with exact covered rows", () =>
{
    var data = Data(0, Row("1"), Row("2")); var snapshot = DashboardService.Calculate(data, new(), data.ExtractedAt); var entries = new[] { Population() };
    AnalyticsPopulationStore.Append(snapshot, Places(data), geography, SnapshotStore.SourceKey(data), entries);
    Near(snapshot.Metric("site-per100-people")!.Value, .5); Assert(snapshot.Metric("site-per100-people")!.Rows.SequenceEqual(new[] { 0 }) && snapshot.Metric("site-per100-people")!.Sample == 200);
    Assert(snapshot.Metric("site-per100-workstations")!.Value == null);
});
Test("Expired and foreign-context site populations stay unavailable rather than becoming zero", () =>
{
    var data = Data(40, Row("1"), Row("2")); var snapshot = DashboardService.Calculate(data, new(), data.ExtractedAt);
    AnalyticsPopulationStore.Append(snapshot, Places(data), geography, SnapshotStore.SourceKey(data), new[] { Population() }); Assert(snapshot.Metric("site-per100-people")!.Value == null);
    data = Data(0, Row("1"), Row("2")); snapshot = DashboardService.Calculate(data, new(), data.ExtractedAt); AnalyticsPopulationStore.Append(snapshot, Places(data), geography, "other", new[] { Population() }); Assert(snapshot.Metric("site-per100-people")!.Value == null);
});
Test("Filtered site ratios keep the site denominator and never infer an excluded site's zero", () =>
{
    var data = Data(0, Row("1"), Row("2")); var snapshot = DashboardService.CalculateSubset(data, new(), new[] { 1 }, "Other site", data.ExtractedAt);
    AnalyticsPopulationStore.Append(snapshot, Places(data), geography, SnapshotStore.SourceKey(data), new[] { Population() }); Assert(snapshot.Metric("site-per100-people")!.Value == null);
});
Test("Offline geography prefers site coordinates and falls back to known city coordinates exactly once per row", () =>
{
    var data = Data(0, Row("1"), Row("2", "Clos")); var snapshot = DashboardService.Calculate(data, new(), data.ExtractedAt); DashboardGeography.Append(snapshot, Places(data), geography);
    Assert(snapshot.Geography.Count == 2 && snapshot.Geography.SelectMany(p => p.Rows).Order().SequenceEqual(new[] { 0, 1 }));
    var point = snapshot.Geography.Single(p => p.Key == "site:" + site.Id); Near(point.Latitude, site.Latitude!.Value); Assert(point.ActiveRows.SequenceEqual(new[] { 0 }));
    var filtered = DashboardService.ResolveScope(snapshot, new[] { new DashboardScopeFilter { SourceKey = "geography-points-all", Value = point.Label } }); Assert(filtered.SequenceEqual(new[] { 0 }));
});
Test("Unknown coordinates or ambiguous city matches are not geocoded or sent externally", () =>
{
    var data = Data(0, Row("1")); var snapshot = DashboardService.Calculate(data, new(), data.ExtractedAt);
    DashboardGeography.Append(snapshot, new() { Data = data, Matches = new[] { new CityMatch(0, city.Id, city.Name, CityMatchState.Ambiguous, new[] { city.Id }) } }, geography); Assert(snapshot.Geography.Count == 0);
});
Test("Complete HTML report includes criteria, SVG graphics, quality and every scoped raw ticket with escaping", () =>
{
    var data = Data(0, Row("1", title: "<script>alert('x')</script>"), Row("2", title: "Raw & exact")); var snapshot = DashboardService.CalculateSubset(data, new(), new[] { 1 }, "Selected", data.ExtractedAt);
    var path = Path.Combine(Folder(), "report.html"); var exported = ReportService.WriteHtml(snapshot, data, "Filtre : catégorie = A & B", path); var html = File.ReadAllText(path);
    Assert(exported.Rows == 1 && html.Contains("Raw &amp; exact") && !html.Contains("alert(") && html.Contains("<svg") && html.Contains("id=\"quality\"") && html.Contains("A &amp; B") && html.Contains("Content-Security-Policy"));
});
Test("HTML includes participation, global and grouped oldest tickets, site zeroes and unknowns from the common scope", () =>
{
    var data = new ExtractionResult
    {
        Headers = new() { "ID", "Titre", "Statut", "Date d'ouverture", "Attribué à - Groupe de techniciens", "Suivis - Rédacteur", "Tâches - Rédacteur", "Attribué à - Technicien", "Priorité" },
        ExtractedAt = clock, Complete = true, ExpectedTotal = 6,
        Rows = new()
        {
            new[] { "900", "PRIVATE OUTSIDE SCOPE", "Nouveau", "01/01/2020 10:00", "Private group", "Private actor (999)", "", "", "Haute" },
            new[] { "201", "Titre <script>alert(1)</script> & détail", "Nouveau", "28/09/2026 09:00", "Groupe <b>&\nSecond groupe", "Acteur <img src=x onerror=alert(1)> (71)", "Autre libellé (71)", "Technicien (71)", "Haute" },
            new[] { "202", "Date absente", "En attente", "", "Groupe <b>&", "", "", "", "Basse" },
            new[] { "203", "Date invalide", "Nouveau", "invalide", "Groupe <b>&", "", "", "", "Moyenne" },
            new[] { "204", "Date future", "Nouveau", "05/10/2026 10:00", "Groupe <b>&", "", "", "", "Moyenne" },
            new[] { "205", "Ticket terminé", "Clos", "01/09/2026 10:00", "Groupe <b>&", "Compte clos (99)", "", "Compte clos (99)", "Basse" }
        }
    };
    var snapshot = DashboardService.CalculateSubset(data, new(), new[] { 1, 2, 3, 4, 5 }, "Périmètre commun", clock);
    snapshot.SiteWorkload = new()
    {
        Available = true, Grain = snapshot.Grain, Population = 4,
        Sites = new[]
        {
            new DashboardSiteWorkloadEntry("site:known", "Site <Nord> & accueil", false, new[] { 1, 2, 3 }),
            new DashboardSiteWorkloadEntry("site:zero", "Site sans ticket", false, Array.Empty<int>()),
            new DashboardSiteWorkloadEntry("unmatched:", "Site non connu / à vérifier", true, new[] { 4 })
        },
        Explanation = "Source <référentiel> & corrections locales."
    };
    var path = Path.Combine(Folder(), "analyses.html");
    var exported = ReportService.WriteHtml(snapshot, data, "Critères & périmètre", path);
    var html = File.ReadAllText(path);
    Assert(exported.Rows == 5 && !html.Contains("PRIVATE OUTSIDE SCOPE") && !html.Contains("Private actor"));
    Assert(html.Contains("id=\"participation\"") && html.Contains("id=\"oldest\"") && html.Contains("id=\"sites\""));
    Assert(html.Contains("Acteur &lt;img") && !html.Contains("<img ") && !html.Contains("<script>"));
    Assert(html.Contains("<td>ID:71</td><td>1</td><td>1</td><td>1</td><td>1</td><td>1</td>"), "Union, intersection and assignment must retain their independent measured values.");
    var participationStart = html.IndexOf("<h2 id=\"participation\"", StringComparison.Ordinal);
    var oldestStart = html.IndexOf("<h2 id=\"oldest\"", StringComparison.Ordinal);
    Assert(!html[participationStart..oldestStart].Contains("Compte clos"), "Completed rows cannot leak into active participation.");
    Assert(html.Contains("<th>Dates absentes / invalides / futures</th><td>1 / 1 / 1</td>"));
    Assert(html.Contains("Groupe &lt;b&gt;&amp;") && html.Contains(System.Net.WebUtility.HtmlEncode(data.Rows[1][1])));
    Assert(html.Contains("01/10/2026") && html.Contains("28/09/2026 09:00") && html.Contains("style=\"background-color:#"));
    var globalStart = html.IndexOf("<h3>Classement global du périmètre</h3>", StringComparison.Ordinal);
    var groupsStart = html.IndexOf("<h3>Synthèse par groupe</h3>", StringComparison.Ordinal);
    Assert(globalStart > oldestStart && groupsStart > globalStart);
    Assert(System.Text.RegularExpressions.Regex.Matches(html[globalStart..groupsStart], "href=\"#ticket-row-2\"").Count == 1,
        "The global oldest list does not repeat a multigroup source occurrence.");
    Assert(html.Contains("Site &lt;Nord&gt; &amp; accueil") && html.Contains(System.Net.WebUtility.HtmlEncode(snapshot.SiteWorkload.Explanation)));
    Assert(html.Contains("<td>Site sans ticket</td><td>Site référencé</td><td>0</td>")
        && html.Contains("<td>" + System.Net.WebUtility.HtmlEncode("Site non connu / à vérifier") + "</td><td>À vérifier</td><td>1</td>"));
    Assert(html.Contains("href=\"#ticket-row-2\">201</a>") && html.Contains("id=\"ticket-row-2\"")
        && !html.Contains("href=\"#ticket-row-1\"") && !html.Contains("id=\"ticket-row-1\""), "Links retain original row indices and remain within the report scope.");
    Assert(html.Contains("href=\"#ticket-row-5\">204</a>"), "Site lists retain tickets excluded from date rankings.");
});
Test("HTML keeps missing analytical sources unavailable and escapes their explanations", () =>
{
    var data = new ExtractionResult { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "1", "Titre" } }, ExtractedAt = clock, Complete = true, ExpectedTotal = 1 };
    var snapshot = DashboardService.Calculate(data, new(), clock);
    snapshot.SiteWorkload = new() { UnavailableReason = "Référentiel <indisponible> & à appliquer." };
    var path = Path.Combine(Folder(), "unavailable.html"); ReportService.WriteHtml(snapshot, data, "", path); var html = File.ReadAllText(path);
    Assert(html.Contains("<th>Population active</th><td>Indisponible") && html.Contains("Participation indisponible"));
    Assert(html.Contains(System.Net.WebUtility.HtmlEncode("Correspondances nécessaires"))
        && html.Contains(System.Net.WebUtility.HtmlEncode(snapshot.SiteWorkload.UnavailableReason)));
    Assert(!html.Contains("<td>Site référencé</td>"), "An unavailable site inventory never becomes a measured zero stock.");
});
Test("HTML rejects out-of-scope analysis links and preserves an existing report atomically", () =>
{
    var folder = Folder(); var path = Path.Combine(folder, "keep.html"); File.WriteAllText(path, "existing report");
    var data = Data(0, Row("1"), Row("2")); var snapshot = DashboardService.CalculateSubset(data, new(), new[] { 1 }, "Second row", clock);
    snapshot.SiteWorkload = new()
    {
        Available = true, Population = 1, Grain = snapshot.Grain,
        Sites = new[] { new DashboardSiteWorkloadEntry("site:wrong", "Stale site", false, new[] { 0 }) }
    };
    Reject<InvalidDataException>(() => ReportService.WriteHtml(snapshot, data, "", path));
    Assert(File.ReadAllText(path) == "existing report" && Directory.GetFiles(folder).SequenceEqual(new[] { path }));
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    Reject<OperationCanceledException>(() => ReportService.WriteHtml(snapshot, data, "", path, cancelled.Token));
    Assert(File.ReadAllText(path) == "existing report" && Directory.GetFiles(folder).SequenceEqual(new[] { path }));
});
Test("Cancelled report leaves no target or temporary file and never exports stale indices", () =>
{
    var folder = Folder(); var path = Path.Combine(folder, "report.html"); var data = Data(0, Row("1")); var snapshot = DashboardService.Calculate(data, new(), data.ExtractedAt);
    using var cts = new CancellationTokenSource(); cts.Cancel(); Reject<OperationCanceledException>(() => ReportService.WriteHtml(snapshot, data, "", path, cts.Token)); Assert(Directory.GetFiles(folder).Length == 0);
    data.Rows.Clear(); Reject<InvalidDataException>(() => ReportService.WriteHtml(snapshot, data, "", path));
});
Test("Native PDF writes consistent cross-reference offsets, two pages and JPEG streams", () =>
{
    using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("AnalyticsChecks.pixel.jpg")!; using var memory = new MemoryStream(); embedded.CopyTo(memory); var jpg = memory.ToArray();
    var path = Path.Combine(root, "native-report.pdf"); PdfReportWriter.Write(path, 2, _ => new(jpg, 4, 4)); var bytes = File.ReadAllBytes(path); var text = Encoding.Latin1.GetString(bytes);
    Assert(text.StartsWith("%PDF-1.4") && text.Contains("/Count 2") && text.Contains("/DCTDecode") && text.EndsWith("%%EOF\n"));
    var start = text.LastIndexOf("startxref\n", StringComparison.Ordinal); var offset = long.Parse(text[(start + 10)..].Split('\n')[0]); Assert(text[(int)offset..].StartsWith("xref\n0 9\n"));
    var lines = text[(int)offset..].Split('\n'); for (var i = 1; i <= 8; i++) { var objectOffset = int.Parse(lines[2 + i][..10]); Assert(text[objectOffset..].StartsWith(i + " 0 obj\n")); }
});
Test("Invalid PDF page and cancellation preserve an existing target atomically", () =>
{
    var path = Path.Combine(Folder(), "keep.pdf"); File.WriteAllText(path, "existing"); Reject<InvalidDataException>(() => PdfReportWriter.Write(path, 1, _ => new(new byte[] { 0, 1, 2 }, 1, 1))); Assert(File.ReadAllText(path) == "existing");
    using var cts = new CancellationTokenSource(); cts.Cancel(); Reject<OperationCanceledException>(() => PdfReportWriter.Write(path, 1, _ => throw new Exception("should not render"), cts.Token)); Assert(File.ReadAllText(path) == "existing");
});

Test("A team union selects each active ticket once across multiple group memberships", () =>
{
    var data = Data(0, Row("1", group: "Alpha ; Beta ; alpha"), Row("2", group: "Beta"),
        Row("3", group: "Gamma"), Row("4", status: "Clos", group: "Alpha"));
    var snapshot = DashboardService.Calculate(data, new(), clock);
    var filter = new DashboardScopeFilter { Kind = "matrix-row", SourceKey = "group-status", Values = new() { "ALPHA", "Beta" } };
    Assert(DashboardService.ResolveScope(snapshot, new[] { filter }).SequenceEqual(new[] { 0, 1 }));
    var matrix = snapshot.Matrices.Single(m => m.Key == "group-status");
    Assert(matrix.Cells.Sum(c => c.Value) == 4);
    Assert(matrix.Cells.GroupBy(c => c.RowLabel).All(group => group.Count() == 4), "Each group keeps the four active statuses, including known zero counts.");
    Assert(matrix.Cells.Where(c => c.Value == 0).All(c => c.Rows.Length == 0), "Zero cells do not invent ticket identities.");
    Assert(matrix.Cells[0].RowLabel == "Beta", "Rows are ordered by total participation rather than their largest cell.");
    var settings = new DashboardSettings { AssignmentCountingMode = "fractional" };
    var fractional = DashboardService.Calculate(data, settings, clock);
    Assert(DashboardService.ResolveScope(fractional, new[] { filter }).SequenceEqual(new[] { 0, 1 }));
});
Test("Union exclusion respects the common cohort and an unavailable matrix fails closed", () =>
{
    var snapshot = DashboardService.Calculate(Data(0, Row("1", group: "Alpha;Beta"), Row("2", group: "Gamma"), Row("3", "Clos", group: "Alpha")), new(), clock);
    var active = new DashboardScopeFilter { Kind = "metric", SourceKey = "active" };
    var groups = new DashboardScopeFilter { Kind = "matrix-row", SourceKey = "group-status", Values = new() { "Alpha", "Beta" }, Exclude = true };
    Assert(DashboardService.ResolveScope(snapshot, new[] { active, groups }).SequenceEqual(new[] { 1 }));
    groups.Exclude = false; groups.Values = new() { "Absent" };
    Assert(DashboardService.ResolveScope(snapshot, new[] { groups }).Length == 0);
    groups.SourceKey = "not-present";
    Reject<InvalidDataException>(() => DashboardService.ResolveScope(snapshot, new[] { groups }));
});
Test("Distribution alternatives preserve literal labels and do not split their punctuation", () =>
{
    var snapshot = DashboardService.Calculate(Data(0, Row("1", group: "A | B"), Row("2", group: "C"), Row("3", group: "A")), new(), clock);
    var filter = new DashboardScopeFilter { SourceKey = "group", Values = new() { "A | B", "C" } };
    Assert(DashboardService.ResolveScope(snapshot, new[] { filter }).SequenceEqual(new[] { 0, 1 }));
    filter.Values.Clear(); filter.Value = "A | B";
    Assert(DashboardService.ResolveScope(snapshot, new[] { filter }).SequenceEqual(new[] { 0 }));
});
Test("Filter alternatives are bounded, deeply copied and preserved in portable configuration", () =>
{
    var filter = new DashboardScopeFilter { Kind = "matrix-row", SourceKey = "group-status", Values = new() { "Alpha", "Beta" } };
    var settings = Settings(); settings.Dashboard.Layout.Normalize();
    settings.Dashboard.Layout.Boards[0].Filters.Add(filter);
    var copied = settings.Copy(); copied.Dashboard.Layout.Boards[0].Filters[0].Values[0] = "Other";
    Assert(filter.Values[0] == "Alpha");
    var path = Path.Combine(Folder(), "team-union.json");
    ConfigurationTransferService.Export(path, settings);
    var imported = ConfigurationTransferService.Import(path, Settings());
    Assert(imported.Dashboard.Layout.Boards[0].Filters.Single().Selections().SequenceEqual(new[] { "Alpha", "Beta" }));
    filter.Values = Enumerable.Range(1, 33).Select(index => "Group " + index).ToList();
    DashboardScopeFilter.ValidateList(new[] { filter });
    filter.Values = Enumerable.Range(1, DashboardScopeFilter.MaximumGroupSelections).Select(index => "Group " + index).ToList();
    DashboardScopeFilter.ValidateList(new[] { filter });
    filter.Values.Add("Overflow"); Reject<InvalidDataException>(() => DashboardScopeFilter.ValidateList(new[] { filter }));
    foreach (var (kind, sourceKey) in new[] { ("distribution", "group"), ("distribution", "group-status"), ("matrix-row", "technician-status") })
    {
        var ordinary = filter.Copy(); ordinary.Kind = kind; ordinary.SourceKey = sourceKey;
        ordinary.Values = Enumerable.Range(1, 32).Select(index => "Choice " + index).ToList();
        DashboardScopeFilter.ValidateList(new[] { ordinary });
        ordinary.Values.Add("Overflow"); Reject<InvalidDataException>(() => DashboardScopeFilter.ValidateList(new[] { ordinary }));
    }
    filter.Values = new() { " " }; Reject<InvalidDataException>(() => DashboardScopeFilter.ValidateList(new[] { filter }));
    filter.Values = new() { "A" }; filter.Kind = "metric"; Reject<InvalidDataException>(() => DashboardScopeFilter.ValidateList(new[] { filter }));
    filter.Values = null!; Reject<InvalidDataException>(() => DashboardScopeFilter.ValidateList(new[] { filter }));
});

var failed = 0;
foreach (var test in tests) try { test.Run(); Console.WriteLine("PASS " + test.Name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + "\n" + error); }
Console.WriteLine($"{tests.Count - failed}/{tests.Count} vérifications réussies."); Console.WriteLine("PDF validation artifact: " + Path.Combine(root, "native-report.pdf"));
return failed == 0 ? 0 : 1;
