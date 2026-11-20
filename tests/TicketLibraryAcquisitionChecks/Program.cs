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
    ("Index : pages réelles de GLPI 9.5, Tous sur trois pages et dates jour/mois/année", Glpi95Index),
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
    ("Index : requête Tous au-delà de vingt secondes et restauration lente suivent la progression réelle", SlowNativeIndexAndRestore),
    ("Index : changement Statut AJAX lent observé sans sélectionner deux fois", SlowNativeStatus),
    ("Index : critère Statut attend DOMContentLoaded et son script defer lent", DeferredStatusInitialization),
    ("Index : rendu des résultats AJAX lent attend la réponse native", SlowNativeResults),
    ("Index : erreur HTTP native précise avant le premier ticket et sans nouvelle tentative", IndexHttpFailure),
    ("Index : panne réseau native précise sans masquer la cause par la restauration", IndexNetworkFailure),
    ("Index : redirection de session refusée sans poursuivre hors du périmètre", IndexSessionRedirect),
    ("Index : blocage sans progression arrêté même si un polling étranger reste actif", IndexNoProgress),
    ("Index : annulation pendant la restauration ferme sa requête et ne retourne pas un succès", CancelDuringRestore),
    ("Index ciblé : sélection bornée parmi 180 000 tickets, identifiant natif observé et filtre restauré", TargetedIndex),
    ("Index ciblé : plusieurs lots sans lecture des autres tickets", TargetedBatches),
    ("Index ciblé : sélection vide et URL étrangère ne déclenchent aucune navigation", TargetedInputGuard),
    ("Index ciblé : champ ID absent ou critère détourné refusé sans recherche globale", TargetedCriterionGuard),
    ("Index ciblé : cible absente demeure explicitement indisponible", TargetedMissing),
    ("Index ciblé : changement de périmètre entre lots interdit une confirmation complète", TargetedChangedScope),
    ("Index ciblé : refus de session et annulation conservent la recherche", TargetedSessionAndCancel),
    ("Index ciblé : ID numérique legacy contains est, Select2 masqué et filtre restauré", () => TargetedNumericOperator("est", multipleBatches: false, select2: true)),
    ("Index ciblé : ID numérique contains is, trente cibles en lots OR exacts", () => TargetedNumericOperator("is", multipleBatches: true, select2: false)),
    ("Index ciblé : opérateurs non exacts ou égalité canonique inutilisable refusés", TargetedNonExactOperator),
    ("Index ciblé : opérateur numérique dupliqué ou contrôle natif ambigu refusé", TargetedAmbiguousOperator),
    ("Index ciblé : opérateur modifié dans la réponse ou une ligne OR interdit la confirmation", TargetedOperatorDrift),
    ("Détails : tous les onglets sûrs, description, suivis, tâches et solution", AllTabs),
    ("Détails : historique paginé lu jusqu’au total, aucune ligne perdue", HistoryPages),
    ("Détails : références des documents sans téléchargement binaire", DocumentReferences),
    ("Détails : onglet étranger et action métier ne sont jamais cliqués", UnsafeTabs),
    ("Détails : onglets sans intérêt (coûts, Tous) jamais ouverts, couverture complète", SkippedTabs),
    ("Détails : fiche rouverte par GLPI sur un onglet vide, onglet Ticket ouvert puis lecture complète", RememberedEmptyTab),
    ("Détails : fiche rouverte par GLPI sur la vue Tous, onglet Ticket ouvert et vue Tous non conservée", RememberedSkippedTab),
    ("Détails : onglet de fiche distant reconnu par sa clé Ticket$main sous un libellé étranger", RemoteFormTab),
    ("Détails : événement masqué et chargement non reconnu restent partiels", HiddenAndLoadMore),
    ("Détails : limite de section empêche la déclaration complète", LimitedDetails),
    ("Détails : pagination JavaScript non canonique ne déclenche aucune action", UnsafeDetailPager),
    ("Détails : annulation d’un onglet conserve la recherche et ferme la fiche", CancelDetails),
    ("Diagnostic : aucune valeur de ticket, adresse détaillée ou référence documentaire", PrivateLogs)
};
checks.AddRange(NativePageLimitChecks.Create(browser));
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

string IdOperatorHtml(FixtureOptions options, int index, bool result = false)
{
    var drift = result ? options.TargetOperatorDrift : "";
    var changedValue = drift == "all-value" || drift == "one-value" && index == 1;
    var value = changedValue ? "equals" : options.NativeIdOperator;
    var label = changedValue ? "est" : drift == "one-label" && index == 1 ? "contient" : options.NativeIdOperatorLabel;
    var disabled = drift == "one-disabled" && index == 1;
    var select = "<select name='criteria[" + index + "][searchtype]'" + (options.NativeIdSelect2 ? " class='select2-hidden-accessible' style='display:none'" : "") + ">"
        + "<option value='" + Escape(value) + "' selected" + (disabled ? " disabled" : "") + ">" + Escape(label) + "</option>"
        + string.Concat(options.AdditionalIdOperators.Select(option => "<option value='" + Escape(option.Value) + "'"
            + (option.Disabled ? " disabled" : "") + ">" + Escape(option.Label) + "</option>")) + "</select>";
    return select + (options.DuplicateIdOperatorControl ? select : "")
        + (options.NativeIdSelect2 ? "<span class='select2-container'>" + Escape(label) + "</span>" : "");
}

