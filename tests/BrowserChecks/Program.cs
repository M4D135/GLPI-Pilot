using System.Text;
using GlpiPilot.Core;
using Microsoft.Playwright;

string? browserPath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
if (string.IsNullOrWhiteSpace(browserPath))
{
    Console.Error.WriteLine("Définir GLPI_TEST_CHROMIUM vers un Chromium de test installé.");
    return 2;
}
string basic = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "basic-glpi.html"));
string temporary = Path.Combine(Path.GetTempPath(), "GlpiPilot-BrowserChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new()
{
    ExecutablePath = browserPath,
    Headless = true,
    Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
});
var checks = new List<(string Name, Func<Task> Run)>
{
    ("Page courante : valeurs et périmètre explicite", CheckCurrent),
    ("Pagination sûre : trois pages et total réconcilié", CheckPagination),
    ("Pagination sans changement : arrêt et conservation des données", CheckNoAdvance),
    ("Délai de clic dépassé : lignes déjà lues conservées", CheckClickTimeout),
    ("Annulation : conservation des lignes déjà lues", CheckCancel),
    ("Sélection visuelle : Échap annule immédiatement", CheckPickerCancel),
    ("Pagination ambiguë : aucun clic", CheckAmbiguous),
    ("Bouton métier hors pagination : aucun clic", CheckBusinessButton),
    ("Sélecteur personnalisé dangereux : action refusée", CheckCustomBusinessButton),
    ("Authentification : tableau refusé en présence d’un mot de passe", CheckLogin),
    ("Export natif : téléchargement CSV reçu et enregistré", CheckDownload)
};
int failures = 0;
try
{
    foreach (var check in checks)
    {
        try { await check.Run().WaitAsync(TimeSpan.FromSeconds(30)); Console.WriteLine("PASS " + check.Name); }
        catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + check.Name + "\n" + e); }
    }
    Console.WriteLine($"{checks.Count - failures}/{checks.Count} vérifications d’intégration réussies.");
}
finally
{
    Directory.Delete(temporary, recursive: true);
}
return failures == 0 ? 0 : 1;

async Task<Harness> Open()
{
    var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
    await context.RouteAsync("http://glpi.test/**", async route =>
    {
        var uri = new Uri(route.Request.Url);
        if (uri.AbsolutePath == "/export.csv")
            await route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "text/csv; charset=utf-8",
                Headers = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"tickets.csv\"" },
                Body = "ID;Titre\r\n001;Écran cassé\r\n"
            });
        else await route.FulfillAsync(new() { Status = 200, ContentType = "text/html; charset=utf-8", Body = basic });
    });
    var page = await context.NewPageAsync();
    await page.GotoAsync("http://glpi.test/front/computer.php");
    var service = new BrowserService();
    service.AttachForTesting(context, "http://glpi.test/front/computer.php");
    return new(context, page, service);
}

AppSettings Settings() => new()
{
    PortalUrl = "http://glpi.test/front/computer.php",
    SelectorOrigin = "http://glpi.test",
    TimeoutSeconds = 1,
    MaxPages = 10,
    MaxRows = 100,
    ExportFolder = temporary
};

async Task<TableChoice> Table(Harness h)
{
    var tables = await h.Service.InspectAsync(h.Page, CancellationToken.None);
    Equal(1, tables.Count);
    return tables[0];
}

async Task CheckCurrent()
{
    await using var h = await Open();
    var table = await Table(h);
    var result = await h.Service.ExtractAsync(table, false, Settings(), null, CancellationToken.None);
    Equal(2, result.Rows.Count); Equal(1, result.Pages); Equal(5, result.ExpectedTotal);
    True(result.Complete, "Une page courante lue sans avertissement doit être complète pour ce périmètre.");
    Equal("Page courante", result.Scope);
    True(result.Rows.SelectMany(r => r).Contains("000123"), "Les zéros initiaux doivent être conservés.");
    True(result.Rows.SelectMany(r => r).Contains("P-MA-001"), "Le nom du poste doit être extrait.");
    Equal("Aucune action métier déclenchée", await h.Page.Locator("#business-action").InnerTextAsync());
}

