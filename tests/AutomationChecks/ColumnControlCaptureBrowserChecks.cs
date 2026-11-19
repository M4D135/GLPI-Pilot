using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Playwright;

// Each hook calls the real adapter first, changes only the fixture DOM, then
// returns the untouched capture. Validation results are observed, never faked.
internal static class ColumnControlCaptureBrowserChecks
{
    private const string Origin = "http://glpi-column-capture.test";
    private const string Target = Origin + "/glpi/front/ticket.php?criteria%5B0%5D%5Bfield%5D=12&criteria%5B0%5D%5Bvalue%5D=1";
    private const string PersonalTab = "/glpi/ajax/common.tabs.php?_itemtype=DisplayPreference&_glpi_tab=DisplayPreference%242&displaytype=Ticket";
    private static readonly string[] WindowRoles = ["opener", "iframe", "close", "table"];
    private static readonly string[] HiddenRoles = [.. WindowRoles, "select", "add"];

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(IBrowser browser)
    {
        yield return ("Colonnes GLPI : capture native · ouverture clonée et masquée après capture", () => Check(browser, "hidden-opener"));
        yield return ("Colonnes GLPI : capture native · iframe clonée et masquée après capture", () => Check(browser, "hidden-iframe"));
        yield return ("Colonnes GLPI : capture native · fermeture clonée et masquée après capture", () => Check(browser, "hidden-close"));
        yield return ("Colonnes GLPI : capture native · tableau cloné après capture, copie conservée après Ajax", () => Check(browser, "hidden-table"));
        yield return ("Colonnes GLPI : capture native · corps obsolète cloné avant choix Select2", () => Check(browser, "hidden-select"));
        yield return ("Colonnes GLPI : capture native · corps obsolète cloné avant ajout personnel", () => Check(browser, "hidden-add"));
        yield return ("Colonnes GLPI : capture native · onglet personnel cloné et masqué avant clic", () => Check(browser, "hidden-personal-tab"));
        yield return ("Colonnes GLPI : capture native · doublons visibles dans des racines shadow étrangères", () => Check(browser, "shadow-decoys"));
        yield return ("Colonnes GLPI : capture native · deux ouvertures actives après capture, aucun clic", () => Check(browser, "visible-ambiguity"));
        yield return ("Colonnes GLPI : capture native · remplacement global avant ajout, aucun POST", () => Check(browser, "global-replacement"));
        yield return ("Colonnes GLPI : capture native · clones répétés, deux collectes et un seul ajout", () => Check(browser, "repeat-hidden"));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static string Html(string body) => "<!doctype html><html lang='fr'><meta charset='utf-8'><title>GLPI — Tickets synthétiques</title>"
        + "<style>body{font:14px sans-serif}.ui-dialog{position:fixed;inset:8px;background:white;z-index:10;padding:12px}iframe{width:900px;height:620px}"
        + "td,th{padding:6px}button,select{min-height:28px}.select2-hidden-accessible{display:none}[hidden]{display:none!important}</style><body>" + body + "</body></html>";

    private static string Hidden(string name, string value) => "<input type='hidden' name='" + name + "' value='" + value + "'>";
    private static string Form(string body) => "<form method='post' action='/glpi/front/displaypreference.form.php'>"
        + Hidden("itemtype", "Ticket") + Hidden("users_id", "17") + Hidden("_glpi_csrf_token", "synthetic") + body + "</form>";
    private static string Csv(bool priority) => "\uFEFFTitre;ID;Statut" + (priority ? ";Priorité" : "") + "\r\nExemple synthétique;7;Nouveau" + (priority ? ";Haute" : "") + "\r\n";

    private static Dictionary<string, string> Pairs(string? values) => (values ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part.Length > 1 ? part[1] : ""), StringComparer.Ordinal);

