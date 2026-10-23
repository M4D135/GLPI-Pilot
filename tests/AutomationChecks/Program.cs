using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GlpiPilot.Core;
using Microsoft.Playwright;

string? browserPath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
if (string.IsNullOrWhiteSpace(browserPath))
{
    Console.Error.WriteLine("Définir GLPI_TEST_CHROMIUM vers un Chromium de test installé.");
    return 2;
}
string temporary = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Automation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1");
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", temporary);
// The downloaded fixture population and the visible sample describe the same
// tickets. Adapter-only DOM fixtures use arbitrary IDs and remain unchanged.
string fixture10 = (await Fixture("native-glpi10.html"))
    .Replace("<td>42</td>", "<td>0003</td>", StringComparison.Ordinal)
    .Replace("item[Ticket][42]", "item[Ticket][3]", StringComparison.Ordinal);
string fixture11 = (await Fixture("native-glpi11.html"))
    .Replace("<td>7</td>", "<td>0003</td>", StringComparison.Ordinal);
string fixtureLegacy = (await Fixture("native-legacy.html")).Replace("PC ancien", "Paris A", StringComparison.Ordinal);
string fixtureLegacyRealistic = await Fixture("native-legacy-realistic.html");
string basic = await Fixture("basic-glpi.html");
const string target = "http://glpi.test/glpi/front/computer.php?criteria%5B0%5D%5Bvalue%5D=0005%20Paris&sort=1&order=ASC&start=2";
const string legacyTarget = "http://glpi.test/glpi/front/ticket.php";
const string csvAll = "\uFEFF\"ID\";\"Nom\";\r\n\"0001\";\"Paris A\";\r\n\"0002\";\"Paris B\";\r\n\"0003\";\"Paris C\";\r\n\"0004\";\"Paris D\";\r\n\"0005\";\"=1+1\";\r\n";
const string csvPartial = "\uFEFFID;Nom\r\n0003;Paris C\r\n0004;Paris D\r\n";
const string ready = "<!doctype html><html><title>GLPI — Recherche</title><body><a href='/logout.php'>Déconnexion</a><table class='search-results' id='results'><thead><tr><th>ID</th><th>Nom</th></tr></thead><tbody><tr><td>0005</td><td>Paris</td></tr></tbody></table></body></html>";
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new()
{
    ExecutablePath = browserPath,
    Headless = true,
    Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
});
var checks = new List<(string Name, Func<Task> Run)>
{
    ("Adresse mémorisée : filtres conservés, paramètres SSO retirés, première page", CheckTargetPolicy),
    ("SSO terminé sur le tableau de bord : retour automatique à la recherche exacte", CheckDashboardReturn),
    ("SSO ayant retiré les filtres : recherche complète réouverte", CheckLostFilters),
    ("Nouvel onglet GLPI : choix automatique devant une connexion restée ouverte", CheckPopupChoice),
    ("SSO discret : souvenir de moi et connexion affichés puis collecte réduite", () => CheckInteractiveSso("complete")),
    ("SSO discret : affichage manuel conservé après la validation", () => CheckInteractiveSso("manual-show")),
    ("SSO discret : masquage manuel respecté tant que le formulaire reste inchangé", () => CheckInteractiveSso("manual-hide")),
    ("SSO discret : annulation rend seulement la fenêtre automatique discrète", () => CheckInteractiveSso("cancel")),
    ("SSO discret : contrôles masqués et redirection passive sans prise de focus", CheckInvisibleSso),
    ("SSO discret : validation dans une iframe IdP visible", CheckAuthenticationIframe),
    ("SSO discret : seule la fenêtre IdP ouverte par le portail est révélée", CheckAuthenticationPopup),
    ("Un clic GLPI 10 : page 2 visible, cinq lignes natives et Excel automatique", CheckNative10),
    ("Annulation avant Excel : reçu natif intact et aucune phase Fichier prêt", () => CheckPipelineCancellation("excel-before")),
    ("Annulation après Excel : fichier terminé conservé, aucun faux succès", () => CheckPipelineCancellation("excel-after")),
    ("Annulation avant publication : le CSV seul ne devient pas un faux succès", () => CheckPipelineCancellation("final")),
    ("Annulation avant CSV vide : aucune écriture démarrée après la demande", CheckCancelledEmptySave),
    ("Un clic : villes MA/Mars/Marseille enrichies dans CSV et Excel, reçu intact", () => CheckNativeCities(true)),
    ("Un clic sans Excel : CSV enrichi principal et métadonnées natives préservées", () => CheckNativeCities(false)),
    ("Un clic GLPI 11 : section All pages choisie et CSV incomplet signalé", CheckNative11Partial),
    ("Formulaire natif ancien : choix CSV toutes-pages puis téléchargement", CheckNativeLegacy),
    ("Export toutes-pages ambigu : arrêt avant téléchargement", CheckNativeAmbiguous),
    ("Mode pagination automatique : retour page 1 avec filtres et cinq lignes", CheckAutomaticPagination),
    ("Chargement AJAX : tableau de présentation ignoré en attendant la recherche", CheckAjaxReadiness),
    ("Repères personnalisés : menu submit et formaction métier refusés sans clic", CheckCustomSafety),
    ("Autre instance GLPI ouverte : seule l’origine cible est retenue", CheckOtherGlpiOrigin),
    ("Portail dans une iframe visible : recherche et export natif complet", CheckIframeNative),
    ("Recherche GLPI vide : compteur zéro et fichiers sans faux résultat", CheckEmptySearch),
    ("Navigateur discret : fenêtre dédiée réduite sans focus automatique", CheckBackgroundFocus),
    ("Navigateur déjà réduit : conservé discret puis restauré sur demande", CheckRestoreOpenWindow),
    ("Popup éphémère : cible CDP disparue sans faux avertissement de navigateur", CheckTransientWindowTarget),
    ("Legacy réel : six tableaux, export adjacent CSV -3 et 828 lignes", () => CheckRealisticLegacy(false)),
    ("Legacy ancien : Select2 masqué, disquette input image et 828 lignes natives", () => CheckRealisticLegacy(true)),
    ("CSV natif 831 lignes : contrôles C0/C1 conservés, fichier intact et Excel valide", CheckNativeTextControls),
    ("CSV reçu HTML : fichier intact, échec explicite et aucun repli silencieux", () => CheckUnreadableNativePreserved(false)),
    ("CSV reçu binaire : fichier intact, échec explicite et aucun repli silencieux", () => CheckUnreadableNativePreserved(true)),
    ("CSV illisible : même avec un contrôle d’affichage, aucun faux Excel", CheckUnreadableNativeRejected),
    ("Export indisponible stable : arrêt rapide et diagnostic sans valeurs privées", CheckUnsupportedNative),
    ("Afficher tout : option native existante, 828 lignes vérifiées puis Excel", CheckDisplayAll),
    ("Afficher tout incomplet ou au-delà de la limite : aucun fichier complet", CheckDisplayAllRejected),
    ("Gestion de fenêtre bloquée : délai borné, sémaphore libéré et annulation rapide", CheckWindowManagementTimeout)
};
var checkFilter = Environment.GetEnvironmentVariable("GLPI_TEST_FILTER");
if (!string.IsNullOrWhiteSpace(checkFilter))
{
    checks = checks.Where(check => check.Name.Contains(checkFilter, StringComparison.OrdinalIgnoreCase)).ToList();
    if (checks.Count == 0) { Console.Error.WriteLine("Aucun contrôle ne correspond au filtre."); Directory.Delete(temporary, true); return 2; }
}
int failures = 0;
try
{
    foreach (var check in checks)
    {
        try { await check.Run().WaitAsync(TimeSpan.FromSeconds(35)); Console.WriteLine("PASS " + check.Name); }
        catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + check.Name + "\n" + e); }
    }
    Console.WriteLine($"{checks.Count - failures}/{checks.Count} vérifications d’automatisation réussies.");
}
finally
{
    Directory.Delete(temporary, recursive: true);
    Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", null);
    Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", null);
}
return failures == 0 ? 0 : 1;

Task<string> Fixture(string name) => File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

AppSettings Settings(string url = target) => new()
{
    PortalUrl = url,
    SelectorOrigin = "http://glpi.test",
    TimeoutSeconds = 12,
    ListTimeoutSeconds = 5,
    DetailTimeoutSeconds = 5,
    ResumePagination = false,
    DownloadTimeoutSeconds = 10,
    MaxPages = 10,
    MaxRows = 100,
    AutoMode = "native",
    AutoSaveXlsx = true,
    ExportFolder = Path.Combine(temporary, Guid.NewGuid().ToString("N"))
};

async Task<Harness> Create(Func<IRoute, Task> handler, string? initialUrl = null, FocusCallTrace? focus = null)
{
    var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
    await context.RouteAsync("**/*", async route =>
    {
        if (new Uri(route.Request.Url).Host is not ("glpi.test" or "other-glpi.test")) { await route.AbortAsync(); return; }
        await handler(route);
    });
    var page = await context.NewPageAsync();
    if (focus != null)
    {
        context = focus.WrapContext(context);
        page = focus.WrapPage(page);
    }
    var service = new BrowserService();
    service.AttachForTesting(context, target);
    var logs = new ConcurrentQueue<string>();
    service.Log += logs.Enqueue;
    if (Environment.GetEnvironmentVariable("GLPI_TEST_TRACE") == "1") service.Log += line => Console.WriteLine("TRACE " + line);
    if (initialUrl != null) await page.GotoAsync(initialUrl);
    return new(context, page, service, logs);
}

