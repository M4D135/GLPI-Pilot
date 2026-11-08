using System.Collections.Concurrent;
using Microsoft.Playwright;

var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "web", "index.html"))) root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("Run from the repository.");
var evidence = Path.Combine(root, "artifacts", "terrain-panel-close"); Directory.CreateDirectory(evidence);
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
var cases = new[] { "module-import", "pending-read", "escape", "repeated-close", "failed-render", "already-ready", "page-disposed" };
var failed = 0;
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
foreach (var scenario in cases)
{
    await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 900 }, Locale = "fr-FR" });
    var page = await context.NewPageAsync(); page.SetDefaultTimeout(5000);
    var errors = new ConcurrentQueue<string>(); page.PageError += (_, error) => errors.Enqueue(error);
    var importReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseImport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await page.RouteAsync("**/*", async route =>
    {
        var uri = new Uri(route.Request.Url);
        if (uri.Host != "terrain-close.test") { errors.Enqueue("Unexpected network host: " + uri.Host); await route.AbortAsync(); return; }
        if (uri.AbsolutePath == "/fixture.html")
        {
            var html = (await File.ReadAllTextAsync(Path.Combine(root, "web", "index.html")))
                .Replace("src=\"js/app.js\"", "src=\"/tests/TerrainPanelCloseUiChecks/fixture.js\"", StringComparison.Ordinal)
                .Replace("</head>", "<link rel=\"stylesheet\" href=\"/tests/FieldPreparationUiChecks/fixture.css\"></head>", StringComparison.Ordinal);
            await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = html }); return;
        }
        if (uri.AbsolutePath == "/js/pages/terrain-review.js" && scenario is "module-import" or "page-disposed")
        { importReached.TrySetResult(); await releaseImport.Task; }
        var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        var path = Path.GetFullPath(Path.Combine(root, relative.StartsWith("tests/", StringComparison.Ordinal) ? relative : "web/" + relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        { errors.Enqueue("Missing fixture asset: " + relative); await route.FulfillAsync(new() { Status = 404 }); return; }
        var mime = Path.GetExtension(path) switch { ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
        await route.FulfillAsync(new() { ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
    });
    try
    {
        await page.GotoAsync("https://terrain-close.test/fixture.html");
        await page.Locator("#app[data-fixture-ready='true']").WaitForAsync();
        var waitingForRead = scenario is "pending-read" or "escape" or "repeated-close" or "failed-render";
        if (waitingForRead) await page.EvaluateAsync("window.terrainCloseFixture.holdRead=true");
        await page.Locator("[data-open-panel='review']").ClickAsync();
        var panel = page.Locator("dialog[open][data-workspace-panel='review']"); await panel.WaitForAsync();
        await panel.EvaluateAsync("node=>node.addEventListener('pilot:closed',()=>window.terrainCloseFixture.closed++)");
        if (scenario is "module-import" or "page-disposed") await importReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else if (waitingForRead) await page.Locator("#app[data-review-waiting='true']").WaitForAsync();
        else await panel.Locator("[data-terrain-panel='review'][data-ready='true']").WaitForAsync();
        if (scenario == "escape") await page.Keyboard.PressAsync("Escape");
        else await panel.Locator(":scope > .modal-heading").GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        if (scenario == "repeated-close")
        {
            await panel.Locator(":scope > .modal-heading").GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
            await page.Keyboard.PressAsync("Escape");
        }
        if (scenario != "already-ready")
            Check(await panel.CountAsync() == 1, "A loading panel must remain until its renderer and cleanup have settled.");
        if (scenario == "page-disposed") await page.EvaluateAsync("window.terrainCloseFixture.dispose()");
        releaseImport.TrySetResult();
        if (waitingForRead) await page.EvaluateAsync(scenario == "failed-render" ? "window.terrainCloseFixture.failRead()" : "window.terrainCloseFixture.releaseRead()");
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.Locator(".terrain-map-page[aria-busy='true']").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Check(await page.EvaluateAsync<int>("window.terrainCloseFixture.closed") == 1, "The dialog must emit one closed event, not one per click.");
        var expectedReads = scenario is "failed-render" or "page-disposed" ? 1 : 2;
        Check(await page.EvaluateAsync<int>("window.terrainCloseFixture.mapReads") == expectedReads, "Map refresh count must match one guarded close.");
        if (scenario == "failed-render")
            Check((await page.Locator(".terrain-map-page .terrain-errors").InnerTextAsync()).Contains("Fictitious review read failure", StringComparison.Ordinal), "The read failure must remain visible rather than being swallowed.");
        Check(errors.IsEmpty, string.Join("\n", errors));
        Console.WriteLine("PASS: actual terrain review close / " + scenario);
    }
    catch (Exception error)
    {
        failed++; Console.Error.WriteLine("FAIL: " + scenario + "\n" + error + "\n" + string.Join("\n", errors));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, scenario + ".png"), FullPage = true });
        await File.WriteAllTextAsync(Path.Combine(evidence, scenario + ".txt"), await page.Locator("body").InnerTextAsync());
    }
    finally { releaseImport.TrySetResult(); }
}
Console.WriteLine($"{cases.Length - failed}/{cases.Length} terrain panel close browser checks passed.");
return failed == 0 ? 0 : 1;
