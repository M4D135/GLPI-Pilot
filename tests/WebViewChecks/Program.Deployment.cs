using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckDeploymentInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var operations = new ConcurrentQueue<(string Operation, JsonElement Args)>(); var externalRequests = 0;
        page.PageError += (_, error) => errors.Enqueue(error);
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404 }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page }; WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json=>window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        var settings = new AppSettings { Theme = "light", ExportFolder = Path.Combine(_root, "Exports"), PortalUrl = "https://deployment-fixture.invalid/front/ticket.php" };
        settings.Interface.OnboardingCompleted = true; settings.Cities.Cities.Add(new() { Name = "Ville actuelle" }); settings.Save();
        await using var app = new PilotApplication(host, (name, value) => bridge?.Publish(name, value), settings);
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, value, ct) =>
        {
            operations.Enqueue((op, value.Clone()));
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = value.GetProperty("open").GetBoolean(); return new { updated = true }; }
            return await app.ExecuteAsync(op, value, ct);
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (()=>{const listeners=new Set();window.chrome||={};window.chrome.webview={
              postMessage:message=>window.__pilotSend(JSON.stringify(message)),
              addEventListener:(name,handler)=>{if(name==='message')listeners.add(handler);},
              removeEventListener:(name,handler)=>listeners.delete(handler)
            };window.__pilotDeliver=message=>{for(const listener of listeners)listener({data:message});};})();
            """);
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        ILocator Review() => page.GetByRole(AriaRole.Dialog, new() { Name = "Examiner la configuration complète", Exact = true });
        async Task SettingsPage()
        {
            await ClickNavigationAsync(page, "settings"); await WaitForNavigationTargetAsync(page, "settings");
            await page.Locator(".settings-workspace[data-ready=true]").WaitForAsync();
            await page.Locator("[data-settings-nav=deploiement]").ClickAsync();
        }
        async Task Idle() => await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
        async Task Capture(string name)
        {
            var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return;
            Directory.CreateDirectory(args[index + 1]); await page.ScreenshotAsync(new() { Path = Path.Combine(args[index + 1], name + ".png"), FullPage = true });
        }
        async Task OpenReview(string path)
        { host.NextOpen = path; await Button("Examiner un fichier complet").ClickAsync(); await Review().WaitForAsync(); }
        async Task ConfirmReview()
        {
            await Review().GetByRole(AriaRole.Checkbox).CheckAsync();
            await Review().GetByRole(AriaRole.Button, new() { Name = "Sauvegarder et remplacer", Exact = true }).ClickAsync();
        }
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(page); await SettingsPage();
        Require(await page.Locator(".deployment-settings").IsVisibleAsync()
            && (await page.Locator(".deployment-settings").InnerTextAsync()).Contains("mises à jour par dossier réseau", StringComparison.Ordinal),
            "Normal Settings exposes the complete deployment configuration separately from the limited team configuration.");
        await page.Locator(".deployment-pack-location summary").ClickAsync();
        Require((await page.Locator(".deployment-pack-location").InnerTextAsync()).Contains(DeploymentConfigurationService.FileName, StringComparison.Ordinal)
            && (await page.Locator(".deployment-pack-location").InnerTextAsync()).Contains("wad/" + DeploymentConfigurationService.FileName, StringComparison.Ordinal)
            && await page.Locator("[data-deployment-state]").CountAsync() == 1,
            "The exact wad filename, legacy compatibility and initial profile status are visible.");
        host.NextSave = Path.Combine(_root, DeploymentConfigurationService.FileName);
        await Button("Exporter la configuration complète").ClickAsync();
        var exported = page.GetByRole(AriaRole.Dialog, new() { Name = "Configuration prête pour le pack PROD", Exact = true }); await exported.WaitForAsync();
        var baseline = DeploymentConfigurationService.Read(host.NextSave, _root).Configuration;
        Require(File.Exists(host.NextSave) && baseline.Settings.Interface.OnboardingCompleted && baseline.Settings.Cities.Cities.Count == 1
            && (await exported.InnerTextAsync()).Contains("GLPI-Pilot-Admin.exe", StringComparison.Ordinal)
            && !operations.Any(op => op.Operation == "configuration.export"),
            "The complete export uses the real deployment envelope and explains auto-application to a fresh profile, without invoking the limited exporter.");
        await exported.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        host.NextOpen = null; var original = JsonSerializer.Serialize(app.Settings, PilotApplication.Json);
        await Button("Examiner un fichier complet").ClickAsync(); await Idle();
        Require(await Review().CountAsync() == 0 && JsonSerializer.Serialize(app.Settings, PilotApplication.Json) == original,
            "Cancelling the file chooser leaves all options unchanged and opens no confirmation.");

        var incoming = app.Settings.Copy(); incoming.Theme = "dark"; incoming.Interface.Accent = "violet"; incoming.ExportFolder = Path.Combine(_root, "Exports déployés");
        incoming.Cities.Cities[0].Name = "Ville du pack";
        for (var i = 0; i < 65; i++) incoming.Cities.Sites.Add(new() { CityId = incoming.Cities.Cities[0].Id, Name = "Site du pack " + i,
            Contact = i == 64 ? "Contact final <b>littéral</b>" : "+33 123456789", Aliases = ["Alias " + i] });
        var document = DeploymentConfigurationService.Capture(incoming, _root);
        document.SharedUpdateFolder = Path.Combine(_root, "Partage PROD");
        var candidate = Path.Combine(_root, "configuration-a-importer.json");
        await File.WriteAllBytesAsync(candidate, DeploymentConfigurationService.Serialize(document));
        var pristine = await File.ReadAllBytesAsync(candidate);
        await OpenReview(candidate);
        Require(await Review().GetByRole(AriaRole.Button, new() { Name = "Sauvegarder et remplacer", Exact = true }).IsDisabledAsync()
            && JsonSerializer.Serialize(app.Settings, PilotApplication.Json) == original,
            "The complete before/after preview never mutates options and requires explicit replacement acknowledgement.");
        var firstFields = await Review().Locator("[data-deployment-path]").AllTextContentsAsync();
        await Review().GetByRole(AriaRole.Button, new() { Name = "Options suivantes", Exact = true }).ClickAsync();
        Require(!firstFields.SequenceEqual(await Review().Locator("[data-deployment-path]").AllTextContentsAsync())
            && await Review().Locator("[data-deployment-path]").CountAsync() == 50,
            "All configuration leaves remain reachable through bounded pagination instead of being truncated.");
        await Review().GetByRole(AriaRole.Searchbox).FillAsync("Contact final");
        Require((await Review().Locator(".deployment-comparison").InnerTextAsync()).Contains("Contact final <b>littéral</b>", StringComparison.Ordinal)
            && await Review().Locator(".deployment-comparison b").CountAsync() == 0,
            "Search reaches the last site's exact literal value beyond the first page and never executes imported markup.");
        await Review().GetByRole(AriaRole.Searchbox).FillAsync("Partage PROD");
        Require((await Review().Locator(".deployment-comparison").InnerTextAsync()).Contains("Partage PROD", StringComparison.Ordinal),
            "The network update path appears in the exhaustive comparison alongside application options.");
        await Review().GetByRole(AriaRole.Searchbox).FillAsync(""); await Capture("deployment-review");
        await Review().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        Require(JsonSerializer.Serialize(app.Settings, PilotApplication.Json) == original, "Cancelling the review preserves the complete current profile.");
        await OpenReview(candidate); await File.AppendAllTextAsync(candidate, " "); await ConfirmReview();
        var failure = page.GetByRole(AriaRole.Dialog, new() { Name = "Configuration non appliquée", Exact = true }); await failure.WaitForAsync();
        Require(JsonSerializer.Serialize(app.Settings, PilotApplication.Json) == original,
            "A source file changed after preview is rejected without replacing any current option.");
        await failure.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync(); await SettingsPage();
        await File.WriteAllBytesAsync(candidate, pristine); await OpenReview(candidate);
        await Call(app, "app.appearance", new { theme = "dark" }); await delivery;
        await ConfirmReview(); await failure.WaitForAsync();
        Require(app.Settings.Cities.Sites.Count == 0 && app.Settings.Theme == "dark",
            "A local setting changed after preview invalidates the token and preserves the newer local edit.");
        await failure.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync(); await SettingsPage();
        await Call(app, "app.appearance", new { theme = "light" }); await delivery;
        await OpenReview(candidate); await page.SetViewportSizeAsync(768, 800);
        Require(await Review().EvaluateAsync<bool>("node=>node.scrollWidth<=innerWidth+1"), "The complete review remains bounded at a compact screen width.");
        await Capture("deployment-review-compact"); await ConfirmReview();
        await page.Locator("[data-deployment-applied]").WaitForAsync();
        var backupPath = await page.Locator("[data-deployment-applied] pre").InnerTextAsync();
        Require(app.Settings.Theme == "dark" && app.Settings.Interface.Accent == "violet" && app.Settings.Cities.Sites.Count == 65
            && (await Call(app, "update.state")).GetProperty("sharedFolder").GetString() == document.SharedUpdateFolder && File.Exists(backupPath),
            "Confirmed import applies sites, presentation and the network update path together and shows the actual restorable backup.");
        var backup = DeploymentConfigurationService.Read(backupPath, _root);
        Require(backup.Configuration.Settings.Theme == "light" && backup.Configuration.Settings.Cities.Sites.Count == 0,
            "The displayed backup is a complete importable document containing the exact previous configuration.");
        await OpenReview(backupPath); await ConfirmReview();
        await WaitForPageAsync(page, "hash=>document.querySelector('[data-deployment-applied]')?.textContent.includes(hash)===true", backup.Sha256);
        Require(app.Settings.Theme == "light" && app.Settings.Cities.Sites.Count == 0
            && (await Call(app, "update.state")).GetProperty("sharedFolder").GetString() == "",
            "Re-importing the visible backup restores the earlier public configuration and update source.");
        if (!AdministrationCapabilities.Enabled)
        {
            var refusal = await page.EvaluateAsync<string>("async()=>{try{await (await import('/js/bridge.js')).api.call('admin.local.preview',{});return 'accepted';}catch(error){return error.message;}}");
            Require(refusal.Contains("Standard", StringComparison.Ordinal),
                "The standard application still rejects administrative operations after a complete configuration import.");
        }
        await page.Locator("[data-deployment-applied]").ScrollIntoViewIfNeededAsync(); await Capture("deployment-applied");

        var readyPack = Path.Combine(_root, "prod-ready"); Directory.CreateDirectory(readyPack); app.SetDeploymentFixture(readyPack, "9.9.8");
        await SettingsPage();
        await Button("Définir comme configuration par défaut").ClickAsync();
        var defaultConfirm = page.GetByRole(AriaRole.Dialog, new() { Name = "Configuration par défaut du pack", Exact = true });
        await defaultConfirm.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        Require(!Directory.Exists(Path.Combine(readyPack, "wad")) && !File.Exists(Path.Combine(readyPack, DeploymentConfigurationService.FileName))
            && !operations.Any(op => op.Operation == "deployment.default.save"),
            "Cancelling Define as default never writes a configuration into the PROD folder.");
        host.NextSave = null;
        await Button("Définir comme configuration par défaut").ClickAsync();
        await defaultConfirm.GetByRole(AriaRole.Button, new() { Name = "Enregistrer comme défaut", Exact = true }).ClickAsync();
        var defaultResult = page.GetByRole(AriaRole.Dialog, new() { Name = "Configuration par défaut enregistrée", Exact = true }); await defaultResult.WaitForAsync();
        var defaultPath = await defaultResult.Locator("pre").First.InnerTextAsync();
        var defaultBytes = await File.ReadAllBytesAsync(defaultPath);
        Require(defaultPath == DeploymentConfigurationInitializer.DefaultPath(readyPack)
            && DeploymentConfigurationService.Read(defaultPath, _root).Configuration.Settings.Theme == "light"
            && !(await Call(app, "deployment.state")).GetProperty("offer").GetProperty("available").GetBoolean(),
            "Define as default creates the complete configuration inside wad without a file chooser and remembers the author's choice.");
        await defaultResult.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await Call(app, "app.appearance", new { theme = "dark" }); await delivery;
        await Button("Définir comme configuration par défaut").ClickAsync();
        await defaultConfirm.GetByRole(AriaRole.Button, new() { Name = "Enregistrer comme défaut", Exact = true }).ClickAsync(); await defaultResult.WaitForAsync();
        var previousDefault = await defaultResult.Locator("pre").Last.InnerTextAsync();
        Require(previousDefault != defaultPath && File.Exists(previousDefault) && (await File.ReadAllBytesAsync(previousDefault)).SequenceEqual(defaultBytes)
            && DeploymentConfigurationService.Read(defaultPath, _root).Configuration.Settings.Theme == "dark",
            "A second save from the same Settings screen uses the updated expected hash and retains a backup containing the old file's exact bytes.");
        await Capture("deployment-default-saved");
        await defaultResult.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await Call(app, "app.appearance", new { theme = "light" }); await delivery;

        var pack = Path.Combine(_root, "pack-proposal"); Directory.CreateDirectory(pack);
        await File.WriteAllBytesAsync(Path.Combine(pack, DeploymentConfigurationService.FileName), pristine);
        DeploymentConfigurationInitializer.TryInitialize(pack, _root, "9.9.9"); app.SetDeploymentFixture(pack, "9.9.9");
        await page.ReloadAsync(); await ReadyAsync(page);
        var offer = page.GetByRole(AriaRole.Dialog, new() { Name = "Configuration fournie avec ce pack", Exact = true }); await offer.WaitForAsync();
        await Capture("deployment-startup-offer");
        Require(app.Settings.Cities.Sites.Count == 0, "An existing profile receives a startup proposal and is never automatically replaced by the pack.");
        await offer.GetByRole(AriaRole.Button, new() { Name = "Examiner la configuration du pack", Exact = true }).ClickAsync(); await Review().WaitForAsync();
        Require(await Review().GetByRole(AriaRole.Button, new() { Name = "Garder mes réglages", Exact = true }).IsVisibleAsync(),
            "The startup proposal opens the same exhaustive review with explicit Keep and Replace choices.");
        await Review().GetByRole(AriaRole.Button, new() { Name = "Garder mes réglages", Exact = true }).ClickAsync();
        await page.ReloadAsync(); await ReadyAsync(page);
        Require(await offer.CountAsync() == 0 && !(await Call(app, "deployment.state")).GetProperty("offer").GetProperty("available").GetBoolean()
            && app.Settings.Cities.Sites.Count == 0, "Keeping local settings is remembered for this pack hash and version across a new startup.");
        await Call(app, "app.demo"); await page.ReloadAsync(); await ReadyAsync(page); await SettingsPage();
        Require(await Button("Exporter la configuration complète").IsDisabledAsync() && await Button("Examiner un fichier complet").IsDisabledAsync()
            && await Button("Définir comme configuration par défaut").IsDisabledAsync(),
            "The demonstration cannot export fictitious settings or replace the real profile.");
        Require(errors.IsEmpty && externalRequests == 0, "The complete configuration and startup-offer journey has no JavaScript errors or external request.");
        await actualBridge.DisposeAsync(); await delivery;
    }
}

namespace GlpiPilot.Runtime
{
    public sealed partial class PilotApplication
    {
        public void SetDeploymentFixture(string directory, string version) { _deploymentDirectory = directory; _deploymentVersion = version; }
    }
}
