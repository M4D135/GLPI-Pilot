using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckCalendarInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1440, Height = 980 }, Locale = "fr-FR", TimezoneId = "Europe/Paris", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var requests = new ConcurrentQueue<string>(); PilotApplication? application = null;
        page.PageError += (_, error) => errors.Enqueue(error);
        await context.RouteAsync("**/*", async route =>
        {
            if (EmbeddedMapsService.IsTileUrl(route.Request.Url) && application?.AllowsMapTileRequest(route.Request.Url) == true)
            { await route.FulfillAsync(new() { ContentType = "image/png", BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=") }); return; }
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404 }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page }; WebBridge? bridge = null; Task delivery = Task.CompletedTask; var deliveryGate = new object();
        void Post(string json)
        {
            lock (deliveryGate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json=>window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), new AppSettings { Theme = "light", ExportFolder = Path.Combine(_root, "Exports") }); application = app;
        await Call(app, "calendar.resource.save", new { resource = new CalendarResource { Key = "calendar-alex", Label = "Alex Martin", TimeZoneId = "UTC" }, expectedRevision = 0 });
        await Call(app, "calendar.resource.save", new { resource = new CalendarResource { Key = "calendar-samir", Label = "Samir Laurent", TimeZoneId = "UTC" }, expectedRevision = 0 });
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            requests.Enqueue(op);
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            return await app.ExecuteAsync(op, data, ct);
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (()=>{const listeners=new Set();window.chrome||={};window.chrome.webview={postMessage:message=>window.__pilotSend(JSON.stringify(message)),
              addEventListener:(name,handler)=>{if(name==='message')listeners.add(handler);},removeEventListener:(name,handler)=>listeners.delete(handler)};
              window.__pilotDeliver=message=>{for(const listener of listeners)listener({data:message});};})();
            """);
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        ILocator Editor() => page.Locator(".calendar-editor-body");
        async Task Settled() => await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending&&!document.querySelector('.interventions-workspace')?.hasAttribute('aria-busy')");
        async Task Date(string value) { await page.GetByLabel("Date du planning", new() { Exact = true }).FillAsync(value); await page.GetByLabel("Date du planning", new() { Exact = true }).DispatchEventAsync("change"); await Settled(); }
        async Task View(string value) { await page.GetByLabel("Présentation du calendrier", new() { Exact = true }).SelectOptionAsync(value); await Settled(); }
        async Task<JsonElement> Current() => await Call(app, "calendar.get", new { from = "2026-11-02", to = "2026-11-16", timeZoneId = "UTC" });
        async Task Capture(string name)
        {
            var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return;
            Directory.CreateDirectory(args[index + 1]); await Settled();
            await page.Locator("#notifications .toast button").EvaluateAllAsync<int>("buttons=>{buttons.forEach(button=>button.click());return buttons.length;}");
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[index + 1], name + ".png") });
        }
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(page);
        await page.Locator(".nav-item[data-page=interventions]").ClickAsync(); await page.Locator(".interventions-workspace[data-ready=true]").WaitForAsync(); await Date("2026-11-02");
        Require(await page.Locator(".calendar-tabs .calendar-tab").CountAsync() == 3 && app.Result is null,
            "The common Planning / Map / Tours workspace opens without a loaded extraction.");
        await Button("Nouvelle intervention").ClickAsync(); await Editor().WaitForAsync();
        await page.GetByLabel("Objet", new() { Exact = true }).FillAsync("Préparation libre <b>sans ticket</b>");
        await page.GetByLabel("Début", new() { Exact = true }).FillAsync("2026-11-02T09:00");
        await page.GetByLabel("Fin", new() { Exact = true }).FillAsync("2026-11-02T10:30");
        await page.GetByLabel("Participant Alex Martin", new() { Exact = true }).CheckAsync();
        await page.GetByLabel("Participant Samir Laurent", new() { Exact = true }).CheckAsync();
        Require(await page.Locator("dialog[open]").CountAsync() == 0 && await page.Locator(".calendar-side").IsVisibleAsync(),
            "Intervention editing uses an integrated side panel without hiding the calendar in a modal.");
        await page.Locator("[data-calendar-action=save]").ClickAsync(); await Editor().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Settled();
        var data = await Current(); var item = data.GetProperty("occurrences")[0]; var id = item.GetProperty("interventionId").GetString()!;
        Require(data.GetProperty("summary").GetProperty("interventions").GetInt32() == 1 && data.GetProperty("summary").GetProperty("personHours").GetDouble() == 3
            && item.GetProperty("tickets").GetArrayLength() == 0 && await page.Locator(".calendar-event b").CountAsync() == 0,
            "A free 90-minute intervention with two technicians counts once, books three person-hours and treats source text as text.");
        await View("resources");
        Require(await page.Locator(".calendar-resource-cell .calendar-event").CountAsync() == 2 && (await page.Locator(".calendar-metric").First.InnerTextAsync()).Contains("1", StringComparison.Ordinal),
            "Resource-week projects one common reservation in each participant lane without duplicating its metric.");
        await Capture("calendar-resources-light");
        await View("month"); Require(await page.Locator(".calendar-month-day").CountAsync() == 42, "The month view shows six complete weeks and includes multi-day reservations with exclusive ends.");
        await View("agenda"); Require(await page.Locator(".calendar-agenda-row").CountAsync() == 1, "Agenda and grid use the same occurrence set.");
        await View("day");
        var firstCard = page.Locator(".calendar-day-column .calendar-event").First;
        await firstCard.DragToAsync(page.Locator("[data-slot='2026-11-02T11:00']")); await Settled();
        await WaitForPageAsync(page, "()=>!!document.querySelector('[data-calendar-action=undo]')");
        var moved = await Call(app, "calendar.item", new { id });
        Require(moved.GetProperty("intervention").GetProperty("startLocal").GetString() == "2026-11-02T11:00", "Dragging to a time slot persists an actual intervention move through the runtime.");
        await page.Locator("[data-calendar-action=undo]").ClickAsync(); await Settled();
        Require((await Call(app, "calendar.item", new { id })).GetProperty("intervention").GetProperty("startLocal").GetString() == "2026-11-02T09:00",
            "Undo restores the saved reservation using the guarded runtime mutation token.");
        await Button("Nouvelle intervention").ClickAsync(); await page.GetByLabel("Objet", new() { Exact = true }).FillAsync("Chevauchement à confirmer");
        await page.GetByLabel("Début", new() { Exact = true }).FillAsync("2026-11-02T10:00"); await page.GetByLabel("Fin", new() { Exact = true }).FillAsync("2026-11-02T11:00");
        await page.GetByLabel("Participant Alex Martin", new() { Exact = true }).CheckAsync(); await page.Locator("[data-calendar-action=save]").ClickAsync(); await Settled();
        Require(await page.Locator("[data-calendar-action=override]").IsVisibleAsync() && (await Current()).GetProperty("summary").GetProperty("interventions").GetInt32() == 1,
            "A collision remains a reviewed proposal until the user explicitly accepts the constraint.");
        await page.Locator("[data-calendar-action=override]").ClickAsync(); await Editor().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Settled();
        Require((await Current()).GetProperty("summary").GetProperty("interventions").GetInt32() == 2 && await page.Locator(".calendar-event.has-conflict").CountAsync() == 2,
            "An accepted overlap stays visible on both affected interventions.");
        await page.Locator(".calendar-event").Filter(new() { HasText = "Chevauchement" }).First.ClickAsync();
        await page.GetByLabel("Début", new() { Exact = true }).FillAsync("2026-11-02T13:00"); await page.GetByLabel("Fin", new() { Exact = true }).FillAsync("2026-11-02T14:00");
        await page.Locator("[data-calendar-action=save]").ClickAsync(); await Editor().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Settled();
        await Button("Nouvelle intervention").ClickAsync(); await page.GetByLabel("Objet", new() { Exact = true }).FillAsync("Maintenance récurrente");
        await page.GetByLabel("Début", new() { Exact = true }).FillAsync("2026-11-03T15:00"); await page.GetByLabel("Fin", new() { Exact = true }).FillAsync("2026-11-03T16:00");
        await page.GetByLabel("Répéter", new() { Exact = true }).SelectOptionAsync("daily"); await page.GetByLabel("Nombre maximal", new() { Exact = true }).FillAsync("3");
        await page.Locator("[data-calendar-action=save]").ClickAsync(); await Editor().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Settled(); await View("week");
        Require(await page.Locator(".calendar-event").Filter(new() { HasText = "Maintenance récurrente" }).CountAsync() == 3,
            "Recurring creation renders the actual three engine occurrences.");
        await page.Locator(".calendar-event").Filter(new() { HasText = "Maintenance récurrente" }).Nth(1).ClickAsync();
        Require(await page.GetByLabel("Portée de la modification", new() { Exact = true }).InputValueAsync() == "occurrence", "Opening a recurring card initially edits only that occurrence.");
        await page.GetByLabel("Objet", new() { Exact = true }).FillAsync("Maintenance · occurrence déplacée");
        await page.GetByLabel("Début", new() { Exact = true }).FillAsync("2026-11-04T16:00"); await page.GetByLabel("Fin", new() { Exact = true }).FillAsync("2026-11-04T17:00");
        await page.Locator("[data-calendar-action=save]").ClickAsync(); await Editor().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Settled();
        Require(await page.Locator(".calendar-event").Filter(new() { HasText = "Maintenance récurrente" }).CountAsync() == 2
            && await page.Locator(".calendar-event").Filter(new() { HasText = "Maintenance · occurrence déplacée" }).CountAsync() == 1,
            "An occurrence edit preserves the other dates and renders its replacement exactly once.");
        await page.Locator("#identity-trigger").ClickAsync();
        var identity = page.Locator("dialog.identity-dialog"); await identity.WaitForAsync();
        await identity.GetByLabel("Rechercher votre nom", new() { Exact = true }).FillAsync("Samir");
        await identity.Locator("article[data-identity-key='calendar-samir']").GetByRole(AriaRole.Button, new() { Name = "C’est moi : Samir Laurent", Exact = true }).ClickAsync();
        await identity.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await WaitForNavigationTargetAsync(page, "interventions"); await Settled();
        await page.GetByLabel("Périmètre des interventions", new() { Exact = true }).SelectOptionAsync("mine"); await Settled();
        Require(await page.Locator(".calendar-event").CountAsync() == 1, "Choosing C’est moi filters shared events without hiding team functions or requiring an account.");
        await page.GetByLabel("Périmètre des interventions", new() { Exact = true }).SelectOptionAsync("all"); await Settled();
        await page.Locator(".calendar-more > summary").ClickAsync(); await Button("Déplacer plusieurs interventions").ClickAsync();
        await page.Locator(".calendar-batch-choice").Filter(new() { HasText = "Préparation libre" }).GetByRole(AriaRole.Checkbox).CheckAsync();
        await page.Locator(".calendar-batch-choice").Filter(new() { HasText = "Chevauchement" }).GetByRole(AriaRole.Checkbox).CheckAsync();
        await page.GetByLabel("Décalage en jours", new() { Exact = true }).FillAsync("1"); await Button("Calculer l’aperçu").ClickAsync(); await Settled();
        Require((await Call(app, "calendar.item", new { id })).GetProperty("intervention").GetProperty("startLocal").GetString() == "2026-11-02T09:00",
            "A batch-shift preview does not move any reservation before confirmation.");
        await Button("Confirmer le déplacement").ClickAsync(); await Settled();
        Require((await Call(app, "calendar.item", new { id })).GetProperty("intervention").GetProperty("startLocal").GetString() == "2026-11-03T09:00",
            "The confirmed multi-intervention move commits the selected batch.");
        await page.Locator("[data-calendar-action=undo]").ClickAsync(); await Settled();
        Require((await Call(app, "calendar.item", new { id })).GetProperty("intervention").GetProperty("startLocal").GetString() == "2026-11-02T09:00",
            "One undo restores the complete batch while preserving unrelated recurring dates.");
        await Capture("calendar-week-light");
        await page.EvaluateAsync("document.documentElement.dataset.theme='dark'"); await page.SetViewportSizeAsync(1280, 800); await Capture("calendar-week-dark-1280");
        Require(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth<=innerWidth+1"), "The 1280-pixel shell contains the calendar without document-level horizontal overflow.");
        Require(await page.EvaluateAsync<bool>("document.querySelector('.calendar-grid-scroll').getBoundingClientRect().top<=390"), "At 1280 × 800 the calendar starts above 390 pixels and keeps its date context and filters accessible.");
        await page.EvaluateAsync("document.documentElement.dataset.theme='light'"); await page.SetViewportSizeAsync(1440, 980);
        host.NextSave = Path.Combine(_root, "planning.ics");
        if (await page.Locator(".calendar-more").GetAttributeAsync("open") is null) await page.Locator(".calendar-more > summary").ClickAsync();
        await Button("Exporter en calendrier (.ics)").ClickAsync(); await Settled();
        Require(File.Exists(host.NextSave) && (await File.ReadAllTextAsync(host.NextSave)).Contains("BEGIN:VEVENT", StringComparison.Ordinal), "Calendar export writes actual iCalendar events using the same visible scope.");
        host.NextSave = Path.Combine(_root, "planning.html"); await page.Locator(".calendar-more > summary").ClickAsync(); await Button("Exporter les fiches d’intervention").ClickAsync(); await Settled();
        Require(File.Exists(host.NextSave) && System.Net.WebUtility.HtmlDecode(await File.ReadAllTextAsync(host.NextSave)).Contains("Préparation libre", StringComparison.Ordinal), "The printable intervention brief exports actual saved appointments.");
        await page.Locator("[data-calendar-tab=map]").ClickAsync(); await Settled();
        var planningMap = page.Locator("[data-calendar-action=planning-map]");
        await page.Locator(".calendar-map-host .maps-canvas, [data-calendar-action=planning-map]").First.WaitForAsync();
        Require(await planningMap.CountAsync() == (app.Result is null ? 0 : 1),
            "The map tab opens on the ticket map exactly when an observation is loaded, and on the planned interventions otherwise.");
        if (await planningMap.CountAsync() > 0) { await planningMap.ClickAsync(); await Settled(); }
        await page.Locator(".calendar-map-host .maps-canvas").WaitForAsync();
        Require(await page.Locator(".calendar-unmapped .calendar-event").CountAsync() >= 3 && await page.Locator("dialog.maps-dialog").CountAsync() == 0,
            "The embedded map retains free interventions without coordinates in its adjacent list.");
        await page.Locator("[data-calendar-tab=planning]").ClickAsync(); await Settled();
        await Button("Nouvelle intervention").ClickAsync(); await page.GetByLabel("Objet", new() { Exact = true }).FillAsync("Brouillon à conserver");
        await page.Locator("[data-calendar-tab=map]").ClickAsync();
        await page.GetByRole(AriaRole.Dialog, new() { Name = "Quitter cette modification ?", Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Dialog, new() { Name = "Quitter cette modification ?", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        Require(await page.GetByLabel("Objet", new() { Exact = true }).InputValueAsync() == "Brouillon à conserver", "Declining a discard keeps the unsaved draft intact when switching calendar views.");
        await Button("Annuler").ClickAsync(); await page.GetByRole(AriaRole.Dialog, new() { Name = "Quitter cette modification ?", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Abandonner les changements", Exact = true }).ClickAsync(); await Settled();
        var sharedStore = new TeamWorkspaceStore(new LibraryPreferencesStore().Load().DatabasePath);
        var replica = new InMemoryTeamWorkspaceReplica();
        await TeamWorkspaceSyncService.SynchronizeAsync(sharedStore, replica);
        var sharedSeries = new CalendarIntervention { Id = Guid.NewGuid().ToString("N"), Title = "Série partagée initiale", StartLocal = "2026-11-09T09:00", EndLocal = "2026-11-09T10:00", TimeZoneId = "UTC", Recurrence = new() { Frequency = "daily", Count = 3 } };
        await Call(app, "calendar.save", new { intervention = sharedSeries });
        await TeamWorkspaceSyncService.SynchronizeAsync(sharedStore, replica);
        var peer = new TeamWorkspaceStore(Path.Combine(_root, "Peer", "library.sqlite")); await peer.InitializeAsync();
        await TeamWorkspaceSyncService.SynchronizeAsync(peer, replica);
        var before = CalendarWorkspaceDocuments.ReadIntervention((await sharedStore.GetAsync(sharedSeries.Id))!);
        var competitor = CalendarWorkspaceDocuments.ReadIntervention((await peer.GetAsync(sharedSeries.Id))!);
        var following = before.Copy(); following.Title = "Suite proposée à conserver"; following.StartLocal = "2026-11-11T14:00"; following.EndLocal = "2026-11-11T15:00";
        await Call(app, "calendar.save", new { intervention = following, expectedRevision = before.Revision, scope = "following", originalStartLocal = "2026-11-11T09:00" });
        competitor.Title = "Version déjà partagée";
        await peer.SaveAsync(CalendarWorkspaceDocuments.ForIntervention(competitor, "Autre poste synthétique"), competitor.Revision);
        await TeamWorkspaceSyncService.SynchronizeAsync(peer, replica); await TeamWorkspaceSyncService.SynchronizeAsync(sharedStore, replica);
        await page.Locator(".calendar-more > summary").ClickAsync(); await Button("Versions à examiner").ClickAsync(); await Settled();
        Require(await page.Locator(".calendar-version-group").CountAsync() == 1, "A rejected multi-event update stays discoverable as one complete shared conflict group.");
        await Button("Comparer les versions").ClickAsync(); await Settled();
        Require(await page.Locator(".calendar-version-item").CountAsync() == 2 && (await page.Locator(".calendar-version-comparison").InnerTextAsync()).Contains("Suite proposée à conserver", StringComparison.Ordinal),
            "The review shows both the existing series and its never-published future copy before any decision.");
        await Capture("calendar-shared-conflict");
        await Button("Conserver la version actuelle").ClickAsync(); await Settled();
        Require((await Call(app, "calendar.conflicts.list")).GetProperty("total").GetInt64() == 0 && (await sharedStore.GetAsync(sharedSeries.Id))!.Title == "Version déjà partagée",
            "One explicit review decision resolves the whole group with current-version guards.");
        await Button("Fermer le panneau").ClickAsync(); await Settled();
        var priorEvents = (await Current()).GetProperty("summary").GetProperty("interventions").GetInt32();
        var importPath = Path.Combine(_root, "incidents-planning.csv");
        await File.WriteAllTextAsync(importPath,"ID;Titre;Statut;Date d'ouverture;Attribué à - Technicien;Attribué à - Groupe de techniciens;Type\n501;Accès du site Exemple A;En cours (Attribué);02/11/2026 08:00;Alex Martin;Équipe support;Incident\n502;Installation du poste Exemple B;Nouveau;02/11/2026 08:30;Samir Laurent;Équipe support;Demande\n", new UTF8Encoding(true));
        host.NextOpen = importPath;
        await page.Keyboard.PressAsync("Control+k");
        var palette = page.Locator("dialog.command-palette"); await palette.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher une commande ou un ticket", Exact = true }).FillAsync("Importer une source Excel ou CSV"); await palette.Locator("[data-command-id='action:test-file']").ClickAsync();
        await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending&&!!document.querySelector('.page-content[data-page=pilot]')");
        await page.Locator(".nav-item[data-page=interventions]").ClickAsync(); await page.Locator(".interventions-workspace[data-ready=true]").WaitForAsync(); await Date("2026-11-02");
        await Button("À planifier").ClickAsync(); await Settled();
        Require(await page.Locator(".calendar-backlog-row").CountAsync() == 2, "An offline CSV populates the planning backlog with its actual ticket references.");
        await page.GetByLabel("Choisir le ticket 501", new() { Exact = true }).CheckAsync(); await page.GetByLabel("Choisir le ticket 502", new() { Exact = true }).CheckAsync(); await Button("Planifier la sélection").ClickAsync();
        Require(await page.Locator(".calendar-ticket-chip").CountAsync() == 2, "Two imported tickets enter one shared intervention draft.");
        await page.GetByLabel("Objet", new() { Exact = true }).FillAsync("Intervention CSV · deux tickets");
        await page.Locator("[data-calendar-action=save]").ClickAsync(); await Editor().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Settled();
        var imported = await Current();
        Require(imported.GetProperty("summary").GetProperty("linkedTickets").GetInt32() == 2 && imported.GetProperty("summary").GetProperty("interventions").GetInt32() == priorEvents + 1 && app.IsFileSource && !app.IsTestMode,
            "A CSV source uses the normal calendar and preserves existing appointments while linking its actual rows.");
        await Capture("calendar-csv-simulation");
        await page.Locator(".calendar-event").Filter(new() { HasText = "Intervention CSV" }).First.ClickAsync(); await page.Locator(".calendar-ticket-link").First.ClickAsync();
        var fileValues = page.GetByRole(AriaRole.Dialog, new() { Name = "Valeurs du fichier", Exact = true }); await fileValues.WaitForAsync(); await Settled();
        Require((await fileValues.InnerTextAsync()).Contains("501", StringComparison.Ordinal) && await page.Locator(".work-ticket-reader").CountAsync() == 0,
            "A calendar link opens the actual exported fields without creating absent ticket details.");
        Require(errors.IsEmpty, "Calendar workflows finish without uncaught JavaScript errors: " + string.Join(" | ", errors));
        Require(requests.Contains("calendar.save") && requests.Contains("calendar.shift") && requests.Contains("calendar.undo") && requests.Contains("calendar.tickets"),
            "Editing, rollback and ticket linking are dispatched through the real C# bridge.");
    }
}
