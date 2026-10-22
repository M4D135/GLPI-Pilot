using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;
using Microsoft.Playwright;

string? browserPath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
if (string.IsNullOrWhiteSpace(browserPath))
{
    Console.Error.WriteLine("Définir GLPI_TEST_CHROMIUM vers un Chromium de test installé.");
    return 2;
}
string basic = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "basic-glpi.html"));
string temporary = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Browser-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1");
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", temporary);
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
    ("Qualité : un total final modifié invalide la réception complète sans perdre le CSV", CheckNativeFinalTotalChanged),
    ("Automatique : identité illisible après transfert, couverture non certifiée", CheckNativeFinalSampleUnavailable),
    ("Échantillon : identifiants groupés normalisés, colonnes absentes non devinées", CheckSampleNormalization),
    ("Échantillon : diagnostic de 33 écarts par colonne sans données privées", CheckSampleDiagnostics),
    ("Échantillon : contrôles C1 conservés, espaces de présentation rapprochés", CheckSampleControls),
    ("Échantillon : ponctuation et abréviations ne masquent pas une divergence", CheckSampleAbbreviations),
    ("Échantillon : équivalence de date réservée aux colonnes de date", CheckSampleDates),
    ("Échantillon : représentation native des guillemets limitée au format GLPI", CheckSampleCsvQuotes),
    ("Échantillon : en-têtes ambigus et cellules absentes non devinés", CheckSampleAmbiguity),
    ("Diagnostic local : cellules liées à leurs lignes, colonnes et valeurs originales", CheckDivergenceCoordinates),
    ("Diagnostic local : équivalences reconnues et identités non rapprochées exclues", CheckDivergenceExclusions),
    ("Diagnostic local : cellule absente distinguée d’une valeur vide", CheckDivergenceMissingCell),
    ("Diagnostic local : rapport borné, fenêtre de texte long et codes Unicode exacts", CheckDivergenceBounds),
    ("Diagnostic local : lecture bornée et noms de fichiers strictement validés", CheckDivergenceReadValidation),
    ("Reprise : pages conservées recontrôlées après annulation", () => CheckCheckpoint(false)),
    ("Reprise : page conservée modifiée, collecte redémarrée", () => CheckCheckpoint(true)),
    ("Reconnexion : expiration SSO puis reprise de la page exacte", () => CheckReconnect(false)),
    ("Reconnexion : changement de périmètre refusé après SSO", () => CheckReconnect(true)),
    ("Réseau : coupure transitoire et reprise sans doublons", () => CheckNetwork(false)),
    ("Réseau : tentatives épuisées et relevé partiel conservé", () => CheckNetwork(true)),
    ("Échantillon : mêmes ID, valeurs différentes importées avec réserve", CheckNativeWrongCells),
    ("Échantillon : 788 lignes et 32 écarts disponibles, CSV intact et Excel qualifié", () => CheckNativeReviewableSample(32)),
    ("Qualité : 788 lignes reçues, 20 identités, 17 écarts et 215 C1 conservés", () => CheckNativeReviewableSample(17)),
    ("Échantillon : cellule GLPI multivaluée complète rapprochée du CSV", () => CheckNativeMultiValue(false)),
    ("Échantillon : divergence après le premier élément multivalué non certifiée", () => CheckNativeMultiValue(true)),
    ("Échantillon : demandeurs imbriqués et textes longs GLPI entièrement rapprochés", () => CheckNativeRichCells(false)),
    ("Échantillon : fin de texte long modifiée conservée sans certification", () => CheckNativeRichCells(true)),
    ("Format GLPI 10 : présentation riche réellement rapprochée du CSV", () => CheckNativeFormattedCells("equal")),
    ("Format GLPI 10 : modification réelle conservée comme divergence", () => CheckNativeFormattedCells("changed")),
    ("Format GLPI 10 : contrôle C1 jamais converti en ponctuation", () => CheckNativeFormattedCells("c1")),
    ("Format GLPI 10 : changement de casse sans balise toujours divergent", CheckNativePlainCase),
    ("Format GLPI 10 : représentation liée à la cellule et aux coordonnées exactes", CheckRepresentationBinding),
    ("Format GLPI 10 : séparateurs exportés entre valeurs complètes", CheckNativeExportedSeparators),
    ("Format GLPI 10 : version absente ou non vérifiée sans équivalence de présentation", CheckNativeUnverifiedVersion)
};
if (args.Length != 0)
{
    if (args.Length != 2 || args[0] != "--filter") { Console.Error.WriteLine("Usage: BrowserChecks [--filter <partie du nom>]"); return 2; }
    checks = checks.Where(check => check.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToList();
    if (checks.Count == 0) { Console.Error.WriteLine("Aucune vérification ne correspond au filtre."); return 2; }
}
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
    try { Directory.Delete(temporary, recursive: true); }
    finally
    {
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", null);
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", null);
    }
}
return failures == 0 ? 0 : 1;

async Task<Harness> Open(string? html = null, string? csv = null, PaginationCheckpointStore? checkpoints = null)
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
    var service = new BrowserService(checkpoints: checkpoints);
    service.AttachForTesting(context, "http://glpi.test/front/computer.php");
    return new(context, page, service);
}

AppSettings Settings() => new()
{
    PortalUrl = "http://glpi.test/front/computer.php",
    SelectorOrigin = "http://glpi.test",
    TimeoutSeconds = 1,
    ListTimeoutSeconds = 5,
    DetailTimeoutSeconds = 5,
    ResumePagination = false,
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
    await using var h = await Open(NativeHtml(true), NativeCsv().Replace("P-MA-001", "Valeur différente", StringComparison.Ordinal));
    var run = await new AutomaticExtractionService(h.Service).RunAsync(NativeSettings(), null, CancellationToken.None);
    True(!run.Data.Complete && !run.Data.Provenance.ScopeVerified, "Une recherche modifiée pendant le transfert ne peut pas devenir comparable.");
    True(run.Data.Warnings.Any(x => x.Contains("contexte de recherche", StringComparison.Ordinal)), "L’écart de contexte doit être visible.");
    True(run.Data.Status.Contains("périmètre", StringComparison.Ordinal) && run.Data.Warnings.Any(x => x.Contains("valeurs comparables diffèrent", StringComparison.Ordinal)), "Les écarts de valeurs initiaux ne doivent ni empêcher le contrôle final ni masquer le périmètre modifié.");
}

async Task CheckNativeFinalTotalChanged()
{
    foreach (var initiallyObserved in new[] { true, false })
    {
        var html = NativeHtml().Replace("</main>", "<script>document.querySelector('#native-csv-all').addEventListener('click',()=>{document.querySelector('#page-summary').textContent='Affichage de 1 à 2 sur 6 éléments';});</script></main>", StringComparison.Ordinal);
        if (!initiallyObserved) html = html.Replace("</body>", "<script>document.querySelector('#page-summary').textContent='';</script></body>", StringComparison.Ordinal);
        var csv = NativeCsv().Replace("P-MA-001", "Valeur différente", StringComparison.Ordinal);
        await using var h = await Open(html, csv);
        var run = await new AutomaticExtractionService(h.Service).RunAsync(NativeSettings(), null, CancellationToken.None);
        Equal(5, run.Data.Rows.Count);
        True(!run.Data.Complete && run.Data.Quality is { RowCountVerified: true, FinalTotalVerified: false, ReceptionComplete: false,
            InitialSample.DifferentCells: 1, FinalSample.DifferentCells: 1 }, "Un nombre reçu égal au total initial ne certifie pas la couverture quand GLPI annonce ensuite un autre total.");
        True(run.Data.Status.Contains(initiallyObserved ? "total GLPI modifié" : "total GLPI différent du total attendu", StringComparison.Ordinal), "La réserve de total conserve sa priorité et distingue une observation initiale d’un total seulement configuré.");
        Equal(csv, await File.ReadAllTextAsync(run.NativePath!));
    }
}