Task Html(IRoute route, string body) => route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = body });
// A real browser navigation keeps each fixture request interceptable. A synthetic
// 302 from Route.Fulfill can bypass interception of the redirected request.
Task Redirect(IRoute route, string location) => Html(route, "<html><body><script>location.replace(" + JsonSerializer.Serialize(location) + ");</script></body></html>");
Task Download(IRoute route, string body) => route.FulfillAsync(new()
{
    ContentType = "text/csv; charset=utf-8",
    Headers = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"glpi.csv\"" },
    Body = body
});

Task CheckTargetPolicy()
{
    var raw = target + "&code=secret-code&state=secret-state&SAMLResponse=secret-saml#list";
    Equal(target + "#list", TargetUrlPolicy.PersistedTarget(raw));
    var first = TargetUrlPolicy.FirstPage(target);
    Equal(target.Replace("start=2", "start=0", StringComparison.Ordinal), first);
    True(TargetUrlPolicy.IncludesSearch(target + "&additional=1", target), "Des paramètres supplémentaires ne doivent pas retirer la recherche.");
    True(!TargetUrlPolicy.IncludesSearch("http://glpi.test/glpi/front/computer.php", target), "Une URL sans filtre ne doit pas être validée comme la recherche demandée.");
    Equal("http://glpi.test/glpi/front/computer.php", AppPaths.SafeUrl(target));
    Equal(target, TargetUrlPolicy.PersistedTarget(target + "#access_token=secret-token"));
    var settings = Settings("http://glpi.test/glpi/front/computer.php?criteria=value-secret&access_token=token-secret");
    var data = new ExtractionResult { Headers = new() { "Nom" }, Rows = new() { new[] { "cell-secret" } }, ExpectedTotal = 1 };
    var diagnostic = DiagnosticReport.Create(settings, data, "NAV https://glpi.test/list?filter=query-secret\npassword=password-secret access_token=access-secret");
    foreach (string secret in new[] { "value-secret", "token-secret", "cell-secret", "query-secret", "password-secret", "access-secret" })
        True(!diagnostic.Contains(secret, StringComparison.Ordinal), "Le diagnostic doit exclure les valeurs privées : " + secret);
    return Task.CompletedTask;
}

async Task CheckDashboardReturn()
{
    var focus = new FocusCallTrace();
    bool authenticated = false;
    var targetQueries = new ConcurrentQueue<string>();
    await using var h = await Create(async route =>
    {
        var uri = new Uri(route.Request.Url);
        if (uri.AbsolutePath == "/glpi/front/computer.php")
        {
            targetQueries.Enqueue(uri.Query);
            if (!authenticated) await Redirect(route, "/login.php");
            else await Html(route, ready);
        }
        else if (uri.AbsolutePath == "/login.php")
            await Html(route, "<html><body><form id='login'>Connexion SSO en cours</form><script>setTimeout(()=>location.replace('/dashboard.php'),120)</script></body></html>");
        else if (uri.AbsolutePath == "/dashboard.php")
        {
            authenticated = true;
            await Html(route, "<html><title>GLPI</title><body><a href='/logout.php'>Déconnexion</a><h1>Tableau de bord</h1></body></html>");
        }
        else await route.FulfillAsync(new() { Status = 404 });
    }, focus: focus);
    var result = await h.Service.OpenTargetAsync(Settings(), CancellationToken.None);
    Equal(target, result.Url);
    Equal(2, targetQueries.Count);
    True(targetQueries.All(query => query == new Uri(target).Query), "Le retour après SSO doit reprendre tous les paramètres de recherche.");
    True(h.Logs.Any(line => line.Contains("retour automatique", StringComparison.Ordinal)), "Le retour depuis le tableau de bord doit être journalisé.");
    Equal(0, focus.BringToFrontCalls);
}

async Task CheckLostFilters()
{
    int requestedWithFilters = 0;
    int requestedWithoutFilters = 0;
    await using var h = await Create(async route =>
    {
        var uri = new Uri(route.Request.Url);
        if (uri.AbsolutePath == "/glpi/front/computer.php")
        {
            if (uri.Query.Length == 0) { requestedWithoutFilters++; await Html(route, ready); }
            else if (++requestedWithFilters == 1) await Redirect(route, "/sso/callback");
            else await Html(route, ready);
        }
        else if (uri.AbsolutePath == "/sso/callback")
            await Html(route, "<html><body>Connexion SSO<script>setTimeout(()=>location.replace('/glpi/front/computer.php'),100)</script></body></html>");
        else await route.FulfillAsync(new() { Status = 404 });
    });
    var result = await h.Service.OpenTargetAsync(Settings(), CancellationToken.None);
    Equal(target, result.Url);
    Equal(2, requestedWithFilters); Equal(1, requestedWithoutFilters);
    True(h.Logs.Any(line => line.Contains("recherche complète mémorisée", StringComparison.Ordinal)), "La recherche retirée par le SSO doit être restaurée explicitement.");
}

async Task CheckPopupChoice()
{
    var focus = new FocusCallTrace();
    int loginRequests = 0, targetRequests = 0;
    await using var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath == "/login.php")
        {
            loginRequests++;
            return Html(route, "<html><body><form id='login'><h1>Ancien écran SSO</h1><input type='password'></form><script>window.testClicks=0;document.addEventListener('click',()=>window.testClicks++);</script></body></html>");
        }
        targetRequests++;
        return Html(route, ready);
    }, "http://glpi.test/login.php", focus);
    var messages = new ConcurrentQueue<string>();
    var challengeShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    h.Service.ConnectionProgress += messages.Enqueue;
    h.Service.Log += message => { if (message.Contains("validation visible détectée", StringComparison.Ordinal)) challengeShown.TrySetResult(); };
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var settling = h.Service.WaitForTargetAsync(Settings(), cancellation.Token);
    await challengeShown.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Equal(1, loginRequests); Equal(0, targetRequests); Equal(1, focus.BringToFrontCalls);
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.testClicks"));
    var popupTask = h.Page.WaitForPopupAsync();
    await h.Page.EvaluateAsync("url => window.open(url, '_blank')", target);
    var popup = focus.WrapPage(await popupTask);
    await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
    var found = await h.Service.FindBestPageAsync(Settings(), h.Page, CancellationToken.None);
    True(ReferenceEquals(popup, found), "L’onglet GLPI ouvert doit dépasser le score de l’écran de connexion.");
    var settled = await settling;
    True(ReferenceEquals(popup, settled), "L’attente automatique doit terminer sur le nouvel onglet.");
    True(ReferenceEquals(popup, h.Service.ActivePage), "L’onglet actif mémorisé doit suivre le portail.");
    True(messages.Any(message => message.Contains("Connexion établie", StringComparison.Ordinal)), "La progression doit annoncer la fin réelle du SSO.");
    Equal(1, loginRequests); Equal(1, targetRequests); Equal(1, focus.BringToFrontCalls);
    True(h.Service.BackgroundMode, "L’onglet GLPI prêt doit rendre le navigateur discret après la validation temporaire.");
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.testClicks"));
    Equal("http://glpi.test/login.php", h.Page.Url);
    Equal(2, h.Context.Pages.Count);
}

string InteractiveSsoMarkup() => """
    <!doctype html><html><body><h1>Authentification</h1>
    <form onsubmit="window.testSubmits++; return false">
      <label><input id="remember" type="checkbox">Se souvenir de moi</label>
      <button type="submit">Se connecter</button>
    </form>
    <script>window.testClicks=0;window.testSubmits=0;document.addEventListener('click',()=>window.testClicks++);</script>
    </body></html>
    """;

