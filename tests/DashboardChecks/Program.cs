using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using GlpiPilot.Core;

var clock = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(2));
var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Equal(double? actual, double expected, string name) => Check(actual.HasValue && Math.Abs(actual.Value - expected) < .000001, $"{name}: expected {expected}, actual {actual}");
DashboardSnapshot Calc(ExtractionResult? data = null, DashboardSettings? settings = null) => DashboardService.Calculate(data ?? Sample(), settings ?? new(), clock);
ExtractionResult Sample() => new()
{
    Headers = new() { "ID", "Titre", "Statut", "Priorité", "Type", "Date d'ouverture", "Dernière modification", "Date de résolution", "Date de clôture", "Temps de résolution", "Attribué à - Technicien", "Attribué à - Groupe de techniciens", "Catégorie", "Entité", "Demandeur", "Lieu", "Source de la demande" },
    Rows = new()
    {
        new[] { "100", "Premier", "Nouveau", "Haute", "Incident", "01/10/2026 12:00", "10/10/2026 12:00", "", "", "20/10/2026 12:00", "", "", "Matériel", "Est", "A", "Site 1", "Mail" },
        new[] { "101", "En attente", "En attente", "Basse", "Demande", "20/10/2026 12:00", "21/10/2026 12:00", "", "", "22/10/2026 12:00", "Sam", "Support", "Accès", "Est", "B", "Site 2", "Web" },
        new[] { "102", "Résolu", "Résolu", "Moyenne", "1", "10/10/2026 12:00", "15/10/2026 12:00", "15/10/2026 12:00", "", "14/10/2026 12:00", "Sam", "Support", "Accès", "Est", "A", "Site 1", "Web" },
        new[] { "103", "Clos", "Clos", "Majeure", "2", "01/10/2026 12:00", "05/10/2026 12:00", "03/10/2026 12:00", "05/10/2026 12:00", "04/10/2026 12:00", "Alex", "Réseau", "Réseau", "Ouest", "C", "Site 3", "Téléphone" },
        new[] { "104", "Inconnu", "Personnalisé", "Critique personnalisé", "Autre", "date invalide", "", "", "", "", "", "", "", "", "", "", "" },
        new[] { "105", "Ancien", "En cours (attribué)", "Très haute", "Incident", "01/09/2026 12:00", "01/10/2026 12:00", "", "", "", "", "Réseau", "Réseau", "Ouest", "A", "Site 3", "Mail" }
    },
    ExpectedTotal = 6, Complete = true, Scope = "Toutes les pages", SourceUrl = "https://glpi.example.invalid/front/ticket.php", ExtractedAt = clock
};

