using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Playwright;

var browserPath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
if (string.IsNullOrWhiteSpace(browserPath)) { Console.Error.WriteLine("Définir GLPI_TEST_CHROMIUM vers un Chromium de test installé."); return 2; }
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { ExecutablePath = browserPath, Headless = true, Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
string legacy = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "ticket-legacy.html"));
string modern = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "ticket-modern.html"));
string treatment = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "ticket-treatment.html"));
const string listUrl = "http://glpi-ticket.test/glpi/front/ticket.php?criteria%5B0%5D%5Bvalue%5D=filtre%20conserv%C3%A9";
const string detailUrl = "http://glpi-ticket.test/glpi/front/ticket.form.php?id=42";
const string listHtml = "<!doctype html><title>Recherche conservée</title><a href='/glpi/front/logout.php'>Déconnexion</a><table id='search-results' class='search-results'><thead><tr><th>ID</th><th>Titre</th></tr></thead><tbody><tr><td>42</td><td>Recherche inchangée</td></tr></tbody></table>";
const string historyHtml = "<h2>Historique</h2><table id='loaded-history'><thead><tr><th>Date</th><th>Changement</th></tr></thead><tbody><tr><td>21/10/2026</td><td>Historique lu par onglet</td></tr><tr><td>22/10/2026</td><td>Second événement</td></tr></tbody></table>";
const string treatmentHtml = """
  <div class="timeline_form"><button data-business="add">Ajouter suivi</button><button data-business="solve">Solution</button></div>
  <div class="timeline_history" id="treatment-timeline">
   <div class="h_item"><div class="h_info"><div class="h_date">21-10-2026 12:30</div><div class="h_user"><span class="h_user_name">Auteur fictif A</span></div></div><div class="h_content ITILFollowup"><div class="displayed_content"><div class="h_controls"><button data-business="edit">Modifier</button></div><div class="item_content long_text"><div class="rich_text_container"><p>Premier suivi synthétique.</p><p>Suite visible en entier malgré le bloc replié.</p></div><p class="read_more"><a data-business="more">.....</a></p></div><div class="b_right">Téléphone</div></div></div></div>
   <div class="h_item"><div class="h_info"><div class="h_date">21-10-2026 11:00</div><span class="h_user_name">Auteur fictif B</span></div><div class="h_content ITILTask"><div class="displayed_content"><div class="h_controls"><span class="private">Privé</span></div><div class="item_content"><span class="state state_1" title="À faire" data-business="state">☐</span><div class="rich_text_container">Tâche de test non modifiée.</div></div><div class="b_right"><span class="actiontime">1 heure</span></div></div></div></div>
   <div class="h_item"><div class="h_date">21-10-2026 10:00</div><span class="h_user_name">Auteur fictif C</span><div class="h_content Solution accepted"><div class="displayed_content"><div class="rich_text_container">Solution synthétique.</div><div class="b_right">Acceptée</div></div></div></div>
   <div class="h_item"><div class="h_date">21-10-2026 09:00</div><div class="h_content Document_Item"><div class="displayed_content"><a href="/glpi/front/document.send.php?docid=3">document-test.pdf</a><span class="buttons"><form><input type="hidden" name="token" value="NEVER_DOC_TOKEN"><button data-business="delete">Supprimer</button></form></span></div></div></div>
   <div class="h_item"><div class="h_date">21-10-2026 08:00</div><div class="h_content TicketValidation"><div class="displayed_content"><div class="rich_text_container">Validation demandée.</div><div class="b_right">En attente</div></div></div></div>
   <div class="h_item" hidden><div class="h_content ITILFollowup">NEVER_HIDDEN_EVENT</div></div>
   <div class="h_item middle"><div class="h_date">20-10-2026 10:00</div><div class="h_content ITILContent"><div class="displayed_content"><div class="rich_text_container">Description initiale synthétique.</div></div></div></div>
  </div>
  """;
var checks = new List<(string Name, Func<Task> Run)>
{
    ("Legacy : champs visibles, choix sélectionnés, multiline et exclusions sensibles", LegacyFields),
    ("GLPI moderne : labels, champs statiques, cases et radios sans options parasites", ModernFields),
    ("Tableaux : tfoot exclu, tables imbriquées distinctes, colonne sensible retirée", NestedTables),
    ("Sections configurées : texte, nom, absence, ambiguïté et limite signalés", ManualSections),
    ("Onglet GLPI legacy : clic de consultation POST existant et lecture du tableau", () => RemoteTab(false)),
    ("Onglet GLPI moderne : clic de consultation GET existant et lecture du tableau", () => RemoteTab(true)),
    ("Onglet AJAX lent : ancien tableau ignoré pendant aria-busy puis nouveau contenu", SlowTab),
    ("Onglet local reconnu : panneau lu après clic explicite configuré", LocalTab),
    ("Actions métier, origine étrangère, autre ID et onclick : aucun clic", UnsafeTabs),
    ("Identité : ID DOM différent et redirection vers un autre ticket refusés", WrongIdentity),
    ("Référence : origine, paramètres métier et chemin non GLPI refusés avant navigation", UnsafeReference),
    ("Recherche : URL, DOM et onglet actif conservés ; page de détail réutilisée", SearchUnchanged),
    ("Détail exclu de Session et du choix automatique même après fermeture recherche", SearchSelection),
    ("Annulation : navigation arrêtée, recherche intacte puis nouvelle sélection isolée", Cancellation),
    ("SSO : redirection naturelle puis retour à la fiche demandée sans saisie", NaturalSso),
    ("SSO popup : seule la descendance de la fiche est suivie, recherche conservée", PopupSso),
    ("Cadres : iframe visible même origine lue, cadres masqué et étranger ignorés", Frames),
    ("Diagnostic : aucun titre, ID, contenu ticket, query ou secret dans les journaux", PrivateLogs),
    ("Lecture : aucun changement de contrôle, soumission ni demande de focus automatique", ReadOnly),
    ("Éditeur riche : description TinyMCE associée visible et upload exclu", RichDescription),
    ("Traitement : onglet automatique, suivis tâches solutions documents et validation", AutomaticTreatment),
    ("Menu : seuls les onglets sûrs sont découverts et lecture différée isolée", LazyTabs),
    ("Traitement : préférence désactivée et limite de lignes explicitement signalée", TreatmentOptions),
    ("Menu : annulation d’un onglet AJAX conserve la recherche", LazyTabCancellation),
    ("Menu : clé native stable après réordonnancement des sélecteurs", StableTabIdentity),
    ("Menu : panneau absent ou inactif ne reprend jamais les champs précédents", StrictTabScope),
    ("Menu : autre installation GLPI et cible secondaire ambiguë refusées", SameInstallation),
    ("Observations : échéances rôles attentes relations et documents explicites", StructuredObservations),
    ("Actions : aperçu sans mutation puis une seule soumission vérifiée", AssistedPreviewAndOneWrite),
    ("Actions : installation identité structure et aperçu périmé refusés", AssistedRejectsUnsafeAndStale),
    ("Actions : résultat ambigu sans aucune relance automatique", AssistedAmbiguousNeverRetries),
    ("Documents : téléchargement explicite authentifié et refus du HTML SSO", AuthenticatedDocumentDownload)
};
var filter = Environment.GetEnvironmentVariable("GLPI_TEST_FILTER");
if (!string.IsNullOrWhiteSpace(filter)) checks = checks.Where(check => check.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
int failed = 0;
foreach (var check in checks)
{
    try { await check.Run().WaitAsync(TimeSpan.FromSeconds(35)); Console.WriteLine("PASS " + check.Name); }
    catch (Exception exception) { failed++; Console.WriteLine("FAIL " + check.Name + "\n" + exception); }
}
Console.WriteLine($"{checks.Count - failed}/{checks.Count} contrôles de lecture de fiche réussis.");
return failed == 0 ? 0 : 1;

async Task<FixtureSession> Setup(string html, Func<IRoute, Uri, Task<bool>>? custom = null)
{
    var context = await browser.NewContextAsync();
    var requests = new ConcurrentQueue<(string Path, string Method)>();
    await context.RouteAsync("**/*", async route =>
    {
        var uri = new Uri(route.Request.Url);
        requests.Enqueue((uri.AbsolutePath, route.Request.Method));
        if (custom != null && await custom(route, uri)) return;
        if (uri.Host != "glpi-ticket.test") { await route.AbortAsync(); return; }
        if (uri.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal)) await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = listHtml });
        else if (uri.AbsolutePath.EndsWith("/front/ticket.form.php", StringComparison.Ordinal))
        {
            var id = TicketReferenceResolver.ParseQuery(uri).FirstOrDefault(pair => pair.Key == "id").Value;
            await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = id == "43" ? html.Replace("42", "43", StringComparison.Ordinal) : html });
        }
        else if (uri.AbsolutePath.EndsWith("/ajax/common.tabs.php", StringComparison.Ordinal)) await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = historyHtml });
        else await route.AbortAsync();
    });
    var search = await context.NewPageAsync();
    await search.GotoAsync(listUrl);
    var settings = new AppSettings { PortalUrl = listUrl, TimeoutSeconds = 7, DetailTimeoutSeconds = 5, TicketDetails = new() };
    var service = new BrowserService();
    service.AttachForTesting(context, listUrl);
    var log = new ConcurrentQueue<string>(); service.Log += log.Enqueue;
    await service.FindBestPageAsync(settings, search, CancellationToken.None);
    log.Clear();
    return new(context, search, service, settings, log, requests);
}