async Task CheckInteractiveSso(string mode)
{
    var focus = new FocusCallTrace { ReportedWindowState = "minimized" };
    await using var h = await Create(route => Html(route,
        new Uri(route.Request.Url).AbsolutePath == "/intermediate" ? InteractiveSsoMarkup() : ready),
        "http://glpi.test/intermediate", focus);
    var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    h.Service.Log += message => { if (message.Contains("validation visible détectée", StringComparison.Ordinal)) shown.TrySetResult(); };
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var settling = h.Service.WaitForTargetAsync(Settings(), cancellation.Token);
    await shown.Task.WaitAsync(TimeSpan.FromSeconds(4));
    Equal(1, focus.BringToFrontCalls);
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.testClicks"));
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.testSubmits"));
    True(!await h.Page.Locator("#remember").IsCheckedAsync(), "La case de persistance appartient à l’utilisateur.");
    True(!h.Service.BackgroundMode, "La validation nécessaire doit restaurer uniquement le navigateur dédié.");
    if (mode == "manual-show") await h.Service.ShowBrowserAsync(h.Page, CancellationToken.None);
    if (mode == "manual-hide")
    {
        await h.Service.HideBrowserAsync(CancellationToken.None);
        await Task.Delay(1100);
        Equal(1, focus.BringToFrontCalls);
        True(h.Service.BackgroundMode, "La validation inchangée ne doit pas annuler un masquage manuel.");
    }
    if (mode == "cancel")
    {
        cancellation.Cancel();
        try { await settling; throw new Exception("La connexion devait être annulée."); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Equal("http://glpi.test/intermediate", h.Page.Url);
        Equal(0, await h.Page.EvaluateAsync<int>("() => window.testSubmits"));
    }
    else
    {
        // Completion is simulated externally. BrowserService never checks or submits the form.
        await h.Page.GotoAsync(target);
        var page = await settling;
        Equal(target, page.Url);
    }
    Equal(mode == "manual-show" ? 2 : 1, focus.BringToFrontCalls);
    Equal(mode != "manual-show", h.Service.BackgroundMode);
    Equal(mode == "manual-show" ? "normal" : "minimized", focus.RequestedWindowStates.Last());
}

async Task CheckInvisibleSso()
{
    var focus = new FocusCallTrace { ReportedWindowState = "minimized" };
    var requests = 0;
    await using var h = await Create(route => Html(route, ++requests == 1 ?
        "<html><body><form hidden id='login'><input type='password'><button>Se connecter</button></form><p>Redirection en cours</p><script>setTimeout(()=>location.reload(),1200)</script></body></html>" : ready), focus: focus);
    var result = await h.Service.OpenTargetAsync(Settings(), CancellationToken.None);
    Equal(target, result.Url);
    Equal(2, requests);
    Equal(0, focus.BringToFrontCalls);
    True(h.Service.BackgroundMode, "Des contrôles cachés ne doivent pas interrompre la collecte discrète.");
}

async Task CheckAuthenticationIframe()
{
    var focus = new FocusCallTrace { ReportedWindowState = "minimized" };
    var authenticated = false;
    await using var h = await Create(route => Html(route, new Uri(route.Request.Url).Host == "other-glpi.test"
        ? InteractiveSsoMarkup() : authenticated ? ready : "<html><body><iframe src='http://other-glpi.test/idp'></iframe></body></html>"), target, focus);
    var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    h.Service.Log += message => { if (message.Contains("validation visible détectée", StringComparison.Ordinal)) shown.TrySetResult(); };
    var settling = h.Service.WaitForTargetAsync(Settings(), CancellationToken.None);
    await shown.Task.WaitAsync(TimeSpan.FromSeconds(4));
    Equal(1, focus.BringToFrontCalls);
    var idp = h.Page.Frames.Single(frame => new Uri(frame.Url).Host == "other-glpi.test");
    Equal(0, await idp.EvaluateAsync<int>("() => window.testClicks"));
    True(!await idp.Locator("#remember").IsCheckedAsync(), "La validation IdP intégrée ne doit pas être actionnée.");
    authenticated = true; await h.Page.GotoAsync(target); await settling;
    True(h.Service.BackgroundMode, "La collecte reprend discrètement après l’authentification intégrée.");
}

async Task CheckAuthenticationPopup()
{
    var focus = new FocusCallTrace { ReportedWindowState = "minimized" };
    var authenticated = false;
    await using var h = await Create(route => Html(route, new Uri(route.Request.Url).Host == "other-glpi.test"
        ? InteractiveSsoMarkup() : authenticated ? ready : "<html><body><p>Connexion en cours</p></body></html>"), target, focus);
    // An unrelated login in the same dedicated context must remain untouched.
    var unrelated = focus.WrapPage(await h.Context.NewPageAsync());
    await unrelated.GotoAsync("http://other-glpi.test/unrelated");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    h.Service.Log += message => { if (message.Contains("validation visible détectée", StringComparison.Ordinal)) shown.TrySetResult(); };
    var settling = h.Service.WaitForTargetAsync(Settings(), cancellation.Token);
    await Task.Delay(900);
    Equal(0, focus.BringToFrontCalls);
    var popupPending = h.Page.WaitForPopupAsync();
    await h.Page.EvaluateAsync("() => window.open('http://other-glpi.test/idp','_blank')");
    var popup = focus.WrapPage(await popupPending);
    await shown.Task.WaitAsync(TimeSpan.FromSeconds(4));
    Equal(1, focus.BringToFrontCalls);
    Equal(0, await popup.EvaluateAsync<int>("() => window.testClicks"));
    True(!await popup.Locator("#remember").IsCheckedAsync(), "La page IdP du popup reste contrôlée par l’utilisateur.");
    authenticated = true; await h.Page.GotoAsync(target); await settling;
    True(h.Service.BackgroundMode, "La fin de la connexion doit réduire le navigateur de nouveau.");
}

async Task CheckNative10()
{
    string html = fixture10.Replace("data-count=\"1234\"", "data-count=\"5\"", StringComparison.Ordinal)
        .Replace("data-start=\"0\"", "data-start=\"2\"", StringComparison.Ordinal)
        .Replace("De 1 à 20 sur 1 234", "De 3 à 4 sur 5", StringComparison.Ordinal);
    var downloads = new ConcurrentQueue<string>();
    await using var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal))
        { downloads.Enqueue(route.Request.Url); return Download(route, csvAll); }
        return Html(route, html);
    }, target);
    Equal(1, await h.Page.Locator("#search_tickets tbody tr").CountAsync());
    var settings = Settings();
    var phases = new List<AutomaticRunPhase>();
    var readySawSavedFiles = false;
    var automatic = new AutomaticExtractionService(h.Service);
    automatic.PhaseChanged += phase =>
    {
        phases.Add(phase);
        if (phase == AutomaticRunPhase.Ready)
            readySawSavedFiles = Directory.Exists(settings.ExportFolder)
                && Directory.GetFiles(settings.ExportFolder, "*.xlsx").Length == 1
                && Directory.GetFiles(settings.ExportFolder, "*.csv").Length == 1;
    };
    var run = await automatic.RunAsync(settings, null, CancellationToken.None);
    Equal(5, run.Data.Rows.Count); Equal(5, run.Data.ExpectedTotal); Equal(2, run.Data.Headers.Count);
    True(run.Data.Complete, string.Join(" | ", run.Data.Warnings));
    Equal("0001", run.Data.Rows[0][0]); Equal("0005", run.Data.Rows[^1][0]); Equal("=1+1", run.Data.Rows[^1][1]);
    Equal("Export natif GLPI : toutes les pages", run.Data.Scope);
    True(run.NativePath != null && run.ExcelPath != null, "Le CSV natif et le classeur doivent être enregistrés automatiquement.");
    Equal(run.ExcelPath, run.PrimaryPath);
    Equal(csvAll, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(run.NativePath!)));
    AssertWorkbook(run.ExcelPath!, 5, "0005", "=1+1");
    Equal(1, downloads.Count);
    True(downloads.Single().Contains("display_type=-4", StringComparison.Ordinal), "Le CSV toutes-pages de GLPI doit être demandé.");
    Equal(1, await h.Page.EvaluateAsync<int>("() => window.menuClicks"));
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.businessSubmits"));
    True(settings.TableHeaderFingerprint.Length > 0, "Le tableau choisi doit être mémorisé pour le prochain clic.");
    Equal(0, Directory.GetFiles(settings.ExportFolder, "*.part").Length);
    Equal(AutomaticRunPhase.Connection, phases.First()); Equal(AutomaticRunPhase.Ready, phases.Last());
    True(phases.Contains(AutomaticRunPhase.Search) && phases.Contains(AutomaticRunPhase.Retrieval)
        && phases.Contains(AutomaticRunPhase.Validation) && phases.Contains(AutomaticRunPhase.Saving), "Les étapes doivent décrire les opérations réellement traversées.");
    True(readySawSavedFiles && phases.Count(phase => phase == AutomaticRunPhase.Ready) == 1,
        "La phase Fichier prêt ne doit être annoncée qu’une fois et après l’enregistrement effectif du CSV et du classeur.");
}

