using System.Text.Json;
using GlpiPilot.Core;

var clock = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(2));
const string paris = "11111111111111111111111111111111";
const string lyon = "22222222222222222222222222222222";
const string marseille = "33333333333333333333333333333333";
var checks = new List<(string Name, Action Run)>();
var directory = Path.Combine(Path.GetTempPath(), "GLPI-TourPlanningChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
void Test(string name, Action run) => checks.Add((name, run));
void Check(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }
void Rows(IEnumerable<int> actual, params int[] expected) => Check(actual.SequenceEqual(expected), $"Expected [{string.Join(',', expected)}], got [{string.Join(',', actual)}]");
void Stops(TourPlan plan, params string[] expected) => Check(plan.Stops.Select(stop => stop.CityId).SequenceEqual(expected), "Unexpected visit order");
void Throws<T>(Action run) where T : Exception
{
    try { run(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
string PathFor(string name) => Path.Combine(directory, name);
CitySettings Cities(bool coordinates = false) => new()
{
    TitleColumn = "Titre", Cities = new()
    {
        new() { Id = paris, Name = "Paris", PostalCode = "75000", Aliases = new() { "PAR" }, Latitude = coordinates ? 48.8566 : null, Longitude = coordinates ? 2.3522 : null },
        new() { Id = lyon, Name = "Lyon", PostalCode = "69000", Aliases = new() { "LYO" }, Latitude = coordinates ? 45.7640 : null, Longitude = coordinates ? 4.8357 : null },
        new() { Id = marseille, Name = "Marseille", PostalCode = "13000", Aliases = new() { "MRS" }, Latitude = coordinates ? 43.2965 : null, Longitude = coordinates ? 5.3698 : null }
    }
};
ExtractionResult Sample() => new()
{
    Headers = new() { "ID", "Titre", "Statut", "Priorité", "Temps de résolution", "Attribué à - Technicien" },
    Rows = new()
    {
        new[] { "100", "Paris – routeur de test", "Nouveau", "Haute", "20/10/2026 12:00", "Alice" },
        new[] { "101", "LYO – imprimante de test", "En attente", "Basse", "24/10/2026 12:00", "Alice; Bob" },
        new[] { "102", "Marseille – poste de test", "En cours (attribué)", "Majeure", "19/10/2026 12:00", "Bob" },
        new[] { "103", "Paris – dossier terminé", "Clos", "Majeure", "01/10/2026 12:00", "Alice" },
        new[] { "104", "Lyon – état spécifique", "Personnalisé", "Haute", "01/10/2026 12:00", "Alice" },
        new[] { "105", "Paris / Lyon – site à confirmer", "Nouveau", "Haute", "22/10/2026 12:00", "Bob" },
        new[] { "106", "Atelier sans lieu déclaré", "Nouveau", "Basse", "23/10/2026 12:00", "Alice" },
        new[] { "107", "", "En attente", "", "date invalide", "Alice" },
        new[] { "108", "Marseille – sans technicien", "Nouveau", "", "", "" },
        new[] { "109", "Lyon – incident résolu", "Résolu", "Haute", "01/10/2026 12:00", "Bob" }
    },
    SourceUrl = "https://glpi.example.invalid/front/ticket.php?criteria%5B0%5D%5Bvalue%5D=test",
    Title = "Relevé synthétique", Complete = true, ExpectedTotal = 10, Pages = 2,
    ExtractedAt = clock, Scope = "Toutes les pages de la recherche de test", Warnings = new() { "Avertissement synthétique conservé" }
};
TourPlanningContext Context(ExtractionResult? data = null, CitySettings? cities = null, DashboardSettings? dashboard = null)
{
    data ??= Sample(); cities ??= Cities(); dashboard ??= new();
    return TourPlanningService.Prepare(CityDetectionService.Enrich(data, cities, dashboard), cities, dashboard, clock);
}
TourPlan Plan(string? technician = null, bool all = false, ExtractionResult? data = null, CitySettings? cities = null, DashboardSettings? settings = null)
    => TourPlanningService.Create(Context(data, cities, settings), technician, all);
TourStop Stop(TourPlan plan, string id) => plan.Stops.Single(stop => stop.CityId == id);
CityEnrichmentResult WithMatches(CityEnrichmentResult source, IReadOnlyList<CityMatch> matches) => new()
{
    Source = source.Source, Data = source.Data, Applied = source.Applied, Matches = matches,
    CityColumnName = source.CityColumnName, StateColumnName = source.StateColumnName,
    TitleColumnName = source.TitleColumnName, Message = source.Message
};

Test("Active defaults retain original indexes and separate completed and unknown statuses", () =>
{
    var plan = Plan(); Rows(plan.SelectedRows, 0, 1, 2, 5, 6, 7, 8);
    Rows(plan.UnknownStatusRows, 4); Check(plan.ExcludedCompletedCount == 2 && !plan.AllStatuses && plan.Available);
    Stops(plan, lyon, marseille, paris);
});
Test("All statuses explicitly includes closed, resolved and unknown source rows", () =>
{
    var plan = Plan(all: true); Rows(plan.SelectedRows, Enumerable.Range(0, 10).ToArray());
    Rows(Stop(plan, lyon).Rows, 1, 4, 9); Rows(Stop(plan, paris).Rows, 0, 3);
    Check(plan.AllStatuses && plan.ExcludedCompletedCount == 0 && Stop(plan, lyon).UnknownStatusCount == 1);
});
Test("Shared assignment belongs to each technician, never twice to the team plan", () =>
{
    var a = Plan("Alice"); var b = Plan("Bob"); var all = Plan();
    Rows(a.SelectedRows, 0, 1, 6, 7); Rows(b.SelectedRows, 1, 2, 5);
    Check(a.SelectedRows.Intersect(b.SelectedRows).SequenceEqual(new[] { 1 }));
    Check(all.SelectedRows.Distinct().Count() == all.SelectedRows.Count && all.SelectedRows.Contains(8));
    Check(Context().Technicians.Single(item => item.Name == "Alice").RowCount == 6);
});
Test("Technician identity normalization does not change source membership", () =>
{
    Rows(Plan("  alice ").SelectedRows, 0, 1, 6, 7);
    Check(Plan("Alice").TechnicianKey == Plan("ALICE").TechnicianKey);
});
Test("Unknown technician produces an empty scope, never a whole-team fallback", () =>
{
    var plan = Plan("Absent"); Check(plan.SelectedRows.Count == 0 && plan.Stops.Count == 0);
    Check(plan.ScopeLabel.Contains("ABSENT", StringComparison.OrdinalIgnoreCase) && plan.Message.Contains("n’apparaît"));
});
Test("Only matched locations create stops; ambiguous and missing title remain accessible", () =>
{
    var plan = Plan(); Rows(plan.AmbiguousRows, 5); Rows(plan.UnlocatedRows, 6, 7); Rows(plan.TitleUnavailableRows, 7);
    Check(!plan.Stops.SelectMany(stop => stop.Rows).Intersect(plan.AmbiguousRows.Concat(plan.UnlocatedRows)).Any());
    Check(plan.Stops.Sum(stop => stop.Count) + plan.AmbiguousRows.Count + plan.UnlocatedRows.Count == plan.SelectedRows.Count);
});
Test("Stop active, priority and overdue metrics match canonical subset dashboards", () =>
{
    var source = Sample(); var cities = Cities(); var settings = new DashboardSettings();
    var report = CityDetectionService.Enrich(source, cities, settings);
    var plan = TourPlanningService.Create(TourPlanningService.Prepare(report, cities, settings, clock), allStatuses: true);
    foreach (var stop in plan.Stops)
    {
        var reference = DashboardService.CalculateSubset(report.Data, settings, stop.Rows.ToArray(), stop.CityName, clock);
        Check(stop.ActiveCount == reference.Metric("active")!.Value, stop.CityName + " active");
        Check(stop.HighPriorityCount == reference.Metric("high")!.Value, stop.CityName + " high");
        Check(stop.OverdueCount == reference.Metric("overdue")!.Value, stop.CityName + " overdue");
        foreach (var key in new[] { "active", "high", "overdue" })
            Check(TourPlanningService.RowsForStop(plan, stop.CityId, key).SequenceEqual(reference.Metric(key)!.Rows), key);
    }
});
Test("Resolved and unknown priorities and due dates cannot change active visit priorities", () =>
{
    var plan = Plan(all: true); var city = Stop(plan, lyon);
    Check(city.ActiveCount == 1 && city.HighPriorityCount == 0 && city.OverdueCount == 0);
    Check(city.MaximumActivePriority == 2 && city.FirstActiveDue == new DateTime(2026, 10, 24, 12, 0, 0));
    Check(city.PrioritySample == 1 && city.DueSample == 1);
});
Test("Missing status column selects no default active rows and explains the all-status option", () =>
{
    var source = Sample(); source.Headers[2] = "Colonne libre";
    var active = Plan(data: source); var all = Plan(all: true, data: source);
    Check(active.SelectedRows.Count == 0 && active.UnknownStatusRows.Count == 10 && active.Message.Contains("Tous les statuts"));
    Check(all.SelectedRows.Count == 10 && all.Stops.All(stop => stop.ActiveCount == null && stop.HighPriorityCount == null && stop.OverdueCount == null));
});
Test("Entirely unknown status values are unavailable active metrics, not zero", () =>
{
    var source = Sample(); foreach (var row in source.Rows) row[2] = "État spécifique";
    var plan = Plan(all: true, data: source);
    Check(plan.Stops.All(stop => stop.ActiveCount == null && stop.UnknownStatusCount == stop.Count));
    Check(Plan(data: source).SelectedRows.Count == 0);
});
Test("Missing priority and due columns produce null counts and zero samples", () =>
{
    var source = Sample(); source.Headers[3] = "Libre priorité"; source.Headers[4] = "Libre échéance";
    var plan = Plan(data: source);
    Check(plan.Stops.All(stop => stop.HighPriorityCount == null && stop.OverdueCount == null && stop.PrioritySample == 0 && stop.DueSample == 0));
});
Test("Per-stop missing values never borrow another city's valid sample", () =>
{
    var source = Sample(); source.Rows[0][3] = ""; source.Rows[0][4] = "date invalide";
    var plan = Plan(data: source); var city = Stop(plan, paris);
    Check(city.ActiveCount == 1 && city.HighPriorityCount == null && city.OverdueCount == null);
    Check(Stop(plan, marseille).HighPriorityCount == 1 && Stop(plan, marseille).PrioritySample == 1);
});
Test("Missing technician column permits team planning but never infers personal assignments", () =>
{
    var source = Sample(); source.Headers[5] = "Champ libre"; var context = Context(source);
    Check(!context.TechnicianColumnAvailable && context.Technicians.Count == 0);
    Check(TourPlanningService.Create(context).SelectedRows.Count == 7);
    var personal = TourPlanningService.Create(context, "Alice"); Check(!personal.Available && personal.SelectedRows.Count == 0);
});
Test("Explicit dashboard column mappings control technician and status scopes together", () =>
{
    var source = Sample(); source.Headers[5] = "Intervenants locaux"; source.Headers[2] = "État local";
    var settings = new DashboardSettings(); settings.ColumnMappings["assignee"] = source.Headers[5]; settings.ColumnMappings["status"] = source.Headers[2];
    Rows(Plan("Bob", data: source, settings: settings).SelectedRows, 1, 2, 5);
});
Test("Disabled city detection retains selected tickets as unlocated, with no invented stops", () =>
{
    var cities = Cities(); cities.Enabled = false; var plan = Plan(cities: cities);
    Check(plan.Stops.Count == 0 && plan.AmbiguousRows.Count == 0 && plan.UnlocatedRows.SequenceEqual(plan.SelectedRows));
    Check(plan.Message.Contains("désactivé"));
});
Test("Empty city reference does not discard the source scope", () =>
{
    var plan = Plan(cities: new()); Check(plan.Stops.Count == 0 && plan.UnlocatedRows.Count == 7);
    Check(TourPlanningService.ToExport(plan).Rows.Count == 7);
});
Test("Absent title column marks selected rows unavailable without guessing from other fields", () =>
{
    var source = Sample(); source.Headers[1] = "Autre champ"; var plan = Plan(data: source);
    Check(plan.Stops.Count == 0 && plan.TitleUnavailableRows.SequenceEqual(plan.SelectedRows));
});
Test("Alphabetic, active priority and active deadline orders are deterministic", () =>
{
    var plan = Plan(); Stops(TourPlanningService.Sort(plan, TourOrderKind.Priorities), marseille, paris, lyon);
    Stops(TourPlanningService.Sort(plan, TourOrderKind.DueDates), marseille, paris, lyon);
    Stops(TourPlanningService.Sort(plan, TourOrderKind.Alphabetical), lyon, marseille, paris);
    Stops(plan, lyon, marseille, paris);
});
Test("Manual moves create independent plans and retain all source scopes", () =>
{
    var plan = Plan(); var moved = TourPlanningService.Move(plan, paris, -1);
    Stops(moved, lyon, paris, marseille); Stops(plan, lyon, marseille, paris);
    Check(moved.OrderKind == TourOrderKind.Manual && moved.SelectedRows.SequenceEqual(plan.SelectedRows));
    Stops(TourPlanningService.Move(plan, lyon, -1), lyon, marseille, paris);
    Throws<ArgumentOutOfRangeException>(() => TourPlanningService.Move(plan, paris, 2));
    Throws<ArgumentException>(() => TourPlanningService.Move(plan, "foreign", 1));
});
Test("Saved city IDs skip removed cities, suppress duplicates and append newly present cities", () =>
{
    var plan = TourPlanningService.ApplySavedOrder(Plan(), new[] { paris, "deleted", paris, lyon });
    Stops(plan, paris, lyon, marseille); Check(plan.OrderKind == TourOrderKind.Saved);
});
Test("Public plan and stop row collections cannot be edited through IList", () =>
{
    var plan = Plan(); Throws<NotSupportedException>(() => ((IList<int>)plan.SelectedRows)[0] = 99);
    Throws<NotSupportedException>(() => ((IList<TourStop>)plan.Stops).Clear());
    Throws<NotSupportedException>(() => ((IList<int>)Stop(plan, paris).Rows).Clear());
});
Test("Proximity requires coordinates for every stop and an explicit start in the plan", () =>
{
    var plan = Plan(cities: Cities(true)); Check(plan.CanUseProximity);
    var ordered = TourPlanningService.SortByProximity(plan, paris); Stops(ordered, paris, lyon, marseille);
    Check(ordered.OrderKind == TourOrderKind.Proximity && ordered.StraightLineDistanceKm is > 650 and < 750);
    Throws<ArgumentException>(() => TourPlanningService.SortByProximity(plan, "foreign"));
    var cities = Cities(true); cities.Cities[0].Latitude = null; cities.Cities[0].Longitude = null;
    var partial = Plan(cities: cities); Check(!partial.CanUseProximity && partial.StraightLineDistanceKm == null);
    Throws<InvalidOperationException>(() => TourPlanningService.SortByProximity(partial, lyon));
});
Test("A single stop does not claim a route distance or a proximity itinerary", () =>
{
    var source = Sample(); source.Rows = new() { source.Rows[0] }; source.ExpectedTotal = 1;
    var plan = Plan(data: source, cities: Cities(true)); Check(!plan.CanUseProximity && plan.StraightLineDistanceKm == null);
});
Test("Export order matches the displayed plan exactly, with each selected row once", () =>
{
    var plan = TourPlanningService.Move(Plan(), paris, -1); var export = TourPlanningService.ToExport(plan);
    Rows(export.Rows.Select(row => int.Parse(row[6]) - 1), 1, 0, 2, 8, 6, 7, 5);
    Check(export.Rows.Select(row => row[6]).Distinct().Count() == plan.SelectedRows.Count);
    Check(export.Rows[0][0] == "1" && export.Rows[1][0] == "2" && export.Rows[2][0] == "3" && export.Rows[3][0] == "3");
    Check(export.Rows.Skip(4).All(row => row[0] == "" && row[1] == "" && row[2] == ""));
    Check(export.Rows[5][3] == "Titre indisponible" && export.Rows[6][3] == "Localisation ambiguë");
});
Test("Export retains all enriched cells and original extraction provenance", () =>
{
    var source = Sample(); var cities = Cities(); var settings = new DashboardSettings();
    var report = CityDetectionService.Enrich(source, cities, settings);
    var plan = TourPlanningService.Create(TourPlanningService.Prepare(report, cities, settings, clock), "Bob", true);
    var export = TourPlanningService.ToExport(plan);
    Check(export.Headers.Skip(7).SequenceEqual(report.Data.Headers));
    foreach (var row in export.Rows) Check(row.Skip(7).SequenceEqual(report.Data.Rows[int.Parse(row[6]) - 1]));
    Check(export.SourceUrl == source.SourceUrl && export.ExtractedAt == clock && export.Pages == 2);
    Check(export.Complete && export.ExpectedTotal == plan.SelectedRows.Count && export.Warnings.Contains(source.Warnings[0]));
    Check(export.Scope.Contains("Bob") && export.Scope.Contains("Tous les statuts") && export.Scope.Contains(plan.OrderLabel) && export.Scope.Contains(source.Scope));
});
Test("Incomplete source remains incomplete after planning and export", () =>
{
    var source = Sample(); source.Complete = false; source.ExpectedTotal = 100;
    var plan = Plan(data: source); var export = TourPlanningService.ToExport(plan);
    Check(!plan.SourceComplete && !export.Complete && export.ExpectedTotal == null && export.Rows.Count == 7);
});
Test("Export headings avoid collisions without dropping or shifting cell contents", () =>
{
    var source = Sample(); source.Headers.Add("Tournée · étape"); source.Headers.Add("ID");
    for (var i = 0; i < source.Rows.Count; i++) source.Rows[i] = source.Rows[i].Concat(new[] { "original-step", "shadow-id" }).ToArray();
    var export = TourPlanningService.ToExport(Plan(data: source));
    Check(export.Headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() == export.Headers.Count);
    Check(export.Headers.Contains("Tournée · étape (source 2)") && export.Headers.Contains("ID (source 2)"));
    Check(export.Rows.All(row => row[13] == "original-step" && row[14] == "shadow-id"));
});
Test("Frozen plan is isolated from subsequent source, settings and export mutations", () =>
{
    var source = Sample(); var cities = Cities(); var dashboard = new DashboardSettings();
    var report = CityDetectionService.Enrich(source, cities, dashboard);
    var context = TourPlanningService.Prepare(report, cities, dashboard, clock); var plan = TourPlanningService.Create(context);
    source.Rows[0][1] = "source changed"; report.Data.Rows[0][1] = "enriched changed";
    report.Data.Headers[1] = "header changed"; cities.Cities[0].Name = "city changed";
    dashboard.ColumnMappings["assignee"] = "missing";
    var ticket = TourPlanningService.TicketsForStop(plan, paris).Single();
    Check(ticket.Title == "Paris – routeur de test" && Stop(plan, paris).CityName == "Paris");
    var export = TourPlanningService.ToExport(plan); export.Rows.Single(row => row[6] == "1")[8] = "export changed";
    Check(TourPlanningService.ToExport(plan).Rows.Single(row => row[6] == "1")[8] == "Paris – routeur de test");
    Rows(TourPlanningService.Create(context, "Alice").SelectedRows, 0, 1, 6, 7);
});
Test("Stop drilldown returns exact source indexes and copied collections", () =>
{
    var plan = Plan(); var rows = TourPlanningService.RowsForStop(plan, marseille); Rows(rows, 2, 8); rows[0] = 99;
    Rows(TourPlanningService.RowsForStop(plan, marseille), 2, 8);
    var tickets = TourPlanningService.TicketsForStop(plan, marseille);
    Check(tickets.Select(ticket => ticket.Id).SequenceEqual(new[] { "102", "108" }));
    Rows(tickets.Select(ticket => ticket.SourceIndex), 2, 8);
    Throws<ArgumentException>(() => TourPlanningService.RowsForStop(plan, marseille, "invented"));
});
Test("Corrupt location indexes fail before a misleading plan can be published", () =>
{
    var cities = Cities(); var report = CityDetectionService.Enrich(Sample(), cities, new());
    Throws<InvalidDataException>(() => TourPlanningService.Prepare(WithMatches(report, report.Matches.Skip(1).ToArray()), cities, new(), clock));
    var duplicate = report.Matches.ToArray(); duplicate[1] = duplicate[0];
    Throws<InvalidDataException>(() => TourPlanningService.Prepare(WithMatches(report, duplicate), cities, new(), clock));
    var outOfBounds = report.Matches.ToArray(); outOfBounds[0] = outOfBounds[0] with { RowIndex = 100 };
    Throws<InvalidDataException>(() => TourPlanningService.Prepare(WithMatches(report, outOfBounds), cities, new(), clock));
});
Test("Matched city absent from the frozen dictionary is rejected rather than reassigned", () =>
{
    var cities = Cities(); var report = CityDetectionService.Enrich(Sample(), cities, new());
    var matches = report.Matches.ToArray(); matches[0] = matches[0] with { CityId = "44444444444444444444444444444444" };
    Throws<InvalidDataException>(() => TourPlanningService.Prepare(WithMatches(report, matches), cities, new(), clock));
});
Test("Report changing source row count is rejected", () =>
{
    var cities = Cities(); var report = CityDetectionService.Enrich(Sample(), cities, new()); report.Data.Rows.RemoveAt(0);
    Throws<InvalidDataException>(() => TourPlanningService.Prepare(report, cities, new(), clock));
});
Test("Cancellation is honored by preparation, selection, sorting, details and export", () =>
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var token = cancellation.Token;
    var cities = Cities(true); var report = CityDetectionService.Enrich(Sample(), cities, new());
    var context = Context(cities: cities); var plan = TourPlanningService.Create(context);
    Throws<OperationCanceledException>(() => TourPlanningService.Prepare(report, cities, new(), clock, token));
    Throws<OperationCanceledException>(() => TourPlanningService.Create(context, ct: token));
    Throws<OperationCanceledException>(() => TourPlanningService.Sort(plan, TourOrderKind.Priorities, token));
    Throws<OperationCanceledException>(() => TourPlanningService.SortByProximity(plan, paris, token));
    Throws<OperationCanceledException>(() => TourPlanningService.TicketsForStop(plan, paris, token));
    Throws<OperationCanceledException>(() => TourPlanningService.ToExport(plan, token));
});
Test("A legitimate empty extraction yields an empty usable plan and export", () =>
{
    var source = Sample(); source.Rows.Clear(); source.ExpectedTotal = 0;
    var plan = Plan(data: source); var export = TourPlanningService.ToExport(plan);
    Check(plan.Available && plan.SelectedRows.Count == 0 && export.Rows.Count == 0 && export.Complete && export.ExpectedTotal == 0);
});
Test("Saved order scopes distinguish team, technician identity and status mode", () =>
{
    var team = TourOrderPreferences.ScopeKey(null, false); var personal = TourOrderPreferences.ScopeKey(" alice ", false);
    Check(team != personal && personal == TourOrderPreferences.ScopeKey("ALICE", false));
    Check(personal != TourOrderPreferences.ScopeKey("Alice", true));
});
Test("Order preferences round-trip only scope keys and city IDs, without ticket cells", () =>
{
    var path = PathFor("order.json"); var key = TourOrderPreferences.ScopeKey("Alice", false);
    var preferences = new TourOrderPreferences().With(key, new[] { marseille, paris, lyon }); preferences.Save(path);
    var loaded = TourOrderPreferences.Load(path); Check(loaded.Get(key).SequenceEqual(new[] { marseille, paris, lyon }));
    var json = File.ReadAllText(path); Check(!json.Contains("routeur") && !json.Contains("Paris") && !json.Contains("75000") && !json.Contains("48.8566"));
    var copy = loaded.Get(key); copy[0] = "mutated"; Check(loaded.Get(key)[0] == marseille);
    var updated = preferences.With(key, new[] { lyon }); Check(preferences.Get(key).Length == 3 && updated.Get(key).Length == 1);
});
Test("Missing preference file starts empty without creating directories", () =>
{
    var missingDirectory = PathFor("does-not-exist"); var preferences = TourOrderPreferences.Load(Path.Combine(missingDirectory, "order.json"));
    Check(preferences.Profiles.Count == 0 && !Directory.Exists(missingDirectory));
});
Test("Malformed, unsupported and excessive preference documents fail visibly to the caller", () =>
{
    var path = PathFor("bad.json"); File.WriteAllText(path, "invalid JSON"); Throws<JsonException>(() => TourOrderPreferences.Load(path));
    File.WriteAllText(path, "{\"Version\":99,\"Profiles\":{}}"); Throws<InvalidDataException>(() => TourOrderPreferences.Load(path));
    File.WriteAllText(path, "{\"Version\":1,\"Profiles\":null}"); Throws<InvalidDataException>(() => TourOrderPreferences.Load(path));
    File.WriteAllText(path, new string(' ', TourOrderPreferences.MaximumFileBytes + 1)); Throws<InvalidDataException>(() => TourOrderPreferences.Load(path));
});
Test("Preference bounds reject oversized keys, city lists and control characters", () =>
{
    var preferences = new TourOrderPreferences();
    Throws<InvalidDataException>(() => preferences.With(new string('x', 451), new[] { paris }));
    Throws<InvalidDataException>(() => preferences.With("team:\nactive", new[] { paris }));
    Throws<InvalidDataException>(() => preferences.With("team:|active", new[] { "invalid\ridentifier" }));
    Throws<InvalidDataException>(() => preferences.With("team:|active", Enumerable.Range(0, TourOrderPreferences.MaximumCitiesPerProfile + 1).Select(index => index.ToString())));
});
Test("Preference retention is bounded and keeps the profile just edited", () =>
{
    var preferences = new TourOrderPreferences();
    for (var i = 0; i < TourOrderPreferences.MaximumProfiles + 10; i++) preferences = preferences.With("technician:" + i + "|active", new[] { paris });
    Check(preferences.Profiles.Count == TourOrderPreferences.MaximumProfiles && preferences.Profiles.ContainsKey("technician:137|active"));
});
Test("Failed local preference write leaves the existing document and no temporary debris", () =>
{
    var blocked = PathFor("blocking-file"); File.WriteAllText(blocked, "existing-content");
    var preferences = new TourOrderPreferences().With("team:|active", new[] { paris });
    Throws<IOException>(() => preferences.Save(Path.Combine(blocked, "orders.json")));
    Check(File.ReadAllText(blocked) == "existing-content" && !Directory.EnumerateFiles(directory, "*.tmp").Any());
});

var passed = 0;
try
{
    foreach (var (name, run) in checks)
    {
        try { run(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); }
    }
    Console.WriteLine($"{passed}/{checks.Count} tour planning checks passed.");
    Environment.ExitCode = passed == checks.Count ? 0 : 1;
}
finally { Directory.Delete(directory, true); }