async Task<TicketDetailResult> Read(FixtureSession session, string id = "42", CancellationToken ct = default) =>
    await session.Service.ReadTicketDetailsAsync(new(id, detailUrl.Replace("id=42", "id=" + id, StringComparison.Ordinal)), session.Settings, null, ct);
static TicketDetailSection Section(TicketDetailResult result, string selector) => result.Sections.FirstOrDefault(section => section.Selector == selector)
    ?? throw new InvalidOperationException("Section absente " + selector + " ; sélecteurs=" + string.Join(", ", result.Sections.Select(section => section.Selector)) + " ; avertissements=" + string.Join(" | ", result.Warnings));
static string Value(TicketDetailSection section, string label) => section.Data.Rows.Single(row => row[0] == label)[1];
static string All(TicketDetailResult result) => JsonSerializer.Serialize(result);
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task Reject(Func<Task> action, string contains)
{
    try { await action(); throw new InvalidOperationException("Le refus attendu n’a pas eu lieu."); }
    catch (InvalidOperationException error) when (error.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { }
}

async Task<FixtureSession> SetupTreatment(Func<IRoute, Uri, Task<bool>>? extra = null) => await Setup(treatment, async (route, uri) =>
{
    if (extra != null && await extra(route, uri)) return true;
    if (uri.AbsolutePath.EndsWith("common.tabs.php", StringComparison.Ordinal) && uri.Query.Contains("Ticket%241", StringComparison.Ordinal))
    { await route.FulfillAsync(new() { ContentType = "text/html", Body = treatmentHtml }); return true; }
    return false;
});

async Task RichDescription()
{
    await using var session = await SetupTreatment();
    session.Settings.TicketDetails.AutoReadTreatment = false;
    var result = await Read(session);
    var fields = Section(result, "#tabsbody");
    var description = Value(fields, "Description *");
    Require(description.Contains("Description enrichie de test.", StringComparison.Ordinal) && description.Contains("Deuxième paragraphe & texte complet.", StringComparison.Ordinal), "Description riche perdue ou mal décodée.");
    Require(!All(result).Contains("VALEUR_SOURCE_ANCIENNE", StringComparison.Ordinal) && !All(result).Contains("NEVER_", StringComparison.Ordinal) && !All(result).Contains("UI_NOISE", StringComparison.Ordinal), "Éditeur/contrôle caché ou widget upload extrait.");
    Require(!fields.Data.Rows.Any(row => row[0].StartsWith("Fichier", StringComparison.Ordinal)), "Ligne du dépôt de fichier incluse comme champ métier.");
    var page = session.Service.TicketPage!;
    Require(await page.Locator("#description").InputValueAsync() == "VALEUR_SOURCE_ANCIENNE", "Le lecteur a enregistré ou modifié l’éditeur.");
}

async Task AutomaticTreatment()
{
    await using var session = await SetupTreatment();
    var result = await Read(session); var section = Section(result, "#treatment-timeline");
    Require(section.Kind == "timeline" && section.Data.Rows.Count == 6, "Traitement non lu en totalité visible.");
    Require(section.Data.Rows.Select(row => row[0]).SequenceEqual(new[] { "Suivi", "Tâche", "Solution", "Document", "Validation", "Description initiale" }), "Types de traitement incorrects.");
    Require(section.Data.Rows[0][1] == "21-10-2026 12:30" && section.Data.Rows[0][2] == "Auteur fictif A" && section.Data.Rows[0][3].Contains("malgré le bloc replié", StringComparison.Ordinal), "Date/auteur/contenu long manquant.");
    Require(section.Data.Rows[1][4].Contains("À faire", StringComparison.Ordinal) && section.Data.Rows[1][4].Contains("Privé", StringComparison.Ordinal), "Métadonnées tâche absentes.");
    Require(section.Data.Rows[3][3].Contains("document-test.pdf", StringComparison.Ordinal), "Nom de document absent.");
    Require(section.Data.Warnings.Any(warning => warning.Contains("masqués", StringComparison.Ordinal)), "Événements masqués non signalés.");
    Require(!All(result).Contains("NEVER_", StringComparison.Ordinal) && !All(result).Contains("Ajouter suivi", StringComparison.Ordinal), "Contenu caché ou commande métier capturé.");
    Require(await session.Service.TicketPage!.EvaluateAsync<int>("window.businessClicks+window.formSubmissions") == 0, "Traitement modifié.");
    Require(result.Tabs.Single(tab => tab.Category == "treatment").Loaded, "État du menu de traitement incorrect.");
}

async Task LazyTabs()
{
    await using var session = await SetupTreatment();
    var before = await session.Search.ContentAsync(); var result = await Read(session);
    Require(result.Tabs.Count == 3 && !result.Tabs.Any(tab => tab.Name.Contains("Supprimer", StringComparison.Ordinal)), "Onglet métier découvert.");
    Require(session.Requests.Count(request => request.Path.EndsWith("common.tabs.php", StringComparison.Ordinal)) == 1, "Onglets secondaires chargés sans demande.");
    var page = session.Service.TicketPage;
    var history = await session.Service.ReadTicketTabAsync(result.Ticket, session.Settings, result.Tabs.Single(tab => tab.Selector == "#history-tab"), null, CancellationToken.None);
    Require(history.Sections.Any(section => section.Selector == "#loaded-history" && section.Data.Rows.Count == 2), "Historique différé non lu.");
    Require(history.Tabs.Single(tab => tab.Selector == "#history-tab").Loaded, "État onglet différé incorrect.");
    Require(ReferenceEquals(page, session.Service.TicketPage) && ReferenceEquals(session.Search, session.Service.ActivePage) && await session.Search.ContentAsync() == before, "Lecture différée modifie la recherche ou multiplie les pages.");
    await Reject(async () => await session.Service.ReadTicketTabAsync(result.Ticket, session.Settings, new TicketDetailTab { Key = "local:#delete", Name = "Supprimer", Selector = "#delete-tab" }, null, CancellationToken.None), "disponible");
    Require(await session.Service.TicketPage!.EvaluateAsync<int>("window.businessClicks+window.formSubmissions") == 0, "Action métier déclenchée par le menu.");
}

async Task TreatmentOptions()
{
    await using (var session = await SetupTreatment())
    {
        session.Settings.TicketDetails.AutoReadTreatment = false;
        var result = await Read(session);
        Require(!result.Sections.Any(section => section.Kind == "timeline") && !session.Requests.Any(request => request.Path.EndsWith("common.tabs.php", StringComparison.Ordinal)), "Traitement lu malgré préférence désactivée.");
    }
    await using (var session = await SetupTreatment())
    {
        session.Settings.TicketDetails.MaxRowsPerSection = 2;
        var result = await Read(session); var data = Section(result, "#treatment-timeline").Data;
        Require(data.Rows.Count == 2 && data.ExpectedTotal == 6 && !data.Complete && data.Warnings.Any(warning => warning.Contains("limite", StringComparison.Ordinal)), "Traitement tronqué silencieusement.");
    }
}

async Task LazyTabCancellation()
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var session = await SetupTreatment(async (route, uri) =>
    {
        if (!uri.AbsolutePath.EndsWith("common.tabs.php", StringComparison.Ordinal) || !uri.Query.Contains("Log%241", StringComparison.Ordinal)) return false;
        started.TrySetResult(); await release.Task;
        try { await route.FulfillAsync(new() { ContentType = "text/html", Body = historyHtml }); } catch (PlaywrightException) { }
        return true;
    });
    using var cancellation = new CancellationTokenSource();
    var initial = await Read(session);
    var pending = session.Service.ReadTicketTabAsync(new("42", detailUrl), session.Settings, initial.Tabs.Single(tab => tab.Selector == "#history-tab"), null, cancellation.Token);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
    try { await pending; throw new InvalidOperationException("Annulation ignorée."); } catch (OperationCanceledException) { }
    release.TrySetResult();
    Require(!session.Search.IsClosed && session.Search.Url == listUrl, "Recherche fermée par annulation du menu.");
}


