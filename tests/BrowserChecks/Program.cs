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
    ("Export natif : téléchargement CSV reçu et enregistré", CheckDownload),
    ("Prévol : configuration et dossier d’export contrôlés avant le navigateur", CheckPreflight),
    ("Profil navigateur : verrou exclusif et reprise après libération", CheckProfileLease),
    ("Pagination : changement de critères interrompu sans mélanger les pages", CheckScopeChange),
    ("Pagination : changement d’entité interrompu sans mélanger les pages", CheckEntityChange),
    ("Pagination : un total configuré ne masque pas un total GLPI modifié", CheckConfiguredTotalChange),
    ("Empreinte : ordre des paramètres et taille des pages ignorés, critères conservés", CheckScopeCanonicalization),
    ("Périmètre : critères DOM recopiés dans l’URL acceptés, valeur divergente refusée", CheckScopeUrlMaterialization),
    ("Automatique : export absent, première page puis pagination complète", CheckAutomaticFallback),
    ("Automatique : repli désactivé respecté", CheckFallbackDisabled),
    ("Automatique : tableau disparu, pages déjà lues enregistrées comme partielles", CheckAutomaticLostTable),
    ("Automatique : export natif rapproché des identifiants visibles", CheckAutomaticNative),
    ("Automatique : mauvais échantillon natif refusé sans repli", CheckNativeWrongSample),
    ("Automatique : CSV corrompu conservé et refusé sans repli", CheckNativeCorrupt),
    ("Automatique : export ambigu refusé sans lancer un repli", CheckAmbiguousNativeNoFallback),
    ("Automatique : export incomplet signalé et jamais remplacé silencieusement", CheckNativeIncomplete),
    ("Automatique : changement de contexte après le téléchargement signalé", CheckNativeScopeChange),
    ("Échantillon : identifiants groupés normalisés, colonnes absentes non devinées", CheckSampleNormalization)
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

async Task<Harness> Open(string? html = null, string? csv = null)
{
    var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
    await context.RouteAsync("http://glpi.test/**", async route =>
    {
        var uri = new Uri(route.Request.Url);
        if (uri.AbsolutePath is "/export.csv" or "/front/report.dynamic.php")
            await route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "text/csv; charset=utf-8",
                Headers = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"tickets.csv\"" },
                Body = csv ?? "ID;Titre\r\n001;Écran cassé\r\n"
            });
        else await route.FulfillAsync(new() { Status = 200, ContentType = "text/html; charset=utf-8", Body = html ?? basic });
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

Task CheckPreflight()
{
    var settings = Settings(); settings.ExportFolder = Path.Combine(temporary, "preflight");
    ExtractionVerificationService.Preflight(settings);
    Equal(0, Directory.GetFiles(settings.ExportFolder).Length);
    settings.MaxRows = 0;
    try { ExtractionVerificationService.Preflight(settings); throw new Exception("Limite nulle acceptée"); }
    catch (InvalidOperationException) { }
    settings.MaxRows = 100; settings.ExpectedTotal = 101;
    try { ExtractionVerificationService.Preflight(settings); throw new Exception("Total supérieur à la limite accepté"); }
    catch (InvalidOperationException) { }
    settings.ExpectedTotal = null; settings.PortalUrl = "file:///tmp/test";
    try { ExtractionVerificationService.Preflight(settings); throw new Exception("Cible locale acceptée"); }
    catch (InvalidOperationException) { }
    settings.PortalUrl = Settings().PortalUrl; settings.ExportFolder = Path.Combine(temporary, "not-a-folder");
    File.WriteAllText(settings.ExportFolder, "preserve");
    try { ExtractionVerificationService.Preflight(settings); throw new Exception("Chemin non répertoire accepté"); }
    catch (InvalidOperationException) { }
    Equal("preserve", File.ReadAllText(settings.ExportFolder));
    return Task.CompletedTask;
}

Task CheckProfileLease()
{
    var profile = Path.Combine(temporary, "profile");
    using (var first = BrowserProfileLease.Acquire(profile))
    {
        try { using var second = BrowserProfileLease.Acquire(profile); throw new Exception("Profil partagé entre deux instances"); }
        catch (InvalidOperationException) { }
    }
    using var reopened = BrowserProfileLease.Acquire(profile);
    return Task.CompletedTask;
}

string WithCriteria() => basic.Replace("<main>", "<main><input type='hidden' id='criteria-value' name='criteria[0][value]' value='origin'>", StringComparison.Ordinal);