async Task CheckPagination()
{
    await using var h = await Open();
    var result = await h.Service.ExtractAsync(await Table(h), true, Settings(), null, CancellationToken.None);
    Equal(5, result.Rows.Count); Equal(3, result.Pages); Equal(5, result.ExpectedTotal);
    True(result.Complete, string.Join(" | ", result.Warnings));
    Equal(0, result.Warnings.Count);
    True(result.Rows.SelectMany(r => r).Contains("=00127"), "Une valeur ressemblant à une formule doit rester intacte pendant l’extraction.");
    Equal("Aucune action métier déclenchée", await h.Page.Locator("#business-action").InnerTextAsync());
}

async Task CheckNoAdvance()
{
    await using var h = await Open();
    await h.Page.EvaluateAsync("""
        () => {
          const old = document.querySelector('#next'); const replacement = old.cloneNode(true);
          replacement.addEventListener('click', () => { window.stalledClicks = (window.stalledClicks || 0) + 1; });
          old.replaceWith(replacement);
        }
        """);
    var result = await h.Service.ExtractAsync(await Table(h), true, Settings(), null, CancellationToken.None);
    Equal(2, result.Rows.Count); Equal(1, result.Pages); True(!result.Complete, "Un clic sans changement ne doit pas produire un succès.");
    Equal(1, await h.Page.EvaluateAsync<int>("() => window.stalledClicks"));
    True(result.Warnings.Any(w => w.Contains("page suivante", StringComparison.OrdinalIgnoreCase)), "L’échec de pagination doit être expliqué.");
}

async Task CheckCancel()
{
    await using var h = await Open();
    using var cancellation = new CancellationTokenSource();
    var progress = new InlineProgress<ExtractionProgress>(_ => cancellation.Cancel());
    var result = await h.Service.ExtractAsync(await Table(h), true, Settings(), progress, cancellation.Token);
    Equal(2, result.Rows.Count); Equal(1, result.Pages); True(!result.Complete, "Une annulation doit rester partielle.");
    True(result.Warnings.Any(w => w.Contains("demande de l’utilisateur", StringComparison.Ordinal)), "L’annulation doit être décrite.");
    Equal("Page 1 sur 3", await h.Page.Locator("#page-number").InnerTextAsync());
}

async Task CheckClickTimeout()
{
    await using var h = await Open();
    await h.Page.EvaluateAsync("""
        () => {
          const overlay = document.createElement('div');
          overlay.style.cssText = 'position:fixed;inset:0;z-index:2147483600;background:transparent';
          document.body.appendChild(overlay);
        }
        """);
    var result = await h.Service.ExtractAsync(await Table(h), true, Settings(), null, CancellationToken.None);
    Equal(2, result.Rows.Count); Equal(1, result.Pages); True(!result.Complete, "Un clic bloqué doit retourner les lignes déjà lues.");
    True(result.Warnings.Count > 0, "Le délai dépassé doit être signalé.");
}

async Task CheckPickerCancel()
{
    await using var h = await Open();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var task = h.Service.PickAsync(h.Page, "table", null, cancellation.Token);
    await h.Page.Locator("[data-glpi-pilot-ui=instruction]").WaitForAsync();
    await h.Page.Keyboard.PressAsync("Escape");
    try
    {
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        throw new Exception("Échap doit interrompre le choix sans fournir de sélection.");
    }
    catch (OperationCanceledException) { }
    Equal(0, await h.Page.Locator("[data-glpi-pilot-ui=instruction]").CountAsync());
}

