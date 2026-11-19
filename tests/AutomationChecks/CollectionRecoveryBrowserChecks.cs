using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Playwright;

internal static class CollectionRecoveryBrowserChecks
{
    private const string Origin = "http://glpi.test";
    private const string Target = Origin + "/glpi/front/ticket.php?criteria%5B0%5D%5Bfield%5D=12&criteria%5B0%5D%5Bvalue%5D=1";
    private const string TabUrl = "/glpi/ajax/common.tabs.php?_itemtype=DisplayPreference&_glpi_tab=DisplayPreference%242&displaytype=Ticket";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(IBrowser browser)
    {
        yield return ("Colonnes GLPI : reprise collecte · onglet personnel renouvelé entre deux lectures, un clic et export natif", () => CheckTab(browser, "renewed"));
        yield return ("Colonnes GLPI : reprise collecte · vue personnelle déjà chargée à la relecture, aucun clic d’onglet", () => CheckTab(browser, "already-active"));
        yield return ("Colonnes GLPI : reprise collecte · dialogue déjà ouvert après échec, reprise sans seconde ouverture", () => CheckTab(browser, "already-open"));
        yield return ("Colonnes GLPI : reprise collecte · onglet devenu menu compact, contrôle retiré pendant le choix", () => CheckTab(browser, "compact"));
        yield return ("Colonnes GLPI : reprise collecte · onglet disparu, fermeture native et export de la vue actuelle", () => CheckTab(browser, "missing"));
        yield return ("Colonnes GLPI : reprise collecte · lien devenu global, aucune commande globale et export actuel", () => CheckTab(browser, "global"));
        yield return ("Colonnes GLPI : reprise collecte · lien devenu étranger, aucune requête étrangère et export actuel", () => CheckTab(browser, "foreign"));
        yield return ("Colonnes GLPI : reprise collecte · critères modifiés avant le repli, aucune fermeture ni export", () => CheckTab(browser, "scope-change"));
        yield return ("Colonnes GLPI : reprise export · lecture par empreinte renouvelle le sélecteur avant export natif", () => CheckTable(browser, "after-read"));
        yield return ("Colonnes GLPI : reprise export · tableau renouvelé juste avant inspection, export natif unique", () => CheckTable(browser, "before-inspect"));
        yield return ("Colonnes GLPI : reprise export · deux tableaux de même empreinte, aucun export arbitraire", () => CheckTable(browser, "ambiguous"));
        yield return ("Colonnes GLPI : reprise export · tableau aux colonnes différentes, aucun export d’un autre tableau", () => CheckTable(browser, "different-headers"));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static AppSettings Settings(string folder) => new()
    {
        PortalUrl = Target, SelectorOrigin = Origin, AutoMode = "native", ColumnPreparationMode = "supervision",
        ExportFolder = folder, AutoSaveXlsx = false, ResumePagination = false, Cities = new() { Enabled = false },
        TimeoutSeconds = 10, ListTimeoutSeconds = 5, DownloadTimeoutSeconds = 10,
        ExpectedTotal = 1, MaxRows = 20, MaxPages = 5
    };

    private static string Html(string body) => "<!doctype html><html lang='fr'><meta charset='utf-8'><title>GLPI — Tickets synthétiques</title>"
        + "<style>body{font:14px sans-serif}.ui-dialog{position:fixed;inset:8px;background:white;z-index:10;padding:12px}iframe{width:900px;height:620px}"
        + "td,th{padding:6px}button,select{min-height:28px}[hidden]{display:none!important}</style><body>" + body + "</body></html>";

    private static string Hidden(string name, string value) => "<input type='hidden' name='" + name + "' value='" + value + "'>";

    private static string ExportForm() => "<div class='pager'><table class='tab_cadre_pager'><tr><td>"
        + "<form id='native-export' method='GET' action='/glpi/front/report.dynamic.php'>"
        + Hidden("item_type", "Ticket") + Hidden("start", "0") + Hidden("criteria[0][field]", "12") + Hidden("criteria[0][value]", "1")
        + "<select name='display_type' id='native-format'><option value='4'>Page courante en CSV</option><option value='-4'>Toutes les pages en CSV</option></select>"
        + "<button type='submit' name='export' id='native-submit' title='Exporter'>Exporter</button></form></td><td>De 1 à 1 sur 1</td></tr></table></div>";

    private static string Table(string id, bool priority) => "<form id='massformTicket" + id + "' data-search-itemtype='Ticket' data-count='1' data-start='0' data-limit='20'>"
        + "<table class='tab_cadre_fixehov' id='search_" + id + "'><thead><tr><th>Titre</th><th>ID</th><th>Statut</th>" + (priority ? "<th>Priorité</th>" : "")
        + "</tr></thead><tbody><tr><td>Exemple synthétique</td><td>7</td><td>Nouveau</td>" + (priority ? "<td>Haute</td>" : "") + "</tr></tbody></table></form>";

    private static string Csv(bool priority) => "\uFEFFTitre;ID;Statut" + (priority ? ";Priorité" : "") + "\r\nExemple synthétique;7;Nouveau" + (priority ? ";Haute" : "") + "\r\n";

    private static Task SendCsv(IRoute route, bool priority) => route.FulfillAsync(new()
    {
        ContentType = "text/csv; charset=utf-8",
        Headers = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"glpi.csv\"" },
        Body = Csv(priority)
    });

