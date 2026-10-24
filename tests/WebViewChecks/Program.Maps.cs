using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckEmbeddedMapOverviewAsync(IPage page, PilotApplication app, Func<string, Task> navigate, Func<string, Task> capture)
    {
        var original = app.Settings.Copy(); var expanded = original.Copy();
        for (var index = 1; index < 26; index++) expanded.Cities.Sites.Add(new SiteDefinition
        {
            Name = "Site fictif " + (index + 1).ToString("00"), Address = "Adresse fictive " + (index + 1),
            Latitude = 44.12 + index % 5 * .03, Longitude = 5.12 + index / 5 * .03
        });
        expanded.Cities.Sites.Add(new SiteDefinition { Name = "Site sans coordonnées", CityId = expanded.Cities.Cities.Single().Id, Address = "Adresse sans géocodage" });
        await Call(app, "settings.save", new { revision = app.Revision, settings = expanded });
        await navigate("home"); await navigate("pilot-sites");
        await page.GetByRole(AriaRole.Button, new() { Name = "Carte des sites du référentiel", Exact = true }).ClickAsync();
        var map = page.Locator("dialog.maps-dialog"); await map.WaitForAsync();
        Require(await map.Locator(".maps-marker").CountAsync() == 26 && await map.Locator(".maps-place").CountAsync() == 27
            && await map.Locator(".maps-missing li").InnerTextAsync() == "Site sans coordonnées"
            && await map.Locator(".maps-canvas").GetAttributeAsync("data-order-connections") == "0",
            "The reference overview places all 26 synthetic geocoded sites, preserves zero-ticket sites, lists the site without coordinates, and does not invent connections or a city-centre position.");
        await capture("simulation-embedded-26-sites");
        var viewport = page.ViewportSize!;
        await page.SetViewportSizeAsync(528, 416);
        Require(await map.EvaluateAsync<bool>("node => { const r = node.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth + 1 && node.querySelector('.modal-body').scrollHeight > node.querySelector('.modal-body').clientHeight && document.documentElement.scrollWidth <= innerWidth; }")
            && await map.Locator(".maps-canvas").EvaluateAsync<bool>("node => node.clientWidth >= 250"),
            "At a small window equivalent to 125% zoom the integrated map remains within the viewport and its list and attribution stay accessible by scrolling.");
        await page.SetViewportSizeAsync(viewport.Width, viewport.Height);
        await map.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await Call(app, "settings.save", new { revision = app.Revision, settings = original });
    }

    private static async Task CheckEmbeddedMapRendererAsync(IPage page)
    {
        // A synthetic rendering fixture exercises an absent intermediate point
        // and literal reference labels without depending on a routing provider.
        await page.EvaluateAsync("""
            async () => {
              const { openMap } = await import('/js/pages/maps.js');
              const controller = new AbortController(); globalThis.__mapController = controller; globalThis.__mapCalls = [];
              const places = [
                { id:'site:a', label:'<b>Nom littéral</b>', address:'<img src=x onerror="globalThis.__mapInjected=true">', latitude:44.1, longitude:5.1, sequence:1 },
                { id:'site:gap', label:'Étape sans coordonnées', address:'Adresse seule', latitude:null, longitude:null, sequence:2 },
                { id:'site:b', label:'Site fictif B', address:'Adresse B', latitude:44.15, longitude:5.15, sequence:3 },
                { id:'site:c', label:'Site fictif C', address:'Adresse C', latitude:44.2, longitude:5.2, sequence:4 }
              ].map(place=>({...place,precision:'Coordonnées du référentiel'}));
              const api={call:async (operation,args)=>{ globalThis.__mapCalls.push({operation,args});
                if(operation==='maps.preview')return {token:'renderer-fixture',title:'Ordre local fictif',simulation:true,notes:[],routes:[{id:'route-1',title:'Parcours <b>littéral</b>',ordered:true,places}]};
                if(operation==='maps.tiles')return {enabled:false}; throw new Error(operation);
              }};
              globalThis.__mapModal = await openMap({api,signal:controller.signal},{kind:'tour'});
              globalThis.__mapElement = document.querySelector('.maps-canvas');
            }
            """);
        var map = page.Locator("dialog.maps-dialog");
        Require(await map.Locator(".maps-marker").CountAsync() == 3 && await map.Locator(".maps-order-line").CountAsync() == 1
            && await map.Locator(".maps-canvas").GetAttributeAsync("data-order-connections") == "1"
            && (await map.InnerTextAsync()).Contains("sans trajet routier", StringComparison.Ordinal),
            "Visit-order lines join only consecutive geocoded steps and never bridge over a missing position or claim a road route.");
        await map.Locator(".maps-place[data-place-id='site:a']").ClickAsync();
        Require((await map.Locator(".maps-popup").InnerTextAsync()).Contains("<img src=x onerror=", StringComparison.Ordinal)
            && await map.Locator(".maps-popup img,.maps-popup b,.maps-route h3 b").CountAsync() == 0
            && await page.EvaluateAsync<bool>("globalThis.__mapInjected !== true"),
            "Free-form site labels and addresses remain literal text in list headings, marker popups and tooltips.");
        await map.Locator(".maps-marker[data-place-key='0:3']").FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        Require(await map.Locator(".maps-place[data-place-id='site:c']").GetAttributeAsync("aria-pressed") == "true",
            "Selecting a marker with the keyboard synchronizes the corresponding visit in the list.");
        await page.EvaluateAsync("globalThis.__mapController.abort()");
        Require(await page.Locator("dialog.maps-dialog").CountAsync() == 0
            && await page.EvaluateAsync<bool>("!globalThis.__mapElement.isConnected && globalThis.__mapElement._leaflet_id === undefined && globalThis.__mapCalls.some(call=>call.operation==='maps.tiles'&&call.args.enabled===false)"),
            "Cancelling the page destroys the Leaflet instance and sends an idempotent tile revocation without the cancelled page signal.");

        await page.EvaluateAsync("""
            async () => {
              const { openMap } = await import('/js/pages/maps.js');
              const controller = new AbortController(); globalThis.__mapController=controller; globalThis.__mapCalls=[];
              const api={call:async(operation,args)=>{globalThis.__mapCalls.push({operation,args});
                if(operation==='maps.preview')return {token:'pending-fixture',title:'Activation en attente',simulation:true,notes:[],routes:[{id:'route-1',title:'Site',ordered:false,places:[{id:'site:a',label:'Site A',address:'Adresse A',precision:'Site',latitude:44,longitude:5,sequence:1}]}]};
                if(operation==='maps.tiles'&&args.enabled)return await new Promise(resolve=>{globalThis.__mapEnable=resolve;});
                return {enabled:false};
              }};
              await openMap({api,signal:controller.signal},{kind:'site'});
            }
            """);
        await map.GetByRole(AriaRole.Button, new() { Name = "Afficher les rues", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "typeof globalThis.__mapEnable === 'function'");
        await page.EvaluateAsync("globalThis.__mapController.abort(); globalThis.__mapEnable({enabled:true,tileUrl:'https://tile.openstreetmap.org/{z}/{x}/{y}.png',maxZoom:19})");
        await WaitForPageAsync(page, "globalThis.__mapCalls.filter(call=>call.operation==='maps.tiles'&&call.args.enabled===false).length>=2");
        Require(await page.Locator("dialog.maps-dialog,.leaflet-tile").CountAsync() == 0,
            "A tile activation completed after closure is revoked again and cannot recreate a map or start image requests.");
        await page.EvaluateAsync("delete globalThis.__mapController; delete globalThis.__mapCalls; delete globalThis.__mapModal; delete globalThis.__mapElement; delete globalThis.__mapEnable");
    }

    private static async Task CheckNormalMapDefaultAsync(IPage page, PilotApplication app, Func<string, Task> navigate)
    {
        var settings = app.Settings.Copy(); var site = settings.Cities.Sites.First(value => value.Name == "Site fictif Nord");
        site.Latitude = 44.1; site.Longitude = 5.1;
        await Call(app, "settings.save", new { revision = app.Revision, settings });
        await navigate("pilot-sites");
        await page.GetByRole(AriaRole.Button, new() { Name = "Carte · Site fictif Nord", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('.maps-canvas')?.dataset.tiles === 'online'");
        Require(!app.IsTestMode && app.AllowsMapTileRequest("https://tile.openstreetmap.org/0/0/0.png"),
            "In the normal workspace the explicit site-map click loads the detailed embedded background directly, without a second confirmation or external window.");
        await page.Locator("dialog.maps-dialog").GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await Until(() => !app.AllowsMapTileRequest("https://tile.openstreetmap.org/0/0/0.png"));
    }
}