Test("French headers: 17 available semantic roles resolved", () => Check(Calc().Columns.Count(c => c.Index.HasValue) == 17));
Test("Six statuses: active/pending/solved/closed and unknown stay separate", () =>
{
    var d = Calc(); Equal(d.Metric("active")!.Value, 3, "active"); Equal(d.Metric("pending")!.Value, 1, "pending");
    Equal(d.Metric("resolved")!.Value, 1, "resolved"); Equal(d.Metric("closed")!.Value, 1, "closed"); Equal(d.Metric("unknown-status")!.Value, 1, "unknown");
    Equal(d.Metric("completed-share")!.Value, 40, "share denominator"); Check(d.Metric("completed-share")!.Sample == 5);
});
Test("Calendar overdue and due soon: active dates only, inclusive 24h edge", () =>
{
    var d = Calc(); Equal(d.Metric("overdue")!.Value, 1, "overdue"); Equal(d.Metric("soon")!.Value, 1, "soon"); Check(d.Metric("overdue")!.Sample == 2);
    Check(d.Metric("overdue")!.Rows.SequenceEqual(new[] { 0 }));
});
Test("Priorities: completed major does not enter active priorities", () =>
{
    var d = Calc(); Equal(d.Metric("high")!.Value, 2, "high"); Equal(d.Metric("major")!.Value, 0, "major"); Check(d.Metric("high")!.Rows.SequenceEqual(new[] { 0, 5 }));
});
Test("Unassigned means both technician and group missing", () =>
{
    var d = Calc(); Equal(d.Metric("unassigned")!.Value, 1, "unassigned"); Check(d.Metric("unassigned")!.Rows.SequenceEqual(new[] { 0 }));
});
Test("One assignment dimension never claims total lack of assignment", () =>
{
    var s = Sample(); s.Headers[11] = "Colonne sans rôle"; var d = Calc(s);
    Check(d.Metric("unassigned")!.Label == "Sans technicien renseigné"); Check(!d.Weather.Signals.Any(x => x.Label == "Sans affectation"));
});
Test("Age, inactivity, median and P90 use valid dates and fixed clock", () =>
{
    var d = Calc(); Equal(d.Metric("old")!.Value, 1, "old"); Equal(d.Metric("stale")!.Value, 2, "stale");
    Equal(d.Metric("age-mean")!.Value, 71d / 3, "mean age"); Equal(d.Metric("age-median")!.Value, 20, "median age"); Equal(d.Metric("age-p90")!.Value, 44, "p90 age");
});
Test("Resolution and closure durations are calendar hours with sample size", () =>
{
    var d = Calc(); Equal(d.Metric("resolution-mean")!.Value, 84, "mean"); Equal(d.Metric("resolution-median")!.Value, 84, "median");
    Equal(d.Metric("resolution-p90")!.Value, 112.8, "p90"); Equal(d.Metric("closure-mean")!.Value, 96, "closure"); Check(d.Metric("resolution-mean")!.Sample == 2);
});
Test("Rolling events include only dates present in this snapshot", () =>
{
    var d = Calc(); Equal(d.Metric("created-7")!.Value, 1, "created7"); Equal(d.Metric("created-30")!.Value, 4, "created30");
    Equal(d.Metric("resolved-7")!.Value, 1, "resolved7"); Check(d.Timeline.Count == 30 && d.Timeline[^2].Created.SequenceEqual(new[] { 1 }));
});
Test("Weighted weather has independent denominators and marks unknown status", () =>
{
    var d = Calc(); Equal(d.Weather.Score, 52.5, "weather score"); Check(d.Weather.State == "storm" && d.Weather.Title.Contains("partielles"));
    Check(d.Weather.Signals.First().Sample == 2 && d.Weather.MeasuredWeight == 100);
});
Test("Missing dates and priorities do not become positive sunshine evidence", () =>
{
    var s = new ExtractionResult { Headers = new() { "ID", "Statut", "Priorité" }, Rows = new() { new[] { "1", "Nouveau", "" } }, Complete = true, ExpectedTotal = 1 };
    var d = Calc(s); Check(d.Weather.State == "unknown" && d.Weather.Score == null);
});
Test("One low-risk signal produces partial weather rather than sun", () =>
{
    var s = new ExtractionResult { Headers = new() { "ID", "Statut", "Priorité" }, Rows = new() { new[] { "1", "Nouveau", "Basse" } }, Complete = true, ExpectedTotal = 1 };
    var d = Calc(s); Check(d.Weather.State == "partial" && d.Weather.MeasuredWeight == 25);
});
Test("Existing but empty analytic fields never report zero evaluated risk", () =>
{
    var s = new ExtractionResult { Headers = new() { "ID", "Statut", "Priorité", "Échéance", "Date d'ouverture", "Dernière modification" }, Rows = new() { new[] { "1", "Nouveau", "", "", "", "" } }, Complete = true, ExpectedTotal = 1 };
    var d = Calc(s); foreach (var key in new[] { "high", "overdue", "soon", "stale", "old" }) Check(d.Metric(key)!.Value == null, key);
});
Test("Healthy complete dated snapshot produces sun with sample coverage", () =>
{
    var s = Sample(); s.Rows = new() { s.Rows[1] }; s.ExpectedTotal = 1;
    var d = Calc(s); Check(d.Weather.State == "sun"); Equal(d.Weather.Score, 0, "healthy score");
});
Test("Partial total downgrades low score despite enough columns", () =>
{
    var s = Sample(); s.Rows = new() { s.Rows[1] }; s.ExpectedTotal = 100; var d = Calc(s);
    Check(!d.Complete && d.Weather.State == "partial" && d.Warnings.Count > 0);
});
Test("Complete reception with representation reserves keeps usable counts and cautious weather/history", () =>
{
    var s = Sample(); s.Rows = new() { s.Rows[1] }; s.ExpectedTotal = 1; s.Complete = false;
    s.Warnings = new() { "Réserve de comparaison synthétique", "Réserve de comparaison synthétique" };
    s.Quality = new() { SourceRows = 1, ExpectedRows = 1, AllPagesRequested = true, RowCountVerified = true, FinalTotalVerified = true,
        InitialSample = new() { IdentityVerified = true, RepresentationVerified = false, SampleSize = 1, ComparedCells = 1, DifferentCells = 1 },
        FinalSample = new() { IdentityVerified = true, RepresentationVerified = true, SampleSize = 1, ComparedCells = 1 } };
    var d = Calc(s);
    Check(d.ReceptionComplete && !d.Complete && d.ExpectedTotal == 1 && d.Quality == s.Quality);
    Check(d.Warnings.Count(warning => warning == "Réserve de comparaison synthétique") == 1);
    Check(d.Warnings.Any(warning => warning.StartsWith("Toutes les lignes annoncées", StringComparison.Ordinal))
        && !d.Warnings.Any(warning => warning.StartsWith("Périmètre incomplet", StringComparison.Ordinal)));
    Check(d.Weather.State == "partial" && d.Weather.Title.Contains("relevé à vérifier", StringComparison.Ordinal)
        && !d.Weather.Title.Contains("données partielles", StringComparison.Ordinal));
    var subset = DashboardService.CalculateSubset(s, new(), new[] { 0 }, "Vue synthétique", clock);
    Check(subset.ReceptionComplete && !subset.Complete && subset.ExpectedTotal == 1);
    var export = DashboardService.ToExport(subset);
    Check(!export.Complete && export.Quality == s.Quality && export.Rows.Any(row => row[^1].Contains("réserves du relevé", StringComparison.Ordinal)));
});
Test("Changed final total and inconsistent receipt metadata cannot certify loaded coverage", () =>
{
    var s = Sample(); s.Complete = false;
    s.Quality = new() { SourceRows = 6, ExpectedRows = 6, AllPagesRequested = true, RowCountVerified = true, FinalTotalVerified = false };
    Check(!Calc(s).ReceptionComplete);
    s.Quality.FinalTotalVerified = true; s.Rows.RemoveAt(0);
    Check(!Calc(s).ReceptionComplete);
    s.Rows = Sample().Rows; s.Quality.ExpectedRows = 7;
    Check(!Calc(s).ReceptionComplete);
    s.Quality = null;
    Check(!Calc(s).ReceptionComplete, "Legacy partial records do not infer quality from matching row counts.");
});
Test("Empty snapshot never claims fine weather", () =>
{
    var s = Sample(); s.Rows.Clear(); s.ExpectedTotal = 0; var d = Calc(s); Check(d.Weather.State == "unknown" && d.RowCount == 0);
});
Test("No active tickets explicitly limits clear weather to current search", () =>
{
    var s = Sample(); s.Rows = new() { s.Rows[2], s.Rows[3] }; s.ExpectedTotal = 2; var d = Calc(s);
    Check(d.Weather.Title.Contains("Aucun actif") && d.Weather.Score == null);
});
Test("Missing status leaves activity metrics unavailable, never zero", () =>
{
    var s = Sample(); s.Headers[2] = "X"; var d = Calc(s); Check(d.Metric("active")!.Value == null && d.Metric("overdue")!.Value == null && d.Weather.State == "unknown");
});
Test("Ambiguous role blocks auto-detection; exact override restores it", () =>
{
    var s = Sample(); s.Headers[1] = "Status"; var d = Calc(s); Check(d.Column("status")!.State == "ambiguous" && d.Metric("active")!.Value == null);
    var settings = new DashboardSettings(); settings.ColumnMappings["status"] = "Statut"; d = Calc(s, settings); Equal(d.Metric("active")!.Value, 3, "explicit mapping");
});
Test("Missing configured header never silently switches back to auto", () =>
{
    var settings = new DashboardSettings(); settings.ColumnMappings["status"] = "Statut ancien"; var d = Calc(settings: settings);
    Check(d.Column("status")!.State == "missing-override" && d.Metric("active")!.Value == null);
});
Test("Duplicate exact headers are ambiguous even with an override", () =>
{
    var s = Sample(); s.Headers[1] = "Statut"; var settings = new DashboardSettings(); settings.ColumnMappings["status"] = "Statut";
    Check(Calc(s, settings).Column("status")!.State == "ambiguous");
});
Test("Grouped and zero-padded IDs share identity without deleting rows", () =>
{
    var s = Sample(); s.Rows[0][0] = "0 001"; s.Rows[1][0] = "1"; var d = Calc(s);
    Equal(d.Metric("duplicate")!.Value, 2, "duplicates"); Equal(d.Metric("unique")!.Value, 5, "unique"); Check(d.RowCount == 6 && d.Grain == "lignes");
});
Test("Malformed groups and multiline ID lists are never fused", () =>
{
    foreach (var value in new[] { "12 34", "12\n34", "12\t34", "18446744073709551616" })
    { var s = Sample(); s.Rows[0][0] = value; Equal(Calc(s).Metric("missing-id")!.Value, 1, value); }
});
Test("Observed column labels: urgency, impact, city and solution stay distinct", () =>
{
    var data = new ExtractionResult { Headers = new() { "Titre", "Type", "Urgence", "Statut", "Date d'ouverture", "Demandeur", "Catégorie", "Technicien", "Groupe de techniciens", "Description", "ID", "Description", "Description", "Intervention fournisseur : Intervention", "Ville", "Date de clôture", "Impact", "Priorité", "Date de résolution", "Dernière modification", "Entité", "Dernière modification par", "Solution" }, Complete = true, ExpectedTotal = 1 };
    data.Rows.Add(new[] { "Ticket synthétique", "Incident", "Très haute", "Nouveau", "20/10/2026 12:00", "Personne A", "Catégorie A", "Technicien A", "Groupe A", "Texte A", "6 070 001", "Texte B", "Texte C", "", "Ville A", "", "Haute", "Basse", "", "21/10/2026 12:00", "Entité A", "Personne B", "Solution synthétique" });
    var d = Calc(data); Equal(d.Metric("high")!.Value, 0, "priority remains low"); Equal(d.Metric("high-urgency")!.Value, 1, "urgency"); Equal(d.Metric("high-impact")!.Value, 1, "impact");
    Equal(d.Metric("with-solution")!.Value, 1, "solution"); Check(d.Column("city")!.Header == "Ville" && d.Column("due")!.Index == null);
    Check(d.Column("updatedBy")!.Header == "Dernière modification par" && d.Column("assignee")!.Header == "Technicien");
});
Test("City, location and postcode mapped separately, leading zeros retained", () =>
{
    var data = new ExtractionResult { Headers = new() { "ID", "Statut", "Ville", "Lieu", "Code postal" }, Rows = new() { new[] { "1", "Nouveau", "Ville A", "Site B", "01230" } }, Complete = true, ExpectedTotal = 1 };
    var d = Calc(data); Check(new[] { "city", "location", "postal" }.All(key => d.Column(key)!.Index.HasValue));
    Check(d.Distributions.Single(x => x.Key == "postal").Buckets.Single().Label == "01230");
});
Test("Non-numeric ticket IDs are not guessed from title text", () =>
{
    var s = Sample(); s.Rows[0][0] = "Ticket #100"; var d = Calc(s); Equal(d.Metric("missing-id")!.Value, 1, "missing ID"); Check(d.Grain == "lignes");
});
Test("French and US ambiguous dates obey selected culture", () =>
{
    Check(DashboardService.TryDate("04/05/2026 08:30", "fr-FR", clock.Offset, out var fr) && fr.Month == 5 && fr.Day == 4);
    Check(DashboardService.TryDate("04/05/2026 08:30", "en-US", clock.Offset, out var us) && us.Month == 4 && us.Day == 5);
    Check(!DashboardService.TryDate("21/10/2026", "en-US", clock.Offset, out _));
});
Test("ISO timezone converted to reference clock and plain ISO preserved", () =>
{
    Check(DashboardService.TryDate("2026-10-21T10:00:00Z", "fr-FR", clock.Offset, out var utc) && utc == clock.DateTime);
    Check(DashboardService.TryDate("2026-10-21 12:00:00", "invariant", clock.Offset, out var plain) && plain == clock.DateTime);
});
Test("Relative dates and serial numbers are not silently guessed", () =>
{
    foreach (var raw in new[] { "hier", "il y a 2 jours", "46316", "21/10", "99/99/2026", "0000-00-00" }) Check(!DashboardService.TryDate(raw, "fr-FR", clock.Offset, out _), raw);
});
Test("Future opening and negative resolution duration are excluded", () =>
{
    var s = Sample(); s.Rows[0][5] = "25/10/2026 12:00"; s.Rows[2][7] = "09/10/2026 12:00"; var d = Calc(s);
    Check(d.Metric("age-mean")!.Sample == 2 && d.Metric("resolution-mean")!.Sample == 1);
    Check(d.Warnings.Any(x => x.Contains("futures")) && d.Warnings.Any(x => x.Contains("chronologie")));
});
Test("Multi-assignment cell remains one combined value, counts conserved", () =>
{
    var s = Sample(); s.Rows[0][11] = "Équipe A\nÉquipe B"; var d = Calc(s); var dist = d.Distributions.Single(x => x.Key == "group");
    Check(dist.Buckets.Sum(x => x.Count) == s.Rows.Count && dist.Buckets.Any(x => x.Label == "Équipe A\nÉquipe B" && x.Count == 1));
});
Test("Every distribution drill-down partitions its declared denominator", () =>
{
    var d = Calc(); foreach (var dist in d.Distributions.Where(x => x.Available))
    { Check(dist.Buckets.Sum(x => x.Count) == dist.Denominator, dist.Key); Check(dist.Buckets.SelectMany(x => x.Rows).Distinct().Count() == dist.Denominator, dist.Key); }
});
Test("Every clickable metric points at the correct original row bounds", () =>
{
    var d = Calc(); Check(d.Metrics.SelectMany(x => x.Rows).All(i => i >= 0 && i < d.RowCount));
    Check(d.Attention.All(x => x.Index is 0 or 5) && d.Attention.Count == 2);
});
Test("Configurable thresholds change stale/old/due-soon selection", () =>
{
    var settings = new DashboardSettings { StaleDays = 15, OldDays = 60, DueSoonHours = 12 }; var d = Calc(settings: settings);
    Equal(d.Metric("stale")!.Value, 1, "stale"); Equal(d.Metric("old")!.Value, 0, "old"); Equal(d.Metric("soon")!.Value, 0, "soon");
});
Test("Invalid thresholds rejected before calculation", () =>
{
    foreach (var settings in new[] { new DashboardSettings { StaleDays = 0 }, new DashboardSettings { OldDays = 2 }, new DashboardSettings { DueSoonHours = 0 }, new DashboardSettings { DateCulture = "random" } })
    { bool threw = false; try { Calc(settings: settings); } catch (InvalidOperationException) { threw = true; } Check(threw); }
});
Test("Cancellation respected even on an empty source", () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel(); bool cancelled = false;
    try { DashboardService.Calculate(new(), new(), clock, cts.Token); } catch (OperationCanceledException) { cancelled = true; } Check(cancelled);
});
Test("Rows beyond 10k preview limit included in calculations", () =>
{
    var s = new ExtractionResult { Headers = new() { "ID", "Statut" }, Complete = true, ExpectedTotal = 10001 };
    for (var i = 0; i < 10001; i++) s.Rows.Add(new[] { (i + 1).ToString(), i == 10000 ? "Clos" : "Nouveau" });
    var d = Calc(s); Equal(d.Metric("closed")!.Value, 1, "last row"); Check(d.Metric("closed")!.Rows.Single() == 10000 && d.RowCount == 10001);
});
Test("Bilan export preserves all distributions and scope warnings", () =>
{
    var d = Calc(); var export = DashboardService.ToExport(d);
    Check(export.Rows.Any(r => r[0] == "Correspondances") && export.Rows.Any(r => r[0] == "Météo") && export.Rows.Count > 150);
    Check(export.Warnings.SequenceEqual(d.Warnings) && export.SourceUrl == d.SourceUrl);
    Check(export.Rows.Count(r => r[0] == "Activité datée sur 30 jours") == 90);
    Check(export.Rows.Count == DashboardService.EstimatedExportRows(d), "Preflight count must be exact");
});
Test("Source quality warnings survive dashboard and exported bilan", () =>
{
    var s = Sample(); s.Warnings.Add("Import : limite de lignes atteinte."); var d = Calc(s);
    Check(d.Warnings.Contains(s.Warnings[0]) && DashboardService.ToExport(d).Warnings.Contains(s.Warnings[0]));
});
Test("Bilan Excel is a readable package with values and information sheet", () =>
{
    var path = Path.Combine(Path.GetTempPath(), "GLPI-DashboardChecks-" + Guid.NewGuid().ToString("N") + ".xlsx");
    try
    {
        ExportService.WriteXlsx(DashboardService.ToExport(Calc()), path);
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".xml"))) using (var stream = entry.Open()) XDocument.Load(stream);
        Check(archive.GetEntry("xl/worksheets/sheet2.xml") != null);
    }
    finally { File.Delete(path); }
});

