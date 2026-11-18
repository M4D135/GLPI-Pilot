using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Playwright;

// The fixture changes native DOM references after the real adapter snapshot,
// not the host's result. Requests, personal POSTs and the CSV all use the real
// BrowserService / AutomaticExtractionService pipeline.
internal static class ColumnPreparationRaceBrowserChecks
{
    private const string Origin = "http://glpi-column-races.test";
    private const string Target = Origin + "/glpi/front/ticket.php?criteria%5B0%5D%5Bfield%5D=12&criteria%5B0%5D%5Bvalue%5D=1";
    private const string PersonalTab = "/glpi/ajax/common.tabs.php?_itemtype=DisplayPreference&_glpi_tab=DisplayPreference%242&displaytype=Ticket";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(IBrowser browser)
    {
        yield return ("Colonnes GLPI : contexte renouvelé · iframe renommée après inspection, collecte unique", () => Check(browser, "iframe-renewed"));
        yield return ("Colonnes GLPI : contexte renouvelé · tableau remplacé avant fermeture, aucun ancien repère attendu", () => Check(browser, "table-renewed"));
        yield return ("Colonnes GLPI : contexte renouvelé · deux tableaux de remplacement, collecte refusée", () => Check(browser, "table-ambiguous"));
        yield return ("Colonnes GLPI : activité native · corps personnel temporairement vide puis chargé en Ajax", () => Check(browser, "body-loading"));
        yield return ("Colonnes GLPI : activité native · lecture de préférences plus lente que le délai de liste", () => Check(browser, "slow-get"));
        yield return ("Colonnes GLPI : activité native · ajout encore en cours au-delà du délai de liste, un POST", () => Check(browser, "slow-post"));
        yield return ("Colonnes GLPI : activité native · refus HTTP 403 sans attente ni export", () => Check(browser, "http-failure"));
        yield return ("Colonnes GLPI : activité native · requête interrompue sans attente ni export", () => Check(browser, "network-failure"));
        yield return ("Colonnes GLPI : activité native · annulation pendant un ajout en cours, aucun renvoi", () => Check(browser, "cancel-post"));
        yield return ("Colonnes GLPI : activité native · requêtes de fond étrangères à la phase ignorées", () => Check(browser, "unrelated-polling"));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static string Html(string body) => "<!doctype html><html lang='fr'><meta charset='utf-8'><title>GLPI — Tickets synthétiques</title>"
        + "<style>body{font:14px sans-serif}.ui-dialog{position:fixed;inset:8px;background:white;z-index:10;padding:12px}iframe{width:900px;height:620px}"
        + "td,th{padding:6px}button,select{min-height:28px}[hidden]{display:none!important}</style><body>" + body + "</body></html>";

    private static string Hidden(string name, string value) => "<input type='hidden' name='" + name + "' value='" + value + "'>";
    private static string Form(string body) => "<form method='post' action='/glpi/front/displaypreference.form.php'>"
        + Hidden("itemtype", "Ticket") + Hidden("users_id", "17") + Hidden("_glpi_csrf_token", "synthetic") + body + "</form>";
    private static string Csv(bool priority) => "\uFEFFTitre;ID;Statut" + (priority ? ";Priorité" : "") + "\r\nExemple synthétique;7;Nouveau" + (priority ? ";Haute" : "") + "\r\n";

    private static Dictionary<string, string> Pairs(string? values) => (values ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part.Length > 1 ? part[1] : ""), StringComparer.Ordinal);

    private static async Task Check(IBrowser browser, string scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "GLPI-column-races-" + Guid.NewGuid().ToString("N"));
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        await using var service = new BrowserService();
        service.AttachForTesting(context, Target); // Includes the production default action timeout.
        var page = await context.NewPageAsync();
        using var cancellation = new CancellationTokenSource();
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var postFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var priority = false;
        var render = 0; var preferenceGets = 0; var tabGets = 0; var posts = 0; var exports = 0; var foreignRequests = 0; var unrelatedRequests = 0;
        var phases = new List<AutomaticRunPhase>();
        var diagnostics = new List<string>();
        service.Log += diagnostics.Add;

        string PersonalBody() => "<table><tr><th>" + Form("<input name='disable' type='submit' value='Désactiver'>")
            + "</th></tr><tr><td colspan='4'>" + Form("<select name='num'>" + (priority ? "" : "<option value='3'>Priorité</option>")
            + "</select><input name='add' type='submit' value='Ajouter'>") + "</td></tr><tr><td>Titre</td><td colspan='3'></td></tr>"
            + string.Concat(new[] { "ID", "Statut" }.Concat(priority ? new[] { "Priorité" } : Array.Empty<string>()).Select((label, index) =>
                "<tr><td>" + label + "</td><td>" + Form(Hidden("id", (700 + index).ToString()) + "<button name='purge' type='submit'>Supprimer</button>")
                + "</td><td></td><td></td></tr>")) + "</table>";