    private static string CaptureHooks(string scenario) => "<script>const scenario=" + JsonSerializer.Serialize(scenario) + ";" + """
        const storage=window===parent?sessionStorage:parent.sessionStorage, changed=new Set();
        const bump=key=>storage.setItem(key,String(Number(storage.getItem(key)||0)+1));
        const roles=scenario==='repeat-hidden'?['opener','iframe','close','table','select','add']:
          scenario==='shadow-decoys'?['opener','iframe','close','table','personal-tab','select','add']:
          scenario.startsWith('hidden-')?[scenario.slice(7)]:[];
        const selectors={opener:'openerSelector',iframe:'iframeSelector',close:'closeSelector',table:'tableSelector',
          'personal-tab':'personalTabSelector',select:'selectSelector',add:'addSelector'};
        function forbidDecoy(node){
          node.dataset.captureDecoy='true';
          for(const event of ['click','change'])node.addEventListener(event,e=>{
            bump('captureDecoyActions');e.preventDefault();e.stopImmediatePropagation();
          },true);
        }
        function mutate(role,capture){
          const target=role==='table'?capture.table:capture.element, selector=capture.state[selectors[role]];
          if(!target||!selector||changed.has(role))return;
          if((role==='select'||role==='add')&&!capture.state.options?.some(option=>option.value==='3'))return;
          const visibleAmbiguity=scenario==='visible-ambiguity'&&role==='opener';
          const globalReplacement=scenario==='global-replacement'&&role==='add';
          if(!roles.includes(role)&&!visibleAmbiguity&&!globalReplacement)return;
          changed.add(role);
          if(globalReplacement){
            const body=target.closest('#tabsbody'), replacement=body.cloneNode(true);
            replacement.querySelectorAll('input[name="users_id"]').forEach(input=>input.value='0');
            body.hidden=true;body.after(replacement);
          }else{
            const source=role==='table'?target.closest('form'):
              (role==='select'||role==='add')?target.closest('#tabsbody'):target;
            const clone=source.cloneNode(true);forbidDecoy(clone);
            clone.removeAttribute('data-active-results');
            if(clone.matches('iframe'))clone.removeAttribute('src');
            if(scenario==='shadow-decoys'){
              const host=document.createElement('div');host.dataset.captureShadow=role;
              host.attachShadow({mode:'open'}).append(clone);document.body.append(host);
              storage.setItem('captureLightCount_'+role,String(document.querySelectorAll(selector).length));
              storage.setItem('captureShadowCount_'+role,String(host.shadowRoot.querySelectorAll('#'+CSS.escape(target.id)).length));
            }else{
              clone.hidden=!visibleAmbiguity;source.before(clone);
            }
          }
          bump('captureRace_'+role);
          const matches=document.querySelectorAll(selector);
          storage.setItem('captureOldSelectorLost_'+role,matches.length===1&&matches[0]===target?'0':'1');
        }
        function watch(capture,roles){
          const validate=capture.validate;
          capture.validate=function(){
            const result=validate();
            for(const role of roles)if(changed.has(role)){
              bump('captureValidated_'+role);
              if(result.sameElement&&(role!=='table'||result.sameTable))bump('captureSame_'+role);
            }
            return result;
          };
          return capture;
        }
        let reader;
        Object.defineProperty(window,'__glpiPilotColumns',{
          configurable:true,get(){return reader;},set(value){
            const captureWindow=value.captureWindow, capturePreferences=value.capturePreferences;
            value.captureWindow=function(selector,role){
              const capture=captureWindow(selector,role), watched=[role];
              // Closing captures the real table whose retirement the host
              // awaits; the obsolete copy must stay attached afterwards.
              if(role==='close'){mutate('table',capture);watched.push('table');}
              if(role!=='table')mutate(role,capture);
              return watch(capture,watched);
            };
            value.capturePreferences=function(role){
              const capture=capturePreferences(role);mutate(role,capture);return watch(capture,[role]);
            };
            reader=value;
          }
        });
        if(scenario==='shadow-decoys'&&window===parent){
          const opener=document.querySelector('#native-column-opener');
          mutate('opener',{element:opener,state:{openerSelector:'#native-column-opener'}});
        }
        </script>
        """;

    private static async Task Check(IBrowser browser, string scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "GLPI-column-capture-" + Guid.NewGuid().ToString("N"));
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        await using var service = new BrowserService();
        service.AttachForTesting(context, Target);
        var page = await context.NewPageAsync();
        var priority = false;
        var render = 0; var preferenceGets = 0; var tabGets = 0; var posts = 0; var invalidPosts = 0; var exports = 0; var foreignRequests = 0;
        var phases = new List<AutomaticRunPhase>();
        var select2 = scenario is "hidden-select" or "shadow-decoys" or "repeat-hidden";
        var tab = scenario is "hidden-personal-tab" or "shadow-decoys";

