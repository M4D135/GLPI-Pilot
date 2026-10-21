using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Throws<T>(Action run) where T : Exception
{ try { run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.FromHours(2));
ExtractionResult Table(string[] headers, params string[][] rows) => new()
{
    Headers = headers.ToList(), Rows = rows.ToList(), Complete = true, ExpectedTotal = rows.Length,
    ExtractedAt = now, Scope = "Toutes les pages", SourceUrl = "https://example.invalid/front/ticket.php"
};
ExtractionResult Simple(params string[] values) => Table(new[] { "ID", "Titre", "Statut", "Ville" },
    values.Select((value, index) => new[] { (index + 1).ToString(), value, "Nouveau", index % 2 == 0 ? "Lyon" : "Marseille" }).ToArray());
SavedQuery Query(string field = "", string op = "contains", string value = "", string basis = "total", string aggregate = "count", string aggregateField = "", string groupBy = "")
    => new()
    {
        Name = "Requête synthétique", BaseProbe = basis, Aggregate = aggregate, AggregateField = aggregateField, GroupBy = groupBy,
        Conditions = field.Length == 0 ? new() : new() { new() { Field = field, Operator = op, Value = value } }
    };
QueryResult Run(ExtractionResult source, SavedQuery query, DashboardSettings? settings = null, int[]? rows = null)
{
    settings ??= new();
    var snapshot = rows is null ? DashboardService.Calculate(source, settings, now)
        : DashboardService.CalculateSubset(source, settings, rows, "Personnel synthétique", now);
    return QueryService.Evaluate(source, settings, snapshot, query);
}

Test("Forty stable validated templates leave room for personal queries", () =>
{
    var library = QueryLibrarySettings.CreateDefault(); library.Validate();
    Assert(library.Queries.Count == 40 && library.Queries.Select(query => query.Id).Distinct().Count() == 40);
    Assert(library.Queries.Select(query => query.Id).SequenceEqual(QueryLibrarySettings.CreateDefault().Queries.Select(query => query.Id)));
    Assert(library.Queries.Count(query => query.GroupBy == "role:city") >= 20 && library.Queries.All(query => query.Aggregate == "count"));
});
Test("Native base choices exclude percentages, averages and other queries", () =>
{
    Assert(QueryService.BaseProbes.Any(choice => choice.Key == "city-matched") && QueryService.BaseProbes.Any(choice => choice.Key == "high-overdue"));
    foreach (var basis in new[] { "city-coverage", "age-mean", "active-share", "query:another" })
    { var query = Query(basis: basis); Throws<InvalidDataException>(query.Validate); Assert(!Run(Simple("A"), query).Available); }
});
Test("Field choices offer roles and only exact unique selectable headers", () =>
{
    var choices = QueryService.FieldChoices(new[] { "Titre", "Duplicate", "Duplicate", " titre " });
    Assert(choices.Any(choice => choice.Key == "role:city") && choices.Any(choice => choice.Key == "column: titre "));
    Assert(!choices.Any(choice => choice.Key == "column:Duplicate"));
});
foreach (var literal in new[] { "' OR 1=1", "%", "*", "[abc]", "a_b", "^x+$" })
{
    var captured = literal;
    Test("Filter remains literal: " + captured, () =>
    {
        var result = Run(Simple("prefix " + captured + " suffix", "ordinary"), Query("role:title", "contains", captured));
        Assert(result.Available && result.Rows.SequenceEqual(new[] { 0 }) && result.Metric.Value == 1);
    });
}
Test("Text equality is case insensitive with edge spaces ignored but no fuzzy accents", () =>
{
    var result = Run(Simple("  Équipe A ", "équipe a", "Equipe A", "Équipe AB"), Query("role:title", "equals", " Équipe A "));
    Assert(result.Rows.SequenceEqual(new[] { 0, 1 }));
});
Test("Starts-with and negative literal comparisons retain their meanings", () =>
{
    var source = Simple("Alpha beta", "beta Alpha", "Gamma", "");
    Assert(Run(source, Query("role:title", "starts-with", "Alpha")).Rows.SequenceEqual(new[] { 0 }));
    Assert(Run(source, Query("role:title", "not-contains", "Alpha")).Rows.SequenceEqual(new[] { 2, 3 }));
    Assert(Run(source, Query("role:title", "not-equals", "Gamma")).Rows.SequenceEqual(new[] { 0, 1, 3 }));
});
Test("Empty and not-empty use real blank values, not invented numeric zeroes", () =>
{
    var source = Simple("", "  ", "0", "Nouveau"); source.Rows[0] = new[] { "1" };
    Assert(Run(source, Query("role:title", "empty")).Rows.SequenceEqual(new[] { 0, 1 }));
    Assert(Run(source, Query("role:title", "not-empty")).Rows.SequenceEqual(new[] { 2, 3 }));
});
Test("All and any combine explicitly without changing the base cohort", () =>
{
    var source = Simple("Alpha", "Alpha", "Gamma", "Gamma");
    var query = Query("role:title", "equals", "Alpha"); query.Conditions.Add(new() { Field = "role:city", Operator = "equals", Value = "Lyon" });
    Assert(Run(source, query).Rows.SequenceEqual(new[] { 0 }));
    query.Match = "any"; Assert(Run(source, query).Rows.SequenceEqual(new[] { 0, 1, 2 }));
});
Test("No conditions means the whole explicit base even in any mode", () =>
{
    var query = Query(); query.Match = "any";
    Assert(Run(Simple("A", "B"), query).Metric.Value == 2);
});
Test("A missing condition column never slips through an OR branch", () =>
{
    var query = Query("role:title", "contains", "Alpha"); query.Match = "any";
    query.Conditions.Add(new() { Field = "column:Absent", Operator = "not-empty" });
    var result = Run(Simple("Alpha"), query);
    Assert(!result.Available && result.Metric.Value is null && result.Rows.Length == 0);
});
Test("A missing field remains unavailable on an empty dataset", () =>
{
    var result = Run(Simple(), Query("column:Absent", "empty"));
    Assert(!result.Available && result.Metric.Value is null);
});
Test("A true OR branch retains a row whose other numeric value is unreadable", () =>
{
    var source = Table(new[] { "Titre", "Montant" }, new[] { "Alpha", "???" }, new[] { "Alpha", "0" }, new[] { "Gamma", "7" });
    var query = Query("role:title", "equals", "Alpha"); query.Match = "any";
    query.Conditions.Add(new() { Field = "column:Montant", Operator = "number-gt", Value = "5" });
    var result = Run(source, query);
    Assert(result.Available && result.Rows.SequenceEqual(new[] { 0, 1, 2 }));
    Assert(result.Message.Contains("1 ligne(s) avec une valeur", StringComparison.Ordinal) && result.Message.Contains("dont 0 non retenue(s)", StringComparison.Ordinal));
});
Test("An urgent title survives an unreadable due date in OR but not in AND", () =>
{
    var source = Table(new[] { "Titre", "Échéance" }, new[] { "URGENT imprimante", "" }, new[] { "Ordinaire", "???" }, new[] { "Ordinaire", "20/10/2026" });
    var query = Query("role:title", "contains", "urgent"); query.Match = "any";
    query.Conditions.Add(new() { Field = "role:due", Operator = "date-before", Value = "2026-10-21" });
    var result = Run(source, query);
    Assert(result.Available && result.Rows.SequenceEqual(new[] { 0, 2 }));
    Assert(result.Message.Contains("2 ligne(s) avec une valeur", StringComparison.Ordinal) && result.Message.Contains("dont 1 non retenue(s)", StringComparison.Ordinal));
    Assert(result.Metric.Explanation.Contains("1 retenue(s) grâce", StringComparison.Ordinal));
    query.Match = "all"; var required = Run(source, query);
    Assert(required.Rows.Length == 0 && required.Message.Contains("dont 2 non retenue(s)", StringComparison.Ordinal));
});
Test("Ambiguous semantic headers fail until an exact mapping is supplied", () =>
{
    var source = Table(new[] { "Titre", "Sujet" }, new[] { "Alpha", "Beta" });
    var query = Query("role:title", "equals", "Alpha");
    Assert(!Run(source, query).Available);
    Assert(Run(source, query, new() { ColumnMappings = new() { ["title"] = "Titre" } }).Metric.Value == 1);
});
Test("An explicit missing mapping is never replaced with an automatic header", () =>
{
    var result = Run(Simple("Alpha"), Query("role:title", "equals", "Alpha"), new() { ColumnMappings = new() { ["title"] = "Absent" } });
    Assert(!result.Available && result.Metric.Value is null);
});
Test("Duplicate exact columns and changed column order fail safely", () =>
{
    var duplicate = Table(new[] { "Valeur", "Valeur" }, new[] { "Alpha", "Beta" });
    Assert(!Run(duplicate, Query("column:Valeur", "equals", "Alpha")).Available);
    var source = Simple("Alpha"); var snapshot = DashboardService.Calculate(source, new(), now);
    source.Headers[1] = "Autre";
    Assert(!QueryService.Evaluate(source, new(), snapshot, Query("role:title", "equals", "Alpha")).Available);
});
Test("Reordered data with a freshly calculated snapshot keeps correct indices", () =>
{
    var source = Table(new[] { "Ville", "Titre", "ID" }, new[] { "Lyon", "Alpha", "1" }, new[] { "Paris", "Beta", "2" });
    Assert(Run(source, Query("role:city", "equals", "Paris")).Rows.SequenceEqual(new[] { 1 }));
});
Test("Personal subsets intersect every base and preserve original row indices", () =>
{
    var source = Simple("A", "B", "C", "D", "E");
    var result = Run(source, Query(groupBy: "role:city"), rows: new[] { 4, 1 });
    Assert(result.Rows.SequenceEqual(new[] { 1, 4 }) && result.Metric.Value == 2);
    Assert(result.Distribution!.Buckets.SelectMany(bucket => bucket.Rows).Order().SequenceEqual(new[] { 1, 4 }));
});
Test("Base indices outside the snapshot scope cannot escape into results", () =>
{
    var source = Simple("A", "B", "C", "D"); var snapshot = DashboardService.CalculateSubset(source, new(), new[] { 1, 3 }, "Personnel", now);
    snapshot.Metrics.RemoveAll(metric => metric.Key == "active");
    snapshot.Metrics.Add(new("active", "Actifs", 4, "lignes", "Fixture de cohorte large", new[] { 0, 1, 2, 3, 3 }));
    var result = QueryService.Evaluate(source, new(), snapshot, Query(basis: "active"));
    Assert(result.Rows.SequenceEqual(new[] { 1, 3 }) && result.Metric.Value == 2);
});
Test("A stale out-of-range source index makes the query unavailable", () =>
{
    var source = Simple("A"); var snapshot = DashboardService.Calculate(source, new(), now);
    snapshot.Metric("total")!.Rows[0] = 99;
    Assert(!QueryService.Evaluate(source, new(), snapshot, Query()).Available);
});
Test("Unknown statuses keep active-base queries unavailable rather than zero", () =>
{
    var source = Simple("A"); source.Rows[0][2] = "Statut inconnu";
    Assert(!Run(source, Query(basis: "active")).Available);
});
Test("A known empty active cohort remains a valid zero count", () =>
{
    var source = Simple("A"); source.Rows[0][2] = "Clos";
    var result = Run(source, Query(basis: "active"));
    Assert(result.Available && result.Metric.Value == 0 && result.Metric.Sample == 0);
});
Test("Percent uses the base population before conditions", () =>
{
    var result = Run(Simple("A", "B", "C", "D"), Query("role:city", "equals", "Lyon", aggregate: "percent"));
    Assert(result.Available && result.Metric.Value == 50 && result.Metric.Sample == 4 && result.Metric.Rows.SequenceEqual(new[] { 0, 2 }));
});
Test("An empty percentage denominator remains null", () =>
{
    var result = Run(Simple(), Query(aggregate: "percent"));
    Assert(result.Available && result.Metric.Value is null && result.Metric.Sample == 0);
});
Test("Duplicate ticket IDs do not silently deduplicate query rows", () =>
{
    var source = Simple("A", "A"); source.Rows[1][0] = source.Rows[0][0];
    var result = Run(source, Query());
    Assert(result.Metric.Value == 2 && result.Metric.Unit == "lignes");
});
Test("French and invariant decimals with valid space grouping aggregate coherently", () =>
{
    var source = Table(new[] { "Montant" }, new[] { "1 234,5" }, new[] { "2\u202f000.25" }, new[] { "-4,75" }, new[] { "+0.5" });
    var result = Run(source, Query(aggregate: "sum", aggregateField: "column:Montant"));
    Assert(result.Available && result.Metric.Value == 3230.5 && result.Metric.Sample == 4);
});
Test("Competing separators, currency and nonfinite strings are excluded, not guessed", () =>
{
    var source = Table(new[] { "Montant" }, new[] { "1,234.56" }, new[] { "12 €" }, new[] { "50%" }, new[] { "NaN" }, new[] { "Infinity" }, new[] { "1 23" }, new[] { "3" });
    var result = Run(source, Query(aggregate: "sum", aggregateField: "column:Montant"));
    Assert(result.Available && result.Metric.Value == 3 && result.Metric.Sample == 1 && result.Rows.Length == 7 && result.Metric.Rows.SequenceEqual(new[] { 6 }));
    Assert(result.Message.Contains("6 valeur(s)", StringComparison.Ordinal));
});
Test("A lone comma is a decimal separator, never an assumed thousands separator", () =>
{
    Assert(Run(Table(new[] { "Montant" }, new[] { "1,234" }), Query(aggregate: "sum", aggregateField: "column:Montant")).Metric.Value == 1.234);
});
Test("All numeric comparison operators compare finite values", () =>
{
    var source = Table(new[] { "Montant" }, new[] { "1" }, new[] { "2,5" }, new[] { "3" }, new[] { "" });
    foreach (var (op, expected) in new[]
    {
        ("number-eq", new[] { 1 }), ("number-gt", new[] { 2 }), ("number-gte", new[] { 1, 2 }),
        ("number-lt", new[] { 0 }), ("number-lte", new[] { 0, 1 })
    }) Assert(Run(source, Query("column:Montant", op, "2.5")).Rows.SequenceEqual(expected), op);
});
Test("Numeric aggregates use exact contributing row indices and their sample", () =>
{
    var source = Table(new[] { "Montant" }, new[] { "2" }, new[] { "6" }, new[] { "?" }, new[] { "" });
    foreach (var (aggregate, expected) in new[] { ("sum", 8d), ("average", 4d), ("min", 2d), ("max", 6d) })
    {
        var result = Run(source, Query(aggregate: aggregate, aggregateField: "column:Montant"));
        Assert(result.Metric.Value == expected && result.Metric.Sample == 2 && result.Metric.Rows.SequenceEqual(new[] { 0, 1 }) && result.Rows.Length == 4);
    }
});
Test("Numeric sums use compensated addition for very different magnitudes", () =>
{
    var source = Table(new[] { "Montant" }, new[] { "10000000000000000" }, new[] { "1" }, new[] { "-10000000000000000" });
    Assert(Run(source, Query(aggregate: "sum", aggregateField: "column:Montant")).Metric.Value == 1);
});
Test("An empty numeric set never reports a fabricated zero even for sum", () =>
{
    foreach (var aggregate in new[] { "sum", "average", "min", "max" })
    {
        var result = Run(Table(new[] { "Montant" }, new[] { "?" }), Query(aggregate: aggregate, aggregateField: "column:Montant"));
        Assert(result.Available && result.Metric.Value is null && result.Metric.Sample == 0 && result.Metric.Rows.Length == 0);
    }
});
Test("Missing aggregate or grouping fields make the complete query unavailable", () =>
{
    Assert(!Run(Simple("A"), Query(aggregate: "sum", aggregateField: "column:Absent")).Available);
    var result = Run(Simple("A"), Query(groupBy: "column:Absent"));
    Assert(!result.Available && result.Metric.Value is null && result.Distribution is { Available: false });
});
Test("Distinct ignores blank values and retains all nonblank contributing rows", () =>
{
    var source = Table(new[] { "Valeur" }, new[] { "Alpha" }, new[] { " alpha " }, new[] { "Beta" }, new[] { "" });
    var result = Run(source, Query(aggregate: "distinct", aggregateField: "column:Valeur"));
    Assert(result.Metric.Value == 2 && result.Metric.Sample == 3 && result.Metric.Rows.SequenceEqual(new[] { 0, 1, 2 }));
    Assert(Run(Table(new[] { "Valeur" }), Query(aggregate: "distinct", aggregateField: "column:Valeur")).Metric.Value == 0);
});
Test("Technician equality matches exact detected names instead of substrings", () =>
{
    var source = Table(new[] { "Technicien" }, new[] { "Alice\nBob" }, new[] { "Malice" }, new[] { "Alice Dupont" }, new[] { "ALICE; alice" });
    Assert(Run(source, Query("role:assignee", "equals", "Alice")).Rows.SequenceEqual(new[] { 0, 3 }));
    Assert(Run(source, Query("role:assignee", "not-equals", "Alice")).Rows.SequenceEqual(new[] { 1, 2 }));
    Assert(Run(source, Query("column:Technicien", "equals", "Alice")).Rows.Length == 0);
});
Test("Technician grouping deduplicates a person within a row and keeps commas intact", () =>
{
    var source = Table(new[] { "Technicien" }, new[] { "Alice\nBob; alice" }, new[] { "Bob | Chloé" }, new[] { "Dupont, Jean" }, new[] { "" });
    var result = Run(source, Query(groupBy: "role:assignee"));
    var buckets = result.Distribution!.Buckets;
    Assert(buckets.Single(bucket => bucket.Label == "Alice").Rows.SequenceEqual(new[] { 0 }));
    Assert(buckets.Single(bucket => bucket.Label == "Bob").Rows.SequenceEqual(new[] { 0, 1 }));
    Assert(buckets.Any(bucket => bucket.Label == "Dupont, Jean") && buckets.Sum(bucket => bucket.Count) == 6 && result.Distribution.Denominator == 4);
    var distinct = Run(source, Query(aggregate: "distinct", aggregateField: "role:assignee"));
    Assert(distinct.Metric.Value == 4 && distinct.Metric.Rows.SequenceEqual(new[] { 0, 1, 2 }));
});
Test("Grouping always counts filtered rows, independently of numeric contributions", () =>
{
    var source = Table(new[] { "Ville", "Montant" }, new[] { "Lyon", "10" }, new[] { "Lyon", "?" }, new[] { "", "20" });
    var result = Run(source, Query(aggregate: "average", aggregateField: "column:Montant", groupBy: "role:city"));
    Assert(result.Metric.Value == 15 && result.Metric.Rows.SequenceEqual(new[] { 0, 2 }));
    Assert(result.Distribution!.Denominator == 3 && result.Distribution.Buckets.Single(bucket => bucket.Label == "Lyon").Count == 2);
    Assert(result.Distribution.Buckets.Single(bucket => bucket.Label.StartsWith("Non renseigné", StringComparison.Ordinal)).Rows.SequenceEqual(new[] { 2 }));
});
Test("Date before and after are strict at midnight", () =>
{
    var source = Table(new[] { "Date d'ouverture" }, new[] { "20/10/2026 23:59:59" }, new[] { "21/10/2026 00:00:00" }, new[] { "21/10/2026 00:00:01" }, new[] { "invalide" });
    Assert(Run(source, Query("role:created", "date-before", "2026-10-21")).Rows.SequenceEqual(new[] { 0 }));
    Assert(Run(source, Query("role:created", "date-after", "2026-10-21")).Rows.SequenceEqual(new[] { 2 }));
});
Test("Last days includes both boundaries, excludes future and unreadable dates", () =>
{
    var source = Table(new[] { "Date d'ouverture" }, new[] { "14/10/2026 00:00:00" }, new[] { "13/10/2026 23:59:59" }, new[] { "21/10/2026 00:00:00" }, new[] { "21/10/2026 00:00:01" }, new[] { "" });
    var result = Run(source, Query("role:created", "last-days", "7"));
    Assert(result.Rows.SequenceEqual(new[] { 0, 2 }) && result.Message.Contains("dont 1 non retenue(s)", StringComparison.Ordinal));
    Assert(result.Metric.Explanation.Contains("21/10/2026 00:00:00 +02:00", StringComparison.Ordinal));
});
Test("Next days and age-at-least respect the same snapshot instant", () =>
{
    var source = Table(new[] { "Échéance" }, new[] { "20/10/2026 23:59:59" }, new[] { "21/10/2026 00:00:00" }, new[] { "22/10/2026 00:00:00" }, new[] { "22/10/2026 00:00:01" });
    Assert(Run(source, Query("role:due", "next-days", "1")).Rows.SequenceEqual(new[] { 1, 2 }));
    var old = Table(new[] { "Date d'ouverture" }, new[] { "14/10/2026" }, new[] { "14/10/2026 00:00:01" }, new[] { "01/01/2026" }, new[] { "22/10/2026" });
    Assert(Run(old, Query("role:created", "age-at-least", "7")).Rows.SequenceEqual(new[] { 0, 2 }));
});
Test("Explicit ISO offsets convert into the snapshot time offset", () =>
{
    var source = Table(new[] { "Date d'ouverture" }, new[] { "2026-10-20T22:00:00Z" }, new[] { "2026-10-20T21:59:59Z" }, new[] { "2026-10-20T22:00:01Z" });
    Assert(Run(source, Query("role:created", "last-days", "0")).Rows.SequenceEqual(new[] { 0 }));
});
Test("Configured date culture controls ambiguous dates without US guessing", () =>
{
    var source = Table(new[] { "Date d'ouverture" }, new[] { "10/11/2026" });
    var query = Query("role:created", "date-before", "2026-10-21");
    Assert(Run(source, query, new() { DateCulture = "fr-FR" }).Rows.Length == 0);
    Assert(Run(source, query, new() { DateCulture = "en-US" }).Rows.SequenceEqual(new[] { 0 }));
    Assert(Run(source, query, new() { DateCulture = "invariant" }).Rows.Length == 0);
});
Test("An unreadable configured date bound makes the query unavailable", () =>
{
    var result = Run(Table(new[] { "Date d'ouverture" }, new[] { "21/10/2026" }), Query("role:created", "date-before", "demain"));
    Assert(!result.Available && result.Metric.Value is null);
});
Test("Negative and excessive date windows and invalid numeric operands fail validation", () =>
{
    foreach (var value in new[] { "-1", "36501", "NaN", "1,2.3" }) Throws<InvalidDataException>(() => Query("role:created", "last-days", value).Validate());
    Throws<InvalidDataException>(() => Query("role:title", "number-gt", "Infinity").Validate());
});
Test("Incomplete source scope remains explicit in query values and export", () =>
{
    var source = Simple("A", "B"); source.Complete = false; source.ExpectedTotal = 100;
    var snapshot = DashboardService.CalculateSubset(source, new(), new[] { 1 }, "Personnel", now);
    var query = Query(); var library = new QueryLibrarySettings { Queries = new() { query } };
    QueryService.AppendDashboardQueries(snapshot, source, new(), library);
    Assert(snapshot.Metric(QueryService.MetricKey(query.Id))!.Value == 1 && !snapshot.Complete && snapshot.ExpectedTotal is null);
    Assert(snapshot.Metric(QueryService.MetricKey(query.Id))!.Explanation.Contains("incomplet", StringComparison.Ordinal));
    var export = DashboardService.ToExport(snapshot);
    Assert(export.Rows.Any(row => row.Contains(query.Name) && row.Contains("1")));
});
Test("City probes and effective inferred-city mapping feed queries together", () =>
{
    var source = Simple("LY incident", "Marseille incident", "LY Marseille", "Autre");
    var report = CityDetectionService.Enrich(source, new() { Cities = new() { new() { Name = "Lyon", Aliases = new() { "LY" } }, new() { Name = "Marseille" } } }, new());
    var settings = new DashboardSettings { ColumnMappings = new() { ["city"] = report.CityColumnName } };
    var snapshot = DashboardService.Calculate(report.Data, settings, now); CityDetectionService.AppendDashboardProbes(snapshot, report);
    var ambiguous = QueryService.Evaluate(report.Data, settings, snapshot, Query(basis: "city-ambiguous"));
    Assert(ambiguous.Metric.Value == 1 && ambiguous.Rows.SequenceEqual(new[] { 2 }));
    var byCity = QueryService.Evaluate(report.Data, settings, snapshot, Query("role:city", "equals", "Lyon", basis: "active"));
    Assert(byCity.Rows.SequenceEqual(new[] { 0 }));
});
Test("Disabled city detection leaves its query bases unavailable", () =>
{
    var source = Simple("Lyon"); var report = CityDetectionService.Enrich(source, new(), new());
    var snapshot = DashboardService.Calculate(source, new(), now); CityDetectionService.AppendDashboardProbes(snapshot, report);
    Assert(!QueryService.Evaluate(source, new(), snapshot, Query(basis: "city-matched")).Available);
});
Test("Appending a library twice replaces only its own metrics and groups", () =>
{
    var source = Simple("A", "B"); var snapshot = DashboardService.Calculate(source, new(), now);
    var total = snapshot.Metric("total"); var query = Query(groupBy: "role:city"); var library = new QueryLibrarySettings { Queries = new() { query } };
    QueryService.AppendDashboardQueries(snapshot, source, new(), library);
    QueryService.AppendDashboardQueries(snapshot, source, new(), library);
    Assert(snapshot.Metrics.Count(metric => metric.Key == QueryService.MetricKey(query.Id)) == 1 && snapshot.Metric("total") == total);
    Assert(snapshot.Distributions.Count(distribution => distribution.Key == QueryService.DistributionKey(query.Id)) == 1);
    QueryService.AppendDashboardQueries(snapshot, source, new(), new());
    Assert(snapshot.Metrics.All(metric => !metric.Key.StartsWith("query:", StringComparison.Ordinal)) && snapshot.Metric("total") == total);
});
Test("The forty-template library evaluates through available and unavailable data honestly", () =>
{
    var source = Simple("A", "B"); var snapshot = DashboardService.Calculate(source, new(), now);
    QueryService.AppendDashboardQueries(snapshot, source, new(), QueryLibrarySettings.CreateDefault());
    Assert(snapshot.Metrics.Count(metric => metric.Key.StartsWith("query:", StringComparison.Ordinal)) == 40);
    Assert(snapshot.Metrics.Where(metric => metric.Key.StartsWith("query:", StringComparison.Ordinal)).Any(metric => metric.Value is null));
});
Test("Every documented configuration size limit is checked on raw values", () =>
{
    foreach (var change in new Action<SavedQuery>[]
    {
        query => query.Name = new string('x', 121), query => query.Description = new string('x', 1201),
        query => query.Category = new string('x', 81), query => query.GroupBy = "column:" + new string('x', 506),
        query => query.Conditions = new() { new() { Field = "role:title", Value = new string('x', 501) } },
        query => query.Conditions = new() { new() { Field = "role:title", Value = "A", Value2 = new string('x', 501) } },
        query => query.Conditions = Enumerable.Range(0, 21).Select(_ => new QueryCondition { Operator = "empty" }).ToList()
    }) { var query = Query(); change(query); Throws<InvalidDataException>(query.Validate); }
    var library = new QueryLibrarySettings { Queries = Enumerable.Range(0, 129).Select(_ => Query()).ToList() };
    Throws<InvalidDataException>(library.Validate);
});
Test("Unknown roles, operators, aggregates and missing operands fail strict validation", () =>
{
    foreach (var query in new[]
    {
        Query("role:invented", "empty"), Query("Titre", "empty"), Query("role:title", "eval", "x"),
        Query("role:title", "contains", ""), Query(aggregate: "median"), Query(aggregate: "sum"),
        Query(groupBy: "role:invented")
    }) Throws<InvalidDataException>(query.Validate);
    Query("column:Not yet present", "empty").Validate();
});
Test("GUID N identities must be valid, nonempty and unique regardless of hex case", () =>
{
    var query = Query(); var duplicate = query.Copy(); duplicate.Id = query.Id.ToUpperInvariant();
    Throws<InvalidDataException>(() => new QueryLibrarySettings { Queries = new() { query, duplicate } }.Validate());
    foreach (var id in new[] { "bad", Guid.Empty.ToString("N"), Guid.NewGuid().ToString("D") })
    { var bad = Query(); bad.Id = id; Throws<InvalidDataException>(bad.Validate); }
});
Test("An empty library is valid and JSON retains definitions and favorites", () =>
{
    new QueryLibrarySettings().Validate();
    var query = Query("role:city", "equals", "Lyon"); query.Favorite = true; query.Description = "Ligne 1\nLigne 2";
    var library = new QueryLibrarySettings { Queries = new() { query } };
    var restored = JsonSerializer.Deserialize<QueryLibrarySettings>(JsonSerializer.Serialize(library))!; restored.Validate();
    Assert(restored.Queries[0].Id == query.Id && restored.Queries[0].Favorite && restored.Queries[0].Conditions[0].Value == "Lyon" && restored.Queries[0].Description == query.Description);
});
Test("Draft copies are deep and preserve invalid raw input until strict validation", () =>
{
    var query = Query("role:city", "equals", "Lyon"); query.Conditions[0].Operator = "invalid";
    var copy = query.Copy(); Assert(copy.Conditions[0].Operator == "invalid"); Throws<InvalidDataException>(copy.Validate);
    copy.Conditions[0].Value = "Autre"; Assert(query.Conditions[0].Value == "Lyon");
    var library = new QueryLibrarySettings { Queries = new() { query } }; var clone = library.Copy();
    clone.Queries[0].Name = "Autre"; Assert(library.Queries[0].Name != "Autre");
});
Test("Local normalization discards invalid logic instead of weakening conditions", () =>
{
    var valid = Query("role:city", "equals", "Lyon");
    var invalid = Query("role:city", "equals", "Lyon"); invalid.Conditions.Add(new() { Field = "role:city", Operator = "not-an-operator", Value = "Paris" });
    var tooMany = Query(); tooMany.Conditions = Enumerable.Range(0, 21).Select(_ => new QueryCondition { Operator = "empty" }).ToList();
    var library = new QueryLibrarySettings { Queries = new() { valid, invalid, tooMany, null! } };
    library.Normalize(); library.Validate();
    Assert(library.Queries.Count == 1 && library.Queries[0].Id == valid.Id && library.Queries[0].Conditions.Count == 1 && invalid.Conditions.Count == 2);
});
Test("Local normalization repairs IDs without mutating repeated draft references", () =>
{
    var query = Query(); var library = new QueryLibrarySettings { Queries = new() { query, query } };
    library.Normalize(); library.Validate();
    Assert(library.Queries[0].Id == query.Id && library.Queries[1].Id != query.Id && !ReferenceEquals(library.Queries[0], library.Queries[1]));
});
Test("Null local settings recover without blocking startup", () =>
{
    var library = new QueryLibrarySettings { Queries = null! }; library.Normalize(); library.Validate(); Assert(library.Queries.Count == 0);
    var query = Query(); query.Conditions = null!; Throws<InvalidDataException>(query.Validate);
    library.Queries.Add(query); library.Normalize(); Assert(library.Queries.Count == 0);
});
Test("Queries search beyond the preview limit and keep later source indices", () =>
{
    var source = Simple(Enumerable.Range(0, 12005).Select(index => index == 12001 ? "target" : "ordinary").ToArray());
    var result = Run(source, Query("role:title", "equals", "target"));
    Assert(result.Rows.SequenceEqual(new[] { 12001 }) && result.Metric.Value == 1);
});
Test("Cancellation is honored before evaluation and prevents partial library publication", () =>
{
    var source = Simple("A"); var snapshot = DashboardService.Calculate(source, new(), now);
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Throws<OperationCanceledException>(() => QueryService.Evaluate(source, new(), snapshot, Query(), cancellation.Token));
    var count = snapshot.Metrics.Count;
    Throws<OperationCanceledException>(() => QueryService.AppendDashboardQueries(snapshot, source, new(), QueryLibrarySettings.CreateDefault(), cancellation.Token));
    Assert(snapshot.Metrics.Count == count && snapshot.Metrics.All(metric => !metric.Key.StartsWith("query:", StringComparison.Ordinal)));
});
Test("Large running evaluation can be cancelled without publishing a result", () =>
{
    var source = Simple(Enumerable.Repeat("A", 100000).ToArray());
    var snapshot = DashboardService.Calculate(source, new(), now);
    var query = Query("role:title", "contains", "A");
    for (var index = 1; index < 20; index++) query.Conditions.Add(query.Conditions[0].Copy());
    using var cancellation = new CancellationTokenSource();
    cancellation.CancelAfter(1);
    Throws<OperationCanceledException>(() => QueryService.Evaluate(source, new(), snapshot, query, cancellation.Token));
    Assert(snapshot.Metric(QueryService.MetricKey(query.Id)) is null);
});

var passed = 0;
foreach (var (name, run) in tests)
    try { run(); passed++; Console.WriteLine("PASS  " + name); }
    catch (Exception exception) { Console.WriteLine("FAIL  " + name + " · " + exception); }
Console.WriteLine($"{passed}/{tests.Count} vérifications réussies.");
return passed == tests.Count ? 0 : 1;