async Task CheckNativeFinalSampleUnavailable()
{
    var html = NativeHtml().Replace("</main>", "<script>document.querySelector('#native-csv-all').addEventListener('click',()=>{for(const row of document.querySelectorAll('#computer-rows tr')) row.children[1].textContent='Identité illisible';});</script></main>", StringComparison.Ordinal);
    await using var h = await Open(html, NativeCsv());
    var logs = new List<string>(); var service = new AutomaticExtractionService(h.Service); service.Log += logs.Add;
    var run = await service.RunAsync(NativeSettings(), null, CancellationToken.None);
    True(!run.Data.Complete && run.Data.Warnings.Any(line => line.Contains("échantillon n’est plus disponible", StringComparison.Ordinal)), "Une identité non vérifiable après le transfert interdit un relevé complet.");
    True(logs.Any(line => line.StartsWith("ÉCHANTILLON FINAL · Rapprochement", StringComparison.Ordinal)), "Le journal doit expliquer l’indisponibilité du contrôle final.");
    True(File.Exists(run.NativePath), "Le fichier natif reste conservé.");
    True(run.Data.Quality is { ReceptionComplete: true, InitialSample.IdentityVerified: true, FinalSample.IdentityVerified: null,
        SampleIdentityVerified: null, RepresentationVerified: null }, "L’identité finale illisible reste indisponible sans annuler le compte de réception ni inventer une seconde observation positive.");
}

Task CheckSampleNormalization()
{
    var visible = new TableSnapshot { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "001 001", "A" }, new[] { "1\u202F002", "B" } } };
    var native = new ExtractionResult { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "1002", "B" }, new[] { "1001", "A" } } };
    var check = ExtractionVerificationService.VerifyNativeSample(visible, native, new());
    True(check.Available && check.Matches && check.Sample == 2, "Les séparateurs de milliers doivent être reconnus sans modifier les cellules.");
    native.Headers[1] = "Autre colonne";
    True(ExtractionVerificationService.VerifyNativeSample(visible, native, new()).Observation() is { IdentityVerified: true, ComparedCells: 0, RepresentationVerified: null }, "L’identité seule ne crée pas une comparaison de valeurs observée.");
    native.Headers[0] = "Sans identité";
    True(!ExtractionVerificationService.VerifyNativeSample(visible, native, new()).Available, "Aucun numéro ne doit être déduit d’une autre colonne.");
    return Task.CompletedTask;
}

Task CheckSampleDiagnostics()
{
    // Synthetic values only. This reproduces the reported count, not the private export.
    var headers = new List<string> { "ID", "Titre", "Statut", "Priorité", "Date d'ouverture", "Dernière modification", "Attribué à - Technicien", "Lieu", "Catégorie", "Entité", "Demandeur", "Urgence", "Impact", "En-tête privé synthétique" };
    var visible = new TableSnapshot { Headers = headers };
    var native = new ExtractionResult { Headers = headers.ToList() };
    for (var index = 0; index < 20; index++)
    {
        var row = Enumerable.Range(0, headers.Count).Select(column => column == 0 ? (800100 + index).ToString() : $"Secret synthétique {index}/{column}").ToArray();
        visible.Rows.Add(row); native.Rows.Add(row.ToArray());
        native.Rows[index][1] = "Titre modifié confidentiel";
        if (index < 13) native.Rows[index][13] = "Autre valeur confidentielle";
    }
    var check = ExtractionVerificationService.VerifyNativeSample(visible, native, new());
    True(check.Available && !check.Matches && check.Sample == 20 && check.Missing == 0, "Les bons ID ne doivent pas annuler les écarts de valeur.");
    True(check.Explanation.Contains("33/260", StringComparison.Ordinal), "Le total des écarts doit rester exact.");
    var diagnostic = check.Explanation + string.Join("\n", check.Diagnostics);
    True(diagnostic.Contains("Colonne liste 2 → CSV 2 ; rôle=title ; écarts=20/20", StringComparison.Ordinal)
        && diagnostic.Contains("Colonne liste 14 → CSV 14 ; rôle=other ; écarts=13/20", StringComparison.Ordinal), "Les positions et catégories doivent localiser les écarts.");
    foreach (var secret in visible.Rows.Concat(native.Rows).SelectMany(row => row).Append(headers[^1]))
        True(!diagnostic.Contains(secret, StringComparison.Ordinal), "Le diagnostic ne doit recopier ni ID, ni valeur, ni en-tête libre.");
    return Task.CompletedTask;
}

NativeSampleVerification CompareCell(string shown, string received, string header = "Titre") =>
    ExtractionVerificationService.VerifyNativeSample(
        new TableSnapshot { Headers = new() { "ID", header }, Rows = new() { new[] { "123", shown } } },
        new ExtractionResult { Headers = new() { "ID", header }, Rows = new() { new[] { "123", received } } }, new());

Task CheckSampleControls()
{
    foreach (var (shown, received) in new[] { ("Début fin", "Début\u0085fin"), ("Texte", "\u0085Texte\u0085"), ("l’intervention", "l\u0092intervention"), ("a – b", "a \u0096 b") })
    {
        var check = CompareCell(shown, received);
        True(!check.Matches && check.Diagnostics.Any(line => line.Contains("avec contrôle C1=1", StringComparison.Ordinal)), "Un contrôle C1 doit rester divergent et identifié, même si .NET le considère comme un espace.");
    }
    True(CompareCell("Début\u00a0fin\nligne", "Début\u202ffin\tligne").Matches, "Les espaces et retours de présentation restent équivalents.");
    True(!CompareCell("22/10/2026 17:20", "\u00852026-10-22 17:20:00\u0085", "Date d'ouverture").Matches, "Le parseur de date ne doit pas effacer indirectement un contrôle C1.");
    True(!CompareCell("Titre…", "Titre\u0085complet").Matches, "Un contrôle C1 divergent ne doit pas être masqué par une abréviation.");
    var original = "l\u0092intervention";
    var visible = new TableSnapshot { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "123", "l’intervention" } } };
    var native = new ExtractionResult { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "123", original } } };
    _ = ExtractionVerificationService.VerifyNativeSample(visible, native, new());
    Equal(original, native.Rows[0][1]); Equal("l’intervention", visible.Rows[0][1]);
    return Task.CompletedTask;
}

Task CheckSampleAbbreviations()
{
    True(!CompareCell("Avant…après", "Avant…autre").Matches, "Une ellipse au milieu d’un texte ne doit pas l’exclure du contrôle.");
    True(!CompareCell("Autre titre…", "Titre complet").Matches, "Une abréviation dont le préfixe diffère reste incompatible.");
    True(!CompareCell("…", "Une valeur arbitraire").Matches, "Une ellipse seule ne fournit aucun préfixe à vérifier.");
    foreach (var shown in new[] { "Titre…", "Titre..." })
    {
        var abbreviated = CompareCell(shown, "Titre complet");
        True(abbreviated.Matches && abbreviated.Diagnostics.Any(line => line.Contains("abrégées compatibles=1", StringComparison.Ordinal)), "Seule l’abréviation au préfixe compatible est signalée comme exclue.");
    }
    var identical = CompareCell("Texte…", "Texte…");
    True(identical.Matches && identical.Explanation.Contains("et 1 valeurs", StringComparison.Ordinal), "Une ponctuation identique est réellement comparée, pas ignorée.");
    return Task.CompletedTask;
}