async Task CheckNativeCities(bool excel)
{
    var html = fixture10.Replace("data-count=\"1234\"", "data-count=\"5\"", StringComparison.Ordinal)
        .Replace("De 1 à 20 sur 1 234", "De 1 à 5 sur 5", StringComparison.Ordinal);
    var received = csvAll.Replace("Paris A", "MA - imprimante", StringComparison.Ordinal)
        .Replace("Paris B", "Mars - réseau", StringComparison.Ordinal)
        .Replace("Paris C", "Marseille - écran", StringComparison.Ordinal);
    await using var h = await Create(route => new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal)
        ? Download(route, received) : Html(route, html), target);
    var settings = Settings(); settings.AutoSaveXlsx = excel;
    settings.Cities = new() { TitleColumn = "Nom", Cities = new() { new() { Name = "Marseille", Aliases = new() { "MA", "Mars" } } } };
    var run = await new AutomaticExtractionService(h.Service).RunAsync(settings, null, CancellationToken.None);
    Equal(5, run.Data.Rows.Count); Equal(4, run.Data.Headers.Count); Equal(5, run.Data.ExpectedTotal);
    True(run.Data.Complete && run.Cities?.Applied == true, "L’enrichissement conserve le total GLPI vérifié.");
    Equal(3, run.Cities!.MatchedCount); Equal(2, run.Cities.UnmatchedCount);
    Equal(2, run.Cities.Source.Headers.Count); Equal(2, run.Cities.Source.Rows[0].Length);
    True(ReferenceEquals(run.Data, run.Cities.Data), "Le résultat et les indices ville doivent concerner le même objet publié.");
    Equal(received, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(run.NativePath!)));
    var enriched = Directory.GetFiles(settings.ExportFolder, "GLPI-villes-*.csv").Single();
    var loaded = NativeCsvImportService.Read(enriched, "", null, 100);
    Equal(5, loaded.Rows.Count); Equal("Ville détectée", loaded.Headers[2]);
    Equal("Marseille", loaded.Rows[0][2]); Equal("Marseille", loaded.Rows[1][2]); Equal("Marseille", loaded.Rows[2][2]);
    Equal("", loaded.Rows[3][2]); Equal("0001", loaded.Rows[0][0]);
    if (excel)
    {
        Equal(run.ExcelPath, run.PrimaryPath); AssertWorkbook(run.ExcelPath!, 5, "Marseille", "Ville détectée");
    }
    else { Equal(enriched, run.PrimaryPath); True(run.ExcelPath == null, "Excel reste désactivé selon le réglage mémorisé."); }
}

async Task CheckPipelineCancellation(string point)
{
    string html = fixture10.Replace("data-count=\"1234\"", "data-count=\"5\"", StringComparison.Ordinal)
        .Replace("De 1 à 20 sur 1 234", "De 1 à 5 sur 5", StringComparison.Ordinal);
    string received = point == "excel-after" ? csvAll.Replace("=1+1", new string('z', 40_000), StringComparison.Ordinal) : csvAll;
    await using var h = await Create(route => new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal)
        ? Download(route, received) : Html(route, html), target);
    var settings = Settings(); settings.AutoSaveXlsx = point != "final";
    using var cancellation = new CancellationTokenSource();
    var automatic = new AutomaticExtractionService(h.Service);
    var phases = new List<AutomaticRunPhase>(); var logs = new List<string>();
    automatic.PhaseChanged += phase =>
    {
        phases.Add(phase);
        if (point == "excel-before" && phase == AutomaticRunPhase.Saving) cancellation.Cancel();
    };
    automatic.Log += message =>
    {
        logs.Add(message);
        if ((point == "excel-after" && message.StartsWith("EXCEL ·", StringComparison.Ordinal))
            || (point == "final" && message.StartsWith("CONTRÔLE · 5 lignes chargées", StringComparison.Ordinal))) cancellation.Cancel();
    };
    bool cancelled = false;
    try { await automatic.RunAsync(settings, null, cancellation.Token); }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
    True(cancelled && cancellation.IsCancellationRequested, "La demande doit remonter comme annulation, même après la lecture de tout le relevé.");
    True(!phases.Contains(AutomaticRunPhase.Ready) && !logs.Any(message => message.Contains("· terminé,", StringComparison.Ordinal)),
        "Une extraction annulée ne doit annoncer ni Fichier prêt, ni terminé.");
    var native = Directory.GetFiles(settings.ExportFolder, "*.csv").Single();
    Equal(received, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(native)));
    var workbooks = Directory.GetFiles(settings.ExportFolder, "*.xlsx");
    Equal(point == "excel-after" ? 1 : 0, workbooks.Length);
    if (workbooks.Length == 1) AssertWorkbook(workbooks[0], 5, "0001", "0005");
    Equal(0, Directory.GetFiles(settings.ExportFolder, "*.part").Length);
}

async Task CheckCancelledEmptySave()
{
    const string html = "<!doctype html><html><body><a href='/logout.php'>Déconnexion</a><form data-search-itemtype='Computer' data-count='0' data-start='0' data-limit='20'><table class='search-results' id='empty-results'><thead><tr style='display:none'><th>ID</th></tr></thead><tbody><tr><td colspan='1'>No item found</td></tr></tbody></table></form></body></html>";
    await using var h = await Create(route => Html(route, html), target);
    var settings = Settings(); using var cancellation = new CancellationTokenSource();
    var automatic = new AutomaticExtractionService(h.Service);
    var phases = new List<AutomaticRunPhase>();
    automatic.PhaseChanged += phase => { phases.Add(phase); if (phase == AutomaticRunPhase.Saving) cancellation.Cancel(); };
    bool cancelled = false;
    try { await automatic.RunAsync(settings, null, cancellation.Token); }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
    True(cancelled, "L’enregistrement du résultat vide doit respecter l’annulation reçue avant son démarrage.");
    True(!phases.Contains(AutomaticRunPhase.Ready), "Aucun résultat prêt après annulation.");
    Equal(0, Directory.GetFiles(settings.ExportFolder).Length);
}

async Task CheckNative11Partial()
{
    var downloads = new ConcurrentQueue<string>();
    string html = fixture11.Replace("data-count=\"200\"", "data-count=\"5\"", StringComparison.Ordinal);
    await using var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal))
        { downloads.Enqueue(route.Request.Url); return Download(route, csvPartial); }
        return Html(route, html);
    }, target);
    var run = await new AutomaticExtractionService(h.Service).RunAsync(Settings(), null, CancellationToken.None);
    Equal(2, run.Data.Rows.Count); Equal(5, run.Data.ExpectedTotal);
    True(!run.Data.Complete && run.Data.Warnings.Count > 0, "Un CSV plus court que le compteur GLPI doit rester incomplet.");
    True(run.ExcelPath != null && Path.GetFileName(run.ExcelPath).StartsWith("GLPI-a-verifier-", StringComparison.Ordinal), "Le fichier incomplet doit être distingué à l’enregistrement.");
    Equal(csvPartial, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(run.NativePath!)));
    AssertWorkbook(run.ExcelPath!, 2, "0003", "0004");
    Equal(1, downloads.Count);
    True(downloads.Single().Contains("display_type=-4", StringComparison.Ordinal), "La section All pages doit être utilisée, pas la première entrée CSV.");
    Equal(1, await h.Page.EvaluateAsync<int>("() => window.menuClicks"));
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.businessSubmits"));
}

async Task CheckNativeLegacy()
{
    var downloads = new ConcurrentQueue<string>();
    var html = fixtureLegacy.Replace("De 1 à 20 sur 75", "De 1 à 1 sur 5", StringComparison.Ordinal);
    await using var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal))
        { downloads.Enqueue(route.Request.Url); return Download(route, csvAll); }
        return Html(route, html);
    }, target);
    var settings = Settings();
    settings.ExpectedTotal = 5;
    settings.RememberTable((await h.Service.InspectAsync(h.Page, CancellationToken.None)).Single(t => t.Selector == "#legacy-table"));
    var run = await new AutomaticExtractionService(h.Service).RunAsync(settings, null, CancellationToken.None);
    Equal(5, run.Data.Rows.Count); True(run.Data.Complete, string.Join(" | ", run.Data.Warnings));
    Equal(1, downloads.Count);
    var uri = new Uri(downloads.Single());
    True(uri.Query.Contains("display_type=-4", StringComparison.Ordinal), "L’option négative CSV toutes-pages doit être choisie avant le submit.");
    True(Uri.UnescapeDataString(uri.Query).Contains("criteria[0][value]=filtre+exemple", StringComparison.Ordinal), "Les champs de filtre du formulaire natif doivent être conservés.");
    Equal("-4", await h.Page.Locator("#dropdown_display_type42").InputValueAsync());
}

async Task CheckNativeAmbiguous()
{
    var html = fixture10.Replace("data-count=\"1234\"", "data-count=\"5\"", StringComparison.Ordinal)
        .Replace("<li id=\"copy_names_to_clipboard\">", "<li><a class='dropdown-item' href='/glpi/front/report.dynamic.php?item_type=Computer&amp;display_type=-4'>Toutes les pages en CSV</a></li><li id=\"copy_names_to_clipboard\">", StringComparison.Ordinal);
    int downloads = 0;
    await using var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal)) { downloads++; return Download(route, csvAll); }
        return Html(route, html);
    }, target);
    var settings = Settings();
    settings.TimeoutSeconds = 4;
    var phases = new List<AutomaticRunPhase>();
    var automatic = new AutomaticExtractionService(h.Service);
    automatic.PhaseChanged += phases.Add;
    try
    {
        await automatic.RunAsync(settings, null, CancellationToken.None);
        throw new Exception("L’export natif ambigu devait s’arrêter.");
    }
    catch (InvalidOperationException e)
    {
        True(e.Message.Contains("ambigu", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("Plusieurs", StringComparison.OrdinalIgnoreCase), "L’erreur doit expliquer les repères ambigus : " + e.Message);
    }
    Equal(0, downloads);
    Equal(0, Directory.Exists(settings.ExportFolder) ? Directory.GetFiles(settings.ExportFolder).Length : 0);
    True(!phases.Contains(AutomaticRunPhase.Ready), "Un export refusé ne doit jamais être présenté comme prêt.");
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.businessSubmits"));
}