async Task StableTabIdentity()
{
    var visits = 0;
    await using var session = await SetupTreatment(async (route, uri) =>
    {
        if (!uri.AbsolutePath.EndsWith("ticket.form.php", StringComparison.Ordinal)) return false;
        if (Interlocked.Increment(ref visits) < 2) return false;
        var changed = treatment.Replace("history-tab", "history-fresh", StringComparison.Ordinal).Replace("treatment-tab", "history-tab", StringComparison.Ordinal);
        await route.FulfillAsync(new() { ContentType = "text/html", Body = changed }); return true;
    });
    var first = await Read(session); var originalTab = first.Tabs.Single(tab => tab.Name == "Historique");
    var second = await session.Service.ReadTicketTabAsync(first.Ticket, session.Settings, originalTab, null, CancellationToken.None);
    Require(second.Sections.Any(section => section.Selector == "#loaded-history") && !second.Sections.Any(section => section.Kind == "timeline"), "Le sélecteur réutilisé a lu un autre onglet.");
    Require(second.Tabs.Single(tab => tab.Key == originalTab.Key).Selector == "#history-fresh", "La clé native n’a pas retrouvé le nouveau repère.");
    originalTab.Name = "Autre libellé";
    await Reject(async () => await session.Service.ReadTicketTabAsync(first.Ticket, session.Settings, originalTab, null, CancellationToken.None), "disponible");
}

async Task StrictTabScope()
{
    var broken = treatment.Replace("panel.hidden=false;panel.setAttribute('aria-busy','true');", "panel.hidden=true;panel.setAttribute('aria-busy','true');", StringComparison.Ordinal);
    await using var session = await Setup(broken, async (route, uri) =>
    {
        if (!uri.AbsolutePath.EndsWith("common.tabs.php", StringComparison.Ordinal)) return false;
        await route.FulfillAsync(new() { ContentType = "text/html", Body = treatmentHtml }); return true;
    });
    session.Settings.TicketDetails.AutoReadTreatment = false;
    var first = await Read(session);
    var selected = first.Tabs.Single(tab => tab.Category == "treatment");
    var second = await session.Service.ReadTicketTabAsync(first.Ticket, session.Settings, selected, null, CancellationToken.None);
    Require(second.Sections.Count == 0 && !second.Tabs.Single(tab => tab.Key == selected.Key).Loaded, "Ancien formulaire lu comme contenu d’un panneau invisible.");
    Require(second.Warnings.Any(warning => warning.Contains("délai", StringComparison.Ordinal)), "Panneau invisible non signalé.");
}

async Task SameInstallation()
{
    var bad = treatment.Replace("_glpi_tab=Ticket%241", "_glpi_tab=Ticket%241", StringComparison.Ordinal)
        .Replace("/glpi/ajax/common.tabs.php?_itemtype=Ticket&amp;_glpi_tab=Ticket", "/other/ajax/common.tabs.php?_itemtype=Ticket&amp;_glpi_tab=Ticket", StringComparison.Ordinal)
        .Replace("_glpi_tab=Log%241&amp;id=42", "_glpi_tab=Log%241&amp;id=42&amp;_target=%2Fglpi%2Ffront%2Fticket.form.php%3Fid%3D42%26id%3D99", StringComparison.Ordinal);
    await using var session = await Setup(bad);
    var result = await Read(session);
    Require(result.Tabs.Count == 1 && !session.Requests.Any(request => request.Path.EndsWith("common.tabs.php", StringComparison.Ordinal)), "Onglet d’une autre installation ou cible ambiguë accepté.");
}

async Task LegacyFields()
{
    await using var session = await Setup(legacy);
    var result = await Read(session);
    var fields = Section(result, "#tabsbody");
    Require(Value(fields, "Titre") == "Ticket privé de fixture", "Titre legacy perdu.");
    Require(Value(fields, "Statut") == "En cours (attribué)", "Select contient des options non choisies.");
    Require(Value(fields, "Urgent") == "Oui" && Value(fields, "Archivé") == "Non", "Cases incorrectes.");
    Require(Value(fields, "Technicien") == "Équipe choisie", "Select2 illisible ou dupliqué.");
    Require(Value(fields, "Description") == "Première ligne privée\nDeuxième ligne & caractères <intacts>", "Multiligne altéré.");
    Require(!All(result).Contains("NEVER_", StringComparison.Ordinal) && !All(result).Contains("NON CHOISI", StringComparison.Ordinal), "Secret ou option cachée extrait.");
    Require(result.Warnings.Any(warning => warning.Contains("autres onglets", StringComparison.Ordinal)), "Périmètre global trop affirmatif.");
}