Task CheckSampleDates()
{
    True(CompareCell("22/10/2026 17:20", "2026-10-22 17:20:00", "Date d'ouverture").Matches, "La même date reste comparable entre formats reconnus.");
    var difference = CompareCell("22/10/2026 17:20", "2026-10-22 17:21:00", "Date d'ouverture");
    True(!difference.Matches && difference.Diagnostics.Any(line => line.Contains("date/heure=1", StringComparison.Ordinal)), "Une minute différente ne doit pas être masquée.");
    True(!CompareCell("22/10/2026", "2026-10-22").Matches, "Deux titres différents ne deviennent pas équivalents parce qu’ils ressemblent à des dates.");
    return Task.CompletedTask;
}

Task CheckSampleCsvQuotes()
{
    // Search::csv_clean in the public GLPI 9.5.13 and 10.0.22 sources uses
    // str_replace("\"", "''", value). This is not a general punctuation mapping.
    var shown = "Demande \"Écran\" à préparer"; var received = "Demande ''Écran'' à préparer";
    var check = CompareCell(shown, received);
    True(check.Matches && check.Diagnostics.Any(line => line.Contains("format guillemets CSV=1", StringComparison.Ordinal)), "La représentation exacte des guillemets par l’export GLPI doit être rapprochée et comptée.");
    True(!CompareCell(shown, "Demande ''Réseau'' à préparer").Matches, "Un mot changé doit toujours diverger avec le même format de guillemets.");
    True(!CompareCell(received, shown).Matches, "La transformation d’export ne s’applique pas dans le sens inverse.");
    True(!CompareCell("Texte ’typographique’", "Texte ''typographique''").Matches, "Les apostrophes typographiques ne doivent pas être assimilées à des guillemets.");
    True(!CompareCell(shown, received + "\u0085").Matches, "Une représentation de guillemets ne doit pas masquer un contrôle C1.");
    return Task.CompletedTask;
}

Task CheckSampleAmbiguity()
{
    var visible = new TableSnapshot { Headers = new() { "ID", "Code-A", "Code A" }, Rows = new() { new[] { "123", "Alpha", "Beta" } } };
    var native = new ExtractionResult { Headers = new() { "ID", "Code A", "Code-A" }, Rows = new() { new[] { "123", "Beta", "Alpha" } } };
    var ambiguous = ExtractionVerificationService.VerifyNativeSample(visible, native, new());
    True(!ambiguous.Available && !ambiguous.Matches && ambiguous.Diagnostics.Any(line => line.Contains("2, 3", StringComparison.Ordinal)), "Des en-têtes normalisés identiques ne doivent pas être liés par première occurrence.");
    native.Rows[0][0] = "999";
    var wrongFile = ExtractionVerificationService.VerifyNativeSample(visible, native, new());
    True(wrongFile.Available && !wrongFile.Matches && wrongFile.Missing == 1, "Des en-têtes ambigus ne doivent pas masquer les identifiants absents d’un mauvais fichier.");
    native.Rows[0][0] = "123";
    visible.Headers = new() { "ID", "Titre" }; native.Headers = new() { "ID", "Titre", "Titre (2)" };
    var renamed = ExtractionVerificationService.VerifyNativeSample(visible, native, new());
    True(!renamed.Available && !renamed.Matches, "Le suffixe ajouté à un en-tête CSV dupliqué ne prouve pas quel champ correspond au premier titre visible.");
    visible.Headers = new() { "ID", "Titre" }; native.Headers = new() { "ID", "Titre" };
    native.Rows[0] = new[] { "123" };
    var missing = ExtractionVerificationService.VerifyNativeSample(visible, native, new());
    True(missing.Available && !missing.Matches && missing.Diagnostics.Any(line => line.Contains("cellule absente=1", StringComparison.Ordinal)), "Une cellule absente ne doit pas être ignorée comme une valeur compatible.");
    return Task.CompletedTask;
}

Task CheckDivergenceCoordinates()
{
    var shown = new TableSnapshot
    {
        Headers = new() { "ID", "Titre", "Date d'ouverture", "Note technique synthétique" },
        Rows = new() { new[] { "001", "Affichage <alpha>", "22/10/2026 17:20", "" }, new[] { "002", "Titre visible synthétique", "22/10/2026 17:20", "l\u0092intervention" } }
    };
    var native = new ExtractionResult
    {
        Headers = new() { "Note technique synthétique", "ID", "Date d'ouverture", "Titre" },
        Rows = new()
        {
            new[] { "Hors échantillon", "999", "22/10/2026 17:20", "Hors échantillon" },
            new[] { "l’intervention", "2", "2026-10-22 17:20:00", "Titre reçu synthétique" },
            new[] { "Valeur reçue synthétique", "1", "22/10/2026 17:21", "Affichage <beta>" }
        }
    };
    var shownBefore = JsonSerializer.Serialize(shown); var nativeBefore = JsonSerializer.Serialize(native);
    var differences = new List<NativeCellDivergence>();
    var check = ExtractionVerificationService.VerifyNativeSample(shown, native, new(), onDifference: differences.Add);
    Equal(5, check.DifferentCells); Equal(5, differences.Count); True(!check.Matches && check.IdentityVerified == true, "Le rapport ne doit pas modifier la conclusion de comparaison.");
    var title = differences.Single(cell => cell.TicketId == "1" && cell.Role == "title");
    Equal(0, title.DisplayRow); Equal(2, title.CsvRow); Equal(1, title.DisplayColumn); Equal(3, title.CsvColumn);
    Equal("Affichage <alpha>", title.RawDisplay); Equal("Affichage <beta>", title.RawCsv); Equal("Titre", title.DisplayHeader); Equal("Titre", title.CsvHeader);
    Equal(1, differences.Count(cell => cell.Category == "date")); Equal(1, differences.Count(cell => cell.Category == "empty")); Equal(1, differences.Count(cell => cell.Category == "controls"));
    Equal("l\u0092intervention", differences.Single(cell => cell.Category == "controls").RawDisplay);
    Equal(shownBefore, JsonSerializer.Serialize(shown)); Equal(nativeBefore, JsonSerializer.Serialize(native));
    var safeDiagnostic = JsonSerializer.Serialize(check);
    foreach (var privateText in new[] { "Affichage <alpha>", "Affichage <beta>", "Titre visible synthétique", "Titre reçu synthétique", "Note technique synthétique" })
        True(!safeDiagnostic.Contains(privateText, StringComparison.Ordinal), "Les contenus autorisés dans le rapport ne doivent pas rejoindre le diagnostic général.");
    return Task.CompletedTask;
}

Task CheckDivergenceExclusions()
{
    var differences = new List<NativeCellDivergence>();
    foreach (var (shown, csv, header) in new[]
    {
        ("même\u00a0texte\nfin", "même texte fin", "Titre"),
        ("Demande \"écran\"", "Demande ''écran''", "Titre"),
        ("22/10/2026 17:20", "2026-10-22 17:20:00", "Date d'ouverture"),
        ("Texte long…", "Texte long complet", "Titre")
    })
    {
        var visible = new TableSnapshot { Headers = new() { "ID", header }, Rows = new() { new[] { "123", shown } } };
        var native = new ExtractionResult { Headers = new() { "ID", header }, Rows = new() { new[] { "123", csv } } };
        True(ExtractionVerificationService.VerifyNativeSample(visible, native, new(), onDifference: differences.Add).Matches, "Les équivalences déjà reconnues restent inchangées.");
    }
    var rich = new TableSnapshot
    {
        Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "123", "Écran" } },
        CsvRepresentations = new() { new CsvCellRepresentation?[] { null, new() { Row = 0, Column = 1, Kind = "glpi10-rich-text-v1", Source = "Écran", Text = "ÉCRAN" } } }
    };
    var exported = new ExtractionResult { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "123", "ÉCRAN" } } };
    True(ExtractionVerificationService.VerifyNativeSample(rich, exported, new(), onDifference: differences.Add).Matches, "La représentation prouvée liée à la cellule ne doit pas être dupliquée comme écart.");
    exported.Rows[0][0] = "456";
    Equal(1, ExtractionVerificationService.VerifyNativeSample(rich, exported, new(), onDifference: differences.Add).Missing);
    Equal(0, differences.Count);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { ExtractionVerificationService.VerifyNativeSample(rich, exported, new(), cancelled.Token, differences.Add); throw new Exception("Annulation ignorée."); }
    catch (OperationCanceledException) { }
    Equal(0, differences.Count);
    return Task.CompletedTask;
}