string ListHtml(Uri uri, FixtureOptions options)
{
    if (options.NativeIdField.Length > 0 && Query(uri, "criteria[0][field]") == options.NativeIdField)
        return TargetedListHtml(uri, options);
    var all = Query(uri, "criteria[0][value]") == "all";
    var start = int.TryParse(Query(uri, "start"), out var parsed) ? parsed : 0;
    var count = options.Empty ? 0 : options.NativeIdField.Length > 0 ? 180000 : all ? 3 : 1;
    var items = options.Empty ? Array.Empty<int>() : !all ? new[] { 41 } : start == 0 ? new[] { 41, 42 } : new[] { options.Duplicate ? 42 : 43 };
    var value = all ? "all" : "notold";
    var fields = options.NoModification ? "" : "<th data-searchopt-id='19'>Dernière modification</th>";
    var needsStatus = options.NeedsStatus && !all;
    var nativeStatus = "<select name='criteria[0][searchtype]'><option value='equals'>est</option></select>"
        + "<select name='criteria[0][value]'><option value='1'>Nouveau</option><option value='5'>Résolu</option><option value='6'>Clos</option>"
        + "<option value='notold' " + (all ? "" : "selected") + ">Non résolu</option>"
        + (options.MissingAll ? "" : "<option value='all' " + (all ? "selected" : "") + ">Tous</option>") + "</select>";
    var statusControls = needsStatus
        ? "<select name='criteria[0][searchtype]'><option value='equals'>est</option></select><input name='criteria[0][value]' value='22/10/2026'>"
        : nativeStatus;
    if (options.DuplicateModification) fields += "<th data-searchopt-id='19'>Date ambiguë</th>";
    var rows = string.Join("", items.Select(id => "<tr><td><a href='/glpi/front/ticket.form.php?id=" + id + "'>Titre synthétique " + id + "</a></td><td>" + (id == 41 ? "Nouveau" : id == 42 ? "Résolu" : "Clos") + "</td>" +
        (options.NoModification ? "" : "<td>" + Escape(options.Modified) + "</td>") + (options.DuplicateModification ? "<td>22/10/2026 14:31</td>" : "") + "<td>Catégorie fictive</td></tr>"));
    if (options.Empty) rows = "<tr><td colspan='4'>Aucun élément trouvé</td></tr>";
    var next = new UriBuilder(uri); var pairs = TicketReferenceResolver.ParseQuery(uri).Where(pair => pair.Key != "start").ToList(); pairs.Add(new("start", "2"));
    if (options.UnsafeNext) pairs.Add(new("delete", "1"));
    next.Query = string.Join("&", pairs.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    var pager = all && start == 0 && count > 2 ? "<div class='search-pager'><p class='page-infos'>1-2/3</p><a class='page-link-next' data-start='2' href='" + Escape(next.Uri.AbsoluteUri) + "'>Suivant</a></div>" : "";
    return $$"""
        <!doctype html><html lang="fr"><meta charset="utf-8"><title>GLPI bibliothèque synthétique</title>{{(needsStatus && options.DeferredStatusHandler ? "<script defer src='/glpi/js/library-search-fixture.js'></script>" : "")}}<body><div class="page"><header class="navbar">Entité {{(options.ChangeScope && start > 0 ? "B" : options.CurrentEntity)}}</header></div>
        <a href="/glpi/front/logout.php">Déconnexion</a>
        <form name="searchformticket" class="search-form-container" method="get" action="/glpi/front/ticket.php">
          <select id="criterion-field" name="criteria[0][field]" {{(options.NativeIdSelect2 ? "class='select2-hidden-accessible' style='display:none'" : "")}}><option value="12" {{(needsStatus ? "" : "selected")}}>Statut</option><option value="19" {{(needsStatus ? "selected" : "")}}>Dernière modification</option>{{(options.NativeIdField.Length > 0 ? "<option value='" + Escape(options.NativeIdField) + "'>ID</option>" : "")}}</select>{{(options.NativeIdSelect2 ? "<span class='select2-container'>Critère natif</span>" : "")}}
          <span id="native-status-fields">{{statusControls}}</span>
          <input type="hidden" name="is_deleted" value="0"><input type="hidden" name="as_map" value="0"><input type="hidden" name="browse" value="0">
          <a class="search-reset" href="/glpi/front/ticket.php?reset=reset">Vider</a><button type="submit" name="search">Rechercher</button>
        </form>
        <form data-search-itemtype="Ticket" data-start="{{start}}" data-count="{{count}}" data-limit="2" action="/glpi/front/massiveaction.php">
          <table class="search-results"><thead><tr><th data-searchopt-id="1">Titre</th><th data-searchopt-id="12">Statut</th>{{fields}}<th data-searchopt-id="7">Catégorie</th></tr></thead><tbody>{{rows}}</tbody></table>{{pager}}
        </form>
        <script>
          {{(needsStatus && !options.DeferredStatusHandler && options.NativeIdField.Length == 0 ? "document.getElementById('criterion-field').addEventListener('change',async()=>{document.getElementById('native-status-fields').innerHTML=await(await fetch('/glpi/ajax/search.php',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:'action=display_searchoption&field=12&itemtype=Ticket'})).text()});" : "")}}
          {{(options.NativeIdField.Length > 0 ? "document.getElementById('criterion-field').addEventListener('change',async e=>{document.getElementById('native-status-fields').innerHTML=await(await fetch('/glpi/ajax/search.php',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:'action=display_searchoption&field='+encodeURIComponent(e.target.value)+'&itemtype=Ticket'})).text()});" : "")}}
          {{(all && start == 0 && options.ResultDelayMilliseconds > 0 ? "const results=document.querySelector('form[data-search-itemtype]');results.setAttribute('aria-busy','true');fetch('/glpi/ajax/search.php',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:'action=display_results&itemtype=Ticket'}).then(r=>r.text()).then(()=>results.removeAttribute('aria-busy'));" : "")}}
          {{(all && options.StalledIndex ? "document.querySelector('form[data-search-itemtype]').setAttribute('aria-busy','true');setInterval(()=>fetch('/glpi/ajax/search.php?action=fold_search&itemtype=Ticket'),150);" : "")}}
        </script></body></html>
        """;
}

string TargetedListHtml(Uri uri, FixtureOptions options)
{
    var criteria = TicketReferenceResolver.ParseQuery(uri).Where(p => p.Key.EndsWith("[field]", StringComparison.Ordinal)).ToList();
    var requested = criteria.Select(p => Query(uri, p.Key.Replace("[field]", "[value]", StringComparison.Ordinal))).ToList();
    var items = requested.Where(id => id != options.MissingTargetId).ToList();
    if (options.OutsideTarget && items.Count > 0) items[0] = "999999";
    var form = string.Join("", requested.Select((id, i) => "<select name='criteria[" + i + "][field]'><option value='12'>Statut</option><option value='" + options.NativeIdField + "' selected>ID</option></select>"
        + IdOperatorHtml(options, i, result: true) + "<input name='criteria[" + i + "][value]' value='" + (options.DriftTargetCriterion ? "999998" : Escape(id)) + "'>"
        + (i == 0 ? "" : "<select name='criteria[" + i + "][link]'><option value='OR'>OU</option></select>")));
    var rows = string.Join("", items.Select(id => "<tr><td><a href='/glpi/front/ticket.form.php?id=" + Escape(id) + "'>Ticket sélectionné " + Escape(id) + "</a></td><td>Clos</td><td>" + Escape(options.Modified) + "</td><td>Catégorie fictive</td></tr>"));
    if (items.Count == 0) rows = "<tr><td colspan='4'>Aucun élément trouvé</td></tr>";
    return $$"""
        <!doctype html><html lang="fr"><meta charset="utf-8"><body><div class="page"><header class="navbar">Entité {{(options.ChangeTargetScope && requested.Contains("30") ? "B" : options.CurrentEntity)}}</header></div>
        <a href="/glpi/front/logout.php">Déconnexion</a>
        <form name="searchformticket" method="get" action="/glpi/front/ticket.php">{{form}}
          <input type="hidden" name="is_deleted" value="0"><input type="hidden" name="as_map" value="0"><input type="hidden" name="browse" value="0">
          <a class="search-reset" href="/glpi/front/ticket.php?reset=reset">Vider</a></form>
        <form data-search-itemtype="Ticket" data-start="0" data-count="{{items.Count}}" data-limit="25">
          <table class="search-results"><thead><tr><th data-searchopt-id="1">Titre</th><th data-searchopt-id="12">Statut</th><th data-searchopt-id="19">Dernière modification</th><th data-searchopt-id="7">Catégorie</th></tr></thead><tbody>{{rows}}</tbody></table></form>
        </body></html>
        """;
}

string DetailHtml(FixtureOptions options) => $$"""
    <!doctype html><html lang="fr"><meta charset="utf-8"><title>Fiche synthétique confidentielle</title><style>body{font:16px sans-serif}[role=tablist]{display:flex;gap:16px}td,th{padding:4px}.h_item{padding:8px}</style><body>
    <a href="/glpi/front/logout.php">Déconnexion</a><ul role="tablist" class="ui-tabs-nav">
      <li role="tab">{{(options.RemoteForm ? "<a id='overview-tab' class='ui-tabs-anchor' href='/glpi/ajax/common.tabs.php?_itemtype=Ticket&amp;_glpi_tab=Ticket%24main&amp;id=42' aria-controls='itil-form' aria-selected='" + (options.RememberedTab.Length > 0 ? "false" : "true") + "'>Supportticket</a>" : "<a id='overview-tab' class='ui-tabs-anchor' href='#itil-form' aria-selected='" + (options.RememberedTab.Length > 0 ? "false" : "true") + "'>Ticket</a>")}}</li>
      <li role="tab"><a id="treatment-tab" class="ui-tabs-anchor" href="/glpi/ajax/common.tabs.php?_itemtype=Ticket&amp;_glpi_tab=Ticket%241&amp;id=42" aria-controls="treatment-panel">Traitement</a></li>
      <li role="tab"><a id="history-tab" class="ui-tabs-anchor" href="/glpi/ajax/common.tabs.php?_itemtype=Ticket&amp;_glpi_tab=Log%241&amp;id=42" aria-controls="history-panel">Historique</a></li>
      <li role="tab"><a id="documents-tab" class="ui-tabs-anchor" href="/glpi/ajax/common.tabs.php?_itemtype=Ticket&amp;_glpi_tab=Document_Item%241&amp;id=42" aria-controls="documents-panel">Documents</a></li>
      <li role="tab"><button id="delete-ticket" type="submit">Supprimer</button></li>
      {{(options.SkippedTabs || options.RememberedTab.Length > 0 ? "<li role='tab'><a id='costs-tab' class='ui-tabs-anchor' href='/glpi/ajax/common.tabs.php?_itemtype=Ticket&amp;_glpi_tab=TicketCost%241&amp;id=42' aria-controls='costs-panel' aria-selected='" + (options.RememberedTab == "costs" ? "true" : "false") + "'>Coûts</a></li><li role='tab'><a id='all-tab' class='ui-tabs-anchor' href='/glpi/ajax/common.tabs.php?_itemtype=Ticket&amp;_glpi_tab=-1&amp;id=42' aria-controls='all-panel' aria-selected='" + (options.RememberedTab == "all" ? "true" : "false") + "'>Tous</a></li>" : "")}}
      {{(options.ForeignTab ? "<li role='tab'><a class='ui-tabs-anchor' href='https://foreign.test/ajax/common.tabs.php?_itemtype=Ticket&amp;id=43&amp;_glpi_tab=Log%241' aria-controls='foreign-panel'>Onglet externe</a></li>" : "")}}
    </ul>
    <div id="itil-form" role="tabpanel"{{(options.RememberedTab.Length > 0 ? " hidden" : "")}}><form name="form" action="/glpi/front/ticket.form.php?id=42"><input type="hidden" name="id" value="42"><input type="hidden" name="_glpi_csrf_token" value="NEVER_LIBRARY_TOKEN"><label for="title">Titre</label><input id="title" name="name" value="Titre synthétique confidentiel"><label for="description">Description</label><textarea id="description" name="content">Description intégrale synthétique. Deuxième phrase conservée.</textarea><button type="submit">Enregistrer</button></form></div>
    <div id="treatment-panel" role="tabpanel" hidden></div><div id="history-panel" role="tabpanel" hidden></div><div id="documents-panel" role="tabpanel" hidden></div><div id="foreign-panel" role="tabpanel" hidden></div>{{(options.RememberedTab == "costs" ? "<div id='costs-panel' role='tabpanel'><div class='center'><table class='tab_cadre_fixe'><tr><th>Aucun historique</th></tr></table></div></div>" : "<div id='costs-panel' role='tabpanel' hidden></div>")}}{{(options.RememberedTab == "all" ? "<div id='all-panel' role='tabpanel'><table id='all-list'><thead><tr><th>Date</th><th>Contenu</th></tr></thead><tbody><tr><td>22/10/2026 10:05</td><td>NEVER_SKIPPED_TAB vue Tous</td></tr></tbody></table></div>" : "<div id='all-panel' role='tabpanel' hidden></div>")}}
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
    if (tab is "TicketCost$1" or "-1") return "<p>NEVER_SKIPPED_TAB</p>";
    if (tab == "Ticket$main") return "<form name='form' action='/glpi/front/ticket.form.php?id=42'><input type='hidden' name='id' value='42'><label for='title'>Titre</label><input id='title' name='name' value='Titre synthétique confidentiel'><label for='description'>Description</label><textarea id='description' name='content'>Description intégrale synthétique distante.</textarea></form>";
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
    var targetRequests = 0;
    await context.RouteAsync("**/*", async route =>
    {
        var uri = new Uri(route.Request.Url); requests.Enqueue(uri.AbsoluteUri);
        if (uri.Host != "glpi-library.test") { await route.AbortAsync(); return; }
        try
        {
            if (uri.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal))
            {
                var targeted = options.NativeIdField.Length > 0 && Query(uri, "criteria[0][field]") == options.NativeIdField;
                if (targeted) targetRequests++;
                if (options.ChangeBetweenTargetBatches && !targeted && Query(uri, "reset") == "reset" && targetRequests > 0)
                    options.CurrentEntity = "B";
                if (Query(uri, "criteria[0][value]") == "all" || targeted)
                {
                    if (options.IndexStatus >= 400) { await route.FulfillAsync(new() { Status = options.IndexStatus, Body = "NEVER_INDEX_ERROR_CONTENT" }); return; }
                    if (options.AbortIndex) { await route.AbortAsync("connectionfailed"); return; }
                    if (options.RedirectIndex) { await route.FulfillAsync(new() { Status = 302, Headers = new Dictionary<string, string> { ["Location"] = "/glpi/front/login.php" } }); return; }
                    if (options.IndexDelayMilliseconds > 0 && Query(uri, "start") == "") await Task.Delay(options.IndexDelayMilliseconds);
                }
                else if (Query(uri, "reset") == "reset")
                {
                    if (options.RestoreStatus >= 400) { await route.FulfillAsync(new() { Status = options.RestoreStatus, Body = "NEVER_RESTORE_ERROR_CONTENT" }); return; }
                    if (options.RestoreDelayMilliseconds > 0) await Task.Delay(options.RestoreDelayMilliseconds);
                }
                if (options.DelayIndex && (Query(uri, "criteria[0][value]") == "all" || targeted)) await Task.Delay(1500);
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = ListHtml(uri, options) });
            }
            else if (uri.AbsolutePath.EndsWith("/js/library-search-fixture.js", StringComparison.Ordinal))
            {
                await Task.Delay(6000);
                await route.FulfillAsync(new() { ContentType = "text/javascript", Body = "document.getElementById('criterion-field').addEventListener('change',async()=>{document.getElementById('native-status-fields').innerHTML=await(await fetch('/glpi/ajax/search.php',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:'action=display_searchoption&field=12&itemtype=Ticket'})).text()});" });
            }
            else if (uri.AbsolutePath.EndsWith("/ajax/search.php", StringComparison.Ordinal))
            {
                var action = Query(uri, "action");
                if (action.Length == 0) action = Query(new Uri("http://body.test/?" + route.Request.PostData), "action");
                if (action == "display_searchoption")
                {
                    await Task.Delay(options.StatusDelayMilliseconds);
                    var field = Query(new Uri("http://body.test/?" + route.Request.PostData), "field");
                    if (options.NativeIdField.Length > 0 && field == options.NativeIdField)
                    {
                        await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = IdOperatorHtml(options, 0) + "<input name='criteria[0][value]' value=''>" });
                        return;
                    }
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = "<select name='criteria[0][searchtype]'><option value='equals'>est</option></select><select name='criteria[0][value]'><option value='1'>Nouveau</option><option value='5'>Résolu</option><option value='6'>Clos</option><option value='notold' selected>Non résolu</option><option value='all'>Tous</option></select>" });
                }
                else { await Task.Delay(action == "display_results" ? options.ResultDelayMilliseconds : 700); await route.FulfillAsync(new() { Body = "" }); }
            }
            else if (uri.AbsolutePath.EndsWith("/front/login.php", StringComparison.Ordinal))
                await route.FulfillAsync(new() { ContentType = "text/html", Body = "<input type='password'><p>NEVER_INDEX_SESSION_CONTENT</p>" });
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
static TicketReference Target(string id) => new(id, "http://glpi-library.test/glpi/front/ticket.form.php?id=" + id);
Task<TicketLibraryIndex> Subset(FixtureSession s, IEnumerable<string> ids, CancellationToken ct = default) =>
    s.Service.ReadTicketLibrarySubsetIndexAsync(s.Settings, ids.Select(Target).ToArray(), null, ct);
static List<Uri> TargetQueries(FixtureSession s, string nativeField = "92") => s.Requests.Select(url => new Uri(url))
    .Where(uri => uri.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal) && Query(uri, "criteria[0][field]") == nativeField).ToList();
static void NoGlobalQuery(FixtureSession s) => Require(!s.Requests.Select(url => new Uri(url)).Any(uri => Query(uri, "criteria[0][value]") == "all"),
    "La collecte ciblée a lancé une recherche globale Tous.");

async Task TargetedIndex()
{
    await using var s = await Setup(new() { NativeIdField = "92" });
    var original = await s.Search.ContentAsync();
    var result = await Subset(s, new[] { "42", "43" });
    Require(result.IsTargeted && result.RequestedCount == 2 && result.ExpectedTotal == 2 && result.Complete && result.AllStatuses,
        "Sélection ciblée non confirmée : " + string.Join(" | ", result.Limitations));
    Require(result.PagesRead == 1 && result.Tickets.Select(t => t.Ticket.Id).SequenceEqual(new[] { "42", "43" })
        && result.Tickets.All(t => t.LastModifiedReliable && t.StatusCode == 6) && result.ScopeFingerprint.Length > 0,
        "Métadonnées ciblées absentes, non fiables ou identités différentes.");
    Require(result.SearchRestored && s.Search.Url == source && await s.Search.ContentAsync() == original
        && s.Context.Pages.Count == 1 && s.Service.ActivePage == s.Search, "Recherche affichée ou session non restaurée.");
    Require(TargetQueries(s).Count == 1 && !s.Requests.Any(url => url.Contains("ticket.form.php", StringComparison.Ordinal)),
        "Les contenus ou plusieurs recherches ont été ouverts pendant l’index ciblé.");
    NoGlobalQuery(s);
    await using var nonStatus = await Setup(new() { NativeIdField = "92", NeedsStatus = true });
    var observedStatuses = await Subset(nonStatus, new[] { "42", "43" });
    Require(observedStatuses.Complete && observedStatuses.Tickets.All(t => t.StatusCode == 6),
        "Les codes de statut n’ont pas été observés avant la recherche ID lorsque le filtre initial portait sur un autre champ.");
    NoGlobalQuery(nonStatus);
}

async Task TargetedBatches()
{
    await using var s = await Setup(new() { NativeIdField = "92" });
    var requested = Enumerable.Range(1, 30).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    var result = await Subset(s, requested);
    Require(result.Complete && result.AllStatuses && result.RequestedCount == 30 && result.Tickets.Count == 30 && result.PagesRead == 2,
        "Lots ciblés incomplets : " + string.Join(" | ", result.Limitations));
    var queries = TargetQueries(s);
    Require(queries.Count == 2 && queries.All(uri => TicketReferenceResolver.ParseQuery(uri).Count(p => p.Key.EndsWith("[field]", StringComparison.Ordinal)) <= 25),
        "Les recherches ne sont pas bornées à 25 identifiants.");
    var sent = queries.SelectMany(uri => TicketReferenceResolver.ParseQuery(uri).Where(p => p.Key.EndsWith("[value]", StringComparison.Ordinal)).Select(p => p.Value));
    Require(sent.SequenceEqual(requested) && result.SearchRestored, "Cible omise, dupliquée, ajoutée ou filtre non restauré entre lots.");
    NoGlobalQuery(s);
}

static void AssertTargetedOperatorQueries(FixtureSession session, IReadOnlyList<string> requested, string nativeField, string nativeOperator)
{
    var queries = TargetQueries(session, nativeField);
    Require(queries.Count == (requested.Count + 24) / 25, "Nombre de recherches ciblées incorrect ou critère renvoyé après attente.");
    var offset = 0;
    foreach (var uri in queries)
    {
        var pairs = TicketReferenceResolver.ParseQuery(uri);
        var count = pairs.Count(pair => pair.Key.EndsWith("[field]", StringComparison.Ordinal));
        Require(count == Math.Min(25, requested.Count - offset)
            && pairs.Count(pair => pair.Key.StartsWith("criteria[", StringComparison.Ordinal)) == 4 * count - 1,
            "Un lot ciblé doit contenir exactement ses identités et ses liaisons OR, sans autre critère.");
        for (var index = 0; index < count; index++)
        {
            var prefix = "criteria[" + index + "]";
            Require(Query(uri, prefix + "[field]") == nativeField && Query(uri, prefix + "[searchtype]") == nativeOperator
                && Query(uri, prefix + "[value]") == requested[offset + index],
                "Le champ, l’opérateur natif ou une identité a été deviné, réécrit ou omis dans la recherche ciblée.");
            Require(index == 0 ? !pairs.Any(pair => pair.Key == prefix + "[link]") : Query(uri, prefix + "[link]") == "OR",
                "Seules les identités du même lot doivent être reliées par OR.");
        }
        Require(Query(uri, "reset") == "reset" && new[] { "is_deleted", "as_map", "browse" }.All(flag => Query(uri, flag) == "0"),
            "La recherche ciblée doit rester hors corbeille et sans restriction implicite de la vue précédente.");
        offset += count;
    }
    Require(offset == requested.Count, "Une identité demandée n’a pas été couverte par les lots ciblés.");
}

async Task AssertTargetedSearchRestored(FixtureSession session, string original)
{
    Require(session.Search.Url == source && await session.Search.ContentAsync() == original
        && session.Context.Pages.Count == 1 && session.Service.ActivePage == session.Search,
        "La recherche affichée doit garder son DOM et sa page active après succès ou refus du critère ID.");
    var restored = session.Requests.Select(address => new Uri(address))
        .Last(uri => uri.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal));
    Require(Query(restored, "reset") == "reset" && Query(restored, "criteria[0][field]") == "12"
        && Query(restored, "criteria[0][searchtype]") == "equals" && Query(restored, "criteria[0][value]") == "notold",
        "Le filtre de session initial Statut equals Non résolu doit être restauré par son GET natif exact.");
    Require(!session.Requests.Any(address => address.Contains("ticket.form.php", StringComparison.Ordinal)),
        "La vérification ciblée des opérateurs ne doit ouvrir aucune fiche de contenu.");
    NoGlobalQuery(session);
}

async Task TargetedNumericOperator(string label, bool multipleBatches, bool select2)
{
    await using var s = await Setup(new()
    {
        NativeIdField = "2", NativeIdOperator = "contains", NativeIdOperatorLabel = label, NativeIdSelect2 = select2
    });
    var original = await s.Search.ContentAsync();
    var requested = multipleBatches
        ? Enumerable.Range(1, 30).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()
        : new[] { "42" };
    var result = await Subset(s, requested);
    Require(result.IsTargeted && result.RequestedCount == requested.Length && result.ExpectedTotal == requested.Length
        && result.Complete && result.AllStatuses && result.SearchRestored && result.ScopeFingerprint.Length > 0,
        "L’opérateur numérique natif observé n’a pas produit une sélection complète et restaurée : " + string.Join(" | ", result.Limitations));
    Require(result.PagesRead == (multipleBatches ? 2 : 1) && result.Tickets.Select(ticket => ticket.Ticket.Id).SequenceEqual(requested)
        && result.Tickets.All(ticket => ticket.LastModifiedReliable && ticket.StatusCode == 6),
        "Les identités, dates ou codes de statut ont été altérés par le changement d’opérateur du champ ID.");
    AssertTargetedOperatorQueries(s, requested, "2", "contains");
    Require(s.Requests.Count(address => new Uri(address).AbsolutePath.EndsWith("/ajax/search.php", StringComparison.Ordinal)) == (multipleBatches ? 2 : 1),
        "Le critère ID doit être sélectionné une fois par lot, sans deuxième changement après attente.");
    await AssertTargetedSearchRestored(s, original);
}

async Task AssertTargetedOperatorRefused(FixtureOptions options, bool queryWasSent)
{
    await using var s = await Setup(options);
    var original = await s.Search.ContentAsync();
    string[] requested = ["42", "43"];
    try
    {
        await Subset(s, requested);
        throw new Exception("Un opérateur ID non exact, ambigu ou modifié a été accepté.");
    }
    catch (GlpiLibraryIndexException) { }
    if (queryWasSent) AssertTargetedOperatorQueries(s, requested, "2", "contains");
    else Require(TargetQueries(s, "2").Count == 0, "Une recherche ID a été envoyée avant d’observer une égalité native univoque.");
    Require(s.Requests.Count(address => new Uri(address).AbsolutePath.EndsWith("/ajax/search.php", StringComparison.Ordinal)) == 1,
        "Un opérateur refusé ne doit pas déclencher une nouvelle sélection du critère.");
    await AssertTargetedSearchRestored(s, original);
}

async Task TargetedNonExactOperator()
{
    foreach (var options in new[]
    {
        new FixtureOptions { NativeIdField = "2", NativeIdOperator = "contains", NativeIdOperatorLabel = "contient" },
        new FixtureOptions { NativeIdField = "2", NativeIdOperator = "notcontains", NativeIdOperatorLabel = "is not" },
        new FixtureOptions { NativeIdField = "2", NativeIdOperator = "contains", NativeIdOperatorLabel = "est", AdditionalIdOperators = [("equals", "est", true)] },
        new FixtureOptions { NativeIdField = "2", NativeIdOperator = "contains", NativeIdOperatorLabel = "est", AdditionalIdOperators = [("equals", "est", false), ("equals", "est", false)] }
    }) await AssertTargetedOperatorRefused(options, queryWasSent: false);
}

async Task TargetedAmbiguousOperator()
{
    foreach (var options in new[]
    {
        new FixtureOptions { NativeIdField = "2", NativeIdOperator = "contains", NativeIdOperatorLabel = "est", AdditionalIdOperators = [("contains", "contient", false)] },
        new FixtureOptions { NativeIdField = "2", NativeIdOperator = "contains", NativeIdOperatorLabel = "est", DuplicateIdOperatorControl = true }
    }) await AssertTargetedOperatorRefused(options, queryWasSent: false);
}

async Task TargetedOperatorDrift()
{
    foreach (var drift in new[] { "all-value", "one-value", "one-label", "one-disabled" })
        await AssertTargetedOperatorRefused(new()
        {
            NativeIdField = "2", NativeIdOperator = "contains", NativeIdOperatorLabel = "est", TargetOperatorDrift = drift
        }, queryWasSent: true);
}

async Task TargetedInputGuard()
{
    await using var s = await Setup(new() { NativeIdField = "92" }); var before = s.Requests.Count;
    var empty = await Subset(s, Array.Empty<string>());
    Require(empty.IsTargeted && empty.Complete && empty.RequestedCount == 0 && empty.ExpectedTotal == 0
        && empty.ScopeFingerprint.Length == 0 && s.Requests.Count == before, "Une sélection vide a ouvert GLPI ou inventé un périmètre de session.");
    try
    {
        await s.Service.ReadTicketLibrarySubsetIndexAsync(s.Settings,
            new[] { Target("42"), new TicketReference("43", "http://foreign.test/glpi/front/ticket.form.php?id=43") }, null, CancellationToken.None);
        throw new Exception("Une référence d’une autre installation a été acceptée.");
    }
    catch (InvalidOperationException) { }
    Require(s.Requests.Count == before, "Une navigation a été envoyée avant la validation de toutes les cibles.");
}

async Task TargetedCriterionGuard()
{
    foreach (var options in new[] { new FixtureOptions(), new FixtureOptions { NativeIdField = "92", DriftTargetCriterion = true },
        new FixtureOptions { NativeIdField = "92", OutsideTarget = true } })
    {
        await using var s = await Setup(options);
        try { await Subset(s, new[] { "42", "43" }); throw new Exception("Critère absent/détourné ou ticket hors sélection accepté."); }
        catch (GlpiLibraryIndexException) { }
        Require(!s.Requests.Any(url => url.Contains("ticket.form.php", StringComparison.Ordinal)), "Une fiche a été lue malgré un index non vérifié.");
        NoGlobalQuery(s);
    }
}

async Task TargetedMissing()
{
    await using var s = await Setup(new() { NativeIdField = "92", MissingTargetId = "43" });
    var result = await Subset(s, new[] { "42", "43" });
    Require(result.Complete && result.RequestedCount == 2 && result.ExpectedTotal == 1 && result.Tickets.Single().Ticket.Id == "42"
        && result.Limitations.Any(x => x.Contains("sans confirmation", StringComparison.Ordinal)),
        "Une cible absente a été déclarée présente ou son absence n’est pas signalée.");
    NoGlobalQuery(s);
}

async Task TargetedChangedScope()
{
    await using var s = await Setup(new() { NativeIdField = "92", ChangeTargetScope = true });
    try
    {
        await Subset(s, Enumerable.Range(1, 30).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        throw new Exception("Le changement de périmètre a rendu un index partiel réutilisable sous une autre session.");
    }
    catch (GlpiLibraryIndexException error)
    {
        Require(error.Category == "index-scope" && error.Phase == "scope", "La dérive de périmètre n’est pas identifiée explicitement.");
    }
    Require(!s.Requests.Any(url => url.Contains("ticket.form.php", StringComparison.Ordinal)),
        "Une fiche a été ouverte après changement de périmètre.");
    NoGlobalQuery(s);
    await using var between = await Setup(new() { NativeIdField = "92", ChangeBetweenTargetBatches = true });
    await between.Search.CloseAsync();
    try
    {
        await Subset(between, Enumerable.Range(1, 30).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        throw new Exception("Deux lots individuellement valides sous des périmètres différents ont été fusionnés.");
    }
    catch (GlpiLibraryIndexException error)
    {
        Require(error.Category == "index-scope" && error.Phase == "scope", "La dérive entre deux lots n’a pas arrêté leur agrégation.");
    }
    Require(TargetQueries(between).Count == 2 && !between.Requests.Any(url => url.Contains("ticket.form.php", StringComparison.Ordinal)),
        "La dérive entre lots n’a pas été contrôlée avant toute lecture de fiche.");
    NoGlobalQuery(between);
}

async Task TargetedSessionAndCancel()
{
    await using (var s = await Setup(new() { NativeIdField = "92", IndexStatus = 403 }))
    {
        try { await Subset(s, new[] { "42" }); throw new Exception("Le refus de session a été ignoré."); }
        catch (GlpiLibraryIndexException error) { Require(error.Category == "index-session", "Le refus HTTP n’est pas identifié comme session/droits."); }
        Require(s.Search.Url == source && s.Context.Pages.Count == 1, "La recherche a été changée ou une fenêtre d’échec est restée ouverte.");
        NoGlobalQuery(s);
    }
    await using (var s = await Setup(new() { NativeIdField = "92", DelayIndex = true }))
    {
        using var cancel = new CancellationTokenSource(600);
        try { await Subset(s, new[] { "42" }, cancel.Token); throw new Exception("Annulation de la sélection ignorée."); }
        catch (OperationCanceledException) { }
        Require(s.Search.Url == source && s.Context.Pages.Count == 1 && s.Service.ActivePage == s.Search,
            "Une requête ciblée annulée reste ouverte ou la recherche affichée a changé.");
        NoGlobalQuery(s);
    }
}

// Real pages saved from a throwaway GLPI 9.5.13 with fictitious tickets (tests/Glpi95Fixtures), scripts removed.
async Task Glpi95Index()
{
    var pages = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../fixtures/glpi95"));
    await using var context = await browser.NewContextAsync();
    await context.RouteAsync("**/*", async route =>
    {
        var uri = new Uri(route.Request.Url);
        if (uri.Host != "glpi95.test" || uri.AbsolutePath != "/front/ticket.php" || route.Request.ResourceType != "document") { await route.AbortAsync(); return; }
        var name = Query(uri, "criteria[0][value]") == "all" ? "list-all-start" + (int.TryParse(Query(uri, "start"), out var start) ? start : 0) : "list-default";
        var file = Path.Combine(pages, name + ".html");
        if (!File.Exists(file)) { await route.FulfillAsync(new() { Status = 404, Body = "Unknown fixture page" }); return; }
        await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = await File.ReadAllTextAsync(file) });
    });
    const string legacy = "http://glpi95.test/front/ticket.php";
    var search = await context.NewPageAsync(); await search.GotoAsync(legacy);
    var service = new BrowserService(); service.AttachForTesting(context, legacy);
    var settings = new AppSettings { PortalUrl = legacy, ListTimeoutSeconds = 5, DetailTimeoutSeconds = 5, MaxPages = 20, MaxRows = 1000 };
    await service.FindBestPageAsync(settings, search, CancellationToken.None);
    var result = await service.ReadTicketLibraryIndexAsync(settings, null, CancellationToken.None, "dmy");
    Require(result.AllStatuses && result.Complete && result.Limitations.Count == 0 && result.ExpectedTotal == 30 && result.PagesRead == 3 && result.Tickets.Count == 30,
        "GLPI 9.5 : index Tous incomplet. " + string.Join(" | ", result.Limitations));
    Require(result.Tickets.Select(t => t.Ticket.Id).Distinct().Count() == 30 && result.Tickets.All(t => t.LastModifiedReliable && t.LastModifiedPrecision == "minutes"),
        "GLPI 9.5 : identités ou dates de modification non fiables.");
    Require(result.Tickets.Any(t => t.StatusCode == 5) && result.Tickets.Any(t => t.StatusCode == 6) && result.Tickets.Any(t => t.StatusCode == 1),
        "GLPI 9.5 : statuts natifs absents.");
}
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
    Require(!result.SearchRestored && !s.Requests.Any(url => Query(new Uri(url), "criteria[0][value]") != "all" && Query(new Uri(url), "reset") == "reset"),
        "L’ancien filtre a été réappliqué dans un contexte d’entités différent.");
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
async Task SlowNativeIndexAndRestore()
{
    await using var s = await Setup(new() { IndexDelayMilliseconds = 22000, RestoreDelayMilliseconds = 6000 });
    var diagnostics = new ConcurrentQueue<LibraryIndexProgress>();
    var result = await s.Service.ReadTicketLibraryIndexAsync(s.Settings, null, CancellationToken.None, diagnostic: diagnostics.Enqueue);
    Require(result.Complete && result.SearchRestored && result.Tickets.Count == 3, "Une requête GLPI active a été coupée par un délai absolu.");
    Require(diagnostics.Any(item => item.Phase == "all-status" && item.Category == "request-pending")
        && diagnostics.Any(item => item.Phase == "restore" && item.Category == "request-pending"), "L’attente active de Tous ou de la restauration n’a pas été expliquée.");
    Require(s.Requests.Count(url => Query(new Uri(url), "criteria[0][value]") == "all" && Query(new Uri(url), "start") == "") == 1,
        "La recherche Tous a été renvoyée après un ancien délai de navigation.");
    Require(s.Context.Pages.Count == 1 && s.Service.ActivePage == s.Search, "Une page lente a été conservée après la fin de l’index.");
}
async Task SlowNativeStatus()
{
    await using var s = await Setup(new() { NeedsStatus = true, StatusDelayMilliseconds = 6000 });
    var diagnostics = new ConcurrentQueue<LibraryIndexProgress>();
    var result = await s.Service.ReadTicketLibraryIndexAsync(s.Settings, null, CancellationToken.None, diagnostic: diagnostics.Enqueue);
    Require(result.Complete && result.SearchRestored && result.Tickets.Count == 3, "Le critère Statut a expiré pendant son chargement natif.");
    Require(s.Requests.Count(url => new Uri(url).AbsolutePath.EndsWith("/ajax/search.php", StringComparison.Ordinal)) == 1,
        "Le changement de critère a été renvoyé au lieu d’attendre son unique réponse.");
    Require(diagnostics.Any(item => item.Phase == "status" && item.Category == "request-pending"), "Progression du critère Statut absente.");
    Require(Query(new Uri(s.Requests.Last()), "criteria[0][field]") == "19", "Le critère précédent n’a pas été restauré.");
}
async Task DeferredStatusInitialization()
{
    // readyState becomes interactive while this external defer script is still
    // in flight. Only DOMContentLoaded proves its change handler is installed.
    await using var s = await Setup(new() { NeedsStatus = true, DeferredStatusHandler = true });
    var diagnostics = new ConcurrentQueue<LibraryIndexProgress>();
    var result = await s.Service.ReadTicketLibraryIndexAsync(s.Settings, null, CancellationToken.None, diagnostic: diagnostics.Enqueue);
    Require(result.Complete && result.SearchRestored && result.Tickets.Count == 3,
        "Le critère a été utilisé avant l’installation de son gestionnaire defer.");
    Require(s.Requests.Count(url => new Uri(url).AbsolutePath.EndsWith("/ajax/search.php", StringComparison.Ordinal)) == 1,
        "Le changement Statut a été perdu ou rejoué après l’initialisation du document.");
    Require(diagnostics.Any(item => item.Phase == "open-list" && item.Category == "request-pending"
        && item.Message.Contains("initialisation", StringComparison.Ordinal)), "L’attente du vrai DOMContentLoaded n’a pas été signalée.");
    Require(s.Context.Pages.Count == 1 && s.Service.ActivePage == s.Search,
        "La page initialisée lentement n’a pas été refermée après l’indexation.");
}
async Task SlowNativeResults()
{
    await using var s = await Setup(new() { ResultDelayMilliseconds = 6000 });
    var diagnostics = new ConcurrentQueue<LibraryIndexProgress>();
    var result = await s.Service.ReadTicketLibraryIndexAsync(s.Settings, null, CancellationToken.None, diagnostic: diagnostics.Enqueue);
    Require(result.Complete && result.Tickets.Count == 3, "Le rendu AJAX des résultats a expiré pendant une requête active.");
    Require(diagnostics.Any(item => item.Phase == "all-status" && item.Category == "request-pending"), "L’attente du rendu AJAX n’est pas visible.");
    Require(s.Requests.Count(url => new Uri(url).AbsolutePath.EndsWith("/ajax/search.php", StringComparison.Ordinal)) == 1,
        "Le chargement natif des résultats a été répété.");
}
async Task IndexHttpFailure()
{
    foreach (var status in new[] { 503, 403 })
    {
        await using var s = await Setup(new() { IndexStatus = status, RestoreStatus = status == 503 ? 500 : 0 });
        try { await Index(s); throw new Exception("Une erreur HTTP de l’index a été acceptée."); }
        catch (GlpiLibraryIndexException error)
        {
            Require(error.Phase == "all-status" && error.Category == (status == 403 ? "index-session" : "index-http-error")
                && error.Message.Contains("HTTP " + status, StringComparison.Ordinal), "L’erreur HTTP a été remplacée par un délai de fiche.");
        }
        Require(s.Requests.Count(url => Query(new Uri(url), "criteria[0][value]") == "all") == 1, "Une recherche échouée a été répétée.");
        Require(!string.Join('\n', s.Logs).Contains("NEVER_", StringComparison.Ordinal), "Le corps d’erreur GLPI a été écrit dans le journal.");
        if (status == 503) Require(s.Logs.Any(message => message.Contains("HTTP 503", StringComparison.Ordinal))
            && s.Logs.Any(message => message.Contains("HTTP 500", StringComparison.Ordinal)), "L’échec secondaire de restauration a masqué l’échec initial.");
        Require(s.Context.Pages.Count == 1 && s.Service.ActivePage == s.Search, "Page d’erreur conservée après l’arrêt.");
    }
}
async Task IndexNetworkFailure()
{
    await using var s = await Setup(new() { AbortIndex = true });
    try { await Index(s); throw new Exception("Une panne réseau de l’index a été acceptée."); }
    catch (GlpiLibraryIndexException error)
    { Require(error.Phase == "all-status" && error.Category == "index-network-error", "La panne réseau n’est pas identifiée à l’étape Tous."); }
    Require(s.Requests.Count(url => Query(new Uri(url), "criteria[0][value]") == "all") == 1, "La navigation interrompue a été renvoyée.");
    Require(s.Logs.Any(message => message.Contains("réseau", StringComparison.Ordinal)) && s.Context.Pages.Count == 1,
        "La cause réseau est masquée par la restauration ou une page temporaire reste ouverte.");
}
async Task IndexSessionRedirect()
{
    await using var s = await Setup(new() { RedirectIndex = true });
    try { await Index(s); throw new Exception("Une redirection d’authentification a été acceptée comme index."); }
    catch (GlpiLibraryIndexException error)
    { Require(error.Phase == "all-status" && error.Category == "index-session", "La redirection est classée comme délai de fiche."); }
    Require(s.Requests.Count(url => Query(new Uri(url), "criteria[0][value]") == "all") == 1 && s.Context.Pages.Count == 1,
        "La recherche a été répétée après une redirection ou sa page n’a pas été fermée.");
    Require(!string.Join('\n', s.Logs).Contains("NEVER_", StringComparison.Ordinal), "Le contenu de la page de connexion a été journalisé.");
}
async Task IndexNoProgress()
{
    await using var s = await Setup(new() { StalledIndex = true }); var timer = System.Diagnostics.Stopwatch.StartNew();
    try { await Index(s); throw new Exception("Un tableau sans progression a été accepté."); }
    catch (GlpiLibraryIndexException error)
    {
        Require(error.Phase == "all-status" && error.Category == "index-stalled"
            && error.Message.Contains("chargement", StringComparison.Ordinal),
            "Le blocage ne décrit pas l’état réel du tableau. Reçu : " + error.Phase + " / " + error.Category + " / " + error.Message);
    }
    Require(timer.Elapsed < TimeSpan.FromSeconds(15) && s.Requests.Any(url => url.Contains("action=fold_search", StringComparison.Ordinal)),
        "Le polling sans rapport a prolongé indéfiniment l’attente de l’index.");
    Require(s.Context.Pages.Count == 1, "Le tableau bloqué est resté ouvert.");
}
async Task CancelDuringRestore()
{
    await using var s = await Setup(new() { RestoreDelayMilliseconds = 10000 }); using var stop = new CancellationTokenSource();
    var restoring = false;
    void Diagnostic(LibraryIndexProgress notice)
    {
        if (notice.Phase == "restore" && notice.Category == "phase-start") { restoring = true; stop.CancelAfter(300); }
    }
    try { await s.Service.ReadTicketLibraryIndexAsync(s.Settings, null, stop.Token, diagnostic: Diagnostic); throw new Exception("Une annulation de restauration a été retournée comme succès."); }
    catch (OperationCanceledException) { }
    Require(restoring && s.Context.Pages.Count == 1 && s.Service.ActivePage == s.Search, "La requête de restauration annulée reste ouverte.");
    Require(s.Requests.Count(url => Query(new Uri(url), "criteria[0][value]") == "notold" && Query(new Uri(url), "reset") == "reset") == 1,
        "La restauration annulée a été répétée.");
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
async Task SkippedTabs()
{
    await using var s = await Setup(new() { SkippedTabs = true }); var result = await Detail(s);
    Require(result.Coverage.Complete && result.Coverage.TabsDiscovered == 4 && result.Coverage.TabsLoaded == 4 && result.Detail.Tabs.Count == 4, "Onglets ignorés comptés ou couverture refusée : " + string.Join(" | ", result.Coverage.Limitations));
    Require(!s.Requests.Any(url => url.Contains("common.tabs.php", StringComparison.Ordinal) && Query(new Uri(url), "_glpi_tab") is "TicketCost$1" or "-1"), "Onglet sans intérêt ouvert.");
    Require(!JsonSerializer.Serialize(result.Detail).Contains("NEVER_SKIPPED", StringComparison.Ordinal), "Contenu d’un onglet ignoré lu.");
    Require(s.Requests.Any(url => url.Contains("/front/ticket.form.php", StringComparison.Ordinal) && Query(new Uri(url), "forcetab") == "Ticket$main"),
        "La fiche n’est pas demandée directement sur son onglet Ticket (forcetab).");
    // GLPI 9.5 links solved tickets with forcetab=Ticket$2: the library replaces it instead of adding a second, ambiguous one.
    var solved = await s.Service.ReadTicketLibraryDetailsAsync(new("42", ticketUrl + "&forcetab=Ticket%242"), s.Settings, null, CancellationToken.None);
    var reopened = new Uri(s.Requests.Last(url => url.Contains("/front/ticket.form.php", StringComparison.Ordinal)));
    Require(solved.Coverage.Complete && Query(reopened, "forcetab") == "Ticket$main" && reopened.Query.Split("forcetab=").Length == 2,
        "Le forcetab d’un lien GLPI n’est pas remplacé : " + string.Join(" | ", solved.Coverage.Limitations));
    // After a collection, one click on the open ticket's form tab makes GLPI remember it again, without opening any ticket.
    var before = s.Requests.Count; await s.Service.RememberTicketFormTabAsync(new("42", ticketUrl), s.Settings, CancellationToken.None);
    Require(!s.Requests.Skip(before).Any(url => url.Contains("/front/ticket.form.php", StringComparison.Ordinal))
        && await s.Service.TicketPage!.EvaluateAsync<string>("document.querySelector('.ui-tabs-anchor[aria-selected=true]').id") == "overview-tab",
        "La fin de collecte rouvre une fiche ou ne revient pas sur l’onglet Ticket.");
}
async Task RememberedEmptyTab()
{
    // GLPI 9.5 reopens a ticket on the tab last clicked in the session; an empty one has nothing readable.
    await using var s = await Setup(new() { RememberedTab = "costs" }); s.Settings.TimeoutSeconds = 20; var result = await Detail(s);
    Require(result.Coverage.Complete && result.Coverage.TabsDiscovered == 4 && result.Coverage.TabsLoaded == 4, "Fiche rouverte sur un onglet vide non lue : " + string.Join(" | ", result.Coverage.Limitations));
    Require(result.Detail.Sections.Any(section => section.Data.Rows.Any(row => row.Any(cell => cell.Contains("Description intégrale synthétique", StringComparison.Ordinal)))), "Formulaire du ticket absent.");
    Require(!s.Requests.Any(url => url.Contains("common.tabs.php", StringComparison.Ordinal) && Query(new Uri(url), "_glpi_tab") is "TicketCost$1" or "-1") && await s.Service.TicketPage!.EvaluateAsync<int>("window.business+window.submitted") == 0, "Onglet ignoré ouvert ou action déclenchée.");
}
async Task RemoteFormTab()
{
    // GLPI keys the form tab Ticket$main in every language; here its label is German and GLPI reopens an empty tab.
    await using var s = await Setup(new() { RememberedTab = "costs", RemoteForm = true }); s.Settings.TimeoutSeconds = 20; var result = await Detail(s);
    Require(result.Coverage.Complete && result.Coverage.TabsDiscovered == 4 && result.Coverage.TabsLoaded == 4, "Onglet de fiche distant non reconnu : " + string.Join(" | ", result.Coverage.Limitations));
    Require(result.Detail.Sections.Any(section => section.Data.Rows.Any(row => row.Any(cell => cell.Contains("Description intégrale synthétique distante", StringComparison.Ordinal)))), "Formulaire distant absent.");
    Require(s.Requests.Count(url => url.Contains("common.tabs.php", StringComparison.Ordinal) && Query(new Uri(url), "_glpi_tab") == "Ticket$main") == 1,
        "L’onglet de la fiche, ouvert à la reprise, est relu par un second clic.");
}
async Task RememberedSkippedTab()
{
    // A readable tab the library does not keep (here the All view) is left for the ticket's form before anything is read.
    await using var s = await Setup(new() { RememberedTab = "all" }); var result = await Detail(s);
    Require(result.Coverage.Complete && result.Coverage.TabsDiscovered == 4 && result.Coverage.TabsLoaded == 4, "Fiche rouverte sur la vue Tous non lue : " + string.Join(" | ", result.Coverage.Limitations));
    Require(!JsonSerializer.Serialize(result.Detail).Contains("NEVER_SKIPPED", StringComparison.Ordinal), "Contenu de la vue Tous conservé.");
    Require(await s.Service.TicketPage!.EvaluateAsync<int>("window.tabReads") == 4, "Un onglet est chargé deux fois : traitement, deux pages d’historique et documents attendus.");
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
    public string NativeIdField { get; init; } = "";
    public string NativeIdOperator { get; init; } = "equals";
    public string NativeIdOperatorLabel { get; init; } = "est";
    public bool NativeIdSelect2 { get; init; }
    public (string Value, string Label, bool Disabled)[] AdditionalIdOperators { get; init; } = [];
    public bool DuplicateIdOperatorControl { get; init; }
    public string TargetOperatorDrift { get; init; } = "";
    public string MissingTargetId { get; init; } = "";
    public bool DriftTargetCriterion { get; init; }
    public bool OutsideTarget { get; init; }
    public bool ChangeTargetScope { get; init; }
    public bool ChangeBetweenTargetBatches { get; init; }
    public string CurrentEntity { get; set; } = "A";
    public bool MissingAll { get; init; }
    public bool NoModification { get; init; }
    public bool DuplicateModification { get; init; }
    public string Modified { get; init; } = "22/10/2026 14:30";
    public bool UnsafeNext { get; init; }
    public bool ChangeScope { get; init; }
    public bool Duplicate { get; init; }
    public bool Empty { get; init; }
    public bool DelayIndex { get; init; }
    public int IndexDelayMilliseconds { get; init; }
    public int RestoreDelayMilliseconds { get; init; }
    public int RestoreStatus { get; init; }
    public bool NeedsStatus { get; init; }
    public bool DeferredStatusHandler { get; init; }
    public int StatusDelayMilliseconds { get; init; }
    public int ResultDelayMilliseconds { get; init; }
    public int IndexStatus { get; init; }
    public bool AbortIndex { get; init; }
    public bool RedirectIndex { get; init; }
    public bool StalledIndex { get; init; }
    public bool ForeignTab { get; init; }
    public bool SkippedTabs { get; init; }
    public string RememberedTab { get; init; } = "";
    public bool RemoteForm { get; init; }
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