async Task ModernFields()
{
    await using var session = await Setup(modern);
    var result = await Read(session); var fields = Section(result, "#itil-form");
    Require(Value(fields, "Statut") == "En attente" && Value(fields, "Privé") == "Oui", "Contrôles modernes incorrects.");
    Require(Value(fields, "Demandeur") == "Personne de test", "Valeur statique omise.");
    Require(Value(fields, "Mode") == "Option choisie", "Radio sélectionné ou non sélectionné mal lu.");
    Require(!All(result).Contains("NEVER_", StringComparison.Ordinal) && !All(result).Contains("NON CHOISI", StringComparison.Ordinal), "Donnée sensible/options parasites.");
    Require(result.Title == "Ticket moderne privé", "Titre de fiche incorrect.");
}

async Task NestedTables()
{
    await using var session = await Setup(legacy); var result = await Read(session);
    var outer = Section(result, "#followups"); var inner = Section(result, "#nested-data");
    Require(outer.Data.Headers.SequenceEqual(new[] { "Date", "Description" }), "Colonne sensible pas exclue.");
    Require(outer.Data.Rows.Count == 2 && inner.Data.Rows.Count == 1, "Nested/tfoot dupliqué.");
    Require(!outer.Data.Rows.Any(row => string.Join(" ", row).Contains("Imbriqué", StringComparison.Ordinal)), "Texte nested dans parent.");
    Require(outer.Data.Rows[0][1] == "Suivi un\nligne deux", "Multiligne tableau incorrect.");
}

async Task ManualSections()
{
    await using var session = await Setup(legacy.Replace("</body>", "<p class='duplicated'>A</p><p class='duplicated'>B</p></body>", StringComparison.Ordinal));
    session.Settings.TicketDetails = new() { IncludeAutoDetected = false, MaxRowsPerSection = 1, Sections = new()
    {
        new() { Name = "Mes notes", Selector = "#notes", Kind = "text" },
        new() { Name = "Historique plafonné", Selector = "#followups", Kind = "table" },
        new() { Name = "Manquant", Selector = "#absent", Kind = "table" },
        new() { Name = "Ambigu", Selector = ".duplicated", Kind = "text" },
        new() { Name = "Invalide", Selector = "[", Kind = "text" }
    }};
    var result = await Read(session);
    Require(result.Sections.Count == 2 && Section(result, "#notes").Name == "Mes notes", "Configuration non respectée.");
    var limited = Section(result, "#followups").Data;
    Require(limited.Rows.Count == 1 && limited.ExpectedTotal == 2 && !limited.Complete && limited.Warnings.Count > 0, "Limite silencieuse.");
    Require(result.Warnings.Any(w => w.Contains("Manquant", StringComparison.Ordinal)) && result.Warnings.Any(w => w.Contains("Ambigu", StringComparison.Ordinal)) && result.Warnings.Any(w => w.Contains("Invalide", StringComparison.Ordinal)), "Absence/ambiguïté non signalée.");
}

async Task RemoteTab(bool isModern)
{
    await using var session = await Setup(isModern ? modern : legacy);
    session.Settings.TicketDetails = new() { IncludeAutoDetected = false, Sections = new() { new() { Name = "Historique configuré", Selector = "#loaded-history", Kind = "table", TabSelector = isModern ? "#modern-history-tab" : "#legacy-history-tab" } } };
    var result = await Read(session);
    Require(result.Sections.Count == 1 && result.Sections[0].Data.Rows.Count == 2, "Onglet non lu.");
    Require(session.Requests.Count(request => request.Path.EndsWith("common.tabs.php", StringComparison.Ordinal) && request.Method == (isModern ? "GET" : "POST")) == 1, "Chargement de consultation incorrect.");
    Require(await session.Service.TicketPage!.EvaluateAsync<int>("window.tabClicks") == 1 && await session.Service.TicketPage!.EvaluateAsync<int>("window.formSubmissions") == 0, "Clic doublé ou formulaire envoyé.");
}

async Task LocalTab()
{
    await using var session = await Setup(modern);
    session.Settings.TicketDetails = new() { IncludeAutoDetected = false, Sections = new() { new() { Name = "Notes", Selector = "#local-notes", Kind = "text", TabSelector = "#local-notes-tab" } } };
    var result = await Read(session);
    Require(result.Sections.Count == 1 && result.Sections[0].Data.Rows[0][0].Contains("Deuxième ligne locale", StringComparison.Ordinal), "Panneau local non lu.");
    Require(await session.Service.TicketPage!.EvaluateAsync<int>("window.tabClicks") == 1, "Clic local incorrect.");
}

async Task SlowTab()
{
    var html = modern.Replace("<section id=\"history-panel\" class=\"tab-pane\" role=\"tabpanel\" hidden></section>",
        "<section id=\"history-panel\" class=\"tab-pane\" role=\"tabpanel\" hidden><table id='loaded-history'><thead><tr><th>Date</th><th>Changement</th></tr></thead><tbody><tr><td>Ancien</td><td>STALE_TAB_SENTINEL</td></tr></tbody></table></section>", StringComparison.Ordinal);
    await using var session = await Setup(html, async (route, uri) =>
    {
        if (!uri.AbsolutePath.EndsWith("common.tabs.php", StringComparison.Ordinal)) return false;
        await Task.Delay(800);
        await route.FulfillAsync(new() { ContentType = "text/html", Body = historyHtml });
        return true;
    });
    session.Settings.TicketDetails = new() { IncludeAutoDetected = false, Sections = new() { new() { Name = "Historique", Selector = "#loaded-history", Kind = "table", TabSelector = "#modern-history-tab" } } };
    var result = await Read(session);
    Require(result.Sections.Count == 1 && result.Sections[0].Data.Rows.Count == 2 && !All(result).Contains("STALE_TAB_SENTINEL", StringComparison.Ordinal), "Ancien contenu AJAX retenu.");
}

async Task UnsafeTabs()
{
    foreach (var sample in new[] { (legacy, new[] { "#bad-submit-tab", "#bad-action-tab", "#bad-cross-tab" }), (modern, new[] { "#inline-business-tab", "#wrong-ticket-tab" }) })
    {
        await using var session = await Setup(sample.Item1);
        session.Settings.TicketDetails = new() { IncludeAutoDetected = false, Sections = sample.Item2.Select(selector => new TicketSectionDefinition { Name = selector, Selector = "#loaded-history", Kind = "table", TabSelector = selector }).ToList() };
        var result = await Read(session);
        Require(result.Sections.Count == 0 && result.Warnings.Count >= sample.Item2.Length + 1, "Action dangereuse acceptée/silencieuse.");
        Require(await session.Service.TicketPage!.EvaluateAsync<int>("window.businessClicks + window.formSubmissions") == 0, "Action métier déclenchée.");
        Require(!session.Requests.Any(request => request.Path.EndsWith("common.tabs.php", StringComparison.Ordinal)), "Requête de mauvais onglet envoyée.");
    }
}