Test("Masculine impact labels are recognized without replacing priority", () =>
{
    var data = new ExtractionResult { Headers = new() { "ID", "Statut", "Priorité", "Urgence", "Impact" }, Complete = true, ExpectedTotal = 5 };
    var impacts = new[] { "Très bas", "Bas", "Moyen", "Haut", "Très haut" };
    for (var i = 0; i < impacts.Length; i++) data.Rows.Add(new[] { (i + 1).ToString(), "Nouveau", "Basse", "Moyenne", impacts[i] });
    var result = Calc(data);
    Equal(result.Metric("high-impact")!.Value, 2, "high impact"); Equal(result.Metric("high-urgency")!.Value, 0, "urgency"); Equal(result.Metric("high")!.Value, 0, "priority");
    Check(result.Distributions.Single(x => x.Key == "impact").Buckets.Any(bucket => bucket.Label == "Moyen" && bucket.Count == 1));
    Check(DashboardService.Severity("Majeure") == null && DashboardService.Severity("6") == null && DashboardService.Priority("Majeure") == 6);
});
Test("Qualified GLPI headers resolve only known ticket and location namespaces", () =>
{
    var headers = new[] { "Ticket - Priorité", "Ticket : Statut", "Tickets > Lieu", "Lieu - Ville", "Location : Code postal", "Demandeur - ID", "Entité - Ville", "Ticket - Attribué à - Technicien" };
    var columns = DashboardService.ResolveColumns(headers, new());
    Check(columns.Single(c => c.Role == "priority").Index == 0 && columns.Single(c => c.Role == "status").Index == 1);
    Check(columns.Single(c => c.Role == "location").Index == 2 && columns.Single(c => c.Role == "city").Index == 3 && columns.Single(c => c.Role == "postal").Index == 4);
    Check(columns.Single(c => c.Role == "id").Index == null && columns.Single(c => c.Role == "assignee").Index == 7);
});
Test("Qualified and simple competing headers remain ambiguous", () =>
{
    var columns = DashboardService.ResolveColumns(new[] { "Priorité", "Ticket - Priorité", "Autre - Impact" }, new());
    Check(columns.Single(c => c.Role == "priority").State == "ambiguous" && columns.Single(c => c.Role == "impact").State == "missing");
});
Test("Importer-renamed duplicate semantic headers require an explicit mapping", () =>
{
    var headers = new[] { "Statut", "Statut (2)", "Priorité (2)" };
    Check(DashboardService.ResolveColumns(headers, new()).Single(c => c.Role == "status").State == "ambiguous");
    Check(DashboardService.ResolveColumns(headers, new()).Single(c => c.Role == "priority").State == "missing");
    Check(DashboardService.ResolveColumns(new[] { "Statut", "Statut (10)" }, new()).Single(c => c.Role == "status").State == "ambiguous");
    var settings = new DashboardSettings(); settings.ColumnMappings["status"] = "Statut (2)";
    Check(DashboardService.ResolveColumns(headers, settings).Single(c => c.Role == "status").Index == 1);
});
Test("Technician roster normalizes case and spacing while preserving name identity", () =>
{
    var data = Sample(); data.Rows[0][10] = "  Sam  "; data.Rows[2][10] = "sAM";
    data.Rows[4][10] = "Alexandre"; data.Rows[5][10] = "Alex";
    var roster = TechnicianDashboardService.Detect(data, new());
    Check(roster.Available && roster.Technicians.Count == 3);
    var sam = roster.Technicians.Single(t => t.Key == "SAM"); Check(sam.Rows.SequenceEqual(new[] { 0, 1, 2 }) && sam.ActiveCount == 2);
    Check(roster.Technicians.Single(t => t.Key == "ALEX").Rows.SequenceEqual(new[] { 3, 5 }));
    Check(TechnicianDashboardService.IdentityKey(" René\u00A0 Dupont ") == "RENÉ DUPONT");
    Check(TechnicianDashboardService.IdentityKey("René") != TechnicianDashboardService.IdentityKey("Rene"));
});
Test("Multiple technician boundaries are explicit and each row belongs once per person", () =>
{
    var names = TechnicianDashboardService.SplitTechnicians("Sam\r\nSam; Alex | Zoé; Aucun\n");
    Check(names.SequenceEqual(new[] { "Sam", "Alex", "Zoé" }));
    Check(TechnicianDashboardService.SplitTechnicians("Martin, Sam").SequenceEqual(new[] { "Martin, Sam" }));
    Check(TechnicianDashboardService.SplitTechnicians("Jean-Paul Dupont").Single() == "Jean-Paul Dupont");
    var data = Sample(); data.Rows[0][10] = "Sam\nAlex\nsAM";
    var roster = TechnicianDashboardService.Detect(data, new());
    Check(roster.Technicians.Single(t => t.Key == "SAM").Rows.Count(i => i == 0) == 1);
    Check(roster.Technicians.Single(t => t.Key == "ALEX").Rows.Contains(0));
    Check(roster.Message.Contains("ne s’additionnent pas"));
});
Test("Personal dashboard recalculates every result and keeps original row indices", () =>
{
    var data = Sample(); data.Rows[3][10] = "Sam"; data.Rows[5][10] = "Sam";
    var result = TechnicianDashboardService.CalculateForTechnician(data, new(), "sam", clock);
    var allowed = new[] { 1, 2, 3, 5 };
    Check(result.IsFiltered && result.RowCount == 4 && result.SourceRowCount == 6 && result.ExpectedTotal == 4 && result.SourceExpectedTotal == 6 && result.Complete);
    Equal(result.Metric("active")!.Value, 2, "personal active"); Equal(result.Metric("high")!.Value, 1, "personal high");
    Check(result.Metric("total")!.Rows.SequenceEqual(allowed));
    Check(result.Metrics.SelectMany(m => m.Rows).All(allowed.Contains));
    Check(result.Distributions.SelectMany(d => d.Buckets).SelectMany(b => b.Rows).All(allowed.Contains));
    Check(result.Timeline.SelectMany(day => day.Created.Concat(day.Resolved).Concat(day.Closed)).All(allowed.Contains));
    Check(result.Weather.Signals.SelectMany(signal => signal.Rows).All(allowed.Contains));
    Check(result.Attention.Count == 1 && result.Attention.Single().Index == 5);
    Check(data.Rows.Count == 6 && data.ExpectedTotal == 6 && data.Scope == "Toutes les pages");
});
Test("Partial source stays partial in personal scope without invented total", () =>
{
    var data = Sample(); data.ExpectedTotal = 100;
    var result = TechnicianDashboardService.CalculateForTechnician(data, new(), "Sam", clock);
    Check(!result.Complete && result.ExpectedTotal == null && result.SourceExpectedTotal == 100 && result.RowCount == 2);
    Check(result.Warnings.Any(warning => warning.Contains("Total annoncé 100")));
});
Test("Absent remembered technician yields an explicit empty view, never the whole team", () =>
{
    var result = TechnicianDashboardService.CalculateForTechnician(Sample(), new(), "Personne absente", clock);
    Check(result.IsFiltered && result.RowCount == 0 && result.ExpectedTotal == 0 && result.Weather.Score == null);
    Check(result.Weather.Title == "Aucun ticket dans cette vue" && result.Warnings.Any(w => w.Contains("n’est pas présent")));
});
Test("Missing technician column cannot become an empty certified personal dashboard", () =>
{
    var data = Sample(); data.Headers[10] = "Autre colonne";
    Check(!TechnicianDashboardService.Detect(data, new()).Available);
    bool rejected = false; try { TechnicianDashboardService.CalculateForTechnician(data, new(), "Sam", clock); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected);
});
Test("Unknown status does not become zero active tickets in technician selector", () =>
{
    var data = Sample(); data.Headers[2] = "Autre";
    Check(TechnicianDashboardService.Detect(data, new()).Technicians.All(entry => entry.ActiveCount == null));
    var personal = TechnicianDashboardService.CalculateForTechnician(data, new(), "Sam", clock);
    Check(personal.Metric("active")!.Value == null && personal.Weather.State == "unknown");
});
Test("Unrecognized-only and mixed technician statuses disclose partial counts", () =>
{
    var data = Sample(); data.Rows[1][2] = "Statut personnalisé"; data.Rows[2][2] = "";
    var roster = TechnicianDashboardService.Detect(data, new());
    var sam = roster.Technicians.Single(entry => entry.Key == "SAM");
    Check(sam.ActiveCount == null && sam.UnknownStatusCount == 2 && roster.Message.Contains("partiels"));
    data.Rows[1][2] = "Nouveau";
    sam = TechnicianDashboardService.Detect(data, new()).Technicians.Single(entry => entry.Key == "SAM");
    Check(sam.ActiveCount == 1 && sam.UnknownStatusCount == 1);
});
Test("Personal bilan records scope and source completeness and has exact preflight count", () =>
{
    var personal = TechnicianDashboardService.CalculateForTechnician(Sample(), new(), "Sam", clock);
    var export = DashboardService.ToExport(personal);
    Check(export.Scope.Contains("Technicien · Sam") && export.Rows.Any(row => row[1] == "Relevé source" && row[2] == "6"));
    Check(export.Rows.Any(row => row[1] == "Lignes du périmètre vérifié" && row[2] == "2"));
    Check(export.Rows.Count == DashboardService.EstimatedExportRows(personal));
});
Test("Subset rejects invalid rows, deduplicates repeated indices and respects cancellation", () =>
{
    var result = DashboardService.CalculateSubset(Sample(), new(), new[] { 5, 1, 5 }, "Test", clock);
    Check(result.Metric("total")!.Rows.SequenceEqual(new[] { 1, 5 }));
    bool rejected = false; try { DashboardService.CalculateSubset(Sample(), new(), new[] { 99 }, "Test", clock); } catch (ArgumentOutOfRangeException) { rejected = true; } Check(rejected);
    using var cts = new CancellationTokenSource(); cts.Cancel(); bool cancelled = false;
    try { TechnicianDashboardService.Detect(Sample(), new(), cts.Token); } catch (OperationCanceledException) { cancelled = true; } Check(cancelled);
});