async Task CheckAutomaticPagination()
{
    var queries = new ConcurrentQueue<string>();
    var html = basic.Replace("let current=0;", "let current=Number(new URLSearchParams(location.search).get('start') || 0)/2;", StringComparison.Ordinal);
    await using var h = await Create(route =>
    {
        queries.Enqueue(new Uri(route.Request.Url).Query);
        return Html(route, html);
    }, target);
    Equal("Page 2 sur 3", await h.Page.Locator("#page-number").InnerTextAsync());
    var settings = Settings(); settings.AutoMode = "pagination";
    var run = await new AutomaticExtractionService(h.Service).RunAsync(settings, null, CancellationToken.None);
    Equal(5, run.Data.Rows.Count); Equal(3, run.Data.Pages); True(run.Data.Complete, string.Join(" | ", run.Data.Warnings));
    True(run.Data.Rows[0].Contains("101") && run.Data.Rows[^1].Contains("105"), "La pagination doit inclure les lignes précédant la page visible au départ.");
    Equal(new Uri(TargetUrlPolicy.FirstPage(target)).Query, queries.Last());
    True(run.NativePath == null && run.ExcelPath != null, "Le mode pagination doit produire CSV et Excel sans export natif.");
    Equal(1, Directory.GetFiles(settings.ExportFolder, "*.csv").Length);
    AssertWorkbook(run.ExcelPath!, 5, "000123", "=00127");
    Equal("Aucune action métier déclenchée", await h.Page.Locator("#business-action").InnerTextAsync());
}

async Task CheckAjaxReadiness()
{
    string native = fixture10.Replace("data-count=\"1234\"", "data-count=\"5\"", StringComparison.Ordinal);
    string html = "<html><title>GLPI</title><body><a href='/logout.php'>Déconnexion</a>"
        + "<table id='loading-layout'><thead><tr><th>Menu</th><th>Information</th></tr></thead><tbody><tr><td>Navigation</td><td>Chargement en cours</td></tr></tbody></table>"
        + "<script>setTimeout(()=>{document.open();document.write(" + JsonSerializer.Serialize(native) + ");document.close();},1200);</script></body></html>";
    int downloads = 0;
    await using var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal)) { downloads++; return Download(route, csvAll); }
        return Html(route, html);
    }, target);
    Equal(1, await h.Page.Locator("#loading-layout").CountAsync());
    var run = await new AutomaticExtractionService(h.Service).RunAsync(Settings(), null, CancellationToken.None);
    Equal(5, run.Data.Rows.Count); True(run.Data.Complete, string.Join(" | ", run.Data.Warnings));
    Equal("#search_tickets", run.Table.Selector); Equal(1, downloads);
    True(!run.Data.Rows.SelectMany(row => row).Contains("Chargement en cours"), "Le tableau de présentation ne doit pas remplacer les résultats attendus.");
}

async Task CheckCustomSafety()
{
    // A customized menu selector must not turn a business submit button into an export menu.
    string menuHtml = fixture10.Replace("type=\"button\" id=\"dropdown-export\"", "type=\"submit\" id=\"dropdown-export\"", StringComparison.Ordinal);
    await using (var h = await Create(route => Html(route, menuHtml), target))
    {
        var settings = Settings();
        settings.MaxRows = 2000;
        settings.NativeSelectorOrigin = "http://glpi.test";
        settings.NativeMenuSelector = "#dropdown-export";
        settings.NativeSubmitSelector = "#all-csv";
        await MustRejectCustom(h, settings);
        Equal(0, await h.Page.EvaluateAsync<int>("() => window.menuClicks"));
        Equal(0, await h.Page.EvaluateAsync<int>("() => window.businessSubmits"));
    }
    // The action on the submit button overrides its parent form in the browser.
    // It must be checked even if the surrounding form looks like report.dynamic.php.
    string submitHtml = fixtureLegacy.Replace("id=\"native-submit\"", "id=\"native-submit\" formaction=\"/glpi/front/massiveaction.php\"", StringComparison.Ordinal)
        .Replace("</html>", "<script>window.exportClicks=0;document.querySelector('#native-submit').addEventListener('click',()=>window.exportClicks++);</script></html>", StringComparison.Ordinal);
    int businessRequests = 0;
    await using (var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/massiveaction.php", StringComparison.Ordinal)) businessRequests++;
        return Html(route, submitHtml);
    }, target))
    {
        var settings = Settings();
        settings.NativeSelectorOrigin = "http://glpi.test";
        settings.NativeFormatSelector = "#dropdown_display_type42";
        settings.NativeFormatValue = "-4";
        settings.NativeSubmitSelector = "#native-submit";
        settings.RememberTable((await h.Service.InspectAsync(h.Page, CancellationToken.None)).Single(t => t.Selector == "#legacy-table"));
        await MustRejectCustom(h, settings);
        Equal(0, await h.Page.EvaluateAsync<int>("() => window.exportClicks"));
        Equal(0, businessRequests);
    }
}

async Task MustRejectCustom(Harness h, AppSettings settings)
{
    try
    {
        await new AutomaticExtractionService(h.Service).RunAsync(settings, null, CancellationToken.None);
        throw new Exception("Le contrôle personnalisé métier devait être refusé.");
    }
    catch (InvalidOperationException e)
    {
        True(e.Message.Contains("commande d’export", StringComparison.Ordinal), "Le refus doit désigner le contrôle non reconnu comme export.");
    }
    Equal(0, Directory.GetFiles(settings.ExportFolder).Length);
}

async Task CheckOtherGlpiOrigin()
{
    await using var h = await Create(route => Html(route, new Uri(route.Request.Url).AbsolutePath == "/login.php"
        ? "<html><body><form id='login'>Connexion de l’instance demandée</form></body></html>"
        : ready), "http://glpi.test/login.php");
    var unrelated = await h.Context.NewPageAsync();
    await unrelated.GotoAsync(target.Replace("glpi.test", "other-glpi.test", StringComparison.Ordinal));
    await h.Page.EvaluateAsync("url => setTimeout(() => location.replace(url), 1200)", target);
    var result = await h.Service.WaitForTargetAsync(Settings(), CancellationToken.None);
    True(ReferenceEquals(h.Page, result), "Une autre instance GLPI déjà prête ne doit pas remplacer la cible pendant son SSO.");
    Equal(target, result.Url);
    Equal("other-glpi.test", new Uri(unrelated.Url).Host);
}

async Task CheckIframeNative()
{
    string native = fixture10.Replace("data-count=\"1234\"", "data-count=\"5\"", StringComparison.Ordinal);
    const string shell = "<html><title>Portail GLPI</title><body><iframe name='glpi-data' title='GLPI' src='/frame/computer.php' style='width:1100px;height:650px;border:0'></iframe></body></html>";
    int downloads = 0;
    await using var h = await Create(route =>
    {
        var uri = new Uri(route.Request.Url);
        if (uri.AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal)) { downloads++; return Download(route, csvAll); }
        return Html(route, uri.AbsolutePath == "/frame/computer.php" ? native : shell);
    }, target);
    var run = await new AutomaticExtractionService(h.Service).RunAsync(Settings(), null, CancellationToken.None);
    Equal(5, run.Data.Rows.Count); True(run.Data.Complete, string.Join(" | ", run.Data.Warnings));
    Equal("glpi-data", run.Table.FrameName); Equal("http://glpi.test", run.Table.FrameOrigin);
    True(run.Table.Frame != h.Page.MainFrame, "Le tableau extrait doit appartenir à l’iframe visible.");
    Equal(1, downloads);
    AssertWorkbook(run.ExcelPath!, 5, "0001", "0005");
}

async Task CheckEmptySearch()
{
    const string html = """
        <!doctype html><html><title>GLPI — Recherche vide</title><body>
        <a href='/logout.php'>Déconnexion</a>
        <form data-search-itemtype='Computer' data-count='0' data-start='0' data-limit='20'>
          <table class='search-results' id='empty-results'>
            <thead><tr style='display:none'><th>ID</th></tr></thead>
            <tbody><tr><td colspan='1'>No item found</td></tr></tbody>
          </table>
        </form></body></html>
        """;
    int exportRequests = 0;
    await using var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath.Contains("report.dynamic", StringComparison.Ordinal)) exportRequests++;
        return Html(route, html);
    }, target);
    var settings = Settings();
    var run = await new AutomaticExtractionService(h.Service).RunAsync(settings, null, CancellationToken.None);
    Equal(0, run.Data.Rows.Count); Equal(0, run.Data.ExpectedTotal); Equal(1, run.Data.Headers.Count); Equal("ID", run.Data.Headers[0]);
    True(run.Data.Complete, "Le compteur officiel zéro doit produire un résultat vide complet.");
    True(run.NativePath == null, "Une recherche vide sans menu ne doit pas prétendre avoir téléchargé un CSV natif.");
    Equal(0, exportRequests);
    True(run.ExcelPath != null, "Les en-têtes réels doivent permettre un classeur vide explicite.");
    AssertWorkbook(run.ExcelPath!, 0, "ID");
    Equal(1, Directory.GetFiles(settings.ExportFolder, "*.csv").Length);
}