Task CheckDivergenceMissingCell()
{
    var shown = new TableSnapshot { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "123", "" } } };
    var native = new ExtractionResult { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "123" } } };
    var differences = new List<NativeCellDivergence>();
    var check = ExtractionVerificationService.VerifyNativeSample(shown, native, new(), onDifference: differences.Add);
    Equal(1, check.DifferentCells); var cell = differences.Single();
    True(cell.CsvCellMissing && !cell.DisplayCellMissing && cell.Category == "missing-cell", "Une cellule absente reste distincte de la chaîne vide effectivement lue.");
    Equal("", cell.RawDisplay); Equal("", cell.RawCsv);
    native.Headers.Add("Titre (2)"); differences.Clear();
    True(!ExtractionVerificationService.VerifyNativeSample(shown, native, new(), onDifference: differences.Add).Available, "Une association ambiguë doit rester indisponible.");
    Equal(0, differences.Count);
    return Task.CompletedTask;
}

Task CheckDivergenceBounds()
{
    var report = new NativeCellDivergenceReport("abcd1234", new DateTimeOffset(2026, 10, 22, 20, 51, 17, TimeSpan.Zero));
    var prefix = new string('a', 6000);
    report.Add("initial", new("123", 2, 17, 4, 9, "Description", "Description", "other", prefix + "😀alpha", prefix + "😁beta", "other"));
    report.Add("final", new("123", 2, 17, 4, 9, "Description", "Description", "other", "l\u0092intervention", "l’intervention", "controls"));
    report.Add("final", new(new string('7', 200), 0, 0, 1, 1, new string('H', 700), "Titre", "title", new string('b', 4095) + "😀fin", "autre", "other"));
    var beyondAnalysis = new string('x', 1024 * 1024 + 1);
    report.Add("initial", new("123", 0, 0, 1, 1, "Titre", "Titre", "title", beyondAnalysis + "a", beyondAnalysis + "b", "other"));
    for (var index = 4; index < 83; index++) report.Add("final", new("123", 0, 0, 1, 1, "Titre", "Titre", "title", "a", "b", "other"));
    Equal(80, report.Count); Equal(83, report.TotalCount);
    var name = report.Save(Path.Combine(temporary, "report-bounds"));
    var document = NativeCellDivergenceReport.Read(Path.Combine(temporary, "report-bounds"), name);
    Equal("abcd1234", document.RunId); Equal(80, document.Count); Equal(83, document.TotalCount); True(document.Truncated, "Le plafond d’entrées et les valeurs coupées doivent être explicitement indiqués.");
    var longCell = document.Entries[0]; Equal(6001, longCell.FirstDifferenceOffset); Equal(4096, longCell.RawDisplay.Length);
    True(longCell.DisplayTruncated && longCell.CsvTruncated && !longCell.AnalysisTruncated, "Une fenêtre exacte doit rester disponible après le préfixe conservé.");
    True(longCell.DisplayWindow.Contains("😀alpha", StringComparison.Ordinal) && longCell.CsvWindow.Contains("😁beta", StringComparison.Ordinal), "La fin divergente du texte doit rester lisible.");
    True(longCell.DisplayCodePoints.Any(code => code.Offset == 6000 && code.Code == "U+1F600")
        && longCell.CsvCodePoints.Any(code => code.Offset == 6000 && code.Code == "U+1F601"), "Les paires de substitution doivent être décrites comme des points de code Unicode complets.");
    var controls = document.Entries[1]; Equal("l\u0092intervention", controls.RawDisplay); Equal("l’intervention", controls.RawCsv);
    True(controls.DisplayCodePoints.Any(code => code.Code == "U+0092") && controls.Hints.Any(hint => hint.Contains("aucune conversion", StringComparison.Ordinal)), "Le contrôle C1 doit rester exact et aucune correction ne doit être présumée.");
    True(document.Entries[2].MetadataTruncated && document.Entries[2].RawDisplay.Length == 4095 && !char.IsHighSurrogate(document.Entries[2].RawDisplay[^1]), "Les limites ne doivent couper ni masquer une paire UTF-16 ni un en-tête tronqué.");
    True(document.Entries[3].AnalysisTruncated && document.Entries[3].FirstDifferenceOffset is null, "La limite d’analyse ne doit pas inventer la première position divergente.");
    True(new FileInfo(Path.Combine(temporary, "report-bounds", name)).Length <= NativeCellDivergenceReport.MaximumFileBytes, "Le fichier doit rester dans sa limite de lecture.");
    return Task.CompletedTask;
}

Task CheckDivergenceReadValidation()
{
    var folder = Path.Combine(temporary, "report-read"); var report = new NativeCellDivergenceReport("12ab34cd", DateTimeOffset.UtcNow);
    report.Add("initial", new("123", 0, 0, 1, 1, "Titre", "Titre", "title", "texte affiché", "texte CSV", "other"));
    var name = report.Save(folder); var path = Path.Combine(folder, name); var bytes = File.ReadAllBytes(path);
    void Rejected(string candidate)
    {
        try { NativeCellDivergenceReport.Read(folder, candidate); throw new Exception("Diagnostic invalide accepté."); }
        catch (InvalidDataException) { }
    }
    foreach (var candidate in new[] { "../" + name, "..\\" + name, path, name + "\n", "autre.json" }) Rejected(candidate);
    void Tampered(Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(bytes)!.AsObject(); edit(json); File.WriteAllText(path, json.ToJsonString()); Rejected(name);
    }
    Tampered(json => json["runId"] = "deadbeef");
    Tampered(json => json["version"] = 2);
    Tampered(json => json["totalCount"] = 0);
    Tampered(json => json["entries"]![0]!["rawDisplay"] = new string('x', 4097));
    Tampered(json => json["entries"]![0]!["displayWindowStart"] = -1);
    Tampered(json => json["entries"] = new JsonArray(Enumerable.Range(0, 81).Select(_ => json["entries"]![0]!.DeepClone()).ToArray()));
    File.WriteAllText(path, "{invalid JSON"); Rejected(name);
    using (var file = new FileStream(path, FileMode.Create, FileAccess.Write)) file.SetLength(NativeCellDivergenceReport.MaximumFileBytes + 1L);
    Rejected(name);
    File.WriteAllBytes(path, bytes); Equal(1, NativeCellDivergenceReport.Read(folder, name).Count);
    return Task.CompletedTask;
}

