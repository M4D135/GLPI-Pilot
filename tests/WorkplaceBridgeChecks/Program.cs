using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

var tests = WorkOperationChecks.Create();
tests.AddRange(WorkBridgeChecks.Create());
var browserPath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
if (!string.IsNullOrWhiteSpace(browserPath)) tests.AddRange(await WorkBrowserChecks.Create(browserPath));
else Console.WriteLine("Browser/WebView checks omitted: set GLPI_TEST_CHROMIUM for the complete suite.");
int failures = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(90)); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + test.Name + "\n" + error); }
}
await WorkBrowserChecks.DisposeAsync();
Console.WriteLine($"{tests.Count - failures}/{tests.Count} Workplace bridge and WebView checks passed.");
return failures == 0 ? 0 : 1;

public sealed class WorkFixture : IAsyncDisposable
{
    public const string ListUrl = "http://workplace-glpi.test/glpi/front/ticket.php?criteria=synthetic";
    public const string TicketUrl = "http://workplace-glpi.test/glpi/front/ticket.form.php?id=42";
    public static readonly DateTimeOffset Observed = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Workplace-" + Guid.NewGuid().ToString("N"));
    public WorkplaceTestHost Host { get; } = new();
    public PilotApplication App { get; }
    public ExtractionResult Source { get; } = SourceData();
    public List<(string Name, object? Data)> Events { get; } = new();
    public WorkFixture(BrowserService? browser = null)
    {
        Directory.CreateDirectory(Folder); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1"); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", Folder);
        var settings = new AppSettings { PortalUrl = ListUrl, TimeoutSeconds = 5, DetailTimeoutSeconds = 5, TicketDetails = new() { AutoLoadOnSelection = false }, Dashboard = new() { ColumnMappings = new() { ["id"] = "ID", ["title"] = "Titre", ["status"] = "Statut", ["priority"] = "Priorité", ["category"] = "Catégorie", ["assignee"] = "Technicien", ["due"] = "Échéance", ["location"] = "Lieu" } } };
        App = new(Host, (name, data) => Events.Add((name, data)), settings, browser, new SnapshotStore(Path.Combine(Folder, "history"))); App.LoadFixture(Source);
    }
    public static ExtractionResult SourceData() => new()
    {
        Headers = new() { "ID", "Titre", "Statut", "Priorité", "Catégorie", "Technicien", "Échéance", "Lieu" },
        Rows = new() { new[] { "42", "Réseau du bâtiment Nord", "Nouveau", "Haute", "Réseau", "Alice", "2026-10-21T12:00:00", "Nord" }, new[] { "43", "Réseau du bâtiment Nord", "En attente", "Normale", "Réseau", "Alice; Bob", "2026-10-26T12:00:00", "Nord" }, new[] { "44", "Terminal réparé", "Clos", "Basse", "Matériel", "Bob", "", "Sud" }, new[] { "45", "État personnalisé", "À examiner", "Normale", "Matériel", "", "", "Sud" } },
        SourceUrl = ListUrl, ExtractedAt = Observed, Complete = true, ExpectedTotal = 4,
        Provenance = new() { RequestedUrl = ListUrl, SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "synthetic-entity", SourceKind = "glpi" }
    };
    public static TicketDetailResult Detail() => new()
    {
        Ticket = new("42", TicketUrl), Title = "Réseau du bâtiment Nord", ReadAt = Observed,
        Sections = new()
        {
            new() { Name = "Fiche", Kind = "fields", TabKey = "overview", Data = new() { Headers = new() { "Champ", "Valeur" }, Rows = new() { new[] { "Statut", "Nouveau" }, new[] { "Titre", "Réseau du bâtiment Nord" } }, Complete = true, ExtractedAt = Observed } },
            new() { Name = "Traitement", Kind = "timeline", TabKey = "treatment", Data = new() { Headers = new() { "Type", "Date", "Auteur", "Contenu" }, Rows = new() { new[] { "Suivi", "21/10/2026 10:00", "Alice", "Réponse visible" }, new[] { "Tâche", "20/10/2026 09:00", "Bob", "Préparer" }, new[] { "Suivi", "Date libre", "Alice", "Observation non datée" } }, Complete = true, ExtractedAt = Observed } }
        }, Tabs = new() { new() { Key = "overview", Name = "Fiche", Category = "overview", Loaded = true }, new() { Key = "treatment", Name = "Traitement", Category = "treatment", Loaded = true } },
        ObservedItems = new()
        {
            new() { Kind = "task", Identity = "1", Title = "Tâche planifiée", Status = "À faire", Due = "2026-10-21T09:00:00", Owner = "Alice", ObservedAt = Observed },
            new() { Kind = "task", Identity = "2", Title = "Date inconnue", Status = "À faire", ObservedAt = Observed },
            new() { Kind = "followup", Identity = "3", Title = "Demande de précision", Author = "Auteur non qualifié", Date = "21/10/2026 10:00", ObservedAt = Observed },
            new() { Kind = "relation", Identity = "43", Title = "Ticket lié", RelatedType = "ticket", Url = TicketUrl.Replace("42", "43"), ObservedAt = Observed },
            new() { Kind = "relation", Identity = "99", Title = "Lien étranger", RelatedType = "problem", Url = "https://evil.invalid/front/problem.form.php?id=99", ObservedAt = Observed },
            new() { Kind = "document", Identity = "7", Title = "document.pdf", Url = "http://workplace-glpi.test/glpi/front/document.send.php?docid=7&tickets_id=42", ObservedAt = Observed }
        }
    };
    public static TechnicianPreference Preference() => new() { DailyCapacityMinutes = 240, EstimatedTicketMinutes = 30, Skills = new() { "Réseau" }, PrimaryTerritories = new() { "Nord" }, WorkPeriods = new() { new() { Day = DayOfWeek.Monday, StartMinute = 480, EndMinute = 720, CapacityMinutes = 180 } }, CapacityDays = new() { new() { Date = new(2026, 10, 26), MorningMinutes = 90, AfternoonMinutes = 0 } }, CategoryEstimates = new() { new() { Category = "Réseau", MinimumMinutes = 20, Minutes = 40, MaximumMinutes = 80 } } };
    public async Task<JsonElement> Call(string operation, object args)
    { var result = await App.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args, PilotApplication.Json)); return JsonSerializer.SerializeToElement(result, PilotApplication.Json); }
    public Task<JsonElement> Open(int index) => Call("workplace.ticket.open", new { rowIndex = index, revision = App.Revision });
    public async Task<string> Key() => (await Open(0)).GetProperty("personal").GetProperty("key").GetString()!;
    public async ValueTask DisposeAsync() { await App.DisposeAsync(); try { Directory.Delete(Folder, true); } catch (IOException) { } }
}

public static class WorkAssert
{
    public static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    public static async Task Refuse(Func<Task> action, string expected)
    {
        try { await action(); } catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { Check(error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase), "Unexpected refusal: " + error.Message); return; }
        throw new InvalidOperationException("Expected refusal: " + expected);
    }
    public static string S(JsonElement element, string key) => element.GetProperty(key).GetString() ?? "";
    public static int I(JsonElement element, string key) => element.GetProperty(key).GetInt32();
}
