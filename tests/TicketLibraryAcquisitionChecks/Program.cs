using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Playwright;

var browserPath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
if (string.IsNullOrWhiteSpace(browserPath)) { Console.Error.WriteLine("Définir GLPI_TEST_CHROMIUM vers un Chromium de test installé."); return 2; }
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { ExecutablePath = browserPath, Headless = true, Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
const string source = "http://glpi-library.test/glpi/front/ticket.php?criteria%5B0%5D%5Bfield%5D=12&criteria%5B0%5D%5Bsearchtype%5D=equals&criteria%5B0%5D%5Bvalue%5D=notold";
const string ticketUrl = "http://glpi-library.test/glpi/front/ticket.form.php?id=42";
var checks = new List<(string Name, Func<Task> Run)>
{
    ("Index : Tous observé inclut résolus et clos sur plusieurs pages", AllStatuses),
    ("Index : recherche affichée et focus conservés, page dédiée refermée", SearchUnchanged),
    ("Index : filtre de session restauré par le GET natif observé", RestoreSearch),
    ("Index : absence de Tous refuse une fausse collecte complète", MissingAll),
    ("Index : date_mod native préservée avec précision et sans fuseau inventé", NativeModification),
    ("Index : date absente ou colonne ambiguë ne produit aucun token fiable", MissingModification),
    ("Index : lien Suivant avec action non reconnue refusé avant navigation", UnsafePagination),
    ("Index : changement de périmètre conserve une couverture partielle", ChangedScope),
    ("Index : limite configurée et identité dupliquée restent partielles", LimitsAndDuplicates),
    ("Index : résultat vide explicitement annoncé peut être complet", EmptyIndex),
    ("Index : annulation ferme la navigation dédiée et conserve la recherche", CancelIndex),
    ("Détails : tous les onglets sûrs, description, suivis, tâches et solution", AllTabs),
    ("Détails : historique paginé lu jusqu’au total, aucune ligne perdue", HistoryPages),
    ("Détails : références des documents sans téléchargement binaire", DocumentReferences),
    ("Détails : onglet étranger et action métier ne sont jamais cliqués", UnsafeTabs),
    ("Détails : événement masqué et chargement non reconnu restent partiels", HiddenAndLoadMore),
    ("Détails : limite de section empêche la déclaration complète", LimitedDetails),
    ("Détails : pagination JavaScript non canonique ne déclenche aucune action", UnsafeDetailPager),
    ("Détails : annulation d’un onglet conserve la recherche et ferme la fiche", CancelDetails),
    ("Diagnostic : aucune valeur de ticket, adresse détaillée ou référence documentaire", PrivateLogs)
};
var filter = Environment.GetEnvironmentVariable("GLPI_TEST_FILTER");
if (!string.IsNullOrWhiteSpace(filter)) checks = checks.Where(item => item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
var failed = 0;
foreach (var check in checks)
{
    try { await check.Run().WaitAsync(TimeSpan.FromSeconds(40)); Console.WriteLine("PASS " + check.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + check.Name + "\n" + error); }
}
Console.WriteLine($"{checks.Count - failed}/{checks.Count} contrôles d’acquisition de bibliothèque réussis.");
return failed == 0 ? 0 : 1;

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static string Escape(string value) => WebUtility.HtmlEncode(value);
static string Query(Uri uri, string name) => TicketReferenceResolver.ParseQuery(uri).FirstOrDefault(pair => pair.Key == name).Value ?? "";

string ListHtml(Uri uri, FixtureOptions options)
{
    var all = Query(uri, "criteria[0][value]") == "all";
    var start = int.TryParse(Query(uri, "start"), out var parsed) ? parsed : 0;
    var count = options.Empty ? 0 : all ? 3 : 1;
    var items = options.Empty ? Array.Empty<int>() : !all ? new[] { 41 } : start == 0 ? new[] { 41, 42 } : new[] { options.Duplicate ? 42 : 43 };
    var value = all ? "all" : "notold";
    var fields = options.NoModification ? "" : "<th data-searchopt-id='19'>Dernière modification</th>";
    if (options.DuplicateModification) fields += "<th data-searchopt-id='19'>Date ambiguë</th>";
    var rows = string.Join("", items.Select(id => "<tr><td><a href='/glpi/front/ticket.form.php?id=" + id + "'>Titre synthétique " + id + "</a></td><td>" + (id == 41 ? "Nouveau" : id == 42 ? "Résolu" : "Clos") + "</td>" +
        (options.NoModification ? "" : "<td>" + Escape(options.Modified) + "</td>") + (options.DuplicateModification ? "<td>22/10/2026 14:31</td>" : "") + "<td>Catégorie fictive</td></tr>"));
    if (options.Empty) rows = "<tr><td colspan='4'>Aucun élément trouvé</td></tr>";
    var next = new UriBuilder(uri); var pairs = TicketReferenceResolver.ParseQuery(uri).Where(pair => pair.Key != "start").ToList(); pairs.Add(new("start", "2"));
    if (options.UnsafeNext) pairs.Add(new("delete", "1"));
    next.Query = string.Join("&", pairs.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    var pager = all && start == 0 && count > 2 ? "<div class='search-pager'><p class='page-infos'>1-2/3</p><a class='page-link-next' data-start='2' href='" + Escape(next.Uri.AbsoluteUri) + "'>Suivant</a></div>" : "";
    return $$"""
        <!doctype html><html lang="fr"><meta charset="utf-8"><title>GLPI bibliothèque synthétique</title><body><div class="page"><header class="navbar">Entité {{(options.ChangeScope && start > 0 ? "B" : "A")}}</header></div>
        <a href="/glpi/front/logout.php">Déconnexion</a>
        <form name="searchformticket" class="search-form-container" method="get" action="/glpi/front/ticket.php">
          <select id="criterion-field" name="criteria[0][field]"><option value="12" selected>Statut</option><option value="19">Dernière modification</option></select>
          <select name="criteria[0][searchtype]"><option value="equals">est</option></select>
          <select name="criteria[0][value]"><option value="1">Nouveau</option><option value="5">Résolu</option><option value="6">Clos</option><option value="notold" {{(all ? "" : "selected")}}>Non résolu</option>{{(options.MissingAll ? "" : "<option value='all' " + (all ? "selected" : "") + ">Tous</option>")}}</select>
          <input type="hidden" name="is_deleted" value="0"><input type="hidden" name="as_map" value="0"><input type="hidden" name="browse" value="0">
          <a class="search-reset" href="/glpi/front/ticket.php?reset=reset">Vider</a><button type="submit" name="search">Rechercher</button>
        </form>
        <form data-search-itemtype="Ticket" data-start="{{start}}" data-count="{{count}}" data-limit="2" action="/glpi/front/massiveaction.php">
          <table class="search-results"><thead><tr><th data-searchopt-id="1">Titre</th><th data-searchopt-id="12">Statut</th>{{fields}}<th data-searchopt-id="7">Catégorie</th></tr></thead><tbody>{{rows}}</tbody></table>{{pager}}
        </form></body></html>
        """;
}

string DetailHtml(FixtureOptions options) => $$"""
    <!doctype html><html lang="fr"><meta charset="utf-8"><title>Fiche synthétique confidentielle</title><style>body{font:16px sans-serif}[role=tablist]{display:flex;gap:16px}td,th{padding:4px}.h_item{padding:8px}</style><body>
    <a href="/glpi/front/logout.php">Déconnexion</a><ul role="tablist" class="ui-tabs-nav">
      <li role="tab"><a id="overview-tab" class="ui-tabs-anchor" href="#itil-form" aria-selected="true">Ticket</a></li>
      <li role="tab"><a id="treatment-tab" class="ui-tabs-anchor" href="/glpi/ajax/common.tabs.php?_itemtype=Ticket&amp;_glpi_tab=Ticket%241&amp;id=42" aria-controls="treatment-panel">Traitement</a></li>
      <li role="tab"><a id="history-tab" class="ui-tabs-anchor" href="/glpi/ajax/common.tabs.php?_itemtype=Ticket&amp;_glpi_tab=Log%241&amp;id=42" aria-controls="history-panel">Historique</a></li>
      <li role="tab"><a id="documents-tab" class="ui-tabs-anchor" href="/glpi/ajax/common.tabs.php?_itemtype=Ticket&amp;_glpi_tab=Document_Item%241&amp;id=42" aria-controls="documents-panel">Documents</a></li>
      <li role="tab"><button id="delete-ticket" type="submit">Supprimer</button></li>
      {{(options.ForeignTab ? "<li role='tab'><a class='ui-tabs-anchor' href='https://foreign.test/ajax/common.tabs.php?_itemtype=Ticket&amp;id=43&amp;_glpi_tab=Log%241' aria-controls='foreign-panel'>Onglet externe</a></li>" : "")}}
    </ul>
    <div id="itil-form" role="tabpanel"><form name="form" action="/glpi/front/ticket.form.php?id=42"><input type="hidden" name="id" value="42"><input type="hidden" name="_glpi_csrf_token" value="NEVER_LIBRARY_TOKEN"><label for="title">Titre</label><input id="title" name="name" value="Titre synthétique confidentiel"><label for="description">Description</label><textarea id="description" name="content">Description intégrale synthétique. Deuxième phrase conservée.</textarea><button type="submit">Enregistrer</button></form></div>
    <div id="treatment-panel" role="tabpanel" hidden></div><div id="history-panel" role="tabpanel" hidden></div><div id="documents-panel" role="tabpanel" hidden></div><div id="foreign-panel" role="tabpanel" hidden></div>
    <script>
      window.business=0;window.binary=0;window.submitted=0;window.tabReads=0;
      document.addEventListener('submit',e=>{e.preventDefault();window.submitted++});
      document.getElementById('delete-ticket').addEventListener('click',()=>window.business++);
      async function openTab(link,extra=''){
        for(const panel of document.querySelectorAll('[role=tabpanel]'))panel.hidden=true;
        for(const a of document.querySelectorAll('.ui-tabs-anchor'))a.setAttribute('aria-selected','false');
        const target=link.getAttribute('aria-controls')||link.hash.slice(1), panel=document.getElementById(target);
        panel.hidden=false;link.setAttribute('aria-selected','true');
        if(link.getAttribute('href').startsWith('#'))return;
        window.tabReads++;panel.setAttribute('aria-busy','true');panel.innerHTML=await(await fetch(link.href+extra,{method:'POST'})).text();panel.removeAttribute('aria-busy');
      }
      for(const link of document.querySelectorAll('.ui-tabs-anchor'))link.addEventListener('click',e=>{e.preventDefault();openTab(link)});
      window.reloadTab=async query=>{const link=document.querySelector('.ui-tabs-anchor[aria-selected=true]');await openTab(link,'&'+query)};
    </script></body></html>
    """;

string TabHtml(Uri uri, FixtureOptions options)
{
    var tab = Query(uri, "_glpi_tab");
    if (tab == "Ticket$1") return "<div class='timeline_history'><div class='h_item'><div class='h_date'>22/10/2026 10:10</div><div class='h_content ITILFollowup'><div class='displayed_content'><div class='rich_text_container'>Suivi synthétique intégral</div></div></div></div><div class='h_item'><div class='h_content ITILTask'><div class='displayed_content'><div class='rich_text_container'>Tâche synthétique intégrale</div></div></div></div><div class='h_item'><div class='h_content Solution accepted'><div class='displayed_content'><div class='rich_text_container'>Solution synthétique complète</div></div></div></div>" +
        (options.HiddenEvent ? "<div class='h_item' hidden>NEVER_HIDDEN_LIBRARY_EVENT</div>" : "") + "</div>" + (options.LoadMore ? "<button type='button'>Charger plus</button>" : "");
    if (tab == "Document_Item$1") return "<table id='document-list'><thead><tr><th>Nom</th><th>Type</th></tr></thead><tbody><tr><td><a href='/glpi/front/document.send.php?docid=7'>document-synthetique.pdf</a></td><td>PDF</td></tr></tbody></table>";
    var second = Query(uri, "start") == "2";
    var rows = second ? "<tr><td>22/10/2026 10:03</td><td>Événement historique trois</td></tr>" : "<tr><td>22/10/2026 10:01</td><td>Événement historique un</td></tr><tr><td>22/10/2026 10:02</td><td>Événement historique deux</td></tr>";
    return "<table id='history-list'><thead><tr><th>Date</th><th>Modification</th></tr></thead><tbody>" + rows + "</tbody></table><div class='search-pager'><p class='page-infos'>" + (second ? "3-3/3" : "1-2/3") + "</p>" +
        (second ? "" : "<a class='page-link-next' data-start='2' href=\"" + (options.UnsafeDetailNext ? "javascript:window.business++" : "javascript:reloadTab('start=2');") + "\">Suivant</a>") + "</div>";
}

async Task<FixtureSession> Setup(FixtureOptions? configuration = null)
{
    var options = configuration ?? new();
    var context = await browser.NewContextAsync();
    var requests = new ConcurrentQueue<string>();
    await context.RouteAsync("**/*", async route =>
    {
        var uri = new Uri(route.Request.Url); requests.Enqueue(uri.AbsoluteUri);
        if (uri.Host != "glpi-library.test") { await route.AbortAsync(); return; }
        try
        {
            if (uri.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal))
            {
                if (options.DelayIndex && Query(uri, "criteria[0][value]") == "all") await Task.Delay(1500);
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = ListHtml(uri, options) });
            }
            else if (uri.AbsolutePath.EndsWith("/front/ticket.form.php", StringComparison.Ordinal))
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = DetailHtml(options) });
            else if (uri.AbsolutePath.EndsWith("/ajax/common.tabs.php", StringComparison.Ordinal))
            {
                if (options.DelayDetail && Query(uri, "_glpi_tab") == "Log$1") await Task.Delay(1500);
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = TabHtml(uri, options) });
            }
            else await route.AbortAsync();
        }
        catch (PlaywrightException) { /* A cancellation deliberately closes the owned page. */ }
    });
    var search = await context.NewPageAsync(); await search.GotoAsync(source);
    var service = new BrowserService(); service.AttachForTesting(context, source);
    var settings = new AppSettings { PortalUrl = source, ListTimeoutSeconds = 5, DetailTimeoutSeconds = 5, MaxPages = 20, MaxRows = 1000 };
    await service.FindBestPageAsync(settings, search, CancellationToken.None);
    var logs = new ConcurrentQueue<string>(); service.Log += logs.Enqueue;
    return new(context, search, service, settings, logs, requests);
}