async Task CheckBackgroundFocus()
{
    var focus = new FocusCallTrace();
    int pageRequests = 0;
    string html = fixture10.Replace("data-count=\"1234\"", "data-count=\"5\"", StringComparison.Ordinal);
    await using var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal)) return Download(route, csvAll);
        pageRequests++;
        return Html(route, html);
    }, target, focus);
    var run = await new AutomaticExtractionService(h.Service).RunAsync(Settings(), null, CancellationToken.None);
    Equal(5, run.Data.Rows.Count);
    Equal(0, focus.BringToFrontCalls);
    True(focus.RequestedWindowStates.Count > 0 && focus.RequestedWindowStates.All(state => state == "minimized"), "L’extraction automatique réduit seulement la fenêtre du contexte dédié.");
    True(h.Service.BackgroundMode, "Le parcours automatique doit conserver le mode arrière-plan.");
    int requestsBeforeShow = pageRequests;
    string urlBeforeShow = h.Page.Url;
    await h.Service.ShowBrowserAsync(h.Page, CancellationToken.None);
    Equal(1, focus.BringToFrontCalls);
    Equal(requestsBeforeShow, pageRequests);
    Equal(urlBeforeShow, h.Page.Url);
    True(!h.Service.BackgroundMode, "Afficher le navigateur doit restaurer le mode visible explicitement.");
    await h.Service.HideBrowserAsync(CancellationToken.None);
    Equal(1, focus.BringToFrontCalls);
    True(h.Service.BackgroundMode, "Rendre la main au logiciel doit conserver le navigateur ouvert derrière lui.");
    True(focus.RequestedWindowStates.Last() == "minimized", "Revenir au logiciel doit rendre le navigateur discret sans nouvelle navigation.");
    Equal(requestsBeforeShow, pageRequests);
    Equal(urlBeforeShow, h.Page.Url);
}

async Task CheckRestoreOpenWindow()
{
    var focus = new FocusCallTrace { ReportedWindowState = "minimized" };
    await using var h = await Create(route => Html(route, ready), focus: focus);
    await h.Service.HideBrowserAsync(CancellationToken.None);
    Equal(0, focus.BringToFrontCalls);
    Equal(0, focus.RequestedWindowStates.Count);
    await h.Service.HideBrowserAsync(CancellationToken.None);
    Equal(0, focus.RequestedWindowStates.Count);
    Equal(0, focus.BringToFrontCalls);
    Equal("about:blank", h.Page.Url);
    True(h.Service.BackgroundMode, "La restauration ne doit pas désactiver le mode logiciel au-dessus.");
    await h.Service.ShowBrowserAsync(h.Page, CancellationToken.None);
    True(focus.RequestedWindowStates.SequenceEqual(new[] { "normal" }), "La demande explicite restaure la fenêtre réduite.");
    Equal(1, focus.BringToFrontCalls);
}

async Task CheckTransientWindowTarget()
{
    var focus = new FocusCallTrace { CdpCreationFailure = "Protocol error (Target.attachToTarget): No target with given id found" };
    await using var h = await Create(route => Html(route, ready), focus: focus);
    await h.Service.HideBrowserAsync(CancellationToken.None);
    Equal(0, h.Logs.Count(line => line.Contains("mode discret indisponible", StringComparison.Ordinal)));
    // A subsequent real restriction must still produce its one-time warning.
    focus.CdpCreationFailure = "Protocol error (Target.attachToTarget): Access denied by browser policy";
    await h.Service.HideBrowserAsync(CancellationToken.None);
    Equal(1, h.Logs.Count(line => line.Contains("mode discret indisponible", StringComparison.Ordinal)));
    Equal(2, focus.NewCdpSessionCalls);
    Equal(0, focus.BringToFrontCalls);
    Equal("about:blank", h.Page.Url);
}

async Task CheckWindowManagementTimeout()
{
    var focus = new FocusCallTrace(stallCdpCreation: true);
    // Keep the initial about:blank page so no navigation schedules a concurrent
    // window operation. ConfigureContext must not start one for existing pages.
    await using var h = await Create(route => Html(route, ready), focus: focus);
    Equal(0, focus.NewCdpSessionCalls);
    await h.Page.EvaluateAsync("""
        () => {
          window.businessClicks = 0;
          const button = document.createElement('button');
          button.textContent = 'Action métier';
          button.addEventListener('click', () => window.businessClicks++);
          document.body.append(button);
        }
        """);
    string initialUrl = h.Page.Url;
    var elapsed = Stopwatch.StartNew();
    await h.Service.HideBrowserAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    True(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Une création de session CDP qui ne répond jamais doit libérer l’opération avant cinq secondes.");
    Equal(1, focus.NewCdpSessionCalls);
    Equal(1, h.Logs.Count(line => line.Contains("mode discret indisponible", StringComparison.Ordinal)));

    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    elapsed.Restart();
    try
    {
        await h.Service.HideBrowserAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(1.5));
        throw new Exception("La demande d’annulation devait interrompre la gestion de fenêtre.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    True(elapsed.Elapsed < TimeSpan.FromSeconds(1.5), "L’annulation doit être reconnue sans attendre le délai interne de trois secondes.");
    Equal(2, focus.NewCdpSessionCalls); // A second CDP attempt proves the first call released the semaphore.
    Equal(1, h.Logs.Count(line => line.Contains("mode discret indisponible", StringComparison.Ordinal)));
    Equal(0, focus.BringToFrontCalls);
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.businessClicks"));
    Equal(initialUrl, h.Page.Url);
}

string WithScript(string html, string script) => html + "\n<script>" + script + "</script>";

string WithoutNativeCsv(string html, bool withoutDisplayAll = false) => WithScript(html, """
    document.querySelectorAll('select[name=display_type]').forEach(select => {
      [...select.options].filter(option => Number(option.value) < 0 && /CSV/i.test(option.textContent)).forEach(option => option.remove());
    });
    """ + (withoutDisplayAll ? "document.querySelectorAll('select[name=glpilist_limit]').forEach(select => [...select.options].filter(option => Number(option.value) >= 828).forEach(option => option.remove()));" : ""));

string NativeCsv828()
{
    var lines = new List<string> { "ID;" + string.Join(';', Enumerable.Range(2, 15).Select(column => "Champ " + column)) };
    for (int row = 1; row <= 828; row++)
        lines.Add((6000000 + row).ToString() + ";" + string.Join(';', Enumerable.Range(2, 15).Select(column => row == 828 && column == 16 ? "=1+1" : $"Fixture {row} / {column}")));
    return "\uFEFF" + string.Join("\r\n", lines) + "\r\n";
}

async Task CheckRealisticLegacy(bool imageSubmit)
{
    string html = fixtureLegacyRealistic;
    if (imageSubmit) html = WithScript(html, """
        const button = document.querySelector('#legacy-submit');
        const image = document.createElement('input');
        image.type = 'image'; image.name = 'export'; image.id = button.id;
        image.alt = 'Exporter'; image.title = 'Exporter'; image.width = 24; image.height = 24;
        image.src = 'data:image/gif;base64,R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==';
        button.replaceWith(image);
        const format = document.querySelector('#legacy-format');
        format.style.display = 'none'; format.classList.add('select2-hidden-accessible');
        const facade = document.createElement('span'); facade.className = 'select2 select2-container'; facade.textContent = 'Format export Select2';
        format.after(facade);
        """);
    string csv = NativeCsv828();
    var downloads = new ConcurrentQueue<string>();
    await using var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal))
        {
            Equal("GET", route.Request.Method);
            downloads.Enqueue(route.Request.Url);
            return Download(route, csv);
        }
        return Html(route, html);
    }, legacyTarget);
    Equal(6, await h.Page.Locator("table").CountAsync());
    Equal(20, await h.Page.Locator("#legacy-results tbody tr").CountAsync());
    Equal(16, await h.Page.Locator("#legacy-results thead th").CountAsync());
    if (imageSubmit)
    {
        var table = (await h.Service.InspectAsync(h.Page, CancellationToken.None)).Single(item => item.Selector == "#legacy-results");
        var inspection = await h.Service.InspectNativeExportAsync(table, CancellationToken.None);
        True(inspection.Plans.Single().FormatHidden, "Le select natif masqué par Select2 doit être reconnu avant sa sélection forcée.");
    }
    var settings = Settings(legacyTarget); settings.MaxRows = 1000;
    var run = await new AutomaticExtractionService(h.Service).RunAsync(settings, null, CancellationToken.None);
    Equal("#legacy-results", run.Table.Selector);
    Equal(828, run.Data.Rows.Count); Equal(828, run.Data.ExpectedTotal); Equal(16, run.Data.Headers.Count);
    True(run.Data.Complete, string.Join(" | ", run.Data.Warnings));
    Equal("6000001", run.Data.Rows[0][0]); Equal("6000828", run.Data.Rows[^1][0]);
    Equal(1, downloads.Count);
    string query = Uri.UnescapeDataString(new Uri(downloads.Single()).Query);
    True(query.Contains("display_type=-3", StringComparison.Ordinal), "Le code CSV réel du GLPI legacy est -3 ; -4 désigne ici un PDF.");
    True(query.Contains("criteria[0][value]=notold", StringComparison.Ordinal), "Le filtre du formulaire d’export doit être préservé.");
    Equal(csv, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(run.NativePath!)));
    AssertWorkbook(run.ExcelPath!, 828, "6000001", "6000828", "=1+1");
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.businessSubmits"));
}

