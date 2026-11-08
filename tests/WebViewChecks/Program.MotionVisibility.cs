using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckMotionVisibilityAsync(string[] args)
    {
        // This part deliberately starts with the normal, empty profile and its
        // default Windows-following preference, before the weather simulation.
        var previousRoot = _root;
        _root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Motion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", _root);
        try { await CheckMotionVisibilityCoreAsync(args); }
        finally
        {
            var completedRoot = _root; _root = previousRoot;
            Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", previousRoot);
            try { Directory.Delete(completedRoot, true); } catch (IOException) { }
        }
    }

    private static async Task CheckMotionVisibilityCoreAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.NoPreference });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var externalRequests = 0;
        page.PageError += (_, error) => errors.Enqueue(error);
        page.Console += (_, message) => { if (message.Type == "error") errors.Enqueue(message.Text); };
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            { await route.FulfillAsync(new() { Status = 404 }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page }; WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json=>window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        var settings = new AppSettings { ExportFolder = Path.Combine(_root, "Exports") };
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), settings);
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            return await app.ExecuteAsync(op, data, ct);
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (()=>{
              const listeners=new Set();window.chrome||={};window.chrome.webview={
                postMessage:message=>window.__pilotSend(JSON.stringify(message)),
                addEventListener:(name,handler)=>{if(name==='message')listeners.add(handler);},
                removeEventListener:(name,handler)=>listeners.delete(handler)
              };window.__pilotDeliver=message=>{for(const listener of listeners)listener({data:message});};
              window.__motionEvidence={entrances:[],curves:[]};
              document.addEventListener('animationstart',event=>{
                const node=event.target;if(!(node instanceof Element))return;
                if(event.animationName==='domenia-surface-enter'){
                  const matrix=new DOMMatrixReadOnly(getComputedStyle(node).transform);
                  window.__motionEvidence.entrances.push({node,time:performance.now(),x:matrix.e,y:matrix.f,scale:matrix.a,
                    page:node.closest('.page-content')?.dataset.page,fixed:node.classList.contains('domenia-surface-fixed')});
                }
                if(event.animationName==='domenia-chart-draw'){
                  const chart=node.closest('svg');
                  const data=()=>JSON.stringify([...chart.querySelectorAll('polyline,path,line,text')].map(item=>
                    [item.tagName,item.getAttribute('points'),item.getAttribute('d'),item.getAttribute('stroke-dasharray'),item.textContent]));
                  const curve={node,chart,data:data(),samples:[],done:false};window.__motionEvidence.curves.push(curve);
                  const start=performance.now();
                  const read=()=>curve.samples.push({time:performance.now()-start,offset:parseFloat(getComputedStyle(node).strokeDashoffset),data:data()});
                  read();
                  const tick=()=>{if(!node.isConnected){curve.detached=true;return;}
                    if(performance.now()-start<1800){read();requestAnimationFrame(tick);}
                    else{read();curve.done=true;}};requestAnimationFrame(tick);
                }
              });
            })();
            """);
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        ILocator MotionDialog() => page.Locator(".settings-workspace");
        async Task Navigate(string route) { await ClickNavigationAsync(page, route); await WaitForNavigationTargetAsync(page, route); }
        async Task Settle() => await WaitForPageAsync(page, """
            !document.getAnimations().some(animation=>animation.playState==='running'&&animation.effect?.getComputedTiming().endTime<=2500)
            """);
        async Task OpenMotion()
        {
            await Navigate("settings");
            await MotionDialog().Locator(".motion-preview").ScrollIntoViewIfNeededAsync();
        }
        async Task Choose(string mode)
        {
            await MotionDialog().GetByLabel("Animations", new() { Exact = true }).SelectOptionAsync(mode);
            await MotionDialog().GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync();
            await WaitForPageAsync(page, "mode=>document.documentElement.dataset.motionPolicy===mode", mode);
            await MotionDialog().Locator(".motion-preview").ScrollIntoViewIfNeededAsync();
        }
        async Task CloseMotion() => await Navigate("home");
        async Task Capture(string name)
        {
            var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return;
            Directory.CreateDirectory(args[index + 1]);
            if (await page.Locator("dialog[open]").CountAsync() == 0) await DismissCaptureNotificationsAsync(page);
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[index + 1], name + ".png"), Animations = ScreenshotAnimations.Allow });
        }
        async Task RecordHomeFrames()
        {
            var index = Array.IndexOf(args, "--home-video-frames"); if (index < 0 || index + 1 >= args.Length) return;
            var directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
            var heading = (await page.Locator(".home-overview-heading").BoundingBoxAsync())!;
            var counter = (await page.Locator(".home-counter[data-metric-known=true]").First.BoundingBoxAsync())!;
            var clock = System.Diagnostics.Stopwatch.StartNew(); var frames = new List<object>();
            for (var frame = 0; frame < 32; frame++)
            {
                var progress = frame / 31.0;
                var x = progress < .65 ? heading.X + 30 + (heading.Width - 60) * progress / .65
                    : heading.X + heading.Width - 30 + (counter.X + counter.Width / 2 - heading.X - heading.Width + 30) * (progress - .65) / .35;
                var y = progress < .65 ? heading.Y + heading.Height / 2
                    : heading.Y + heading.Height / 2 + (counter.Y + counter.Height / 2 - heading.Y - heading.Height / 2) * (progress - .65) / .35;
                await page.Mouse.MoveAsync((float)x, (float)y);
                var name = "home-" + frame.ToString("D3") + ".png"; var seconds = clock.Elapsed.TotalSeconds;
                await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name), Animations = ScreenshotAnimations.Allow });
                frames.Add(new { file = name, seconds, pointer = new { x, y } });
                var remaining = TimeSpan.FromMilliseconds((frame + 1) * 125) - clock.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(new { viewport = page.ViewportSize, frames, duration = clock.Elapsed.TotalSeconds }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Real Chromium Home motion frames: " + directory);
        }
        async Task<JsonElement> Observe(string selector)
        {
            // A preference/media update changes CSS before its asynchronous
            // play/pause task commits. Sample only a committed animation state.
            await WaitForPageAsync(page, """
                selector=>{const node=document.querySelector(selector);return !!node&&node.getAnimations()
                  .filter(item=>/^(domenia-|pilot-weather-)/.test(item.animationName||'')).every(item=>!item.pending);}
                """, selector);
            return await page.EvaluateAsync<JsonElement>("""
            async selector=>{
              const node=document.querySelector(selector);if(!node)return {missing:true,moving:false,frozen:false,stable:false};
              const container=node.closest('.motion-preview,.home-overview-heading');
              const read=()=>{const style=getComputedStyle(node),bounds=node.getBoundingClientRect();
                const animation=node.getAnimations().find(item=>/^(domenia-|pilot-weather-)/.test(item.animationName||''));
                const content=[...container.querySelectorAll('h2,strong,.home-source-line,.motion-preview-status')];
                return {time:performance.now(),clock:animation?.currentTime??null,state:animation?.playState||'none',pending:animation?.pending??false,
                  geometry:JSON.stringify([style.transform,style.translate,style.scale,style.opacity,node.getScreenCTM?.()?.toString()]),
                  visible:bounds.width>0&&bounds.height>0&&bounds.bottom>0&&bounds.top<innerHeight&&style.visibility!=='hidden'&&style.display!=='none',
                  content:JSON.stringify(content.map(item=>{const r=item.getBoundingClientRect();return [item.textContent,...[r.x,r.y,r.width,r.height].map(x=>Math.round(x*100)/100)];}))};};
              const samples=[read()],start=performance.now();
              for(const offset of [350,1000]){await new Promise(resolve=>{const tick=()=>performance.now()-start>=offset?resolve():requestAnimationFrame(tick);requestAnimationFrame(tick);});samples.push(read());}
              return {samples,stable:samples.every(item=>item.content===samples[0].content),
                moving:samples.every(item=>item.visible&&item.state==='running'&&!item.pending)&&samples[1].clock>samples[0].clock+100&&samples[2].clock>samples[1].clock+100&&new Set(samples.map(item=>item.geometry)).size>1,
                frozen:samples.every(item=>item.visible&&item.state!=='running'&&!item.pending)&&new Set(samples.map(item=>item.geometry)).size===1&&new Set(samples.map(item=>item.clock)).size===1};
            }
            """, selector);
        }
        async Task<bool> Decorated(string selector) => await page.EvaluateAsync<bool>("""
            selector=>{const node=document.querySelector(selector);return !!node&&['domenia-orbs','domenia-glow-ring','domenia-spotlight'].every(name=>node.querySelectorAll(`:scope > .${name}`).length===1)
              &&node.querySelectorAll(':scope > .domenia-orbs > .domenia-orb').length===3;}
            """, selector);
        bool Flag(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
        const string homeOrb = ".home-overview-heading > .domenia-orbs > .domenia-orb.a";
        const string previewCloud = ".settings-workspace .motion-preview .pilot-weather-cloud-near";

        await page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(page);
        await WaitForNavigationTargetAsync(page, "home"); await Settle();
        var fresh = await Observe(homeOrb);
        var before = await page.Locator(".home-overview-heading").ScreenshotAsync(new() { Animations = ScreenshotAnimations.Allow });
        await Task.Delay(550);
        var after = await page.Locator(".home-overview-heading").ScreenshotAsync(new() { Animations = ScreenshotAnimations.Allow });
        Console.WriteLine("MOTION FRESH HOME: " + fresh.GetRawText());
        Require(app.Result is null && !app.IsTestMode && app.Settings.Interface.FollowWindowsMotion && !app.Settings.Interface.ReduceMotion
            && await Decorated(".home-overview-heading") && Flag(fresh, "moving") && Flag(fresh, "stable") && !before.SequenceEqual(after)
            && await page.Locator(".home-counter[data-metric-known=false]").CountAsync() == 3,
            "A fresh normal profile visibly animates the Home header without data or Expressives opt-in, while its three unknown ticket values remain unknown.");
        Require(await page.EvaluateAsync<bool>("""
            ()=>{const entries=window.__motionEvidence.entrances.filter(item=>item.page==='home'&&!item.fixed&&item.node.isConnected);
              return entries.length>=3&&entries.some(item=>Math.abs(item.y)>=8||item.scale<.995)
                &&Math.max(...entries.map(item=>item.time))-Math.min(...entries.map(item=>item.time))>=30
                &&entries.every(item=>getComputedStyle(item.node).transform==='none');}
            """), "Home cards arrive in a measured spatial cascade and settle into their final, stable hit-test rectangles.");
        await Capture("motion-fresh-home-dark");

        var header = page.Locator(".home-overview-heading"); var headerBounds = (await header.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(headerBounds.X + 30, headerBounds.Y + 30);
        await WaitForPageAsync(page, "document.querySelector('.home-overview-heading')?.dataset.motionPointer==='true'");
        var pointer = await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('.home-overview-heading > .domenia-spotlight')).backgroundImage");
        await page.Mouse.MoveAsync(headerBounds.X + headerBounds.Width - 30, headerBounds.Y + headerBounds.Height - 30);
        await WaitForPageAsync(page, """
            ()=>{const halo=document.querySelector('.domenia-cursor-halo');return !!halo&&halo.classList.contains('is-visible')&&Number(getComputedStyle(halo).opacity)>.5;}
            """);
        Require(await page.EvaluateAsync<bool>("""
            previous=>{const halo=document.querySelector('.domenia-cursor-halo'),spot=document.querySelector('.home-overview-heading > .domenia-spotlight');
              const host=spot.parentElement,r=host.getBoundingClientRect(),h=halo.getBoundingClientRect();
              return getComputedStyle(halo).pointerEvents==='none'&&halo.getAttribute('aria-hidden')==='true'
                &&Math.abs(h.x+h.width/2-(r.right-30))<2&&Math.abs(h.y+h.height/2-(r.bottom-30))<2
                &&getComputedStyle(spot).backgroundImage!==previous&&document.elementFromPoint(r.right-30,r.bottom-30)!==halo;}
            """, pointer), "Moving the real mouse moves the visible halo and header reflection without intercepting the underlying controls.");

        // Exercise the same replacement primitive as asynchronous page renders,
        // keeping the real visible host and its original interactive children.
        await page.EvaluateAsync("""
            async()=>{const host=document.querySelector('.home-overview-heading');window.__motionHomeHost=host;
              window.__motionHomeEntranceCount=window.__motionEvidence.entrances.filter(item=>item.node===host).length;
              const {replaceChildren}=await import('/js/ui.js');
              replaceChildren(host,...[...host.children].filter(node=>!node.matches('.domenia-orbs,.domenia-glow-ring,.domenia-spotlight')));}
            """);
        await WaitForPageAsync(page, "document.querySelector('.home-overview-heading > .domenia-orbs > .domenia-orb.a')!==null");
        // The replacement creates fresh CSSAnimation objects. Wait for their
        // pending play task, as for the existing weather pause/resume checks.
        await WaitForPageAsync(page, """
            selector=>{const animation=document.querySelector(selector)?.getAnimations().find(item=>item.animationName==='domenia-float-a');
              return animation?.playState==='running'&&!animation.pending;}
            """, homeOrb);
        var repaired = await Observe(homeOrb);
        Console.WriteLine("MOTION REPAIRED HOME: " + repaired.GetRawText());
        Require(await Decorated(".home-overview-heading") && Flag(repaired, "moving") && Flag(repaired, "stable")
            && await page.EvaluateAsync<bool>("window.__motionHomeHost===document.querySelector('.home-overview-heading')&&window.__motionHomeEntranceCount===window.__motionEvidence.entrances.filter(item=>item.node===window.__motionHomeHost).length"),
            "Replacing a live Home header's children repairs each missing decoration once and resumes visible movement without replaying the host entrance.");

        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='off'");
        await OpenMotion(); await Settle();
        var paused = await Observe(previewCloud);
        Require(Flag(paused, "frozen") && Flag(paused, "stable")
            && await MotionDialog().Locator(".motion-preview-status").InnerTextAsync() == "Animations en pause à la demande de Windows."
            && await MotionDialog().GetByLabel("Animations", new() { Exact = true }).InputValueAsync() == "system"
            && await Button("Régler les animations").CountAsync() == 0,
            "Settings explains Windows' reduced-motion pause and shows a complete, genuinely stationary preview before any choice is made.");
        await Capture("motion-control-system-paused");
        await Choose("full"); await Settle();
        var active = await Observe(previewCloud);
        before = await MotionDialog().Locator(".motion-preview-illustration").ScreenshotAsync(new() { Animations = ScreenshotAnimations.Allow });
        await Task.Delay(370);
        after = await MotionDialog().Locator(".motion-preview-illustration").ScreenshotAsync(new() { Animations = ScreenshotAnimations.Allow });
        Require(Flag(active, "moving") && Flag(active, "stable") && !before.SequenceEqual(after)
            && !app.Settings.Interface.ReduceMotion && !app.Settings.Interface.FollowWindowsMotion
            && !AppSettings.Load().Interface.FollowWindowsMotion
            && await page.EvaluateAsync<bool>("matchMedia('(prefers-reduced-motion:reduce)').matches"),
            "One explicit Settings choice starts the real preview's clocks, geometry and pixels despite Windows reduction and saves that choice in the local profile.");
        await Capture("motion-control-expressive");
        await CloseMotion(); await page.ReloadAsync(); await ReadyAsync(page);
        await WaitForNavigationTargetAsync(page, "home"); await Settle();
        Require(await Decorated(".home-overview-heading") && Flag(await Observe(homeOrb), "moving")
            && await Button("Régler les animations").CountAsync() == 0
            && !AppSettings.Load().Interface.FollowWindowsMotion,
            "Reloading retains the expressive choice and exactly one live set of Home decorations while Windows remains reduced.");
        await RecordHomeFrames();
        await OpenMotion(); await Choose("reduce"); await Settle();
        Require(Flag(await Observe(previewCloud), "frozen") && app.Settings.Interface.ReduceMotion
            && !app.Settings.Interface.FollowWindowsMotion && AppSettings.Load().Interface.ReduceMotion
            && await MotionDialog().Locator(".motion-preview-status").InnerTextAsync() == "Animations réduites dans votre profil.",
            "The Reduce choice in Settings stops actual movement immediately and records an explicit reduced profile, distinct from following Windows.");
        await Choose("system"); await Settle();
        var systemPaused = Flag(await Observe(previewCloud), "frozen");
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
        await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='on'");
        await Settle();
        var systemResumed = await Observe(previewCloud);
        Console.WriteLine("MOTION SYSTEM RESUMED: " + systemResumed.GetRawText());
        Require(systemPaused && Flag(systemResumed, "moving") && app.Settings.Interface.FollowWindowsMotion
            && !app.Settings.Interface.ReduceMotion && AppSettings.Load().Interface.FollowWindowsMotion,
            "Following Windows restores the system policy and resumes the same preview when that media preference changes, without another saved choice.");
        await CloseMotion();
        // Freeze the real Home entrance at its first frame: pointerdown must not
        // move this navigation button away before the ensuing native click.
        var loadData = Button("Collecter depuis GLPI");
        await loadData.ScrollIntoViewIfNeededAsync(); await Settle();
        await loadData.EvaluateAsync("""
            async control=>{const surface=control.closest('.domenia-motion-surface');
              surface.classList.remove('domenia-surface-enter');void surface.offsetWidth;
              surface.classList.add('domenia-surface-enter');surface.style.setProperty('--d-card-delay','0ms');
              const animation=surface.getAnimations().find(item=>item.animationName==='domenia-surface-enter');
              animation.pause();animation.currentTime=0;await animation.ready;}
            """);
        await loadData.ClickAsync(); await WaitForNavigationTargetAsync(page, "data");
        Require(await page.Locator(".page-content[data-page=data]").IsVisibleAsync() && app.Result is null,
            "The first real navigation click during the Home entrance reaches Data without losing its button hit target.");
        await Navigate("settings");
        var enrichment = page.GetByRole(AriaRole.Checkbox, new() { Name = "Lectures ciblées", Exact = true });
        await enrichment.ScrollIntoViewIfNeededAsync(); await Settle();
        // Hold the real entrance at its initial frame so the regression does
        // not depend on CI speed. The next action remains an actual mouse click.
        await enrichment.EvaluateAsync("""
            async control=>{const surface=control.closest('.domenia-motion-surface');
              surface.classList.remove('domenia-surface-enter');void surface.offsetWidth;
              surface.classList.add('domenia-surface-enter');surface.style.setProperty('--d-card-delay','0ms');
              const animation=surface.getAnimations().find(item=>item.animationName==='domenia-surface-enter');
              animation.pause();animation.currentTime=0;await animation.ready;}
            """);
        var controlBefore = (await enrichment.BoundingBoxAsync())!;
        await enrichment.CheckAsync();
        var controlAfter = (await enrichment.BoundingBoxAsync())!;
        Require(await enrichment.IsCheckedAsync()
            && Math.Abs(controlAfter.X - controlBefore.X) < .1 && Math.Abs(controlAfter.Y - controlBefore.Y) < .1
            && Math.Abs(controlAfter.Width - controlBefore.Width) < .1 && Math.Abs(controlAfter.Height - controlBefore.Height) < .1
            && app.Settings.Interface.HiddenNavigation.Contains("enrichment"),
            "The first real click during a Settings form entrance checks its checkbox without moving its hit rectangle or applying the draft.");
        await enrichment.UncheckAsync();
        var preview = page.Locator(".settings-workspace .motion-preview"); await preview.ScrollIntoViewIfNeededAsync(); await Settle();
        await page.GetByLabel("Animations", new() { Exact = true }).SelectOptionAsync("reduce");
        Require(await preview.GetAttributeAsync("data-motion-active") == "true" && !app.Settings.Interface.ReduceMotion,
            "The Settings preview reports the applied preference while an unapplied dropdown change remains a draft.");
        await Button("Appliquer").ClickAsync(); await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='off'");
        await preview.ScrollIntoViewIfNeededAsync(); await Settle();
        var appliedPreview = await Observe(".settings-workspace .motion-preview .pilot-weather-cloud-near");
        Console.WriteLine("MOTION SETTINGS APPLIED: " + JsonSerializer.Serialize(new { active = await preview.GetAttributeAsync("data-motion-active"), persisted = AppSettings.Load().Interface.ReduceMotion, observation = appliedPreview }));
        Require(await preview.GetAttributeAsync("data-motion-active") == "false"
            && Flag(appliedPreview, "frozen")
            && AppSettings.Load().Interface.ReduceMotion,
            "Applying Settings updates the real inline preview and persists its stationary state.");
        await page.GetByLabel("Animations", new() { Exact = true }).SelectOptionAsync("full"); await Button("Appliquer").ClickAsync();
        await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='on'");

        var source = QualityFixture();
        source.Headers.Add("Catégorie");
        source.Rows = Enumerable.Range(1, 24).Select(index => new[] { index.ToString(), "Ticket de mouvement " + index, "Nouveau", index <= 12 ? "Poste" : index <= 20 ? "Réseau" : "Téléphonie" }).ToList();
        source.ExpectedTotal = 24; source.Quality = null; source.Complete = true; source.Warnings.Clear();
        app.LoadQualityFixture(source); await delivery;
        var layout = (await Call(app, "dashboard.layout.get", new { businessView = "workspace" })).Deserialize<DashboardLayoutSettings>(PilotApplication.Json)!;
        var board = layout.Boards[0];
        board.Sections.Insert(0, new() { Key = "motion-evidence", Title = "Répartition de contrôle" });
        board.Widgets.Insert(0, new() { Id = "motion-pareto", Title = "Catégories du relevé", Kind = "distribution", SourceKey = "category", Presentation = "pareto", Section = "motion-evidence", Span = 12 });
        await Call(app, "dashboard.layout.save", new { businessView = "workspace", layout }); await delivery;
        await Navigate("pilot");
        await page.Locator("[data-widget-id=motion-pareto] .analytics-column-chart svg").ScrollIntoViewIfNeededAsync();
        await WaitForPageAsync(page, "window.__motionEvidence.curves.some(item=>item.node.isConnected&&item.done)");
        var chart = await page.EvaluateAsync<JsonElement>("""
            ()=>{const curve=window.__motionEvidence.curves.find(item=>item.node.closest('[data-widget-id=motion-pareto]')&&item.node.isConnected);
              if(!curve)return {drawn:false};window.__motionCurve=curve;
              const offsets=curve.samples.map(item=>item.offset),threshold=curve.chart.querySelector('line[stroke-dasharray]');
              return {drawn:offsets[0]>20&&Math.max(...offsets)-Math.min(...offsets)>20&&Math.abs(offsets.at(-1))<.1,
                stable:curve.samples.every(item=>item.data===curve.data),labels:[...curve.chart.querySelectorAll('g>text:first-of-type')].map(item=>item.textContent),
                threshold:threshold?.getAttribute('stroke-dasharray'),clean:!curve.node.classList.contains('domenia-chart-draw')&&!curve.node.style.getPropertyValue('--d-chart-length'),
                firstOffset:offsets[0],lastOffset:offsets.at(-1),samples:curve.samples.length};}
            """);
        Console.WriteLine("MOTION REAL PARETO: " + chart.GetRawText());
        Require(Flag(chart, "drawn") && Flag(chart, "stable") && Flag(chart, "clean")
            && chart.GetProperty("threshold").GetString() == "6 4"
            && chart.GetProperty("labels").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "12", "8", "4" }),
            "The real 24-ticket Pareto curve draws progressively once while its 12/8/4 values, data points and dashed 80-percent threshold remain exact.");
        await Capture("motion-real-chart");
        await page.EvaluateAsync("document.querySelector('.workspace').scrollTop=document.querySelector('.workspace').scrollHeight");
        await WaitForPageAsync(page, "window.__motionCurve.chart.dataset.motionVisible==='false'");
        await page.Locator("[data-widget-id=motion-pareto] .analytics-column-chart svg").ScrollIntoViewIfNeededAsync(); await Settle();
        Require(await page.EvaluateAsync<bool>("window.__motionEvidence.curves.filter(item=>item.node===window.__motionCurve.node).length===1&&window.__motionCurve.node.isConnected&&!window.__motionCurve.node.classList.contains('domenia-chart-draw')"),
            "Returning to an existing chart does not erase and redraw its already-readable data curve.");

        await page.EvaluateAsync("window.__motionPilotHeader=document.querySelector('.pilot-intro')");
        await Button("Personnaliser la vue").ClickAsync(); await Button("Terminer la composition").ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('.pilot-intro > .domenia-orbs')!==null");
        Require(await Decorated(".pilot-intro")
            && await page.EvaluateAsync<bool>("window.__motionPilotHeader===document.querySelector('.pilot-intro')&&document.querySelector('.workspace > .page-heading > .domenia-spotlight')!==null")
            && externalRequests == 0 && errors.IsEmpty,
            "The real Pilotage header retains exactly one decoration set after repeated redraws, the visible page heading is reconciled too, and the whole path has no browser error or external request.");
        await delivery;
    }
}