Task<TicketLibraryIndex> Index(FixtureSession s, CancellationToken ct = default) => s.Service.ReadTicketLibraryIndexAsync(s.Settings, null, ct);
Task<TicketLibraryDetailRead> Detail(FixtureSession s, CancellationToken ct = default) => s.Service.ReadTicketLibraryDetailsAsync(new("42", ticketUrl), s.Settings, null, ct);

async Task AllStatuses()
{
    await using var s = await Setup(); var result = await Index(s);
    Require(result.Complete && result.AllStatuses && result.ExpectedTotal == 3 && result.PagesRead == 2 && result.Tickets.Count == 3, "Index Tous incomplet.");
    Require(result.Tickets.Select(t => t.Ticket.Id).SequenceEqual(new[] { "41", "42", "43" }), "Identités résolue/close absentes.");
    Require(result.Tickets.Select(t => t.StatusCode).SequenceEqual(new int?[] { 1, 5, 6 }), "Statuts natifs non conservés.");
    Require(result.Tickets.All(t => t.IndexHeaders.Count == t.IndexValues.Count && t.Fields["Catégorie"] == "Catégorie fictive"), "Colonnes d’index perdues.");
}
async Task SearchUnchanged()
{
    await using var s = await Setup(); var before = await s.Search.ContentAsync(); await Index(s);
    Require(s.Search.Url == source && await s.Search.ContentAsync() == before && s.Service.ActivePage == s.Search && s.Context.Pages.Count == 1, "La recherche a été modifiée ou la page dédiée conservée.");
}
async Task RestoreSearch()
{
    await using var s = await Setup(); var result = await Index(s);
    Require(result.SearchRestored && Query(new Uri(s.Requests.Last()), "criteria[0][value]") == "notold", "Le filtre natif précédent n’a pas été restauré après Tous.");
    Require(Query(new Uri(s.Requests.Last()), "reset") == "reset" && s.Context.Pages.Count == 1, "Restauration non native ou page temporaire encore ouverte.");
}
async Task MissingAll()
{
    await using var s = await Setup(new() { MissingAll = true });
    try { await Index(s); throw new Exception("Tous absent accepté."); } catch (InvalidOperationException e) { Require(e.Message.Contains("Tous", StringComparison.Ordinal), "Refus inattendu."); }
    Require(!s.Requests.Any(url => Query(new Uri(url), "criteria[0][value]") == "all"), "Paramètre Tous inventé sans option observée.");
}
async Task NativeModification()
{
    await using var s = await Setup(); var index = await Index(s); var seed = index.Tickets[0];
    Require(seed.LastModifiedRaw == "22/10/2026 14:30" && seed.LastModifiedReliable && seed.LastModifiedPrecision == "minutes" && seed.LastModifiedAt == null, "Précision minute ou fuseau incorrect.");
    var explicitTime = BrowserService.ReadLibraryModification("2026-10-22T14:30:15+02:00", true);
    Require(explicitTime.Reliable && explicitTime.Precision == "seconds" && explicitTime.Instant?.Offset == TimeSpan.FromHours(2), "Offset explicite perdu.");
    Require(!BrowserService.ReadLibraryModification("hier à midi", true).Reliable && !BrowserService.ReadLibraryModification("22/10/2026 14:30", false).Reliable, "Date libre ou colonne non prouvée acceptée.");
    Require(!BrowserService.ReadLibraryModification("05/06/2026 14:30", true).Reliable, "Format jour/mois ambigu accepté sans preuve du format GLPI.");
    foreach (var order in new[] { "dmy", "mdy" })
    {
        await using var configured = await Setup(new() { Modified = "05/06/2026 14:30" });
        var observed = await configured.Service.ReadTicketLibraryIndexAsync(configured.Settings, null, CancellationToken.None, order);
        Require(observed.Complete && observed.Tickets.All(t => t.LastModifiedRaw == "05/06/2026 14:30"
            && t.LastModifiedReliable && t.LastModifiedPrecision == "minutes" && t.LastModifiedAt == null),
            "Ordre de dates explicitement configuré perdu entre le navigateur et le jeton de modification.");
    }
    Require(!BrowserService.ReadLibraryModification("05/06/2026 14:30", true, "ymd").Reliable
        && !BrowserService.ReadLibraryModification("22/10/2026 14:30", true, "mdy").Reliable
        && !BrowserService.ReadLibraryModification("10/22/2026 14:30", true, "dmy").Reliable
        && !BrowserService.ReadLibraryModification("31/02/2026 14:30", true, "dmy").Reliable
        && !BrowserService.ReadLibraryModification("05/06/2026 14:30", false, "dmy").Reliable,
        "Un ordre explicite ne doit pas autoriser une date impossible, un autre ordre ou une colonne non prouvée.");
    Require(BrowserService.ReadLibraryModification("10/22/2026 14:30:15", true, "mdy") is { Reliable: true, Precision: "seconds", Instant: null }
        && BrowserService.ReadLibraryModification("2026-10-22 14:30", true, "ymd") is { Reliable: true, Precision: "minutes", Instant: null },
        "L’ordre explicite ne doit pas inventer de fuseau ou modifier la précision observée.");
}
async Task MissingModification()
{
    foreach (var options in new[] { new FixtureOptions { NoModification = true }, new FixtureOptions { DuplicateModification = true } })
    { await using var s = await Setup(options); var result = await Index(s); Require(result.Complete && result.Tickets.All(t => !t.LastModifiedReliable && t.LastModifiedAt == null), "Date absente ou ambiguë devenue fiable."); }
}
async Task UnsafePagination()
{
    await using var s = await Setup(new() { UnsafeNext = true });
    try { await Index(s); throw new Exception("Pagination dangereuse acceptée."); } catch (InvalidOperationException) { }
    Require(!s.Requests.Any(url => url.Contains("delete=", StringComparison.Ordinal)), "Action de pagination demandée.");
}
async Task ChangedScope()
{
    await using var s = await Setup(new() { ChangeScope = true }); var result = await Index(s);
    Require(!result.Complete && result.Tickets.Count == 2 && result.Limitations.Count > 0, "Changement de périmètre ignoré.");
}
async Task LimitsAndDuplicates()
{
    await using var limited = await Setup(); limited.Settings.MaxRows = 1; var first = await Index(limited);
    Require(!first.Complete && first.Tickets.Count == 1, "Limite de lignes ignorée.");
    await using var duplicate = await Setup(new() { Duplicate = true }); var second = await Index(duplicate);
    Require(!second.Complete && second.Tickets.Count == 2, "Doublon d’identité déclaré complet.");
}
async Task EmptyIndex()
{
    await using var s = await Setup(new() { Empty = true }); var result = await Index(s);
    Require(result.Complete && result.ExpectedTotal == 0 && result.Tickets.Count == 0, "Total zéro explicite refusé.");
}
async Task CancelIndex()
{
    await using var s = await Setup(new() { DelayIndex = true }); using var cancel = new CancellationTokenSource(400);
    try { await Index(s, cancel.Token); throw new Exception("Annulation ignorée."); } catch (OperationCanceledException) { }
    Require(s.Context.Pages.Count == 1 && s.Search.Url == source && s.Service.ActivePage == s.Search, "Page d’index non fermée.");
    Require(Query(new Uri(s.Requests.Last()), "criteria[0][value]") == "notold", "Filtre non restauré après annulation.");
}
async Task AllTabs()
{
    await using var s = await Setup(); var result = await Detail(s); var json = JsonSerializer.Serialize(result.Detail);
    Require(result.Coverage.Complete && result.Coverage.TabsDiscovered == 4 && result.Coverage.TabsLoaded == 4, "Couverture des quatre onglets non confirmée : " + string.Join(" | ", result.Coverage.Limitations));
    foreach (var expected in new[] { "Description intégrale synthétique", "Suivi synthétique intégral", "Tâche synthétique intégrale", "Solution synthétique complète" })
        Require(result.Detail.Sections.Any(section => section.Data.Rows.Any(row => row.Any(cell => cell.Contains(expected, StringComparison.Ordinal)))), "Contenu de consultation absent.");
    Require(!json.Contains("NEVER_", StringComparison.Ordinal) && await s.Service.TicketPage!.EvaluateAsync<int>("window.business+window.submitted") == 0, "Secret lu ou mutation déclenchée.");
}
async Task HistoryPages()
{
    await using var s = await Setup(); var result = await Detail(s);
    var history = result.Detail.Sections.Single(section => section.Selector == "#history-list");
    Require(history.Data.Rows.Count == 3 && history.Data.Pages == 2 && history.Data.Rows[2][1] == "Événement historique trois", "Historique paginé tronqué ou dupliqué.");
    Require(s.Requests.Count(url => url.Contains("common.tabs.php", StringComparison.Ordinal) && Query(new Uri(url), "_glpi_tab") == "Log$1") == 2, "Historique relu ou page suivante absente.");
}
async Task DocumentReferences()
{
    await using var s = await Setup(); var result = await Detail(s);
    Require(result.Detail.ObservedItems.Any(item => item.Kind == "document" && item.Url.EndsWith("document.send.php?docid=7", StringComparison.Ordinal)), "Référence documentaire absente.");
    Require(!s.Requests.Any(url => url.Contains("document.send.php", StringComparison.Ordinal)) && result.Coverage.Limitations.Any(x => x.Contains("binaire", StringComparison.Ordinal)), "Binaire téléchargé ou limite non exposée.");
}
async Task UnsafeTabs()
{
    await using var s = await Setup(new() { ForeignTab = true }); var result = await Detail(s);
    Require(!result.Coverage.Complete && !s.Requests.Any(url => url.Contains("foreign.test", StringComparison.Ordinal)) && await s.Service.TicketPage!.EvaluateAsync<int>("window.business") == 0, "Onglet étranger ou action ouverte.");
}
async Task HiddenAndLoadMore()
{
    await using var s = await Setup(new() { HiddenEvent = true, LoadMore = true }); var result = await Detail(s);
    Require(!result.Coverage.Complete && result.Coverage.Limitations.Any(x => x.Contains("masqués", StringComparison.Ordinal)) && result.Coverage.Limitations.Any(x => x.Contains("chargement", StringComparison.Ordinal)), "Contenu masqué ou pagination inconnue déclaré complet.");
    Require(!JsonSerializer.Serialize(result).Contains("NEVER_HIDDEN", StringComparison.Ordinal), "Événement masqué extrait.");
}
async Task LimitedDetails()
{
    await using var s = await Setup(); s.Settings.TicketDetails.MaxRowsPerSection = 1; var result = await Detail(s);
    Require(!result.Coverage.Complete && result.Detail.Sections.Any(section => !section.Data.Complete), "Section limitée déclarée complète.");
}
async Task UnsafeDetailPager()
{
    await using var s = await Setup(new() { UnsafeDetailNext = true }); var result = await Detail(s);
    Require(!result.Coverage.Complete && await s.Service.TicketPage!.EvaluateAsync<int>("window.business") == 0, "JavaScript non canonique exécuté.");
}
async Task CancelDetails()
{
    await using var s = await Setup(new() { DelayDetail = true }); using var cancel = new CancellationTokenSource(1800);
    try { await Detail(s, cancel.Token); throw new Exception("Annulation détail ignorée."); } catch (OperationCanceledException) { }
    Require(s.Search.Url == source && s.Context.Pages.Count == 1 && s.Service.ActivePage == s.Search, "Fiche annulée conservée ou recherche modifiée.");
}
async Task PrivateLogs()
{
    await using var s = await Setup(); await Index(s); await Detail(s); var log = string.Join("\n", s.Logs);
    foreach (var forbidden in new[] { "confidentiel", "synthétique", "docid=", "id=42", "NEVER_", "Solution synthétique" })
        Require(!log.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "Contenu de ticket écrit au journal.");
}

sealed class FixtureOptions
{
    public bool MissingAll { get; init; }
    public bool NoModification { get; init; }
    public bool DuplicateModification { get; init; }
    public string Modified { get; init; } = "22/10/2026 14:30";
    public bool UnsafeNext { get; init; }
    public bool ChangeScope { get; init; }
    public bool Duplicate { get; init; }
    public bool Empty { get; init; }
    public bool DelayIndex { get; init; }
    public bool ForeignTab { get; init; }
    public bool HiddenEvent { get; init; }
    public bool LoadMore { get; init; }
    public bool UnsafeDetailNext { get; init; }
    public bool DelayDetail { get; init; }
}
sealed class FixtureSession(IBrowserContext context, IPage search, BrowserService service, AppSettings settings, ConcurrentQueue<string> logs, ConcurrentQueue<string> requests) : IAsyncDisposable
{
    public IBrowserContext Context { get; } = context;
    public IPage Search { get; } = search;
    public BrowserService Service { get; } = service;
    public AppSettings Settings { get; } = settings;
    public ConcurrentQueue<string> Logs { get; } = logs;
    public ConcurrentQueue<string> Requests { get; } = requests;
    public ValueTask DisposeAsync() => Service.DisposeAsync();
}