async Task CheckNativeTextControls()
{
    const string privateSentinel = "PRIVATE-CSV-CONTROL-CELL";
    var headers = new[] { "ID" }.Concat(Enumerable.Range(2, 15).Select(column => "Champ " + column)).ToArray();
    var expectedRows = new List<string[]>();
    var special = new[]
    {
        privateSentinel + " avant\u000Baprès",
        privateSentinel + " avant\u000Caprès",
        privateSentinel + " avant\u0085après",
        privateSentinel + " avant\u009Daprès",
        privateSentinel + " première ligne\r\nau milieu\u000Bdu texte ; \"guillemets\"\nfin\u000C\u0085\u009D",
        privateSentinel + " littéraux _x000B_ / _x000C_ / _x0085_ / _x009D_ / _x005F_ et vrai \u000B",
        "=1+1"
    };
    var csv = new StringBuilder("\uFEFF");
    void AppendRecord(IEnumerable<string> values) => csv.AppendJoin(';', values.Select(value => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""))
        .Append(";\r\n");
    AppendRecord(headers);
    for (int row = 1; row <= 831; row++)
    {
        var values = new string[16];
        values[0] = (6000000 + row).ToString();
        values[1] = (row <= special.Length ? special[row - 1] : $"Description synthétique {row}") + " " + new string('z', 1280);
        for (int column = 2; column < values.Length; column++) values[column] = $"Fixture {row} / {column + 1}";
        expectedRows.Add(values);
        AppendRecord(values);
    }
    byte[] expectedBytes = Encoding.UTF8.GetBytes(csv.ToString());
    True(expectedBytes.Length > 1_000_000, "Le cas doit couvrir un téléchargement de plus d’un Mo, au-delà des préfixes d’analyse.");
    string html = WithScript(fixtureLegacyRealistic, "renderLegacyRows(20, 831); const descriptions = " + JsonSerializer.Serialize(expectedRows.Take(20).Select(row => row[1]).ToArray())
        + "; document.querySelectorAll('#legacy-rows tr').forEach((row,index) => row.cells[1].textContent=descriptions[index]);");
    int downloads = 0, displayRequests = 0;
    await using var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal))
        {
            downloads++;
            return Download(route, csv.ToString());
        }
        if (route.Request.Method == "POST") displayRequests++;
        return Html(route, html);
    }, legacyTarget);
    var settings = Settings(legacyTarget); settings.MaxRows = 1000;
    var automatic = new AutomaticExtractionService(h.Service);
    automatic.Log += h.Logs.Enqueue;
    var run = await automatic.RunAsync(settings, null, CancellationToken.None);
    Equal(831, run.Data.Rows.Count); Equal(831, run.Data.ExpectedTotal); Equal(16, run.Data.Headers.Count);
    True(run.Data.Complete, "Les contrôles textuels conservés ne doivent pas rendre partiel un total natif conforme.");
    Equal("Export natif GLPI : toutes les pages", run.Data.Scope);
    Equal(1, downloads); Equal(0, displayRequests);
    True(run.NativePath != null && run.ExcelPath != null, "Le CSV original et le classeur doivent être conservés automatiquement.");
    Equal(run.ExcelPath, run.PrimaryPath);
    byte[] downloadedBytes = await File.ReadAllBytesAsync(run.NativePath!);
    True(expectedBytes.SequenceEqual(downloadedBytes), "Le téléchargement natif doit rester identique octet pour octet.");
    for (int row = 0; row < expectedRows.Count; row++)
        True(expectedRows[row].SequenceEqual(run.Data.Rows[row]), $"Les valeurs CSV de la ligne {row + 1} doivent être conservées sans nettoyage.");
    AssertWorkbookExactRows(run.ExcelPath!, headers, expectedRows);
    string diagnostic = DiagnosticReport.Create(settings, run.Data, string.Join('\n', h.Logs));
    True(!diagnostic.Contains(privateSentinel, StringComparison.Ordinal), "Le diagnostic des caractères doit exclure le contenu des cellules.");
    foreach (string code in new[] { "U+000B", "U+000C", "U+0085", "U+009D" })
        True(diagnostic.Contains(code, StringComparison.Ordinal), "Le diagnostic doit identifier le code Unicode sans exposer sa cellule : " + code);
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.businessSubmits"));
}

async Task CheckUnreadableNativePreserved(bool binary)
{
    const string privateSentinel = "PRIVATE-UNREADABLE-DOWNLOAD-CONTENT";
    string received = binary ? "%PDF-1.7\n\0" + privateSentinel : "<!doctype html><html><body>" + privateSentinel + "</body></html>";
    string html = WithScript(fixtureLegacyRealistic, "renderLegacyRows(20, 831);");
    int downloads = 0, displayRequests = 0;
    await using var h = await Create(route =>
    {
        if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal))
        { downloads++; return Download(route, received); }
        if (route.Request.Method == "POST")
        {
            True(route.Request.PostData?.Contains("glpilist_limit=9999999", StringComparison.Ordinal) == true, "Le repli doit utiliser l’option native existante.");
            displayRequests++;
            return Html(route, WithScript(html, "renderLegacyRows(831, 831); document.querySelectorAll('.tab_cadre_pager th.right a').forEach(a => a.replaceWith(document.createTextNode(a.textContent)));"));
        }
        return Html(route, html);
    }, legacyTarget);
    var settings = Settings(legacyTarget); settings.MaxRows = 1000;
    var automatic = new AutomaticExtractionService(h.Service);
    automatic.Log += h.Logs.Enqueue;
    var phases = new List<AutomaticRunPhase>(); automatic.PhaseChanged += phases.Add;
    string? nativePath = null; automatic.NativeFileSaved += path => nativePath = path;
    InvalidDataException? failure = null;
    try { await automatic.RunAsync(settings, null, CancellationToken.None); }
    catch (InvalidDataException error) { failure = error; }
    True(failure != null, "Un reçu HTML ou binaire doit produire un échec explicite.");
    Equal(1, downloads); Equal(0, displayRequests);
    True(nativePath != null && !phases.Contains(AutomaticRunPhase.Ready), "Le reçu doit être conservé sans annoncer un résultat prêt.");
    byte[] downloadedBytes = await File.ReadAllBytesAsync(nativePath!);
    True(Encoding.UTF8.GetBytes(received).SequenceEqual(downloadedBytes), "Le reçu natif illisible ne doit être modifié ni remplacé par le CSV local.");
    var csvPaths = Directory.GetFiles(settings.ExportFolder, "*.csv");
    Equal(1, csvPaths.Length); Equal(nativePath, csvPaths[0]);
    Equal(0, Directory.GetFiles(settings.ExportFolder, "*.xlsx").Length);
    string diagnostic = DiagnosticReport.Create(settings, null, string.Join('\n', h.Logs) + "\n" + failure!.Message);
    True(!diagnostic.Contains(privateSentinel, StringComparison.Ordinal), "Le diagnostic du reçu illisible doit exclure son contenu.");
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.businessSubmits"));
}

async Task CheckUnreadableNativeRejected()
{
    const string received = "<!doctype html><html><body>PRIVATE-REJECTED-DOWNLOAD-CONTENT</body></html>";
    foreach (bool hasDisplayControl in new[] { true, false })
    {
        string html = WithScript(fixtureLegacyRealistic, "renderLegacyRows(20, 831);" +
            (hasDisplayControl ? "" : "document.querySelectorAll('select[name=glpilist_limit]').forEach(select => [...select.options].filter(option => Number(option.value) >= 831).forEach(option => option.remove()));"));
        int downloads = 0, displayRequests = 0;
        await using var h = await Create(route =>
        {
            if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal))
            { downloads++; return Download(route, received); }
            if (route.Request.Method == "POST") displayRequests++;
            // The server exposes only 20 of 831 rows. No POST should occur.
            return Html(route, html);
        }, legacyTarget);
        var settings = Settings(legacyTarget); settings.MaxRows = 1000; settings.TimeoutSeconds = 2;
        Exception? failure = null;
        try { await new AutomaticExtractionService(h.Service).RunAsync(settings, null, CancellationToken.None); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { failure = ex; }
        True(failure != null, "Un reçu illisible sans affichage complet vérifié doit rester un échec explicite.");
        Equal(1, downloads); Equal(0, displayRequests);
        Equal(0, Directory.GetFiles(settings.ExportFolder, "*.xlsx").Length);
        var remaining = Directory.GetFiles(settings.ExportFolder);
        Equal(1, remaining.Length);
        byte[] downloadedBytes = await File.ReadAllBytesAsync(remaining[0]);
        True(Encoding.UTF8.GetBytes(received).SequenceEqual(downloadedBytes), "Seul le reçu natif intact doit rester après le refus de repli.");
        Equal(0, await h.Page.EvaluateAsync<int>("() => window.businessSubmits"));
    }
}