async Task CheckAmbiguous()
{
    await using var h = await Open();
    await h.Page.EvaluateAsync("""
        () => {
          const pager = document.querySelector('.pagination');
          pager.innerHTML = '<span class="pagination-info">Affichage de 1 à 2 sur 5 éléments</span><a href="/?page=2" id="next-one">Suivant</a><a href="/?page=99" id="next-two">Suivant</a>';
          window.ambiguousClicks = 0;
          pager.querySelectorAll('a').forEach(a => a.addEventListener('click', () => window.ambiguousClicks++));
        }
        """);
    var result = await h.Service.ExtractAsync(await Table(h), true, Settings(), null, CancellationToken.None);
    Equal(2, result.Rows.Count); True(!result.Complete, "Une pagination ambiguë doit être partielle.");
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.ambiguousClicks"));
    True(result.Warnings.Any(w => w.Contains("Plusieurs", StringComparison.Ordinal)), "L’ambiguïté doit être expliquée.");
}

async Task CheckBusinessButton()
{
    await using var h = await Open();
    await h.Page.EvaluateAsync("() => document.querySelector('#next').remove()");
    var result = await h.Service.ExtractAsync(await Table(h), true, Settings(), null, CancellationToken.None);
    Equal(2, result.Rows.Count); True(!result.Complete, "Un total non atteint doit être signalé.");
    Equal("Aucune action métier déclenchée", await h.Page.Locator("#business-action").InnerTextAsync());
}

async Task CheckCustomBusinessButton()
{
    await using var h = await Open();
    await h.Page.EvaluateAsync("""
        () => {
          const nav = document.createElement('nav');
          nav.innerHTML = '<button id="archive-records" type="button">Archiver</button>';
          document.body.appendChild(nav); window.archiveClicks = 0;
          nav.firstElementChild.addEventListener('click', () => window.archiveClicks++);
        }
        """);
    var settings = Settings(); settings.NextSelector = "#archive-records";
    var result = await h.Service.ExtractAsync(await Table(h), true, settings, null, CancellationToken.None);
    Equal(2, result.Rows.Count); True(!result.Complete, "Un sélecteur métier doit être refusé.");
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.archiveClicks"));
}

async Task CheckLogin()
{
    await using var h = await Open();
    var selected = await Table(h);
    await h.Page.EvaluateAsync("""
        () => { const input = document.createElement('input'); input.type = 'password'; input.value = 'DO-NOT-READ-THIS'; document.body.prepend(input); }
        """);
    Equal(0, (await h.Service.InspectAsync(h.Page, CancellationToken.None)).Count);
    var result = await h.Service.ExtractAsync(selected, false, Settings(), null, CancellationToken.None);
    Equal(0, result.Rows.Count); True(!result.Complete, "Une page d’authentification ne doit pas être exportée.");
    True(result.Warnings.Any(w => w.Contains("Authentification", StringComparison.Ordinal)), "L’arrêt pour authentification doit être expliqué.");
    True(!result.Warnings.Any(w => w.Contains("DO-NOT-READ-THIS", StringComparison.Ordinal)), "Une valeur de mot de passe ne doit pas apparaître dans les diagnostics.");
}

async Task CheckDownload()
{
    await using var h = await Open();
    await h.Page.EvaluateAsync("""
        () => { const a = document.createElement('a'); a.id = 'native-export'; a.href = '/export.csv'; a.textContent = 'Exporter'; document.body.prepend(a); }
        """);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var receiving = h.Service.CaptureDownloadAsync(h.Page, temporary, cancellation.Token);
    await h.Page.Locator("#native-export").ClickAsync();
    string path = await receiving;
    True(File.Exists(path), "Le téléchargement doit être enregistré.");
    True(path.EndsWith("tickets.csv", StringComparison.Ordinal), "Le nom natif doit être conservé avec un préfixe unique.");
    Equal("ID;Titre\r\n001;Écran cassé\r\n", await File.ReadAllTextAsync(path, Encoding.UTF8));
    Equal(0, Directory.GetFiles(temporary, "*.part").Length);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Attendu : {expected}\nObtenu : {actual}");
}

static void True(bool condition, string reason)
{
    if (!condition) throw new Exception(reason);
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class Harness(IBrowserContext context, IPage page, BrowserService service) : IAsyncDisposable
{
    public IBrowserContext Context { get; } = context;
    public IPage Page { get; } = page;
    public BrowserService Service { get; } = service;
    public ValueTask DisposeAsync() => Service.DisposeAsync();
}