Dictionary<string, string> ProbeRow(params (string Header, string Value)[] values) => values.ToDictionary(value => value.Header, value => value.Value, StringComparer.Ordinal);
ExtractionResult ProbeData(params Dictionary<string, string>[] values)
{
    var source = new ExtractionResult
    {
        Headers = new() { "ID", "Statut", "Priorité", "Type", "Date d'ouverture", "Dernière modification", "Date de résolution", "Date de clôture", "Échéance", "Technicien", "Groupe attribué", "Catégorie", "Demandeur", "Entité", "Lieu", "Ville", "Code postal", "Source de la demande" },
        Complete = true, ExpectedTotal = values.Length, ExtractedAt = clock, Scope = "Relevé synthétique"
    };
    for (var index = 0; index < values.Length; index++)
    {
        var row = source.Headers.Select(header => values[index].GetValueOrDefault(header, "")).ToArray();
        if (!values[index].ContainsKey("ID")) row[0] = (index + 1).ToString(CultureInfo.InvariantCulture);
        if (!values[index].ContainsKey("Statut")) row[1] = "Nouveau";
        source.Rows.Add(row);
    }
    return source;
}
string DateText(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

Test("Advanced shares disclose their distinct known denominators", () =>
{
    var result = Calc();
    Equal(result.Metric("active-share")!.Value, 60, "active among five recognized statuses");
    Equal(result.Metric("pending-share")!.Value, 100d / 3, "pending among three active");
    Equal(result.Metric("unassigned-share")!.Value, 100d / 3, "unassigned among active");
    Equal(result.Metric("overdue-share")!.Value, 50, "overdue among two usable due dates");
    Equal(result.Metric("high-share")!.Value, 200d / 3, "high among known active priorities");
    Check(result.Metric("active-share")!.Sample == 5 && result.Metric("pending-share")!.Sample == 3 && result.Metric("overdue-share")!.Sample == 2);
    Check(ReferenceEquals(result.Metric("active-share")!.Rows, result.Metric("active")!.Rows), "A ratio needlessly duplicated its drill-down array");
    foreach (var key in new[] { "active", "new", "processing", "pending", "resolved", "closed" }) Check(result.Metric(key)!.Sample == 5, key);
});
Test("Unknown-only statuses never become zero activity or assignment risk", () =>
{
    var source = Sample(); foreach (var row in source.Rows) row[2] = "Statut personnalisé";
    var result = Calc(source);
    foreach (var key in new[] { "active", "new", "pending", "processing", "resolved", "closed", "high", "overdue", "active-share", "without-technician", "without-group", "multi-technician", "active-no-due", "due-today", "new-unassigned", "high-overdue", "active-incidents", "active-requests" })
        Check(result.Metric(key)!.Value == null, key);
    Equal(result.Metric("unknown-status")!.Value, 6, "unrecognized statuses");
    Check(result.Weather.State == "unknown" && result.Distributions.Single(distribution => distribution.Key == "active-type").Available == false);
    var unavailable = DashboardService.ToExport(result).Rows.Single(row => row[0] == "Répartitions" && row[1] == "Incidents et demandes — actifs");
    Check(unavailable[2] == "Non disponible" && unavailable[5].Contains("sans échantillon exploitable", StringComparison.Ordinal));
});
Test("A truly empty known-schema source keeps zero counts and undefined shares", () =>
{
    var result = Calc(ProbeData());
    foreach (var key in new[] { "active", "pending", "without-technician", "multi-technician", "active-no-due", "due-today", "missing-category", "invalid-dates", "inconsistent-dates", "active-incidents" }) Equal(result.Metric(key)!.Value, 0, key);
    foreach (var key in new[] { "active-share", "pending-share", "overdue-share", "inactivity-median", "resolution-closure-mean" }) Check(result.Metric(key)!.Value == null, key);
});
Test("Missing semantic columns keep advanced probes unavailable", () =>
{
    var result = Calc(new ExtractionResult { Headers = new() { "ID" }, Rows = new() { new[] { "1" } }, Complete = true, ExpectedTotal = 1 });
    foreach (var key in new[] { "active-share", "pending-share", "unassigned-share", "overdue-share", "high-share", "without-technician", "without-group", "multi-technician", "active-no-due", "due-today", "new-unassigned", "high-overdue", "high-stale", "missing-category", "missing-requester", "invalid-dates", "future-dates", "inconsistent-dates", "inactivity-mean", "resolution-closure-mean", "active-incidents" })
        Check(result.Metric(key)!.Value == null, key);
    Check(result.Distributions.Where(distribution => distribution.Key.StartsWith("quality-", StringComparison.Ordinal)).All(distribution => !distribution.Available));
});
Test("Technician and group absence remain separate from total lack of assignment", () =>
{
    var result = Calc();
    Check(result.Metric("without-technician")!.Rows.SequenceEqual(new[] { 0, 5 }));
    Check(result.Metric("without-group")!.Rows.SequenceEqual(new[] { 0 }));
    Equal(result.Metric("without-technician-share")!.Value, 200d / 3, "without technician");
    Equal(result.Metric("without-group-share")!.Value, 100d / 3, "without group");
    var distribution = result.Distributions.Single(value => value.Key == "assignment-coverage");
    Check(distribution.Denominator == 3 && distribution.Buckets.Sum(bucket => bucket.Count) == 3);
    Check(distribution.Buckets.Single(bucket => bucket.Label == "Groupe seul").Rows.SequenceEqual(new[] { 5 }));
});
Test("A missing assignment dimension never certifies new unassigned tickets or a total ratio", () =>
{
    var source = Sample(); source.Headers[11] = "Sans correspondance";
    var result = Calc(source);
    Check(result.Metric("unassigned-share")!.Value == null && result.Metric("new-unassigned")!.Value == null && result.Metric("without-group")!.Value == null);
    Equal(result.Metric("without-technician")!.Value, 2, "known technician dimension");
    Check(!result.Distributions.Single(distribution => distribution.Key == "assignment-coverage").Available);
});
Test("Multi-technician probe shares exact conservative identity splitting", () =>
{
    var source = ProbeData(
        ProbeRow(("Technicien", "Sam\n Alex ; sAM | Zoé")),
        ProbeRow(("Technicien", "Martin, Sam")),
        ProbeRow(("Technicien", "Jean Paul Dupont")),
        ProbeRow(("Technicien", "Aucun\nNone;0")),
        ProbeRow(("Technicien", "René;Rene")),
        ProbeRow(("Statut", "Clos"), ("Technicien", "Sam;Alex")));
    var result = Calc(source);
    Check(result.Metric("multi-technician")!.Rows.SequenceEqual(new[] { 0, 4 }));
    Check(result.Metric("without-technician")!.Rows.SequenceEqual(new[] { 3 }));
    Check(result.Metric("new-unassigned")!.Rows.SequenceEqual(new[] { 3 }));
    Check(DashboardService.SplitTechnicianNames(" René\u00a0Dupont ; RENE\u0301 DUPONT ").Count == 1);
});
Test("Missing due dates remain distinct from present but unreadable dates", () =>
{
    var result = Calc(ProbeData(ProbeRow(("Échéance", "")), ProbeRow(("Échéance", "date invalide")), ProbeRow(("Échéance", "2026-10-22 12:00:00"))));
    Check(result.Metric("active-no-due")!.Rows.SequenceEqual(new[] { 0 }));
    Check(result.Metric("invalid-dates")!.Rows.SequenceEqual(new[] { 1 }));
    var quality = result.Distributions.Single(distribution => distribution.Key == "quality-due");
    Check(quality.Buckets.Single(bucket => bucket.Label == "Non renseignée").Rows.SequenceEqual(new[] { 0 }));
    Check(quality.Buckets.Single(bucket => bucket.Label == "Illisible").Rows.SequenceEqual(new[] { 1 }));
    Check(quality.Buckets.Single(bucket => bucket.Label == "Lisible").Rows.SequenceEqual(new[] { 2 }));
    Check(result.Metric("overdue-share")!.Sample == 1 && result.Metric("overdue-share")!.Value == 0);
});
Test("Today and tomorrow use civil-day boundaries while the week excludes past hours", () =>
{
    var dates = new[] { "2026-10-21 00:00:00", "2026-10-21 12:00:00", "2026-10-21 23:59:59", "2026-10-22 00:00:00", "2026-10-23 00:00:00", "2026-10-20 23:59:59", "2026-10-26 00:00:00", "2026-10-25 23:59:59" };
    var result = Calc(ProbeData(dates.Select(date => ProbeRow(("Échéance", date))).ToArray()));
    Check(result.Metric("due-today")!.Rows.SequenceEqual(new[] { 0, 1, 2 }));
    Check(result.Metric("due-tomorrow")!.Rows.SequenceEqual(new[] { 3 }));
    Check(result.Metric("due-week")!.Rows.SequenceEqual(new[] { 1, 2, 3, 4, 7 }));
    Check(result.Metric("overdue")!.Rows.SequenceEqual(new[] { 0, 5 }));
    Check(result.Metric("due-today")!.Explanation.Contains("21/10/2026 12:00:00 +02:00", StringComparison.Ordinal));
});
Test("Sunday's remaining week ends exactly before Monday midnight", () =>
{
    var sunday = new DateTimeOffset(2026, 10, 25, 23, 59, 0, TimeSpan.FromHours(2));
    var source = ProbeData(ProbeRow(("Échéance", "2026-10-25 23:59:00")), ProbeRow(("Échéance", "2026-10-25 23:59:59")), ProbeRow(("Échéance", "2026-10-26 00:00:00")));
    var result = DashboardService.Calculate(source, new(), sunday);
    Check(result.Metric("due-week")!.Rows.SequenceEqual(new[] { 0, 1 }) && result.Metric("due-tomorrow")!.Rows.SequenceEqual(new[] { 2 }));
    var monday = sunday.Date.AddDays(1);
    result = DashboardService.Calculate(source, new(), new DateTimeOffset(monday, sunday.Offset));
    Check(result.Metric("due-week")!.Rows.SequenceEqual(new[] { 2 }) && result.Metric("due-today")!.Rows.SequenceEqual(new[] { 2 }));
});
Test("Zoned timestamps are placed in the calculation offset before civil-day grouping", () =>
{
    var result = Calc(ProbeData(ProbeRow(("Échéance", "2026-10-20T22:00:00Z")), ProbeRow(("Échéance", "2026-10-21T22:00:00Z"))));
    Check(result.Metric("due-today")!.Rows.SequenceEqual(new[] { 0 }) && result.Metric("due-tomorrow")!.Rows.SequenceEqual(new[] { 1 }));
});
Test("High-priority intersections use joint priority and date samples", () =>
{
    var result = Calc();
    Check(result.Metric("high-overdue")!.Rows.SequenceEqual(new[] { 0 }) && result.Metric("high-overdue")!.Sample == 2);
    Check(result.Metric("high-stale")!.Rows.SequenceEqual(new[] { 0, 5 }) && result.Metric("high-stale")!.Sample == 3);
    var source = ProbeData(ProbeRow(("Priorité", "Inconnue"), ("Échéance", "2026-10-20 12:00:00")), ProbeRow(("Priorité", "Haute"), ("Échéance", "invalide")));
    Check(Calc(source).Metric("high-overdue")!.Value == null && Calc(source).Metric("high-overdue")!.Sample == 0);
});
Test("Blank analytic fields disclose missing samples instead of zero risk", () =>
{
    var result = Calc(ProbeData(ProbeRow()));
    foreach (var key in new[] { "overdue-share", "high-share", "due-today", "due-tomorrow", "due-week", "high-overdue", "high-stale", "invalid-dates", "future-dates", "inconsistent-dates", "inactivity-median", "active-incidents", "active-requests" })
        Check(result.Metric(key)!.Value == null, key);
    Equal(result.Metric("active-no-due")!.Value, 1, "known missing due date");
});
Test("Missing category and requester quality counts include unknown-status rows", () =>
{
    var result = Calc();
    Check(result.Metric("missing-category")!.Rows.SequenceEqual(new[] { 4 }));
    Check(result.Metric("missing-requester")!.Rows.SequenceEqual(new[] { 4 }));
    Check(result.Metric("missing-category")!.Sample == 6 && result.Metric("missing-requester")!.Sample == 6);
});
Test("Date-quality probes count affected rows once and keep separate issue classes", () =>
{
    var result = Calc(ProbeData(
        ProbeRow(("Date d'ouverture", "bad"), ("Dernière modification", "also bad")),
        ProbeRow(("Échéance", "bad due")),
        ProbeRow(("Date d'ouverture", "2026-10-22 12:00:00")),
        ProbeRow(("Échéance", "2026-10-22 12:00:00")),
        ProbeRow(("Date d'ouverture", "2026-10-20 12:00:00"), ("Dernière modification", "2026-10-19 12:00:00"), ("Date de résolution", "2026-10-18 12:00:00"), ("Date de clôture", "2026-10-17 12:00:00")),
        ProbeRow(("Date d'ouverture", "2026-10-01 12:00:00"), ("Dernière modification", "2026-10-02 12:00:00"), ("Date de résolution", "2026-10-03 12:00:00"), ("Date de clôture", "2026-10-04 12:00:00"))));
    Check(result.Metric("invalid-dates")!.Rows.SequenceEqual(new[] { 0, 1 }) && result.Metric("invalid-dates")!.Sample == 6);
    Check(result.Metric("future-dates")!.Rows.SequenceEqual(new[] { 2 }) && result.Metric("future-dates")!.Sample == 3);
    Check(result.Metric("inconsistent-dates")!.Rows.SequenceEqual(new[] { 4 }) && result.Metric("inconsistent-dates")!.Sample == 2);
    foreach (var distribution in result.Distributions.Where(distribution => distribution.Key.StartsWith("quality-", StringComparison.Ordinal)))
    {
        Check(distribution.Denominator == 6 && distribution.Buckets.Sum(bucket => bucket.Count) == 6, distribution.Key);
        Check(distribution.Buckets.SelectMany(bucket => bucket.Rows).Distinct().Count() == 6, distribution.Key);
    }
});
Test("A modification before opening is excluded from inactivity statistics", () =>
{
    var result = Calc(ProbeData(ProbeRow(("Date d'ouverture", "2026-10-20 12:00:00"), ("Dernière modification", "2026-10-19 12:00:00"), ("Priorité", "Haute"))));
    Equal(result.Metric("inconsistent-dates")!.Value, 1, "chronology");
    Check(result.Metric("inactivity-median")!.Value == null && result.Metric("inactivity-median")!.Sample == 0);
    Check(result.Metric("stale")!.Value == null && result.Metric("high-stale")!.Value == null);
    Check(result.Distributions.Single(distribution => distribution.Key == "inactivity").Buckets.Single().Label == "Date indisponible");
});
Test("Inactivity durations reuse the valid active sample and deterministic percentiles", () =>
{
    var result = Calc();
    Equal(result.Metric("inactivity-mean")!.Value, 31d / 3, "mean");
    Equal(result.Metric("inactivity-median")!.Value, 11, "median");
    Equal(result.Metric("inactivity-p90")!.Value, 18.2, "P90");
    Equal(result.Metric("inactivity-max")!.Value, 20, "maximum");
    Check(result.Metric("inactivity-median")!.Rows.SequenceEqual(new[] { 0, 1, 5 }));
    Check(ReferenceEquals(result.Metric("inactivity-mean")!.Rows, result.Metric("inactivity-p90")!.Rows), "Duration metrics duplicate the same row array");
    Equal(result.Metric("age-max")!.Value, 50, "existing maximum age retained");
});
Test("Resolution-to-closure duration requires a valid ordered pair rather than a status", () =>
{
    var result = Calc(ProbeData(
        ProbeRow(("Statut", "Clos"), ("Date de résolution", "2026-10-20 12:00:00"), ("Date de clôture", "2026-10-21 06:00:00")),
        ProbeRow(("Statut", "Clos"), ("Date de résolution", "2026-10-20 12:00:00"), ("Date de clôture", "2026-10-20 10:00:00")),
        ProbeRow(("Statut", "Clos")),
        ProbeRow(("Statut", "Clos"), ("Date de résolution", "2026-10-20 12:00:00"), ("Date de clôture", "2026-10-22 12:00:00"))));
    foreach (var suffix in new[] { "mean", "median", "p90", "max" })
    {
        var metric = result.Metric("resolution-closure-" + suffix)!;
        Equal(metric.Value, 18, suffix); Check(metric.Sample == 1 && metric.Rows.SequenceEqual(new[] { 0 }) && metric.Unit == "h");
    }
});
Test("Incident and request probes exclude completed and unknown types without guessing", () =>
{
    var result = Calc(ProbeData(ProbeRow(("Type", "Incident")), ProbeRow(("Type", "2")), ProbeRow(("Type", "Type personnalisé")), ProbeRow(), ProbeRow(("Type", "Incident"), ("Statut", "Clos"))));
    Check(result.Metric("active-incidents")!.Rows.SequenceEqual(new[] { 0 }) && result.Metric("active-incidents")!.Sample == 2);
    Check(result.Metric("active-requests")!.Rows.SequenceEqual(new[] { 1 }) && result.Metric("active-requests")!.Sample == 2);
    var distribution = result.Distributions.Single(value => value.Key == "active-type");
    Check(distribution.Denominator == 4 && distribution.Buckets.Any(bucket => bucket.Label == "Type personnalisé" && bucket.Count == 1));
});
Test("Active entity, location, city, postcode and source distributions preserve their full scope", () =>
{
    var result = Calc(ProbeData(ProbeRow(("Entité", "A"), ("Lieu", "Site 1"), ("Ville", "Ville 1"), ("Code postal", "01230"), ("Source de la demande", "Web")), ProbeRow(("Statut", "Clos"), ("Entité", "B")), ProbeRow(("Statut", "Inconnu"), ("Entité", "C")), ProbeRow()));
    foreach (var role in new[] { "entity", "location", "city", "postal", "source" })
    {
        var distribution = result.Distributions.Single(value => value.Key == "active-" + role);
        Check(distribution.Available && distribution.Denominator == 2 && distribution.Buckets.SelectMany(bucket => bucket.Rows).Order().SequenceEqual(new[] { 0, 3 }), role);
    }
    Check(result.Distributions.Single(value => value.Key == "active-postal").Buckets.Any(bucket => bucket.Label == "01230"));
});
Test("Overdue duration bins have exact hour and day boundaries", () =>
{
    var hours = new[] { .5, 1d, 4d, 24d, 72d, 168d, 720d };
    var source = ProbeData(hours.Select(value => ProbeRow(("Échéance", DateText(clock.DateTime.AddHours(-value))))).ToArray());
    var distribution = Calc(source).Distributions.Single(value => value.Key == "overdue-delay");
    Check(distribution.Denominator == 7 && distribution.Buckets.Count == 7);
    Check(distribution.Buckets.Select(bucket => bucket.Rows.Single()).SequenceEqual(Enumerable.Range(0, 7)));
    Check(distribution.Note.Contains("Calculé au", StringComparison.Ordinal));
});
Test("Upcoming due bins include now and exclude past deadlines", () =>
{
    var hours = new[] { 0d, .5, 1d, 4d, 24d, 72d, 168d, 720d, -.5 };
    var source = ProbeData(hours.Select(value => ProbeRow(("Échéance", DateText(clock.DateTime.AddHours(value))))).ToArray());
    var result = Calc(source); var distribution = result.Distributions.Single(value => value.Key == "due-horizon");
    Check(distribution.Denominator == 8 && distribution.Buckets.Count == 7);
    Check(distribution.Buckets[0].Rows.SequenceEqual(new[] { 0, 1 }));
    Check(distribution.Buckets.SelectMany(bucket => bucket.Rows).Order().SequenceEqual(Enumerable.Range(0, 8)));
    Check(result.Metric("overdue")!.Rows.SequenceEqual(new[] { 8 }));
});
Test("Advanced probes recalculate a partial subset and export its real sample values", () =>
{
    var source = Sample(); source.ExpectedTotal = 999;
    var result = DashboardService.CalculateSubset(source, new(), new[] { 5, 0, 5 }, "Sous-ensemble test", clock);
    Equal(result.Metric("active-share")!.Value, 100, "scoped active"); Equal(result.Metric("unassigned-share")!.Value, 50, "scoped assignment");
    Equal(result.Metric("overdue-share")!.Value, 100, "scoped overdue"); Check(result.Metric("overdue-share")!.Sample == 1);
    Check(!result.Complete && result.SourceRowCount == 6 && result.SourceExpectedTotal == 999 && result.ExpectedTotal == null);
    Check(result.Metrics.SelectMany(metric => metric.Rows).All(index => index is 0 or 5));
    Check(result.Distributions.SelectMany(distribution => distribution.Buckets).SelectMany(bucket => bucket.Rows).All(index => index is 0 or 5));
    var export = DashboardService.ToExport(result);
    var exported = export.Rows.Single(row => row[0] == "Indicateurs" && row[1] == result.Metric("overdue-share")!.Label);
    Check(exported[2] == "100" && exported[3] == "%" && exported[4] == "1");
    Check(export.Rows.Count == DashboardService.EstimatedExportRows(result) && source.Rows.Count == 6);
});
Test("Cancellation requested while enumerating a sparse subset still cancels the calculation", () =>
{
    using var cancellation = new CancellationTokenSource();
    var source = ProbeData(Enumerable.Range(0, 1200).Select(_ => ProbeRow()).ToArray());
    var scope = new CancelledScope(500, cancellation);
    var cancelled = false;
    try { DashboardService.CalculateSubset(source, new(), scope, "Annulation", clock, cancellation.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled);
});

Test("Operational intersections preserve exact active source rows", () =>
{
    var d = Calc();
    foreach (var (key, rows) in new (string, int[])[]
    {
        ("pending-old", Array.Empty<int>()), ("pending-stale", Array.Empty<int>()),
        ("new-stale", new[] { 0 }), ("high-without-technician", new[] { 0, 5 }),
        ("overdue-without-technician", new[] { 0 }), ("group-only", new[] { 5 }),
        ("planned-overdue", Array.Empty<int>()), ("old-without-technician", new[] { 5 }),
        ("active-no-category", Array.Empty<int>()), ("resolved-aging", Array.Empty<int>()),
        ("resolved-no-date", Array.Empty<int>()), ("high-incidents", new[] { 0, 5 })
    })
    {
        Equal(d.Metric(key)!.Value, rows.Length, key);
        Check(d.Metric(key)!.Rows.SequenceEqual(rows), key);
    }
    Equal(d.Metric("resolved-waiting-mean")!.Value, 6, "days since resolution");
    Check(d.Metric("resolved-waiting-mean")!.Rows.SequenceEqual(new[] { 2 }));
});
Test("Pending age and inactivity thresholds use different dates and inclusive boundaries", () =>
{
    var source = Sample(); source.Rows[1][5] = "21/09/2026 12:00"; source.Rows[1][6] = "14/10/2026 12:00";
    var d = Calc(source);
    Check(d.Metric("pending-old")!.Rows.SequenceEqual(new[] { 1 }));
    Check(d.Metric("pending-stale")!.Rows.SequenceEqual(new[] { 1 }));
    var changed = Calc(source, new() { StaleDays = 8, OldDays = 31 });
    Equal(changed.Metric("pending-old")!.Value, 0, "age threshold");
    Equal(changed.Metric("pending-stale")!.Value, 0, "modification threshold");
    Check(d.Metric("pending-old")!.Explanation.Contains("date de passage en attente n’est pas connue", StringComparison.Ordinal));
});
Test("Operational probes remain unavailable when their semantic columns are absent", () =>
{
    var source = new ExtractionResult { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "1", "Ticket" } } };
    var d = Calc(source);
    foreach (var key in new[] { "pending-old", "pending-stale", "new-stale", "high-without-technician", "overdue-without-technician", "group-only", "planned-overdue", "old-without-technician", "active-no-category", "resolved-aging", "resolved-no-date", "high-incidents", "resolved-waiting-mean" })
        Check(d.Metric(key)!.Value is null && d.Metric(key)!.Rows.Length == 0, key);
});
Test("Unreadable pending dates do not create zero-risk observations", () =>
{
    var source = Sample(); source.Rows = new() { source.Rows[1] }; source.Rows[0][5] = "illisible"; source.Rows[0][6] = "22/10/2026 12:00"; source.ExpectedTotal = 1;
    var d = Calc(source);
    Check(d.Metric("pending-old")!.Value is null && d.Metric("pending-stale")!.Value is null);
    Equal(d.Metric("new-stale")!.Value, 0, "no new tickets");
});
Test("Resolution follow-up excludes closed, future and impossible dates and separates absent from invalid", () =>
{
    var source = new ExtractionResult
    {
        Headers = new() { "ID", "Statut", "Date d'ouverture", "Date de résolution" },
        Rows = new()
        {
            new[] { "1", "Résolu", "01/10/2026 12:00", "14/10/2026 12:00" },
            new[] { "2", "Clos", "01/10/2026 12:00", "02/10/2026 12:00" },
            new[] { "3", "Résolu", "01/10/2026 12:00", "22/10/2026 12:00" },
            new[] { "4", "Résolu", "20/10/2026 12:00", "10/10/2026 12:00" },
            new[] { "5", "Résolu", "01/10/2026 12:00", "" },
            new[] { "6", "Résolu", "01/10/2026 12:00", "illisible" }
        }
    };
    var d = Calc(source);
    Check(d.Metric("resolved-aging")!.Rows.SequenceEqual(new[] { 0 }) && d.Metric("resolved-aging")!.Sample == 1);
    Check(d.Metric("resolved-no-date")!.Rows.SequenceEqual(new[] { 4 }) && d.Metric("resolved-no-date")!.Sample == 5);
    Equal(d.Metric("resolved-waiting-median")!.Value, 7, "waiting median");
});
Test("Operational drill-downs remain within the personal source subset", () =>
{
    var d = DashboardService.CalculateSubset(Sample(), new(), new[] { 2, 5 }, "Personnel", clock);
    Check(d.Metric("high-without-technician")!.Rows.SequenceEqual(new[] { 5 }));
    Check(d.Metric("resolved-waiting-max")!.Rows.SequenceEqual(new[] { 2 }));
    Check(d.Metric("overdue-without-technician")!.Value is null, "The scoped active ticket has no readable deadline; it must not become a zero-risk sample.");
});