        string Preferences()
        {
            if (scenario == "unrelated-polling")
                return Html("<div id='tabsbody' aria-busy='true'>Chargement de la vue personnelle…</div>");
            if (scenario != "body-loading" || priority)
                return Html("<div id='tabsbody' data-priority='" + (priority ? "true" : "false") + "'>" + PersonalBody() + "</div>");
            // Let the host observe the real loading state once, then let the
            // fixture's ordinary native Ajax handler finish the personal view.
            return Html("<div id='tabsbody' aria-busy='true'>Chargement de la vue personnelle…</div><script>"
                + "const personalTab=" + JsonSerializer.Serialize(PersonalTab) + ";" + """
                let columnReader, started=false;
                Object.defineProperty(window,'__glpiPilotColumns',{
                  configurable:true,get(){return columnReader;},set(value){
                    const inspect=value.inspectPreferences;
                    value.inspectPreferences=function(){
                      const snapshot=inspect();
                      if(!started){
                        started=true;parent.sessionStorage.setItem('emptyBodyObserved','1');
                        fetch(personalTab).then(reply=>reply.text()).then(body=>{
                          const target=document.querySelector('#tabsbody');target.innerHTML=body;target.removeAttribute('aria-busy');
                        });
                      }
                      return snapshot;
                    };
                    columnReader=value;
                  }
                });
                </script>
                """);
        }