async Task WrongIdentity()
{
    await using (var session = await Setup(modern.Replace("name=\"id\" type=\"hidden\" value=\"42\"", "name=\"id\" type=\"hidden\" value=\"99\"", StringComparison.Ordinal)))
        await Reject(async () => await Read(session), "identifiant");
    var interceptedIds = new ConcurrentQueue<string>();
    await using (var session = await Setup(modern, async (route, uri) =>
    {
        if (!uri.AbsolutePath.EndsWith("ticket.form.php", StringComparison.Ordinal)) return false;
        var id = TicketReferenceResolver.ParseQuery(uri).Single(pair => pair.Key == "id").Value;
        interceptedIds.Enqueue(id);
        // Playwright does not route an intercepted HTTP redirect's destination again.
        // A client navigation keeps both fixture requests intercepted, even offline.
        if (id == "42")
            await route.FulfillAsync(new() { ContentType = "text/html", Body = "<!doctype html><script>location.replace('/glpi/front/ticket.form.php?id=99')</script>" });
        else if (id == "99")
            await route.FulfillAsync(new() { ContentType = "text/html", Body = modern.Replace("42", "99", StringComparison.Ordinal) });
        else return false;
        return true;
    }))
    {
        await session.Context.SetOfflineAsync(true);
        var searchBefore = await session.Search.ContentAsync();
        await Reject(async () => await Read(session), "autre fiche");
        Require(interceptedIds.SequenceEqual(new[] { "42", "99" }), "La destination de redirection n’a pas été servie par les fixtures.");
        var detail = session.Service.TicketPage;
        Require(detail is { IsClosed: false } && detail.Url == detailUrl.Replace("id=42", "id=99", StringComparison.Ordinal), "La redirection n’a pas atteint la fiche attendue.");
        Require(await detail!.Locator("input[name='id']").InputValueAsync() == "99", "La fiche cible synthétique n’a pas été chargée.");
        Require(session.Search.Url == listUrl && await session.Search.ContentAsync() == searchBefore && ReferenceEquals(session.Service.ActivePage, session.Search), "Le refus d’identité a modifié la recherche.");
    }
}

