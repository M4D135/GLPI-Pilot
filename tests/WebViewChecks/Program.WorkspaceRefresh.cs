using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckWorkspaceRefreshInterfaceAsync(string[] args)
    {
        var webArgument = Array.IndexOf(args, "--web-root");
        var web = Path.GetFullPath(webArgument >= 0 && webArgument + 1 < args.Length ? args[webArgument + 1] : Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        var settings = new AppSettings { Theme = "light", PortalUrl = "https://glpi-refresh-fixture.invalid/front/ticket.php", ExportFolder = Path.Combine(_root, "Exports") };
        settings.Interface.OnboardingCompleted = true; settings.Interface.FollowWindowsTheme = false;
        settings.Cities = new() { Enabled = true, TitleColumn = "Titre", LocationColumn = "Lieu" };
        var city = new CityDefinition { Name = "Ville de contrôle", Latitude = 44.1, Longitude = 5.1 };
        var north = new SiteDefinition { Name = "Atelier Nord fictif", CityId = city.Id, Latitude = 44.11, Longitude = 5.11, Aliases = ["ATELIER NORD"] };
        var south = new SiteDefinition { Name = "Atelier Sud fictif", CityId = city.Id, Latitude = 44.13, Longitude = 5.13, Aliases = ["ATELIER SUD"] };
        settings.Cities.Cities.Add(city); settings.Cities.Sites.AddRange([north, south]); settings.Save();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", TimezoneId = "Europe/Paris", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var requests = new ConcurrentQueue<(string Operation, JsonElement Arguments)>();
        var externalRequests = 0; var collectionAttempts = 0; PilotApplication? application = null;
        var holdCalendar = 0;
        TaskCompletionSource? calendarEntered = null, calendarRelease = null;
        page.PageError += (_, error) => errors.Enqueue(error);
        await context.RouteAsync("**/*", async route =>
        {
            if (EmbeddedMapsService.IsTileUrl(route.Request.Url) && application?.AllowsMapTileRequest(route.Request.Url) == true)
            { await route.FulfillAsync(new() { ContentType = "image/png", BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=") }); return; }
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404 }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page }; WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        await using var app = new PilotApplication(host, (name, data) =>
        {
            if (name == "operation.progress") Console.WriteLine("TRACE refresh progress " + JsonSerializer.Serialize(data, PilotApplication.Json));
            bridge?.Publish(name, data);
        }, settings); application = app;
        await Call(app, "calendar.resource.save", new { resource = new CalendarResource { Key = "calendar-alex", Label = "Alex Martin", TimeZoneId = "UTC" }, expectedRevision = 0 });
        await Call(app, "calendar.resource.save", new { resource = new CalendarResource { Key = "calendar-samir", Label = "Samir Laurent", TimeZoneId = "UTC" }, expectedRevision = 0 });
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            requests.Enqueue((op, data.Clone()));
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            if (op.StartsWith("collect.", StringComparison.Ordinal))
            { Interlocked.Increment(ref collectionAttempts); throw new InvalidOperationException("A workspace refresh must not invoke GLPI collection, even in the isolated fixture."); }
            if (op == "calendar.get" && Interlocked.Exchange(ref holdCalendar, 0) == 1)
            {
                // Pause before the real read to exercise navigation during an
                // in-flight refresh without replacing any C# response.
                calendarEntered!.TrySetResult(); await calendarRelease!.Task.WaitAsync(ct);
            }
            var timing = System.Diagnostics.Stopwatch.StartNew();
            if (op == "app.demo") Console.WriteLine("TRACE demo request admitted " + DateTimeOffset.UtcNow.ToString("O"));
            try { return await app.ExecuteAsync(op, data, ct); }
            finally { if (op == "app.demo") Console.WriteLine($"TRACE demo request settled after {timing.ElapsedMilliseconds} ms; simulation={app.IsTestMode}; cancelled={ct.IsCancellationRequested}"); }
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (() => { const listeners = new Set(); window.chrome ||= {}; window.chrome.webview = {
              postMessage: message => window.__pilotSend(JSON.stringify(message)),
              addEventListener: (name, handler) => { if (name === 'message') listeners.add(handler); },
              removeEventListener: (name, handler) => listeners.delete(handler)
            }; window.__pilotDeliver = message => { for (const listener of listeners) listener({ data: message }); }; })();
            """);
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        ILocator Refresh() => page.Locator(".page-heading button[title='Actualiser cet espace']");
        ILocator Tab(string key) => page.Locator($".calendar-tabs [data-calendar-tab='{key}']");
        async Task Idle() => await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending&&!document.querySelector('#content').hasAttribute('aria-busy')&&!document.querySelector('.interventions-workspace')?.hasAttribute('aria-busy')&&document.querySelector('.page-heading button[title=\"Actualiser cet espace\"]')?.getAttribute('aria-busy')!=='true'");
        int Count(string operation) => requests.Count(request => request.Operation == operation);
        JsonElement Last(string operation) => requests.Last(request => request.Operation == operation).Arguments;
        Task<JsonElement> MapView() => page.EvaluateAsync<JsonElement>("""
            () => {const map=window.__refreshObservedMaps.findLast(item=>item.getContainer().isConnected);
              const center=map.getCenter();return {lat:center.lat,lng:center.lng,zoom:map.getZoom()};}
            """);
        async Task MapSettled()
        {
            await page.EvaluateAsync("()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
            await WaitForPageAsync(page, "()=>{const map=window.__refreshObservedMaps.findLast(item=>item.getContainer().isConnected);return map&&!map._animatingZoom&&!map._panAnim?._inProgress;}");
        }
        async Task Capture(string name)
        {
            var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return;
            Directory.CreateDirectory(args[index + 1]); await DismissCaptureNotificationsAsync(page);
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[index + 1], name + ".png"), FullPage = true });
        }
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(page);
        // Observe actual Leaflet instances. User clicks and drags still control
        // them; no map state or production response is replaced by this hook.
        await page.EvaluateAsync("""
            () => {window.__refreshObservedMaps=[];const create=window.L.map;
              window.L.map=function(...args){const map=create.apply(this,args);window.__refreshObservedMaps.push(map);return map;};}
            """);
        await Button("Collecter depuis GLPI").ClickAsync(); await ReadyAsync(page.Locator(".data-workspace[data-ready=true]"));
        await page.Locator("[data-open-panel=sites]").ClickAsync();
        var reference = page.Locator("dialog[data-workspace-panel=reference]"); await ReadyAsync(reference.Locator(".terrain-function-panel[data-ready=true]"));
        await reference.Locator(":scope > .modal-heading button").ClickAsync(); await reference.WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Idle();
        Require(await page.Locator(".interventions-workspace").GetAttributeAsync("data-tab") == "map" && app.Result is null,
            "The real Data-to-site-reference deep link opens the ticket map and closes its reference panel without loading business data.");
        var reads = requests.Count(request => request.Operation == "calendar.get");
        await Refresh().ClickAsync(); await Idle(); await Capture("refresh-after-reference");
        Require(Count("calendar.get") > reads && await reference.CountAsync() == 0 && await page.Locator(".terrain-map-page").CountAsync() == 1,
            "The global Actualiser reads the current workspace without replaying the closed site-reference opening command.");
        await Tab("planning").ClickAsync(); await Idle(); await Tab("map").ClickAsync(); await Idle();
        Require(await reference.CountAsync() == 0 && await page.Locator(".interventions-workspace").GetAttributeAsync("data-tab") == "map",
            "Returning from Planning to Map does not replay an already consumed site-reference deep link.");

        var csv = Path.Combine(_root, "tickets-refresh.csv");
        await File.WriteAllTextAsync(csv, "ID;Titre;Statut;Type;Lieu;Attribué à - Technicien;Attribué à - Groupe\n4101;Incident ATELIER NORD;Nouveau;Incident;ATELIER NORD;Alex Martin;Support Nord\n4102;Demande ATELIER SUD;Nouveau;Demande;ATELIER SUD;Samir Laurent;Support Sud\n4103;Incident ATELIER NORD suite;En cours;Incident;ATELIER NORD;Alex Martin;Support Nord\n");
        host.NextOpen = csv; await Call(app, "records.import");
        await page.ReloadAsync(); await ReadyAsync(page);
        await page.EvaluateAsync("""
            () => {window.__refreshObservedMaps=[];const create=window.L.map;
              window.L.map=function(...args){const map=create.apply(this,args);window.__refreshObservedMaps.push(map);return map;};}
            """);
        // Forget the calendar view remembered by the deep link above: a profile that never used a map command.
        await page.EvaluateAsync("()=>{for(const key of Object.keys(localStorage))if(key.endsWith(':calendar'))localStorage.removeItem(key);}");
        await page.Locator(".nav-item[data-page=interventions]").ClickAsync(); await page.Locator(".interventions-workspace").WaitForAsync(); await Idle();
        if (await Tab("map").GetAttributeAsync("aria-current") != "page") await Tab("map").ClickAsync();
        await page.Locator(".calendar-ticket-map-host .terrain-map-page[data-ready=true]").WaitForAsync(); await Idle();
        var counted = await page.Locator(".maps-inline-host .maps-marker .maps-pin").AllInnerTextsAsync();
        Require(app.Result is not null && await page.Locator(".calendar-map-host").CountAsync() == 0 && counted.Count > 0 && counted.All(text => int.TryParse(text, out var count) && count > 0),
            "With an observation loaded and no map command ever used, Interventions → Carte opens on the ticket map and each marker carries its ticket count.");
        await page.Locator("[data-calendar-action=planning-map]").ClickAsync(); await page.Locator(".calendar-map-host .maps-canvas").WaitForAsync(); await Idle();
        await Tab("planning").ClickAsync(); await Idle(); await Tab("map").ClickAsync(); await page.Locator(".calendar-map-host .maps-canvas").WaitForAsync(); await Idle();
        Require(await page.Locator(".calendar-ticket-map-host").CountAsync() == 0,
            "An explicit choice of the planned-interventions map is kept when returning to the map tab.");
        await page.Locator("[data-calendar-action=ticket-map]").ClickAsync();
        await page.Locator(".calendar-ticket-map-host .terrain-map-page[data-ready=true]").WaitForAsync(); await Idle();
        await page.Locator(".nav-item[data-page=data]").ClickAsync(); await ReadyAsync(page.Locator(".data-workspace[data-ready=true]"));
        await page.Locator("[data-open-panel=sites]").ClickAsync(); await ReadyAsync(reference.Locator(".terrain-function-panel[data-ready=true]"));
        await reference.Locator(":scope > .modal-heading button").ClickAsync(); await reference.WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Idle();
        Require(app.IsFileSource && !app.IsTestMode && app.Result!.Rows.Count == 3,
            "The loaded-map scenario uses three rows read by the production CSV importer, not a canned map model.");
        await page.GetByLabel("Type de ticket", new() { Exact = true }).SelectOptionAsync("incident"); await Idle();
        await Button("Statuts actifs (4/4)").ClickAsync();
        var statuses = page.GetByRole(AriaRole.Dialog, new() { Name = "Statuts actifs", Exact = true });
        foreach (var control in await statuses.GetByRole(AriaRole.Checkbox).AllAsync())
            if (await control.GetAttributeAsync("aria-label") != "Nouveau") await control.UncheckAsync();
        await statuses.GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync(); await Idle();
        var search = page.GetByLabel("Rechercher dans les tickets du périmètre", new() { Exact = true });
        await search.FillAsync("4101"); await search.PressAsync("Enter"); await Idle();
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Sélectionner le ticket 4101", Exact = true }).CheckAsync(); await Idle();
        await page.Locator(".maps-ticket-rows").GetByRole(AriaRole.Button, new() { Name = "Localiser", Exact = true }).ClickAsync(); await Idle();
        var canvas = page.Locator(".maps-inline-host .maps-canvas"); await canvas.ScrollIntoViewIfNeededAsync();
        await canvas.GetByTitle("Agrandir la carte", new() { Exact = true }).ClickAsync();
        var box = (await canvas.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(box.X + box.Width * .65f, box.Y + box.Height * .55f); await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + box.Width * .65f + 70, box.Y + box.Height * .55f + 35, new() { Steps = 8 }); await page.Mouse.UpAsync();
        await MapSettled();
        var viewportBefore = await MapView(); var filtersBefore = Last("maps.workspace.get"); var mapReads = Count("maps.workspace.get");
        await Refresh().ClickAsync(); await Idle(); await MapSettled(); var viewportAfter = await MapView();
        Require(Count("maps.workspace.get") == mapReads + 1 && Last("maps.workspace.get").GetRawText() == filtersBefore.GetRawText()
            && await page.GetByLabel("Type de ticket", new() { Exact = true }).InputValueAsync() == "incident"
            && Last("maps.workspace.get").GetProperty("statusCodes").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual(new[] { 1 }),
            "Global refresh reloads the actual ticket map exactly once with the current type and status filters.");
        Require(await search.InputValueAsync() == "4101" && await page.GetByRole(AriaRole.Checkbox, new() { Name = "Sélectionner le ticket 4101", Exact = true }).IsCheckedAsync()
            && Last("maps.workspace.rows").GetProperty("search").GetString() == "4101"
            && Last("maps.workspace.rows").GetProperty("placeKey").GetString() == "site:" + north.Id && await reference.CountAsync() == 0,
            "The refreshed map retains its real search text, selected ticket and selected place without reopening the reference panel.");
        Require(viewportBefore.GetProperty("zoom").GetInt32() == viewportAfter.GetProperty("zoom").GetInt32()
            && Math.Abs(viewportBefore.GetProperty("lat").GetDouble() - viewportAfter.GetProperty("lat").GetDouble()) < .00001
            && Math.Abs(viewportBefore.GetProperty("lng").GetDouble() - viewportAfter.GetProperty("lng").GetDouble()) < .00001,
            $"A user-zoomed and panned Leaflet viewport survives the real map refresh ({viewportBefore.GetRawText()} → {viewportAfter.GetRawText()}).");
        await Capture("refresh-map-filters-selection");
        await Call(app, "cache.restore"); File.Delete(csv); await Idle(); await Refresh().ClickAsync(); await Idle();
        var restoredRows = app.Result; var restoredSearch = await search.InputValueAsync();
        // Idle() n'attend que la fin des appels au pont, jamais la fin du rendu des lignes : la case pouvait
        // encore porter la coche de la révision précédente à l'instant où on la lisait, et l'assertion échouait
        // au hasard du rafraîchissement. On attend l'état réellement attendu, avec un délai borné, pour qu'un
        // rendu lent passe mais qu'une vraie conservation de la sélection échoue quand même.
        var selectionBox = page.GetByRole(AriaRole.Checkbox, new() { Name = "Sélectionner le ticket 4101", Exact = true });
        var stillSelected = true;
        for (var attempt = 0; attempt < 100 && stillSelected; attempt++)
        {
            stillSelected = await selectionBox.CountAsync() > 0 && await selectionBox.IsCheckedAsync();
            if (stillSelected) await page.WaitForTimeoutAsync(100);
        }
        Require(app.IsCachedSource && restoredRows!.Rows.Count == 3 && !File.Exists(csv) && restoredSearch == "4101" && !stillSelected,
            "Refreshing the restored offline source rereads the cached rows without the CSV and clears ticket choices made against the old revision "
            + $"(sourceEnCache={app.IsCachedSource}, lignes={restoredRows?.Rows.Count}, csvSupprime={!File.Exists(csv)}, recherche=\"{restoredSearch}\", selectionGardee={stillSelected}).");

        await Tab("planning").ClickAsync(); await Idle();
        await page.GetByLabel("Date du planning", new() { Exact = true }).FillAsync("2026-11-17");
        await page.GetByLabel("Date du planning", new() { Exact = true }).DispatchEventAsync("change"); await Idle();
        await page.GetByLabel("Présentation du calendrier", new() { Exact = true }).SelectOptionAsync("day"); await Idle();
        await page.Locator("[data-calendar-action=filters]").ClickAsync();
        await page.Locator(".calendar-side").GetByRole(AriaRole.Checkbox, new() { Name = "Alex Martin", Exact = true }).First.CheckAsync();
        await page.Locator(".calendar-side").GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync(); await Idle();
        var calendarBefore = Last("calendar.get"); reads = Count("calendar.get");
        await Refresh().ClickAsync(); await Idle();
        Require(Count("calendar.get") > reads && Last("calendar.get").GetRawText() == calendarBefore.GetRawText()
            && calendarBefore.GetProperty("technicians").GetArrayLength() == 1
            && await page.Locator(".interventions-workspace").GetAttributeAsync("data-tab") == "planning"
            && await page.GetByLabel("Date du planning", new() { Exact = true }).InputValueAsync() == "2026-11-17"
            && await page.GetByLabel("Présentation du calendrier", new() { Exact = true }).InputValueAsync() == "day" && await reference.CountAsync() == 0,
            "Global refresh keeps the current Planning tab, date, day view and technician scope instead of restoring the initial map route.");
        await Button("Nouvelle intervention").ClickAsync(); await page.Locator(".calendar-editor-body").WaitForAsync();
        await page.GetByLabel("Objet", new() { Exact = true }).FillAsync("Brouillon non enregistré de contrôle");
        await page.GetByLabel("Début", new() { Exact = true }).FillAsync("2026-11-17T09:15");
        var saves = Count("calendar.save"); await Refresh().ClickAsync(); await Idle();
        Require(await page.GetByLabel("Objet", new() { Exact = true }).InputValueAsync() == "Brouillon non enregistré de contrôle"
            && await page.GetByLabel("Début", new() { Exact = true }).InputValueAsync() == "2026-11-17T09:15"
            && await page.Locator("dialog[open]").CountAsync() == 0 && Count("calendar.save") == saves,
            "Refreshing Planning preserves the unsaved intervention fields without saving them or demanding an unnecessary abandon confirmation.");
        await Capture("refresh-planning-unsaved-editor");
        await Button("Fermer le panneau").ClickAsync();
        await page.GetByRole(AriaRole.Dialog, new() { Name = "Quitter cette modification ?", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Abandonner les changements", Exact = true }).ClickAsync(); await Idle();
        // The declared calendar resource is intentionally distinct from the
        // observed CSV identity. Clear that calendar-only scope through the UI
        // before exercising the ticket map; its unknown-key guard must remain.
        await page.Locator("[data-calendar-action=filters]").ClickAsync();
        await page.Locator(".calendar-side").GetByRole(AriaRole.Button, new() { Name = "Tout afficher", Exact = true }).ClickAsync(); await Idle();

        await Call(app, "terrain.tours.select", new { revision = app.Revision, rows = new[] { 0 }, selected = true });
        await Call(app, "terrain.tours.create", new { revision = app.Revision, name = "Tournée de contrôle", date = "2026-11-17", discardDraft = true });
        bridge.Publish("app.navigate", new { page = "tours", @params = new { openDraft = true } });
        await page.Locator(".calendar-tours-host").WaitForAsync(); await Idle();
        await page.Locator(".calendar-tours-host summary").Filter(new() { HasText = "Nom, horaires, départ, retour et consignes" }).ClickAsync();
        var tourName = page.Locator(".calendar-tours-host").GetByLabel("Nom", new() { Exact = true });
        await tourName.FillAsync("Titre de tournée encore saisi");
        var tourReads = Count("terrain.tours.state"); await Refresh().ClickAsync();
        var tourConfirm = page.GetByRole(AriaRole.Dialog, new() { Name = "Actualiser le planning", Exact = true }); await tourConfirm.WaitForAsync();
        await tourConfirm.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync(); await Idle();
        Require(await tourName.InputValueAsync() == "Titre de tournée encore saisi" && Count("terrain.tours.state") == tourReads
            && (await Call(app, "terrain.tours.state")).GetProperty("draft").GetProperty("name").GetString() == "Tournée de contrôle",
            "Cancelling the existing Tours refresh confirmation retains the typed header and leaves the real draft unchanged.");
        await Capture("refresh-tours-cancelled");
        await Refresh().ClickAsync(); await tourConfirm.WaitForAsync();
        await tourConfirm.GetByRole(AriaRole.Button, new() { Name = "Actualiser", Exact = true }).ClickAsync(); await Idle();
        Require(Count("terrain.tours.state") == tourReads + 1 && await tourName.InputValueAsync() == "Tournée de contrôle"
            && await page.Locator(".interventions-workspace").GetAttributeAsync("data-tab") == "tours",
            "Confirming Tours refresh reads its actual draft and redraws that same tab without replaying a previous reference request.");

        await Tab("map").ClickAsync();
        try { await page.Locator(".calendar-ticket-map-host .terrain-map-page[data-ready=true]").WaitForAsync(); }
        catch { Console.WriteLine("Map mount diagnostic: " + await page.Locator("#content").InnerTextAsync()); await Capture("refresh-map-mount-failure"); throw; }
        await Idle(); mapReads = Count("maps.workspace.get");
        calendarEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); calendarRelease = new(TaskCreationOptions.RunContinuationsAsynchronously); holdCalendar = 1;
        await Refresh().ClickAsync(); await calendarEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try { await Tab("planning").ClickAsync(); await page.Locator(".interventions-workspace[data-tab=planning]").WaitForAsync(); }
        finally { calendarRelease.TrySetResult(); }
        await Idle();
        Require(await page.Locator(".interventions-workspace").GetAttributeAsync("data-tab") == "planning"
            && Count("maps.workspace.get") == mapReads && await page.Locator(".calendar-ticket-map-host").CountAsync() == 0,
            $"A calendar read completed after switching tabs cannot refresh or remount the old ticket-map child (tab={await page.Locator(".interventions-workspace").GetAttributeAsync("data-tab")}; map reads={mapReads}→{Count("maps.workspace.get")}; old hosts={await page.Locator(".calendar-ticket-map-host").CountAsync()}).");
        calendarEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); calendarRelease = new(TaskCreationOptions.RunContinuationsAsynchronously); holdCalendar = 1;
        await Refresh().ClickAsync(); await calendarEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try { await page.Locator(".nav-item[data-page=data]").ClickAsync(); }
        finally { calendarRelease.TrySetResult(); }
        await ReadyAsync(page.Locator(".data-workspace[data-ready=true]")); await Idle();
        Require(await page.Locator(".interventions-workspace,dialog[data-workspace-panel=reference]").CountAsync() == 0
            && (await page.Locator("[data-current-origin]").InnerTextAsync()).Contains("Cache local", StringComparison.Ordinal),
            "Navigating away during refresh keeps the new Data workspace and its honest cached-source provenance.");

        var realSettings = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        await OpenDemonstrationFromSettingsAsync(page);
        try { await WaitForNavigationTargetAsync(page, "home"); }
        catch
        {
            Console.WriteLine($"TRACE demo timeout: busy={app.IsBusy}; operation={app.CurrentOperation}; simulation={app.IsTestMode}; requested={Count("app.demo")}");
            Console.WriteLine("TRACE synthetic page: " + await page.EvaluateAsync<string>("""
                async () => JSON.stringify({ page: document.querySelector('.page-content')?.dataset.page,
                  contentBusy: document.querySelector('#content')?.getAttribute('aria-busy'),
                  pending: (await import('/js/bridge.js')).api.pending,
                  status: document.querySelector('.status-text')?.textContent?.slice(0,500),
                  errors: [...document.querySelectorAll('.notice.danger,#notifications .toast')].map(n=>(n.textContent||'').slice(0,800)).slice(0,5),
                  dialogs: [...document.querySelectorAll('dialog[open]')].map(n=>({panel:n.dataset.workspacePanel,title:n.querySelector('h2')?.textContent})),
                  demoButtons: [...document.querySelectorAll('button')].filter(n=>/démonstration|scénario/.test(n.textContent||'')).map(n=>({disabled:n.disabled,busy:n.getAttribute('aria-busy')})) })
                """));
            await Capture("refresh-demo-navigation-failure");
            throw;
        }
        await page.Locator(".nav-item[data-page=data]").ClickAsync(); await ReadyAsync(page.Locator(".data-workspace[data-ready=true]"));
        await page.Locator("[data-open-panel=sites]").ClickAsync(); await ReadyAsync(reference.Locator(".terrain-function-panel[data-ready=true]"));
        await reference.Locator(":scope > .modal-heading button").ClickAsync(); await reference.WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Idle();
        mapReads = Count("maps.workspace.get"); await Refresh().ClickAsync(); await Idle();
        var afterSimulation = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        Require(app.IsTestMode && Count("maps.workspace.get") == mapReads + 1 && await reference.CountAsync() == 0 && realSettings.SequenceEqual(afterSimulation),
            "The same global refresh works inside the genuine fictitious demo and leaves the real profile byte-for-byte unchanged.");
        await Call(app, "records.test.exit");
        Require(app.IsCachedSource && app.Result!.Rows.Count == 3, "Leaving the refresh demonstration restores the original offline observation.");
        Require(errors.IsEmpty && externalRequests == 0 && collectionAttempts == 0,
            "Workspace refresh uses real local C# reads without JavaScript errors, external requests or GLPI collection attempts.");
        await actualBridge.DisposeAsync(); await delivery;
    }
}
