using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckWorkspaceGridInterfaceAsync(string[] args)
    {
        // This suite deliberately exercises the real pack-relative save command.
        // Do not let its synthetic default become the next edition's startup seed.
        if (Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_MODE") != "1")
            throw new InvalidOperationException("This fixture requires the isolated test harness.");
        var paths = new[] {
            Path.Combine(AppContext.BaseDirectory, "wad", DeploymentConfigurationService.FileName),
            Path.Combine(AppContext.BaseDirectory, DeploymentConfigurationService.FileName)
        };
        var original = paths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        try
        {
            foreach (var path in paths) if (File.Exists(path)) File.Delete(path);
            await CheckWorkspaceGridScenarioAsync(args);
        }
        finally
        {
            foreach (var pair in original)
            {
                if (pair.Value is null) { if (File.Exists(pair.Key)) File.Delete(pair.Key); }
                else { Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!); File.WriteAllBytes(pair.Key, pair.Value); }
            }
        }
    }

    private static async Task CheckWorkspaceGridScenarioAsync(string[] args)
    {
        var source = CreateWeatherScopeCsv();
        var settings = new AppSettings { Theme = "dark", ExportFolder = Path.Combine(_root, "Exports") };
        settings.Interface.OnboardingCompleted = true;
        var city = new CityDefinition { Name = "Commune de démonstration", PostalCode = "00000", Aliases = ["VILLE-TEST"] };
        var site = new SiteDefinition { CityId = city.Id, Name = "Site fictif du test", Aliases = ["SITE-TEST"], Address = "Adresse synthétique personnalisée" };
        settings.Cities.Cities.Add(city); settings.Cities.Sites.Add(site);
        settings.Cities.Buildings.Add(new() { SiteId = site.Id, Name = "Bâtiment fictif", Aliases = ["BAT-TEST"], Floor = "2" });
        var board = DashboardLayoutSettings.CreateBoard("Grille de test", "empty");
        board.Sections = [new() { Key = "main", Title = "Indicateurs" }, new() { Key = "secondary", Title = "Autre section" }];
        board.Widgets =
        [
            new() { Id = "grid-a", Kind = "metric", SourceKey = "active", Title = "Premier indicateur", Section = "main", Span = 4, Height = 220, AutoHeight = false, ShowMiniHistory = false, GridX = 0, GridY = 0 },
            new() { Id = "grid-b", Kind = "metric", SourceKey = "pending", Title = "Deuxième indicateur", Section = "main", Span = 4, Height = 220, AutoHeight = false, ShowMiniHistory = false, GridX = 4, GridY = 0 },
            new() { Id = "grid-c", Kind = "distribution", SourceKey = "active-priority", Title = "Grand graphique", Section = "main", Span = 4, Height = 700, AutoHeight = false, GridX = 8, GridY = 0 },
            new() { Id = "grid-locked", Kind = "note", Title = "Repère verrouillé", Note = "Données fictives uniquement", Section = "secondary", Span = 6, Height = 220, AutoHeight = false, Locked = true, GridX = 0, GridY = 0 }
        ];
        settings.Dashboard.Layout = new() { ActiveBoardId = board.Id, Boards = [board] };
        settings.Normalize(); settings.Save();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        string savedLayout;
        int? savedX, savedY;
        await using (var ui = await WeatherSelectionUi.CreateAsync(browser, settings, source))
        {
            var page = ui.Page;
            DashboardBoardSettings Current() => ui.App.Settings.Dashboard.Layout.Boards.Single(b => b.Id == board.Id);
            DashboardWidgetSettings Item(string id) => Current().Widgets.Single(w => w.Id == id);
            ILocator Card(string id) => page.Locator($".pilot-widget[data-widget-id='{id}']");
            // A card is painted at the height its row span reserves, which is what the
            // drag preview shows. The saved height stays the value David chose.
            static double LatticeHeight(double height) => Math.Ceiling((height + 16) / 20) * 20 - 16;
            async Task Ready()
            {
                await ui.Idle();
                await WaitForPageAsync(page, "()=>document.querySelectorAll('.pilot-widget-grid[data-grid-ready=true]').length===2");
                await page.EvaluateAsync("()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
            }
            await ui.Navigate();
            await page.GetByLabel("Composition du pilotage", new() { Exact = true }).SelectOptionAsync("service"); await ui.Idle();
            await WaitForPageAsync(page, "!!document.querySelector('.pilot-block[data-section-key=flow] [data-grid-ready=true]')");
            Require(await page.EvaluateAsync<bool>("""
                () => {
                  window.defaultStockRects = () => [...document.querySelectorAll('.pilot-block[data-section-key=flow] .pilot-widget[data-widget-kind=metric]')].map(node => {
                    const r = node.getBoundingClientRect(), grid = node.parentElement.getBoundingClientRect();
                    return { id: node.dataset.widgetId, x: r.x - grid.x, y: r.y - grid.y, width: r.width, height: r.height };
                  });
                  window.defaultStockBefore = window.defaultStockRects();
                  const r = window.defaultStockBefore;
                  return r.length >= 6 && r.every(item => Math.abs(item.height - 324) < 1)
                    && Math.abs(r[0].y-r[1].y)<1 && Math.abs(r[1].y-r[2].y)<1
                    && Math.abs(r[3].y-r[4].y)<1 && Math.abs(r[4].y-r[5].y)<1 && r[3].y>r[0].y;
                }
                """), "The real default service board starts with equal-height stock indicators on aligned rows.");
            await page.Locator(".pilot-block[data-section-key=flow]").ScrollIntoViewIfNeededAsync();
            await ui.Capture(args, "workspace-defaults-reading");
            await page.GetByRole(AriaRole.Button, new() { Name = "Personnaliser la vue", Exact = true }).ClickAsync(); await ui.Idle();
            await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
            Require(await page.EvaluateAsync<bool>("""
                () => window.defaultStockRects().every(rect => {
                  const before=window.defaultStockBefore.find(item=>item.id===rect.id);
                  return before && ['x','y','width','height'].every(key=>Math.abs(before[key]-rect[key])<1);
                })
                """), "The default service indicators keep the same geometry while composing.");
            await page.Locator(".pilot-block[data-section-key=flow]").ScrollIntoViewIfNeededAsync();
            await ui.Capture(args, "workspace-defaults-editing");
            await page.GetByRole(AriaRole.Button, new() { Name = "Terminer la composition", Exact = true }).ClickAsync(); await ui.Idle();
            await page.GetByLabel("Composition du pilotage", new() { Exact = true }).SelectOptionAsync(board.Id); await Ready();
            Require(await page.EvaluateAsync<bool>("""
                () => [...document.querySelectorAll('.pilot-widget > .analytics-widget-body')]
                  .every(node => getComputedStyle(node).overflowY === 'hidden')
                """) && Item("grid-a").Height == 220 && Item("grid-b").Height == 220 && Item("grid-c").Height == 700,
                "Reading previews keep bounded geometry without nested body scrolling or rewriting saved independent heights.");
            Require(await page.Locator("[data-grid-move],[data-grid-size],[data-grid-resize]").CountAsync() == 0,
                "Reading mode does not expose movement or resizing controls.");
            await page.EvaluateAsync("""
                () => {
                  window.workspaceRects = () => [...document.querySelectorAll('.pilot-widget')].map(node => {
                    const r = node.getBoundingClientRect(), grid = node.parentElement.getBoundingClientRect();
                    return { id: node.dataset.widgetId, x: r.x - grid.x, y: r.y - grid.y, width: r.width, height: r.height };
                  });
                  window.workspaceBeforeModeChange = window.workspaceRects();
                  window.workspaceMatches = () => window.workspaceRects().every(rect => {
                    const before = window.workspaceBeforeModeChange.find(item => item.id === rect.id);
                    return before && ['x','y','width','height'].every(key => Math.abs(before[key] - rect[key]) < 1);
                  });
                }
                """);
            await page.GetByRole(AriaRole.Button, new() { Name = "Personnaliser la vue", Exact = true }).ClickAsync(); await Ready();
            Require(await page.EvaluateAsync<bool>("() => window.workspaceMatches()"),
                "Entering composition preserves the real reading rectangles, including the tall independent chart.");
            var a = await Card("grid-a").BoundingBoxAsync() ?? throw new Exception("Missing first widget");
            var b = await Card("grid-b").BoundingBoxAsync() ?? throw new Exception("Missing second widget");
            var c = await Card("grid-c").BoundingBoxAsync() ?? throw new Exception("Missing chart");
            Require(Math.Abs(a.Height - LatticeHeight(220)) < 2 && Math.Abs(b.Height - LatticeHeight(220)) < 2
                    && Math.Abs(c.Height - LatticeHeight(700)) < 2 && Math.Abs(a.Height - b.Height) < 1
                    && c.Height > a.Height + 400,
                "The real Pilotage DOM gives two 220-pixel indicators independent heights beside a 700-pixel chart.");

            Require(await page.Locator("[data-grid-move]").CountAsync() == 4 && await page.Locator("[data-grid-size='grid-locked']").IsDisabledAsync()
                && await page.Locator("[data-grid-resize='grid-locked']").IsDisabledAsync(),
                "Composition mode exposes direct controls while a locked card refuses movement and resizing.");
            Require(await page.Locator("[data-grid-size='grid-a'] option:not([disabled])").CountAsync() == 3,
                "Every editable card offers exactly the three Small, Medium and Large presets.");
            await page.Locator("[data-grid-size='grid-a']").SelectOptionAsync("medium"); await Ready();
            Require(Item("grid-a").Span == 6 && Item("grid-a").Height == (int)LatticeHeight(360) && !Item("grid-a").AutoHeight
                && Item("grid-b").Height == 220 && Item("grid-c").Height == 700,
                "Choosing Medium persists only the requested dimensions through the real C# save operation.");
            await page.Locator("[data-grid-size='grid-a']").SelectOptionAsync("small"); await Ready();
            var beforeY = Item("grid-a").GridY;
            await page.Locator("[data-grid-move='grid-a']").PressAsync("ArrowDown"); await Ready();
            Require(Item("grid-a").GridY == beforeY + 1 && Item("grid-a").GridX.HasValue,
                "Keyboard movement saves two-dimensional coordinates rather than merely swapping list order.");
            Require(await page.EvaluateAsync<bool>("""
                async () => {
                  const { passiveWidget } = await import('/js/pages/presentation-layout.js');
                  return ['grid-a', 'grid-c'].every(id => {
                    const original = document.querySelector(`.pilot-widget[data-widget-id='${id}']`);
                    const originalStyle = original.getAttribute('style');
                    const copy = passiveWidget(original);
                    const frame = document.createElement('div');
                    frame.className = 'presentation-item-content analytics';
                    Object.assign(frame.style, { position: 'fixed', left: '0', top: '0', width: '600px', height: '400px', visibility: 'hidden' });
                    frame.append(copy); document.body.append(frame);
                    try {
                      const outer = frame.getBoundingClientRect(), inner = copy.getBoundingClientRect();
                      return ['left', 'top', 'width', 'height', 'position', '--widget-height'].every(name => copy.style.getPropertyValue(name) === '')
                        && !copy.hasAttribute('data-grid-column') && !copy.hasAttribute('data-grid-row')
                        && !copy.querySelector('[data-grid-move],[data-grid-size],[data-grid-resize]')
                        && original.getAttribute('style') === originalStyle && !!original.querySelector('[data-grid-resize]')
                        && Math.abs(inner.left - outer.left) < 1 && Math.abs(inner.top - outer.top) < 1
                        && Math.abs(inner.width - outer.width) < 1 && inner.bottom <= outer.bottom + 1;
                    } finally { frame.remove(); }
                  });
                }
                """), "Presentation and PDF clones use their own geometry, remove grid controls and leave the moved desktop cards unchanged.");

            // Real pointer capture, movement, scrolling, drop and C# persistence.
            var handle = page.Locator("[data-grid-move='grid-a']");
            await handle.ScrollIntoViewIfNeededAsync();
            var hb = await handle.BoundingBoxAsync() ?? throw new Exception("Missing move handle");
            await page.Mouse.MoveAsync(hb.X + hb.Width / 2, hb.Y + hb.Height / 2); await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(hb.X + hb.Width / 2 + 16, hb.Y + hb.Height / 2 + 8);
            var destination = page.Locator(".pilot-block[data-section-key='secondary'] .pilot-widget-grid");
            await destination.ScrollIntoViewIfNeededAsync();
            var db = await destination.BoundingBoxAsync() ?? throw new Exception("Missing destination");
            await page.Mouse.MoveAsync(db.X + db.Width - 24, db.Y + 32, new() { Steps = 12 });
            await page.Mouse.UpAsync(); await Ready();
            Require(Item("grid-a").Section == "secondary" && Item("grid-a").GridX >= 6 && Item("grid-a").GridY.HasValue,
                "Dragging a card into an empty position of another section persists the chosen section and grid position.");
            Require(Item("grid-locked").GridX == 0 && Item("grid-locked").GridY == 0,
                "Cross-section placement does not move the locked destination card.");
            var beforeCancel = JsonSerializer.Serialize(Current(), PilotApplication.Json);
            handle = page.Locator("[data-grid-move='grid-a']"); await handle.ScrollIntoViewIfNeededAsync();
            hb = await handle.BoundingBoxAsync() ?? throw new Exception("Missing move handle after reload");
            await page.Mouse.MoveAsync(hb.X + hb.Width / 2, hb.Y + hb.Height / 2); await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(hb.X + hb.Width / 2 - 30, hb.Y + hb.Height / 2 + 70, new() { Steps = 5 });
            await page.Keyboard.PressAsync("Escape"); await page.Mouse.UpAsync(); await Ready();
            Require(JsonSerializer.Serialize(Current(), PilotApplication.Json) == beforeCancel
                && await page.Locator(".workspace-grid-preview,.workspace-grid-ghost").CountAsync() == 0,
                "Escape cancels a real pointer gesture without saving or leaving an orphan preview.");
            var grip = page.Locator("[data-grid-resize='grid-a']"); await grip.ScrollIntoViewIfNeededAsync();
            var gb = await grip.BoundingBoxAsync() ?? throw new Exception("Missing resize handle");
            await page.Mouse.MoveAsync(gb.X + gb.Width / 2, gb.Y + gb.Height / 2); await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(gb.X + gb.Width / 2, gb.Y + gb.Height / 2 + 80, new() { Steps = 8 });
            await page.Mouse.UpAsync(); await Ready();
            Require(Item("grid-a").Height == (int)LatticeHeight(LatticeHeight(220) + 80) && Item("grid-a").Span == 4
                    && Item("grid-b").Height == 220,
                "The resize grip persists the 80-pixel drag on the row lattice without stretching another card.");
            await ui.Capture(args, "workspace-grid-edition");
            await page.EvaluateAsync("() => { window.workspaceBeforeModeChange = window.workspaceRects(); }");
            await page.GetByRole(AriaRole.Button, new() { Name = "Terminer la composition", Exact = true }).ClickAsync(); await Ready();
            Require(await page.EvaluateAsync<bool>("() => window.workspaceMatches()"),
                "Validating composition preserves every moved and resized rectangle after the real C# save and reload.");
            var unchanged = JsonSerializer.Serialize(Current(), PilotApplication.Json);
            foreach (var size in new[] { (Width: 850, Height: 900), (Width: 1440, Height: 1000) })
            {
                await page.SetViewportSizeAsync(size.Width, size.Height); await Ready();
                Require(await page.EvaluateAsync<bool>("""
                    () => [...document.querySelectorAll('.pilot-widget-grid')].every(grid => {
                      const r=[...grid.querySelectorAll(':scope>.pilot-widget')].map(n=>n.getBoundingClientRect());
                      return r.every((a,i)=>r.slice(i+1).every(b=>a.right<=b.left+1||b.right<=a.left+1||a.bottom<=b.top+1||b.bottom<=a.top+1));
                    })
                    """), "Responsive projection keeps every card non-overlapping at width " + size.Width + ".");
            }
            Require(JsonSerializer.Serialize(Current(), PilotApplication.Json) == unchanged,
                "Changing viewport size never overwrites the user's saved desktop coordinates.");
            await ui.Capture(args, "workspace-grid-lecture");

            // Native service compositions use overrides, not the custom-board list.
            var native = (await Call(ui.App, "dashboard.layout.get", new { businessView = "workspace" }))
                .Deserialize<DashboardLayoutSettings>(PilotApplication.Json)!;
            var nativeHero = native.Boards[0].Widgets.First(w => w.Id.StartsWith("weather-rule-", StringComparison.Ordinal));
            var nativeHeroId = nativeHero.Id;
            nativeHero.GridX = 0; nativeHero.GridY = 3; nativeHero.Span = 6; nativeHero.Height = 300; nativeHero.AutoHeight = false;
            await Call(ui.App, "dashboard.layout.save", new { businessView = "workspace", layout = native });
            var profile = await Call(ui.App, "dashboard.business.settings.get");
            var profileSettings = profile.GetProperty("business").Deserialize<BusinessDashboardSettings>(PilotApplication.Json)!;
            await Call(ui.App, "dashboard.business.settings.save", new { business = profileSettings,
                baseFingerprint = profile.GetProperty("fingerprint").GetString() });
            var regenerated = ui.App.Settings.Dashboard.Business.BoardOverrides.Single(bd => bd.Id == "business-workspace")
                .Widgets.Single(w => w.Id == nativeHeroId);
            Require(regenerated.GridX == 0 && regenerated.GridY == 3 && regenerated.Span == 6 && regenerated.Height == 300,
                "Editing native weather rules preserves the native service card's explicit position and independent size.");
            var deploymentState = await Call(ui.App, "deployment.state");
            var saved = await Call(ui.App, "deployment.default.save", new { confirmed = true,
                expectedSha256 = deploymentState.GetProperty("defaultSha256").GetString() ?? "" });
            var path = saved.GetProperty("path").GetString()!;
            Require(Path.GetFileName(Path.GetDirectoryName(path)) == "wad" && File.Exists(path),
                "The actual default-configuration command creates its JSON in wad.");
            var imported = DeploymentConfigurationService.Read(path, Path.Combine(_root, "Recipient")).Configuration;
            var copied = imported.Settings.Dashboard.Layout.Boards.Single(bd => bd.Id == board.Id);
            Require(JsonSerializer.Serialize(copied, PilotApplication.Json) == JsonSerializer.Serialize(Current(), PilotApplication.Json),
                "Default JSON round-trip retains every widget coordinate, custom size, section, lock and presentation property.");
            Require(JsonSerializer.Serialize(imported.Settings.Cities, PilotApplication.Json) == JsonSerializer.Serialize(ui.App.Settings.Cities, PilotApplication.Json)
                && imported.Settings.Cities.Sites.Single().Address == site.Address,
                "The real importer preserves complete private sites, aliases, postal codes, buildings and customized addresses in the local default JSON.");
            var importedNative = imported.Settings.Dashboard.Business.BoardOverrides.Single(bd => bd.Id == "business-workspace")
                .Widgets.Single(w => w.Id == nativeHeroId);
            Require(importedNative.GridX == 0 && importedNative.GridY == 3 && importedNative.Height == 300,
                "The local default includes native service overrides as well as custom compositions.");
            Require(imported.Options.Count >= 8 && imported.Settings.Theme == "dark",
                "The complete configuration retains the existing option stores and application settings, not a layout-only fragment.");
            var portablePath = Path.Combine(_root, "portable-layout-settings.json");
            ConfigurationTransferService.Export(portablePath, ui.App.Settings);
            var portable = ConfigurationTransferService.Import(portablePath, new AppSettings());
            Require(JsonSerializer.Serialize(portable.Dashboard.Layout, PilotApplication.Json) == JsonSerializer.Serialize(ui.App.Settings.Dashboard.Layout, PilotApplication.Json),
                "The bounded portable configuration importer also accepts and retains grid coordinates.");
            savedLayout = JsonSerializer.Serialize(Current(), PilotApplication.Json); savedX = Item("grid-a").GridX; savedY = Item("grid-a").GridY;
            Require(ui.Errors.IsEmpty && ui.ExternalRequests == 0,
                "Composition, default export and real persistence complete without JavaScript errors or external network requests.");
        }
        File.Delete(source);
        await using (var ui = await WeatherSelectionUi.CreateAsync(browser))
        {
            await ui.Navigate(); await ui.Page.GetByLabel("Composition du pilotage", new() { Exact = true }).SelectOptionAsync(board.Id); await ui.Idle();
            var restored = ui.App.Settings.Dashboard.Layout.Boards.Single(b => b.Id == board.Id);
            var moved = restored.Widgets.Single(w => w.Id == "grid-a");
            Require(ui.App.IsCachedSource && moved.GridX == savedX && moved.GridY == savedY
                && JsonSerializer.Serialize(restored, PilotApplication.Json) == savedLayout,
                "A new C# application and empty browser profile restore the layout and sizes from disk with only the offline ticket cache.");
            await Call(ui.App, "app.demo");
            var rejected = false;
            try { await Call(ui.App, "deployment.default.save", new { confirmed = true, expectedSha256 = "" }); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "The demonstration is refused access to the real default-configuration export.");
            Require(SettingsRecovery.Read(AppPaths.SettingsFile).Dashboard.Layout.Boards.Any(b => b.Id == board.Id),
                "Entering the demonstration does not replace the real saved composition.");
        }
        var checkedLayout = settings.Dashboard.Layout.Copy();
        checkedLayout.Boards[0].Widgets[0].GridX = 11;
        var refused = false; try { checkedLayout.Validate(); } catch (InvalidDataException) { refused = true; }
        Require(refused, "C# rejects a grid coordinate that would make a card leave the twelve-column canvas.");
        checkedLayout.Normalize();
        Require(checkedLayout.Boards[0].Widgets[0].GridX is null && checkedLayout.Boards[0].Widgets[0].GridY is null,
            "Legacy recovery clears an invalid coordinate pair instead of keeping a partially positioned card.");
    }
}