async Task CheckScopeChange()
{
    await using var h = await Open(WithCriteria());
    await h.Page.EvaluateAsync("() => document.querySelector('#next').addEventListener('click', () => document.querySelector('#criteria-value').value = 'other')");
    var result = await h.Service.ExtractAsync(await Table(h), true, Settings(), null, CancellationToken.None);
    Equal(2, result.Rows.Count); True(!result.Complete, "Les critères modifiés ne doivent pas créer une extraction complète.");
    True(result.Warnings.Any(x => x.Contains("profil GLPI a changé", StringComparison.Ordinal)), "Le changement de périmètre doit être expliqué.");
}

async Task CheckEntityChange()
{
    await using var h = await Open(basic.Replace("<main>", "<main><select name='entities_id' id='entity'><option value='1'>A</option><option value='2'>B</option></select>", StringComparison.Ordinal));
    await h.Page.EvaluateAsync("() => document.querySelector('#next').addEventListener('click', () => document.querySelector('#entity').value = '2')");
    var result = await h.Service.ExtractAsync(await Table(h), true, Settings(), null, CancellationToken.None);
    Equal(2, result.Rows.Count); True(!result.Complete, "Les entités ne doivent pas être mélangées.");
}

async Task CheckConfiguredTotalChange()
{
    await using var h = await Open();
    await h.Page.EvaluateAsync("() => document.querySelector('#next').addEventListener('click', () => document.querySelector('#page-summary').textContent = 'Affichage de 3 à 4 sur 6 éléments')");
    var settings = Settings(); settings.ExpectedTotal = 5;
    var result = await h.Service.ExtractAsync(await Table(h), true, settings, null, CancellationToken.None);
    Equal(2, result.Rows.Count); True(!result.Complete && result.Warnings.Any(x => x.Contains("nombre de résultats", StringComparison.Ordinal)), "Le total configuré ne doit pas masquer le changement de total source.");
}

Task CheckScopeCanonicalization()
{
    var one = BrowserService.ScopeLocation("http://glpi.test/front/ticket.php?start=0&criteria%5B0%5D%5Bvalue%5D=A&sort=1&list_limit=20");
    var two = BrowserService.ScopeLocation("http://glpi.test/front/ticket.php?sort=1&criteria%5B0%5D%5Bvalue%5D=A&start=20&list_limit=100");
    Equal(one, two);
    True(one != BrowserService.ScopeLocation("http://glpi.test/front/ticket.php?sort=1&criteria%5B0%5D%5Bvalue%5D=B"), "Une autre valeur de filtre doit changer l’empreinte.");
    Equal(BrowserService.ScopeLocation("http://glpi.test/front/ticket.php?filter=Paris+Centre&order=ASC"),
        BrowserService.ScopeLocation("http://glpi.test/front/ticket.php?order=DESC&filter=Paris%20Centre"));
    True(one != BrowserService.ScopeLocation("http://glpi.test/front/ticket.php?criteria%5B0%5D%5Bvalue%5D=A&is_deleted=1"),
        "Un filtre non représenté dans le DOM ne doit pas disparaître de l’empreinte.");
    return Task.CompletedTask;
}

async Task CheckScopeUrlMaterialization()
{
    await using var h = await Open(WithCriteria());
    var table = await Table(h);
    var original = await h.Service.CaptureScopeEvidenceAsync(table, Settings().PortalUrl, CancellationToken.None);
    await h.Page.EvaluateAsync("() => history.replaceState({},'', '?criteria%5B0%5D%5Bvalue%5D=origin&sort=1&order=DESC&start=20&glpilist_limit=9999999')");
    var materialized = await h.Service.CaptureScopeEvidenceAsync(table, Settings().PortalUrl, CancellationToken.None);
    True(original.Known && materialized.Known, "Les critères sont explicitement observés dans le formulaire.");
    Equal(original.Fingerprint, materialized.Fingerprint);
    await h.Page.EvaluateAsync("() => history.replaceState({},'', '?criteria%5B0%5D%5Bvalue%5D=other&start=20')");
    var divergent = await h.Service.CaptureScopeEvidenceAsync(table, Settings().PortalUrl, CancellationToken.None);
    True(divergent.Fingerprint != original.Fingerprint, "Un critère URL divergent ne doit jamais être masqué par un ancien contrôle DOM.");
}

AppSettings AutomaticSettings()
{
    var settings = Settings(); settings.TimeoutSeconds = 10; settings.DownloadTimeoutSeconds = 10;
    settings.AutoSaveXlsx = false; settings.Cities.Enabled = false;
    return settings;
}