async Task UnsafeReference()
{
    await using var session = await Setup(modern);
    var before = session.Context.Pages.Count;
    foreach (var url in new[] { "https://other.test/glpi/front/ticket.form.php?id=42", detailUrl + "&purge=1", "http://glpi-ticket.test/glpi/ajax/delete.php?id=42" })
    {
        bool rejected = false;
        try { await session.Service.ReadTicketDetailsAsync(new("42", url), session.Settings, null, CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "Référence non sûre acceptée.");
    }
    Require(session.Context.Pages.Count == before, "Page créée avant validation référence.");
}

async Task SearchUnchanged()
{
    await using var session = await Setup(modern);
    var before = await session.Search.ContentAsync(); var initialRequests = session.Requests.Count(request => request.Path.EndsWith("ticket.php", StringComparison.Ordinal));
    await Read(session); var detail = session.Service.TicketPage;
    var second = await Read(session, "43");
    Require(ReferenceEquals(detail, session.Service.TicketPage) && second.Ticket.Id == "43", "Page dédiée non réutilisée.");
    Require(session.Search.Url == listUrl && await session.Search.ContentAsync() == before, "Recherche modifiée.");
    Require(ReferenceEquals(session.Service.ActivePage, session.Search), "Onglet actif passé à la fiche.");
    Require(session.Requests.Count(request => request.Path.EndsWith("ticket.php", StringComparison.Ordinal)) == initialRequests, "Recherche rechargée.");
    Require(session.Context.Pages.Count == 2, "Onglet ajouté à chaque sélection.");
}

async Task SearchSelection()
{
    await using var session = await Setup(modern); await Read(session);
    var detail = session.Service.TicketPage!;
    Require(!((await session.Service.GetPagesAsync()).Any(choice => ReferenceEquals(choice.Page, detail))), "Fiche proposée comme recherche.");
    await session.Service.SessionAsync(detail, session.Settings);
    Require(ReferenceEquals(session.Service.ActivePage, session.Search), "Session choisit fiche.");
    Require(ReferenceEquals(await session.Service.FindBestPageAsync(session.Settings, detail, CancellationToken.None), session.Search), "FindBest choisit fiche.");
    await session.Search.CloseAsync();
    Require(await session.Service.FindBestPageAsync(session.Settings, detail, CancellationToken.None) == null, "Fiche reprend recherche fermée.");
}

async Task Cancellation()
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var session = await Setup(modern, async (route, uri) =>
    {
        if (!uri.AbsolutePath.EndsWith("ticket.form.php", StringComparison.Ordinal) || uri.Query != "?id=42") return false;
        started.TrySetResult(); await release.Task;
        try { await route.FulfillAsync(new() { ContentType = "text/html", Body = modern }); } catch (PlaywrightException) { }
        return true;
    });
    using var cancellation = new CancellationTokenSource();
    var pending = Read(session, ct: cancellation.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var timer = Stopwatch.StartNew(); cancellation.Cancel();
    try { await pending; throw new InvalidOperationException("Annulation non respectée."); } catch (OperationCanceledException) { }
    Require(timer.Elapsed.TotalSeconds < 4 && !session.Search.IsClosed && session.Search.Url == listUrl, "Annulation lente ou recherche fermée.");
    release.TrySetResult();
    var next = await Read(session, "43");
    Require(next.Ticket.Id == "43" && session.Service.TicketPage!.Url.EndsWith("id=43", StringComparison.Ordinal), "Ancienne navigation écrase nouvelle sélection.");
}

async Task NaturalSso()
{
    int attempts = 0;
    await using var session = await Setup(modern, async (route, uri) =>
    {
        if (uri.AbsolutePath.EndsWith("ticket.form.php", StringComparison.Ordinal) && Interlocked.Increment(ref attempts) == 1)
        { await route.FulfillAsync(new() { ContentType = "text/html", Body = "<title>Redirection SSO</title><script>location.href='http://idp-ticket.test/login'</script>" }); return true; }
        if (uri.Host == "idp-ticket.test")
        { await route.FulfillAsync(new() { ContentType = "text/html", Body = "<title>SSO</title><input type=password value='NEVER_SSO_831'><script>setTimeout(()=>location.href='http://glpi-ticket.test/glpi/front/central.php',300)</script>" }); return true; }
        if (uri.AbsolutePath.EndsWith("central.php", StringComparison.Ordinal))
        { await route.FulfillAsync(new() { ContentType = "text/html", Body = "<a href='/glpi/front/logout.php'>Déconnexion</a><h1>Accueil</h1>" }); return true; }
        return false;
    });
    TicketDetailResult result;
    try { result = await Read(session); }
    catch { Console.WriteLine("SSO FIXTURE url=" + session.Service.TicketPage?.Url + " requests=" + JsonSerializer.Serialize(session.Requests.ToArray()) + " logs=" + string.Join(" | ", session.Log)); throw; }
    Require(result.Sections.Count > 0 && attempts == 2, "SSO/retour fiche incorrect.");
    Require(session.Search.Url == listUrl && !string.Join(" ", session.Log).Contains("NEVER_SSO", StringComparison.Ordinal), "SSO touche recherche/secret.");
}

async Task Frames()
{
    const string shell = "<!doctype html><title>Portail ticket</title><iframe id='content' src='/glpi/detailframe.php' style='width:1000px;height:900px'></iframe><iframe hidden src='/glpi/hiddenframe.php'></iframe><iframe src='http://foreign-ticket.test/frame' style='width:500px;height:100px'></iframe>";
    await using var session = await Setup(shell, async (route, uri) =>
    {
        if (uri.AbsolutePath == "/glpi/detailframe.php") { await route.FulfillAsync(new() { ContentType = "text/html", Body = modern }); return true; }
        if (uri.AbsolutePath == "/glpi/hiddenframe.php" || uri.Host == "foreign-ticket.test") { await route.FulfillAsync(new() { ContentType = "text/html", Body = modern.Replace("42", "99", StringComparison.Ordinal).Replace("Ticket moderne privé", "NEVER_FRAME_831", StringComparison.Ordinal) }); return true; }
        return false;
    });
    var result = await Read(session);
    Require(result.Sections.Count >= 2 && Value(Section(result, "#itil-form"), "Statut") == "En attente", "Cadre visible non lu.");
    Require(!All(result).Contains("NEVER_FRAME", StringComparison.Ordinal), "Cadre masqué ou étranger lu.");
}

async Task PopupSso()
{
    int visits = 0;
    await using var session = await Setup(modern, async (route, uri) =>
    {
        if (uri.Host == "idp-ticket.test")
        {
            await route.FulfillAsync(new() { ContentType = "text/html", Body = "<title>Validation SSO</title><form id='login'><input autocomplete='one-time-code' value='NEVER_OTP_831'></form>" });
            return true;
        }
        if (!uri.AbsolutePath.EndsWith("ticket.form.php", StringComparison.Ordinal) || Interlocked.Increment(ref visits) != 1) return false;
        await route.FulfillAsync(new() { ContentType = "text/html", Body = "<title>Connexion de la fiche</title><form id='login'><input type='password'></form><script>setTimeout(()=>window.open('http://idp-ticket.test/login','ticket-sso-popup'),250)</script>" });
        return true;
    });
    var pending = Read(session);
    var timer = Stopwatch.StartNew();
    while (session.Service.TicketPage?.Url != "http://idp-ticket.test/login" && timer.Elapsed.TotalSeconds < 4) await Task.Delay(50);
    Require(session.Service.TicketPage?.Url == "http://idp-ticket.test/login", "TicketPage ne désigne pas le challenge SSO pour l’affichage volontaire.");
    Require(ReferenceEquals(session.Service.ActivePage, session.Search), "Challenge SSO affecte ActivePage.");
    // Emulate a successful external identity-provider return. The reader itself
    // never types the OTP or invokes the test transition.
    await session.Service.TicketPage!.EvaluateAsync("url => { location.href = url; }", detailUrl);
    var result = await pending;
    Require(result.Sections.Count >= 2 && visits == 2, "Popup SSO non suivi.");
    Require(ReferenceEquals(session.Service.ActivePage, session.Search) && session.Search.Url == listUrl, "Popup SSO prend la recherche.");
    Require((await session.Service.GetPagesAsync()).Count == 1, "Popup SSO proposé comme page de recherche.");
    Require(session.Service.TicketPage != null && !ReferenceEquals(session.Service.TicketPage, session.Search), "Page dédiée absente après SSO popup.");
}

async Task PrivateLogs()
{
    await using var session = await Setup(modern); await Read(session);
    var text = string.Join("\n", session.Log);
    foreach (var sentinel in new[] { "42", "Ticket moderne privé", "Description multiligne", "Personne de test", "NEVER_", "?id=", "criteria", "http://" })
        Require(!text.Contains(sentinel, StringComparison.Ordinal), "Journal contient information privée : " + sentinel);
    Require(text.Contains("sections", StringComparison.Ordinal), "Diagnostic structurel absent.");
}

async Task ReadOnly()
{
    await using var session = await Setup(modern);
    await session.Context.AddInitScriptAsync("window.__focusCalls=0;window.focus=()=>window.__focusCalls++;");
    var result = await Read(session);
    var page = session.Service.TicketPage!;
    Require(await page.EvaluateAsync<int>("window.formSubmissions+window.businessClicks+window.tabClicks+window.__focusCalls") == 0, "La lecture automatique a déclenché une action ou un focus JS.");
    Require(await page.Locator("#name").InputValueAsync() == result.Title && await page.Locator("#status").InputValueAsync() == "En attente" && await page.Locator("#private").IsCheckedAsync(), "Contrôles de la fiche modifiés.");
    Require(!session.Requests.Any(request => request.Method != "GET"), "La lecture automatique a envoyé un formulaire.");
}

async Task StructuredObservations()
{
    var html = """
    <!doctype html><title>Ticket synthétique</title><form id="itil-form"><input type="hidden" name="id" value="42"><label for="name">Titre</label><input id="name" name="name" value="Lecture structurée"></form>
    <div class="timeline_history" id="observed">
      <div class="h_item" id="task-1"><time class="h_date">20/10/2026</time><div class="h_content ITILTask"><div class="rich_text_container">Tâche explicite</div><span class="state" title="À faire">À faire</span><span data-task-owner>Alice</span><time data-due-date datetime="2026-10-21T09:00:00">21/10/2026 09:00</time><time data-planned-start datetime="2026-10-20T10:00:00">20/10/2026</time></div></div>
      <div class="h_item" id="task-2"><time class="h_date">20/10/2026</time><div class="h_content ITILTask"><div class="rich_text_container">Sans échéance</div></div></div>
      <div class="h_item" id="validation-1"><div class="h_content TicketValidation"><div class="rich_text_container">Accord demandé</div><span data-validation-owner>Responsable</span><span data-validation-status>En attente</span><time data-requested-at>20/10/2026 10:00</time></div></div>
      <div class="h_item waiting" id="wait-1"><div class="h_content"><span data-waiting-start>20/10/2026 10:00</span><span data-waiting-end>20/10/2026 12:00</span><span data-waiting-reason>Livraison</span></div></div>
      <div class="h_item" id="followup-1"><time class="h_date">21/10/2026 09:00</time><span class="h_user_name">Auteur déclaré</span><span data-actor-role="requester">Demandeur</span><div class="h_content ITILFollowup"><div class="rich_text_container">Précision attendue</div></div></div>
      <div class="h_item" hidden><div class="h_content ITILTask"><span data-due-date>NEVER_HIDDEN_DEADLINE</span></div></div>
      <a href="/glpi/front/document.send.php?docid=7&amp;tickets_id=42">Pièce synthétique.pdf</a>
      <a href="/glpi/front/problem.form.php?id=17">Problème lié</a><a href="/glpi/front/computer.form.php?id=18">PC-18</a>
      <a href="/other/front/problem.form.php?id=17">NEVER_OTHER_INSTALLATION</a><a href="/glpi/front/problem.form.php?id=18&amp;delete=1">NEVER_BUSINESS_LINK</a>
    </div>
    """;
    await using var session = await Setup(html); var result = await Read(session);
    Require(result.ObservedItems.Count(item => item.Kind == "task") == 2, "Tâches structurées absentes/dupliquées.");
    var task = result.ObservedItems.Single(item => item.Title == "Tâche explicite");
    Require(task.Due == "2026-10-21T09:00:00" && task.Status == "À faire" && task.Owner == "Alice", "Échéance, état ou propriétaire explicite perdu.");
    Require(result.ObservedItems.Single(item => item.Title == "Sans échéance").Due == "", "Date de création détournée en échéance.");
    Require(result.ObservedItems.Single(item => item.Kind == "validation").RequestedAt.Contains("20/10/2026", StringComparison.Ordinal), "Date de validation non observée.");
    Require(result.ObservedItems.Single(item => item.Kind == "waiting").WaitingReason == "Livraison", "Motif non lu.");
    Require(result.ObservedItems.Single(item => item.Kind == "followup").AuthorRole == "requester", "Origine explicite non lue.");
    Require(result.ObservedItems.Count(item => item.Kind == "document") == 1 && result.ObservedItems.Count(item => item.Kind == "relation") == 2, "Index documents/relations incorrect.");
    Require(!JsonSerializer.Serialize(result.ObservedItems).Contains("NEVER_", StringComparison.Ordinal), "Valeur masquée ou lien interdit retenu.");
    Require(!session.Requests.Any(request => request.Method != "GET" || request.Path.Contains("document.send", StringComparison.Ordinal)), "Lecture déclenche action ou téléchargement.");
}

static string ActionFixture(string kind, string existingContent = "", string selected = "7", string extraFields = "", string actionOverride = "")
{
    var path = actionOverride.Length > 0 ? actionOverride : kind == "assignment" ? "/glpi/front/ticket.form.php" : kind == "task" ? "/glpi/front/tickettask.form.php" : "/glpi/front/itilfollowup.form.php";
    var fields = kind == "assignment" ? $"<input type='hidden' name='id' value='42'><label for='actor'>Attribué à</label><select id='actor' name='_users_id_assign' class='select2-hidden-accessible' style='display:none'><option value='7' {(selected == "7" ? "selected" : "")}>Alice</option><option value='8' {(selected == "8" ? "selected" : "")}>Bob</option></select><span class='select2-container'>{(selected == "8" ? "Bob" : "Alice")}</span>"
        : "<input type='hidden' name='items_id' value='42'><input type='hidden' name='itemtype' value='Ticket'><label for='action-content'>Contenu</label><textarea id='action-content' name='content'></textarea><label for='privacy'>Privé</label><input id='privacy' type='checkbox' name='is_private' value='1' checked>";
    if (kind == "task") fields = fields.Replace("id='action-content' name='content'", "id='action-content' name='content' style='display:none'", StringComparison.Ordinal)
        + "<div class='tox-tinymce'><iframe id='action-content_ifr' style='width:600px;height:150px'></iframe></div><script>const f=document.getElementById('action-content_ifr'); f.contentDocument.body.contentEditable='true'; window.tinymce={get:id=>id==='action-content'?{getContent:()=>f.contentDocument.body.innerText,save:()=>document.getElementById(id).value=f.contentDocument.body.innerText}:null};</script>";
    var eventClass = kind == "task" ? "ITILTask" : "ITILFollowup";
    return "<!doctype html><html><head><meta charset='utf-8'><title>Fiche synthétique</title></head><body><a href='/glpi/front/logout.php'>Déconnexion</a><form id='itil-form'><input type='hidden' name='id' value='42'><label for='name'>Titre</label><input id='name' name='name' value='Fiche de test'></form>"
        + $"<form id='action-form' method='post' action='{path}'>{fields}{extraFields}<input type='hidden' name='_glpi_csrf_token' value='NEVER_ACTION_CSRF'><button name='{(kind == "assignment" ? "update" : "add")}' value='1' type='submit'>Enregistrer</button></form>"
        + "<div class='timeline_history'>" + (existingContent.Length == 0 ? "" : $"<div class='h_item' id='entry-501'><div class='h_content {eventClass}'><div class='rich_text_container'>{System.Net.WebUtility.HtmlEncode(existingContent)}</div></div></div>") + "</div></body></html>";
}

async Task AssistedPreviewAndOneWrite()
{
    foreach (var kind in new[] { "followup", "task", "assignment" })
    {
        var posted = ""; var selected = "7"; var submissions = 0;
        await using var session = await Setup(ActionFixture(kind), async (route, uri) =>
        {
            if (route.Request.Method == "POST")
            {
                submissions++; var pairs = (route.Request.PostData ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2)).Select(pair => new KeyValuePair<string, string>(Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "")).ToArray();
                Require(pairs.Any(pair => pair.Key == "_glpi_csrf_token" && pair.Value == "NEVER_ACTION_CSRF"), "Le formulaire natif a perdu sa protection CSRF.");
                posted = pairs.FirstOrDefault(pair => pair.Key == "content").Value ?? ""; selected = pairs.FirstOrDefault(pair => pair.Key == "_users_id_assign").Value ?? selected;
                await route.FulfillAsync(new() { Status = 303, Headers = new Dictionary<string, string> { ["Location"] = detailUrl } }); return true;
            }
            if (uri.AbsolutePath.EndsWith("ticket.form.php", StringComparison.Ordinal)) { await route.FulfillAsync(new() { ContentType = "text/html", Body = ActionFixture(kind, posted, selected) }); return true; }
            return false;
        });
        await Read(session);
        var ticket = new TicketReference("42", detailUrl);
        var candidates = await session.Service.LearnTicketActionsAsync(ticket, session.Settings, CancellationToken.None);
        Require(candidates.Count == 1 && candidates[0].Adapter.Kind == kind, "Formulaire natif non appris : " + kind);
        Require(!JsonSerializer.Serialize(candidates).Contains("NEVER_ACTION_CSRF", StringComparison.Ordinal), "Jeton exposé dans l’adaptateur appris.");
        var value = kind == "assignment" ? "8" : "Constat synthétique\nProchaine action explicite";
        var preview = await session.Service.PreviewTicketActionAsync(ticket, new(candidates[0].Adapter, value, value), session.Settings, CancellationToken.None);
        Require(submissions == 0 && (kind == "assignment" ? await session.Service.TicketPage!.Locator("#actor").InputValueAsync() == "7" : await session.Service.TicketPage!.Locator("#action-content").InputValueAsync() == ""), "Prévisualiser a modifié ou soumis un champ.");
        await Reject(() => session.Service.ConfirmTicketActionAsync(preview.Token, false, session.Settings, CancellationToken.None), "confirmation explicite");
        Require(submissions == 0, "Envoi sans confirmation.");
        var outcome = await session.Service.ConfirmTicketActionAsync(preview.Token, true, session.Settings, CancellationToken.None);
        Require(submissions == 1 && outcome.Verified, "Soumission unique/relecture non vérifiée : " + kind + " / " + submissions + " envoi(s) / " + outcome.Message);
        await Reject(() => session.Service.ConfirmTicketActionAsync(preview.Token, true, session.Settings, CancellationToken.None), "déjà été consommé");
        Require(submissions == 1 && session.Search.Url == listUrl && ReferenceEquals(session.Service.ActivePage, session.Search), "Envoi répété ou recherche modifiée.");
        Require(!string.Join(" ", session.Log).Contains("Constat synthétique", StringComparison.Ordinal), "Contenu de suivi exposé dans le journal.");
    }
}

