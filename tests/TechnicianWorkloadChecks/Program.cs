using System.Text.Json;
using GlpiPilot.Core;

var clock = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(2));
var tests = new List<(string Name, Action Run)>();
var temporary = Path.Combine(Path.GetTempPath(), "GLPI-TechnicianWorkloadChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
void Test(string name, Action run) => tests.Add((name, run));
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
void Close(double? actual, double expected) => Check(actual.HasValue && Math.Abs(actual.Value - expected) < .000001, $"Expected {expected}, actual {actual}");
void Throws<T>(Action run) where T : Exception
{
    try { run(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
string TestPath(string file) => Path.Combine(temporary, file);
ExtractionResult Sample() => new()
{
    Headers = new() { "ID", "Titre", "Statut", "Priorité", "Date d'ouverture", "Dernière modification", "Temps de résolution", "Attribué à - Technicien" },
    Rows = new()
    {
        new[] { "100", "Incident Alpha", "Nouveau", "Haute", "01/09/2026 12:00", "01/10/2026 12:00", "20/10/2026 12:00", "Alice" },
        new[] { "101", "Incident partagé", "En attente", "Basse", "20/10/2026 12:00", "21/10/2026 12:00", "22/10/2026 12:00", "Alice; Bob" },
        new[] { "102", "Incident clos", "Clos", "Majeure", "01/09/2026 12:00", "01/10/2026 12:00", "01/10/2026 12:00", "Bob" },
        new[] { "103", "Statut libre", "Personnalisé", "Haute", "01/09/2026 12:00", "01/10/2026 12:00", "01/10/2026 12:00", "Alice" },
        new[] { "104", "Données inconnues", "Nouveau", "", "22/10/2026 12:00", "22/10/2026 12:00", "date invalide", "Charlie" },
        new[] { "105", "Sans technicien", "En cours (attribué)", "Basse", "20/10/2026 12:00", "20/10/2026 12:00", "23/10/2026 12:00", "" },
        new[] { "106", "Nom composé", "Nouveau", "Haute", "01/10/2026 12:00", "01/10/2026 12:00", "", "Dupont, Léa / Support" }
    },
    Complete = true, ExpectedTotal = 7, ExtractedAt = clock, Scope = "Toutes les pages", SourceUrl = "https://glpi.example.invalid/front/ticket.php"
};
TechnicianWorkloadSnapshot Calculate(ExtractionResult? data = null, DashboardSettings? settings = null, IReadOnlyList<int>? rows = null)
    => TechnicianWorkloadService.Calculate(data ?? Sample(), settings ?? new(), rows, clock);
TechnicianWorkloadEntry Entry(TechnicianWorkloadSnapshot result, string name) => result.Technicians.Single(row => row.Key == TechnicianDashboardService.IdentityKey(name));

Test("Counts agree with independently calculated technician dashboards", () =>
{
    var data = Sample(); var workload = Calculate(data);
    foreach (var technician in workload.Technicians)
    {
        var reference = DashboardService.CalculateSubset(data, new(), technician.Rows, technician.Name, clock);
        foreach (var key in new[] { "total", "active", "pending", "high", "overdue", "old", "stale", "soon" })
        {
            var actual = technician.Measure(key); var expected = reference.Metric(key)!;
            Check(actual.Count == expected.Value, technician.Name + " " + key);
            Check(actual.Rows.SequenceEqual(expected.Rows), technician.Name + " source indexes " + key);
        }
    }
});
Test("Shared tickets enter both technicians but team unions only once", () =>
{
    var w = Calculate(); var a = Entry(w, "Alice"); var b = Entry(w, "Bob");
    Check(a.Rows.SequenceEqual(new[] { 0, 1, 3 }) && b.Rows.SequenceEqual(new[] { 1, 2 }));
    Check(w.AssignedRows.Length == 6 && w.Technicians.Sum(entry => entry.Rows.Length) == 7);
    Check(w.MultipleAssignmentRows.SequenceEqual(new[] { 1 }) && a.MultipleAssignmentCount == 1 && b.MultipleAssignmentCount == 1);
    Check(w.AssignedActiveRows.SequenceEqual(new[] { 0, 1, 4, 6 }));
});
Test("Pending, high priority, old, overdue and stale are active-only", () =>
{
    var w = Calculate(); var a = Entry(w, "Alice"); var b = Entry(w, "Bob");
    Check(a.ActiveCount == 2 && a.PendingCount == 1 && a.HighCount == 1 && a.OverdueCount == 1 && a.OldCount == 1 && a.StaleCount == 1);
    Check(b.ActiveCount == 1 && b.HighCount == 0 && b.OverdueCount == 0 && b.OldCount == 0 && b.StaleCount == 0);
    Check(a.UnknownStatusCount == 1 && a.Measure("active").Sample == 2 && a.Measure("active").Population == 3);
});
Test("Empty values in one technician do not borrow another technician's coverage", () =>
{
    var c = Entry(Calculate(), "Charlie");
    foreach (var key in new[] { "high", "overdue", "soon", "old", "stale" })
        Check(c.Measure(key).Count == null && c.Measure(key).Sample == 0 && c.Measure(key).Rows.Length == 0, key);
    Check(c.ActiveCount == 1 && c.MeanAgeDays == null && c.MeanInactivityDays == null);
});
Test("Calendar means retain valid sample sizes and exclude unknown status", () =>
{
    var a = Entry(Calculate(), "Alice");
    Close(a.MeanAgeDays, 25.5); Close(a.MeanInactivityDays, 10);
    Check(a.Measure("old").Sample == 2 && a.Measure("stale").Sample == 2);
});
Test("No technician is a distinct source scope, including all statuses", () =>
{
    var data = Sample(); data.Rows[5][2] = "Clos";
    var w = Calculate(data); Check(w.UnassignedRows.SequenceEqual(new[] { 5 }));
    Check(!w.AssignedRows.Intersect(w.UnassignedRows).Any() && w.AssignedRows.Length + w.UnassignedRows.Length == w.Global.RowCount);
});
Test("Unknown status for an entire technician produces unavailable active measures", () =>
{
    var data = Sample(); data.Rows[4][2] = "Statut personnalisé";
    var c = Entry(Calculate(data), "Charlie");
    Check(c.ActiveCount == null && c.PendingCount == null && c.UnknownStatusCount == 1);
    Check(TechnicianWorkloadService.LoadPercentage(c, 10) == null);
});
Test("Missing status column does not turn unavailable stock into zero", () =>
{
    var data = Sample(); data.Headers[2] = "Champ libre";
    var w = Calculate(data);
    foreach (var entry in w.Technicians)
        Check(entry.ActiveCount == null && entry.UnknownStatusCount == entry.Rows.Length && entry.HighCount == null);
});
Test("Missing and ambiguous technician columns do not fabricate a roster", () =>
{
    var data = Sample(); data.Headers[7] = "Champ libre";
    var absent = Calculate(data); Check(!absent.Roster.Available && absent.Technicians.Count == 0 && absent.UnassignedRows.Length == 0);
    data = Sample(); data.Headers.Add("Technicien");
    var ambiguous = Calculate(data); Check(!ambiguous.Roster.Available && ambiguous.Technicians.Count == 0);
});
Test("Explicit technician mapping resolves a real alternate column", () =>
{
    var data = Sample(); data.Headers[7] = "Responsable local";
    var settings = new DashboardSettings(); settings.ColumnMappings["assignee"] = "Responsable local";
    var w = Calculate(data, settings); Check(w.Roster.Available && Entry(w, "Alice").ActiveCount == 2);
});
Test("Subset calculations preserve original row indexes and de-duplicate the selection", () =>
{
    var w = Calculate(rows: new[] { 6, 1, 6 });
    Check(w.Global.RowCount == 2 && w.Global.SourceRowCount == 7 && w.Global.IsFiltered);
    Check(w.AssignedRows.SequenceEqual(new[] { 1, 6 }) && w.MultipleAssignmentRows.SequenceEqual(new[] { 1 }));
    Check(Entry(w, "Alice").Rows.SequenceEqual(new[] { 1 }) && Entry(w, "Dupont, Léa / Support").Measure("high").Rows.SequenceEqual(new[] { 6 }));
});
Test("Invalid subset indexes fail before publishing a comparison", () =>
{
    Throws<ArgumentOutOfRangeException>(() => Calculate(rows: new[] { -1 }));
    Throws<ArgumentOutOfRangeException>(() => Calculate(rows: new[] { 7 }));
});
Test("Empty extraction and empty subset remain distinct known scopes", () =>
{
    var data = Sample(); data.Rows.Clear(); data.ExpectedTotal = 0;
    var empty = Calculate(data); Check(empty.Roster.Available && empty.Technicians.Count == 0 && empty.Global.Complete && empty.Global.RowCount == 0);
    var subset = Calculate(rows: Array.Empty<int>()); Check(subset.Global.RowCount == 0 && subset.Global.SourceRowCount == 7 && subset.Technicians.Count == 0);
});
Test("Repeated names inside one assignment cell do not duplicate that technician's row", () =>
{
    var data = Sample(); data.Rows[1][7] = "Alice; Alice\nALICE | Bob; bob";
    var w = Calculate(data); Check(Entry(w, "Alice").Rows.Length == 3 && Entry(w, "Bob").Rows.Length == 2 && w.MultipleAssignmentRows.Length == 1);
});
Test("Conservative actor parsing preserves accents, commas and slashes", () =>
{
    var names = TechnicianDashboardService.SplitTechnicians(" Élodie ; E\u0301LODIE\nElodie | Dupont, Léa / Support; Aucun");
    Check(names.Count == 3 && names[0] == "Élodie" && names[1] == "Elodie" && names[2] == "Dupont, Léa / Support");
    Check(names.SequenceEqual(DashboardService.SplitTechnicianNames(" Élodie ; E\u0301LODIE\nElodie | Dupont, Léa / Support; Aucun")));
});
Test("Duplicate ticket IDs stay as occurrences without inflating unique-ticket claims", () =>
{
    var data = Sample(); data.Rows[1][0] = data.Rows[0][0];
    var w = Calculate(data); Check(w.Global.Grain == "lignes" && w.Global.Metric("duplicate")!.Value == 2);
    Check(w.AssignedRows.Length == 6 && Entry(w, "Alice").Rows.Length == 3);
});
Test("Partial extraction carries its real source coverage into management", () =>
{
    var data = Sample(); data.Complete = false; data.ExpectedTotal = 100;
    var w = Calculate(data); Check(!w.Global.Complete && w.Global.ExpectedTotal == 100 && w.Global.RowCount == 7 && w.Global.Warnings.Count > 0);
});
Test("Inconsistent update before opening is excluded from inactivity coverage", () =>
{
    var data = Sample(); data.Rows[1][5] = "01/10/2026 12:00";
    var w = Calculate(data); var b = Entry(w, "Bob");
    Check(b.StaleCount == null && b.Measure("stale").Sample == 0 && b.MeanInactivityDays == null);
    var reference = DashboardService.CalculateSubset(data, new(), b.Rows, "Bob", clock);
    Check(reference.Metric("stale")!.Value == b.StaleCount);
});
Test("Threshold changes affect shared canonical metrics and sample counts", () =>
{
    var settings = new DashboardSettings { StaleDays = 1, OldDays = 2, DueSoonHours = 48 };
    var w = Calculate(settings: settings); var a = Entry(w, "Alice");
    Check(a.OldCount == 1 && a.StaleCount == 1 && a.Measure("soon").Count == 1 && a.Measure("old").Label.Contains("2"));
});
Test("Known roster dashboard keeps source indexes for a selected technician", () =>
{
    var data = Sample(); var w = Calculate(data);
    var dashboard = TechnicianDashboardService.CalculateForKnownTechnician(data, new(), w.Roster, "Bob", clock);
    Check(dashboard.RowCount == 2 && dashboard.Metric("total")!.Rows.SequenceEqual(new[] { 1, 2 }));
    var absent = TechnicianDashboardService.CalculateForKnownTechnician(data, new(), w.Roster, "Absent", clock);
    Check(absent.RowCount == 0 && absent.Weather.State == "unknown");
});
Test("Load percentage always uses an explicit local target and may exceed 100 percent", () =>
{
    var a = Entry(Calculate(), "Alice");
    Check(TechnicianWorkloadService.LoadPercentage(a, null) == null);
    Close(TechnicianWorkloadService.LoadPercentage(a, 4), 50);
    Close(TechnicianWorkloadService.LoadPercentage(a, 1), 200);
    Throws<ArgumentOutOfRangeException>(() => TechnicianWorkloadService.LoadPercentage(a, 0));
    Throws<ArgumentOutOfRangeException>(() => TechnicianWorkloadService.LoadPercentage(a, TechnicianPreferences.MaximumActiveTarget + 1));
});
Test("Pre-cancelled calculation publishes nothing", () =>
{
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    Throws<OperationCanceledException>(() => TechnicianWorkloadService.Calculate(Sample(), new(), referenceTime: clock, ct: cancel.Token));
});
Test("Cancellation interrupts a larger calculation", () =>
{
    var data = Sample(); var template = data.Rows[0];
    data.Rows = Enumerable.Range(0, 20000).Select(index =>
    { var row = template.ToArray(); row[0] = (index + 1).ToString(); return row; }).ToList();
    data.ExpectedTotal = data.Rows.Count;
    using var cancel = new CancellationTokenSource(); cancel.CancelAfter(TimeSpan.FromMilliseconds(1));
    Throws<OperationCanceledException>(() => TechnicianWorkloadService.Calculate(data, new(), referenceTime: clock, ct: cancel.Token));
});
Test("Large roster counts assignment intersections and source unions consistently", () =>
{
    var data = Sample(); var template = data.Rows[0];
    data.Rows = Enumerable.Range(0, 1200).Select(index =>
    {
        var row = template.ToArray(); row[0] = (index + 1).ToString();
        row[7] = $"Tech {index:D4}; Tech {(index + 1) % 1200:D4}"; return row;
    }).ToList();
    data.ExpectedTotal = data.Rows.Count;
    var w = Calculate(data); Check(w.Technicians.Count == 1200 && w.AssignedRows.Length == 1200 && w.MultipleAssignmentRows.Length == 1200);
    Check(w.Technicians.All(entry => entry.Rows.Length == 2 && entry.ActiveCount == 2 && entry.OverdueCount == 2));
});
Test("Missing local preferences return defaults without writing a file", () =>
{
    var path = TestPath("missing.json"); var prefs = TechnicianPreferences.Load(path);
    Check(prefs.Technicians.Count == 0 && !File.Exists(path) && prefs.Get("Alice").ActiveTarget == null);
});
Test("Local favorites, objectives and notes round-trip atomically", () =>
{
    var path = TestPath("roundtrip.json");
    var prefs = new TechnicianPreferences().With(" alice ", new() { Favorite = true, ActiveTarget = 12, Notes = "Repère synthétique\r\nÀ relire" });
    prefs.Save(path); var loaded = TechnicianPreferences.Load(path); var a = loaded.Get("ALICE");
    Check(a.Favorite && a.ActiveTarget == 12 && a.Notes == "Repère synthétique\nÀ relire");
    Check(loaded.Technicians.ContainsKey("ALICE") && Directory.GetFiles(temporary, "*.tmp").Length == 0);
});
Test("Preference copies and changes cannot mutate an applied previous state", () =>
{
    var original = new TechnicianPreferences().With("Alice", new() { Favorite = true, ActiveTarget = 10 });
    var update = original.With("Alice", new() { ActiveTarget = 4 });
    var copy = original.Copy(); copy.Technicians["ALICE"].Notes = "Copie";
    var item = original.Get("Alice"); item.ActiveTarget = 99;
    Check(original.Get("Alice").ActiveTarget == 10 && original.Get("Alice").Favorite && original.Get("Alice").Notes.Length == 0);
    Check(update.Get("Alice").ActiveTarget == 4 && !update.Get("Alice").Favorite);
});
Test("Clearing all local aids removes the saved technician preference", () =>
{
    var prefs = new TechnicianPreferences().With("Alice", new() { Favorite = true });
    Check(prefs.With("Alice", new()).Technicians.Count == 0 && prefs.Technicians.Count == 1);
});
Test("Objective bounds reject zero, negatives and excessive values", () =>
{
    var prefs = new TechnicianPreferences();
    Check(prefs.With("Alice", new() { ActiveTarget = 1 }).Get("Alice").ActiveTarget == 1);
    Check(prefs.With("Alice", new() { ActiveTarget = TechnicianPreferences.MaximumActiveTarget }).Get("Alice").ActiveTarget == TechnicianPreferences.MaximumActiveTarget);
    foreach (var target in new[] { 0, -1, TechnicianPreferences.MaximumActiveTarget + 1 })
        Throws<InvalidDataException>(() => prefs.With("Alice", new() { ActiveTarget = target }));
});
Test("Notes and identities have explicit text and length bounds", () =>
{
    var prefs = new TechnicianPreferences();
    Check(prefs.With("Élodie", new() { Notes = new string('x', TechnicianPreferences.MaximumNotesLength) }).Get("Élodie").Notes.Length == TechnicianPreferences.MaximumNotesLength);
    Throws<InvalidDataException>(() => prefs.With("Alice", new() { Notes = new string('x', TechnicianPreferences.MaximumNotesLength + 1) }));
    Throws<InvalidDataException>(() => prefs.With("Alice", new() { Notes = "bad\0text" }));
    Throws<InvalidDataException>(() => prefs.With(" ", new() { Favorite = true }));
    Throws<InvalidDataException>(() => prefs.With(new string('x', TechnicianPreferences.MaximumIdentityLength + 1), new()));
    Throws<InvalidDataException>(() => prefs.With("Alice\nBob", new()));
});
Test("Malformed, future-version and duplicate-normalized preference documents fail safely", () =>
{
    var path = TestPath("invalid.json");
    File.WriteAllText(path, "{"); Throws<JsonException>(() => TechnicianPreferences.Load(path));
    File.WriteAllText(path, "{\"Version\":99,\"Technicians\":{}}"); Throws<InvalidDataException>(() => TechnicianPreferences.Load(path));
    File.WriteAllText(path, "{\"Version\":1,\"Technicians\":{\"Alice\":{\"Favorite\":true},\" ALICE \":{\"Favorite\":false}}}");
    Throws<InvalidDataException>(() => TechnicianPreferences.Load(path));
    File.WriteAllText(path, "{\"Version\":1,\"Technicians\":{\"Alice\":{\"ActiveTarget\":0}}}");
    Throws<InvalidDataException>(() => TechnicianPreferences.Load(path));
});
Test("Preference document size and identity count are bounded", () =>
{
    var path = TestPath("too-large.json"); File.WriteAllBytes(path, new byte[TechnicianPreferences.MaximumFileBytes + 1]);
    Throws<InvalidDataException>(() => TechnicianPreferences.Load(path));
    var prefs = new TechnicianPreferences
    {
        Technicians = Enumerable.Range(0, TechnicianPreferences.MaximumTechnicians + 1)
            .ToDictionary(index => "TECH " + index, _ => new TechnicianPreference { Favorite = true })
    };
    Throws<InvalidDataException>(() => prefs.Save(TestPath("too-many.json")));
});
Test("Failed atomic write leaves the previous applied preferences and file intact", () =>
{
    var path = TestPath("previous.json"); var applied = new TechnicianPreferences().With("Alice", new() { ActiveTarget = 10 });
    applied.Save(path); var before = File.ReadAllBytes(path);
    var destinationDirectory = TestPath("directory-instead-of-file"); Directory.CreateDirectory(destinationDirectory);
    var candidate = applied.With("Alice", new() { ActiveTarget = 20 });
    var rejected = false;
    try { candidate.Save(destinationDirectory); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        // Windows reports an existing directory as access denied; Unix reports an I/O error.
        rejected = true;
    }
    Check(rejected && Directory.Exists(destinationDirectory));
    Check(applied.Get("Alice").ActiveTarget == 10 && File.ReadAllBytes(path).SequenceEqual(before));
    Check(Directory.GetFiles(temporary, "*.tmp").Length == 0);
});

var failures = 0;
try
{
    foreach (var test in tests)
    {
        try { test.Run(); Console.WriteLine("PASS " + test.Name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex.GetType().Name + " · " + ex.Message); }
    }
    Console.WriteLine($"{tests.Count - failures}/{tests.Count} checks passed.");
}
finally { Directory.Delete(temporary, recursive: true); }
return failures == 0 ? 0 : 1;
