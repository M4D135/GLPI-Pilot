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
    private IPage? _unsettledLibraryIndexPage;
    private bool HasUnclosedLibraryPages(IPage root) => !root.IsClosed
        || _ticketPageRoots.Any(pair => ReferenceEquals(pair.Value, root) && !pair.Key.IsClosed);

    // GLPI 10: Search::showGenericSearch, Ticket::getAllStatusArray(true),
    // templates/components/search/{display_data,table}.html.twig and pager.html.twig.
    // A reset alone restores status=notold/notclosed: the observed All option is essential.
    private const string LibrarySearchDomScript = """
        arg => {
          const visible = e => e instanceof Element && e.getClientRects().length > 0 && !e.closest('[hidden],[aria-hidden="true"]') && getComputedStyle(e).visibility !== 'hidden';
          const text = e => visible(e) ? (e.innerText || '').trim() : '';
          const css = e => { if(e.id && document.querySelectorAll('#'+CSS.escape(e.id)).length===1)return '#'+CSS.escape(e.id); const p=[]; for(let n=e;n instanceof Element;n=n.parentElement){const peers=n.parentElement?[...n.parentElement.children].filter(x=>x.localName===n.localName):[n];p.unshift(n.localName+':nth-of-type('+(peers.indexOf(n)+1)+')');}return p.join(' > '); };
          let restoreUrl='';const state=[document.readyState];
          const fail = (reason,terminal=false,category='index-session') => ({ready:false,reason,restoreUrl,terminal,category,progress:JSON.stringify(state),documentReady:document.readyState!=='loading'});
          if(location.origin!==arg.origin || location.pathname!==arg.path) return fail('La page de recherche appartient à une autre installation.',true);
          if([...document.querySelectorAll('input[type=password],input[autocomplete=one-time-code]')].some(visible))return fail('Une authentification est demandée.',true);
          const forms=[...document.querySelectorAll('form[name="searchformticket"],form[name="searchformTicket"],form.search-form-container')].filter(f=>{
            let u;try{u=new URL(f.getAttribute('action')||location.href,document.baseURI)}catch{return false}
            return (f.getAttribute('method')||'get').toLowerCase()==='get' && u.origin===arg.origin && u.pathname===arg.path && !u.search && !u.hash && !u.username && !u.password;
          });
          state.push(forms.length);
          if(forms.length!==1)return fail('Le formulaire natif de recherche des tickets est absent ou ambigu.');
          const form=forms[0], allFields=[...form.elements].filter(e=>e.name && /^(?:meta)?criteria\[/.test(e.name));
          state.push(allFields.length);
          if(allFields.length>500)return fail('Le formulaire contient trop de critères pour une vérification sûre.');
          const resets=[...form.querySelectorAll('a.search-reset[href],a.reset-search[href]')].filter(a=>{try{const u=new URL(a.href);return u.origin===arg.origin&&u.pathname===arg.path&&u.searchParams.size===1&&u.searchParams.get('reset')==='reset'&&!u.hash}catch{return false}});
          const restoreName=name=>/^(?:meta)?criteria(?:\[\d+\]|\[criteria\])*\[(?:field|searchtype|value|link|itemtype|meta)\](?:\[\])?$/.test(name)||/^(?:is_deleted|as_map|browse|savedsearches_id|start|list_limit|glpilist_limit|sort(?:\[\d+\])?|order(?:\[\d+\])?)$/.test(name);
          const restore=new URL(form.getAttribute('action'),document.baseURI);
          const controls=[...form.elements].filter(e=>restoreName(e.name||'')&&!['password','file','button','submit','reset'].includes(e.type)&&(!['checkbox','radio'].includes(e.type)||e.checked));
          if(resets.length===1&&controls.some(e=>/^(?:meta)?criteria\[/.test(e.name))&&controls.length<=500){
            restore.searchParams.set('reset','reset');
            for(const [k,v] of new URL(location.href).searchParams)if(restoreName(k)&&!controls.some(e=>e.name===k))restore.searchParams.append(k,v);
            for(const e of controls){const values=e instanceof HTMLSelectElement?[...e.selectedOptions].map(o=>o.value):[e.value];for(const v of values)restore.searchParams.append(e.name,v)}
            if(restore.href.length<=8192)restoreUrl=restore.href;
          }
          const targets=Array.isArray(arg.targetIds)?arg.targetIds:[],targeted=targets.length>0;
          const fieldControls=allFields.filter(e=>e instanceof HTMLSelectElement && /^criteria\[\d+\]\[field\]$/.test(e.name));
          const statusField=fieldControls.find(e=>e.value==='12')||fieldControls.find(e=>[...e.options].some(o=>o.value==='12'));
          const statusValues=statusField?[...form.elements].filter(e=>e.name===statusField.name.replace(/\[field\]$/,'[value]')):[];
          const statusOptions=statusField?.value==='12'&&statusValues.length===1&&statusValues[0] instanceof HTMLSelectElement
            ? [...statusValues[0].options].filter(o=>/^[1-6]$/.test(o.value)&&o.textContent.trim().length>0).map(o=>({code:Number(o.value),label:o.textContent.trim()})):[];
          // An ID field number is not assumed: use the unique native option explicitly named ID.
          const idFields=[...new Set(fieldControls.flatMap(e=>[...e.options].filter(o=>/^\d{1,20}$/.test(o.value)&&/^(id|identifiant|identifier|ticket id)$/.test(o.textContent.normalize('NFKC').trim().replace(/\s+/g,' ').toLowerCase())).map(o=>o.value)))];
          if(targeted&&idFields.length!==1)return fail('Le critère Identifiant natif est absent ou ambigu.',true,'index-page');
          const nativeField=targeted?idFields[0]:'12';
          const fields=fieldControls.filter(e=>[...e.options].some(o=>o.value===nativeField));
          const selected=fields.find(e=>e.value===nativeField) || fields[0];
          if(!selected)return fail('Le critère Statut natif n’est pas disponible.');
          const prefix=selected.name.replace(/\[field\]$/,''), named=suffix=>[...form.elements].filter(e=>e.name===prefix+suffix);
          const types=named('[searchtype]'), values=named('[value]');
          const type=types.length===1?types[0]:null,value=values.length===1?values[0]:null;
          state.push(selected.value,types.length,values.length,type instanceof HTMLSelectElement?type.value:'',selected.value===nativeField?value?.value||'':'',type?.options?.length||0,value?.options?.length||0);
          const observed=selected.value===nativeField && type instanceof HTMLSelectElement && [...type.options].some(o=>o.value==='equals') && resets.length===1 && (targeted
            ? value instanceof HTMLInputElement && ['text','search','number'].includes(value.type)
            : value instanceof HTMLSelectElement && [...value.options].some(o=>o.value==='all'));
          const selectedPairs=allFields.filter(e=>!['checkbox','radio'].includes(e.type)||e.checked).map(e=>[e.name,e.value]);
          const expected=new Map();
          if(targeted){for(let i=0;i<targets.length;i++){const key='criteria['+i+']';expected.set(key+'[field]',nativeField);expected.set(key+'[searchtype]','equals');expected.set(key+'[value]',targets[i]);if(i>0)expected.set(key+'[link]','OR');}}
          else {expected.set(prefix+'[field]','12');expected.set(prefix+'[searchtype]','equals');expected.set(prefix+'[value]','all');}
          const flagsOk=['is_deleted','as_map','browse'].every(name=>{const controls=[...form.elements].filter(e=>e.name===name);return controls.length<=1&&controls.every(e=>e.value==='0')});
          const optionalLink=targeted?'criteria[0][link]':prefix+'[link]';
          const exact=observed && flagsOk && selectedPairs.length>=expected.size && selectedPairs.length<=expected.size+1 && [...expected].every(([k,v])=>selectedPairs.filter(p=>p[0]===k&&p[1]===v).length===1) && selectedPairs.every(([k,v])=>expected.get(k)===v || k===optionalLink&&v==='AND');
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
          const response={ready:true,reason:'',formSelector:css(form),fieldSelector:css(selected),statusFieldSelector:statusField?css(statusField):'',statusOptions,nativeField,statusSelected:selected.value===nativeField,allUrl:observed?action.href:'',allStatuses:exact&&queryOk,scope,restoreUrl,progress:JSON.stringify(state),documentReady:document.readyState!=='loading'};
          if(arg.mode!=='read')return response;
          if(targeted&&!response.allStatuses)return fail('La recherche ne confirme pas les seuls identifiants sélectionnés sans autre filtre.',true,'index-scope');
          if(!response.allStatuses)return fail('La recherche ne confirme pas Statut = Tous sans autre filtre.');
          // GLPI 10 marks its result container; GLPI 9.5 puts the native table in its mass-action form and writes the range in its pager.
          let owners=[...document.querySelectorAll('form[data-search-itemtype="Ticket"]')].filter(visible);
          const legacy=owners.length===0;
          if(legacy)owners=[...document.querySelectorAll('form[name="massformTicket"]')].filter(f=>visible(f)&&f.querySelector('table.tab_cadrehov'));
          state.push(owners.length);
          if(owners.length!==1)return fail('Le conteneur natif des résultats Ticket est absent ou ambigu.');
          const owner=owners[0], tables=[...owner.querySelectorAll(legacy?'table.tab_cadrehov':'table.search-results')].filter(visible);
          state.push(tables.length);
          if(tables.length!==1)return fail('Le tableau natif des tickets est absent ou ambigu.');
          const table=tables[0], number=name=>{const v=owner.getAttribute(name);return /^\d+$/.test(v||'')&&Number.isSafeInteger(Number(v))?Number(v):null};
          state.push(table.rows.length,number('data-count'),number('data-start'),number('data-limit'));
          if(table.classList.contains('deleted-results'))return fail('La liste affichée correspond à la corbeille.');
          const legacyPagers=legacy?[...document.querySelectorAll('table.tab_cadre_pager')].filter(visible):[];
          const legacyRange=()=>{
            const ranges=legacyPagers.flatMap(p=>[...p.querySelectorAll('td.b')].filter(visible).map(e=>{const n=(text(e).match(/\d[\d \u00a0\u202f]*/g)||[]).map(v=>Number(v.replace(/[ \u00a0\u202f]/g,'')));return n.length===3&&n.every(Number.isSafeInteger)&&n[0]>=1&&n[0]<=n[1]&&n[1]<=n[2]?n:null}).filter(Boolean));
            // GLPI 9.5 replaces this native select with a select2 widget and hides the original: read the native value inside the visible pagers.
            const limits=legacyPagers.flatMap(p=>[...p.querySelectorAll('select[name="glpilist_limit"]')]).map(e=>e.value);
            if(ranges.length===0||ranges.some(r=>JSON.stringify(r)!==JSON.stringify(ranges[0]))||limits.length===0||limits.some(v=>v!==limits[0])||!/^\d+$/.test(limits[0]))return [null,null,null];
            const [from,to,total]=ranges[0],size=Number(limits[0]);
            return size>=1&&to===Math.min(from-1+size,total)?[total,from-1,size]:[null,null,null];
          };
          const [count,start,limit]=legacy?legacyRange():[number('data-count'),number('data-start'),number('data-limit')];
          if(count===null||start===null||limit===null||limit<1||count>10000000)return fail('La pagination native n’expose pas un total et une plage valides.');
          if(table.closest('[aria-busy="true"]')||[...owner.querySelectorAll('.loadingindicator,[aria-busy="true"]')].some(visible))return fail('Le tableau est en cours de chargement.');
          const headerRows=[...table.querySelectorAll('thead > tr')].filter(r=>r.closest('table')===table);
          if(headerRows.length!==1)return fail('Les colonnes du tableau sont ambiguës.');
          const headings=[...headerRows[0].children].filter(e=>e.matches('th,td'));
          if(headings.length>256||headings.some(h=>Number(h.getAttribute('colspan')||1)!==1))return fail('La structure des colonnes n’est pas prise en charge.');
          const option=e=>{if(!legacy)return e.getAttribute('data-searchopt-id');const links=[...e.querySelectorAll('a[href]')].filter(a=>a.closest('th,td')===e);if(links.length!==1)return null;try{const u=new URL(links[0].href);return u.origin===arg.origin&&u.pathname===arg.path&&u.searchParams.getAll('sort').length===1?u.searchParams.get('sort'):null}catch{return null}};
          const primary=id=>{const matches=headings.map((e,i)=>({e,i})).filter(x=>visible(x.e)&&option(x.e)===id && x.e.getAttribute('data-nosort')!=='true');return matches.length===1?matches[0].i:-1};
          const modification=primary('19'),status=primary('12'),category=primary('7');
          const headers=headings.map(text), rows=[]; let invalidRows=0,characters=0;
          if(headers.some(h=>h.length>1000))return fail('Un en-tête de l’index dépasse la limite de lecture.');
          const bodyRows=[...table.querySelectorAll('tbody > tr')].filter(r=>r.closest('table')===table);
          if(bodyRows.length>100000)return fail('La page dépasse la limite de lecture des tickets.');
          for(const row of bodyRows){
            if(!visible(row))continue;
            const cells=[...row.children].filter(e=>e.matches('td,th'));
            if(legacy&&cells.length===headings.length&&cells.every(c=>c.localName==='th'))continue;
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
            const knownStatuses=targeted&&Array.isArray(arg.statusOptions)?arg.statusOptions:statusOptions;
            const codes=knownStatuses.filter(o=>Number.isInteger(o.code)&&o.code>=1&&o.code<=6&&o.label===stat).map(o=>o.code);
            rows.push({id:link.id,url:link.url,title:link.title,status:stat,statusCode:codes.length===1?codes[0]:null,category:category>=0?values[category]:'',modified:modification>=0?values[modification]:'',modificationProven:modification>=0&&visible(cells[modification]),headers,values});
          }
          if(legacy){
            // The same "next" link is printed above and below the list.
            const hrefs=[...new Set(legacyPagers.flatMap(p=>[...p.querySelectorAll('a[href]')].filter(a=>visible(a)&&a.querySelector(':scope > i.fa-chevron-right'))).map(a=>a.href))];
            if(hrefs.length>1)return fail('Plusieurs pages suivantes sont possibles.');
            const nextStart=hrefs.length?Number(new URL(hrefs[0]).searchParams.get('start')):null;
            return {...response,count,start,limit,rows,invalidRows,nextUrl:hrefs[0]||'',nextStart:Number.isSafeInteger(nextStart)?nextStart:null};
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
        [JsonPropertyName("documentReady")] public bool DocumentReady { get; set; }
        [JsonPropertyName("terminal")] public bool Terminal { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("progress")] public string Progress { get; set; } = "";
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
        [JsonPropertyName("formSelector")] public string FormSelector { get; set; } = "";
        [JsonPropertyName("fieldSelector")] public string FieldSelector { get; set; } = "";
        [JsonPropertyName("statusFieldSelector")] public string StatusFieldSelector { get; set; } = "";
        [JsonPropertyName("statusOptions")] public List<LibraryStatusOption> StatusOptions { get; set; } = new();
        [JsonPropertyName("nativeField")] public string NativeField { get; set; } = "";
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

    private sealed class LibraryStatusOption
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; } = "";
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

    public Task<TicketLibraryIndex> ReadTicketLibraryIndexAsync(AppSettings settings,
        IProgress<ExtractionProgress>? progress, CancellationToken ct, string sourceDateOrder = "", Action<LibraryIndexProgress>? diagnostic = null) =>
        ReadTicketLibraryIndexCoreAsync(settings, progress, ct, sourceDateOrder, diagnostic);

    private async Task<TicketLibraryIndex> ReadTicketLibraryIndexCoreAsync(AppSettings settings,
        IProgress<ExtractionProgress>? progress, CancellationToken ct, string sourceDateOrder, Action<LibraryIndexProgress>? diagnostic,
        IReadOnlyList<string>? targetIds = null, int? pageLimit = null, int? rowLimit = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (sourceDateOrder is not ("" or "dmy" or "mdy" or "ymd"))
            throw new ArgumentException("L’ordre des dates GLPI n’est pas reconnu.", nameof(sourceDateOrder));
        var source = TicketReferenceResolver.ReadHttpUrl(settings.PortalUrl, "L’adresse de la liste des tickets");
        if (!source.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal))
            throw new GlpiLibraryIndexException("session", "index-page", "La bibliothèque requiert la liste native /front/ticket.php de cette installation GLPI.");
        var targeted = targetIds is { Count: > 0 };
        var result = new TicketLibraryIndex
        {
            SourceUrl = source.GetLeftPart(UriPartial.Path), IsTargeted = targeted, RequestedCount = targetIds?.Count ?? 0,
            ScopeDescription = targeted ? "Tickets sélectionnés, tous statuts hors corbeille, dans le périmètre d’entités actif de la session GLPI"
                : "Tous les statuts hors corbeille, dans le périmètre d’entités actif de la session GLPI"
        };
        await _ticketReadGate.WaitAsync(ct);
        IPage? page = null;
        LibraryIndexActivity? activity = null;
        string restoreUrl = "";
        string? scope = null;
        var restoreAllowed = true;
        var restoreSkipped = "Le périmètre GLPI a changé ; le filtre précédent n’a pas été réappliqué à ce nouveau contexte.";
        var phase = "session";
        void Report(LibraryIndexProgress notice)
        {
            phase = notice.Phase;
            progress?.Report(new(result.PagesRead, result.Tickets.Count, result.ExpectedTotal, notice.Message));
            Log?.Invoke("BIBLIOTHÈQUE · " + notice.Message);
            diagnostic?.Invoke(notice);
        }
        GlpiLibraryIndexException Failure(string category, string message)
        {
            var error = new GlpiLibraryIndexException(activity?.Phase ?? phase, category, message);
            Report(new(error.Phase, error.Category, error.Message)); return error;
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            Report(new("session", "phase-start", "Vérification de la session GLPI pour lire l’index…"));
            if (_unsettledLibraryIndexPage is { } unsettled && HasUnclosedLibraryPages(unsettled))
            {
                await CloseTicketFamilyAsync(unsettled);
                if (HasUnclosedLibraryPages(unsettled))
                    throw new GlpiLibraryIndexException("session", "index-browser", "La fenêtre du précédent index n’est pas encore fermée. Aucune nouvelle recherche n’a été envoyée ; fermer cette fenêtre GLPI avant de reprendre.");
            }
            _unsettledLibraryIndexPage = null;
            if (_context == null) await OpenAsync(settings, ct, background: true);
            if (_context == null || _profileInstallation != ObjectIdentity.InstallationKey(settings.PortalUrl))
                throw new GlpiLibraryIndexException("session", "index-session", "La session du navigateur ne correspond pas à cette installation GLPI.");
            if (ActivePage is { IsClosed: false } active && TargetUrlPolicy.SamePage(active.Url, source.AbsoluteUri))
            {
                try
                {
                    var previous = await ReadLibraryIndexDomAsync(active, source, "form", ct);
                    restoreUrl = previous.RestoreUrl;
                    if (previous.Ready && previous.DocumentReady) scope = previous.Scope;
                }
                catch (PlaywrightException)
                { Report(new("session", "restore-unconfirmed", "Le filtre affiché n’est pas encore lisible ; il sera observé dans la page dédiée avant la collecte.")); }
            }
            var pending = _context.NewPageAsync();
            try { page = await pending.WaitAsync(ct); }
            catch (OperationCanceledException) { _ = CloseLateTicketPageAsync(pending); throw; }
            OwnTicketPage(page);
            activity = new(page, source, settings.ListTimeoutSeconds, Report);
            await NavigateLibraryListAsync(page, source.AbsoluteUri, "open-list", activity, ct);
            var form = await WaitLibraryIndexDomAsync(page, source, "form", activity, ct, targetIds);
            if (scope is not null && scope != form.Scope)
            {
                restoreAllowed = false;
                throw new GlpiLibraryIndexException("open-list", "index-scope", "Le profil ou le périmètre d’entités a changé avant la recherche. L’index n’a pas été collecté.");
            }
            scope = form.Scope;
            if (restoreUrl.Length == 0) restoreUrl = form.RestoreUrl;
            if (targeted && form.StatusOptions.Count == 0)
            {
                // Preserve the same native status codes as the global reader.
                // Otherwise ID-only searches would change a seed from code 6 to
                // null and create a false same-version conflict in an old cache.
                if (form.StatusFieldSelector.Length == 0)
                    throw new GlpiLibraryPageException("Le critère Statut natif n’est pas disponible pour vérifier les codes des tickets sélectionnés.");
                await LibraryCollectionPacer.BeforeNavigationAsync(ct).ConfigureAwait(false);
                var verifiedStatus = await activity.AwaitAsync(ReadLibraryIndexDomAsync(page, source, "form", ct, targetIds), ct);
                if (!verifiedStatus.Ready || verifiedStatus.FormSelector != form.FormSelector || verifiedStatus.StatusFieldSelector != form.StatusFieldSelector)
                    throw new GlpiLibraryPageException("Le formulaire de recherche a changé pendant la temporisation. Aucun critère n’a été modifié.");
                if (verifiedStatus.Scope != scope)
                {
                    restoreAllowed = false;
                    throw new GlpiLibraryIndexException("open-list", "index-scope", "Le profil ou le périmètre d’entités a changé pendant la temporisation. Aucun critère n’a été modifié.");
                }
                activity.ObserveNativeField("12"); activity.BeginPhase("status");
                await activity.AwaitAsync(page.Locator(form.StatusFieldSelector).SelectOptionAsync("12", new() { Force = true, Timeout = 0 }), ct);
                _ = await WaitLibraryIndexDomAsync(page, source, "status", activity, ct);
                form = await WaitLibraryIndexDomAsync(page, source, "form", activity, ct, targetIds);
                if (form.Scope != scope)
                {
                    restoreAllowed = false;
                    throw new GlpiLibraryIndexException("status", "index-scope", "Le profil ou le périmètre d’entités a changé pendant l’observation des statuts.");
                }
            }
            IReadOnlyList<LibraryStatusOption>? statusOptions = targeted ? form.StatusOptions : null;
            if (targeted && statusOptions!.Count == 0)
                throw new GlpiLibraryPageException("Les codes de statut natifs n’ont pas pu être observés. Aucun contenu n’a été relu.");
            if (!form.StatusSelected)
            {
                await LibraryCollectionPacer.BeforeNavigationAsync(ct).ConfigureAwait(false);
                var verifiedForm = await activity.AwaitAsync(ReadLibraryIndexDomAsync(page, source, "form", ct, targetIds), ct);
                if (!verifiedForm.Ready || verifiedForm.FormSelector != form.FormSelector || verifiedForm.FieldSelector != form.FieldSelector || verifiedForm.NativeField != form.NativeField)
                    throw new GlpiLibraryPageException("Le formulaire de recherche a changé pendant la temporisation. Aucun critère n’a été modifié.");
                if (verifiedForm.Scope != scope)
                {
                    restoreAllowed = false;
                    throw new GlpiLibraryIndexException("open-list", "index-scope", "Le profil ou le périmètre d’entités a changé pendant la temporisation. Aucun critère n’a été modifié.");
                }
                activity.ObserveNativeField(form.NativeField);
                activity.BeginPhase(targeted ? "ticket-id" : "status");
                await activity.AwaitAsync(page.Locator(form.FieldSelector).SelectOptionAsync(form.NativeField, new() { Force = true, Timeout = 0 }), ct);
                form = await WaitLibraryIndexDomAsync(page, source, "status", activity, ct, targetIds);
            }
            if (!form.Ready || form.AllUrl.Length == 0)
                throw new GlpiLibraryPageException(targeted
                    ? "Le critère Identifiant et son opérateur d’égalité n’ont pas été observés. Aucune recherche globale n’a été lancée."
                    : "L’option Tous du critère Statut n’a pas été observée dans le formulaire GLPI. Aucun périmètre complet n’est déclaré.");
            ValidateLibraryListUrl(form.AllUrl, source, allowReset: true);
            await NavigateLibraryListAsync(page, form.AllUrl, targeted ? "targeted" : "all-status", activity, ct);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var expectedStart = 0;
            var maxPages = Math.Min(Math.Clamp(settings.MaxPages, 1, 10000), pageLimit ?? 10000);
            var maxRows = Math.Min(Math.Clamp(settings.MaxRows, 1, 1000000), rowLimit ?? 1000000);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = await WaitLibraryIndexDomAsync(page, source, "read", activity, ct, targetIds, statusOptions);
                ValidateLibraryListUrl(page.Url, source, allowReset: true);
                if (!snapshot.AllStatuses) throw new GlpiLibraryPageException("Le filtre attendu a changé pendant la lecture de la bibliothèque.");
                if (targeted && snapshot.Count > targetIds!.Count)
                    throw new GlpiLibraryPageException("Le total GLPI dépasse la liste d’identifiants demandée. La recherche ciblée n’a pas été confirmée.");
                if (scope != snapshot.Scope)
                {
                    restoreAllowed = false;
                    if (targeted)
                        throw new GlpiLibraryIndexException("scope", "index-scope",
                            "Le profil ou le périmètre d’entités a changé pendant la recherche ciblée. La collecte a été arrêtée avant toute lecture de contenu.");
                    result.Limitations.Add("Le périmètre d’entités ou le profil GLPI a changé pendant la lecture."); break;
                }
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
                    if (targeted && !targetIds!.Contains(reference.Id, StringComparer.Ordinal))
                        throw new GlpiLibraryPageException("GLPI a renvoyé un ticket extérieur à la sélection. Aucun contenu de cette recherche n’a été lu.");
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
                progress?.Report(new(result.PagesRead, result.Tickets.Count, result.ExpectedTotal,
                    targeted ? "Vérification des tickets sélectionnés…" : "Indexation des tickets de tous les statuts…"));
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
                await NavigateLibraryListAsync(page, next.AbsoluteUri, "page", activity, ct);
            }
            result.Limitations = result.Limitations.Distinct(StringComparer.Ordinal).ToList();
            Report(new(activity.Phase, "index-complete", $"Index lu : {result.Tickets.Count} tickets, {result.PagesRead} pages ; couverture {(result.Complete ? "confirmée" : "à vérifier")} dans le périmètre d’entités actif."));
        }
        catch (GlpiLibraryIndexException error)
        { Report(new(error.Phase, error.Category, error.Message)); throw; }
        catch (GlpiLibraryPageException error)
        { throw Failure("index-page", error.Message); }
        catch (PlaywrightException)
        { throw Failure("index-browser", "La recherche GLPI a changé ou s’est fermée pendant l’indexation de la bibliothèque. La page ou le critère en cours n’a pas été renvoyé."); }
        catch (TimeoutException)
        { throw Failure("index-stalled", "Le navigateur n’a pas confirmé l’étape de lecture de l’index. Aucune recherche n’a été renvoyée automatiquement."); }
        catch (LibraryCollectorLeaseException)
        {
            restoreAllowed = false;
            restoreSkipped = "Le verrou du collecteur n’est plus vérifiable ; aucune restauration du filtre n’a été envoyée.";
            throw;
        }
        catch (InvalidOperationException)
        { throw Failure("index-page", "La recherche ou un lien de l’index ne correspond pas au périmètre natif vérifié. La collecte de la liste a été arrêtée."); }
        finally
        {
            try
            {
                activity?.Dispose();
                // Close the owned page before restoring: an interrupted request must
                // never remain live while the next operation changes session state.
                if (page != null) await CloseTicketFamilyAsync(page);
                if (page != null && HasUnclosedLibraryPages(page))
                {
                    _unsettledLibraryIndexPage = page;
                    restoreAllowed = false;
                    restoreSkipped = "La fermeture de la fenêtre d’index n’est pas confirmée ; aucune restauration ni nouvelle recherche ne sera envoyée avant sa fermeture.";
                }
                if (activity?.SearchMayHaveChanged == true)
                {
                    result.SearchRestored = restoreAllowed && await RestoreLibrarySearchAsync(restoreUrl, source, settings, Report, ct);
                    if (!result.SearchRestored)
                    {
                        result.Limitations.Add("Le filtre précédent n’a pas pu être restauré dans la session GLPI ; la page de recherche affichée est conservée.");
                        if (!restoreAllowed) Report(new("restore", "restore-unconfirmed", restoreSkipped));
                    }
                }
            }
            finally { _ticketReadGate.Release(); }
        }
        ct.ThrowIfCancellationRequested();
        return result;
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

    private async Task NavigateLibraryListAsync(IPage page, string url, string phase, LibraryIndexActivity activity, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await LibraryCollectionPacer.BeforeNavigationAsync(ct).ConfigureAwait(false);
        activity.BeginPhase(phase);
        // readyState=interactive can precede deferred/module script execution.
        // Wait for the browser's real DOMContentLoaded barrier before touching
        // controls, without a deadline that cuts off GLPI or its needed scripts.
        await activity.AwaitDocumentAsync(() => page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 0 }), ct);
        ct.ThrowIfCancellationRequested();
    }

    private async Task<bool> RestoreLibrarySearchAsync(string value, Uri source, AppSettings settings,
        Action<LibraryIndexProgress> report, CancellationToken ct)
    {
        bool Unconfirmed(string message) { report(new("restore", "restore-unconfirmed", message)); return false; }
        if (_context == null || value.Length == 0)
            return Unconfirmed("Le filtre précédent n’a pas pu être observé ; sa restauration n’est pas confirmée. La recherche affichée est conservée.");
        // A normal restoration follows live progress just like index reading.
        // Only cancellation cleanup has a small independent bound: stopping a
        // collection must remain responsive even when the server never replies.
        using var cleanup = ct.IsCancellationRequested ? new CancellationTokenSource(TimeSpan.FromSeconds(3)) : null;
        var restoreToken = cleanup?.Token ?? ct;
        IPage? restorePage = null;
        LibraryIndexActivity? activity = null;
        try
        {
            var target = TicketReferenceResolver.ReadHttpUrl(value, "La recherche à restaurer");
            if (!TicketReferenceResolver.SameOrigin(target, source) || target.AbsolutePath != source.AbsolutePath || target.Fragment.Length > 0)
                return Unconfirmed("L’adresse du filtre précédent ne correspond plus à la liste GLPI ; la restauration n’a pas été envoyée.");
            var pairs = TicketReferenceResolver.ParseQuery(target);
            if (!pairs.Any(pair => pair.Key.StartsWith("criteria[", StringComparison.Ordinal)) || pairs.Count > 500 || pairs.Any(pair =>
                !(Regex.IsMatch(pair.Key, @"\A(?:meta)?criteria(?:\[\d+\]|\[criteria\])*\[(?:field|searchtype|value|link|itemtype|meta)\](?:\[\])?\z", RegexOptions.CultureInvariant)
                  || Regex.IsMatch(pair.Key, @"\A(?:is_deleted|as_map|browse|savedsearches_id|start|list_limit|glpilist_limit|sort(?:\[\d+\])?|order(?:\[\d+\])?)\z", RegexOptions.CultureInvariant)
                  || pair.Key == "reset" && pair.Value == "reset")))
                return Unconfirmed("Le filtre précédent contient des paramètres non reconnus ; la restauration n’a pas été envoyée.");
            var pending = _context.NewPageAsync();
            try { restorePage = await pending.WaitAsync(restoreToken); }
            catch (OperationCanceledException) { _ = CloseLateTicketPageAsync(pending); throw; }
            OwnTicketPage(restorePage);
            activity = new(restorePage, source, settings.ListTimeoutSeconds, report);
            activity.BeginPhase("restore");
            // This is the single native GET captured before Tous, never a retry
            // of the interrupted index request or a business form submission.
            await activity.AwaitDocumentAsync(() => restorePage.GotoAsync(target.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 0 }), restoreToken);
            var restored = await WaitLibraryIndexDomAsync(restorePage, source, "restore", activity, restoreToken);
            if (restored.RestoreUrl.Length == 0)
                return Unconfirmed("La réponse GLPI ne permet pas de confirmer le filtre précédent ; la recherche affichée est conservée.");
            static string Canonical(string url) => string.Join('&', TicketReferenceResolver.ParseQuery(new Uri(url)).Select(pair => QueryPair(pair.Key, pair.Value)).Order(StringComparer.Ordinal));
            if (Canonical(restored.RestoreUrl) != Canonical(target.AbsoluteUri))
                return Unconfirmed("Le filtre renvoyé par GLPI diffère du filtre précédent ; la restauration n’est pas confirmée.");
            return true;
        }
        catch (GlpiLibraryIndexException error)
        { return Unconfirmed("Restauration du filtre non confirmée : " + error.Message); }
        catch (OperationCanceledException)
        { return Unconfirmed("La restauration du filtre a été interrompue pendant l’arrêt de la collecte ; la recherche affichée est conservée."); }
        catch (Exception error) when (error is PlaywrightException or InvalidOperationException or TimeoutException)
        { return Unconfirmed("Le navigateur n’a pas confirmé la restauration du filtre précédent ; la recherche affichée est conservée."); }
        finally
        {
            activity?.Dispose();
            if (restorePage != null) await CloseTicketFamilyAsync(restorePage);
            if (restorePage != null && HasUnclosedLibraryPages(restorePage))
            {
                _unsettledLibraryIndexPage = restorePage;
                report(new("restore", "restore-unconfirmed", "La fermeture de la fenêtre de restauration n’est pas confirmée ; une nouvelle indexation attendra sa fermeture."));
            }
        }
    }

    private Task<LibraryIndexDom> ReadLibraryIndexDomAsync(IPage page, Uri source, string mode, CancellationToken ct,
        IReadOnlyList<string>? targetIds = null, IReadOnlyList<LibraryStatusOption>? statusOptions = null) =>
        ReadJsonAsync<LibraryIndexDom>(page.MainFrame, LibrarySearchDomScript,
            new { mode, targetIds, statusOptions = statusOptions?.Select(o => new { code = o.Code, label = o.Label }).ToArray(),
                origin = source.GetLeftPart(UriPartial.Authority), path = source.AbsolutePath, detailPath = source.AbsolutePath.Replace("/front/ticket.php", "/front/ticket.form.php", StringComparison.Ordinal) }, ct);

    // Only fixed reasons written by LibrarySearchDomScript leave the reader.
    private static readonly HashSet<string> IndexReasons = Regex.Matches(LibrarySearchDomScript, @"fail\('([^']+)'").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    internal static string KnownIndexReason(string? reason) =>
        reason is { Length: > 0 } && IndexReasons.Contains(reason) ? reason : "La recherche GLPI ne présente pas une structure de liste prise en charge.";

    private async Task<LibraryIndexDom> WaitLibraryIndexDomAsync(IPage page, Uri source, string mode, LibraryIndexActivity activity, CancellationToken ct,
        IReadOnlyList<string>? targetIds = null, IReadOnlyList<LibraryStatusOption>? statusOptions = null)
    {
        LibraryIndexDom? last = null;
        GlpiLibraryIndexException Stalled()
        {
            var reason = mode == "status" && last?.Ready == true
                ? targetIds is { Count: > 0 }
                    ? "L’opérateur d’égalité du critère Identifiant n’a pas été observé dans le formulaire GLPI."
                    : "L’option Tous du critère Statut n’a pas été observée dans le formulaire GLPI."
                : KnownIndexReason(last?.Reason);
            return new(activity.Phase, "index-stalled",
                reason + " Aucune progression ni requête native en cours ; l’index n’est pas confirmé.");
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested(); activity.ThrowIfFailed();
            try
            {
                // Inactivity can expire while the next DOM read is in flight.
                // Keep the last observed reason in that path as well.
                last = await activity.AwaitAsync(ReadLibraryIndexDomAsync(page, source, mode, ct, targetIds, statusOptions), ct, Stalled);
                activity.Observe(last.Progress);
                if (last.Terminal)
                    throw new GlpiLibraryIndexException(activity.Phase,
                        last.Category is "index-page" or "index-scope" ? last.Category : "index-session", KnownIndexReason(last.Reason));
                if ((mode == "restore" ? last.RestoreUrl.Length > 0 : last.Ready) && last.DocumentReady && !activity.HasPendingRequest
                    && (mode != "status" || last.StatusSelected && last.AllUrl.Length > 0))
                { activity.CompletePhase(); return last; }
            }
            catch (PlaywrightException) when (!page.IsClosed && activity.HasPendingRequest)
            { activity.ThrowIfFailed(); } // Only a live native navigation can replace an execution context normally.
            if (!activity.CanWait)
                throw Stalled();
            await Task.Delay(200, ct);
        }
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

/// <summary>A GLPI list page did not match a supported structure. Its message is always one of the program's own fixed sentences.</summary>
public sealed class GlpiLibraryPageException(string message) : InvalidOperationException(message);