async Task AssistedRejectsUnsafeAndStale()
{
    foreach (var unsafeHtml in new[]
    {
        ActionFixture("followup", actionOverride:"http://foreign-ticket.test/glpi/front/itilfollowup.form.php"),
        ActionFixture("followup", actionOverride:"/other/front/itilfollowup.form.php"),
        ActionFixture("followup", extraFields:"<input type='hidden' name='delete' value='1'>"),
        ActionFixture("followup").Replace("name='items_id' value='42'", "name='items_id' value='43'", StringComparison.Ordinal),
        ActionFixture("followup").Replace("method='post'", "method='get'", StringComparison.Ordinal),
        ActionFixture("followup").Replace("method='post'", "method='post' target='search-page'", StringComparison.Ordinal)
    })
    {
        await using var session = await Setup(unsafeHtml); await Read(session);
        Require((await session.Service.LearnTicketActionsAsync(new("42", detailUrl), session.Settings, CancellationToken.None)).Count == 0, "Formulaire dangereux appris.");
        Require(!session.Requests.Any(request => request.Method == "POST"), "Inspection dangereuse a soumis.");
    }
    await using (var session = await Setup(ActionFixture("followup")))
    {
        await Read(session); var ticket = new TicketReference("42", detailUrl);
        var candidate = (await session.Service.LearnTicketActionsAsync(ticket, session.Settings, CancellationToken.None)).Single();
        var preview = await session.Service.PreviewTicketActionAsync(ticket, new(candidate.Adapter, "Test sécurisé", "Test sécurisé"), session.Settings, CancellationToken.None);
        await session.Service.TicketPage!.Locator("#privacy").UncheckAsync();
        await Reject(() => session.Service.ConfirmTicketActionAsync(preview.Token, true, session.Settings, CancellationToken.None), "changé depuis l’aperçu");
        Require(!session.Requests.Any(request => request.Method == "POST"), "Formulaire modifié après aperçu soumis.");
        await session.Service.TicketPage.Locator("#privacy").CheckAsync();
        preview = await session.Service.PreviewTicketActionAsync(ticket, new(candidate.Adapter, "Valeur prévue", "Valeur prévue"), session.Settings, CancellationToken.None);
        await session.Service.TicketPage.Locator("#action-content").EvaluateAsync("e=>e.addEventListener('input',()=>document.getElementById('privacy').checked=false)");
        await Reject(() => session.Service.ConfirmTicketActionAsync(preview.Token, true, session.Settings, CancellationToken.None), "autre champ a changé");
        Require(!session.Requests.Any(request => request.Method == "POST"), "Effet secondaire d’un champ non annoncé soumis.");
    }
}