string PagedHtml() => WithCriteria()
    .Replace("<button id=\"next\" aria-label=\"Page suivante\">Suivant</button>", "<a id=\"next\" rel=\"next\" href=\"?start=2\" aria-label=\"Page suivante\">Suivant</a>", StringComparison.Ordinal)
    .Replace("let current=0;", "let current=Math.min(2,Math.floor(Number(new URL(location.href).searchParams.get('start')||'0')/2));", StringComparison.Ordinal)
    .Replace("document.getElementById('next').addEventListener('click',()=>{if(current<2){current++;render();}});", "", StringComparison.Ordinal)
    .Replace("</body>", "<script>document.getElementById('next').href='?start='+((current+1)*2);document.getElementById('next').setAttribute('aria-disabled',current===2?'true':'false');</script></body>", StringComparison.Ordinal);

async Task CheckCheckpoint(bool changed)
{
    var store = new PaginationCheckpointStore(Path.Combine(temporary, "resume-" + Guid.NewGuid().ToString("N")));
    var settings = Settings(); settings.ResumePagination = true; settings.TimeoutSeconds = 8;
    await using (var h = await Open(PagedHtml(), checkpoints: store))
    {
        using var cancellation = new CancellationTokenSource(); var calls = 0;
        var result = await h.Service.ExtractAsync(await Table(h), true, settings, new InlineProgress<ExtractionProgress>(_ => { if (++calls == 2) cancellation.Cancel(); }), cancellation.Token);
        Equal(4, result.Rows.Count); True(!result.Complete && store.Load(settings)?.Data.Pages == 2, "Les deux pages contrôlées doivent être conservées avant l’annulation.");
    }
    var html = changed ? PagedHtml().Replace("P-MA-003", "P-MA-003 corrigé", StringComparison.Ordinal) : PagedHtml();
    await using (var h = await Open(html, checkpoints: store))
    {
        var progressPages = 0;
        var result = await h.Service.ExtractAsync(await Table(h), true, settings, new InlineProgress<ExtractionProgress>(_ => progressPages++), CancellationToken.None);
        True(result.Complete, string.Join(" | ", result.Warnings)); Equal(5, result.Rows.Count); Equal(3, result.Pages);
        Equal(!changed, result.Provenance.Resumed); Equal(changed ? 3 : 1, progressPages);
        True(store.Load(settings) == null, "Le point terminé doit être supprimé.");
        if (changed) True(result.Rows.Any(row => row.Contains("P-MA-003 corrigé")), "La collecte redémarrée doit contenir la valeur actuelle.");
    }
}

async Task CheckReconnect(bool changedScope)
{
    await using var h = await Open(PagedHtml()); var requests = 0;
    await h.Context.RouteAsync("**/front/computer.php?start=2", async route =>
    {
        var body = ++requests == 1
            ? "<html><body><form><input type='password'></form><script>setTimeout(()=>location.href='/front/computer.php?start=2',1100)</script></body></html>"
            : changedScope ? PagedHtml().Replace("value='origin'", "value='other'", StringComparison.Ordinal) : PagedHtml();
        await route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = body });
    });
    var settings = Settings(); settings.TimeoutSeconds = 8; var logs = new List<string>(); h.Service.Log += logs.Add;
    var result = await h.Service.ExtractAsync(await Table(h), true, settings, null, CancellationToken.None);
    Equal(changedScope ? 2 : 5, result.Rows.Count); Equal(!changedScope, result.Complete);
    True(logs.Any(line => line.StartsWith("RECONNEXION", StringComparison.Ordinal)), "L’expiration SSO doit passer par le chemin de reprise.");
    if (changedScope) True(result.Warnings.Any(line => line.Contains("périmètre", StringComparison.OrdinalIgnoreCase)), "Le changement de recherche doit être expliqué.");
    else Equal(3, result.Pages);
}

async Task CheckNetwork(bool permanent)
{
    await using var h = await Open(PagedHtml()); var requests = 0;
    await h.Context.RouteAsync("**/front/computer.php?start=2", async route =>
    {
        if (++requests == 1 || permanent) await route.AbortAsync("connectionreset");
        else await route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = PagedHtml() });
    });
    var settings = Settings(); settings.TimeoutSeconds = 8; settings.ReconnectAttempts = 2; var logs = new List<string>(); h.Service.Log += logs.Add;
    var result = await h.Service.ExtractAsync(await Table(h), true, settings, null, CancellationToken.None);
    Equal(permanent ? 2 : 5, result.Rows.Count); Equal(!permanent, result.Complete);
    var attempts = logs.Count(line => line.StartsWith("RECONNEXION", StringComparison.Ordinal));
    True(requests >= 2 && attempts == (permanent ? 2 : 1), $"La reconnexion doit borner les tentatives applicatives : {attempts} reprises, {requests} requêtes (reprises internes Chromium comprises). " + string.Join(" | ", result.Warnings));
    if (!permanent) Equal(3, result.Pages);
}

async Task CheckNativeWrongCells()
{
    await using var h = await Open(NativeHtml(), NativeCsv().Replace("P-MA-001", "Une autre valeur", StringComparison.Ordinal));
    var logs = new List<string>(); string? retained = null;
    var service = new AutomaticExtractionService(h.Service); service.Log += logs.Add; service.NativeFileSaved += path => retained = path;
    var run = await service.RunAsync(NativeSettings(), null, CancellationToken.None);
    Equal(5, run.Data.Rows.Count);
    True(!run.Data.Complete && run.Data.Provenance.ScopeVerified, "Les valeurs divergentes restent consultables sans certifier leur concordance ; le contrôle final du périmètre doit s'exécuter.");
    True(run.Data.Warnings.Any(line => line.Contains("valeurs comparables diffèrent", StringComparison.Ordinal)), "La réserve de comparaison doit accompagner les données importées.");
    True(run.Data.Quality is { ReceptionComplete: true, ScopeStable: true, FinalTotalVerified: true,
        InitialSample: { IdentityVerified: true, RepresentationVerified: false },
        FinalSample: { IdentityVerified: true, RepresentationVerified: false } },
        "La réception complète et le périmètre stable restent distincts des valeurs divergentes constatées avant et après le transfert.");
    Equal("Import terminé · 5 lignes chargées", run.Data.Status);
    True(logs.Any(line => line.StartsWith("ÉCHANTILLON FINAL ·", StringComparison.Ordinal)), "L'import doit aller jusqu'au contrôle final.");
    True(File.Exists(retained) && (await File.ReadAllTextAsync(retained!)).Contains("Une autre valeur", StringComparison.Ordinal), "Le fichier natif divergent doit rester intact.");
    True(logs.Any(line => line.StartsWith("ÉCHANTILLON DÉTAIL · Colonne liste", StringComparison.Ordinal)), "Le diagnostic de colonne doit être présent dans le vrai parcours automatique.");
    True(!string.Join("\n", logs).Contains("Une autre valeur", StringComparison.Ordinal), "La valeur divergente ne doit pas être inscrite au journal.");
    Equal("Page 1 sur 3", await h.Page.Locator("#page-number").InnerTextAsync());
}

