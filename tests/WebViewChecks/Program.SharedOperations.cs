using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private const string SharedNote = "Note partagée <b>littérale</b>\nDeuxième ligne. null";
    private const string PrivateChecklist = "PRIVATE_CHECKLIST_SYNTHETIC";
    private const string PrivateDraft = "PRIVATE_DRAFT_SYNTHETIC";
    private const string PrivateTechnicianNote = "PRIVATE_TECHNICIAN_NOTE_SYNTHETIC";
    private const string PrivateAvailability = "PRIVATE_AVAILABILITY_SYNTHETIC";

    private static async Task CheckSharedOperationsInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        new LibraryPreferencesStore().Save(new() { LocalFolder = Path.Combine(_root, "Library"), DisplayName = "Technicien synthétique" });
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(12000);
        var errors = new ConcurrentQueue<string>(); var externalRequests = 0;
        var replies = new ConcurrentQueue<(string Operation, JsonElement Result)>();
        page.PageError += (_, error) => errors.Enqueue(error);
        page.Console += (_, message) => { if (message.Type == "error") errors.Enqueue(message.Text); };
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page }; WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        const string sourceUrl = "https://shared-operation-fixture.invalid/front/ticket.php";
        var settings = new AppSettings
        {
            Theme = "dark", PortalUrl = sourceUrl, ExportFolder = Path.Combine(_root, "Exports"),
            Cities = new() { Cities = [new() { Id = "11111111111111111111111111111111", Name = "Alpha", Latitude = 50, Longitude = 2 }] }
        };
        settings.TicketDetails.AutoLoadOnSelection = false;
        settings.Dashboard.ColumnMappings = new(StringComparer.Ordinal)
        { ["id"] = "ID", ["title"] = "Titre", ["status"] = "Statut", ["priority"] = "Priorité", ["assignee"] = "Technicien", ["location"] = "Lieu", ["category"] = "Catégorie" };
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), settings);
        app.LoadQualityFixture(new()
        {
            Title = "Travail partagé synthétique", SourceUrl = sourceUrl, Complete = true, ExpectedTotal = 2, ExtractedAt = DateTimeOffset.UtcNow,
            Headers = ["ID", "Titre", "Statut", "Priorité", "Technicien", "Lieu", "Catégorie"],
            Rows = [new[] { "8200001", "Alpha · réseau", "Nouveau", "Haute", "Alice", "Alpha", "Réseau" },
                    new[] { "8200002", "Alpha · terminal", "En cours (attribué)", "Moyenne", "Bob", "Alpha", "Matériel" }],
            Provenance = new() { SourceKind = "glpi", RequestedUrl = sourceUrl, SearchVerified = true, ScopeVerified = true }
        });
        var opened = await Call(app, "workplace.ticket.open", new { rowIndex = 0, revision = app.Revision });
        var ticketKey = opened.GetProperty("personal").GetProperty("key").GetString()!;
        await Call(app, "workplace.personal.save", new
        {
            ticketKey, notes = "Note locale initiale", pinned = true, queued = true,
            checklist = new[] { new { text = PrivateChecklist, done = true } }
        });
        await Call(app, "workplace.draft.save", new { ticketKey, text = PrivateDraft });
        var sharedPreference = new TechnicianPreference
        {
            Favorite = true, ActiveTarget = 9, Notes = PrivateTechnicianNote, AvailabilityNote = PrivateAvailability,
            DailyCapacityMinutes = 240, EstimatedTicketMinutes = 30, Skills = ["Réseau"], PrimaryTerritories = ["Alpha"]
        };
        await Call(app, "workplace.technicians.save", new { technicianKey = "Alice", preference = sharedPreference });
        await Call(app, "workplace.technicians.save", new { technicianKey = "Bob", preference = new TechnicianPreference { DailyCapacityMinutes = 240, Skills = ["Matériel"] } });
        await Call(app, "workplace.planning.save", new { name = "Répartition partagée synthétique", date = "2030-01-07" });
        await Call(app, "workplace.planning.assign", new { revision = app.Revision, ticketKey, technicianKey = "Alice", minutes = 45, note = "Préparer le contrôle réseau", requiredSkills = Array.Empty<string>() });
        await Call(app, "terrain.tours.select", new { revision = app.Revision, rows = new[] { 0 }, selected = true });
        var tour = await Call(app, "terrain.tours.create", new { revision = app.Revision, name = "Tournée partagée synthétique", date = "2030-01-07", technician = "Alice" });
        tour = await Call(app, "terrain.tours.save", new { revision = app.Revision, draftVersion = tour.GetProperty("draftVersion").GetInt32() });
        var originalTourId = tour.GetProperty("draft").GetProperty("id").GetString()!;

        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            var result = await app.ExecuteAsync(op, data, ct);
            replies.Enqueue((op, JsonSerializer.SerializeToElement(result, PilotApplication.Json)));
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
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        async Task Ready(string id) => await WaitForPageAsync(page,
            "id => document.querySelector('.page-content')?.dataset.page===id && !document.querySelector('#content').hasAttribute('aria-busy')", id);
        async Task Go(string id) { await page.Locator(".nav-item[data-page=" + id + "]").ClickAsync(); await Ready(id); }
        async Task Idle() => await WaitForPageAsync(page, "!document.querySelector('.page-content button[aria-busy=true]')");
        async Task<TeamWorkspaceItem> Published(string operation)
        {
            await Until(() => replies.Any(reply => reply.Operation == operation));
            await Idle();
            return replies.Last(reply => reply.Operation == operation).Result.Deserialize<TeamWorkspaceItem>(PilotApplication.Json)!;
        }
        async Task Capture(string name)
        {
            var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return;
            var directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
            await page.Locator("#notifications .toast button").EvaluateAllAsync<int>("buttons=>{buttons.forEach(button=>button.click());return buttons.length;}");
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        }
        async Task<ILocator> Preview(TeamWorkspaceItem item, string title)
        {
            await Go("library");
            await page.GetByRole(AriaRole.Navigation, new() { Name = "Vues de la bibliothèque", Exact = true })
                .GetByRole(AriaRole.Button, new() { Name = "Équipe", Exact = true }).ClickAsync();
            var card = page.Locator(".library-result").Filter(new()
            { Has = page.GetByRole(AriaRole.Heading, new() { Name = item.Title, Exact = true }) });
            await card.GetByRole(AriaRole.Button, new() { Name = "Examiner et reprendre", Exact = true }).ClickAsync();
            var modal = page.GetByRole(AriaRole.Dialog, new() { Name = title, Exact = true });
            await modal.WaitForAsync(); return modal;
        }
        async Task<JsonElement> Apply(ILocator modal, string label, string route)
        {
            var count = replies.Count(reply => reply.Operation == "workspace.operation.apply");
            await modal.GetByRole(AriaRole.Button, new() { Name = label, Exact = true }).ClickAsync();
            await Until(() => replies.Count(reply => reply.Operation == "workspace.operation.apply") == count + 1);
            await Ready(route);
            return replies.Last(reply => reply.Operation == "workspace.operation.apply").Result;
        }

        await page.GotoAsync("https://glpi-pilot.local/index.html"); await page.Locator("body[data-ready=true]").WaitForAsync();
        await Go("tickets"); await Button("Mon travail").ClickAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Mes notes", Exact = true }).FillAsync(SharedNote);
        await Button("Enregistrer et partager la note").ClickAsync();
        var noteItem = await Published("workspace.publishPersonalNote");
        var personal = (await Call(app, "workplace.ticket.open", new { ticketKey })).GetProperty("personal");
        var noteStrings = SharedStrings(JsonSerializer.SerializeToElement(noteItem, PilotApplication.Json)).ToArray();
        Require(noteItem.Kind == "personal-note" && noteStrings.Any(value => value.Contains(SharedNote, StringComparison.Ordinal))
            && noteStrings.All(value => !value.Contains(PrivateChecklist) && !value.Contains(PrivateDraft))
            && personal.GetProperty("notes").GetString() == SharedNote && personal.GetProperty("pinned").GetBoolean(),
            "The ticket editor saves and publishes the exact note through its real action while private checklist, draft and pinning stay on the workstation.");

        await Go("tours"); await Button("Planning daté").ClickAsync(); await Idle();
        await Button("Enregistrer et partager la tournée").ClickAsync();
        var tourItem = await Published("workspace.publishTour");
        Require(tourItem.Kind == "tour" && tourItem.Payload is not null
            && SharedStrings(tourItem.Payload.Value).Contains(originalTourId)
            && ScheduledTourStore.Load(Path.Combine(_root, "scheduled-tours.json")).Tours.Single().Tickets.Count == 1,
            "The tour editor publishes the saved tour with its real ticket references and typed payload.");

        await Go("planning"); await Button("Enregistrer et partager la répartition").ClickAsync();
        var dispatchItem = await Published("workspace.publishDispatch");
        Require(dispatchItem.Kind == "dispatch" && dispatchItem.Payload is not null
            && SharedStrings(dispatchItem.Payload.Value).Contains("Préparer le contrôle réseau")
            && TechnicianPlanningService.Load(Path.Combine(_root, "dispatch-scenario.json")).Assignments.Single().EstimatedMinutes == 45,
            "The planning page publishes its saved local assignment scenario, including the proposed technician and duration.");

        await Go("technicians"); await Button("Partager les compétences et capacités").ClickAsync();
        var technicianItem = await Published("workspace.publishTechnicians");
        var technicianStrings = SharedStrings(JsonSerializer.SerializeToElement(technicianItem, PilotApplication.Json)).ToArray();
        Require(technicianItem.Kind == "technicians" && technicianItem.Payload is not null && technicianStrings.Contains("Réseau")
            && technicianStrings.All(value => !value.Contains(PrivateTechnicianNote) && !value.Contains(PrivateAvailability))
            && (await Call(app, "workspace.list")).GetProperty("total").GetInt64() == 4,
            "The technicians page shares declared business skills and capacities while excluding private technician notes and availability annotations.");

        const string recipientNote = "Note locale à conserver.";
        await Call(app, "workplace.personal.save", new { ticketKey, notes = recipientNote });
        var notePreview = await Preview(noteItem, "Reprendre la note partagée");
        var beforeApplyNote = WorkplaceStore.Load(Path.Combine(_root, "workplace.json")).Tickets.Single(value => value.Key == ticketKey).Notes;
        var noteReviewLiteral = await notePreview.Locator("tbody tr").Last.Locator("td").Last.TextContentAsync() == SharedNote
            && await notePreview.Locator("b").CountAsync() == 0;
        await Capture("shared-note-review");
        await Apply(notePreview, "Ajouter à ma note", "tickets");
        personal = (await Call(app, "workplace.ticket.open", new { ticketKey })).GetProperty("personal");
        Console.WriteLine("NOTE RECEIPT: " + JsonSerializer.Serialize(new
        {
            literalPreview = noteReviewLiteral, previewReadOnly = beforeApplyNote == recipientNote,
            priorRetained = personal.GetProperty("notes").GetString()!.Contains(recipientNote),
            incomingRetained = personal.GetProperty("notes").GetString()!.Contains(SharedNote),
            pinned = personal.GetProperty("pinned").GetBoolean(), checklistRetained = SharedStrings(personal).Contains(PrivateChecklist)
        }));
        Require(noteReviewLiteral && beforeApplyNote == recipientNote && personal.GetProperty("notes").GetString()!.Contains(recipientNote)
            && personal.GetProperty("notes").GetString()!.Contains(SharedNote) && personal.GetProperty("pinned").GetBoolean()
            && SharedStrings(personal).Contains(PrivateChecklist),
            "The team preview leaves the existing note untouched; explicit acceptance appends the shared text and preserves private pinning and checklist.");

        var tourPreview = await Preview(tourItem, "Reprendre la tournée partagée");
        var countBeforeTourApply = ScheduledTourStore.Load(Path.Combine(_root, "scheduled-tours.json")).Tours.Count;
        var appliedTour = await Apply(tourPreview, "Créer une tournée brouillon", "tours");
        var copiedTourId = appliedTour.GetProperty("params").GetProperty("tourId").GetString()!;
        var tours = ScheduledTourStore.Load(Path.Combine(_root, "scheduled-tours.json")).Tours;
        Require(countBeforeTourApply == 1 && tours.Count == 2 && copiedTourId != originalTourId
            && tours.Single(value => value.Id == copiedTourId).State == "draft"
            && tours.Single(value => value.Id == copiedTourId).Tickets.Count == 1
            && tours.Any(value => value.Id == originalTourId),
            "Accepting a shared tour creates a new local draft with its tickets while the original saved tour remains intact.");

        await Call(app, "workplace.planning.save", new { name = "Scénario local à conserver", clear = true, confirmed = true });
        await Call(app, "workplace.planning.assign", new { revision = app.Revision, ticketKey, technicianKey = "Bob", minutes = 27, note = "LOCAL_SCENARIO_SYNTHETIC", requiredSkills = Array.Empty<string>() });
        var scenarioPath = Path.Combine(_root, "dispatch-scenario.json");
        var scenarioBefore = await File.ReadAllBytesAsync(scenarioPath);
        var dispatchPreview = await Preview(dispatchItem, "Reprendre la répartition partagée");
        var dispatchUnchanged = Enumerable.SequenceEqual(scenarioBefore, await File.ReadAllBytesAsync(scenarioPath));
        await Capture("shared-dispatch-review");
        var appliedDispatch = await Apply(dispatchPreview, "Remplacer mon scénario local", "planning");
        var dispatchBackup = Path.Combine(_root, "SharedImports", appliedDispatch.GetProperty("backupName").GetString()!);
        var appliedScenario = TechnicianPlanningService.Load(scenarioPath);
        Require(dispatchUnchanged && Enumerable.SequenceEqual(scenarioBefore, await File.ReadAllBytesAsync(dispatchBackup))
            && appliedScenario.Name == "Répartition partagée synthétique"
            && appliedScenario.Assignments.Single().EstimatedMinutes == 45
            && appliedScenario.Assignments.Single().TechnicianKey == TechnicianDashboardService.IdentityKey("Alice"),
            "Replacing a scenario requires its explicit team review and preserves an exact file backup of the previous local scenario before applying the shared proposals.");

        var recipientPreference = sharedPreference.Copy();
        recipientPreference.Favorite = false; recipientPreference.ActiveTarget = 17;
        recipientPreference.Notes = "RECIPIENT_PRIVATE_NOTE"; recipientPreference.AvailabilityNote = "RECIPIENT_PRIVATE_AVAILABILITY";
        recipientPreference.Skills = ["Compétence locale précédente"]; recipientPreference.DailyCapacityMinutes = 90;
        await Call(app, "workplace.technicians.save", new { technicianKey = "Alice", preference = recipientPreference });
        var preferencePath = Path.Combine(_root, "technician-preferences.json");
        var preferencesBefore = await File.ReadAllBytesAsync(preferencePath);
        var technicianPreview = await Preview(technicianItem, "Reprendre les compétences et capacités");
        var preferencesUnchanged = Enumerable.SequenceEqual(preferencesBefore, await File.ReadAllBytesAsync(preferencePath));
        var appliedTechnicians = await Apply(technicianPreview, "Appliquer les données métier", "technicians");
        var technicianBackup = Path.Combine(_root, "SharedImports", appliedTechnicians.GetProperty("backupName").GetString()!);
        var received = TechnicianPreferences.Load(preferencePath).Get("Alice");
        Require(preferencesUnchanged && Enumerable.SequenceEqual(preferencesBefore, await File.ReadAllBytesAsync(technicianBackup))
            && received.Skills.SequenceEqual(["Réseau"]) && received.DailyCapacityMinutes == 240
            && !received.Favorite && received.ActiveTarget == 17 && received.Notes == recipientPreference.Notes
            && received.AvailabilityNote == recipientPreference.AvailabilityNote
            && externalRequests == 0 && errors.IsEmpty
            && !replies.Any(reply => reply.Operation is "workplace.ticket.read" or "workplace.ticket.browser" or "workplace.ticket.tab"
                or "workplace.actions.inspect" or "workplace.actions.preview" or "workplace.actions.confirm"),
            "Receiving shared skills updates business declarations after review, preserves personal favorites, objectives and notes, retains an exact backup, and performs no GLPI read or write.");
        await Capture("shared-technicians-applied");
        await delivery;
    }

    private static IEnumerable<string> SharedStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) foreach (var text in SharedStrings(property.Value)) yield return text;
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var entry in value.EnumerateArray()) foreach (var text in SharedStrings(entry)) yield return text;
    }
}
