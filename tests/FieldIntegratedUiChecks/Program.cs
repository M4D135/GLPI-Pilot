using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

var repository = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(repository, "web", "index.html"))) repository = Directory.GetParent(repository)?.FullName ?? throw new DirectoryNotFoundException();
var web = Path.Combine(repository, "web"); var evidence = Path.Combine(repository, "artifacts", "field-integrated"); Directory.CreateDirectory(evidence);
var passed = 0;
void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS: " + label); }
await using var f = new CalendarFixture();
var entry = CalendarFixture.Entry(await f.Save(f.Event("Intervention terrain fictive", participants: new[] { "agent-fictif" })));
var catalogueReply = await f.Call("field.catalogue.get");
var catalogue = catalogueReply.GetProperty("catalogue").Deserialize<FieldMaterialCatalogue>(PilotApplication.Json)!;
catalogue.References.Add(new("integration-cable", catalogue.Categories[0].Id, "FICTIF", "Câble fictif intégré", "metre", "Référence fictive de qualification"));
await f.Call("field.catalogue.save", new { catalogue, expectedRevision = catalogueReply.GetProperty("revision").GetInt64() });
var occurrence = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences")[0].GetProperty("key").GetString();
var opened = await f.Call("field.open", new { occurrenceKey = occurrence, revision = f.App.Revision, from = "2026-11-02", to = "2026-11-09", timeZoneId = "Europe/Paris" });
var initial = opened.GetProperty("document").Deserialize<FieldPreparationDocument>(PilotApplication.Json)!;
await f.Call("field.save", new { token = opened.GetProperty("token").GetString(), plan = initial.Plan });
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM") });
await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
var errors = new ConcurrentQueue<string>(); page.PageError += (_, error) => errors.Enqueue(error);
await context.RouteAsync("**/*", async route =>
{
    var uri = new Uri(route.Request.Url);
    if (uri.Host != "glpi-pilot.local") { errors.Enqueue("Unexpected network request: " + uri.Host); await route.AbortAsync(); return; }
    var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
    if (relative is "" or "index.html")
    {
        var html = (await File.ReadAllTextAsync(Path.Combine(web, "index.html"))).Replace("src=\"js/app.js\"", "src=\"/tests/FieldIntegratedUiChecks/fixture.js\"", StringComparison.Ordinal)
            .Replace("</head>", "<link rel=\"stylesheet\" href=\"/tests/FieldPreparationUiChecks/fixture.css\"></head>", StringComparison.Ordinal);
        await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = html }); return;
    }
    var path = Path.GetFullPath(Path.Combine(repository, relative.StartsWith("tests/", StringComparison.Ordinal) ? relative : "web/" + relative));
    if (!path.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { errors.Enqueue("Missing asset: " + relative); await route.FulfillAsync(new() { Status = 404 }); return; }
    var mime = Path.GetExtension(path) switch { ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
    await route.FulfillAsync(new() { ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
});
Task delivery = Task.CompletedTask; var gate = new object();
void Post(string json)
{
    lock (gate) delivery = delivery.ContinueWith(async _ => { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
}
await using var bridge = new WebBridge(f.App.Operations, (operation, arguments, ct) => f.App.ExecuteAsync(operation, arguments, ct), Post);
await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
await page.AddInitScriptAsync("""
    (() => { const listeners = new Set(); window.chrome ||= {}; window.chrome.webview = {
      postMessage: message => window.__pilotSend(JSON.stringify(message)),
      addEventListener: (name, handler) => { if (name === 'message') listeners.add(handler); },
      removeEventListener: (name, handler) => listeners.delete(handler)
    }; window.__pilotDeliver = message => { for (const listener of listeners) listener({ data: message }); }; })();
    """);
try
{
    await page.GotoAsync("https://glpi-pilot.local/index.html"); await page.Locator("[data-field-plan]").First.WaitForAsync();
    Check(await page.Locator("[data-field-plan]").CountAsync() == 1, "Real C# bridge loads the existing SQLite preparation into the actual interface.");
    await page.Locator("[data-field-open]").First.ClickAsync();
    await page.GetByLabel("Référence à ajouter", new() { Exact = true }).SelectOptionAsync("integration-cable");
    await page.GetByLabel("Quantité à ajouter", new() { Exact = true }).FillAsync("0.125");
    await page.Locator("[data-field-action='add-line']").ClickAsync();
    await page.GetByLabel("Consignes de préparation", new() { Exact = true }).FillAsync("Consigne fictive conservée dans SQLite");
    await page.Locator("[data-field-action='save-plan']").ClickAsync();
    await page.Locator("dialog.field-modal").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    var saved = (await f.App.CalendarStoreFixture.GetAsync(initial.Plan.Id))!;
    var material = FieldPreparationDocuments.ReadPlan(saved);
    Check(material.Plan.Lines.Single().Quantity == 0.125m && material.Plan.PreparationNote == "Consigne fictive conservée dans SQLite", "Editing a real form writes exact fractional quantities and notes through WebBridge into SQLite.");
    var reopened = new TeamWorkspaceStore(f.App.CalendarStoreFixture.DatabasePath);
    Check(FieldPreparationDocuments.ReadPlan((await reopened.GetAsync(initial.Plan.Id))!).Plan.Lines[0].Quantity == 0.125m, "Another store instance reopens the actual browser-saved material.");
    await page.Locator("[data-field-action='catalogue']").ClickAsync();
    await page.GetByLabel("Rechercher une référence du catalogue", new() { Exact = true }).FillAsync("FICTIF");
    await page.GetByLabel("Libellé de référence : Câble fictif intégré", new() { Exact = true }).FillAsync("Câble fictif renommé");
    await page.Locator("[data-field-action='save-catalogue']").ClickAsync();
    await page.Locator("dialog.field-modal").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    var updatedCatalogue = (await f.Call("field.catalogue.get")).GetProperty("catalogue").Deserialize<FieldMaterialCatalogue>(PilotApplication.Json)!;
    Check(updatedCatalogue.References.Single(reference => reference.Id == "integration-cable").Label == "Câble fictif renommé"
        && FieldPreparationDocuments.ReadPlan((await reopened.GetAsync(initial.Plan.Id))!).Plan.Lines[0].Label == "Câble fictif intégré", "Catalogue edits persist without rewriting the label recorded on an existing preparation line.");

    // Seed a received concurrent document through the actual synchronization apply path.
    saved = (await f.App.CalendarStoreFixture.GetAsync(initial.Plan.Id))!;
    var current = saved.Copy(); current.CentralRevision = 1; current.Pending = false;
    var incomingDocument = FieldPreparationDocuments.ReadPlan(saved); incomingDocument.Plan.PreparationNote = "Version reçue fictive choisie dans le GUI";
    var incoming = FieldPreparationDocuments.ForPlan(incomingDocument, "Autre agent fictif");
    await f.App.CalendarStoreFixture.ApplyChangeAsync(new(1, current, incoming, "Fixture de réception concurrente"), CancellationToken.None);
    await page.GetByRole(AriaRole.Button, new() { Name = "Actualiser", Exact = true }).ClickAsync();
    await page.GetByRole(AriaRole.Button, new() { Name = "Voir les versions", Exact = true }).ClickAsync();
    await page.GetByRole(AriaRole.Button, new() { Name = "Choisir la version reçue", Exact = true }).First.ClickAsync();
    await page.Locator("dialog.modal").GetByRole(AriaRole.Button, new() { Name = "Confirmer le choix", Exact = true }).ClickAsync();
    await page.Locator("dialog.field-modal").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    var resolved = (await reopened.GetAsync(initial.Plan.Id))!;
    Check(resolved.Conflicts == 0 && FieldPreparationDocuments.ReadPlan(resolved).Plan.PreparationNote == incomingDocument.Plan.PreparationNote,
        "An explicit GUI comparison and confirmation resolves the actual stored conflict, not a simulated response.");

    await page.Locator("[data-field-action='select-all']").ClickAsync();
    await page.Locator("[data-field-action='preview']").ClickAsync(); await page.Locator(".field-report").WaitForAsync();
    Check((await page.Locator(".field-report").InnerTextAsync()).Contains("Câble fictif intégré", StringComparison.Ordinal), "The real report engine returns the selected persisted material snapshot to the preview.");
    f.Host.SavePath = Path.Combine(f.Root, "actual-field-export.html");
    await page.Locator("dialog.field-modal").GetByRole(AriaRole.Button, new() { Name = "Exporter en HTML", Exact = true }).ClickAsync();
    // Le bouton n'est plus desactive pendant l'ecriture : attendre le fichier ecrit par l'hote, non un effet de bord du clic.
    var exported = "";
    var exportDeadline = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < exportDeadline)
    {
        try { if (File.Exists(f.Host.SavePath)) exported = await File.ReadAllTextAsync(f.Host.SavePath); } catch (IOException) { exported = ""; }
        if (exported.Contains("0,125", StringComparison.Ordinal)) break;
        await Task.Delay(100);
    }
    await Assertions.Expect(page.Locator("dialog.field-modal").GetByRole(AriaRole.Button, new() { Name = "Exporter en HTML", Exact = true })).ToBeEnabledAsync();
    Check(exported.Contains("0,125", StringComparison.Ordinal), "The preview exports an actual HTML file via the host-approved path and real C# writer.");
    await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "actual-preview.png"), FullPage = true });
    await page.Locator(".field-modal-heading").GetByRole(AriaRole.Button, new() { Name = "Fermer le panneau", Exact = true }).ClickAsync();

    entry.State = "cancelled"; await f.Save(entry, entry.Revision);
    await page.Locator(".field-advanced-filters > summary").ClickAsync();
    await page.GetByLabel("Inclure les fiches terminées ou annulées", new() { Exact = true }).CheckAsync();
    await page.GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync();
    await page.Locator(".field-plan-card").First.GetByText("Annulée", new() { Exact = true }).WaitForAsync();
    Check((await page.Locator(".field-plan-card").First.InnerTextAsync()).Contains("Annulée", StringComparison.Ordinal)
        && (await f.Call("field.get")).GetProperty("summary").GetProperty("planCount").GetInt32() == 0,
        "Cancelling the real appointment updates the visible sheet and removes its departure quantities.");
    await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "actual-cancelled.png"), FullPage = true });
    Check(errors.IsEmpty, "No JavaScript exception, missing asset or unexpected network request occurred: " + string.Join("; ", errors));
    Console.WriteLine($"{passed}/{passed} integrated field workflow checks passed."); return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error + "\n" + string.Join("\n", errors));
    await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "failure.png"), FullPage = true });
    Console.Error.WriteLine(await page.Locator("body").InnerTextAsync()); return 1;
}
finally { await delivery; }