async Task CheckUnsupportedNative()
{
    string html = WithScript(WithoutNativeCsv(fixtureLegacyRealistic, true), """
        document.querySelector('#legacy-results thead th').textContent = 'HEADER-PRIVATE-SENTINEL';
        document.querySelector('#legacy-results tbody td').textContent = 'CELL-PRIVATE-SENTINEL';
        document.querySelector('#legacy-export input[name="criteria[0][value]"]').value = 'FILTER-PRIVATE-SENTINEL';
        """);
    await using var h = await Create(route => Html(route, html), legacyTarget);
    var settings = Settings(legacyTarget + "?filter=URL-PRIVATE-SENTINEL");
    settings.Automation.AutomaticPaginationFallback = false;
    settings.TimeoutSeconds = 60; settings.MaxRows = 1000;
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(9));
    var elapsed = Stopwatch.StartNew();
    var failure = await MustRejectRun(() => new AutomaticExtractionService(h.Service).RunAsync(settings, null, cancellation.Token));
    True(elapsed.Elapsed < TimeSpan.FromSeconds(8), "Une barre d’outils stable non prise en charge doit s’arrêter avant 8 secondes, pas attendre 30 secondes.");
    True(failure.Message.Length > 30, "L’échec doit expliquer le contrôle d’export manquant.");
    var table = (await h.Service.InspectAsync(h.Page, CancellationToken.None)).Single(item => item.Selector == "#legacy-results");
    var inspection = await h.Service.InspectNativeExportAsync(table, CancellationToken.None);
    True(inspection.Diagnostics.Count > 0, "Un diagnostic structurel doit être disponible pour le contrôle non reconnu.");
    True(inspection.DisplayPlan == null && inspection.Plans.Count == 0, "La fixture ne doit proposer ni CSV toutes-pages ni limite d’affichage suffisante.");
    var diagnostic = DiagnosticReport.Create(settings, null, string.Join('\n', h.Logs));
    string allDiagnostics = diagnostic + string.Join('\n', inspection.Diagnostics) + failure.Message;
    foreach (string secret in new[] { "HEADER-PRIVATE-SENTINEL", "CELL-PRIVATE-SENTINEL", "FILTER-PRIVATE-SENTINEL", "URL-PRIVATE-SENTINEL", "FAKE-TOKEN-DO-NOT-DIAGNOSE" })
        True(!allDiagnostics.Contains(secret, StringComparison.Ordinal), "Le diagnostic structurel contient une valeur privée : " + secret);
    True(h.Logs.Count(line => line.Contains("Analyse terminée", StringComparison.Ordinal)) <= 4, "L’attente ne doit pas répéter le même diagnostic toutes les 500 ms.");
    Equal(0, Directory.Exists(settings.ExportFolder) ? Directory.GetFiles(settings.ExportFolder).Length : 0);
}

async Task CheckDisplayAll()
{
    string html = WithoutNativeCsv(fixtureLegacyRealistic);
    int displayRequests = 0, nativeRequests = 0;
    await using var h = await Create(route =>
    {
        var uri = new Uri(route.Request.Url);
        if (uri.AbsolutePath.EndsWith("/report.dynamic.php", StringComparison.Ordinal)) nativeRequests++;
        if (route.Request.Method == "POST")
        {
            True(route.Request.PostData?.Contains("glpilist_limit=9999999", StringComparison.Ordinal) == true, "Seule l’option existante Afficher tout doit être demandée.");
            displayRequests++;
            return Html(route, WithScript(html, "renderLegacyRows(828, 828); document.querySelectorAll('.tab_cadre_pager th.right a').forEach(a => a.replaceWith(document.createTextNode(a.textContent)));"));
        }
        return Html(route, html);
    }, legacyTarget);
    var settings = Settings(legacyTarget); settings.MaxRows = 1000;
    var run = await new AutomaticExtractionService(h.Service).RunAsync(settings, null, CancellationToken.None);
    Equal(828, run.Data.Rows.Count); Equal(828, run.Data.ExpectedTotal);
    True(run.Data.Complete, string.Join(" | ", run.Data.Warnings));
    Equal(1, displayRequests); Equal(0, nativeRequests);
    True(run.NativePath == null, "Afficher tout ne doit pas inventer un fichier natif téléchargé.");
    AssertWorkbook(run.ExcelPath!, 828);
    Equal(1, Directory.GetFiles(settings.ExportFolder, "*.csv").Length);
    Equal(0, await h.Page.EvaluateAsync<int>("() => window.businessSubmits"));
}

async Task CheckDisplayAllRejected()
{
    string html = WithoutNativeCsv(fixtureLegacyRealistic);
    int displayRequests = 0;
    await using var h = await Create(route =>
    {
        if (route.Request.Method == "POST")
        {
            displayRequests++;
            // The server retained its 20-row limit despite accepting the list choice.
            return Html(route, WithScript(html, "document.querySelectorAll('select[name=glpilist_limit]').forEach(select => select.value='9999999');"));
        }
        return Html(route, html);
    }, legacyTarget);
    var capped = Settings(legacyTarget); capped.MaxRows = 100; capped.TimeoutSeconds = 2;
    var capFailure = await MustRejectRun(() => new AutomaticExtractionService(h.Service).RunAsync(capped, null, CancellationToken.None));
    True(capFailure.Message.Contains("828", StringComparison.Ordinal), "La limite mémoire doit être expliquée avec le total attendu.");
    Equal(0, displayRequests);
    Equal(0, Directory.Exists(capped.ExportFolder) ? Directory.GetFiles(capped.ExportFolder).Length : 0);
    var incomplete = Settings(legacyTarget); incomplete.MaxRows = 1000; incomplete.TimeoutSeconds = 2;
    var failure = await MustRejectRun(() => new AutomaticExtractionService(h.Service).RunAsync(incomplete, null, CancellationToken.None));
    True(failure.Message.Contains("20", StringComparison.Ordinal) && failure.Message.Contains("828", StringComparison.Ordinal), "L’affichage partiel doit annoncer 20 lignes sur 828.");
    Equal(1, displayRequests);
    Equal(0, Directory.Exists(incomplete.ExportFolder) ? Directory.GetFiles(incomplete.ExportFolder).Length : 0);
}

static async Task<InvalidOperationException> MustRejectRun(Func<Task<AutomaticRunResult>> run)
{
    try { await run(); }
    catch (InvalidOperationException exception) { return exception; }
    throw new Exception("Le parcours automatique devait refuser ce résultat.");
}

static void AssertWorkbook(string path, int expectedRows, params string[] values)
{
    True(File.Exists(path), "Le classeur attendu doit exister.");
    using var zip = ZipFile.OpenRead(path);
    using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    var sheet = XDocument.Load(stream);
    XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    Equal(expectedRows + 1, sheet.Descendants(x + "row").Count());
    Equal(0, sheet.Descendants(x + "f").Count());
    var content = sheet.Descendants(x + "t").Select(t => t.Value).ToArray();
    foreach (var value in values) True(content.Contains(value), "Valeur littérale absente du classeur : " + value);
    True(zip.GetEntry("xl/worksheets/sheet2.xml") != null, "La feuille Informations doit être présente.");
}

static void AssertWorkbookExactRows(string path, IReadOnlyList<string> headers, IReadOnlyList<string[]> expectedRows)
{
    using var zip = ZipFile.OpenRead(path);
    // Every XML part must remain well-formed, including the warnings sheet.
    foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.Ordinal) || entry.FullName.EndsWith(".rels", StringComparison.Ordinal)))
    {
        using var part = entry.Open();
        _ = XDocument.Load(part);
    }
    using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    var sheet = XDocument.Load(stream);
    XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    Equal(0, sheet.Descendants(x + "f").Count());
    var rows = sheet.Descendants(x + "row").ToArray();
    Equal(expectedRows.Count + 1, rows.Length);
    // Decode SpreadsheetML escapes once. _x005F_x000B_ is a literal _x000B_,
    // whereas _x000B_ encodes the real control character. A second pass is wrong.
    string Decode(string value) => Regex.Replace(value, "_x([0-9a-fA-F]{4})_", match =>
        ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
    for (int row = 0; row < rows.Length; row++)
    {
        var cells = rows[row].Elements(x + "c").ToArray();
        var expected = row == 0 ? headers : expectedRows[row - 1];
        Equal(expected.Count, cells.Length);
        for (int column = 0; column < cells.Length; column++)
        {
            Equal("inlineStr", (string?)cells[column].Attribute("t"));
            string actual = Decode(string.Concat(cells[column].Descendants(x + "t").Select(text => text.Value)));
            True(expected[column] == actual, $"Le classeur doit préserver exactement la cellule ligne {row + 1}, colonne {column + 1}, après décodage SpreadsheetML.");
        }
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Attendu : {expected}\nObtenu : {actual}");
}

static void True(bool condition, string reason)
{
    if (!condition) throw new Exception(reason);
}

sealed class Harness(IBrowserContext context, IPage page, BrowserService service, ConcurrentQueue<string> logs) : IAsyncDisposable
{
    public IBrowserContext Context { get; } = context;
    public IPage Page { get; } = page;
    public BrowserService Service { get; } = service;
    public ConcurrentQueue<string> Logs { get; } = logs;
    public ValueTask DisposeAsync() => Service.DisposeAsync();
}