        string PersonalBody() => "<table><tr><th>" + Form("<input name='disable' type='submit' value='Désactiver'>")
            + "</th></tr><tr><td colspan='4'>" + Form("<select name='num' id='native-column-select'" + (select2 ? " class='select2-hidden-accessible'" : "") + ">"
            + "<option value=''>Choisir</option>" + (priority ? "" : "<option value='3'>Priorité</option>") + "</select>"
            + (select2 ? "<span class='select2-container'>Choisir une colonne</span>" : "") + "<input name='add' id='native-column-add' type='submit' value='Ajouter'>")
            + "</td></tr><tr><td>Titre</td><td colspan='3'></td></tr>"
            + string.Concat(new[] { "ID", "Statut" }.Concat(priority ? new[] { "Priorité" } : Array.Empty<string>()).Select((label, index) =>
                "<tr><td>" + label + "</td><td>" + Form(Hidden("id", (700 + index).ToString()) + "<button name='purge' type='submit'>Supprimer</button>")
                + "</td><td></td><td></td></tr>")) + "</table>";

        string Preferences() => Html((tab && !priority
            ? "<div id='tabspanel'><a id='native-personal-tab' href='" + PersonalTab + "'>Vue personnelle</a></div>"
                + "<div id='tabsbody'>" + Hidden("itemtype", "Ticket") + Hidden("users_id", "0") + "Vue globale</div>"
            : "<div id='tabsbody'>" + PersonalBody() + "</div>") + "<script>const personalTab=" + JsonSerializer.Serialize(PersonalTab) + ";" + """
            document.addEventListener('click',async event=>{
              const link=event.target.closest('#native-personal-tab');
              if(!link||link.closest('[data-capture-decoy]'))return;
              event.preventDefault();
              const response=await fetch(personalTab);
              document.querySelector('#tabsbody').innerHTML=await response.text();document.querySelector('#tabspanel').remove();
            });
            document.addEventListener('change',event=>{
              if(event.target.matches('select[name="num"]')&&!event.target.closest('[data-capture-decoy]'))
                parent.sessionStorage.setItem('captureNativeSelections',String(Number(parent.sessionStorage.getItem('captureNativeSelections')||0)+1));
            });
            </script>
            """ + CaptureHooks(scenario));

        string ListPage()
        {
            var number = ++render;
            return Html("<a href='/glpi/logout.php'>Déconnexion</a><form name='searchformTicket'>" + Hidden("criteria[0][field]", "12")
                + Hidden("criteria[0][value]", "1") + "</form><section id='ticket-results'><form data-active-results='true' id='massformTicket" + number
                + "' data-search-itemtype='Ticket' data-count='1' data-start='0' data-limit='20'><table class='tab_cadre_fixehov' id='search_" + number
                + "'><thead><tr><th>Titre</th><th>ID</th><th>Statut</th>" + (priority ? "<th>Priorité</th>" : "")
                + "</tr></thead><tbody><tr><td>Exemple synthétique</td><td>7</td><td>Nouveau</td>" + (priority ? "<td>Haute</td>" : "") + "</tr></tbody></table></form>"
                + "<div class='pager'><table class='tab_cadre_pager'><tr><td><form method='GET' action='/glpi/front/report.dynamic.php'>"
                + Hidden("item_type", "Ticket") + Hidden("start", "0") + Hidden("criteria[0][field]", "12") + Hidden("criteria[0][value]", "1")
                + "<select name='display_type'><option value='4'>Page courante en CSV</option><option value='-4'>Toutes les pages en CSV</option></select>"
                + "<button type='submit' name='export' title='Exporter'>Exporter</button></form></td><td>De 1 à 1 sur 1</td></tr></table></div></section>"
                + "<span id='native-column-opener' class='fa-wrench pointer' onclick=\"$('#search_config_top').dialog('open');\">Colonnes</span>"
                + "<div class='ui-dialog' role='dialog' id='dialog' hidden><button id='native-column-close' class='ui-dialog-titlebar-close'>Fermer</button>"
                + "<div id='search_config_top'><iframe id='Iframesearch_config_top'></iframe></div></div>" + """
                <script>
                const dialog=document.querySelector('#dialog'), nativeFrame=dialog.querySelector('iframe');
                window.$=()=>({dialog:()=>{
                  sessionStorage.setItem('captureOpenClicks',String(Number(sessionStorage.getItem('captureOpenClicks')||0)+1));
                  dialog.hidden=false;nativeFrame.src='/glpi/front/displaypreference.form.php?itemtype=Ticket';
                }});
                document.querySelector('#native-column-close').onclick=async()=>{
                  sessionStorage.setItem('captureCloseClicks',String(Number(sessionStorage.getItem('captureCloseClicks')||0)+1));
                  dialog.hidden=true;
                  const active=document.querySelector('form[data-active-results]');
                  const response=await fetch(location.href), fresh=new DOMParser().parseFromString(await response.text(),'text/html');
                  active.replaceWith(fresh.querySelector('form[data-active-results]'));
                  if(document.querySelector('form[data-capture-decoy]')?.isConnected&&!active.isConnected)
                    sessionStorage.setItem('captureRetainedTable','1');
                };
                </script>
                """ + CaptureHooks(scenario));
        }

