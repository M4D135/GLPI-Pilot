using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckMotionInterfaceAsync(string[] args)
    {
        await CheckMotionVisibilityAsync(args);
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1600, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.NoPreference });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(12000);
        var errors = new ConcurrentQueue<string>(); var externalRequests = 0; PilotApplication? mapApplication = null;
        page.PageError += (_, error) => errors.Enqueue(error);
        page.Console += (_, message) => { if (message.Type == "error") errors.Enqueue(message.Text); };
        await context.RouteAsync("**/*", async route =>
        {
            if (EmbeddedMapsService.IsTileUrl(route.Request.Url) && mapApplication?.AllowsMapTileRequest(route.Request.Url) == true)
            {
                await route.FulfillAsync(new() { ContentType = "image/png", BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=") }); return;
            }
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page, NextOpen = CreateBusinessPreviewCsv() };
        WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        var settings = new AppSettings
        { Theme = "light", ExportFolder = Path.Combine(_root, "Exports"), Interface = new() { FollowWindowsMotion = false, OnboardingCompleted = true } };
        foreach (var (label, latitude) in new[] { ("Nord", 48.0), ("Sud", 47.6) })
        {
            var city = new CityDefinition { Name = "Commune fictive " + label, Latitude = latitude, Longitude = 2 };
            settings.Cities.Cities.Add(city);
            settings.Cities.Sites.Add(new() { Name = "Site exemple " + label, CityId = city.Id,
                Address = "1 rue de l’Essai, Commune fictive " + label, Latitude = latitude + .01, Longitude = 2.01 });
        }
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), settings); mapApplication = app;
        await Call(app, "records.test.import");
        var profile = await Call(app, "dashboard.business.settings.get");
        var business = new BusinessDashboardSettings();
        business.WeatherRules.Add(new() { Id = "motion-stock", Label = "Stock fictif", OrangeAbove = 0, RedAbove = 1000 });
        for (var index = 1; index <= 8; index++) business.WeatherRules.Add(new()
        {
            Id = "motion-category-" + index, Label = "Catégorie " + index, Kind = "category", Values = new() { "Catégorie " + index },
            IncludeDescendants = true, OrangeAbove = index % 3 == 0 ? null : index % 3 == 1 ? 100 : 0,
            RedAbove = index % 3 == 0 ? null : index % 3 == 1 ? 200 : 0
        });
        for (var index = 1; index <= 6; index++) business.WeatherRules.Add(new()
        {
            Id = "motion-team-" + index, Label = "Équipe " + index, Kind = "team", Values = new() { "Groupe " + index },
            Visible = index != 1, OrangeAbove = index % 2 == 0 ? 20 : 40, RedAbove = index % 2 == 0 ? 25 : 50
        });
        await Call(app, "dashboard.business.settings.save", new { business, baseFingerprint = profile.GetProperty("fingerprint").GetString() });
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            return await app.ExecuteAsync(op, data, ct);
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (() => { const listeners = new Set(); window.chrome ||= {}; window.chrome.webview = {
              postMessage: message => window.__pilotSend(JSON.stringify(message)),
              addEventListener: (name, handler) => { if (name === 'message') listeners.add(handler); },
              removeEventListener: (name, handler) => listeners.delete(handler)
            }; window.__pilotDeliver = message => { for (const listener of listeners) listener({ data: message }); }; })();
            """);
        async Task Navigate(string route)
        {
            await ClickNavigationAsync(page, route);
            await WaitForNavigationTargetAsync(page, route);
        }
        async Task Apply(InterfacePreferences preferences, string? theme = null)
        {
            await Call(app, "app.appearance", new { preferences, theme }); await delivery;
        }
        async Task Settle()
        {
            await WaitForPageAsync(page, "!document.getAnimations().some(animation=>animation.playState==='running'&&animation.effect?.getComputedTiming().endTime<=2000)");
            await page.EvaluateAsync("""
                async()=>{const workspace=document.querySelector('.workspace');let previous=workspace.scrollTop,stable=0;
                  await new Promise(resolve=>{const tick=()=>{const now=workspace.scrollTop;stable=Math.abs(now-previous)<.1?stable+1:0;previous=now;if(stable>=5)resolve();else requestAnimationFrame(tick);};requestAnimationFrame(tick);});}
                """);
        }
        async Task Capture(string name)
        {
            var argument = Array.IndexOf(args, "--screenshots"); if (argument < 0 || argument + 1 >= args.Length) return;
            Directory.CreateDirectory(args[argument + 1]);
            if (await page.Locator("dialog[open]").CountAsync() == 0)
                await DismissCaptureNotificationsAsync(page);
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[argument + 1], name + ".png"), Animations = ScreenshotAnimations.Allow });
        }
        async Task RecordMotionFrames()
        {
            var argument = Array.IndexOf(args, "--video-frames"); if (argument < 0 || argument + 1 >= args.Length) return;
            var directory = Path.GetFullPath(args[argument + 1]); Directory.CreateDirectory(directory);
            var clock = System.Diagnostics.Stopwatch.StartNew(); var frames = new List<object>();
            for (var index = 0; index < 40; index++)
            {
                var name = "motion-" + index.ToString("D3") + ".png"; var seconds = clock.Elapsed.TotalSeconds;
                await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name), Animations = ScreenshotAnimations.Allow });
                frames.Add(new { file = name, seconds });
                var remaining = TimeSpan.FromMilliseconds((index + 1) * 100) - clock.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(new { viewport = page.ViewportSize, frames, duration = clock.Elapsed.TotalSeconds }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Real Chromium motion frames: " + directory);
        }
        var targets = new[] {
            "[data-weather-scene=sun] .pilot-weather-rays",
            "[data-weather-scene=watch] .pilot-weather-cloud-near",
            "[data-weather-scene=rain] .pilot-weather-rain-particle"
        };
        async Task<JsonElement> Observe(string[] selectors) => await page.EvaluateAsync<JsonElement>("""
            async selectors => {
              const read = () => ({
                time:performance.now(),
                scrollTop:document.querySelector('.workspace')?.scrollTop,
                targets:selectors.map(selector=>{
                  const node=document.querySelector(selector);if(!node)return {selector,missing:true};
                  const animation=node.getAnimations().find(value=>value.animationName?.startsWith('pilot-weather-'));
                  const style=getComputedStyle(node),matrix=node.getScreenCTM?.(),rect=node.getBoundingClientRect();
                  return {selector,visible:node.getClientRects().length>0,inViewport:rect.bottom>0&&rect.top<innerHeight&&rect.right>0&&rect.left<innerWidth,
                    paused:node.closest('.domenia-motion-surface')?.dataset.motionVisible==='false',
                    state:animation?.playState||'none',pending:animation?.pending??false,cssState:style.animationPlayState,clock:animation?.currentTime??null,
                    infinite:animation?.effect?.getTiming().iterations===Infinity,
                    geometry:JSON.stringify([style.transform,style.opacity,matrix?[matrix.a,matrix.b,matrix.c,matrix.d,matrix.e,matrix.f]:null])};
                }),
                cards:JSON.stringify([...document.querySelectorAll('.business-weather-card')].map(node=>{
                  const r=node.getBoundingClientRect();return [r.x,r.y,r.width,r.height].map(value=>Math.round(value*100)/100);
                })),
                values:JSON.stringify([...document.querySelectorAll('.analytics-metric-value')].map(node=>node.textContent))
              });
              const samples=[read()],start=performance.now();
              for(const offset of [350,900]){
                await new Promise(resolve=>{const tick=()=>performance.now()-start>=offset?resolve():requestAnimationFrame(tick);requestAnimationFrame(tick);});
                samples.push(read());
              }
              const stable=samples.every(sample=>sample.cards===samples[0].cards&&sample.values===samples[0].values);
              const moving=selectors.every((_,index)=>samples.every(sample=>sample.targets[index].visible&&sample.targets[index].inViewport&&sample.targets[index].state==='running'&&sample.targets[index].infinite)
                &&samples[1].targets[index].clock>samples[0].targets[index].clock+100
                &&samples[2].targets[index].clock>samples[1].targets[index].clock+100
                &&new Set(samples.map(sample=>sample.targets[index].geometry)).size>1);
              const frozen=selectors.every((_,index)=>samples.every(sample=>sample.targets[index].visible&&sample.targets[index].state!=='running')
                &&new Set(samples.map(sample=>sample.targets[index].geometry)).size===1
                &&new Set(samples.map(sample=>sample.targets[index].clock)).size===1);
              return {moving,frozen,stable,samples};
            }
            """, selectors);
        async Task<JsonElement> MeasureWeatherTravel() => await page.EvaluateAsync<JsonElement>("""
            async () => {
              const cases=[
                {selector:'.business-weather-card:not(.business-weather-hero) [data-weather-scene=sun] .pilot-weather-rays',phase:[0,.125],minimum:8,maximumCycle:12000,tip:true},
                {selector:'.business-weather-card:not(.business-weather-hero) [data-weather-scene=rain] .pilot-weather-cloud-near',phase:[0,.5],minimum:9,maximumCycle:6500},
                {selector:'.business-weather-card:not(.business-weather-hero) [data-weather-scene=rain] .pilot-weather-rain-particle',phase:[.2,.7],minimum:8,maximumCycle:1800}
              ];
              const nodes=cases.map(test=>document.querySelector(test.selector));
              if(nodes.some(node=>!node))return {readable:false,missing:true};
              const scenes=[...new Set(nodes.map(node=>node.closest('svg')))];
              const animations=scenes.flatMap(scene=>scene.getAnimations({subtree:true}));
              const saved=animations.map(animation=>({animation,time:animation.currentTime,state:animation.playState}));
              animations.forEach(animation=>animation.pause());
              const result=[];
              try {
                for(let index=0;index<cases.length;index++){
                  const test=cases[index],node=nodes[index],animation=node.getAnimations().find(value=>value.animationName?.startsWith('pilot-weather-'));
                  if(!animation){result.push({selector:test.selector,readable:false});continue;}
                  const timing=animation.effect.getTiming(),duration=Number(timing.duration),delay=Number(timing.delay),base=Math.ceil(Math.max(0,-delay)/duration)+1;
                  const point=()=>{
                    const line=test.tip?node.querySelector('line'):null,bounds=node.getBBox();
                    const local=new DOMPoint(line?line.x2.baseVal.value:bounds.x+bounds.width/2,line?line.y2.baseVal.value:bounds.y+bounds.height/2);
                    const screen=local.matrixTransform(node.getScreenCTM());return {x:screen.x,y:screen.y};
                  };
                  const samples=[];
                  for(const phase of test.phase){animation.currentTime=delay+(base+phase)*duration;await new Promise(requestAnimationFrame);samples.push(point());}
                  const pixels=Math.hypot(samples[1].x-samples[0].x,samples[1].y-samples[0].y);
                  const cycle=duration*(timing.direction.startsWith('alternate')?2:1);
                  result.push({selector:test.selector,pixels,cycle,readable:pixels>=test.minimum&&cycle<=test.maximumCycle});
                }
              } finally {
                for(const item of saved){item.animation.currentTime=item.time;if(item.state==='running')item.animation.play();}
              }
              return {readable:result.every(value=>value.readable),result};
            }
            """);
        async Task WaitHeroMotion(bool visible) => await WaitForPageAsync(page, """
            visible => {
              const hero=document.querySelector('.business-weather-hero'),node=hero?.querySelector('.pilot-weather-cloud-near');
              if(!node||hero.dataset.motionVisible!==String(visible))return false;
              const animation=node.getAnimations().find(value=>value.animationName?.startsWith('pilot-weather-'));
              const expected=visible?'running':'paused';
              // The observer's attribute precedes the asynchronous CSS pause/play task.
              // Measure only after that task commits; the subsequent clock and geometry checks remain exact.
              return animation?.playState===expected&&!animation.pending&&getComputedStyle(node).animationPlayState===expected;
            }
            """, visible);
        bool Flag(JsonElement observation, string key) => observation.GetProperty(key).GetBoolean();
        async Task<bool> ToolbarAligned(bool singleRow) => await page.EvaluateAsync<bool>("""
            singleRow => {
              const bar=document.querySelector('.pilot-workspace .workspace-commandbar');if(!bar)return false;
              const bounds=bar.getBoundingClientRect();
              const controls=[...bar.querySelectorAll('button,summary,select')].filter(node=>node.getClientRects().length&&!node.closest('.workspace-menu-content')).map(node=>{
                const r=node.getBoundingClientRect();return {left:r.left,right:r.right,top:r.top,bottom:r.bottom,height:r.height,center:r.top+r.height/2};
              });
              if(controls.length<3||controls.some(r=>r.left<bounds.left-1||r.right>bounds.right+1||r.right>innerWidth+1))return false;
              if(singleRow&&Math.max(...controls.map(r=>r.center))-Math.min(...controls.map(r=>r.center))>2)return false;
              for(const a of controls)for(const b of controls){
                const overlap=Math.min(a.bottom,b.bottom)-Math.max(a.top,b.top);
                if(overlap>Math.min(a.height,b.height)/2&&(Math.abs(a.center-b.center)>2||Math.abs(a.height-b.height)>2))return false;
              }
              return document.documentElement.scrollWidth<=innerWidth+1;
            }
            """, singleRow);

        await page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(page);
        await Navigate("pilot-weather"); await Settle();
        var full = app.Settings.Interface.Copy(); full.ReduceMotion = false; full.FollowWindowsMotion = false;
        foreach (var theme in new[] { "light", "dark" })
        {
            await Apply(full, theme); await Settle();
            var observation = await Observe(targets);
            var scene = page.Locator(".pilot-weather-scene[data-weather-scene=rain]").First;
            var before = await scene.ScreenshotAsync(new() { Animations = ScreenshotAnimations.Allow });
            await Task.Delay(370);
            var after = await scene.ScreenshotAsync(new() { Animations = ScreenshotAnimations.Allow });
            Console.WriteLine("MOTION " + theme + ": " + observation.GetRawText());
            Require(Flag(observation, "moving") && Flag(observation, "stable") && !before.SequenceEqual(after) && await ToolbarAligned(true),
                $"{theme}: real sun, cloud and rain clocks advance, SVG matrices and rendered pixels change continuously, while values, card geometry and toolbar alignment stay stable.");
            var travel = await MeasureWeatherTravel();
            Console.WriteLine("WEATHER TRAVEL " + theme + ": " + travel.GetRawText());
            Require(Flag(travel, "readable"),
                $"{theme}: even the small cards give sun-ray tips, cloud layers and falling drops a visible 8–9 pixel minimum travel at a readable continuous pace.");
            await Capture("motion-weather-" + theme);
            Require(await page.EvaluateAsync<bool>("""
                ()=>{const edges=[...document.querySelectorAll('.domenia-widget-light')];return edges.length>0&&edges.every(edge=>{
                  const style=getComputedStyle(edge),after=getComputedStyle(edge,'::after');
                  return parseFloat(style.height)<=2&&style.pointerEvents==='none'&&['none','normal',''].includes(after.content)
                    &&['none','normal',''].includes(getComputedStyle(edge.parentElement.querySelector('.analytics-kpi-emblem,.home-counter-emblem')||edge,'::after').content);
                });}
                """), $"{theme}: widget decoration is confined to a two-pixel edge reflection with no oval or rotating emblem ring and cannot intercept figures or controls.");
            if (theme == "light") await RecordMotionFrames();
        }
        var reduced = full.Copy(); reduced.ReduceMotion = true;
        await Apply(reduced); await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='off'");
        var stopped = await Observe(targets);
        Require(Flag(stopped, "frozen") && Flag(stopped, "stable"), "Changing the app preference stops actual weather movement immediately without replacing measured values or shifting cards.");
        Require(await page.EvaluateAsync<bool>("[...document.querySelectorAll('.pilot-weather-scene')].every(scene=>!scene.getAnimations({subtree:true}).some(animation=>animation.playState==='running'))"),
            "Reduced motion also stops every new halo, sparkle, distant droplet and ripple, while the complete weather drawing remains present.");
        await Apply(full); await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='on'");
        Require(Flag(await Observe(targets), "moving"), "Re-enabling expressive motion resumes the existing weather scenes without navigation or a reload.");
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        var system = full.Copy(); system.FollowWindowsMotion = true;
        await Apply(system); await WaitForPageAsync(page, "document.documentElement.dataset.motionPolicy==='system'&&document.documentElement.dataset.domeniaMotion==='off'");
        Require(Flag(await Observe(targets), "frozen"), "System-following mode stops the real scene under the Windows reduced-motion media preference.");
        await Navigate("settings");
        await page.GetByLabel("Animations", new() { Exact = true }).SelectOptionAsync("full");
        await page.GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.documentElement.dataset.motionPolicy==='full'&&document.documentElement.dataset.domeniaMotion==='on'");
        await Navigate("pilot-weather"); await Settle();
        Require(Flag(await Observe(targets), "moving") && !app.Settings.Interface.ReduceMotion && !app.Settings.Interface.FollowWindowsMotion
            && await page.EvaluateAsync<bool>("matchMedia('(prefers-reduced-motion:reduce)').matches"),
            "Choosing Expressives in the real Settings screen persists the opt-out and animates SVG scenes even while Windows still requests reduced motion.");
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
        var contrast = full.Copy(); contrast.HighContrast = true;
        await Apply(contrast); await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='off'");
        Require(Flag(await Observe(targets), "frozen"), "High contrast leaves complete, readable static weather illustrations without a running decorative loop.");
        await Apply(full);
        await page.EmulateMediaAsync(new() { ForcedColors = ForcedColors.Active });
        await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='off'");
        Require(Flag(await Observe(targets), "frozen"), "Windows forced colors stops every weather loop without hiding the state illustration or changing the measured ticket count.");
        await page.EmulateMediaAsync(new() { ForcedColors = ForcedColors.None });
        await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='on'");

        await Navigate("home"); await Settle();
        var metricMotion = await page.EvaluateAsync<bool>("""
            async()=>{
              const card=document.querySelector('.home-counter[data-metric-known=true]'),icon=card?.querySelector('.home-counter-emblem>.icon'),value=card?.querySelector('.metric-value');
              if(!icon||!value)return false;
              const read=()=>{const r=card.getBoundingClientRect(),v=value.getBoundingClientRect();return JSON.stringify([value.textContent,...[r.x,r.y,r.width,r.height,v.x,v.y,v.width,v.height].map(x=>Math.round(x*100)/100)]);};
              const before=read(),matrix=getComputedStyle(icon).transform;
              await new Promise(resolve=>setTimeout(resolve,700));
              return before===read()&&matrix!==getComputedStyle(icon).transform&&icon.getAnimations().some(animation=>animation.playState==='running');
            }
            """);
        Require(metricMotion, "A real home indicator animates its emblem while its number, label and card rectangle stay fixed.");
        await page.EvaluateAsync("window.__edgeNode=document.querySelector('.home-counter[data-metric-known=true]>.domenia-widget-light')");
        async Task<JsonElement> EdgeState() => await page.EvaluateAsync<JsonElement>("""
            async()=>{const node=window.__edgeNode;if(!node?.isConnected)return {moving:false,frozen:false};
              const read=()=>{const style=getComputedStyle(node),animation=node.getAnimations().find(item=>item.animationName==='domenia-widget-light');
                return {clock:animation?.currentTime??null,state:animation?.playState||'none',pending:animation?.pending||false,position:style.backgroundPosition,opacity:style.opacity};};
              const before=read();await new Promise(resolve=>setTimeout(resolve,450));const after=read();
              return {moving:before.state==='running'&&after.state==='running'&&!before.pending&&!after.pending&&after.clock>before.clock+150&&before.position!==after.position,
                frozen:before.state!=='running'&&after.state!=='running'&&!before.pending&&!after.pending&&before.clock===after.clock&&before.position===after.position&&before.opacity===after.opacity};}
            """);
        var edgeMoving = Flag(await EdgeState(), "moving");
        await Apply(reduced); await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='off'");
        var edgeReduced = Flag(await EdgeState(), "frozen");
        await Apply(full); await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='on'");
        await page.EvaluateAsync("document.querySelector('.workspace').scrollTop=document.querySelector('.workspace').scrollHeight");
        await WaitForPageAsync(page, "window.__edgeNode?.parentElement.dataset.motionVisible==='false'&&!window.__edgeNode.getAnimations().some(animation=>animation.pending)");
        var edgeOffscreen = Flag(await EdgeState(), "frozen");
        await page.Locator(".home-counter[data-metric-known=true]").First.ScrollIntoViewIfNeededAsync();
        await WaitForPageAsync(page, "window.__edgeNode?.parentElement.dataset.motionVisible==='true'&&!window.__edgeNode.getAnimations().some(animation=>animation.pending)");
        Require(edgeMoving && edgeReduced && edgeOffscreen && Flag(await EdgeState(), "moving"),
            "The thin edge reflection visibly changes position when active, freezes with reduced motion or offscreen, and resumes on return without replacing the card.");
        await Capture("motion-home-indicators");
        await Navigate("pilot-weather"); await Settle();

        await page.SetViewportSizeAsync(600, 600); await Settle();
        Require(await ToolbarAligned(false), "At a narrow width the same weather controls wrap into aligned rows without overlap, overflow or a stretched group summary.");
        await Capture("motion-weather-compact");
        var hero = page.Locator(".business-weather-hero");
        await hero.ScrollIntoViewIfNeededAsync();
        await WaitHeroMotion(true);
        await page.EvaluateAsync("document.querySelector('.workspace').scrollTop=document.querySelector('.workspace').scrollHeight");
        await WaitHeroMotion(false);
        var hidden = await Observe([".business-weather-hero .pilot-weather-cloud-near"]);
        await hero.ScrollIntoViewIfNeededAsync();
        await WaitHeroMotion(true);
        var returned = await Observe([".business-weather-hero .pilot-weather-cloud-near"]);
        Console.WriteLine("MOTION OFFSCREEN: " + hidden.GetRawText());
        Console.WriteLine("MOTION RETURNED: " + returned.GetRawText());
        Require(Flag(hidden, "frozen") && Flag(returned, "moving"),
            "Scrolling a real weather card out of view pauses its clocks and geometry, and returning it to view resumes the same scene.");

        await page.SetViewportSizeAsync(1600, 1000); await Navigate("records"); await Settle();
        var stableTable = await page.EvaluateAsync<bool>("""
            async () => {
              const rows=[...document.querySelectorAll('.page-content table tr')].slice(0,8);if(rows.length<3)return false;
              const read=()=>JSON.stringify(rows.map(node=>{const r=node.getBoundingClientRect();return [node.textContent,...[r.x,r.y,r.width,r.height].map(value=>Math.round(value*100)/100)];}));
              const before=read(), background=getComputedStyle(document.body,'::before').transform;
              await new Promise(resolve=>setTimeout(resolve,900));
              return before===read()&&background!==getComputedStyle(document.body,'::before').transform;
            }
            """);
        Require(stableTable && externalRequests == 0 && errors.IsEmpty,
            "The real ticket rows and values stay fixed while the continuous background moves, with no external request or browser error.");
        await Capture("motion-tickets-stable");
        if (args.Contains("--screenshots"))
        {
            foreach (var (width, height) in new[] { (1600, 1000), (1280, 800) })
            foreach (var theme in new[] { "light", "dark" })
            {
                await page.SetViewportSizeAsync(width, height); await Apply(full, theme);
                foreach (var route in new[] { "pilot", "pilot-participants", "pilot-sites", "terrain-review" })
                {
                    await Navigate(route); await Settle();
                    await Capture($"gui-{route}-{theme}-{width}x{height}");
                }
            }
        }
        await delivery;
    }
}