    private static Dictionary<string, string> Pairs(string? values) => (values ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part.Length > 1 ? part[1] : ""), StringComparer.Ordinal);

    private static async Task CheckTab(IBrowser browser, string scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "GLPI-tab-recovery-" + Guid.NewGuid().ToString("N"));
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        await using var service = new BrowserService();
        service.AttachForTesting(context, Target);
        var page = await context.NewPageAsync();
        var priority = false;
        var render = 0; var preferenceReads = 0; var tabGets = 0; var posts = 0; var exports = 0; var foreignRequests = 0;
        var shouldPrepare = scenario is "renewed" or "already-active" or "already-open" or "compact";
        var phases = new List<AutomaticRunPhase>();
        string Form(string body) => "<form method='post' action='/glpi/front/displaypreference.form.php'>"
            + Hidden("itemtype", "Ticket") + Hidden("users_id", "17") + Hidden("_glpi_csrf_token", "synthetic") + body + "</form>";
        string PersonalBody() => "<table><tr><th>" + Form("<input name='disable' type='submit' value='Désactiver'>") + "</th></tr><tr><td colspan='4'>"
            + Form("<select name='num'>" + (priority ? "" : "<option value='3'>Priorité</option>") + "</select><input name='add' type='submit' value='Ajouter'>")
            + "</td></tr><tr><td>Titre</td><td colspan='3'></td></tr>"
            + string.Concat(new[] { "ID", "Statut" }.Concat(priority ? new[] { "Priorité" } : Array.Empty<string>()).Select((label, index) =>
                "<tr><td>" + label + "</td><td>" + Form(Hidden("id", (700 + index).ToString()) + "<button name='purge' type='submit'>Supprimer</button>") + "</td><td></td><td></td></tr>")) + "</table>";
        string Preferences(bool initial)
        {
            if (!initial) return Html("<div id='tabsbody'>" + PersonalBody() + "</div>");
            // The hook changes only the fixture DOM after a real adapter snapshot.
            // The following host read must discover the native replacement itself.
            return Html("<div id='tabspanel'><a id='personal_tab_before' href='" + TabUrl + "'>Vue personnelle</a></div>"
                + "<div id='tabsbody'>" + Hidden("itemtype", "Ticket") + Hidden("users_id", "0") + "Vue globale</div><script>"
                + "const scenario=" + JsonSerializer.Serialize(scenario) + ", personalBody=" + JsonSerializer.Serialize(PersonalBody()) + ";"
                + "const nativeTabUrl=" + JsonSerializer.Serialize(TabUrl) + ";"
                + """
                let reader, rerendered = false;
                async function openPersonal(){
                  const reply=await fetch(nativeTabUrl);
                  document.querySelector('#tabsbody').innerHTML=await reply.text();
                }
                document.addEventListener('click',event=>{
                  const link=event.target.closest('a[href]');
                  if(!link)return;
                  event.preventDefault();
                  if(link.getAttribute('href')===nativeTabUrl)openPersonal();
                  else parent.sessionStorage.setItem('unexpectedTabClicks',String(Number(parent.sessionStorage.getItem('unexpectedTabClicks')||0)+1));
                });
                Object.defineProperty(window,'__glpiPilotColumns',{
                  configurable:true,get(){return reader;},set(value){
                    function observe(snapshot){
                      if(!rerendered&&snapshot.personalTabSelector&&!snapshot.personalScope){
                        rerendered=true;
                        const link=document.querySelector('#personal_tab_before');
                        if(scenario==='already-active'||scenario==='already-open'){
                          document.querySelector('#tabsbody').innerHTML=personalBody;link.remove();
                        }else if(scenario==='renewed'){
                          const replacement=link.cloneNode(true);replacement.id='personal_tab_after';link.replaceWith(replacement);
                        }else if(scenario==='compact'){
                          link.hidden=true;
                          const select=document.createElement('select');select.id='tabspanel-select';
                          select.innerHTML='<option value="-1">Choisir</option><option value="0">Vue personnelle</option>';
                          document.querySelector('#tabspanel').after(select);
                          select.addEventListener('change',()=>{select.remove();openPersonal();});
                        }else if(scenario==='global'){
                          link.href=nativeTabUrl.replace('DisplayPreference%242','DisplayPreference%241');
                        }else if(scenario==='foreign'){
                          link.href='http://other-glpi.test'+nativeTabUrl;
                        }else{
                          link.remove();
                          if(scenario==='scope-change')parent.document.querySelector('input[name="criteria[0][value]"]').value='5';
                        }
                      }
                      return snapshot;
                    }
                    const inspect=value.inspectPreferences, capture=value.capturePreferences;
                    value.inspectPreferences=()=>observe(inspect());
                    value.capturePreferences=function(role){const result=capture(role);observe(result.state);return result;};
                    reader=value;
                  }
                });
                </script>
                """);
        }
        string ListPage()
        {
            var currentRender = ++render;
            // OpenAsync always navigates to the requested search before
            // preparation. Present the already-open dialog on that second
            // document, not on the initial document that navigation replaces.
            var alreadyOpen = scenario == "already-open" && currentRender == 2;
            return Html("<a href='/glpi/logout.php'>Déconnexion</a><form name='searchformTicket'>"
                + Hidden("criteria[0][field]", "12") + Hidden("criteria[0][value]", "1") + "</form><section id='ticket-results'>"
                + Table(currentRender.ToString(), priority) + ExportForm() + "</section>"
                + "<span class='fa-wrench pointer' onclick=\"$('#search_config_top').dialog('open');\">Colonnes</span>"
                + "<div class='ui-dialog' role='dialog' id='dialog'" + (alreadyOpen ? "" : " hidden") + "><button class='ui-dialog-titlebar-close'>Fermer</button>"
                + "<div id='search_config_top'><iframe id='Iframesearch_config_top'" + (alreadyOpen ? " src='/glpi/front/displaypreference.form.php?itemtype=Ticket'" : "") + "></iframe></div></div>"
                + """
              <script>
              window.$=()=>({dialog:()=>{
                sessionStorage.setItem('columnOpenerClicks',String(Number(sessionStorage.getItem('columnOpenerClicks')||0)+1));
                document.querySelector('#dialog').hidden=false;
                document.querySelector('iframe').src='/glpi/front/displaypreference.form.php?itemtype=Ticket';
              }});
              document.querySelector('.ui-dialog-titlebar-close').onclick=()=>{
                document.querySelector('#dialog').hidden=true;setTimeout(()=>location.reload(),180);
              };
              </script>
              """);
        }
        try
        {
            await context.RouteAsync("**/*", async route =>
            {
                var uri = new Uri(route.Request.Url);
                if (uri.GetLeftPart(UriPartial.Authority) != Origin) { foreignRequests++; await route.AbortAsync(); return; }
                if (uri.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal))
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = ListPage() });
                else if (uri.AbsolutePath.EndsWith("/front/displaypreference.form.php", StringComparison.Ordinal))
                {
                    if (route.Request.Method == "POST")
                    {
                        var values = Pairs(route.Request.PostData);
                        Require(values.GetValueOrDefault("itemtype") == "Ticket" && values.GetValueOrDefault("users_id") == "17"
                            && values.GetValueOrDefault("num") == "3" && values.GetValueOrDefault("add") == "Ajouter" && !priority,
                            "Only one offered column may be added to the personal Ticket view.");
                        posts++; priority = true;
                    }
                    else preferenceReads++;
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Preferences(!priority) });
                }
                else if (uri.AbsolutePath.EndsWith("/ajax/common.tabs.php", StringComparison.Ordinal))
                {
                    var values = Pairs(uri.Query);
                    Require(route.Request.Method == "GET" && values.GetValueOrDefault("_glpi_tab") == "DisplayPreference$2"
                        && values.GetValueOrDefault("displaytype") == "Ticket", "Only the observed personal Ticket tab may be requested.");
                    tabGets++;
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = PersonalBody() });
                }
                else if (uri.AbsolutePath.EndsWith("/front/report.dynamic.php", StringComparison.Ordinal))
                {
                    var values = Pairs(uri.Query);
                    Require(route.Request.Method == "GET" && values.GetValueOrDefault("display_type") == "-4"
                        && values.GetValueOrDefault("criteria[0][value]") == "1", "The actual all-pages native export must preserve the search.");
                    exports++; await SendCsv(route, priority);
                }
                else await route.AbortAsync();
            });
            await page.GotoAsync(Target);
            var automatic = new AutomaticExtractionService(service);
            automatic.PhaseChanged += phases.Add;
            var settings = Settings(folder);
            if (scenario == "scope-change")
            {
                try { await automatic.RunAsync(settings, null, CancellationToken.None); throw new Exception("A modified search must stop before closing its dialog."); }
                catch (InvalidOperationException error)
                {
                    Require(error.Message.Contains("critères", StringComparison.Ordinal) && exports == 0 && posts == 0 && tabGets == 0,
                        "Search changes must stop native collection before any preference or export request.");
                    Require(await page.Locator("#dialog").IsVisibleAsync()
                        && await page.Locator("form[name=searchformTicket] input[name='criteria[0][value]']").InputValueAsync() == "5",
                        "Native close must not reload away the modified, unsubmitted search.");
                    Require(!phases.Contains(AutomaticRunPhase.Ready), "A refused collection must not publish Ready.");
                }
                return;
            }
            var result = await automatic.RunAsync(settings, null, CancellationToken.None);
            var receipt = result.Data.Provenance.ColumnPreparation;
            Require(exports == 1 && posts == (shouldPrepare ? 1 : 0) && tabGets == (scenario is "renewed" or "compact" ? 1 : 0)
                && foreignRequests == 0 && await page.EvaluateAsync<int>("Number(sessionStorage.getItem('unexpectedTabClicks')||0)") == 0,
                "Tab recovery must submit each native command once, with no global or foreign action.");
            Require(receipt is not null && receipt.State == (shouldPrepare ? "ready" : "unavailable")
                && receipt.AddedCount == (shouldPrepare ? 1 : 0), "Preparation must report its actual outcome without blocking a safe native export.");
            var dialogVisible = await page.Locator("#dialog").IsVisibleAsync();
            var preferenceFrames = page.Frames.Count(frame => frame.Url.Contains("/displaypreference.form.php", StringComparison.Ordinal));
            var openerClicks = await page.EvaluateAsync<int>("Number(sessionStorage.getItem('columnOpenerClicks')||0)");
            Require(preferenceReads == 1 && render >= (scenario == "already-open" ? 3 : 2) && !dialogVisible
                && preferenceFrames == 0 && openerClicks == (scenario == "already-open" ? 0 : 1),
                $"The native dialog must close and really reload the search before export. Preference GETs={preferenceReads}, list renders={render}, dialog visible={dialogVisible}, preference frames={preferenceFrames}, opener clicks={openerClicks}.");
            Require(result.Data.Rows.Count == 1 && result.Data.Headers.Count == (shouldPrepare ? 4 : 3)
                && File.Exists(result.PrimaryPath) && await File.ReadAllTextAsync(result.PrimaryPath) == Csv(priority).TrimStart('\uFEFF')
                && phases.Contains(AutomaticRunPhase.Ready), "The real native CSV must be preserved and imported before Ready.");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task CheckTable(IBrowser browser, string scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "GLPI-table-recovery-" + Guid.NewGuid().ToString("N"));
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        await using var service = new BrowserService();
        service.AttachForTesting(context, Target);
        var page = await context.NewPageAsync();
        var exports = 0;
        try
        {
            await context.RouteAsync("**/*", async route =>
            {
                var uri = new Uri(route.Request.Url);
                if (uri.GetLeftPart(UriPartial.Authority) != Origin) { await route.AbortAsync(); return; }
                if (uri.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal))
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Html("<section id='ticket-results'>" + Table("1", false) + ExportForm() + "</section>") });
                else if (uri.AbsolutePath.EndsWith("/front/report.dynamic.php", StringComparison.Ordinal)) { exports++; await SendCsv(route, false); }
                else await route.AbortAsync();
            });
            await page.GotoAsync(Target);
            var settings = Settings(folder); settings.ColumnPreparationMode = "current";
            var original = await service.ChooseAutoTableAsync(page, settings, CancellationToken.None);
            await page.EvaluateAsync("""
                scenario=>{
                  const old=document.querySelector('#search_1'), replacement=old.cloneNode(true);
                  replacement.id='search_2';old.replaceWith(replacement);
                  if(scenario==='ambiguous'){
                    const form=replacement.closest('form').cloneNode(true);form.id='massformTicket2';
                    form.querySelector('table').id='search_3';document.querySelector('#ticket-results').append(form);
                  }
                  if(scenario==='different-headers')replacement.querySelector('th').textContent='Autre colonne';
                }
                """, scenario);
            if (scenario == "after-read")
            {
                var sample = await service.ReadVerificationSampleAsync(original, CancellationToken.None);
                Require(sample.Rows.Count == 1 && original.Selector == "#search_2", "Reading the renewed table by exact headers must also refresh its stored selector.");
            }
            if (scenario is "ambiguous" or "different-headers")
            {
                var refused = false;
                try { await service.ExportAllNativeAsync(original, settings, CancellationToken.None); }
                catch (Exception error) when (error is InvalidOperationException or PlaywrightException)
                { refused = error.Message.Contains("tableau", StringComparison.OrdinalIgnoreCase); }
                Require(refused && exports == 0 && (!Directory.Exists(folder) || !Directory.EnumerateFiles(folder).Any()),
                    "An ambiguous or changed table must be refused before any export or file creation.");
                return;
            }
            var inspection = await service.InspectNativeExportAsync(original, CancellationToken.None);
            Require(inspection.Supported && inspection.Plans.Count == 1 && original.Selector == "#search_2",
                "Native inspection must use the uniquely renewed table in its original frame.");
            var download = await service.ExportAllNativeAsync(original, settings, CancellationToken.None);
            Require(exports == 1 && File.Exists(download.Path) && await File.ReadAllTextAsync(download.Path) == Csv(false).TrimStart('\uFEFF'),
                "Exactly one native export from the renewed table must be captured intact.");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

}