        Task<int> Counter(string key) => page.EvaluateAsync<int>("key=>Number(sessionStorage.getItem(key)||0)", key);
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
                        posts++;
                        var values = Pairs(route.Request.PostData);
                        if (values.GetValueOrDefault("itemtype") != "Ticket" || values.GetValueOrDefault("users_id") != "17"
                            || values.GetValueOrDefault("num") != "3" || values.GetValueOrDefault("add") != "Ajouter" || priority) invalidPosts++;
                        else priority = true;
                    }
                    else preferenceGets++;
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Preferences() });
                }
                else if (uri.AbsolutePath.EndsWith("/ajax/common.tabs.php", StringComparison.Ordinal))
                {
                    var values = Pairs(uri.Query);
                    Require(route.Request.Method == "GET" && values.GetValueOrDefault("_glpi_tab") == "DisplayPreference$2"
                        && values.GetValueOrDefault("displaytype") == "Ticket", "Only the native personal Ticket tab may load.");
                    tabGets++;
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = PersonalBody() });
                }
                else if (uri.AbsolutePath.EndsWith("/front/report.dynamic.php", StringComparison.Ordinal))
                {
                    var values = Pairs(uri.Query);
                    Require(route.Request.Method == "GET" && values.GetValueOrDefault("display_type") == "-4"
                        && values.GetValueOrDefault("criteria[0][value]") == "1", "The native all-pages export must preserve the search.");
                    exports++;
                    await route.FulfillAsync(new() { ContentType = "text/csv; charset=utf-8",
                        Headers = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"glpi.csv\"" }, Body = Csv(priority) });
                }
                else await route.AbortAsync();
            });
            await page.GotoAsync(Target);
            if (scenario == "shadow-decoys")
            {
                Require(await page.EvaluateAsync<int>("document.querySelectorAll('#native-column-opener').length") == 1
                    && await page.Locator("#native-column-opener").CountAsync() == 2,
                    "The fixture must reproduce native DOM uniqueness versus Playwright CSS shadow piercing before collection.");
                await page.EvaluateAsync("sessionStorage.clear()");
            }
            var automatic = new AutomaticExtractionService(service);
            automatic.PhaseChanged += phases.Add;
            var settings = new AppSettings
            {
                PortalUrl = Target, SelectorOrigin = Origin, AutoMode = "native", ColumnPreparationMode = "supervision",
                ExportFolder = folder, AutoSaveXlsx = false, ResumePagination = false, Cities = new() { Enabled = false },
                TimeoutSeconds = 10, ListTimeoutSeconds = 5, DownloadTimeoutSeconds = 10, ExpectedTotal = 1, MaxRows = 20, MaxPages = 5
            };
            if (scenario is "visible-ambiguity" or "global-replacement")
            {
                Exception? failure = null;
                try { await automatic.RunAsync(settings, null, CancellationToken.None); }
                catch (InvalidOperationException error) { failure = error; }
                var role = scenario == "visible-ambiguity" ? "opener" : "add";
                Require(failure is not null && posts == 0 && invalidPosts == 0 && exports == 0 && !phases.Contains(AutomaticRunPhase.Ready)
                    && await Counter("captureRace_" + role) == 1 && await Counter("captureValidated_" + role) > 0
                    && await Counter("captureDecoyActions") == 0,
                    "Post-capture ambiguity or a replacement global view must be revalidated and refused before any personal POST, export or arbitrary click.");
                if (scenario == "visible-ambiguity") Require(preferenceGets == 0 && await Counter("captureOpenClicks") == 0
                    && failure!.Message.Contains("ouverture des colonnes", StringComparison.Ordinal)
                    && failure.Message.Contains("2", StringComparison.Ordinal),
                    "Neither active opener may be chosen; the failure must identify its native role and actual candidate count.");
                else Require(failure!.Message.Contains("vue personnelle", StringComparison.Ordinal),
                    "A global replacement must remain an explicit personal-scope refusal.");
                return;
            }
            var result = await automatic.RunAsync(settings, null, CancellationToken.None);
            Require(posts == 1 && invalidPosts == 0 && preferenceGets == 1 && tabGets == (tab ? 1 : 0) && exports == 1 && foreignRequests == 0,
                "Every captured native action must finish once, without a duplicate personal mutation or foreign request.");
            Require(result.Data.Provenance.ColumnPreparation is { State: "ready", AddedCount: 1, ColumnCount: 4, SourceChanged: false }
                && result.Data.Rows.Count == 1 && phases.Contains(AutomaticRunPhase.Ready)
                && File.Exists(result.PrimaryPath) && await File.ReadAllTextAsync(result.PrimaryPath) == Csv(true).TrimStart('\uFEFF'),
                "Captured native controls must produce verified columns and the actual intact CSV before publishing Ready.");
            Require(await Counter("captureOpenClicks") == 1 && await Counter("captureCloseClicks") == 1
                && await Counter("captureNativeSelections") == 1 && await Counter("captureDecoyActions") == 0
                && !await page.Locator("#dialog").IsVisibleAsync(), "The real controls must be used once and every decoy must remain untouched.");
            string[] roles = scenario == "repeat-hidden" ? HiddenRoles : scenario == "shadow-decoys" ? [.. HiddenRoles, "personal-tab"] : [scenario[7..]];
            foreach (var role in roles)
            {
                Require(await Counter("captureRace_" + role) == 1 && await Counter("captureValidated_" + role) > 0
                    && await Counter("captureSame_" + role) > 0,
                    "The " + role + " fixture must actually change the DOM after capture and preserve the real node through semantic validation.");
                if (scenario == "shadow-decoys") Require(await Counter("captureLightCount_" + role) == 1 && await Counter("captureShadowCount_" + role) == 1,
                    "A visible shadow decoy must coexist with exactly one native light-DOM " + role + " target.");
                else Require(await Counter("captureOldSelectorLost_" + role) == 1,
                    "The old " + role + " selector must cease to identify its captured node uniquely.");
            }
            if (scenario is "hidden-table" or "repeat-hidden")
                Require(await Counter("captureRetainedTable") == 1 && await page.Locator("form[data-capture-decoy]").CountAsync() == 1,
                    "The obsolete hidden table must stay attached after the captured active table is replaced.");
            if (scenario == "repeat-hidden")
            {
                phases.Clear();
                var repeated = await automatic.RunAsync(settings, null, CancellationToken.None);
                Require(posts == 1 && invalidPosts == 0 && preferenceGets == 2 && exports == 2 && foreignRequests == 0
                    && repeated.Data.Provenance.ColumnPreparation is { State: "ready", AddedCount: 0, ColumnCount: 4, SourceChanged: false }
                    && repeated.Data.Rows.Count == 1 && phases.Contains(AutomaticRunPhase.Ready),
                    "The second collection must keep persisted personal columns and perform one new export without a repeated POST.");
                Require(await Counter("captureOpenClicks") == 2 && await Counter("captureCloseClicks") == 2
                    && await Counter("captureNativeSelections") == 1 && await Counter("captureDecoyActions") == 0
                    && File.Exists(result.PrimaryPath) && File.Exists(repeated.PrimaryPath)
                    && await File.ReadAllTextAsync(result.PrimaryPath) == Csv(true).TrimStart('\uFEFF')
                    && await File.ReadAllTextAsync(repeated.PrimaryPath) == Csv(true).TrimStart('\uFEFF'),
                    "Both native CSV receipts must remain intact and no stale control or persisted option may be invoked twice.");
                foreach (var role in HiddenRoles)
                    Require(await Counter("captureRace_" + role) == (WindowRoles.Contains(role) ? 2 : 1),
                        "The repeated collection must independently exercise its " + role + " capture without repeating a personal addition.");
            }
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