async Task AssistedAmbiguousNeverRetries()
{
    var submissions = 0;
    await using var session = await Setup(ActionFixture("followup"), async (route, uri) =>
    {
        if (route.Request.Method != "POST") return false;
        submissions++;
        await route.FulfillAsync(new() { Status = 503, ContentType = "text/html", Body = "<h1>État inconnu</h1>" }); return true;
    });
    await Read(session); var ticket = new TicketReference("42", detailUrl);
    var candidate = (await session.Service.LearnTicketActionsAsync(ticket, session.Settings, CancellationToken.None)).Single();
    var preview = await session.Service.PreviewTicketActionAsync(ticket, new(candidate.Adapter, "Message de test", "Message de test"), session.Settings, CancellationToken.None);
    var outcome = await session.Service.ConfirmTicketActionAsync(preview.Token, true, session.Settings, CancellationToken.None);
    Require(!outcome.Verified && submissions == 1 && outcome.Message.Contains("non confirmé", StringComparison.Ordinal), "Un échec a été déclaré réussi ou répété.");
    await Reject(() => session.Service.ConfirmTicketActionAsync(preview.Token, true, session.Settings, CancellationToken.None), "déjà été consommé");
    Require(submissions == 1 && session.Search.Url == listUrl, "Nouvel envoi après résultat ambigu ou recherche modifiée.");
}

async Task AuthenticatedDocumentDownload()
{
    var reserve = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); reserve.Start(); var port = ((System.Net.IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
    using var server = new System.Net.HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
    var origin = $"http://127.0.0.1:{port}"; var total = 0; var authenticated = false; var html = false;
    var task = Task.Run(async () =>
    {
        for (var i = 0; i < 2; i++)
        {
            var request = await server.GetContextAsync(); total++; authenticated |= request.Request.Cookies["fixture-auth"]?.Value == "synthetic-session";
            var bytes = System.Text.Encoding.UTF8.GetBytes(html ? "<html>Connexion</html>" : "Document fictif authentifié");
            request.Response.ContentType = html ? "text/html" : "application/pdf"; request.Response.ContentLength64 = bytes.Length;
            await request.Response.OutputStream.WriteAsync(bytes); request.Response.Close();
        }
    });
    await using var context = await browser.NewContextAsync();
    await context.AddCookiesAsync(new[] { new Cookie { Name = "fixture-auth", Value = "synthetic-session", Url = origin } });
    await using var service = new BrowserService(); service.AttachForTesting(context, origin + "/glpi/front/ticket.php");
    var settings = new AppSettings { PortalUrl = origin + "/glpi/front/ticket.php" };
    var ticket = new TicketReference("42", origin + "/glpi/front/ticket.form.php?id=42");
    var document = new TicketObservedItem { Kind = "document", Identity = "7", Url = origin + "/glpi/front/document.send.php?docid=7&tickets_id=42" };
    var folder = Path.Combine(Path.GetTempPath(), "glpi-document-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
    try
    {
        Require(total == 0, "Téléchargement avant action explicite.");
        var file = Path.Combine(folder, "piece.pdf"); await service.DownloadTicketDocumentAsync(ticket, document, settings, file, CancellationToken.None);
        Require(authenticated && File.ReadAllText(file) == "Document fictif authentifié", "Téléchargement hors contexte authentifié ou contenu perdu.");
        html = true; await Reject(() => service.DownloadTicketDocumentAsync(ticket, document, settings, Path.Combine(folder, "login.pdf"), CancellationToken.None), "page HTML");
        await task;
        Require(total == 2 && !File.Exists(Path.Combine(folder, "login.pdf")) && !Directory.EnumerateFiles(folder, "*.partial").Any(), "Page de connexion enregistrée ou fichier partiel oublié.");
    }
    finally { server.Stop(); Directory.Delete(folder, true); }
}

sealed class FixtureSession(IBrowserContext context, IPage search, BrowserService service, AppSettings settings,
    ConcurrentQueue<string> log, ConcurrentQueue<(string Path, string Method)> requests) : IAsyncDisposable
{
    public IBrowserContext Context { get; } = context;
    public IPage Search { get; } = search;
    public BrowserService Service { get; } = service;
    public AppSettings Settings { get; } = settings;
    public ConcurrentQueue<string> Log { get; } = log;
    public ConcurrentQueue<(string Path, string Method)> Requests { get; } = requests;
    public ValueTask DisposeAsync() => Service.DisposeAsync();
}