async Task CheckAutomaticLostTable()
{
    var html = WithCriteria().Replace("</body>", "<script>document.querySelector('#next').addEventListener('click',()=>document.querySelector('#search-results').remove());</script></body>", StringComparison.Ordinal);
    await using var h = await Open(html);
    var settings = AutomaticSettings(); settings.AutoMode = "pagination"; settings.TimeoutSeconds = 2;
    var run = await new AutomaticExtractionService(h.Service).RunAsync(settings, null, CancellationToken.None);
    Equal(2, run.Data.Rows.Count);
    True(!run.Data.Complete && !run.Data.Provenance.ScopeVerified, "La perte du tableau interdit la certification du résultat.");
    True(run.Data.Warnings.Any(x => x.Contains("contrôle final", StringComparison.OrdinalIgnoreCase)), "L’arrêt du contrôle final doit être expliqué.");
    True(File.Exists(run.PrimaryPath) && Path.GetFileName(run.PrimaryPath).StartsWith("GLPI-a-verifier-", StringComparison.Ordinal), "Le fichier partiel doit conserver les lignes lues sous un nom explicite.");
    var saved = NativeCsvImportService.Read(run.PrimaryPath, settings.PortalUrl, 5, 100);
    Equal(2, saved.Rows.Count); True(!saved.Complete, "Le fichier conservé ne doit pas inventer les pages manquantes.");
}

async Task CheckAutomaticFallback()
{
    var html = WithCriteria().Replace("let current=0;", "let current=Math.min(2,Math.floor(Number(new URL(location.href).searchParams.get('start') || '0') / 2));", StringComparison.Ordinal);
    await using var h = await Open(html);
    var settings = AutomaticSettings(); settings.PortalUrl += "?start=2";
    var run = await new AutomaticExtractionService(h.Service).RunAsync(settings, null, CancellationToken.None);
    Equal(5, run.Data.Rows.Count); Equal(3, run.Data.Pages); True(run.Data.Complete, string.Join(" | ", run.Data.Warnings));
    Equal("pagination-fallback", run.Data.Provenance.Method);
    True(run.Data.Provenance.ScopeVerified && run.Data.Provenance.SearchVerified && File.Exists(run.PrimaryPath), "Le repli doit conserver un périmètre contrôlé et un export réel.");
    Equal("Aucune action métier déclenchée", await h.Page.Locator("#business-action").InnerTextAsync());
}

async Task CheckFallbackDisabled()
{
    await using var h = await Open(WithCriteria());
    var settings = AutomaticSettings(); settings.Automation.AutomaticPaginationFallback = false;
    try { await new AutomaticExtractionService(h.Service).RunAsync(settings, null, CancellationToken.None); throw new Exception("Repli malgré l’option désactivée"); }
    catch (InvalidOperationException error) { True(error.Message.Contains("export", StringComparison.OrdinalIgnoreCase), "L’absence de commande d’export doit être expliquée."); }
    Equal("Page 1 sur 3", await h.Page.Locator("#page-number").InnerTextAsync());
}

string NativeHtml(bool changeScope = false) => WithCriteria().Replace("</main>",
    "<a id='native-csv-all' href='/front/report.dynamic.php?item_type=Computer&amp;display_type=-4'>Toutes les pages en CSV</a>"
    + (changeScope ? "<script>document.querySelector('#native-csv-all').addEventListener('click',()=>document.querySelector('#criteria-value').value='other');</script>" : "") + "</main>", StringComparison.Ordinal);
string NativeCsv() => "ID;Nom\r\n101;P-MA-001\r\n102;P-MA-002\r\n103;P-MA-003\r\n104;P-MA-004\r\n105;P-MA-005\r\n";
AppSettings NativeSettings()
{
    var settings = AutomaticSettings(); settings.ExpectedTotal = 5;
    return settings;
}

async Task CheckAutomaticNative()
{
    await using var h = await Open(NativeHtml(), NativeCsv());
    var logs = new List<string>(); var service = new AutomaticExtractionService(h.Service); service.Log += logs.Add;
    var run = await service.RunAsync(NativeSettings(), null, CancellationToken.None);
    Equal(5, run.Data.Rows.Count); True(run.Data.Complete && run.Data.Provenance.ScopeVerified, string.Join(" | ", run.Data.Warnings));
    Equal("native-csv", run.Data.Provenance.Method); True(File.Exists(run.NativePath), "Le fichier natif doit être conservé.");
    True(logs.Any(x => x.StartsWith("ÉCHANTILLON · Les 2", StringComparison.Ordinal)), "Les ID affichés doivent être rapprochés avant de publier le résultat.");
}

