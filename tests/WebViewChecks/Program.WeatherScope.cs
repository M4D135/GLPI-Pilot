using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckWeatherScopeInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1280, Height = 800 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(12000);
        var errors = new ConcurrentQueue<string>(); var externalRequests = 0; var tileRequests = 0;
        PilotApplication? mapApplication = null;
        page.PageError += (_, error) => errors.Enqueue(error);
        await context.RouteAsync("**/*", async route =>
        {
            if (EmbeddedMapsService.IsTileUrl(route.Request.Url))
            {
                if (mapApplication?.AllowsMapTileRequest(route.Request.Url) != true) { await route.AbortAsync(); return; }
                Interlocked.Increment(ref tileRequests);
                await route.FulfillAsync(new() { ContentType = "image/png", BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=") }); return;
            }
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page, NextOpen = CreateWeatherScopeCsv() };
        WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        JsonElement latestPilot = default, latestMap = default;
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), new AppSettings { Theme = "light", ExportFolder = Path.Combine(_root, "Exports") });
        mapApplication = app;
        var normalSettings = JsonSerializer.Serialize(app.Settings, PilotApplication.Json);
        await Call(app, "records.test.import");
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (operation, data, cancellationToken) =>
        {
            if (operation == "app.ready") return new { ready = true };
            if (operation == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            var result = await app.ExecuteAsync(operation, data, cancellationToken);
            if (operation is "dashboard.get" or "maps.workspace.get")
            {
                var serialized = JsonSerializer.SerializeToElement(result, PilotApplication.Json);
                lock (gate) { if (operation == "dashboard.get") latestPilot = serialized; else latestMap = serialized; }
            }
            return result;
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (() => { const listeners = new Set(); window.chrome ||= {}; window.chrome.webview = {
              postMessage: message => window.__pilotSend(JSON.stringify(message)),
              addEventListener: (name, handler) => { if (name === 'message') listeners.add(handler); },
              removeEventListener: (name, handler) => listeners.delete(handler)
            }; window.__pilotDeliver = message => { for (const listener of listeners) listener({ data: message }); }; })();
            """);
        JsonElement Pilot() { lock (gate) return latestPilot.Clone(); }
        JsonElement Map() { lock (gate) return latestMap.Clone(); }
        static JsonElement Hero(JsonElement model) => model.GetProperty("widgets").EnumerateArray().Single(widget => widget.GetProperty("id").GetString() == model.GetProperty("weatherScope").GetProperty("heroWidgetId").GetString());
        static JsonElement Team(JsonElement model, string id) => model.GetProperty("widgets").EnumerateArray().Single(widget =>
            widget.TryGetProperty("weatherContext", out var weather) && weather.ValueKind == JsonValueKind.Object
            && weather.GetProperty("role").GetString() == "team" && weather.GetProperty("ruleId").GetString() == id);
        static bool Neutral(JsonElement widget) => widget.GetProperty("content").GetProperty("volumeWeather").GetProperty("state").GetString() == "unknown"
            && widget.GetProperty("settings").GetProperty("warningValue").ValueKind == JsonValueKind.Null
            && widget.GetProperty("settings").GetProperty("criticalValue").ValueKind == JsonValueKind.Null;
        ILocator HeroCard() => page.Locator(".business-weather-hero");
        ILocator PilotPicker() => page.GetByRole(AriaRole.Dialog, new() { Name = "Choisir plusieurs équipes", Exact = true });
        async Task Settled() => await WaitForPageAsync(page, "async()=>!document.querySelector('.pilot-workspace')?.hasAttribute('aria-busy')&&!(await import('/js/bridge.js')).api.pending");
        async Task Navigate(string route) { await ClickNavigationAsync(page, route); await WaitForNavigationTargetAsync(page, route); await Settled(); }
        async Task Stock(string value) => await WaitForPageAsync(page, "value=>document.querySelector('.business-weather-hero .analytics-metric-value')?.textContent.trim()===value", value);
        async Task ChoosePilot(params string[] labels)
        {
            await page.Locator("[data-filter-teams]").ClickAsync();
            await PilotPicker().GetByRole(AriaRole.Button, new() { Name = "Toutes", Exact = true }).ClickAsync();
            foreach (var label in labels) await PilotPicker().GetByRole(AriaRole.Checkbox, new() { Name = label, Exact = true }).CheckAsync();
            await PilotPicker().GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync(); await Settled();
        }
        async Task Capture(string name)
        {
            var argument = Array.IndexOf(args, "--screenshots"); if (argument < 0 || argument + 1 >= args.Length) return;
            Directory.CreateDirectory(args[argument + 1]);
            await page.Locator("#notifications .toast button").EvaluateAllAsync<int>("buttons=>{buttons.forEach(button=>button.click());return buttons.length;}");
            await page.Locator(".workspace").EvaluateAsync("node=>node.scrollTop=0");
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[argument + 1], name + ".png") });
        }
        async Task InspectWeatherCard(string name, bool thresholds)
        {
            var hero = HeroCard();
            await hero.EvaluateAsync("node=>{node.scrollTop=0;node.scrollIntoView({block:'center',behavior:'instant'});}");
            var geometry = await hero.EvaluateAsync<JsonElement>("""
                node=>{
                  const bounds=node.getBoundingClientRect(), next=node.closest('.pilot-block').nextElementSibling?.getBoundingClientRect(), style=getComputedStyle(node);
                  return {height:bounds.height,clientHeight:node.clientHeight,scrollHeight:node.scrollHeight,
                    bounded:!next||next.top>=bounds.bottom-1,horizontal:node.scrollWidth<=node.clientWidth+1,
                    scrollable:node.scrollHeight<=node.clientHeight+1||['auto','scroll'].includes(style.overflowY),
                    reason:node.querySelector('.analytics-weather-context')?.textContent,
                    thresholds:node.querySelector('.pilot-weather-threshold-labels')?.textContent||''};
                }
                """);
            var argument = Array.IndexOf(args, "--screenshots");
            var path = argument >= 0 && argument + 1 < args.Length ? args[argument + 1] : null;
            if (path is not null) await hero.ScreenshotAsync(new() { Path = Path.Combine(path, name + "-card.png") });
            var detail = hero.Locator(thresholds ? ".pilot-weather-thresholds" : ".analytics-weather-context");
            await detail.ScrollIntoViewIfNeededAsync();
            var readable = await detail.EvaluateAsync<bool>("""
                node=>{const rect=node.getBoundingClientRect(),card=node.closest('.business-weather-hero').getBoundingClientRect();
                  return rect.top>=card.top-1&&rect.bottom<=card.bottom+1&&rect.left>=card.left-1&&rect.right<=card.right+1;}
                """);
            Require(Math.Abs(geometry.GetProperty("height").GetDouble() - 240) <= 1 && geometry.GetProperty("bounded").GetBoolean()
                && geometry.GetProperty("horizontal").GetBoolean() && geometry.GetProperty("scrollable").GetBoolean() && readable
                && !string.IsNullOrWhiteSpace(geometry.GetProperty("reason").GetString())
                && (thresholds ? geometry.GetProperty("thresholds").GetString()!.Contains("Vigilance > 10", StringComparison.Ordinal)
                    && geometry.GetProperty("thresholds").GetString()!.Contains("Tension > 20", StringComparison.Ordinal)
                    : geometry.GetProperty("thresholds").GetString() == ""),
                thresholds ? "At 1280 pixels, the saved 240-pixel hero keeps its exact 10/20 labels accessible by contained scrolling without overlapping the next section."
                    : "The 240-pixel cumulative hero retains a readable neutral explanation, no irrelevant threshold track and no horizontal or neighbouring-section overflow.");
            if (path is not null)
            {
                await hero.ScreenshotAsync(new() { Path = Path.Combine(path, name + "-detail.png") });
                await File.WriteAllTextAsync(Path.Combine(path, name + "-geometry.json"), geometry.GetRawText());
            }
            await hero.EvaluateAsync("node=>node.scrollTop=0");
        }
        async Task DeepLink(string route, object parameters)
        {
            actualBridge.Publish("app.navigate", new { page = route, @params = parameters }); await delivery;
            await WaitForNavigationTargetAsync(page, route); await Settled();
        }
        async Task MapStock(int count)
        {
            await WaitForPageAsync(page, "count=>document.querySelector('.maps-ticket-rows')?.dataset.total===String(count)&&document.querySelector('.maps-workload-total strong')?.textContent.trim()===String(count)", count);
            await Settled();
        }
        async Task<int[]> MapIds()
        {
            var rows = await Call(app, "maps.workspace.rows", new { token = Map().GetProperty("token").GetString(), offset = 0, limit = 200 });
            return rows.GetProperty("items").EnumerateArray().Select(row => int.Parse(row.GetProperty("ticket").GetProperty("id").GetString()!, CultureInfo.InvariantCulture)).Order().ToArray();
        }
        async Task ChooseTerrain(params string[] labels)
        {
            await page.Locator(".maps-scope-toolbar").GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex(@"^Équipes(?: \(\d+\))?$") }).ClickAsync();
            var picker = page.GetByRole(AriaRole.Dialog, new() { Name = "Équipes", Exact = true });
            await picker.GetByRole(AriaRole.Button, new() { Name = "Toutes", Exact = true }).ClickAsync();
            foreach (var label in labels) await picker.GetByRole(AriaRole.Checkbox, new() { Name = label, Exact = true }).CheckAsync();
            await picker.GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync(); await Settled();
        }
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await page.Locator("body[data-ready=true]").WaitForAsync();
        await Navigate("pilot"); await Stock("100");

        var catalogue = await page.EvaluateAsync<JsonElement>("""
            async () => {
              const { teamScopeCatalog, teamScopeSelection } = await import('/js/team-scope.js');
              const teams=[{key:'team:support-a',label:'Support',groups:['A']},{key:'team:alias',label:'Alias de support',groups:['a']},
                {key:'team:support-b',label:'Support',groups:['B']},{key:'team:network',label:'Réseau',groups:['A','B']},
                {key:'team:network-reversed',label:'Réseau inversé',groups:['B','A']}];
              const options=teamScopeCatalog(teams,['A','a','B','C']);
              const selected=teamScopeSelection(options,['group:A','team:alias']);
              const unknown=teamScopeSelection(options,['group:retiré']);
              const distinct=teamScopeCatalog([],['A','a','Équipe','Equipe','G-A','G A']);
              return {options,selected,unknown,distinct,all:teamScopeSelection(options,[])};
            }
            """);
        var options = catalogue.GetProperty("options").EnumerateArray().ToArray();
        Require(options.Length == 4 && options.Count(option => option.GetProperty("label").GetString() == "Support") == 2
            && catalogue.GetProperty("selected").GetProperty("keys").GetArrayLength() == 1
            && catalogue.GetProperty("selected").GetProperty("keys")[0].GetString() == "team:support-a"
            && options.Single(option => option.GetProperty("key").GetString() == "team:network").GetProperty("aliases").EnumerateArray().Any(alias => alias.GetString() == "team:network-reversed")
            && options.Single(option => option.GetProperty("key").GetString() == "team:support-a").GetProperty("labels").EnumerateArray().Any(label => label.GetString() == "Alias de support"),
            "The shared catalogue deduplicates equal group sets, retains alias labels and legacy keys, and keeps different groups despite identical team names.");
        Require(catalogue.GetProperty("distinct").GetArrayLength() == 5
            && catalogue.GetProperty("unknown").GetProperty("unknownKeys").GetArrayLength() == 1
            && catalogue.GetProperty("all").GetProperty("keys").GetArrayLength() == 0,
            "Catalogue identity ignores only group-name case: accents and punctuation stay distinct, and a retired key never silently becomes All.");

        await page.Locator("[data-filter-teams]").ClickAsync();
        Require(await PilotPicker().GetByRole(AriaRole.Checkbox).CountAsync() == 3
            && await PilotPicker().GetByRole(AriaRole.Checkbox, new() { Name = "Support", Exact = true }).CountAsync() == 1
            && await PilotPicker().GetByRole(AriaRole.Checkbox, new() { Name = "Réseau", Exact = true }).CountAsync() == 1,
            "The real Pilotage picker offers each observed team once, without a duplicate raw-group choice.");
        await PilotPicker().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await ChoosePilot("Réseau"); await Stock("18");
        Require(Pilot().GetProperty("weatherScope").GetProperty("mode").GetString() == "team"
            && await HeroCard().GetAttributeAsync("data-weather-state") == "watch"
            && Hero(Pilot()).GetProperty("settings").GetProperty("warningValue").GetDouble() == 10
            && Hero(Pilot()).GetProperty("settings").GetProperty("criticalValue").GetDouble() == 20
            && await page.Locator(".workspace-weather-defaults").IsVisibleAsync(),
            "The complete synthetic 18-ticket network scope uses its own 10/20 starter thresholds and keeps their uncalibrated-default notice visible.");
        await ChoosePilot("Support", "Réseau"); await Stock("96");
        Require(Pilot().GetProperty("weatherScope").GetProperty("mode").GetString() == "multiple" && Neutral(Hero(Pilot()))
            && await HeroCard().GetAttributeAsync("data-weather-state") == "unknown"
            && !(await HeroCard().InnerTextAsync()).Contains("Maîtrisé", StringComparison.Ordinal)
            && await page.Locator(".workspace-weather-defaults").IsVisibleAsync(),
            "A multiple-team union of 96 source tickets stays neutral instead of borrowing the global 100/200 threshold or presenting starter limits as calibrated.");
        await Capture("scope-defaults-multiple-light-1280");

        var beforeProfile = await Call(app, "dashboard.business.settings.get");
        var business = new BusinessDashboardSettings { Notes = "Données synthétiques de régression · aucun incident réel.", FreshnessWarningHours = 48,
            WeatherRules = new()
            {
                new() { Id = "scope-global", Label = "Stock fictif du service", OrangeAbove = 100, RedAbove = 200 },
                new() { Id = "scope-support", Kind = "team", Label = "Support", Values = new() { "Support" }, OrangeAbove = 10, RedAbove = 20 },
                new() { Id = "scope-network", Kind = "team", Label = "Réseau", Values = new() { "Réseau" }, OrangeAbove = 2, RedAbove = 8 }
            } };
        await Call(app, "dashboard.business.settings.save", new { business, baseFingerprint = beforeProfile.GetProperty("fingerprint").GetString(),
            sites = new[] { new SiteDefinition { Name = "Site fictif Cobalt", Latitude = 44.1, Longitude = 5.1 }, new SiteDefinition { Name = "Site fictif Turquoise", Latitude = 44.2, Longitude = 5.2 } } });
        var layout = (await Call(app, "dashboard.layout.get", new { businessView = "workspace" })).Deserialize<DashboardLayoutSettings>(PilotApplication.Json)!;
        var storedHero = layout.Boards[0].Widgets.Single(widget => widget.Id == BusinessDashboardTemplates.WeatherWidgetId("scope-global"));
        storedHero.Title = "Titre fictif personnalisé"; storedHero.Height = 240; storedHero.AutoHeight = false;
        await Call(app, "dashboard.layout.save", new { businessView = "workspace", layout });
        var storedRules = JsonSerializer.Serialize(app.Settings.Dashboard.Business.WeatherRules, PilotApplication.Json);
        await DeepLink("pilot", new { focusKeys = new[] { "group:Support", "team:scope-support" } }); await Stock("84");
        var single = Pilot(); var singleHero = Hero(single);
        Require(single.GetProperty("weatherScope").GetProperty("mode").GetString() == "team"
            && singleHero.GetProperty("content").GetProperty("value").GetDouble() == 84
            && singleHero.GetProperty("settings").GetProperty("warningValue").GetDouble() == 10
            && singleHero.GetProperty("settings").GetProperty("criticalValue").GetDouble() == 20
            && await HeroCard().GetAttributeAsync("data-weather-state") == "rain"
            && await page.Locator("[data-filter-teams]").InnerTextAsync() == "Équipes (1)",
            "A legacy group key and its current team key normalize to one selection: the 84-ticket hero uses Support's actual thresholds.");
        Require(Team(single, "scope-support").GetProperty("weatherContext").GetProperty("representedByHero").GetBoolean()
            && !Team(single, "scope-network").GetProperty("weatherContext").GetProperty("applicable").GetBoolean()
            && await page.Locator("[data-weather-role=team]").CountAsync() == 0
            && await page.Locator(".business-weather-card[data-weather-state=sun]").CountAsync() == 0,
            "The represented Support card is not duplicated and the excluded network is not displayed as a misleading healthy zero.");
        await Capture("scope-support-84-light-1280");
        await InspectWeatherCard("scope-support-84-light", thresholds: true);
        await page.GetByRole(AriaRole.Button, new() { Name = "Composer", Exact = true }).ClickAsync();
        Require(await page.Locator("[data-weather-placeholder]").CountAsync() >= 2
            && await page.Locator("[data-weather-placeholder] .analytics-metric-value").CountAsync() == 0
            && await HeroCard().EvaluateAsync<bool>("node=>node.style.getPropertyValue('--widget-height')==='240px'"),
            "Composer retains neutral placeholders for scoped-out weather without inventing zero values or discarding a saved custom height.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Terminer la composition", Exact = true }).ClickAsync();
        var rawBoard = (await Call(app, "dashboard.layout.get", new { businessView = "workspace" })).GetProperty("boards")[0];
        var rawHero = rawBoard.GetProperty("widgets").EnumerateArray().Single(widget => widget.GetProperty("id").GetString() == storedHero.Id);
        Require(rawHero.GetProperty("warningValue").GetDouble() == 100 && rawHero.GetProperty("criticalValue").GetDouble() == 200
            && rawHero.GetProperty("title").GetString() == "Titre fictif personnalisé" && rawHero.GetProperty("height").GetInt32() == 240,
            "Effective team thresholds are transient: the stored service rule, title and geometry remain unchanged.");

        await HeroCard().Locator(".analytics-metric-value").ClickAsync();
        var rowsDialog = page.Locator("dialog .analytics-row-dialog");
        await WaitForPageAsync(page, "document.querySelectorAll('dialog .analytics-row-dialog tbody tr').length===50");
        var first = await rowsDialog.Locator("tbody tr td:nth-child(2)").EvaluateAllAsync<int[]>("cells=>cells.map(cell=>Number(cell.textContent.trim()))");
        await rowsDialog.GetByRole(AriaRole.Button, new() { Name = "Suivant", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelectorAll('dialog .analytics-row-dialog tbody tr').length===34");
        var second = await rowsDialog.Locator("tbody tr td:nth-child(2)").EvaluateAllAsync<int[]>("cells=>cells.map(cell=>Number(cell.textContent.trim()))");
        Require(first.Concat(second).SequenceEqual(Enumerable.Range(10001, 84)),
            "The coherent 84-ticket weather hero opens precisely its 84 source IDs, excluding closed rows and without repeated multigroup occurrences.");
        await page.Keyboard.PressAsync("Escape");
        await page.Locator("[data-section-key=teams]").GetByRole(AriaRole.Button, new() { Name = "Voir sur la carte", Exact = true }).ClickAsync();
        await WaitForNavigationTargetAsync(page, "terrain-map"); await MapStock(84);
        Require((await MapIds()).SequenceEqual(Enumerable.Range(10001, 84))
            && Map().GetProperty("totals").GetProperty("placed").GetInt32() == 84,
            "The actual Pilotage-to-Terrain action preserves the same 84 source IDs and their referenced positions.");
        await page.Locator(".maps-scope-toolbar").GetByRole(AriaRole.Button, new() { Name = "Équipes (1)", Exact = true }).ClickAsync();
        var terrainPicker = page.GetByRole(AriaRole.Dialog, new() { Name = "Équipes", Exact = true });
        Require(await terrainPicker.GetByRole(AriaRole.Checkbox).CountAsync() == 3
            && await terrainPicker.GetByRole(AriaRole.Checkbox, new() { Name = "Support", Exact = true }).IsCheckedAsync()
            && await terrainPicker.GetByRole(AriaRole.Checkbox, new() { Name = "Groupe isolé", Exact = true }).CountAsync() == 1,
            "Terrain uses the same unique team catalogue and retains an uncovered raw group as a useful choice.");
        await terrainPicker.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await DeepLink("terrain-map", new { focusKeys = new[] { "group:Support", "team:scope-support" } }); await MapStock(84);
        Require(await page.Locator(".maps-scope-toolbar").GetByRole(AriaRole.Button, new() { Name = "Équipes (1)", Exact = true }).CountAsync() == 1
            && (await MapIds()).SequenceEqual(Enumerable.Range(10001, 84)),
            "A legacy Terrain deep link keeps its tickets while its equivalent team/group keys collapse to one displayed choice.");
        await ChooseTerrain("Support", "Réseau"); await MapStock(96);
        Require((await MapIds()).SequenceEqual(Enumerable.Range(10001, 96))
            && Map().GetProperty("totals").GetProperty("tickets").GetInt32() == 96,
            "Terrain counts the Support/Network union once per source row: 84 plus 18 minus six shared occurrences equals 96.");
        await Capture("scope-terrain-union-light-1280");
        await ChooseTerrain("Groupe isolé"); await MapStock(4);
        Require((await MapIds()).SequenceEqual(Enumerable.Range(10097, 4)),
            "An observed group without a configured team remains selectable and reaches its four exact source tickets.");
        await ChooseTerrain(); await MapStock(100);
        Require(Map().GetProperty("filters").GetProperty("focusKeys").GetArrayLength() == 0 && (await MapIds()).Length == 100,
            "All removes the team restriction in Terrain while closed tickets remain excluded from its active stock.");

        await Navigate("pilot"); await ChoosePilot("Réseau"); await Stock("18");
        var overlap = Pilot();
        Require(Neutral(Hero(overlap)) && Team(overlap, "scope-network").GetProperty("content").GetProperty("value").GetDouble() == 12
            && !Team(overlap, "scope-network").GetProperty("weatherContext").GetProperty("representedByHero").GetBoolean()
            && await page.Locator("[data-weather-role=team][data-weather-represented=false]").CountAsync() == 1
            && !string.IsNullOrWhiteSpace(overlap.GetProperty("weatherScope").GetProperty("reason").GetString()),
            "When first-team precedence leaves only 12 of 18 network tickets in its weather rule, the scope hero stays neutral and the distinct 12-ticket rule remains visible.");
        await Capture("scope-overlap-light-1280");
        await ChoosePilot("Support"); await Stock("84");
        await page.GetByLabel("Technicien du périmètre", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Agent fictif A" });
        await Stock("42");
        Require(Pilot().GetProperty("weatherScope").GetProperty("mode").GetString() == "filtered" && Neutral(Hero(Pilot()))
            && Neutral(Team(Pilot(), "scope-support")) && Team(Pilot(), "scope-support").GetProperty("content").GetProperty("value").GetDouble() == 42,
            "A 42-ticket technician subset does not reuse the 84-ticket team's calibrated weather verdict.");
        await page.GetByLabel("Technicien du périmètre", new() { Exact = true }).SelectOptionAsync(""); await Stock("84");
        await page.GetByRole(AriaRole.Button, new() { Name = "Filtres", Exact = true }).ClickAsync();
        var filterDialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Périmètre commun à toutes les analyses", Exact = true });
        var filterEditor = filterDialog.Locator(".analytics-filter-editor").First;
        await filterEditor.GetByRole(AriaRole.Button, new() { Name = "+ Condition", Exact = true }).ClickAsync();
        await filterEditor.GetByLabel("Source", new() { Exact = true }).SelectOptionAsync("type");
        await filterEditor.GetByLabel("Valeurs acceptées · une par ligne", new() { Exact = true }).FillAsync("Incident");
        await filterDialog.GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync(); await Stock("42"); await Settled();
        Require(Pilot().GetProperty("weatherScope").GetProperty("mode").GetString() == "filtered" && Neutral(Hero(Pilot()))
            && Neutral(Team(Pilot(), "scope-support")) && Team(Pilot(), "scope-support").GetProperty("content").GetProperty("value").GetDouble() == 42,
            "A persistent incident-only filter keeps its real 42-ticket stock but suppresses the unrelated whole-team threshold verdict.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Réinitialiser", Exact = true }).ClickAsync(); await Stock("100"); await Settled();
        Require(Pilot().GetProperty("weatherScope").GetProperty("mode").GetString() == "service"
            && Hero(Pilot()).GetProperty("settings").GetProperty("warningValue").GetDouble() == 100
            && await HeroCard().GetAttributeAsync("data-weather-state") == "sun"
            && await page.Locator("[data-weather-role=team]").CountAsync() == 2,
            "Reset restores the full service and its strict 100/200 thresholds, together with both configured team cards.");
        Require(JsonSerializer.Serialize(app.Settings.Dashboard.Business.WeatherRules, PilotApplication.Json) == storedRules,
            "Browsing the scopes, comparison placeholders and map has not rewritten the user's weather rules.");
        await ChoosePilot("Support"); await Stock("84");
        await Call(app, "app.appearance", new { theme = "dark" }); await delivery;
        await Capture("scope-support-84-dark-1280");
        await ChoosePilot("Support", "Réseau"); await Stock("96");
        await Capture("scope-multiple-dark-1280");
        await InspectWeatherCard("scope-multiple-dark", thresholds: false);
        var beforeRetiredKey = Pilot().GetProperty("runId").GetString();
        await DeepLink("pilot", new { focusKey = "group:Équipe retirée du relevé" });
        Require(await page.GetByRole(AriaRole.Heading, new() { Name = "Équipes à actualiser", Exact = true }).IsVisibleAsync()
            && await HeroCard().CountAsync() == 0 && Pilot().GetProperty("runId").GetString() == beforeRetiredKey,
            "A retired legacy group key stops the figures with an explicit scope warning instead of silently widening them to all teams.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Afficher toutes les équipes", Exact = true }).ClickAsync(); await Stock("100"); await Settled();
        Require(Pilot().GetProperty("weatherScope").GetProperty("mode").GetString() == "service",
            "The user can explicitly recover from a retired group selection by choosing all teams.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Quitter la simulation", Exact = true }).ClickAsync();
        await WaitForNavigationTargetAsync(page, "home"); await Settled();
        Require(!app.IsTestMode && JsonSerializer.Serialize(app.Settings, PilotApplication.Json) == normalSettings,
            "All regression settings and terrain operations remain inside simulation and restore the normal profile on exit.");
        Require(externalRequests == 0 && tileRequests > 0 && errors.IsEmpty,
            "The real Pilotage/Terrain regression uses the C# bridge and only intercepted, permitted tile images, with no external data reads or JavaScript error."
            + (errors.IsEmpty ? "" : "\n" + string.Join("\n", errors)));
        await delivery;
    }

    private static string CreateWeatherScopeCsv()
    {
        var path = Path.Combine(_root, "synthetic-weather-scope.csv");
        static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var rows = new List<string> { string.Join(';', LocalTestHeaders.Select(Csv)) };
        for (var index = 1; index <= 104; index++)
        {
            var support = index <= 84 || index > 100;
            var group = index <= 6 ? "Support\nRéseau" : support ? "Support" : index <= 96 ? "Réseau" : "Groupe isolé";
            var row = new[] { (10000 + index).ToString(CultureInfo.InvariantCulture),
                $"Incident fictif {index:000} · {(support ? "Site fictif Cobalt" : "Site fictif Turquoise")}",
                index > 100 ? "Clos" : new[] { "Nouveau", "En cours (attribué)", "En cours (planifié)", "En attente" }[(index - 1) % 4],
                DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), DateTime.Today.AddDays(-index).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), "Moyenne",
                "Demandeur fictif", index <= 42 ? "Agent fictif A" : "Agent fictif B", "Catégorie fictive",
                "Rédacteur fictif", "", "", "", index % 2 == 0 ? "Incident" : "Demande", group,
                index.ToString(CultureInfo.InvariantCulture), index <= 6 ? "2" : "1", "1", "Données entièrement synthétiques pour la régression des périmètres météo." };
            rows.Add(string.Join(';', row.Select(Csv)));
        }
        File.WriteAllLines(path, rows, new UTF8Encoding(true)); return path;
    }
}