Test("Common filters intersect populations and preserve original indices", () =>
{
    var baseline = Calc();
    var rows = DashboardService.ResolveScope(baseline, new DashboardScopeFilter[]
    {
        new() { Kind = "metric", SourceKey = "active" },
        new() { Kind = "distribution", SourceKey = "priority", Value = "Haute" }
    });
    Check(rows.SequenceEqual(new[] { 0 }));
    var scoped = baseline.RecalculateScope!(rows, "Ma sélection", CancellationToken.None);
    Check(scoped.RowCount == 1 && scoped.ScopeRows.SequenceEqual(new[] { 0 }) && scoped.Metric("high")!.Rows.SequenceEqual(new[] { 0 }));
});
Test("Filters on personal snapshots cannot escape their initial scope", () =>
{
    var personal = DashboardService.CalculateSubset(Sample(), new(), new[] { 2, 5 }, "Personnel", clock);
    Check(DashboardService.ResolveScope(personal, Array.Empty<DashboardScopeFilter>()).SequenceEqual(new[] { 2, 5 }));
    Check(DashboardService.ResolveScope(personal, new[] { new DashboardScopeFilter { Kind = "metric", SourceKey = "active" } }).SequenceEqual(new[] { 5 }));
});
Test("Date exclusions keep missing and invalid dates unknown", () =>
{
    var baseline = Calc();
    var filter = new DashboardScopeFilter { Kind = "date", SourceKey = "created", Value = "2026-10-01", Value2 = "2026-10-20", Exclude = true };
    Check(DashboardService.ResolveScope(baseline, new[] { filter }).SequenceEqual(new[] { 5 }));
    filter.Exclude = false;
    Check(DashboardService.ResolveScope(baseline, new[] { filter }).SequenceEqual(new[] { 0, 1, 2, 3 }));
});
Test("Unavailable filter sources fail closed instead of showing all tickets", () =>
{
    foreach (var filter in new[]
    {
        new DashboardScopeFilter { Kind = "distribution", SourceKey = "does-not-exist", Value = "Paris" },
        new DashboardScopeFilter { Kind = "metric", SourceKey = "does-not-exist" },
        new DashboardScopeFilter { Kind = "date", SourceKey = "due", Value = "2026-01-01", Value2 = "2026-12-31" }
    })
    {
        var noDates = Calc(new() { Headers = new() { "ID" }, Rows = new() { new[] { "1" } } });
        try { DashboardService.ResolveScope(noDates, new[] { filter }); throw new Exception("Unavailable filter accepted"); }
        catch (InvalidDataException) { }
    }
});
Test("A genuine no-match category has an empty result while excluded populations stay scoped", () =>
{
    Check(DashboardService.ResolveScope(Calc(), new[] { new DashboardScopeFilter { Kind = "distribution", SourceKey = "status", Value = "No match" } }).Length == 0);
    var rows = DashboardService.ResolveScope(Calc(), new[] { new DashboardScopeFilter { Kind = "metric", SourceKey = "active", Exclude = true } });
    Check(rows.SequenceEqual(new[] { 2, 3, 4 }), "Unknown statuses remain outside the known active population");
});
Test("Histogram bins include their lower edge and conserve finite observations", () =>
{
    var series = new DashboardNumericSeries("age", "Âge", "j", new[]
    {
        new DashboardNumericValue(8, 0), new DashboardNumericValue(3, 1), new DashboardNumericValue(7, 7),
        new DashboardNumericValue(12, 30), new DashboardNumericValue(15, double.NaN)
    }, "Test");
    var histogram = DashboardService.Histogram(series, new[] { 7d, 1d, 7d, 30d });
    Check(histogram.Denominator == 4 && histogram.Buckets.Count == 4);
    Check(histogram.Buckets.Select(x => x.Rows.Single()).SequenceEqual(new[] { 8, 3, 7, 12 }));
    Check(DashboardService.Histogram(series, Array.Empty<double>()).Buckets.Single().Count == 4);
});
Test("Histogram rejects invalid boundaries and keeps honest empty bins", () =>
{
    var series = new DashboardNumericSeries("age", "Âge", "j", Array.Empty<DashboardNumericValue>(), "Test");
    Check(DashboardService.Histogram(series, new[] { 1d, 7d }).Buckets.All(x => x.Count == 0));
    try { DashboardService.Histogram(series, new[] { double.PositiveInfinity }); throw new Exception("Invalid bin accepted"); }
    catch (ArgumentException) { }
});
Test("Fractional technician matrices conserve tickets while participation counts names", () =>
{
    var source = Sample(); source.Rows = source.Rows.Take(2).ToList(); source.ExpectedTotal = 2; source.Rows[0][10] = "Sam;Alex";
    var settings = new DashboardSettings { ColumnMappings = new() { ["city"] = "Lieu" }, AssignmentCountingMode = "fractional" };
    var matrix = Calc(source, settings).Matrices.Single(x => x.Key == "technician-city");
    Equal(matrix.Cells.Sum(x => x.Value), 2, "fractional total");
    Equal(matrix.Cells.Single(x => x.RowLabel == "Sam" && x.ColumnLabel == "Site 1").Value, .5, "first participant share");
    Check(matrix.Cells.SelectMany(x => x.Rows).Distinct().Order().SequenceEqual(new[] { 0, 1 }));
    settings.AssignmentCountingMode = "participation";
    Equal(Calc(source, settings).Matrices.Single(x => x.Key == "technician-city").Cells.Sum(x => x.Value), 3, "participation total");
    settings.AssignmentCountingMode = "cell";
    var cells = Calc(source, settings).Matrices.Single(x => x.Key == "technician-city").Cells;
    Check(cells.Count == 2 && cells.Any(x => x.RowLabel == "Sam;Alex"));
});
Test("Unavailable matrix dimensions are never replaced with guessed locations", () =>
{
    var matrix = Calc().Matrices.Single(x => x.Key == "technician-city");
    Check(!matrix.Available && matrix.Cells.Count == 0);
    var scoped = DashboardService.CalculateSubset(Sample(), new() { ColumnMappings = new() { ["city"] = "Lieu" } }, new[] { 5 }, "Un seul ticket", clock);
    Check(scoped.Matrices.SelectMany(x => x.Cells).SelectMany(x => x.Rows).All(x => x == 5));
});
Test("Box plots expose linear quartiles and all contributing original rows", () =>
{
    var source = Sample(); foreach (var row in source.Rows) row[15] = "Marseille";
    var series = Calc(source, new() { ColumnMappings = new() { ["city"] = "Lieu" } }).BoxSeries.Single(x => x.Key == "age-city");
    var box = series.Boxes.Single();
    Check(box.Sample == 3 && box.Rows.SequenceEqual(new[] { 0, 1, 5 }));
    Equal(box.Minimum, 1, "minimum"); Equal(box.FirstQuartile, 10.5, "Q1"); Equal(box.Median, 20, "median");
    Equal(box.ThirdQuartile, 35, "Q3"); Equal(box.P90, 44, "P90"); Equal(box.Maximum, 50, "maximum");
});
Test("Scatter axes use calendar age and signed deadline horizon on valid active pairs", () =>
{
    var scatter = Calc().Scatters.Single(x => x.Key == "age-deadline");
    Check(scatter.Points.Select(x => x.Row).SequenceEqual(new[] { 0, 1 }));
    Equal(scatter.Points[0].X, 20, "age"); Equal(scatter.Points[0].Y, -24, "overdue horizon"); Equal(scatter.Points[1].Y, 24, "future horizon");
});
Test("Local priority matrix is optional and never invents GLPI priority policy", () =>
{
    var source = new ExtractionResult { Headers = new() { "ID", "Statut", "Priorité", "Impact", "Urgence" }, Rows = new() { new[] { "1", "Nouveau", "Haute", "Haut", "Haute" }, new[] { "2", "Nouveau", "Très haute", "Haut", "Haute" } } };
    Check(Calc(source).Metric("priority-rule-mismatch")!.Value == null);
    var metric = Calc(source, new() { PriorityRules = new() { ["4:4"] = 4 } }).Metric("priority-rule-mismatch")!;
    Equal(metric.Value, 1, "mismatch"); Check(metric.Sample == 2 && metric.Rows.SequenceEqual(new[] { 1 }));
});
Test("Weather supports an explicit zero-weight configuration without false sunshine", () =>
{
    var settings = new DashboardSettings { Weather = new() { OverdueWeight = 0, PriorityWeight = 0, AssignmentWeight = 0, StaleWeight = 0, OldWeight = 0 } };
    var weather = Calc(settings: settings).Weather;
    Check(weather.Score == null && weather.State == "unknown" && weather.Signals.Count == 0);
});
Test("Configured weather weights and thresholds are applied to the actual sample", () =>
{
    var source = Sample(); source.Rows = new() { source.Rows[0], source.Rows[1] }; source.ExpectedTotal = 2;
    var settings = new DashboardSettings { Weather = new() { OverdueWeight = 0, PriorityWeight = 100, AssignmentWeight = 0, StaleWeight = 0, OldWeight = 0, WatchThreshold = 60, RainThreshold = 70, StormThreshold = 90 } };
    var weather = Calc(source, settings).Weather;
    Equal(weather.Score, 50, "weighted priority-only score"); Check(weather.State == "sun" && weather.MeasuredWeight == 100 && weather.Signals.Single().Sample == 2);
});
Test("Weather minimum coverage remains explicit when priorities are missing", () =>
{
    var source = Sample(); source.Rows = new() { source.Rows[0], source.Rows[1] }; source.ExpectedTotal = 2; source.Rows[0][3] = "";
    var settings = new DashboardSettings { Weather = new() { OverdueWeight = 0, PriorityWeight = 25, AssignmentWeight = 0, StaleWeight = 0, OldWeight = 0 } };
    Check(Calc(source, settings).Weather.State == "partial");
    settings.Weather.MinimumCoveragePercent = 50;
    Check(Calc(source, settings).Weather.State == "sun");
});
Test("Specialised geography weather stays unavailable without city evidence", () =>
{
    var snapshot = Calc(); Check(snapshot.WeatherProfiles["geography"].State == "unknown");
    snapshot.Metrics.Add(new("city-unmatched", "Sans correspondance", 1, "lignes", "", new[] { 0 }, 6));
    DashboardService.RefreshWeatherProfiles(snapshot, new());
    var profile = snapshot.WeatherProfiles["geography"];
    Equal(profile.Score, 100d / 6, "specialised denominator"); Check(profile.Title.Contains("partiel") && profile.Signals.Single().Sample == 6);
});
Test("Numeric aggregates export actual values and preserve unavailable instead of count fallback", () =>
{
    var snapshot = Calc();
    snapshot.Distributions.Add(new("query-group:test", "Moyennes locales", true, 6, "Une moyenne par groupe, pas une part du total", new[]
    {
        new DashboardBucket("Mesuré", 4, new[] { 0, 1, 2, 3 }, 3.75, "h", 2, true),
        new DashboardBucket("Absent", 2, new[] { 4, 5 }, null, "h", 0, true)
    }));
    var exported = DashboardService.ToExport(snapshot);
    var rows = exported.Rows.Where(x => x[0] == "Moyennes locales").ToArray();
    Check(rows[0][2] == "3.75" && rows[0][3] == "h" && rows[0][4] == "2");
    Check(rows[1][2] == "Non disponible" && rows[1][4] == "0");
    Check(exported.Rows.Count == DashboardService.EstimatedExportRows(snapshot));
});
Test("Expanded export includes analytical sources and never exports incompatible history as a valid value", () =>
{
    var snapshot = Calc();
    snapshot.History.Add(new("total", "Stock observé", "tickets", new[]
    {
        new DashboardHistoryPoint(clock.AddDays(-1), 12, 12, true, true, "a", "Comparable"),
        new DashboardHistoryPoint(clock, 99, 99, false, false, "b", "Relevé incomplet")
    }, "Archives"));
    var exported = DashboardService.ToExport(snapshot);
    Check(exported.Rows.Count == DashboardService.EstimatedExportRows(snapshot));
    Check(exported.Rows.Single(x => x[0] == "Stock observé" && x[1] == clock.ToString("O"))[2] == "Non comparable");
    Check(exported.Rows.Any(x => x[0].Contains("Âge des actifs par") && x[1].Contains("Q1")));
    Check(exported.Rows.Any(x => x[0] == "Âge et échéance des actifs"));
});