async Task CheckNativeReviewableSample(int changedCells)
{
    // Synthetic equivalent of the reported shape, with no original headers,
    // identities, addresses or contents. Keep the native export authoritative.
    string[] headers = ["ID", "Titre", "Statut", "Priorité", "Date de création", "Dernière modification", "Demandeur", "Attribué à - Technicien", "Entité", "Type", "Description", "Suivis", "Solution", "Catégorie"];
    var rows = Enumerable.Range(1, 788).Select(number => new[] {
        number.ToString(), "Bureau Exemple " + number, "En cours", "Moyenne", "2026-01-01 09:00", "2026-01-02 09:00", "Demandeur Exemple", "Technicien Exemple", "Entité Exemple", "Incident", "Description Exemple", "Suivi Exemple", "Solution Exemple", "Matériel"
    }).ToList();
    var displayed = rows.Take(20).Select(row => row.ToArray()).ToArray();
    displayed[0][1] = "Bureau \"Exemple\""; rows[0][1] = "Bureau ''Exemple''";
    for (var index = 0; index < 2; index++) rows[index][6] += " modifié";
    for (var index = 0; index < (changedCells == 17 ? 5 : 14); index++) rows[index][10] += index < 2 ? "\u0092modifié" : " modifié";
    for (var index = 0; index < (changedCells == 17 ? 4 : 6); index++) rows[index][11] += " modifié";
    for (var index = 0; index < (changedCells == 17 ? 6 : 10); index++) rows[index][12] += index == 0 ? "\u0085modifié" : " modifié";
    if (changedCells == 17) for (var index = 20; index < 232; index++) rows[index][10] += "\u0092contrôle synthétique";
    string H(string value) => System.Net.WebUtility.HtmlEncode(value);
    var markup = "<thead><tr><th><input type='checkbox' aria-label='Tout sélectionner'></th>" + string.Concat(headers.Select(header => "<th>" + H(header) + "</th>")) + "</tr></thead><tbody id='computer-rows'>"
        + string.Concat(displayed.Select(row => "<tr><td><input type='checkbox'></td>" + string.Concat(row.Select(value => "<td>" + H(value) + "</td>")) + "</tr>")) + "</tbody>";
    var html = NativeHtml().Replace("</body>", "<script>document.querySelector('#search-results').innerHTML=" + System.Text.Json.JsonSerializer.Serialize(markup)
        + ";document.querySelector('#page-summary').textContent='Affichage de 1 à 20 sur 788 éléments';document.querySelector('#page-number').textContent='Page 1 sur 40';</script></body>", StringComparison.Ordinal);
    string CsvRow(IEnumerable<string> row) => string.Join(';', row.Select(value => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
    var csv = "\ufeff" + CsvRow(headers) + "\r\n" + string.Join("\r\n", rows.Select(CsvRow)) + "\r\n";
    await using var h = await Open(html, csv);
    var settings = NativeSettings(); settings.ExpectedTotal = 788; settings.MaxRows = 1000; settings.AutoSaveXlsx = true;
    settings.Cities.Enabled = true; settings.Cities.Cities = [new() { Name = "Bureau", Aliases = ["BUR"] }];
    var logs = new List<string>(); var service = new AutomaticExtractionService(h.Service); service.Log += logs.Add;
    var run = await service.RunAsync(settings, null, CancellationToken.None);
    Equal(788, run.Data.Rows.Count); Equal(788, run.Data.ExpectedTotal);
    True(!run.Data.Complete && run.Data.Provenance.ScopeVerified && run.Data.Provenance.Method == "native-csv", "L'export validé par ses ID doit être chargé avec une réserve de valeurs, sans repli ni certification abusive.");
    True(run.Cities is { Applied: true, MatchedCount: 788 }, "L'enrichissement des villes doit rester disponible et conserver la réserve du relevé.");
    True(logs.Any(line => line.Contains($"{changedCells}/260 valeurs comparables diffèrent", StringComparison.Ordinal)), "Le scénario doit réellement reproduire le nombre d’écarts sur 260 comparaisons.");
    True(run.Data.Quality is { SourceRows: 788, ExpectedRows: 788, ReceptionComplete: true, FinalTotalVerified: true,
        SampleIdentityVerified: true, RepresentationVerified: false }, "La réception et les identités restent confirmées sans certifier une représentation divergente.");
    foreach (var sample in new[] { run.Data.Quality!.InitialSample, run.Data.Quality.FinalSample })
    {
        True(sample is { IdentityVerified: true, RepresentationVerified: false, SampleSize: 20, MissingSampleIds: 0, ComparedCells: 260, EquivalentFormatCells: 1 }, "Les deux observations conservent l’identité et l’équivalence exacte de guillemets.");
        Equal(changedCells, sample!.DifferentCells);
    }
    Equal(changedCells == 17 ? 215L : 3L, run.Data.Quality.C1ControlCount);
    Equal(changedCells * 2, run.Data.Quality.DivergenceReportCount);
    Equal(changedCells * 2, run.Data.Quality.DivergenceTotalCount);
    var report = NativeCellDivergenceReport.Read(AppPaths.CellDivergenceFolder, run.Data.Quality.DivergenceReportName);
    Equal(run.Data.Provenance.RunId, report.RunId); Equal(changedCells * 2, report.Count);
    Equal(changedCells, report.Entries.Count(entry => entry.Phase == "initial")); Equal(changedCells, report.Entries.Count(entry => entry.Phase == "final"));
    True(report.Entries.All(entry => entry.Role != "title"), "Les guillemets CSV reconnus comme équivalents ne doivent pas entrer dans le rapport.");
    True(report.Entries.Any(entry => entry.RawCsv.Contains("\u0092modifié", StringComparison.Ordinal)) && !report.Truncated, "Les valeurs divergentes et leurs contrôles C1 doivent être conservés dans le fichier dédié.");
    var qualityJson = JsonSerializer.Serialize(run.Data.Quality);
    True(!qualityJson.Contains("Description Exemple", StringComparison.Ordinal) && !qualityJson.Contains("Demandeur Exemple", StringComparison.Ordinal), "La qualité ne transporte que le nom et les compteurs du fichier dédié.");
    Equal(run.Data.Warnings.Count, run.Data.Warnings.Distinct(StringComparer.Ordinal).Count());
    True(logs.Any(line => line.StartsWith("ÉCHANTILLON FINAL ·", StringComparison.Ordinal)) && logs.Any(line => line.Contains("contrôle avant/après=identique", StringComparison.Ordinal)), "Les contrôles finaux doivent être exécutés malgré les écarts d'affichage.");
    True(File.Exists(run.ExcelPath) && Path.GetFileName(run.ExcelPath!).StartsWith("GLPI-a-verifier-", StringComparison.Ordinal), "L'export Excel doit être fourni avec son statut explicite.");
    var nativeBytes = await File.ReadAllBytesAsync(run.NativePath!);
    True(Encoding.UTF8.GetBytes(csv).SequenceEqual(nativeBytes), "Le CSV UTF-8 BOM et ses caractères C1 doivent rester identiques octet pour octet.");
    Equal(rows[0][10], run.Data.Rows[0][10]); Equal(rows[0][12], run.Data.Rows[0][12]);
    True(!string.Join('\n', logs).Contains("Description Exemple", StringComparison.Ordinal) && !string.Join('\n', logs).Contains("Demandeur Exemple", StringComparison.Ordinal), "Les diagnostics de comparaison ne doivent pas recopier les données synthétiques.");
    Equal("Page 1 sur 40", await h.Page.Locator("#page-number").InnerTextAsync());
}

async Task CheckNativeMultiValue(bool changed, bool exportedSeparator = false)
{
    // GLPI 9.5/10 Search::showItem + Html::showToolTip, with synthetic contents.
    // Only the first value is visible; the full static popup is already in this cell.
    var html = NativeHtml().Replace("</body>", """
        <style>.tooltip-invisible { display:none }</style><script>
        for (const row of document.querySelectorAll('#computer-rows tr')) {
          const id = row.dataset.id; const cell = row.children[2]; const name = cell.textContent;
          cell.innerHTML = `<a href='/front/computer.form.php?id=${id}'>${name}</a> <span id='tooltip${id}' class='fas fa-comments fa-fw'></span><div id='contenttooltip${id}' class='tooltip-invisible'><div class='fup-popup'><a href='/front/computer.form.php?id=${id}'>${name}</a><br>Annexe-${id}<br></div></div>`;
        }
        </script></body>
        """, StringComparison.Ordinal);
    var csv = NativeCsv().Replace("101;P-MA-001", "101;\"P-MA-001\nAnnexe-101\"", StringComparison.Ordinal)
        .Replace("102;P-MA-002", "102;\"P-MA-002\nAnnexe-102\"", StringComparison.Ordinal);
    if (changed) csv = csv.Replace("Annexe-101", "Annexe modifiée", StringComparison.Ordinal);
    if (exportedSeparator)
    {
        csv = csv.Replace("\nAnnexe", "<br>Annexe", StringComparison.Ordinal);
        html = WithOfficialGlpiVersion(html);
    }
    await using var h = await Open(html, csv);
    var logs = new List<string>(); string? retained = null;
    var service = new AutomaticExtractionService(h.Service); service.Log += logs.Add; service.NativeFileSaved += path => retained = path;
    if (changed)
    {
        var run = await service.RunAsync(NativeSettings(), null, CancellationToken.None);
        True(!run.Data.Complete && run.Data.Warnings.Any(line => line.Contains("valeurs comparables diffèrent", StringComparison.Ordinal)), "Le second élément doit participer à la comparaison sans rendre le CSV entier inaccessible.");
        Equal("P-MA-001" + (exportedSeparator ? "<br>" : "\n") + "Annexe modifiée", run.Data.Rows[0][1]);
        True(!string.Join("\n", logs).Contains("Annexe modifiée", StringComparison.Ordinal), "Le diagnostic ne doit pas exposer le contenu du popup ni du CSV.");
    }
    else
    {
        var run = await service.RunAsync(NativeSettings(), null, CancellationToken.None);
        True(run.Data.Complete && run.Data.Rows[0][1] == "P-MA-001" + (exportedSeparator ? "<br>" : "\n") + "Annexe-101", "La cellule complète doit être rapprochée sans retirer le second élément du CSV.");
        if (exportedSeparator) Equal(2, run.Data.Quality!.InitialSample!.EquivalentFormatCells);
        True(logs.Any(line => line.StartsWith("ÉCHANTILLON · Les 2 identifiants et 2 valeurs", StringComparison.Ordinal)), "Les cellules complètes doivent être effectivement comparées, pas exclues.");
    }
    True(File.Exists(retained), "Le téléchargement natif est conservé."); Equal(csv, await File.ReadAllTextAsync(retained!));
}

async Task CheckNativeExportedSeparators()
{
    await CheckNativeMultiValue(false, exportedSeparator: true);
    await CheckNativeMultiValue(true, exportedSeparator: true);
}

async Task CheckNativeRichCells(bool changed)
{
    // Public GLPI Search::giveItem/showItem and DbUtils::getUserName structures:
    // cloned long-text triplets and nested hidden user cards, synthetic data only.
    var html = NativeHtml().Replace("</body>", """
        <style>.tooltip-invisible { display:none }</style><script>
        const user = (name,id) => `${name}<a href='/front/user.form.php?id=${id}'><span id='tooltip${id}' class='fas fa-info'></span></a><div id='contenttooltip${id}' class='tooltip-invisible'>FICHE PRIVEE FICTIVE<br>CONTACT FICTIF<br></div>`;
        const long = (prefix,full,id) => `<span id='text${id}'>${prefix}&nbsp;(...)</span> <span id='tooltip${id+1}' class='fas fa-comments'></span><div id='contenttooltip${id+1}' class='tooltip-invisible'><div class='fup-popup'>${full}</div></div>`;
        const table = document.querySelector('#search-results');
        table.innerHTML = '<thead><tr><th></th><th>ID</th><th>Titre</th><th>Demandeur</th><th>Description</th></tr></thead><tbody id="computer-rows"></tbody>';
        for (let id=101;id<=102;id++) {
          const firstUser=user('Alice Exemple',id*100), secondUser=user('Bob Exemple',id*100+1);
          const first=long('Premier contenu','Premier contenu complet avec détail.',id*100+10), second=long('Deuxième contenu','Deuxième contenu complet avec détail.',id*100+20);
          document.querySelector('#computer-rows').insertAdjacentHTML('beforeend',`<tr><td></td><td>${id}</td><td>P-MA-00${id-100}</td><td>${firstUser} <span id='tooltip${id*100+30}' class='fas fa-comments'></span><div id='contenttooltip${id*100+30}' class='tooltip-invisible'><div class='fup-popup'>${firstUser}<br>${secondUser}<br></div></div></td><td>${first} <span id='tooltip${id*100+40}' class='fas fa-comments'></span><div id='contenttooltip${id*100+40}' class='tooltip-invisible'><div class='fup-popup'>${first}<hr>${second}<hr></div></div></td></tr>`);
        }
        </script></body>
        """, StringComparison.Ordinal);
    const string full = "Premier contenu complet avec détail.\nDeuxième contenu complet avec détail.";
    var csv = "ID;Titre;Demandeur;Description\r\n" + string.Join("\r\n", Enumerable.Range(101, 5).Select(id =>
        $"{id};P-MA-00{id - 100};\"Alice Exemple\nBob Exemple\";\"{(changed && id == 101 ? full.Replace("Deuxième contenu complet avec détail.", "Deuxième contenu complet avec une autre fin.", StringComparison.Ordinal) : full)}\"")) + "\r\n";
    await using var h = await Open(html, csv);
    var logs = new List<string>(); var service = new AutomaticExtractionService(h.Service); service.Log += logs.Add;
    var run = await service.RunAsync(NativeSettings(), null, CancellationToken.None);
    Equal(5, run.Data.Rows.Count); Equal(!changed, run.Data.Complete);
    Equal("Alice Exemple\nBob Exemple", run.Data.Rows[0][2]);
    Equal(changed ? full.Replace("Deuxième contenu complet avec détail.", "Deuxième contenu complet avec une autre fin.", StringComparison.Ordinal) : full, run.Data.Rows[0][3]);
    True(run.Data.Provenance.ScopeVerified && run.Data.Provenance.Method == "native-csv", "Les cellules développées doivent traverser la collecte native et son contrôle final.");
    True(logs.Any(line => line.StartsWith("ÉCHANTILLON ·", StringComparison.Ordinal) && line.Contains(changed ? "1/6 valeurs comparables diffèrent" : "6 valeurs comparables", StringComparison.Ordinal)), "Les textes complets doivent être comparés sans exclusion pour abréviation ni perte des demandeurs suivants.");
    True(!string.Join('\n', logs).Contains("FICHE PRIVEE", StringComparison.Ordinal) && !run.Data.Rows.SelectMany(row => row).Any(value => value.Contains("FICHE PRIVEE", StringComparison.Ordinal)), "Les fiches utilisateur imbriquées ne deviennent jamais des valeurs de ticket ni du diagnostic.");
    Equal(csv, await File.ReadAllTextAsync(run.NativePath!));
}

string WithOfficialGlpiVersion(string html, string version = "10.0.22") => html.Replace("<body>", """
    <body><div class="page"><header class="navbar"><a id="show_about_modal_100" href="#">À propos</a><div class="modal fade" id="about_modal_100" role="dialog" style="display:none"><div class="modal-dialog"><div class="modal-content"><div class="modal-body"><p><a href="http://glpi-project.org/" title="Powered by Teclib and contributors" class="copyright">GLPI VERSION Copyright (C) 2015-2025 Teclib' and contributors</a></p></div></div></div></div></header></div>
    """.Replace("VERSION", System.Net.WebUtility.HtmlEncode(version), StringComparison.Ordinal), StringComparison.Ordinal);

string FormattedHtml(string tail = "fin.") => WithOfficialGlpiVersion(NativeHtml().Replace("</body>", """
    <style>.tooltip-invisible { display:none }</style><script>
    for (const row of document.querySelectorAll('#computer-rows tr')) {
      const id = Number(row.dataset.id) * 10;
      row.children[2].innerHTML = `<span id='text${id}'>Bonjour&nbsp;(...)</span> <span id='tooltip${id+1}' class='fas fa-comments'></span><div id='contenttooltip${id+1}' class='tooltip-invisible'><div class='fup-popup'><p><strong>Bonjour</strong> monde "exemple"</p><p><em>note</em></p><ul><li>Premier</li><li>Second</li></ul><hr><p>TAIL</p></div></div>`;
    }
    </script></body>
    """.Replace("TAIL", System.Net.WebUtility.HtmlEncode(tail), StringComparison.Ordinal), StringComparison.Ordinal));

string FormattedValue(string tail = "fin.") => "BONJOUR monde ''exemple''\n_note_\n* Premier\n* Second\n-------------------------\n" + tail;

string FormattedCsv(string firstTail = "fin.") => "ID;Nom\r\n" + string.Join("\r\n", Enumerable.Range(101, 5).Select(id =>
    id + ";\"" + FormattedValue(id == 101 ? firstTail : "fin.") + "\"")) + "\r\n";

async Task CheckNativeFormattedCells(string variation)
{
    var html = FormattedHtml(variation == "c1" ? "fin’." : "fin.");
    var csv = FormattedCsv(variation == "changed" ? "autre fin." : variation == "c1" ? "fin\u0092." : "fin.");
    if (variation == "c1") csv = csv.Replace("fin.", "fin\u0092.", StringComparison.Ordinal);
    await using var h = await Open(html, csv);
    var logs = new List<string>(); var service = new AutomaticExtractionService(h.Service); service.Log += logs.Add;
    var run = await service.RunAsync(NativeSettings(), null, CancellationToken.None);
    Equal(variation == "equal", run.Data.Complete); Equal(5, run.Data.Rows.Count);
    True(run.Data.Quality is { ReceptionComplete: true, SampleIdentityVerified: true }, "La réception et les ID sont indépendants de la représentation des cellules.");
    Equal(variation == "equal", run.Data.Quality!.RepresentationVerified);
    Equal(variation == "equal" ? 2 : variation == "changed" ? 1 : 0, run.Data.Quality.InitialSample!.EquivalentFormatCells);
    Equal(variation == "equal" ? 0 : variation == "changed" ? 1 : 2, run.Data.Quality.InitialSample.DifferentCells);
    Equal(csv, await File.ReadAllTextAsync(run.NativePath!));
    True(logs.Any(line => line.Contains("format HTML/CSV prouvé=", StringComparison.Ordinal)) || variation == "c1", "La preuve de format est comptée, pas un simple indicateur d’acceptation.");
    True(!string.Join('\n', logs).Contains("BONJOUR", StringComparison.Ordinal) && !string.Join('\n', logs).Contains("exemple''", StringComparison.Ordinal), "Le contenu des représentations ne traverse pas le journal.");
}

async Task CheckNativePlainCase()
{
    var html = NativeHtml().Replace("P-MA-001", "Nom synthétique", StringComparison.Ordinal);
    var csv = NativeCsv().Replace("P-MA-001", "NOM SYNTHÉTIQUE", StringComparison.Ordinal);
    await using var h = await Open(html, csv);
    var run = await new AutomaticExtractionService(h.Service).RunAsync(NativeSettings(), null, CancellationToken.None);
    True(!run.Data.Complete && run.Data.Quality!.InitialSample is { DifferentCells: 1, EquivalentFormatCells: 0 }, "La casse demeure significative lorsqu’aucune balise de la cellule ne justifie sa présentation CSV.");
}

async Task CheckNativeUnverifiedVersion()
{
    foreach (var version in new string?[] { null, "9.5.13", "10.0.0", "10.0.21", "11.0.0" })
    {
        var html = NativeHtml().Replace("</body>", """
            <script>for (const row of document.querySelectorAll('#computer-rows tr')) row.children[2].innerHTML = '<strong>Bonjour</strong>';</script></body>
            """, StringComparison.Ordinal);
        if (version is not null) html = WithOfficialGlpiVersion(html, version);
        var csv = "ID;Nom\r\n" + string.Join("\r\n", Enumerable.Range(101, 5).Select(id => id + ";BONJOUR")) + "\r\n";
        await using var h = await Open(html, csv);
        var sample = await h.Service.ReadVerificationSampleAsync(await Table(h), CancellationToken.None);
        Equal(0, sample.CsvRepresentations.Count);
        var run = await new AutomaticExtractionService(h.Service).RunAsync(NativeSettings(), null, CancellationToken.None);
        True(!run.Data.Complete && run.Data.Quality is { ReceptionComplete: true, SampleIdentityVerified: true, RepresentationVerified: false }, "Sans preuve de version 10.0.22, une présentation riche ne certifie pas un changement de casse du CSV.");
        True(run.Data.Quality!.InitialSample is { DifferentCells: 2, EquivalentFormatCells: 0 }, "La version absente ou non vérifiée conserve les deux différences de valeurs.");
        Equal(csv, await File.ReadAllTextAsync(run.NativePath!));
        var unchanged = new ExtractionResult { Headers = new() { "ID", "Nom" }, Rows = new() { new[] { "101", "Bonjour" }, new[] { "102", "Bonjour" } } };
        True(ExtractionVerificationService.VerifyNativeSample(sample, unchanged, new()).Matches, "Le rapprochement exact ordinaire reste disponible sans projection de présentation.");
    }
}

async Task CheckRepresentationBinding()
{
    await using var h = await Open(FormattedHtml());
    var visible = await h.Service.ReadVerificationSampleAsync(await Table(h), CancellationToken.None);
    var downloaded = new ExtractionResult { Headers = new() { "ID", "Nom" }, Rows = new() { new[] { "101", FormattedValue() }, new[] { "102", FormattedValue() } } };
    var proof = visible.CsvRepresentations[0][2]!;
    True(proof is { Kind: "glpi10-rich-text-v1", Row: 0, Column: 2 } && proof.Source == visible.Rows[0][2], "La preuve réelle doit venir de la même cellule et conserver son texte exact.");
    NativeSampleVerification Verify() => ExtractionVerificationService.VerifyNativeSample(visible, downloaded, new());
    True(Verify().Matches, "La représentation produite par le navigateur doit expliquer les deux cellules.");
    foreach (var invalid in new[]
    {
        new CsvCellRepresentation { Kind = proof.Kind, Row = 1, Column = 2, Source = proof.Source, Text = proof.Text },
        new CsvCellRepresentation { Kind = proof.Kind, Row = 0, Column = 3, Source = proof.Source, Text = proof.Text },
        new CsvCellRepresentation { Kind = proof.Kind, Row = 0, Column = 2, Source = "Autre cellule", Text = proof.Text },
        new CsvCellRepresentation { Kind = "declared-compatible", Row = 0, Column = 2, Source = proof.Source, Text = proof.Text },
        new CsvCellRepresentation { Kind = proof.Kind, Row = 0, Column = 2, Source = proof.Source, Text = new string('x', 262145) },
        new CsvCellRepresentation { Kind = proof.Kind, Row = 0, Column = 2, Source = proof.Source, Text = proof.Text + "\u0085" }
    })
    {
        visible.CsvRepresentations[0][2] = invalid;
        True(!Verify().Matches, "Une projection étrangère, non reconnue, surdimensionnée ou contenant un contrôle C1 ne peut masquer une différence.");
    }
    visible.CsvRepresentations[0][2] = proof;
    True(Verify().Matches, "Le contrôle négatif ne modifie ni la preuve d’origine ni les données.");
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
