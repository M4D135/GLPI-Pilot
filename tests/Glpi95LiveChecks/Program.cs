using System.Diagnostics;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Playwright;

// Live check of the ticket library engine against a throwaway GLPI with fictitious data (GLPI 9.5 by default).
// Arguments: base URL, expected ticket count, number of tickets whose content is read, output report path.
if (args.Length < 4) { Console.Error.WriteLine("Usage : <url> <tickets attendus> <fiches à lire> <rapport.json>"); return 2; }
var baseUrl = args[0].TrimEnd('/'); var expected = int.Parse(args[1]); var detailCount = int.Parse(args[2]); var reportPath = args[3];
var browserPath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
var failures = new List<string>(); var report = new Dictionary<string, object?>();
void Check(bool ok, string message) { Console.WriteLine((ok ? "PASS " : "FAIL ") + message); if (!ok) failures.Add(message); }

using var playwright = await Playwright.CreateAsync();
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
var settings = new AppSettings { PortalUrl = source, ListTimeoutSeconds = 30, DetailTimeoutSeconds = 30, MaxPages = 10000, MaxRows = 1000000 };
await service.FindBestPageAsync(settings, page, CancellationToken.None);

var clock = Stopwatch.StartNew();
TicketLibraryIndex? index = null;
try { index = await service.ReadTicketLibraryIndexAsync(settings, null, CancellationToken.None, "dmy"); }
catch (Exception error) { Check(false, "Index lisible : " + error.GetType().Name + " · " + error.Message); }
report["indexSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
if (index is not null)
{
    report["indexed"] = index.Tickets.Count; report["expectedTotal"] = index.ExpectedTotal; report["pages"] = index.PagesRead; report["limitations"] = index.Limitations;
    Check(index.AllStatuses, "Le filtre Statut = Tous est confirmé.");
    Check(index.Complete && index.Limitations.Count == 0, $"Index complet sans limite ({index.Tickets.Count}/{index.ExpectedTotal}, {index.PagesRead} pages) {string.Join(" | ", index.Limitations)}");
    Check(index.Tickets.Count == expected && index.ExpectedTotal == expected, $"Les {expected} tickets fictifs sont indexés.");
    Check(index.Tickets.All(t => t.LastModifiedReliable), "Chaque date de dernière modification est lue de façon fiable (ordre jour/mois/année).");
    Check(index.Tickets.Select(t => t.Status).Distinct().Count() >= 5, "Les statuts ouverts, résolus et clos sont tous présents.");
    var targets = index.Tickets.Where(t => t.Status is "Résolu" or "Clos").Take(Math.Max(1, detailCount / 2))
        .Concat(index.Tickets.Where(t => t.Status is not ("Résolu" or "Clos")).Take(Math.Max(1, detailCount - detailCount / 2))).ToList();
    var complete = 0; var withFollowup = 0; var withSolution = 0; var details = Stopwatch.StartNew(); var problems = new List<string>();
    foreach (var seed in targets)
    {
        try
        {
            var read = await service.ReadTicketLibraryDetailsAsync(seed.Ticket, settings, null, CancellationToken.None);
            var text = JsonSerializer.Serialize(read.Detail);
            if (read.Coverage.Complete) complete++; else problems.Add($"#{seed.Ticket.Id} : {string.Join(" | ", read.Coverage.Limitations)}");
            if (text.Contains("Suivi fictif", StringComparison.Ordinal)) withFollowup++;
            if (text.Contains("Solution fictive", StringComparison.Ordinal)) withSolution++;
        }
        catch (Exception error) { problems.Add($"#{seed.Ticket.Id} : {error.GetType().Name} · {error.Message}"); }
    }
    report["detailsRead"] = targets.Count; report["detailsComplete"] = complete; report["detailSeconds"] = Math.Round(details.Elapsed.TotalSeconds, 1);
    report["detailProblems"] = problems.Take(20).ToList();
    Check(complete == targets.Count, $"Contenu complet pour {complete}/{targets.Count} fiches. {string.Join(" ; ", problems.Take(3))}");
    Check(withFollowup > 0, $"Les suivis fictifs sont recueillis ({withFollowup} fiches).");
    Check(withSolution > 0, $"Les solutions fictives des tickets résolus ou clos sont recueillies ({withSolution} fiches).");
}
report["failures"] = failures; report["totalSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(failures.Count == 0 ? "Toutes les vérifications GLPI réelles ont réussi." : $"{failures.Count} vérification(s) en échec.");
return failures.Count == 0 ? 0 : 1;
