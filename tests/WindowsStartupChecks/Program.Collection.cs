using System.Text.Json;
using Microsoft.Playwright;

internal sealed partial class WindowsWebViewFixture
{
    private async Task CheckCollectionNativeAsync(IPage page, string edition)
    {
        // Only the isolated profile is changed. Never click Start or enable the
        // scheduler: the full acquisition journey has its own synthetic portal.
        var before = await page.EvaluateAsync<string>("async()=>JSON.stringify(await (await import('/js/bridge.js')).api.call('library.collection.get'))");
        using var snapshot = JsonDocument.Parse(before);
        var originalOptions = snapshot.RootElement.GetProperty("state").GetProperty("options").GetRawText();
        var period = snapshot.RootElement.GetProperty("state").GetProperty("options").GetProperty("intervalMinutes").GetInt32() == 180 ? 181 : 180;
        // Admin arrives from another panel in this same workspace. A route-name
        // check alone can still see the previous Data DOM while cleanup runs.
        // Wait for that exact root to detach before using the new controls.
        var previousRoot = await page.Locator(".page-content").ElementHandleAsync();
        await NavigateAsync(page, "data");
        if (previousRoot is not null)
        {
            await WaitForPageAsync(page, "previous=>!previous.isConnected", previousRoot);
            await previousRoot.DisposeAsync();
        }
        await WaitForPageAsync(page, "()=>!!document.querySelector('.data-workspace[data-ready=true]')&&!document.querySelector('#content').hasAttribute('aria-busy')");
        await page.Locator("[data-open-panel='collection']").ClickAsync();
        var panel = page.Locator("dialog[open][data-workspace-panel='collection']");
        var source = await page.EvaluateAsync<JsonElement>("async()=>(await (await import('/js/bridge.js')).api.call('app.bootstrap')).session");
        if (source.GetProperty("fileSource").GetBoolean())
        {
            // The PDF journey imports a CSV. A subsequent native process restores
            // that exact file cache, which must never become a live GLPI source.
            await panel.GetByRole(AriaRole.Heading, new() { Name = "Collecte GLPI réelle", Exact = true }).WaitForAsync();
            Check(source.GetProperty("cachedSource").GetBoolean() && source.GetProperty("rows").GetInt32() == 4
                && await panel.Locator(".collection-workspace").CountAsync() == 0
                && await panel.GetByRole(AriaRole.Button, new() { Name = "Mettre à jour", Exact = true }).CountAsync() == 0
                && await page.EvaluateAsync<bool>("async()=>(await (await import('/js/bridge.js')).api.call('library.collection.status')).requests===0"),
                "Native " + edition + " restores the four-row PDF input as a file cache and refuses real collection without any GLPI request.");
            await panel.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).First.ClickAsync();
            await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
            var fileRoot = await page.Locator(".page-content").ElementHandleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = source.GetProperty("fileCanRestore").GetBoolean() ? "Revenir à la source précédente" : "Fermer cette source", Exact = true }).ClickAsync();
            await WaitForPageAsync(page, "previous=>!previous.isConnected", fileRoot);
            await fileRoot!.DisposeAsync();
            await WaitForPageAsync(page, "async()=>{const s=(await (await import('/js/bridge.js')).api.call('app.bootstrap')).session;return !s.fileSource&&!s.simulation&&!!document.querySelector('.data-workspace[data-ready=true]')&&!document.querySelector('#content').hasAttribute('aria-busy');}");
            Check(await page.EvaluateAsync<bool>("async()=>(await (await import('/js/bridge.js')).api.call('library.collection.status')).requests===0"),
                "Native " + edition + " explicitly closes the file source through the visible control without starting collection.");
            await page.Locator("[data-open-panel='collection']").ClickAsync();
        }
        await panel.Locator(".collection-workspace").WaitForAsync();
        await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
        Check(await panel.GetByRole(AriaRole.Button, new() { Name = "Mettre à jour", Exact = true }).IsVisibleAsync()
            && !await panel.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true }).IsVisibleAsync()
            && !await panel.GetByRole(AriaRole.Button, new() { Name = "Arrêter", Exact = true }).IsVisibleAsync()
            && await panel.Locator(".collection-disclosure[open]").CountAsync() == 0,
            "Native " + edition + " collection menu exposes the idle action and keeps advanced controls collapsed.");
        Check(await page.EvaluateAsync<bool>("""
            async()=>{const {api}=await import('/js/bridge.js');
              const before=await api.call('library.collection.status');
              const result=await api.call('library.collection.control',{action:'stop',sessionId:'00000000000000000000000000000001'});
              const after=await api.call('library.collection.status');
              return result.requested===false && after.sessionId===before.sessionId && after.requests===before.requests && after.requests===0;}
            """), "Native " + edition + " opening the menu and rejecting a stale control do not start any GLPI navigation.");
        await panel.GetByText("Rythme et programmation", new() { Exact = true }).ClickAsync();
        await panel.GetByLabel("Ce poste effectue les mises à jour automatiques", new() { Exact = true }).UncheckAsync();
        await panel.GetByLabel("Intervalle entre passages (minutes)", new() { Exact = true }).FillAsync(period.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await panel.GetByRole(AriaRole.Button, new() { Name = "Enregistrer le rythme", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, """
            async expected=>{const {api}=await import('/js/bridge.js');if(api.pending)return false;
              const result=await api.call('library.collection.get');return result.state.options.intervalMinutes===expected && result.state.options.automatic===false;}
            """, period);
        Check(await page.EvaluateAsync<bool>("async()=>{const s=await (await import('/js/bridge.js')).api.call('library.collection.get');return s.state.nextAt===null&&s.activity.requests===0;}"),
            "Native " + edition + " rhythm edits persist through the packaged C# bridge without enabling automatic collection.");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).First.ClickAsync();
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.Locator("[data-open-panel='collection']").ClickAsync();
        await panel.Locator(".collection-workspace").WaitForAsync();
        await panel.GetByText("Rythme et programmation", new() { Exact = true }).ClickAsync();
        Check(await panel.GetByLabel("Intervalle entre passages (minutes)", new() { Exact = true }).InputValueAsync() == period.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Native " + edition + " reopening collection restores the saved rhythm rather than the initial form defaults.");
        await page.ScreenshotAsync(new() { Path = Path.Combine(_output, "collection-native-" + edition + ".png"), FullPage = true });
        await panel.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).First.ClickAsync();
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.EvaluateAsync("""
            async original=>{const {api}=await import('/js/bridge.js');const current=await api.call('library.collection.get');
              await api.call('library.collection.settings',{revision:current.state.revision,options:JSON.parse(original),confirmedCollector:false});}
            """, originalOptions);
        await NavigateAsync(page, "home");
    }
}
