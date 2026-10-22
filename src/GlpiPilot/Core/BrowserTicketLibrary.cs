using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    // GLPI 10: Search::showGenericSearch, Ticket::getAllStatusArray(true),
    // templates/components/search/{display_data,table}.html.twig and pager.html.twig.
    // A reset alone restores status=notold/notclosed: the observed All option is essential.
    private const string LibrarySearchDomScript = """
        arg => {
          const visible = e => e instanceof Element && e.getClientRects().length > 0 && !e.closest('[hidden],[aria-hidden="true"]') && getComputedStyle(e).visibility !== 'hidden';
          const text = e => visible(e) ? (e.innerText || '').trim() : '';
          const css = e => { if(e.id && document.querySelectorAll('#'+CSS.escape(e.id)).length===1)return '#'+CSS.escape(e.id); const p=[]; for(let n=e;n instanceof Element;n=n.parentElement){const peers=n.parentElement?[...n.parentElement.children].filter(x=>x.localName===n.localName):[n];p.unshift(n.localName+':nth-of-type('+(peers.indexOf(n)+1)+')');}return p.join(' > '); };
          let restoreUrl='';
          const fail = reason => ({ready:false,reason,restoreUrl});
          if(location.origin!==arg.origin || location.pathname!==arg.path) return fail('La page de recherche appartient à une autre installation.');
          if([...document.querySelectorAll('input[type=password],input[autocomplete=one-time-code]')].some(visible))return fail('Une authentification est demandée.');
          const forms=[...document.querySelectorAll('form[name="searchformticket"],form.search-form-container')].filter(f=>{
            let u;try{u=new URL(f.getAttribute('action')||location.href,document.baseURI)}catch{return false}
            return (f.getAttribute('method')||'get').toLowerCase()==='get' && u.origin===arg.origin && u.pathname===arg.path && !u.search && !u.hash && !u.username && !u.password;
          });
          if(forms.length!==1)return fail('Le formulaire natif de recherche des tickets est absent ou ambigu.');
          const form=forms[0], allFields=[...form.elements].filter(e=>e.name && /^(?:meta)?criteria\[/.test(e.name));
          if(allFields.length>500)return fail('Le formulaire contient trop de critères pour une vérification sûre.');
          const resets=[...form.querySelectorAll('a.search-reset[href]')].filter(a=>{try{const u=new URL(a.href);return u.origin===arg.origin&&u.pathname===arg.path&&u.searchParams.size===1&&u.searchParams.get('reset')==='reset'&&!u.hash}catch{return false}});
          const restoreName=name=>/^(?:meta)?criteria(?:\[\d+\]|\[criteria\])*\[(?:field|searchtype|value|link|itemtype|meta)\](?:\[\])?$/.test(name)||/^(?:is_deleted|as_map|browse|savedsearches_id|start|list_limit|glpilist_limit|sort(?:\[\d+\])?|order(?:\[\d+\])?)$/.test(name);
          const restore=new URL(form.getAttribute('action'),document.baseURI);
          const controls=[...form.elements].filter(e=>restoreName(e.name||'')&&!['password','file','button','submit','reset'].includes(e.type)&&(!['checkbox','radio'].includes(e.type)||e.checked));
          if(resets.length===1&&controls.some(e=>/^(?:meta)?criteria\[/.test(e.name))&&controls.length<=500){
            restore.searchParams.set('reset','reset');
            for(const [k,v] of new URL(location.href).searchParams)if(restoreName(k)&&!controls.some(e=>e.name===k))restore.searchParams.append(k,v);
            for(const e of controls){const values=e instanceof HTMLSelectElement?[...e.selectedOptions].map(o=>o.value):[e.value];for(const v of values)restore.searchParams.append(e.name,v)}
            if(restore.href.length<=8192)restoreUrl=restore.href;
          }
          const fields=allFields.filter(e=>e instanceof HTMLSelectElement && /^criteria\[\d+\]\[field\]$/.test(e.name) && [...e.options].some(o=>o.value==='12'));
          const selected=fields.find(e=>e.value==='12') || fields[0];
          if(!selected)return fail('Le critère Statut natif n’est pas disponible.');
          const prefix=selected.name.replace(/\[field\]$/,''), named=suffix=>[...form.elements].filter(e=>e.name===prefix+suffix);
          const types=named('[searchtype]'), values=named('[value]');
          const type=types.length===1?types[0]:null,value=values.length===1?values[0]:null;
          const observed=selected.value==='12' && type instanceof HTMLSelectElement && [...type.options].some(o=>o.value==='equals') && value instanceof HTMLSelectElement && [...value.options].some(o=>o.value==='all') && resets.length===1;
          const selectedPairs=allFields.filter(e=>!['checkbox','radio'].includes(e.type)||e.checked).map(e=>[e.name,e.value]);
          const expected=new Map([[prefix+'[field]','12'],[prefix+'[searchtype]','equals'],[prefix+'[value]','all']]);
          const flagsOk=['is_deleted','as_map','browse'].every(name=>{const controls=[...form.elements].filter(e=>e.name===name);return controls.length<=1&&controls.every(e=>e.value==='0')});
          const exact=observed && flagsOk && selectedPairs.length>=3 && selectedPairs.length<=4 && expected.size===3 && [...expected].every(([k,v])=>selectedPairs.filter(p=>p[0]===k&&p[1]===v).length===1) && selectedPairs.every(([k,v])=>expected.get(k)===v || k===prefix+'[link]'&&v==='AND');
          const action=new URL(form.getAttribute('action'),document.baseURI);
          if(observed){action.searchParams.set('reset','reset');for(const [k,v] of expected)action.searchParams.set(k,v);for(const key of ['is_deleted','as_map','browse'])action.searchParams.set(key,'0');}
          const queryOk=[...new URL(location.href).searchParams].every(([k,v])=>{
            if(/^(?:meta)?criteria\[/.test(k))return selectedPairs.some(p=>p[0]===k&&p[1]===v);
            if(['is_deleted','as_map','browse'].includes(k))return v==='0';
            if(k==='reset')return v==='reset';if(k==='itemtype')return v==='Ticket';if(k==='search')return v===''||v==='Search'||v==='Rechercher';
            if(/^(?:start|list_limit|glpilist_limit|sort(?:\[\d+\])?)$/.test(k))return /^\d+$/.test(v);
            return /^order(?:\[\d+\])?$/.test(k)&&/^(ASC|DESC)$/i.test(v);
          });
          const scopeControls=[...document.querySelectorAll('select[name],input[name]')].filter(e=>/^(active_entity|entities_id|newprofile|profiles_id)$/.test(e.name));
          const scope=JSON.stringify({context:scopeControls.map(e=>[e.name,e.value]).sort(),header:[...document.querySelectorAll('body > .page > header.navbar,body > .page > aside.navbar,#header_top,#c_ssmenu2')].map(text)});
          if(scope.length>64000 || scopeControls.length>100)return fail('Le périmètre de session ne peut pas être borné.');
          const response={ready:true,reason:'',formSelector:css(form),fieldSelector:css(selected),statusSelected:selected.value==='12',allUrl:observed?action.href:'',allStatuses:exact&&queryOk,scope,restoreUrl};
          if(arg.mode!=='read')return response;
          if(!response.allStatuses)return fail('La recherche ne confirme pas Statut = Tous sans autre filtre.');
          const owners=[...document.querySelectorAll('form[data-search-itemtype="Ticket"]')].filter(visible);
          if(owners.length!==1)return fail('Le conteneur natif des résultats Ticket est absent ou ambigu.');
          const owner=owners[0], tables=[...owner.querySelectorAll('table.search-results')].filter(visible);
          if(tables.length!==1)return fail('Le tableau natif des tickets est absent ou ambigu.');
          const table=tables[0], number=name=>{const v=owner.getAttribute(name);return /^\d+$/.test(v||'')&&Number.isSafeInteger(Number(v))?Number(v):null};
          if(table.classList.contains('deleted-results'))return fail('La liste affichée correspond à la corbeille.');
          const count=number('data-count'),start=number('data-start'),limit=number('data-limit');
          if(count===null||start===null||limit===null||limit<1||count>10000000)return fail('La pagination native n’expose pas un total et une plage valides.');
          if(table.closest('[aria-busy="true"]')||[...owner.querySelectorAll('.loadingindicator,[aria-busy="true"]')].some(visible))return fail('Le tableau est en cours de chargement.');
          const headerRows=[...table.querySelectorAll('thead > tr')].filter(r=>r.closest('table')===table);
          if(headerRows.length!==1)return fail('Les colonnes du tableau sont ambiguës.');
          const headings=[...headerRows[0].children].filter(e=>e.matches('th,td'));
          if(headings.length>256||headings.some(h=>Number(h.getAttribute('colspan')||1)!==1))return fail('La structure des colonnes n’est pas prise en charge.');
          const primary=id=>{const matches=headings.map((e,i)=>({e,i})).filter(x=>visible(x.e)&&x.e.getAttribute('data-searchopt-id')===id && x.e.getAttribute('data-nosort')!=='true');return matches.length===1?matches[0].i:-1};
          const modification=primary('19'),status=primary('12'),category=primary('7');
          const headers=headings.map(text), rows=[]; let invalidRows=0,characters=0;
          if(headers.some(h=>h.length>1000))return fail('Un en-tête de l’index dépasse la limite de lecture.');
          const bodyRows=[...table.querySelectorAll('tbody > tr')].filter(r=>r.closest('table')===table);
          if(bodyRows.length>100000)return fail('La page dépasse la limite de lecture des tickets.');
          for(const row of bodyRows){
            if(!visible(row))continue;
            const cells=[...row.children].filter(e=>e.matches('td,th'));
            const links=[...row.querySelectorAll('a[href]')].filter(a=>a.closest('tr')===row&&visible(a)).flatMap(a=>{try{
              const u=new URL(a.href);if(u.origin!==arg.origin||u.pathname!==arg.detailPath||u.username||u.password||u.hash||u.searchParams.getAll('id').length!==1||!/^\d+$/.test(u.searchParams.get('id')||'')||BigInt(u.searchParams.get('id'))<=0n||[...u.searchParams.keys()].some(k=>!['id','forcetab','_glpi_tab'].includes(k)))return [];
              return [{id:String(BigInt(u.searchParams.get('id'))),url:u.href,title:text(a)}];
            }catch{return []}});
            if(count===0&&links.length===0)continue;
            const identities=[...new Set(links.map(l=>l.id))];
            if(identities.length!==1||cells.length!==headings.length||cells.some(c=>Number(c.getAttribute('colspan')||1)!==1||Number(c.getAttribute('rowspan')||1)!==1)){invalidRows++;continue;}
            const link=links.find(l=>l.title)||links[0],values=cells.map(text);
            characters+=values.reduce((n,v)=>n+v.length,0);
            if(link.title.length>10000||values.some(v=>v.length>500000)||characters>4000000)return fail('Le contenu de la page dépasse la limite de lecture de l’index.');
            const stat=status>=0?(values[status]||[...cells[status].querySelectorAll('[title]')].filter(visible).map(e=>e.getAttribute('title')).filter(Boolean).join(' ')):'';
            const codes=value instanceof HTMLSelectElement?[...value.options].filter(o=>/^[1-6]$/.test(o.value)&&o.textContent.trim()===stat).map(o=>Number(o.value)):[];
            rows.push({id:link.id,url:link.url,title:link.title,status:stat,statusCode:codes.length===1?codes[0]:null,category:category>=0?values[category]:'',modified:modification>=0?values[modification]:'',modificationProven:modification>=0&&visible(cells[modification]),headers,values});
          }
          const next=[...owner.querySelectorAll('.search-pager .page-link-next[href]')].filter(a=>visible(a)&&!a.closest('table'));
          const enabled=next.filter(a=>a.getAttribute('aria-disabled')!=='true'&&!a.closest('.disabled'));
          if(enabled.length>1)return fail('Plusieurs pages suivantes sont possibles.');
          return {...response,count,start,limit,rows,invalidRows,nextUrl:enabled.length?enabled[0].href:'',nextStart:enabled.length?Number(enabled[0].getAttribute('data-start')):null};
        }
        """;

    private sealed class LibraryIndexDom
    {
        [JsonPropertyName("ready")] public bool Ready { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
        [JsonPropertyName("formSelector")] public string FormSelector { get; set; } = "";
        [JsonPropertyName("fieldSelector")] public string FieldSelector { get; set; } = "";
        [JsonPropertyName("statusSelected")] public bool StatusSelected { get; set; }
        [JsonPropertyName("allUrl")] public string AllUrl { get; set; } = "";
        [JsonPropertyName("restoreUrl")] public string RestoreUrl { get; set; } = "";
        [JsonPropertyName("allStatuses")] public bool AllStatuses { get; set; }
        [JsonPropertyName("scope")] public string Scope { get; set; } = "";
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("start")] public int Start { get; set; }
        [JsonPropertyName("limit")] public int Limit { get; set; }
        [JsonPropertyName("invalidRows")] public int InvalidRows { get; set; }
        [JsonPropertyName("nextUrl")] public string NextUrl { get; set; } = "";
        [JsonPropertyName("nextStart")] public int? NextStart { get; set; }
        [JsonPropertyName("rows")] public List<LibraryIndexRow> Rows { get; set; } = new();
    }

    private sealed class LibraryIndexRow
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("statusCode")] public int? StatusCode { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("modified")] public string Modified { get; set; } = "";
        [JsonPropertyName("modificationProven")] public bool ModificationProven { get; set; }
        [JsonPropertyName("headers")] public List<string> Headers { get; set; } = new();
        [JsonPropertyName("values")] public List<string> Values { get; set; } = new();
    }

    public async Task<TicketLibraryIndex> ReadTicketLibraryIndexAsync(AppSettings settings,
        IProgress<ExtractionProgress>? progress, CancellationToken ct, string sourceDateOrder = "")
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (sourceDateOrder is not ("" or "dmy" or "mdy" or "ymd"))
            throw new ArgumentException("L’ordre des dates GLPI n’est pas reconnu.", nameof(sourceDateOrder));
        var source = TicketReferenceResolver.ReadHttpUrl(settings.PortalUrl, "L’adresse de la liste des tickets");
        if (!source.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal))
            throw new InvalidOperationException("La bibliothèque requiert la liste native /front/ticket.php de cette installation GLPI.");
        var result = new TicketLibraryIndex { SourceUrl = source.GetLeftPart(UriPartial.Path) };
        await _ticketReadGate.WaitAsync(ct);
        IPage? page = null;
        string restoreUrl = "";
        var sessionMayChange = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_context == null) await OpenAsync(settings, ct, background: true);
            if (_context == null || _profileInstallation != ObjectIdentity.InstallationKey(settings.PortalUrl))
                throw new InvalidOperationException("La session du navigateur ne correspond pas à cette installation GLPI.");
            if (ActivePage is { IsClosed: false } active && TargetUrlPolicy.SamePage(active.Url, source.AbsoluteUri))
            {
                try { restoreUrl = (await ReadLibraryIndexDomAsync(active, source, "form", ct)).RestoreUrl; }
                catch (PlaywrightException) { }
            }
            var pending = _context.NewPageAsync();
            try { page = await pending.WaitAsync(ct); }
            catch (OperationCanceledException) { _ = CloseLateTicketPageAsync(pending); throw; }
            OwnTicketPage(page);
            progress?.Report(new(0, 0, null, "Préparation de la recherche de tous les statuts…"));
            sessionMayChange = true;
            await NavigateLibraryListAsync(page, source.AbsoluteUri, ct);
            var form = await WaitLibraryIndexDomAsync(page, source, "form", settings, ct);
            if (restoreUrl.Length == 0) restoreUrl = form.RestoreUrl;
            if (!form.StatusSelected)
            {
                await page.Locator(form.FieldSelector).SelectOptionAsync("12", new() { Force = true, Timeout = Math.Clamp(settings.ListTimeoutSeconds, 5, 600) * 1000 }).WaitAsync(ct);
                var timer = Stopwatch.StartNew();
                do
                {
                    ct.ThrowIfCancellationRequested();
                    form = await ReadLibraryIndexDomAsync(page, source, "form", ct);
                    if (form.Ready && form.StatusSelected && form.AllUrl.Length > 0) break;
                    await Task.Delay(150, ct);
                } while (timer.Elapsed.TotalSeconds < settings.ListTimeoutSeconds);
            }
            if (!form.Ready || form.AllUrl.Length == 0)
                throw new InvalidOperationException("L’option Tous du critère Statut n’a pas été observée dans le formulaire GLPI. Aucun périmètre complet n’est déclaré.");
            ValidateLibraryListUrl(form.AllUrl, source, allowReset: true);
            await NavigateLibraryListAsync(page, form.AllUrl, ct);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            string? scope = null;
            var expectedStart = 0;
            var maxPages = Math.Clamp(settings.MaxPages, 1, 10000);
            var maxRows = Math.Clamp(settings.MaxRows, 1, 1000000);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = await WaitLibraryIndexDomAsync(page, source, "read", settings, ct);
                ValidateLibraryListUrl(page.Url, source, allowReset: true);
                if (!snapshot.AllStatuses) throw new InvalidOperationException("Le filtre Tous a changé pendant la lecture de la bibliothèque.");
                scope ??= snapshot.Scope;
                if (scope != snapshot.Scope) { result.Limitations.Add("Le périmètre d’entités ou le profil GLPI a changé pendant la lecture."); break; }
                result.ScopeFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.GetLeftPart(UriPartial.Path) + "\n" + scope)));
                result.AllStatuses = true;
                result.ExpectedTotal ??= snapshot.Count;
                if (snapshot.Count != result.ExpectedTotal || snapshot.Start != expectedStart)
                { result.Limitations.Add("Le total ou la plage de pagination a changé pendant la lecture."); break; }
                if (snapshot.InvalidRows > 0)
                    result.Limitations.Add("Certaines lignes ne possèdent pas une identité de ticket et des colonnes univoques.");
                if (snapshot.Rows.Count != Math.Min(snapshot.Limit, Math.Max(0, snapshot.Count - snapshot.Start)))
                    result.Limitations.Add("Le nombre de lignes de cette page ne correspond pas à la plage annoncée par GLPI.");
                foreach (var row in snapshot.Rows)
                {
                    var reference = ValidateTicketReference(new(row.Id, row.Url), settings);
                    if (!ids.Add(reference.Id)) { result.Limitations.Add("Un ticket a été rencontré plusieurs fois pendant la pagination."); continue; }
                    if (result.Tickets.Count >= maxRows) { result.Limitations.Add("La limite de tickets configurée a été atteinte."); break; }
                    var modification = ReadLibraryModification(row.Modified, row.ModificationProven, sourceDateOrder);
                    var seed = new TicketLibrarySeed
                    {
                        Ticket = reference, Title = row.Title, Status = row.Status, StatusCode = row.StatusCode, Category = row.Category,
                        LastModifiedRaw = row.Modified, LastModifiedReliable = modification.Reliable,
                        LastModifiedPrecision = modification.Precision, LastModifiedAt = modification.Instant,
                        ObservedAt = result.ObservedAt, IndexHeaders = row.Headers, IndexValues = row.Values
                    };
                    for (var index = 0; index < Math.Min(row.Headers.Count, row.Values.Count); index++)
                        if (row.Headers[index].Length > 0 && row.Headers.Count(name => name == row.Headers[index]) == 1)
                            seed.Fields[row.Headers[index]] = row.Values[index];
                    result.Tickets.Add(seed);
                }
                result.PagesRead++;
                progress?.Report(new(result.PagesRead, result.Tickets.Count, result.ExpectedTotal, "Indexation des tickets de tous les statuts…"));
                if (result.Limitations.Count > 0) break;
                if (snapshot.NextUrl.Length == 0)
                {
                    result.Complete = result.Tickets.Count == result.ExpectedTotal;
                    if (!result.Complete) result.Limitations.Add("La pagination s’est arrêtée avant le total annoncé par GLPI.");
                    break;
                }
                if (result.PagesRead >= maxPages || result.Tickets.Count >= maxRows)
                { result.Limitations.Add("La limite de pages ou de tickets configurée a été atteinte."); break; }
                var next = ValidateLibraryListUrl(snapshot.NextUrl, source, allowReset: true);
                expectedStart = snapshot.Start + snapshot.Limit;
                if (snapshot.NextStart != expectedStart || !TicketReferenceResolver.ParseQuery(next).Any(pair => pair.Key == "start" && pair.Value == expectedStart.ToString(CultureInfo.InvariantCulture)))
                { result.Limitations.Add("Le lien Suivant ne correspond pas à la plage suivante attendue."); break; }
                await NavigateLibraryListAsync(page, next.AbsoluteUri, ct);
            }
            result.Limitations = result.Limitations.Distinct(StringComparer.Ordinal).ToList();
            Log?.Invoke($"BIBLIOTHÈQUE · index lu : {result.Tickets.Count} tickets, {result.PagesRead} pages ; couverture {(result.Complete ? "confirmée" : "à vérifier")} dans le périmètre d’entités actif.");
            return result;
        }
        catch (PlaywrightException)
        { throw new InvalidOperationException("La recherche GLPI a changé ou s’est fermée pendant l’indexation de la bibliothèque."); }
        finally
        {
            if (page != null) await CloseTicketFamilyAsync(page);
            if (sessionMayChange)
            {
                result.SearchRestored = await RestoreLibrarySearchAsync(restoreUrl, source);
                if (!result.SearchRestored)
                {
                    result.Limitations.Add("Le filtre précédent n’a pas pu être restauré dans la session GLPI ; la page de recherche affichée est conservée.");
                    Log?.Invoke("BIBLIOTHÈQUE · restauration du filtre précédent non confirmée ; la recherche affichée est conservée.");
                }
            }
            _ticketReadGate.Release();
        }
    }

    private static Uri ValidateLibraryListUrl(string value, Uri source, bool allowReset)
    {
        var uri = TicketReferenceResolver.ReadHttpUrl(value, "La page de la bibliothèque");
        if (!TicketReferenceResolver.SameOrigin(uri, source) || uri.AbsolutePath != source.AbsolutePath || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Le lien de recherche ne correspond pas à cette liste GLPI.");
        foreach (var pair in TicketReferenceResolver.ParseQuery(uri))
        {
            var valid = Regex.IsMatch(pair.Key, @"\Acriteria\[\d+\]\[(field|searchtype|value|link)\]\z", RegexOptions.CultureInvariant)
                || pair.Key is "is_deleted" or "as_map" or "browse" && pair.Value == "0"
                || allowReset && pair.Key == "reset" && pair.Value == "reset"
                || pair.Key == "itemtype" && pair.Value == "Ticket"
                || pair.Key == "search" && pair.Value is "" or "Search" or "Rechercher"
                || Regex.IsMatch(pair.Key, @"\A(start|list_limit|glpilist_limit|sort(?:\[\d+\])?)\z", RegexOptions.CultureInvariant) && Regex.IsMatch(pair.Value, @"\A\d+\z", RegexOptions.CultureInvariant)
                || Regex.IsMatch(pair.Key, @"\Aorder(?:\[\d+\])?\z", RegexOptions.CultureInvariant) && pair.Value is "ASC" or "DESC";
            if (!valid) throw new InvalidOperationException("Le lien de recherche contient un paramètre non reconnu pour cette lecture.");
        }
        return uri;
    }

    private async Task NavigateLibraryListAsync(IPage page, string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 }).WaitAsync(ct); }
        catch (PlaywrightException error) when (error.Message.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase)) { }
        ct.ThrowIfCancellationRequested();
    }

    private async Task<bool> RestoreLibrarySearchAsync(string value, Uri source)
    {
        if (_context == null || value.Length == 0) return false;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        IPage? restorePage = null;
        try
        {
            var target = TicketReferenceResolver.ReadHttpUrl(value, "La recherche à restaurer");
            if (!TicketReferenceResolver.SameOrigin(target, source) || target.AbsolutePath != source.AbsolutePath || target.Fragment.Length > 0) return false;
            var pairs = TicketReferenceResolver.ParseQuery(target);
            if (!pairs.Any(pair => pair.Key.StartsWith("criteria[", StringComparison.Ordinal)) || pairs.Count > 500 || pairs.Any(pair =>
                !(Regex.IsMatch(pair.Key, @"\A(?:meta)?criteria(?:\[\d+\]|\[criteria\])*\[(?:field|searchtype|value|link|itemtype|meta)\](?:\[\])?\z", RegexOptions.CultureInvariant)
                  || Regex.IsMatch(pair.Key, @"\A(?:is_deleted|as_map|browse|savedsearches_id|start|list_limit|glpilist_limit|sort(?:\[\d+\])?|order(?:\[\d+\])?)\z", RegexOptions.CultureInvariant)
                  || pair.Key == "reset" && pair.Value == "reset"))) return false;
            var pending = _context.NewPageAsync();
            try { restorePage = await pending.WaitAsync(budget.Token); }
            catch (OperationCanceledException) { _ = CloseLateTicketPageAsync(pending); throw; }
            OwnTicketPage(restorePage);
            await restorePage.GotoAsync(target.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 2500 }).WaitAsync(budget.Token);
            var restored = await ReadLibraryIndexDomAsync(restorePage, source, "form", budget.Token);
            if (restored.RestoreUrl.Length == 0) return false;
            static string Canonical(string url) => string.Join('&', TicketReferenceResolver.ParseQuery(new Uri(url)).Select(pair => QueryPair(pair.Key, pair.Value)).Order(StringComparer.Ordinal));
            return Canonical(restored.RestoreUrl) == Canonical(target.AbsoluteUri);
        }
        catch (Exception error) when (error is OperationCanceledException or PlaywrightException or InvalidOperationException or TimeoutException) { return false; }
        finally { if (restorePage != null) await CloseTicketFamilyAsync(restorePage); }
    }

    private Task<LibraryIndexDom> ReadLibraryIndexDomAsync(IPage page, Uri source, string mode, CancellationToken ct) =>
        ReadJsonAsync<LibraryIndexDom>(page.MainFrame, LibrarySearchDomScript,
            new { mode, origin = source.GetLeftPart(UriPartial.Authority), path = source.AbsolutePath, detailPath = source.AbsolutePath.Replace("/front/ticket.php", "/front/ticket.form.php", StringComparison.Ordinal) }, ct);

    private async Task<LibraryIndexDom> WaitLibraryIndexDomAsync(IPage page, Uri source, string mode, AppSettings settings, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        LibraryIndexDom? last = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                last = await ReadLibraryIndexDomAsync(page, source, mode, ct);
                if (last.Ready) return last;
            }
            catch (PlaywrightException) when (!page.IsClosed) { }
            await Task.Delay(200, ct);
        } while (timer.Elapsed.TotalSeconds < Math.Clamp(settings.ListTimeoutSeconds, 5, 600));
        throw new InvalidOperationException(last?.Reason.Length > 0 ? last.Reason : "La recherche GLPI n’est pas devenue lisible dans le délai prévu.");
    }

    internal static (bool Reliable, string Precision, DateTimeOffset? Instant) ReadLibraryModification(string raw, bool proven, string sourceDateOrder = "")
    {
        if (!proven || raw.Length is 0 or > 80 || sourceDateOrder is not ("" or "dmy" or "mdy" or "ymd")) return (false, "unknown", null);
        var value = raw.Trim();
        // Preserve the exact original string. No workstation timezone and no missing seconds are invented.
        var local = Regex.Match(value, @"\A(?<day>\d{2})[/\-](?<month>\d{2})[/\-]\d{4} ", RegexOptions.CultureInvariant);
        if (sourceDateOrder.Length == 0 && local.Success && int.Parse(local.Groups["day"].Value, CultureInfo.InvariantCulture) is var day
            && int.Parse(local.Groups["month"].Value, CultureInfo.InvariantCulture) is var month
            && day <= 12 && month <= 12 && day != month)
            return (false, "unknown", null); // GLPI's user date order has neither been observed nor configured.
        var localFormats = sourceDateOrder switch
        {
            "mdy" => new[] { "MM/dd/yyyy HH:mm", "MM-dd-yyyy HH:mm" },
            "ymd" => Array.Empty<string>(),
            _ => new[] { "dd/MM/yyyy HH:mm", "dd-MM-yyyy HH:mm" }
        };
        var minuteFormats = new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm" }.Concat(localFormats).ToArray();
        var secondFormats = minuteFormats.Select(format => format + ":ss").ToArray();
        if (DateTime.TryParseExact(value, minuteFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return (true, "minutes", null);
        if (DateTime.TryParseExact(value, secondFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return (true, "seconds", null);
        if (Regex.IsMatch(value, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:Z|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)) return (true, "seconds", instant);
        return (false, "unknown", null);
    }
}
