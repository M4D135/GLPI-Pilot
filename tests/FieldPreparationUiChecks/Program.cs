using System.Collections.Concurrent;
using Microsoft.Playwright;

var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "web", "index.html"))) root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("Run from the repository.");
var evidence = Path.Combine(root, "artifacts", "field-preparation-ui"); Directory.CreateDirectory(evidence);
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM") });
var checks = FieldUiTests.Cases(evidence);
var failed = 0;
foreach (var check in checks)
{
    await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 950 } });
    var page = await context.NewPageAsync(); page.SetDefaultTimeout(10000);
    var errors = new ConcurrentQueue<string>(); page.PageError += (_, error) => errors.Enqueue(error);
    await page.RouteAsync("**/*", async route =>
    {
        var uri = new Uri(route.Request.Url);
        if (uri.Host != "field-ui.test") { errors.Enqueue("Unexpected network host: " + uri.Host); await route.AbortAsync(); return; }
        if (uri.AbsolutePath == "/fixture.html")
        {
            var html = await File.ReadAllTextAsync(Path.Combine(root, "web", "index.html"));
            html = html.Replace("src=\"js/app.js\"", "src=\"/tests/FieldPreparationUiChecks/fixture.js\"", StringComparison.Ordinal)
                .Replace("</head>", "<link rel=\"stylesheet\" href=\"/tests/FieldPreparationUiChecks/fixture.css\"></head>", StringComparison.Ordinal);
            await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = html }); return;
        }
        var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        var path = Path.GetFullPath(Path.Combine(root, relative.StartsWith("tests/", StringComparison.Ordinal) ? relative : "web/" + relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        { errors.Enqueue("Missing fixture asset: " + relative); await route.FulfillAsync(new() { Status = 404, Body = "Missing" }); return; }
        var mime = Path.GetExtension(path) switch { ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
        await route.FulfillAsync(new() { ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
    });
    try
    {
        // Match the desktop's secure virtual host and leave its strict CSP intact.
        await page.GotoAsync("https://field-ui.test/fixture.html");
        await page.Locator("[data-field-plan]").First.WaitForAsync();
        await check.Run(page);
        FieldUiTests.Check(await page.EvaluateAsync<bool>("() => { const w=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT);let n;while(n=w.nextNode()){if(n.parentElement?.closest('.field-workspace,.field-modal')&&/^(null|undefined)$/.test(n.textContent.trim()))return false;}return true; }"), "An optional UI child was rendered as null or undefined.");
        FieldUiTests.Check(errors.IsEmpty, string.Join("\n", errors)); Console.WriteLine("PASS: " + check.Name);
    }
    catch (Exception error)
    {
        failed++; Console.Error.WriteLine("FAIL: " + check.Name + "\n" + error + "\n" + string.Join("\n", errors));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "failure-" + failed + ".png"), FullPage = true });
    }
}
Console.WriteLine($"{checks.Count - failed}/{checks.Count} field browser checks passed.");
return failed == 0 ? 0 : 1;
