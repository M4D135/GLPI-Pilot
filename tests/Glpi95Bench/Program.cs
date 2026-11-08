using System.Diagnostics;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

// Benchmark of the ticket collection against a throwaway GLPI 9.5 with fictitious tickets.
//   collect <url> <tickets> <data dir> <report.json> : production controller, pause after 10 tickets, resume, then an unchanged pass.
//   changed <url> <tickets> <changed> <data dir> <report.json> : production controller after <changed> tickets were modified in GLPI.
//   volume <url> <tickets> <contents> <report.json> : collection engine alone, whole index plus a sample of ticket contents.
if (args.Length < 5) { Console.Error.WriteLine("Usage : collect|changed|volume …"); return 2; }
var phase = args[0]; var baseUrl = args[1].TrimEnd('/'); var tickets = int.Parse(args[2]);
var report = new Dictionary<string, object?> { ["phase"] = phase, ["tickets"] = tickets };
var failures = new List<string>();
void Check(bool ok, string message) { Console.WriteLine((ok ? "PASS " : "FAIL ") + message); if (!ok) failures.Add(message); }
var reportPath = args[^1];

using var playwright = await Playwright.CreateAsync();
var browserPath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
await using var browser = await playwright.Chromium.LaunchAsync(new()
{ ExecutablePath = string.IsNullOrWhiteSpace(browserPath) ? null : browserPath, Headless = true, Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
var context = await browser.NewContextAsync(new() { Locale = "fr-FR", ViewportSize = new() { Width = 1440, Height = 1000 } });
var page = await context.NewPageAsync();
await page.GotoAsync(baseUrl + "/index.php");
await page.FillAsync("#login_name", "glpi"); await page.FillAsync("#login_password", "glpi");
await Task.WhenAll(page.WaitForURLAsync(url => !url.EndsWith("/index.php", StringComparison.Ordinal)), page.ClickAsync("[name=submit]"));
var source = baseUrl + "/front/ticket.php";
await page.GotoAsync(source);
var service = new BrowserService(); service.AttachForTesting(context, source);
// Each opening of a ticket page in GLPI is counted, to prove which tickets a pass really re-reads.
var opened = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
context.Request += (_, request) =>
{
    if (request.ResourceType != "document" || !request.Url.Contains("/front/ticket.form.php", StringComparison.Ordinal)) return;
    var id = TicketReferenceResolver.ParseQuery(new Uri(request.Url)).FirstOrDefault(pair => pair.Key == "id").Value ?? "";
    opened.AddOrUpdate(id, 1, (_, count) => count + 1);
};
var settings = new AppSettings { PortalUrl = source, ListTimeoutSeconds = 60, DetailTimeoutSeconds = 60, MaxPages = 10000, MaxRows = 1000000 };
await service.FindBestPageAsync(settings, page, CancellationToken.None);

if (phase == "volume")
{
    var contents = int.Parse(args[3]);
    var clock = Stopwatch.StartNew();
    var index = await service.ReadTicketLibraryIndexAsync(settings, null, CancellationToken.None, "dmy");
    report["indexSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1); report["pages"] = index.PagesRead; report["indexed"] = index.Tickets.Count;
    report["secondsPerPage"] = index.PagesRead > 0 ? Math.Round(clock.Elapsed.TotalSeconds / index.PagesRead, 2) : null;
    Check(index.AllStatuses && index.Complete && index.Limitations.Count == 0 && index.Tickets.Count == tickets && index.ExpectedTotal == tickets,
        $"Index complet de {index.Tickets.Count}/{tickets} tickets sur {index.PagesRead} pages en {clock.Elapsed.TotalSeconds:N0} s {string.Join(" | ", index.Limitations)}");
    Check(index.Tickets.Select(t => t.Ticket.Id).Distinct().Count() == index.Tickets.Count && index.Tickets.All(t => t.LastModifiedReliable),
        "Identités uniques et dates de modification fiables sur tout l’index.");
    var sample = index.Tickets.Where((_, position) => position % Math.Max(1, index.Tickets.Count / contents) == 0).Take(contents).ToList();
    var detailClock = Stopwatch.StartNew(); var complete = 0; var problems = new List<string>(); var durations = new List<double>();
    foreach (var seed in sample)
    {
        var one = Stopwatch.StartNew();
        try { var read = await service.ReadTicketLibraryDetailsAsync(seed.Ticket, settings, null, CancellationToken.None); if (read.Coverage.Complete) complete++; else problems.Add($"#{seed.Ticket.Id} : {string.Join(" | ", read.Coverage.Limitations)}"); }
        catch (Exception error) { problems.Add($"#{seed.Ticket.Id} : {error.GetType().Name} · {error.Message}"); }
        durations.Add(one.Elapsed.TotalSeconds);
    }
    durations.Sort();
    report["contentsRead"] = sample.Count; report["contentsComplete"] = complete; report["contentSeconds"] = Math.Round(detailClock.Elapsed.TotalSeconds, 1);
    report["secondsPerTicketMedian"] = durations.Count > 0 ? Math.Round(durations[durations.Count / 2], 2) : null;
    report["secondsPerTicketP95"] = durations.Count > 0 ? Math.Round(durations[(int)Math.Min(durations.Count - 1, Math.Ceiling(durations.Count * 0.95) - 1)], 2) : null;
    report["contentProblems"] = problems.Take(20).ToList();
    Check(complete == sample.Count, $"Contenu complet pour {complete}/{sample.Count} tickets. {string.Join(" ; ", problems.Take(3))}");
}
else
{
    // Isolated test profile: the application only accepts a temporary GLPI-Pilot-Checks-* folder, kept between the phases.
    var dataRoot = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-" + args[phase == "changed" ? 4 : 3]); Directory.CreateDirectory(dataRoot);
    Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1"); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", dataRoot);
    var pauseAfter = phase == "collect" ? 10 : 0; var paused = false; PilotApplication? app = null; var progress = new List<string>();
    app = new PilotApplication(new NullHost(), (name, data) =>
    {
        if (name != "library.collection.progress") return;
        var value = JsonSerializer.SerializeToElement(data, PilotApplication.Json);
        if (progress.Count < 400) progress.Add(value.GetProperty("phase").GetString() ?? "");
        if (pauseAfter == 0 || paused || value.GetProperty("state").GetString() != "running" || value.GetProperty("processed").GetInt32() != pauseAfter) return;
        paused = true;
        _ = app!.ExecuteAsync("library.collection.control", JsonSerializer.SerializeToElement(new { action = "pause", sessionId = value.GetProperty("sessionId").GetString() })).GetAwaiter().GetResult();
    }, settings, service);
    await using var owned = app!;
    async Task<JsonElement> Call(string operation, object values) => JsonSerializer.SerializeToElement(await owned.ExecuteAsync(operation, JsonSerializer.SerializeToElement(values, PilotApplication.Json)), PilotApplication.Json);
    // Production values except the pacing, set to its allowed minimum so the benchmark fits in the CI budget.
    var current = await Call("library.collection.get", new { });
    var options = current.GetProperty("state").GetProperty("options").Deserialize<LibraryCollectionOptions>(PilotApplication.Json)!;
    options.DelayMilliseconds = 1000; options.BatchPauseSeconds = 0; options.MaximumMinutes = 240;
    await Call("library.collection.settings", new { revision = current.GetProperty("state").GetProperty("revision").GetInt64(), options });
    // GLPI date settings as an administrator sets them: the throwaway server writes naive dates in UTC, day first.
    var library = (await Call("library.get", new { })).GetProperty("settings");
    await Call("library.settings.save", new { settings = new { localFolder = library.GetProperty("localFolder").GetString(), syncMode = "local", autoSync = false,
        intervalMinutes = 5, sourceTimeZone = "UTC", sourceDateOrder = "dmy", maxAgeDays = 7, displayName = "Banc" } });
    object Summary(JsonElement result, double seconds) => new
    {
        state = result.GetProperty("collectionState").GetString(), observed = result.GetProperty("observed").GetInt32(), read = result.GetProperty("read").GetInt32(),
        reused = result.GetProperty("reused").GetInt32(), retry = result.GetProperty("retryCount").GetInt32(), scope = result.GetProperty("finalScopeVerified").GetBoolean(), seconds = Math.Round(seconds, 1),
        limitations = result.GetProperty("limitations").EnumerateArray().Select(e => e.GetString()).ToList()
    };
    if (phase == "collect")
    {
        var clock = Stopwatch.StartNew();
        var first = await Call("library.capture", new { mode = "incremental" }); var firstSeconds = clock.Elapsed.TotalSeconds;
        report["firstPass"] = Summary(first, firstSeconds);
        Check(first.GetProperty("collectionState").GetString() == "paused" && first.GetProperty("read").GetInt32() == pauseAfter, $"La pause demandée après {pauseAfter} tickets est respectée ({first.GetProperty("read").GetInt32()} lus).");
        clock.Restart();
        var resumed = await Call("library.capture", new { mode = "resume", runId = first.GetProperty("runId").GetString() }); var resumeSeconds = clock.Elapsed.TotalSeconds;
        report["resume"] = Summary(resumed, resumeSeconds);
        Check(resumed.GetProperty("collectionState").GetString() == "completed" && resumed.GetProperty("retryCount").GetInt32() == 0 && resumed.GetProperty("finalScopeVerified").GetBoolean(),
            $"La reprise termine la collecte, périmètre final confirmé, sans fiche à reprendre ({resumeSeconds:N0} s).");
        Check(resumed.GetProperty("observed").GetInt32() == tickets && opened.Count == tickets && opened.Values.All(count => count == 1),
            $"Les {tickets} tickets sont observés et chaque fiche est ouverte une seule fois malgré la pause ({opened.Count} fiches, {opened.Values.Sum()} ouvertures).");
        opened.Clear();
        clock.Restart();
        var unchanged = await Call("library.capture", new { mode = "incremental" }); var unchangedSeconds = clock.Elapsed.TotalSeconds;
        report["unchangedPass"] = Summary(unchanged, unchangedSeconds);
        Check(unchanged.GetProperty("read").GetInt32() == 0 && unchanged.GetProperty("reused").GetInt32() == tickets && unchanged.GetProperty("collectionState").GetString() == "completed" && opened.IsEmpty,
            $"Un passage sans changement n’ouvre aucune fiche ({unchanged.GetProperty("reused").GetInt32()} réutilisés, {opened.Count} ouvertures, {unchangedSeconds:N0} s).");
    }
    else
    {
        var changed = int.Parse(args[3]);
        var clock = Stopwatch.StartNew();
        var after = await Call("library.capture", new { mode = "incremental" }); var seconds = clock.Elapsed.TotalSeconds;
        report["changedPass"] = Summary(after, seconds);
        Check(after.GetProperty("read").GetInt32() == changed && after.GetProperty("reused").GetInt32() == tickets - changed && after.GetProperty("collectionState").GetString() == "completed" && opened.Count == changed,
            $"Seuls les {changed} tickets modifiés dans GLPI sont relus ({after.GetProperty("read").GetInt32()} lus, {after.GetProperty("reused").GetInt32()} réutilisés, {seconds:N0} s).");
        // The library search ranks any matching word; « banc » only appears in the follow-up added to the changed tickets.
        var found = await Call("library.search", new { text = "banc", limit = 100 });
        Check(found.GetProperty("total").GetInt64() == changed, $"Le nouveau suivi des {changed} tickets modifiés est dans la bibliothèque ({found.GetProperty("total").GetInt64()} trouvés).");
    }
    report["phases"] = progress.Distinct().Take(40).ToList();
}
report["failures"] = failures;
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine(failures.Count == 0 ? "Banc réussi." : $"{failures.Count} vérification(s) en échec.");
return failures.Count == 0 ? 0 : 1;

sealed class NullHost : IPilotHost
{
    public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult<string?>(null);
    public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task SetClipboardAsync(string text) => Task.CompletedTask;
    public Task OpenExternalAsync(string url) => Task.CompletedTask;
    public Task CloseAsync() => Task.CompletedTask;
    public Task<byte[]> CapturePngAsync() => Task.FromResult(Array.Empty<byte>());
    public Task PrintPdfAsync(string path) => Task.CompletedTask;
    public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
}
