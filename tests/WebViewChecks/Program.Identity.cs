using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckIdentityInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1440, Height = 980 }, Locale = "fr-FR", TimezoneId = "Europe/Paris", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var mutations = new ConcurrentQueue<string>(); var externalRequests = 0;
        (TaskCompletionSource Started, TaskCompletionSource Release)? heldPersonalSave = null;
        page.PageError += (_, error) => errors.Enqueue(error);
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            { await route.FulfillAsync(new() { Status = 404 }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page }; WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json=>window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        var settings = new AppSettings { Theme = "light", PortalUrl = "https://identity-fixture.invalid/front/ticket.php", ExportFolder = Path.Combine(_root, "Exports") };
        settings.Interface.OnboardingCompleted = true;
        settings.Dashboard.ColumnMappings = new() { ["id"] = "ID", ["title"] = "Titre", ["status"] = "Statut", ["assignee"] = "Technicien" };
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), settings);
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            if (op is "identity.set" or "identity.create" or "identity.clear") mutations.Enqueue(op);
            if (op == "workplace.personal.save" && heldPersonalSave is { } held)
            {
                heldPersonalSave = null; held.Started.TrySetResult();
                // Hold admission to the real store, so a context change cannot
                // pass merely because an earlier save response was delayed.
                await held.Release.Task.WaitAsync(ct);
            }
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            return await app.ExecuteAsync(op, data, ct);
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (()=>{const listeners=new Set();window.chrome||={};window.chrome.webview={
              postMessage:message=>window.__pilotSend(JSON.stringify(message)),
              addEventListener:(name,handler)=>{if(name==='message')listeners.add(handler);},
              removeEventListener:(name,handler)=>{if(name==='message')listeners.delete(handler);}
            };window.__pilotDeliver=message=>{for(const listener of listeners)listener({data:message});};})();
            """);
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        ILocator IdentityDialog() => page.Locator("dialog.identity-dialog");
        ILocator Search() => IdentityDialog().GetByLabel("Rechercher votre nom", new() { Exact = true });
        Task<JsonElement> Identity() => Call(app, "identity.get", new { currentOnly = true });
        async Task Ready(string route) => await WaitForNavigationTargetAsync(page, route);
        async Task Navigate(string route) { await ClickNavigationAsync(page, route); await Ready(route); }
        async Task OpenIdentity()
        {
            await page.Locator("#identity-trigger").ClickAsync(); await IdentityDialog().WaitForAsync(); await Search().WaitForAsync();
            await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
        }
        async Task WaitCandidates(params string[] keys) => await WaitForPageAsync(page, """
            keys=>JSON.stringify([...document.querySelectorAll('dialog.identity-dialog article[data-identity-key]')].map(node=>node.dataset.identityKey).sort())===JSON.stringify([...keys].sort())
            """, keys);
        async Task Choose(string key)
        {
            await IdentityDialog().Locator($"article[data-identity-key='{key}']").GetByRole(AriaRole.Button, new() { Name = "C’est moi : ", Exact = false }).ClickAsync();
            await IdentityDialog().WaitForAsync(new() { State = WaitForSelectorState.Detached });
        }
        async Task WaitRows(params string[] ids) => await WaitForPageAsync(page, """
            ids=>JSON.stringify([...document.querySelectorAll('.tickets-record-grid .tickets-row-id')].map(node=>node.textContent).sort())===JSON.stringify([...ids].sort())
            """, ids);
        async Task Capture(string name)
        {
            var argument = Array.IndexOf(args, "--screenshots"); if (argument < 0 || argument + 1 >= args.Length) return;
            Directory.CreateDirectory(args[argument + 1]);
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[argument + 1], name + ".png"), FullPage = true });
        }

        await page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(page); await Ready("home");
        Require(await page.Locator("#identity-trigger").GetAttributeAsync("aria-label") == "Choisir mon identité"
            && await page.Locator("#identity-trigger").IsVisibleAsync() && (await Identity()).GetProperty("current").ValueKind == JsonValueKind.Null,
            "An unconfigured installation exposes the identity choice directly before any ticket data or account is present.");
        await OpenIdentity(); await Capture("identity-empty-light");
        Require(await IdentityDialog().GetByRole(AriaRole.Heading, new() { Name = "Mon identité", Exact = true }).IsVisibleAsync()
            && await IdentityDialog().Locator("article[data-identity-key]").CountAsync() == 0 && mutations.IsEmpty,
            "Opening the empty identity chooser does not silently choose a person or write local settings.");
        const string declaredName = "Noémie Hors-Relevé";
        await IdentityDialog().GetByLabel("Nom tel qu’il apparaît dans les tickets", new() { Exact = true }).FillAsync(declaredName);
        await IdentityDialog().GetByRole(AriaRole.Button, new() { Name = "Enregistrer mon identité", Exact = true }).ClickAsync();
        await IdentityDialog().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Ready("home");
        var declared = (await Identity()).GetProperty("current"); var declaredKey = declared.GetProperty("key").GetString()!;
        Require(declared.GetProperty("label").GetString() == declaredName && declaredKey.Length > 0 && app.Result is null
            && AppSettings.Load().Dashboard.PersonalTechnician == declaredKey
            && await page.Locator("#identity-trigger").GetAttributeAsync("aria-label") == "Mon identité : " + declaredName,
            "An explicit name can be declared without a source or a full availability form and is saved in the actual local profile.");
        await page.ReloadAsync(); await ReadyAsync(page); await Ready("home");
        Require((await Identity()).GetProperty("current").GetProperty("key").GetString() == declaredKey
            && await page.Locator("#identity-trigger").GetAttributeAsync("aria-label") == "Mon identité : " + declaredName
            && AppSettings.Load().Dashboard.PersonalTechnician == declaredKey,
            "Reloading the real interface retains the declared identity and its readable label.");
        var beforeCancel = mutations.Count;
        await OpenIdentity(); await Search().FillAsync("Personne absente pour annuler"); await WaitCandidates();
        await IdentityDialog().GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).Last.ClickAsync();
        await IdentityDialog().WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Require(mutations.Count == beforeCancel && (await Identity()).GetProperty("current").GetProperty("key").GetString() == declaredKey
            && AppSettings.Load().Dashboard.PersonalTechnician == declaredKey,
            "Searching and cancelling leaves the previous identity and saved profile unchanged.");
        await OpenIdentity(); await IdentityDialog().GetByRole(AriaRole.Button, new() { Name = "Retirer mon identité", Exact = true }).ClickAsync();
        await IdentityDialog().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Ready("home");
        Require((await Identity()).GetProperty("current").ValueKind == JsonValueKind.Null && AppSettings.Load().Dashboard.PersonalTechnician.Length == 0
            && await page.Locator("#identity-trigger").GetAttributeAsync("aria-label") == "Choisir mon identité",
            "Removing an identity clears the saved personal filter without selecting a replacement person.");

        app.LoadQualityFixture(IdentitySource()); await delivery; await Navigate("records"); await WaitRows("#101", "#102", "#201", "#301", "#401");
        await Button("Mes tickets").ClickAsync(); await IdentityDialog().WaitForAsync(); await Search().WaitForAsync();
        Require(await page.Locator(".page-content[data-page=records]").CountAsync() == 1 && (await Identity()).GetProperty("current").ValueKind == JsonValueKind.Null,
            "Mes tickets opens the identity chooser inside Tickets without detouring through calendar administration.");
        const string accentedKey = "ÉLODIE MARTIN", plainKey = "ELODIE MARTIN";
        await Search().FillAsync("elodie"); await WaitCandidates(accentedKey, plainKey); await Capture("identity-search-light");
        Require(await IdentityDialog().Locator($"article[data-identity-key='{accentedKey}']").GetByText("Élodie Martin", new() { Exact = true }).IsVisibleAsync()
            && await IdentityDialog().Locator($"article[data-identity-key='{plainKey}']").GetByText("Elodie Martin", new() { Exact = true }).IsVisibleAsync()
            && (await Identity()).GetProperty("current").ValueKind == JsonValueKind.Null,
            "Accent-insensitive discovery keeps accented and unaccented identities distinct and awaits an explicit choice.");
        await Choose(accentedKey); await Ready("records"); await WaitRows("#101", "#102");
        Require((await Identity()).GetProperty("current").GetProperty("key").GetString() == accentedKey
            && AppSettings.Load().Dashboard.PersonalTechnician == accentedKey
            && (await page.Locator(".tickets-record-grid .tickets-row-id").AllTextContentsAsync()).SequenceEqual(new[] { "#101", "#102" }),
            "Choosing the accented identity returns to Tickets and displays exactly its two real source rows.");
        await OpenIdentity(); await Search().FillAsync("elodie"); await WaitCandidates(accentedKey, plainKey); await Choose(plainKey); await Ready("records"); await WaitRows("#201");
        Require((await Identity()).GetProperty("current").GetProperty("key").GetString() == plainKey
            && await page.Locator("#identity-trigger").GetAttributeAsync("aria-label") == "Mon identité : Elodie Martin"
            && await page.Locator(".tickets-record-grid tbody tr").CountAsync() == 1,
            "Changing the identity from the global control refreshes the personal ticket scope without merging a similar name.");

        await page.Locator(".tickets-record-grid tbody tr").First.GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).ClickAsync();
        await page.Locator(".tickets-detail-pane .work-ticket-heading h2").WaitForAsync();
        await Button("Notes et rappels").ClickAsync();
        const string pendingNote = "Note conservée avant changement d’identité";
        await page.GetByLabel("Mes notes", new() { Exact = true }).FillAsync(pendingNote);
        var ticketKey = (await Call(app, "workplace.get")).GetProperty("activeTicketKey").GetString()!;
        var held = (Started: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), Release: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        heldPersonalSave = held;
        await Button("Enregistrer mon travail").ClickAsync(); await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var beforePendingChoice = mutations.Count;
        try
        {
            // Search is read-only and can finish while the old reader save is pending.
            await page.Locator("#identity-trigger").ClickAsync(); await IdentityDialog().WaitForAsync();
            await Search().FillAsync("elodie"); await WaitCandidates(accentedKey, plainKey);
            await IdentityDialog().Locator($"article[data-identity-key='{accentedKey}']").GetByRole(AriaRole.Button, new() { Name = "C’est moi : ", Exact = false }).ClickAsync();
            await WaitForPageAsync(page, "document.querySelector('.workspace')?.inert === true");
            Require(mutations.Count == beforePendingChoice && app.Settings.Dashboard.PersonalTechnician == plainKey,
                "An identity transition waits for the old ticket reader save before admitting the identity mutation.");
        }
        finally { held.Release.TrySetResult(); }
        await IdentityDialog().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Ready("records");
        var work = await Call(app, "workplace.get");
        Require(work.GetProperty("openTickets").EnumerateArray().Any(ticket => ticket.GetProperty("key").GetString() == ticketKey && ticket.GetProperty("notes").GetString() == pendingNote)
            && (await Identity()).GetProperty("current").GetProperty("key").GetString() == accentedKey,
            "The pending personal note is actually saved on its original ticket before the chosen identity changes.");

        await Navigate("data");
        var normalSettings = await File.ReadAllBytesAsync(Path.Combine(_root, "settings.json"));
        await OpenDemonstrationFromSettingsAsync(page); await Ready("home"); await OpenIdentity();
        await IdentityDialog().GetByRole(AriaRole.Button, new() { Name = "Mon nom n’est pas dans la liste", Exact = true }).ClickAsync();
        await IdentityDialog().GetByLabel("Nom tel qu’il apparaît dans les tickets", new() { Exact = true }).FillAsync("Identité de simulation");
        await IdentityDialog().GetByRole(AriaRole.Button, new() { Name = "Enregistrer mon identité", Exact = true }).ClickAsync();
        await IdentityDialog().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Ready("home");
        var settingsDuringSimulation = await File.ReadAllBytesAsync(Path.Combine(_root, "settings.json"));
        Require(app.IsTestMode && (await Identity()).GetProperty("current").GetProperty("label").GetString() == "Identité de simulation"
            && normalSettings.SequenceEqual(settingsDuringSimulation),
            "Choosing a simulation identity changes only the simulated profile and preserves the exact normal settings file.");
        await Button("Quitter la simulation").ClickAsync(); await Ready("pilot");
        var settingsAfterSimulation = await File.ReadAllBytesAsync(Path.Combine(_root, "settings.json"));
        Require(!app.IsTestMode && (await Identity()).GetProperty("current").GetProperty("key").GetString() == accentedKey
            && await page.Locator("#identity-trigger").GetAttributeAsync("aria-label") == "Mon identité : Élodie Martin"
            && normalSettings.SequenceEqual(settingsAfterSimulation),
            "Leaving simulation restores the normal identity, its header label and the original saved profile.");

        await Navigate("settings");
        var zoom = page.GetByLabel("Zoom (%)", new() { Exact = true });
        var savedZoom = app.Settings.InterfaceZoomPercent; await zoom.FillAsync("125");
        var beforeGuard = mutations.Count;
        await OpenIdentity(); await Search().FillAsync("elodie"); await WaitCandidates(accentedKey, plainKey);
        await IdentityDialog().Locator($"article[data-identity-key='{plainKey}']").GetByRole(AriaRole.Button, new() { Name = "C’est moi : Elodie Martin", Exact = true }).ClickAsync();
        var settingsGuard = page.GetByRole(AriaRole.Dialog, new() { Name = "Changer d’identité ?", Exact = true });
        await settingsGuard.WaitForAsync(); await Capture("identity-unsaved-settings");
        await settingsGuard.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await settingsGuard.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await IdentityDialog().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Ready("settings");
        Require(await zoom.InputValueAsync() == "125" && app.Settings.InterfaceZoomPercent == savedZoom
            && (await Identity()).GetProperty("current").GetProperty("key").GetString() == accentedKey && mutations.Count == beforeGuard,
            "Declining the identity change preserves unapplied settings, the previous identity and the untouched profile.");
        await OpenIdentity(); await Search().FillAsync("elodie"); await WaitCandidates(accentedKey, plainKey);
        await IdentityDialog().Locator($"article[data-identity-key='{plainKey}']").GetByRole(AriaRole.Button, new() { Name = "C’est moi : Elodie Martin", Exact = true }).ClickAsync();
        await settingsGuard.WaitForAsync();
        await settingsGuard.GetByRole(AriaRole.Button, new() { Name = "Continuer le changement", Exact = true }).ClickAsync();
        await settingsGuard.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await IdentityDialog().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Ready("settings");
        Require(await zoom.InputValueAsync() == savedZoom.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && app.Settings.InterfaceZoomPercent == savedZoom && (await Identity()).GetProperty("current").GetProperty("key").GetString() == plainKey,
            "Explicitly continuing discards only the unapplied form values and then changes the identity in the same workspace.");
        await zoom.FillAsync("110"); await Button("Appliquer").ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('#zoom-label').textContent.includes('110')");
        await OpenIdentity(); await Search().FillAsync("elodie"); await WaitCandidates(accentedKey, plainKey); await Choose(accentedKey); await Ready("settings");
        Require(app.Settings.InterfaceZoomPercent == 110 && await zoom.InputValueAsync() == "110"
            && (await Identity()).GetProperty("current").GetProperty("key").GetString() == accentedKey && await settingsGuard.CountAsync() == 0,
            "Already-applied settings survive the identity change without a redundant discard prompt.");

        await page.SetViewportSizeAsync(1000, 740); await page.EvaluateAsync("document.documentElement.dataset.theme='dark'");
        await OpenIdentity(); await Capture("identity-compact-dark");
        var compact = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth<=innerWidth+1&&document.querySelector('dialog.identity-dialog').getBoundingClientRect().width<=innerWidth");
        await page.Keyboard.PressAsync("Escape"); await IdentityDialog().WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Require(compact && await page.Locator("#identity-trigger").IsVisibleAsync()
            && await page.EvaluateAsync<bool>("document.activeElement?.id==='identity-trigger'"),
            "The compact window keeps the identity control and chooser usable, and Escape restores keyboard focus to the opener.");
        Require(errors.IsEmpty && externalRequests == 0,
            "Identity selection, filtering and simulation complete without unhandled JavaScript errors or any external authentication request.");
    }

    private static ExtractionResult IdentitySource() => new()
    {
        Headers = new() { "ID", "Titre", "Statut", "Technicien" },
        Rows = new()
        {
            new[] { "101", "Intervention Élodie A", "Nouveau", "Élodie Martin" },
            new[] { "102", "Intervention Élodie B", "Nouveau", "Élodie Martin" },
            new[] { "201", "Intervention Elodie", "Nouveau", "Elodie Martin" },
            new[] { "301", "Intervention Samir", "Nouveau", "Samir Laurent" },
            new[] { "401", "Intervention non attribuée", "Nouveau", "" }
        },
        SourceUrl = "https://identity-fixture.invalid/front/ticket.php", Title = "Identités synthétiques", ExpectedTotal = 5, Complete = true,
        Provenance = new() { Method = "native-export", SourceKind = "glpi", SearchVerified = true, ScopeVerified = true }
    };
}
