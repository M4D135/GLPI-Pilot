using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Playwright;

internal static class ColumnPreparationBrowserChecks
{
    private const string Origin = "http://glpi.test";
    private const string Target = Origin + "/glpi/front/ticket.php?criteria%5B0%5D%5Bfield%5D=12&criteria%5B0%5D%5Bvalue%5D=1";
    private static readonly (string Id, string Label, string Group)[] Choices =
    {
        ("2", "ID", "Caractéristiques"), ("12", "Statut", "Caractéristiques"), ("3", "Priorité", "Caractéristiques"),
        ("8", "Groupe de techniciens", "Attribué à"), ("64", "Rédacteur", "Suivis"), ("65", "Rédacteur", "Tâches"),
        ("900", "Champ complémentaire", "Extension")
    };

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(IBrowser browser)
    {
        yield return ("Colonnes GLPI : activation personnelle legacy, ajouts supervision et tableau régénéré", () => Check(browser, "legacy"));
        yield return ("Colonnes GLPI : iframe 11 réutilisé, vraie relecture GET et persistance", () => Check(browser, "modern"));
        yield return ("Colonnes GLPI : option toutes traite aussi les colonnes sans rôle de supervision", () => Check(browser, "all"));
        yield return ("Colonnes GLPI : HTTP 200 et DOM optimiste sans persistance sont refusés", () => Check(browser, "not-persisted"));
        yield return ("Colonnes GLPI : vue globale seule préservée, collecte actuelle explicitement signalée", () => Check(browser, "global-only"));
        yield return ("Colonnes GLPI : changement de critères arrête les ajouts avant la collecte", () => Check(browser, "scope-change"));
        yield return ("Colonnes GLPI : annulation entre deux ajouts n’exécute pas le suivant", () => Check(browser, "cancel"));
        yield return ("Colonnes GLPI : mode vue actuelle n’ouvre aucune préférence", () => Check(browser, "current"));
        yield return ("Colonnes GLPI : contrôles renouvelés avant et pendant le choix, ajout unique", () => Check(browser, "renewed"));
        yield return ("Colonnes GLPI : option disparue avant clic, repli après vrai rechargement legacy", () => Check(browser, "missing"));
        yield return ("Colonnes GLPI : option disparue, collecte automatique complète et fichier conservés", () => Check(browser, "missing-automatic"));
        yield return ("Colonnes GLPI : option disparue après ajout confirmé, collecte partielle explicite", () => Check(browser, "missing-after-add"));
        yield return ("Colonnes GLPI : le repli ne masque jamais un ajout précédent non persisté", () => Check(browser, "missing-after-unpersisted"));
        yield return ("Colonnes GLPI : critères modifiés pendant la fermeture du repli, collecte arrêtée", () => Check(browser, "missing-scope-close"));
        yield return ("Colonnes GLPI : critères modifiés avant repli, aucune fermeture qui les efface", () => Check(browser, "missing-scope-before-close"));
        yield return ("Colonnes GLPI : option déjà présente après relecture, aucun doublon", () => Check(browser, "already-present"));
        yield return ("Colonnes GLPI : option déjà visible mais non persistée, relecture GET obligatoire", () => Check(browser, "already-unpersisted"));
        yield return ("Colonnes GLPI : identité personnelle modifiée avant clic, aucun repli", () => Check(browser, "user-change"));
        yield return ("Colonnes GLPI : annulation au moment du repli, aucun faux succès", () => Check(browser, "missing-cancel"));
        yield return ("Colonnes GLPI : refus avant clic préservé par l’attente native sans envoi", () => CheckBeforeClickWaiter(browser));
        foreach (var check in CollectionRecoveryBrowserChecks.Cases(browser)) yield return check;
        foreach (var check in ColumnPreparationRaceBrowserChecks.Cases(browser)) yield return check;
        foreach (var check in ColumnControlCaptureBrowserChecks.Cases(browser)) yield return check;
        foreach (var check in TemporaryColumnRestorationBrowserChecks.Cases(browser)) yield return check;
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private sealed class ImmediateProgress(Action<ExtractionProgress> report) : IProgress<ExtractionProgress>
    { public void Report(ExtractionProgress value) => report(value); }
    private sealed class BeforeNativeClickException : InvalidOperationException;

    private static async Task CheckBeforeClickWaiter(IBrowser browser)
    {
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var sent = 0;
        page.Request += (_, request) => { if (request.Method == "POST") sent++; };
        var expected = new BeforeNativeClickException();
        try
        {
            await page.RunAndWaitForResponseAsync(() => Task.FromException(expected), response => response.Request.Method == "POST", new() { Timeout = 1000 });
            throw new Exception("A refusal before the native click must propagate.");
        }
        catch (BeforeNativeClickException observed)
        {
            Require(ReferenceEquals(expected, observed) && sent == 0, "Playwright must preserve the pre-click refusal type without wrapping it or sending any command.");
        }
    }

    private static async Task Check(IBrowser browser, string scenario)
    {
        using var storage = new ColumnTestStorageScope();
        var modern = scenario is "modern" or "all" or "not-persisted" or "missing-after-add" or "missing-after-unpersisted" or "already-present" or "already-unpersisted";
        var alreadyActive = scenario is "renewed" or "missing" or "missing-automatic" or "missing-after-add" or "missing-after-unpersisted" or "missing-scope-close" or "missing-scope-before-close" or "already-present" or "already-unpersisted" or "user-change" or "missing-cancel";
        var persisted = new List<string> { "2", "12" };
        if (scenario is "already-present" or "already-unpersisted") persisted.AddRange(new[] { "8", "64", "65", "900" });
        var displayed = persisted.ToList();
        var active = alreadyActive;
        var render = 0; var reads = 0; var mutations = 0; var adds = 0; var removals = 0; var disables = 0;
        var methods = new List<string>();
        var diagnostics = new List<string>();
        var host = await browser.NewContextAsync();
        await using var service = new BrowserService();
        service.Log += diagnostics.Add;
        service.AttachForTesting(host, Target);
        var page = await host.NewPageAsync();
        string Html(string body) => "<!doctype html><html><meta charset='utf-8'><style>body{font:14px sans-serif}iframe{width:900px;height:650px}.modal,.ui-dialog{position:fixed;inset:8px;background:white;z-index:10;padding:12px}button{min-height:28px}td{padding:4px}[hidden]{display:none!important}</style><body>" + body + "</body></html>";
        string Hidden(string name, string value) => "<input type='hidden' name='" + name + "' value='" + value + "'>";
        string Identity(string user = "17") => Hidden("itemtype", "Ticket") + Hidden("users_id", user);
        string Form(string body, string user = "17") => "<form method='post' action='/glpi/front/displaypreference.form.php'>" + Identity(user) + Hidden("_glpi_csrf_token", "synthetic") + body + "</form>";
        string FullLabel(string id)
        {
            var option = Choices.Single(item => item.Id == id);
            return option.Group == "Caractéristiques" ? option.Label : option.Group + " - " + option.Label;
        }
        string Options() => string.Concat(Choices.Where(item => modern || !persisted.Contains(item.Id)).OrderBy(item => scenario is "renewed" or "missing-scope-before-close" && item.Id == "900" ? 0 : 1).GroupBy(item => item.Group).Select(group =>
            "<optgroup label='" + group.Key + "'>" + string.Concat(group.Select(item => "<option value='" + item.Id + "' data-full-label='" + FullLabel(item.Id) + "'>" + item.Label + "</option>")) + "</optgroup>"));
        string Preferences()
        {
            if (scenario == "global-only") return Html("<div id='tabsbody'>" + Form("<select name='num'><option value='3'>Priorité</option></select><input name='add' type='submit' value='Ajouter'>", "0") + "</div>");
            string body;
            if (!active) body = modern ? Identity() + "<button type='button' name='activate'>Créer la vue personnelle</button>"
                : Form("<input type='submit' name='activate' value='Créer la vue personnelle'>");
            else if (modern) body = Identity() + "<button name='disable' type='button'>Désactiver</button><select name='num'>" + Options() + "</select><button name='add_opt' type='button'>Ajouter</button><ul class='list-group'>"
                + "<li data-opt-id='1' data-fixed='true'><span>Titre</span></li>" + string.Concat(persisted.Select(id => "<li data-opt-id='" + id + "'><span class='flex-fill'>" + FullLabel(id) + "</span><button name='remove_opt' type='button'>Supprimer</button></li>")) + "</ul>";
            else body = "<table><tr><th>" + Form("<input name='disable' type='submit' value='Désactiver'>") + "</th></tr><tr><td colspan='4'>"
                + Form("<select name='num' id='dropdown_num_" + reads + "'>" + Options() + "</select><input type='submit' name='add' value='Ajouter'>")
                + "</td></tr><tr><td>Titre</td><td colspan='3'></td></tr>"
                + string.Concat(persisted.Select((id, index) => "<tr><td>" + FullLabel(id) + "</td><td>" + Form(Hidden("id", (700 + index).ToString()) + "<button name='purge' type='submit'>Supprimer</button>") + "</td><td></td><td></td></tr>")) + "</table>";
            var script = modern ? """
                <script>
                document.querySelector('[name=activate]')?.addEventListener('click',async()=>{
                  // GLPI 11's native $.ajax().then(reload) runs after the full
                  // response. fetch alone resolves at headers and can abort an
                  // unread body on reload, unlike that native XHR lifecycle.
                  const reply=await fetch('/glpi/ajax/displaypreference.php',{method:'POST',body:new URLSearchParams({activate:'1',itemtype:'Ticket',users_id:'17'})});
                  await reply.text();location.reload();
                });
                document.querySelector('[name=add_opt]')?.addEventListener('click',async()=>{
                  const select=document.querySelector('select[name=num]'), value=select.value;
                  const li=document.createElement('li');li.dataset.optId=value;
                  const span=document.createElement('span');span.className='flex-fill';span.textContent=select.selectedOptions[0].dataset.fullLabel;li.append(span);
                  const remove=document.createElement('button');remove.type='button';remove.name='remove_opt';remove.textContent='Supprimer';li.append(remove);
                  document.querySelector('.list-group').append(li);
                  const body=new URLSearchParams({action:'update_order',itemtype:'Ticket',users_id:'17'});
                  document.querySelectorAll('li[data-opt-id]:not([data-fixed])').forEach(item=>body.append('opts[]',item.dataset.optId));
                  const reply=await fetch('/glpi/ajax/displaypreference.php',{method:'POST',body});await reply.text();
                });
                document.addEventListener('click',async event=>{
                  const button=event.target.closest('button[name=remove_opt]');if(!button)return;
                  button.closest('li[data-opt-id]').remove();
                  const body=new URLSearchParams({action:'update_order',itemtype:'Ticket',users_id:'17'});
                  document.querySelectorAll('li[data-opt-id]:not([data-fixed])').forEach(item=>body.append('opts[]',item.dataset.optId));
                  const reply=await fetch('/glpi/ajax/displaypreference.php',{method:'POST',body});await reply.text();
                });
                document.querySelector('[name=disable]')?.addEventListener('click',async()=>{
                  const reply=await fetch('/glpi/ajax/displaypreference.php',{method:'POST',body:new URLSearchParams({disable:'1',itemtype:'Ticket',users_id:'17'})});
                  await reply.text();location.reload();
                });
                </script>
                """ : scenario == "renewed" ? """
                <script>
                document.addEventListener('change', event => {
                  const select = event.target;
                  if (!select.matches('select[name=num]')) return;
                  // A concurrent native form rerender changes its generated
                  // references while keeping the chosen native value intact.
                  const form = select.form, chosen = select.value, clone = form.cloneNode(true);
                  clone.id = 'personal_form_after_choice';
                  clone.querySelector('select').id = 'dropdown_num_after_choice';
                  clone.querySelector('select').value = chosen;
                  clone.querySelector('[name=add]').id = 'add_after_choice';
                  form.replaceWith(clone);
                });
                </script>
                """ : scenario == "missing-scope-before-close" ? """
                <script>
                document.addEventListener('change', event => {
                  if (!event.target.matches('select[name=num]')) return;
                  event.target.querySelector('option[value="3"]')?.remove();
                  parent.document.querySelector('input[name="criteria[0][value]"]').value='5';
                });
                </script>
                """ : "";
            return Html("<div id='tabsbody' class='" + (modern ? "display_preference_config" : "") + "'>" + body + "</div>" + script);
        }
        string Card()
        {
            var number = ++render;
            var headers = new[] { "Titre" }.Concat(displayed.Select(FullLabel)).ToArray();
            var values = new[] { "Exemple synthétique" }.Concat(displayed.Select(id => id switch { "2" => "7", "12" => "Nouveau", "3" => "Haute", _ => "Valeur synthétique" }));
            var table = "<table class='" + (modern ? "search-results" : "tab_cadre_fixehov") + "' id='search_" + number + "'><thead><tr>"
                + string.Concat(headers.Select(header => "<th>" + header + "</th>")) + "</tr></thead><tbody><tr>" + string.Concat(values.Select(value => "<td>" + value + "</td>")) + "</tr></tbody></table>";
            return "<div class='search-card'>" + (modern ? "<button type='button' class='show_displaypreference_modal' id='opener'>Colonnes</button>" : "")
                + "<form id='massformTicket" + number + "' data-search-itemtype='Ticket' data-count='1' data-start='0' data-limit='20'>" + table + "</form></div>";
        }
        string ListPage(string criteriaValue = "1")
        {
            var card = Card();
            var criteria = "<form name='searchformTicket'>" + Hidden("criteria[0][field]", "12") + Hidden("criteria[0][value]", criteriaValue) + "</form>";
            var legacyClose = scenario is "missing" or "missing-automatic" or "missing-cancel" ? "document.querySelector('#dialog').hidden=true;setTimeout(()=>location.reload(),180);"
                : scenario == "missing-scope-close" ? "const changed=new URL(location.href);changed.searchParams.set('criteria[0][value]','5');location.href=changed.href;"
                : "location.reload();";
            if (!modern) return Html(criteria + card.Replace("class='search-card'", "class='legacy-search'", StringComparison.Ordinal)
                + "<span class='fa-wrench pointer' onclick=\"$('#search_config_top').dialog('open');\">Colonnes</span><div class='ui-dialog' role='dialog' id='dialog' hidden><button class='ui-dialog-titlebar-close'>Fermer</button><div id='search_config_top'><iframe id='Iframesearch_config_top'></iframe></div></div>"
                + "<script>window.$=()=>({dialog:()=>{document.querySelector('#dialog').hidden=false;document.querySelector('iframe').src='/glpi/front/displaypreference.form.php?itemtype=Ticket';}});document.querySelector('.ui-dialog-titlebar-close').onclick=()=>{" + legacyClose + "};</script>");
            return Html(criteria + card + """
                <script>
                function bindColumns(){document.querySelector('#opener').onclick=()=>{
                  let modal=document.querySelector('#displaypreference_modal_sticky');
                  if(!modal){modal=document.createElement('div');modal.id='displaypreference_modal_sticky';modal.className='modal';modal.setAttribute('role','dialog');
                    modal.innerHTML='<button type="button" data-bs-dismiss="modal">Fermer</button><iframe src="/glpi/front/displaypreference.form.php?itemtype=Ticket"></iframe>';document.body.append(modal);
                    modal.querySelector('button').onclick=async()=>{modal.hidden=true;
                      const result=await fetch('/glpi/ajax/search.php',{method:'POST',body:new URLSearchParams(location.search)});
                      const fresh=new DOMParser().parseFromString(await result.text(),'text/html');document.querySelector('.search-card').replaceWith(fresh.querySelector('.search-card'));bindColumns();};}
                  modal.hidden=false;
                };}bindColumns();
                </script>
                """);
        }
        await host.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.GetLeftPart(UriPartial.Authority) != Origin) { await route.AbortAsync(); return; }
            var body = route.Request.PostData ?? "";
            var pairs = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
                .Select(parts => (Key: WebUtility.UrlDecode(parts[0]), Value: WebUtility.UrlDecode(parts.Length > 1 ? parts[1] : ""))).ToArray();
            string Value(string name) => pairs.FirstOrDefault(pair => pair.Key == name).Value ?? "";
            methods.Add(route.Request.Method + " " + uri.AbsolutePath);
            if (uri.AbsolutePath.EndsWith("/displaypreference.form.php", StringComparison.Ordinal) || uri.AbsolutePath.EndsWith("/ajax/displaypreference.php", StringComparison.Ordinal))
            {
                if (route.Request.Method == "POST")
                {
                    Require(Value("itemtype") == "Ticket" && Value("users_id") == "17", "Only native personal Ticket commands may be submitted.");
                    mutations++;
                    if (Value("activate").Length > 0) active = true;
                    else if (pairs.Any(pair => pair.Key == "disable"))
                    {
                        Require(active && persisted.SequenceEqual(new[] { "2", "12" }), "A temporary personal view may be disabled only after its original columns have been restored.");
                        active = false; disables++;
                    }
                    else if (modern)
                    {
                        Require(Value("action") == "update_order", "Only a native modern column handler may update its order.");
                        var submitted = pairs.Where(pair => pair.Key == "opts[]").Select(pair => pair.Value).ToList();
                        if (submitted.Count == displayed.Count + 1) adds++;
                        else
                        {
                            Require(submitted.Count == persisted.Count - 1 && persisted.Except(submitted).Count() == 1
                                && persisted.Except(submitted).All(id => id is not ("2" or "12")), "Restoration may remove only one temporary column at a time.");
                            removals++;
                        }
                        displayed = submitted;
                        if (scenario is not ("not-persisted" or "missing-after-unpersisted")) persisted = submitted.ToList();
                    }
                    else if (pairs.Any(pair => pair.Key == "purge"))
                    {
                        Require(int.TryParse(Value("id"), out var nativeId), "The native preference row must carry its real identifier.");
                        var index = nativeId - 700;
                        Require(index >= 2 && index < persisted.Count, "Restoration may remove only a temporary personal preference row.");
                        persisted.RemoveAt(index); displayed = persisted.ToList(); removals++;
                    }
                    else
                    {
                        var value = Value("num"); Require(Value("add").Length > 0 && Choices.Any(item => item.Id == value) && !persisted.Contains(value), "Only an offered, missing column may be added.");
                        persisted.Add(value); displayed = persisted.ToList(); adds++;
                    }
                    if (scenario == "scope-change" && adds == 1)
                        await page.Locator("input[name='criteria[0][value]']").EvaluateAsync("input => input.value='5'");
                }
                else reads++;
                await route.FulfillAsync(new() { ContentType = modern && route.Request.Method == "POST" ? "application/json" : "text/html; charset=utf-8", Body = modern && route.Request.Method == "POST" ? "{}" : Preferences() });
            }
            else if (uri.AbsolutePath.EndsWith("/ajax/search.php", StringComparison.Ordinal))
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Html(Card()) });
            else if (uri.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal))
            {
                var criteriaValue = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                    .Where(pair => pair.Length == 2 && WebUtility.UrlDecode(pair[0]) == "criteria[0][value]")
                    .Select(pair => WebUtility.UrlDecode(pair[1])).FirstOrDefault() ?? "1";
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = ListPage(criteriaValue) });
            }
            else await route.AbortAsync();
        });
        await page.GotoAsync(Target);
        var settings = new AppSettings { PortalUrl = Target, AutoMode = "pagination", ListTimeoutSeconds = 5, TimeoutSeconds = 5 };
        settings.ColumnPreparationMode = scenario == "all" ? "all" : "supervision";
        if (scenario == "current") settings.ColumnPreparationMode = "current";
        var original = await service.ChooseAutoTableAsync(page, settings, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var changed = false;
        var progress = new ImmediateProgress(value =>
        {
            if (scenario == "cancel" && value.Message.StartsWith("Ajout de colonne 2", StringComparison.Ordinal)) cancellation.Cancel();
            if (changed || !alreadyActive || !value.Message.StartsWith(scenario is "missing-after-add" or "missing-after-unpersisted" ? "Ajout de colonne 2" : "Ajout de colonne 1", StringComparison.Ordinal)) return;
            changed = true;
            var preferenceFrame = page.Frames.Single(frame => frame.Url.Contains("/front/displaypreference.form.php", StringComparison.Ordinal));
            if (scenario == "renewed")
                preferenceFrame.EvaluateAsync("""
                    () => { const select=document.querySelector('select[name=num]');select.id='dropdown_num_rerendered';select.form.id='personal_form_rerendered';select.form.querySelector('[name=add]').id='add_rerendered'; }
                    """).GetAwaiter().GetResult();
            else if (scenario is "already-present" or "already-unpersisted")
            {
                displayed = persisted.Concat(new[] { "3" }).ToList();
                if (scenario == "already-present") persisted.Add("3");
                preferenceFrame.EvaluateAsync("""
                    () => { const li=document.createElement('li');li.dataset.optId='3';li.innerHTML='<span class="flex-fill">Priorité</span>';document.querySelector('.list-group').append(li); }
                    """).GetAwaiter().GetResult();
            }
            else if (scenario == "user-change")
                preferenceFrame.EvaluateAsync("() => document.querySelectorAll('input[name=users_id]').forEach(input=>input.value='18')").GetAwaiter().GetResult();
            else if (scenario == "missing-scope-before-close") { /* Native change handler alters the form during selection. */ }
            else
            {
                preferenceFrame.EvaluateAsync("value => document.querySelector('select[name=num] option[value=\"' + value + '\"]').remove()", adds == 0 ? "3" : "8").GetAwaiter().GetResult();
                if (scenario == "missing-cancel") cancellation.Cancel();
            }
        });
        try
        {
            if (scenario == "missing-automatic")
            {
                settings.ExportFolder = Path.Combine(Path.GetTempPath(), "glpi-column-fallback-" + Guid.NewGuid().ToString("N"));
                settings.AutoSaveXlsx = false; settings.ResumePagination = false; settings.ExpectedTotal = 1; settings.MaxRows = 20; settings.MaxPages = 5;
                try
                {
                    var automatic = await new AutomaticExtractionService(service).RunAsync(settings, progress, cancellation.Token);
                    Require(automatic.Data.Rows.Count == 1 && File.Exists(automatic.PrimaryPath)
                        && automatic.Data.Provenance.ColumnPreparation is { State: "unavailable", AddedCount: 0 }, "The actual automatic pipeline must finish its CSV and retain the optional preparation receipt.");
                    Require(mutations == 0 && automatic.Table.Selector != original.Selector
                        && !await page.Locator("#dialog").IsVisibleAsync(), "Automatic collection must continue from the refreshed table after native modal closure, without a column POST.");
                }
                finally { if (Directory.Exists(settings.ExportFolder)) Directory.Delete(settings.ExportFolder, true); }
                return;
            }
            var result = await service.PrepareColumnsAsync(original, settings, progress, cancellation.Token);
            if (scenario is "not-persisted" or "scope-change" or "cancel" or "missing-after-unpersisted" or "missing-scope-close" or "missing-scope-before-close" or "already-unpersisted" or "user-change" or "missing-cancel") throw new Exception("The unsafe or cancelled preparation should have stopped.");
            if (scenario == "current") Require(mutations == 0 && reads == 0 && result.Receipt.State == "current", "Current view must not open or modify preferences.");
            else if (scenario == "global-only") Require(mutations == 0 && result.Receipt.State == "unavailable" && result.Receipt.Warnings.Count == 1, "Global-only scope must be preserved with an explicit fallback.");
            else if (scenario is "missing" or "missing-after-add")
            {
                var count = scenario == "missing" ? 0 : 1;
                Require(mutations == count && adds == count && result.Receipt.AddedCount == count, "A missing option must not trigger another native POST.");
                Require(result.Receipt.State == "unavailable" && result.Receipt.Warnings.Count == 1 && !result.Receipt.Message.Contains("Toutes les options", StringComparison.Ordinal), "A recovered preparation must expose its limit without claiming every option was processed.");
                Require(result.Table.Selector != original.Selector && result.Table.Headers.Count == original.Headers.Count + count, "Fallback must wait for the real refreshed table, including delayed zero-add legacy reload.");
                if (!modern) Require(!await page.Locator("#dialog").IsVisibleAsync()
                    && !page.Frames.Any(frame => frame.Url.Contains("/front/displaypreference.form.php", StringComparison.Ordinal)), "Legacy fallback must close its preference iframe through the native reload.");
                if (modern) Require(reads >= 2 && persisted.Count == 3, "Earlier modern additions must be confirmed by a server GET before fallback succeeds.");
                var collected = await service.ExtractAsync(result.Table, false, settings, null, CancellationToken.None);
                Require(collected.Rows.Count == 1 && collected.Headers.SequenceEqual(result.Table.Headers), "The refreshed table must remain directly collectable after the optional preparation fallback.");
            }
            else
            {
                var count = scenario == "all" ? 5 : scenario == "already-present" ? 0 : 4;
                Require(result.Receipt.State == "ready" && result.Receipt.AddedCount == count && adds == count && mutations == count + (alreadyActive ? 0 : 1), "Every native addition and activation must be verified once.");
                Require(result.Table.Selector != original.Selector && result.Table.Headers.Count == original.Headers.Count + count + (scenario == "already-present" ? 1 : 0), "Native reload must replace the table choice even when its random ID changes.");
                Require(persisted.Take(2).SequenceEqual(new[] { "2", "12" }) && ColumnPreparationPolicy.PreservesHeaders(original.Headers, result.Table.Headers), "Existing columns and their order must be retained.");
                Require(!result.Receipt.SourceChanged && page.Url == Target, "Preparation must preserve ticket sample and filters.");
                if (modern) Require(reads >= (alreadyActive ? 2 : 3), "A reused optimistic iframe must be read through the observed native GET before certification.");
                if (scenario == "renewed") Require(diagnostics.Any(message => message.Contains("repères natifs renouvelés", StringComparison.Ordinal)), "A native control refresh must be distinguished from an option disappearing.");
            }
            Require(!methods.Any(method => method.Contains("massiveaction", StringComparison.Ordinal)), "No business or global action may be submitted.");
            if (service.HasPendingTemporaryColumns(settings))
            {
                var restoration = await service.RestoreTemporaryColumnsAsync(settings, null, CancellationToken.None);
                Require(!restoration.Pending && !service.HasPendingTemporaryColumns(settings)
                    && persisted.Take(2).SequenceEqual(new[] { "2", "12" }) && active == alreadyActive,
                    "Every successful temporary preparation must restore its original personal activation and initial column order.");
                Require(removals == adds && disables == (alreadyActive ? 0 : 1),
                    "Restoration must reverse each owned addition once, and disable only a view activated by this preparation.");
            }
        }
        catch (OperationCanceledException) when (scenario == "cancel") { Require(adds == 1, "Cancellation must stop before the second native addition."); }
        catch (OperationCanceledException) when (scenario == "missing-cancel") { Require(adds == 0, "Cancellation must not be converted to a successful fallback."); }
        catch (InvalidOperationException error) when (scenario is "not-persisted" or "missing-after-unpersisted" or "already-unpersisted")
        { Require(error.Message.Contains("confirmées après relecture", StringComparison.Ordinal) && reads >= (alreadyActive ? 2 : 3), "Optimistic HTTP200 must fail on the server-rendered persistence proof: " + error.Message); }
        catch (InvalidOperationException error) when (scenario == "scope-change")
        { Require(error.Message.Contains("critères", StringComparison.Ordinal) && adds == 1, "A changed search must stop the next addition."); }
        catch (InvalidOperationException error) when (scenario == "missing-scope-close")
        { Require(error.Message.Contains("critères", StringComparison.Ordinal) && adds == 0 && mutations == 0, "A changed search must stop a recovered preparation before extraction."); }
        catch (InvalidOperationException error) when (scenario == "missing-scope-before-close")
        { Require(error.Message.Contains("critères", StringComparison.Ordinal) && mutations == 0
            && await page.Locator("#dialog").IsVisibleAsync()
            && await page.Locator("input[name='criteria[0][value]']").InputValueAsync() == "5", "A changed form must be detected before native close can erase it by reloading."); }
        catch (InvalidOperationException error) when (scenario == "user-change")
        { Require(error.Message.Contains("vue personnelle", StringComparison.Ordinal) && adds == 0 && mutations == 0, "Another personal identity must never be used or hidden by fallback."); }
    }
}
