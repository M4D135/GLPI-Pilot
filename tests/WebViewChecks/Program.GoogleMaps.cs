using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckGoogleMapFrameAsync(IBrowser browser, string web, GoogleMapFrame model)
    {
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1080, Height = 740 } });
        var requests = new List<string>(); var forbidden = new List<string>();
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host == GoogleMapsService.MapHost)
            {
                var path = Path.GetFullPath(Path.Combine(web, uri.AbsolutePath.TrimStart('/')));
                if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.AbortAsync(); return; }
                var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", _ => "application/octet-stream" };
                await route.FulfillAsync(new() { BodyBytes = await File.ReadAllBytesAsync(path), ContentType = mime }); return;
            }
            if (route.Request.Url == model.EmbedUrl)
            {
                requests.Add(route.Request.Url);
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = "<!doctype html><html><head><meta charset='utf-8'></head><body><h1>Carte interceptée pour le test local</h1></body></html>" }); return;
            }
            forbidden.Add(route.Request.Url); await route.AbortAsync();
        });
        var page = await context.NewPageAsync(); await page.GotoAsync("https://" + GoogleMapsService.MapHost + "/maps.html");
        Require(requests.Count == 0 && await page.Locator("#map-frame").GetAttributeAsync("src") is null
            && await page.EvaluateAsync<bool>("typeof window.chrome?.webview === 'undefined'"),
            "The dedicated map shell loads without any Google request or application bridge.");
        var raw = System.Text.Json.JsonSerializer.Serialize(model, PilotApplication.Json);
        Require(await page.EvaluateAsync<bool>("json => window.showGoogleMap(JSON.parse(json))", raw), "The dedicated map renderer accepts the explicitly prepared documented Embed URL.");
        await page.FrameLocator("#map-frame").GetByRole(AriaRole.Heading, new() { Name = "Carte interceptée pour le test local", Exact = true }).WaitForAsync();
        Require(requests.Count == 1 && forbidden.Count == 0 && await page.Locator("#map-frame").GetAttributeAsync("referrerpolicy") == "strict-origin-when-cross-origin"
            && await page.Locator("html").GetAttributeAsync("data-theme") == model.Theme,
            "The themed iframe loads only the intercepted map request and sends the restricted origin referrer required by API-key restrictions.");
    }
}