Test("Explicit source number culture validates grouping and rejects malformed or competing separators", () =>
{
    Check(DashboardService.TrySourceNumber("1 234,50", new(), out var french) && french == 1234.5);
    Check(DashboardService.TrySourceNumber("2\u202f000.25", new(), out var legacy) && legacy == 2000.25);
    Check(!DashboardService.TrySourceNumber("1 23", new(), out _) && !DashboardService.TrySourceNumber("1,234.5", new(), out _));
    Check(DashboardService.TrySourceNumber("1,234.5", new() { NumberCulture = "en-US" }, out var english) && english == 1234.5);
    Check(!DashboardService.TrySourceNumber("1,23.5", new() { NumberCulture = "en-US" }, out _) && !DashboardService.TrySourceNumber("Infinity", new(), out _));
});
Test("Source timezone converts naive dates and rejects ambiguous or skipped daylight-saving wall times", () =>
{
    var settings = new DashboardSettings { SourceTimeZone = "Europe/Paris" };
    Check(DashboardService.TrySourceDate("2026-07-01 12:00", settings, TimeSpan.Zero, out var utc) && utc.Hour == 10);
    Check(!DashboardService.TrySourceDate("2026-03-29 02:30", settings, TimeSpan.Zero, out _));
    Check(!DashboardService.TrySourceDate("2026-10-25 02:30", settings, TimeSpan.Zero, out _));
    Check(DashboardService.TrySourceDate("2026-10-25T02:30:00+02:00", settings, TimeSpan.Zero, out var explicitOffset) && explicitOffset.Hour == 0);
});
Test("Value dictionaries interpret semantic fields while preserving raw cells and deep copies", () =>
{
    var data = Sample(); data.Rows[0][2] = "READY"; var settings = new DashboardSettings { ValueDictionaries = new() { ["status"] = new() { ["ready"] = "Résolu" } } };
    var snapshot = Calc(data, settings); Equal(snapshot.Metric("resolved")!.Value, 2, "mapped resolved"); Check(data.Rows[0][2] == "READY");
    var copy = settings.Copy(); copy.ValueDictionaries["status"]["ready"] = "Clos"; Check(settings.ValueDictionaries["status"]["ready"] == "Résolu");
});
Test("Duplicate dictionary keys differing only by case recover deterministically without a startup exception", () =>
{
    var settings = new DashboardSettings { ValueDictionaries = new() { ["status"] = new() { ["x"] = "Nouveau", ["X"] = "Clos" }, ["STATUS"] = new() { ["x"] = "Résolu" } } };
    var copy = settings.Copy(); Check(copy.ValueDictionaries.Count == 1); Check(DashboardService.NormalizeValue("X", "status", "Statut", copy) == "Résolu");
});
Test("Exclusive due horizons conserve every active row at overdue and exact boundary edges", () =>
{
    var data = new ExtractionResult { Headers = new() { "ID", "Statut", "Temps de résolution" }, Complete = true, ExpectedTotal = 5 };
    foreach (var (id, hours) in new[] { ("1", -1d), ("2", 0d), ("3", 24d), ("4", 48d) }) data.Rows.Add(new[] { id, "Nouveau", clock.AddHours(hours).ToString("O") });
    data.Rows.Add(new[] { "5", "Nouveau", "unknown" });
    var distribution = Calc(data, new() { DueHorizonHours = new() { 0, 24, 48 } }).Distributions.Single(x => x.Key == "due-exclusive");
    Check(distribution.Available && distribution.Buckets.Sum(x => x.Count) == 5 && distribution.Buckets.SelectMany(x => x.Rows).Distinct().Count() == 5);
    Check(distribution.Buckets[0].Rows.SequenceEqual(new[] { 0 }) && distribution.Buckets[2].Rows.SequenceEqual(new[] { 2 }) && distribution.Buckets[^1].Rows.SequenceEqual(new[] { 4 }));
});
Test("Satisfaction includes valid zero notes and separates snapshot coverage from actual invitations", () =>
{
    var data = new ExtractionResult { Headers = new() { "ID", "Satisfaction", "Enquête envoyée" }, Complete = true,
        Rows = new() { new[] { "1", "0", "oui" }, new[] { "2", "5", "oui" }, new[] { "3", "", "oui" }, new[] { "4", "6", "" } } };
    var d = Calc(data); Equal(d.Metric("satisfaction-score")!.Value, 2.5, "mean"); Equal(d.Metric("satisfaction-responses")!.Value, 2, "responses");
    Equal(d.Metric("satisfaction-coverage")!.Value, 50, "source coverage"); Equal(d.Metric("satisfaction-response-rate")!.Value, 200d / 3, "invited response rate");
    Check(d.Metric("satisfaction-score")!.Rows.SequenceEqual(new[] { 0, 1 }));
});
Test("Absent satisfaction fields never fabricate satisfaction or a zero response rate", () =>
{
    var d = Calc(); Check(d.Metric("satisfaction-score")!.Value == null && d.Metric("satisfaction-responses")!.Value == null && d.Metric("satisfaction-response-rate")!.Value == null);
});
Test("Workload weather describes observed concentration and remains unknown for a single assignee", () =>
{
    var source = new ExtractionResult { Headers = new() { "ID", "Statut", "Attribué à - Technicien" }, Complete = true };
    for (var i = 0; i < 12; i++) source.Rows.Add(new[] { (i + 1).ToString(), "Nouveau", i < 10 ? "Alex" : i == 10 ? "Sam" : "Jo" });
    var d = Calc(source); var weather = d.WeatherProfiles["workload"]; Check(weather.Score > 0 && weather.Signals[0].Rows.Length == 10 && weather.Description.Contains("productivité"));
    source.Rows = source.Rows.Take(2).ToList(); Check(Calc(source).WeatherProfiles["workload"].State == "unknown");
});
Test("Group dimensions and row alignment are deterministic and do not mutate locked widgets", () =>
{
    var board = DashboardLayoutSettings.CreateBoard("Test", "empty");
    board.Widgets.AddRange(new[] { new DashboardWidgetSettings { Id = "before", Span = 3, Height = 220 }, new DashboardWidgetSettings { Id = "a" }, new DashboardWidgetSettings { Id = "locked", Locked = true, Height = 190 }, new DashboardWidgetSettings { Id = "b" }, new DashboardWidgetSettings { Id = "after" } });
    DashboardLayoutEngine.AlignGroup(board, new[] { "a", "b", "locked" }, 6, 400, "Aligned");
    var placements = DashboardLayoutEngine.Arrange(board.Widgets, 1200); var a = placements.Single(x => x.Id == "a"); var b = placements.Single(x => x.Id == "b");
    Check(a.Top == b.Top && a.Height == 400 && b.Height == 400 && a.Column == 0 && b.Column == 6); Check(board.Widgets.Single(x => x.Id == "locked").Height == 190);
    var narrow = DashboardLayoutEngine.Arrange(board.Widgets, 500); Check(narrow.All(x => x.Span == 12));
});
Test("Recent widgets and analytical presentation options survive deep copy and bounded normalization", () =>
{
    var settings = new DashboardLayoutSettings { CatalogRecent = Enumerable.Range(0, 40).Select(i => "metric\n" + i).ToList() }; settings.Normalize(); Check(settings.CatalogRecent.Count == 30);
    var widget = settings.Boards[0].Widgets[0]; widget.StackedMode = "volume"; widget.ParetoThresholdPercent = 63; widget.HistoryPeriodDays = 14; widget.StartNewRow = true;
    settings.Validate(); var copy = settings.Copy(); copy.CatalogRecent.Clear(); Check(settings.CatalogRecent.Count == 30);
    var saved = System.Text.Json.JsonSerializer.Deserialize<DashboardLayoutSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!; saved.Validate();
    Check(saved.Boards[0].Widgets[0].ParetoThresholdPercent == 63 && saved.Boards[0].Widgets[0].StackedMode == "volume" && saved.Boards[0].Widgets[0].StartNewRow);
});

int failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + "\n" + ex); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} vérifications réussies.");
return failures == 0 ? 0 : 1;

sealed class CancelledScope : IReadOnlyList<int>
{
    private readonly CancellationTokenSource _cancellation;
    public int Count { get; }
    public int this[int index] => 2 * index + 1;
    public CancelledScope(int count, CancellationTokenSource cancellation) { Count = count; _cancellation = cancellation; }
    public IEnumerator<int> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            if (index == 256) _cancellation.Cancel();
            yield return this[index];
        }
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