async Task CheckNativeWrongSample()
{
    await using var h = await Open(NativeHtml(), NativeCsv().Replace("101;", "201;", StringComparison.Ordinal));
    var service = new AutomaticExtractionService(h.Service); string? retained = null; service.NativeFileSaved += path => retained = path;
    try { await service.RunAsync(NativeSettings(), null, CancellationToken.None); throw new Exception("Mauvais fichier publié"); }
    catch (InvalidDataException error) { True(error.Message.Contains("échantillon", StringComparison.Ordinal), "Le mauvais échantillon doit être expliqué."); }
    True(File.Exists(retained), "Le mauvais fichier doit rester disponible pour diagnostic.");
    Equal("Page 1 sur 3", await h.Page.Locator("#page-number").InnerTextAsync());
}

async Task CheckNativeCorrupt()
{
    await using var h = await Open(NativeHtml(), "ID;Nom\r\n101;\"champ non terminé");
    var service = new AutomaticExtractionService(h.Service); string? retained = null; service.NativeFileSaved += path => retained = path;
    try { await service.RunAsync(NativeSettings(), null, CancellationToken.None); throw new Exception("CSV corrompu publié"); }
    catch (InvalidDataException) { }
    True(File.Exists(retained), "Le CSV corrompu doit rester disponible pour diagnostic.");
    Equal("Page 1 sur 3", await h.Page.Locator("#page-number").InnerTextAsync());
}

async Task CheckAmbiguousNativeNoFallback()
{
    var html = NativeHtml().Replace("</main>", "<a href='/front/report.dynamic.php?item_type=Ticket&amp;display_type=-4'>Toutes les pages en CSV</a></main>", StringComparison.Ordinal);
    await using var h = await Open(html, NativeCsv());
    try { await new AutomaticExtractionService(h.Service).RunAsync(NativeSettings(), null, CancellationToken.None); throw new Exception("Export ambigu accepté"); }
    catch (InvalidOperationException error) { True(error.Message.Contains("Plusieurs exports", StringComparison.Ordinal), "L’ambiguïté doit être distinguée de l’absence de commande."); }
    Equal("Page 1 sur 3", await h.Page.Locator("#page-number").InnerTextAsync());
}

async Task CheckNativeIncomplete()
{
    await using var h = await Open(NativeHtml(), NativeCsv().Replace("105;P-MA-005\r\n", "", StringComparison.Ordinal));
    var run = await new AutomaticExtractionService(h.Service).RunAsync(NativeSettings(), null, CancellationToken.None);
    Equal(4, run.Data.Rows.Count); True(!run.Data.Complete && run.Data.Warnings.Any(x => x.Contains("4 lignes pour 5", StringComparison.Ordinal)), "Le fichier tronqué reste identifié comme incomplet.");
    Equal("native-csv", run.Data.Provenance.Method); Equal("Page 1 sur 3", await h.Page.Locator("#page-number").InnerTextAsync());
}

async Task CheckNativeScopeChange()
{
    await using var h = await Open(NativeHtml(true), NativeCsv());
    var run = await new AutomaticExtractionService(h.Service).RunAsync(NativeSettings(), null, CancellationToken.None);
    True(!run.Data.Complete && !run.Data.Provenance.ScopeVerified, "Une recherche modifiée pendant le transfert ne peut pas devenir comparable.");
    True(run.Data.Warnings.Any(x => x.Contains("contexte de recherche", StringComparison.Ordinal)), "L’écart de contexte doit être visible.");
}

Task CheckSampleNormalization()
{
    var visible = new TableSnapshot { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "001 001", "A" }, new[] { "1\u202F002", "B" } } };
    var native = new ExtractionResult { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "1002", "B" }, new[] { "1001", "A" } } };
    var check = ExtractionVerificationService.VerifyNativeSample(visible, native, new());
    True(check.Available && check.Matches && check.Sample == 2, "Les séparateurs de milliers doivent être reconnus sans modifier les cellules.");
    native.Headers[0] = "Sans identité";
    True(!ExtractionVerificationService.VerifyNativeSample(visible, native, new()).Available, "Aucun numéro ne doit être déduit d’une autre colonne.");
    return Task.CompletedTask;
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