        string ListPage()
        {
            var number = ++render;
            return Html("<a href='/glpi/logout.php'>Déconnexion</a><form name='searchformTicket'>"
                + Hidden("criteria[0][field]", "12") + Hidden("criteria[0][value]", "1") + "</form><section id='ticket-results'>"
                + "<form id='massformTicket" + number + "' data-search-itemtype='Ticket' data-count='1' data-start='0' data-limit='20'>"
                + "<table class='tab_cadre_fixehov' id='search_" + number + "'><thead><tr><th>Titre</th><th>ID</th><th>Statut</th>"
                + (priority ? "<th>Priorité</th>" : "") + "</tr></thead><tbody><tr><td>Exemple synthétique</td><td>7</td><td>Nouveau</td>"
                + (priority ? "<td>Haute</td>" : "") + "</tr></tbody></table></form>"
                + "<div class='pager'><table class='tab_cadre_pager'><tr><td><form id='native-export' method='GET' action='/glpi/front/report.dynamic.php'>"
                + Hidden("item_type", "Ticket") + Hidden("start", "0") + Hidden("criteria[0][field]", "12") + Hidden("criteria[0][value]", "1")
                + "<select name='display_type' id='native-format'><option value='4'>Page courante en CSV</option><option value='-4'>Toutes les pages en CSV</option></select>"
                + "<button type='submit' name='export' id='native-submit' title='Exporter'>Exporter</button></form></td><td>De 1 à 1 sur 1</td></tr></table></div></section>"
                + "<span class='fa-wrench pointer' onclick=\"$('#search_config_top').dialog('open');\">Colonnes</span>"
                + "<div class='ui-dialog' role='dialog' id='dialog' hidden><button class='ui-dialog-titlebar-close'>Fermer</button>"
                + "<div id='search_config_top'><iframe id='Iframesearch_config_top'></iframe></div></div><script>"
                + "const scenario=" + JsonSerializer.Serialize(scenario) + ";" + """
                window.$=()=>({dialog:()=>{
                  sessionStorage.setItem('columnOpenerClicks',String(Number(sessionStorage.getItem('columnOpenerClicks')||0)+1));
                  document.querySelector('#dialog').hidden=false;
                  document.querySelector('iframe').src='/glpi/front/displaypreference.form.php?itemtype=Ticket';
                  if(scenario==='unrelated-polling'){
                    // A separate search widget keeps polling while the
                    // preferences document itself makes no further progress.
                    const poll=()=>fetch('/glpi/ajax/search.php?criteria[0][field]=99&criteria[0][value]=other');
                    poll();window.unrelatedSearchTimer=setInterval(poll,200);
                  }
                }});
                document.querySelector('.ui-dialog-titlebar-close').onclick=()=>{
                  clearInterval(window.unrelatedSearchTimer);
                  sessionStorage.setItem('columnCloseClicks',String(Number(sessionStorage.getItem('columnCloseClicks')||0)+1));
                  document.querySelector('#dialog').hidden=true;setTimeout(()=>location.reload(),180);
                };
                let columnReader, renewed=false;
                Object.defineProperty(window,'__glpiPilotColumns',{
                  configurable:true,get(){return columnReader;},set(value){
                    const inspect=value.inspect;
                    value.inspect=function(selector){
                      const snapshot=inspect(selector), frame=document.querySelector('iframe');
                      if(!renewed&&snapshot.iframeSelector){
                        if(scenario==='iframe-renewed'){
                          renewed=true;frame.id='Iframesearch_config_renewed';
                        }else if((scenario==='table-renewed'||scenario==='table-ambiguous')&&frame.contentDocument?.querySelector('#tabsbody[data-priority="true"]')){
                          renewed=true;
                          const table=document.querySelector('.tab_cadre_fixehov'), replacement=table.cloneNode(true);
                          replacement.id='search_background_refresh';table.replaceWith(replacement);
                          if(scenario==='table-ambiguous'){
                            const owner=replacement.closest('form').cloneNode(true);owner.id='massformTicket999';
                            owner.querySelector('table').id='search_background_duplicate';document.querySelector('#ticket-results').append(owner);
                          }
                        }
                        if(renewed)sessionStorage.setItem('columnRaceObserved','1');
                      }
                      return snapshot;
                    };
                    columnReader=value;
                  }
                });
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
                            "Only the offered native personal addition may be submitted, once.");
                        posts++; postStarted.TrySetResult();
                        try
                        {
                            if (scenario == "slow-post") await Task.Delay(6200);
                            if (scenario == "cancel-post") await releasePost.Task;
                            if (scenario == "http-failure") { await route.FulfillAsync(new() { Status = 403, ContentType = "text/html", Body = "Accès refusé" }); return; }
                            if (scenario == "network-failure") { await route.AbortAsync("failed"); return; }
                            priority = true;
                            await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Preferences() });
                        }
                        catch (PlaywrightException) when (scenario == "cancel-post" && cancellation.IsCancellationRequested)
                        {
                            // The cancelled host retires and closes its page;
                            // the fixture's late response then has no client.
                        }
                        finally { postFinished.TrySetResult(); }
                    }
                    else
                    {
                        preferenceGets++;
                        // Outlast both the native click's 7-second action
                        // budget and the 5-second list inactivity setting.
                        // The host must bind and observe the request itself.
                        if (scenario == "slow-get") await Task.Delay(12200);
                        await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Preferences() });
                    }
                }
                else if (uri.AbsolutePath.EndsWith("/ajax/common.tabs.php", StringComparison.Ordinal))
                {
                    var values = Pairs(uri.Query);
                    Require(route.Request.Method == "GET" && values.GetValueOrDefault("_glpi_tab") == "DisplayPreference$2"
                        && values.GetValueOrDefault("displaytype") == "Ticket", "Only the native personal Ticket view may load.");
                    tabGets++;
                    await Task.Delay(250);
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = PersonalBody() });
                }
                else if (uri.AbsolutePath.EndsWith("/ajax/search.php", StringComparison.Ordinal) && scenario == "unrelated-polling")
                {
                    unrelatedRequests++;
                    // Neither an unrelated 403 nor repeated successful
                    // background requests may poison/extend this phase.
                    await route.FulfillAsync(new() { Status = unrelatedRequests == 1 ? 403 : 200, ContentType = "application/json", Body = "{}" });
                }
                else if (uri.AbsolutePath.EndsWith("/front/report.dynamic.php", StringComparison.Ordinal))
                {
                    var values = Pairs(uri.Query);
                    Require(route.Request.Method == "GET" && values.GetValueOrDefault("display_type") == "-4"
                        && values.GetValueOrDefault("criteria[0][value]") == "1", "The all-pages export must keep the same search scope.");
                    exports++;
                    await route.FulfillAsync(new() { ContentType = "text/csv; charset=utf-8",
                        Headers = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"glpi.csv\"" }, Body = Csv(priority) });
                }
                else await route.AbortAsync();
            });
            await page.GotoAsync(Target);
            var automatic = new AutomaticExtractionService(service);
            automatic.PhaseChanged += phases.Add;
            var settings = new AppSettings
            {
                PortalUrl = Target, SelectorOrigin = Origin, AutoMode = "native", ColumnPreparationMode = "supervision",
                ExportFolder = folder, AutoSaveXlsx = false, ResumePagination = false, Cities = new() { Enabled = false },
                TimeoutSeconds = 10, ListTimeoutSeconds = 5, DownloadTimeoutSeconds = 10,
                ExpectedTotal = 1, MaxRows = 20, MaxPages = 5
            };
            var run = automatic.RunAsync(settings, null, cancellation.Token);
            if (scenario == "cancel-post")
            {
                await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                cancellation.Cancel();
                try { await run.WaitAsync(TimeSpan.FromSeconds(4)); throw new InvalidOperationException("Cancelling a live column request must stop the run."); }
                catch (OperationCanceledException)
                {
                    Require(posts == 1 && exports == 0 && !phases.Contains(AutomaticRunPhase.Ready),
                        "Cancellation must propagate promptly without retrying the personal POST or exporting.");
                }
                releasePost.TrySetResult();
                await postFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return;
            }
            if (scenario is "http-failure" or "network-failure")
            {
                await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Exception? failure = null;
                try { await run.WaitAsync(TimeSpan.FromSeconds(4)); }
                catch (InvalidOperationException error) { failure = error; }
                Require(failure is not null && posts == 1 && exports == 0 && !phases.Contains(AutomaticRunPhase.Ready),
                    "An explicit failed native request must stop promptly, without another POST or a successful export.");
                if (scenario == "http-failure") Require(failure!.Message.Contains("403", StringComparison.Ordinal), "The actual HTTP rejection must remain visible in the failure.");
                return;
            }
            if (scenario == "table-ambiguous")
            {
                Exception? failure = null;
                try { await run; }
                catch (InvalidOperationException error) { failure = error; }
                Require(failure is not null && failure.Message.Contains("tableau", StringComparison.OrdinalIgnoreCase)
                    && posts == 1 && exports == 0 && !phases.Contains(AutomaticRunPhase.Ready),
                    "Two replacement tables must be refused without choosing one or publishing a successful extraction.");
                Require(await page.EvaluateAsync<string>("sessionStorage.getItem('columnRaceObserved')||''") == "1", "The ambiguous replacement must actually occur.");
                return;
            }
            if (scenario == "unrelated-polling")
            {
                var collected = await run.WaitAsync(TimeSpan.FromSeconds(10));
                Require(unrelatedRequests >= 3 && posts == 0 && preferenceGets == 1 && tabGets == 0 && exports == 1,
                    "Background polling must not keep optional preparation alive or cause a false native failure.");
                Require(collected.Data.Provenance.ColumnPreparation is { State: "unavailable", AddedCount: 0, ColumnCount: 3 }
                    && File.Exists(collected.PrimaryPath) && await File.ReadAllTextAsync(collected.PrimaryPath) == Csv(false).TrimStart('\uFEFF')
                    && phases.Contains(AutomaticRunPhase.Ready),
                    "A stationary optional view must close and export the verified current columns, without claiming preparation succeeded.");
                return;
            }
            var result = await run;
            Require(posts == 1 && preferenceGets == 1 && tabGets == (scenario == "body-loading" ? 1 : 0) && exports == 1 && foreignRequests == 0,
                "Recovery or slow activity must finish each real request once, with no extra personal mutation or export.");
            Require(result.Data.Provenance.ColumnPreparation is { State: "ready", AddedCount: 1, ColumnCount: 4, SourceChanged: false }
                && result.Data.Rows.Count == 1 && result.Data.Headers.Count == 4 && phases.Contains(AutomaticRunPhase.Ready),
                "The prepared columns must be confirmed and the actual native CSV imported before publishing Ready.");
            Require(File.Exists(result.PrimaryPath) && await File.ReadAllTextAsync(result.PrimaryPath) == Csv(true).TrimStart('\uFEFF'),
                "The native export must be preserved intact.");
            Require(await page.EvaluateAsync<int>("Number(sessionStorage.getItem('columnOpenerClicks')||0)") == 1
                && await page.EvaluateAsync<int>("Number(sessionStorage.getItem('columnCloseClicks')||0)") == 1
                && !await page.Locator("#dialog").IsVisibleAsync() && render >= 3,
                "Recovery must open and close the native preferences once, then reread the refreshed list.");
            if (scenario is "iframe-renewed" or "table-renewed")
                Require(await page.EvaluateAsync<string>("sessionStorage.getItem('columnRaceObserved')||''") == "1", "The stale-reference race must actually occur.");
            if (scenario == "body-loading")
                Require(await page.EvaluateAsync<string>("sessionStorage.getItem('emptyBodyObserved')||''") == "1", "The empty Ajax state must actually be observed before loading completes.");
            if (scenario is "slow-get" or "slow-post")
                Require(diagnostics.Any(message => message.Contains("requête GLPI est encore en cours", StringComparison.Ordinal)),
                    "The native activity observer must report the still-pending request after the configured list delay, rather than hiding the wait inside a locator action.");
        }
        finally
        {
            releasePost.TrySetResult();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
