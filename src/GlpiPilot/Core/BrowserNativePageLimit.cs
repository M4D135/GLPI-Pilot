using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed record NativeTicketPageLimitPreparation(TableChoice Table, int? AppliedLimit, bool Changed, string Message);

public sealed partial class BrowserService
{
    // Native GLPI 9.5 pager forms and GLPI 10 components/dropdown/limit.html.twig.
    // Discovery is read-only. Only an existing, associated select receives one change.
    private const string NativeTicketPageLimitScript = """
        arg => {
          const visible=e=>e instanceof Element&&e.getClientRects().length>0&&!e.closest('[hidden],[aria-hidden="true"]')&&getComputedStyle(e).visibility!=='hidden'&&getComputedStyle(e).display!=='none';
          const css=e=>{if(e.id&&document.querySelectorAll('#'+CSS.escape(e.id)).length===1)return '#'+CSS.escape(e.id);const p=[];for(let n=e;n instanceof Element;n=n.parentElement){const peers=n.parentElement?[...n.parentElement.children].filter(x=>x.localName===n.localName):[n];p.unshift(n.localName+':nth-of-type('+(peers.indexOf(n)+1)+')');}return p.join(' > ')};
          const number=v=>/^(0|[1-9]\d*)$/.test(v||'')&&Number.isSafeInteger(Number(v))&&Number(v)<=10000000?Number(v):null;
          const unavailable=reason=>({ready:false,reason,documentReady:document.readyState!=='loading'});
          if(location.origin!==arg.origin||location.pathname!==arg.path)return unavailable('session');
          if([...document.querySelectorAll('input[type=password],input[autocomplete=one-time-code]')].some(visible))return unavailable('session');
          const allTables=[...document.querySelectorAll('form[data-search-itemtype="Ticket"] table.search-results,form[name="massformTicket"] table.tab_cadrehov,form[name="massformTicket"] table.tab_cadre_fixehov')].filter(visible);
          const tables=arg.selector?allTables.filter(t=>t.matches(arg.selector)):allTables;
          if(tables.length!==1||allTables.length!==1)return unavailable('table');
          const table=tables[0],owner=table.closest('form'),modern=owner.getAttribute('data-search-itemtype')==='Ticket';
          const associated=p=>{if(!visible(p)||table.contains(p))return false;for(let n=p.parentElement;n;n=n.parentElement){const ts=[...n.querySelectorAll('table.search-results,table.tab_cadrehov,table.tab_cadre_fixehov')].filter(visible);if(ts.length)return ts.length===1&&ts[0]===table;}return false;};
          const pagers=modern?[...owner.querySelectorAll('.search-pager')].filter(visible):[...document.querySelectorAll('table.tab_cadre_pager')].filter(associated);
          const ranges=pagers.flatMap(p=>[...p.querySelectorAll(modern?'.page-infos':'td.b')].filter(visible).map(e=>{const n=(e.innerText.match(/\d[\d \u00a0\u202f]*/g)||[]).map(x=>Number(x.replace(/[ \u00a0\u202f]/g,'')));return n.length===3&&n.every(Number.isSafeInteger)?n:null}).filter(Boolean));
          let count=modern?number(owner.getAttribute('data-count')):null,start=modern?number(owner.getAttribute('data-start')):null,limit=modern?number(owner.getAttribute('data-limit')):null;
          const rawSelects=pagers.flatMap(p=>[...p.querySelectorAll(modern?'select.search-limit-dropdown,select[name="glpilist_limit"]':'select[name="glpilist_limit"]')]);
          if(!modern){
            const values=rawSelects.map(s=>number(s.value));
            if(!ranges.length||ranges.some(r=>JSON.stringify(r)!==JSON.stringify(ranges[0]))||!values.length||values.some(v=>v===null||v!==values[0]))return unavailable('range');
            [start,,count]=ranges[0];start--;limit=values[0];
          }
          if(count===null||start===null||limit===null||limit<1||start<0||start>count||count>10000000)return unavailable('range');
          if(ranges.some(r=>r[0]!==start+1||r[1]!==Math.min(start+limit,count)||r[2]!==count))return unavailable('range');
          if(table.closest('[aria-busy="true"]')||[...owner.querySelectorAll('.loadingindicator,[aria-busy="true"]')].some(visible))return unavailable('loading');
          const forms=[...document.querySelectorAll('form[name="searchformticket"],form[name="searchformTicket"],form.search-form-container')].filter(f=>{try{const u=new URL(f.getAttribute('action')||location.href,document.baseURI);return (f.method||'get').toLowerCase()==='get'&&u.origin===arg.origin&&u.pathname===arg.path&&!u.search&&!u.hash}catch{return false}});
          if(forms.length!==1)return unavailable('scope');
          const form=forms[0],controls=[...form.elements].filter(e=>e.name&&!e.matches(':disabled')&&(/^(?:meta)?criteria\[/.test(e.name)||/^(is_deleted|as_map|browse|savedsearches_id|sort(?:\[\d+\])?|order(?:\[\d+\])?)$/.test(e.name))&&!['button','submit','password','file'].includes(e.type)&&(!['checkbox','radio'].includes(e.type)||e.checked));
          if(!controls.some(e=>/^(?:meta)?criteria\[/.test(e.name))||controls.length>500)return unavailable('scope');
          const pairs=controls.flatMap(e=>e instanceof HTMLSelectElement?[...e.selectedOptions].map(o=>[e.name,o.value]):[[e.name,e.value]]);
          const pagination=k=>/^(start|list_limit|glpilist_limit|sort(?:\[\d+\])?|order(?:\[\d+\])?)$/.test(k);
          // GLPI 9.5 has no browse control and omits browse=0 from its
          // native pager although the all-status URL explicitly includes it.
          // Only neutral native flags may disappear; nonzero values and
          // contradictory DOM controls remain part of the scope evidence.
          const neutralFlag=(k,v)=>['is_deleted','as_map','browse'].includes(k)&&v==='0'&&!pairs.some(p=>p[0]===k&&p[1]!=='0');
          const query=[...new URL(location.href).searchParams].filter(([k,v])=>!pagination(k)&&!neutralFlag(k,v)&&!(k==='reset'&&v==='reset')&&!(k==='itemtype'&&v==='Ticket')&&!(k==='search'&&['','Search','Rechercher'].includes(v))&&!pairs.some(p=>p[0]===k&&p[1]===v));
          const context=[...document.querySelectorAll('input[name],select[name]')].filter(e=>/^(active_entity|entities_id|newprofile|profiles_id)$/.test(e.name)).map(e=>[e.name,e.value]);
          const scope=JSON.stringify({pairs:pairs.sort(),query:query.sort(),context:context.sort(),header:[...document.querySelectorAll('body > .page > header.navbar,body > .page > aside.navbar,#header_top,#c_ssmenu2')].filter(visible).map(e=>e.innerText.trim())});
          if(scope.length>128000||context.length>100)return unavailable('scope');
          const headers=[...table.querySelectorAll('thead > tr')].filter(r=>r.closest('table')===table);
          if(headers.length!==1)return unavailable('rows');
          const headings=[...headers[0].children];
          if(!headings.length||headings.length>256||headings.some(h=>Number(h.getAttribute('colspan')||1)!==1))return unavailable('rows');
          const rows=[...table.querySelectorAll('tbody > tr')].filter(r=>r.closest('table')===table&&visible(r)&&!(r.children.length===headings.length&&[...r.children].every(c=>c.localName==='th')));
          const ids=[];
          for(const row of rows){
            const found=[...new Set([...row.querySelectorAll('a[href]')].filter(a=>a.closest('tr')===row&&visible(a)).flatMap(a=>{try{const u=new URL(a.href);const id=u.searchParams.get('id');return u.origin===arg.origin&&u.pathname===arg.detailPath&&u.searchParams.getAll('id').length===1&&/^[1-9]\d{0,19}$/.test(id||'')?[id]:[]}catch{return []}}))];
            if(count===0&&!found.length)continue;
            if(found.length!==1||row.children.length!==headings.length)return unavailable('rows');
            ids.push(found[0]);
          }
          if(ids.length!==Math.min(limit,count-start)||new Set(ids).size!==ids.length)return unavailable('rows');
          const state={ready:true,reason:'',documentReady:document.readyState!=='loading',count,start,limit,ids,scope,headers:headings.map(h=>h.innerText.trim()),selector:'',value:'',hidden:false,method:'',action:'',proof:'',currentValue:''};
          const candidates=[];
          for(const select of rawSelects){
            const widget=[select.nextElementSibling,select.previousElementSibling,select.id?document.getElementById('s2id_'+select.id):null].some(e=>e?.matches('.select2,.select2-container')&&visible(e));
            const hidden=!visible(select)&&select.matches('.select2-hidden-accessible,.select2-offscreen,[data-select2-id]')&&widget;
            if((!visible(select)&&!hidden)||select.matches(':disabled')||select.multiple)continue;
            const options=[...select.options].filter(o=>!o.disabled&&!o.hidden&&!o.closest('optgroup[disabled]')&&number(o.value)>0);
            if(!options.length||new Set(options.map(o=>o.value)).size!==options.length)continue;
            const current=number(select.value);if(current===null||!options.some(o=>o.value===select.value)||current!==limit)continue;
            const sizes=options.map(o=>Number(o.value)).sort((a,b)=>a-b);
            const desired=current>=1000?current:(sizes.find(v=>v>=1000)??sizes[sizes.length-1]);
            let method='',action='',proof='';
            const onchange=(select.getAttribute('onchange')||'').trim();
            if(modern&&select.matches('.search-limit-dropdown')&&select.closest('.search-pager')&&owner.contains(select)&&!onchange){
              proof='native-ticket-fluid-pager';
            }else if(modern&&select.matches('.search-limit-dropdown')){
              const match=onchange.match(/^location\.href='([^']*)'\+this\.value\+'([^']*)'\s*;?$/);
              if(!match)continue;
              try{const u=new URL(match[1]+desired+match[2],document.baseURI);if(u.origin!==arg.origin||u.pathname!==arg.path||u.hash||u.username||u.password||u.searchParams.getAll('glpilist_limit').length!==1||u.searchParams.get('glpilist_limit')!==String(desired))continue;action=u.href;method='get';proof='native-ticket-location-pager';}catch{continue;}
            }else{
              const nativeForm=select.form,pager=select.closest('table.tab_cadre_pager,.search-pager');
              if(!nativeForm||!pager?.contains(nativeForm)||nativeForm.hasAttribute('onsubmit')||!/^(?:(?:this\.form|form)\.)?submit\(\)\s*;?$/.test(onchange))continue;
              method=(nativeForm.getAttribute('method')||'get').toLowerCase();if(!['get','post'].includes(method))continue;
              let u;try{u=new URL(nativeForm.getAttribute('action')||location.href,document.baseURI);}catch{continue;}
              if(u.origin!==arg.origin||u.pathname!==arg.path||u.hash||u.username||u.password)continue;
              const allowed=k=>/^(?:meta)?criteria\[/.test(k)||/^(?:is_deleted|as_map|browse|savedsearches_id|start|list_limit|glpilist_limit|sort(?:\[\d+\])?|order(?:\[\d+\])?|reset|itemtype|search|_glpi_csrf_token)$/.test(k);
              if([...u.searchParams.keys()].some(k=>!allowed(k))||[...nativeForm.elements].some(e=>e.name&&!allowed(e.name)))continue;
              if([...nativeForm.elements].filter(e=>e.name==='glpilist_limit').length!==1)continue;
              action=u.href;proof='native-ticket-pager-form';
            }
            if(action){
              const submitted=new URL(action,document.baseURI);
              const nativeForm=proof==='native-ticket-pager-form'?select.form:null;
              const submittedPairs=[...submitted.searchParams].concat(nativeForm?[...nativeForm.elements].filter(e=>e.name&&!e.matches(':disabled')&&!['button','submit','password','file'].includes(e.type)&&(!['checkbox','radio'].includes(e.type)||e.checked)).map(e=>[e.name,e.value]):[]);
              if(submittedPairs.some(([k,v])=>/^(?:meta)?criteria\[/.test(k)&&!pairs.some(p=>p[0]===k&&p[1]===v)))continue;
              if(submittedPairs.some(([k,v])=>k==='start'&&v!==String(start)))continue;
            }
            candidates.push({selector:css(select),value:String(desired),hidden,method,action,proof,currentValue:select.value});
          }
          if(!candidates.length)return {...state,reason:'unavailable'};
          if(new Set(candidates.map(c=>JSON.stringify([c.value,c.currentValue,c.method,c.action,c.proof]))).size!==1)return {...state,reason:'ambiguous'};
          return {...state,...candidates[0]};
        }
        """;

    private sealed class NativeTicketPageLimitDom
    {
        [JsonPropertyName("ready")] public bool Ready { get; set; }
        [JsonPropertyName("documentReady")] public bool DocumentReady { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
        [JsonPropertyName("selector")] public string Selector { get; set; } = "";
        [JsonPropertyName("value")] public string Value { get; set; } = "";
        [JsonPropertyName("currentValue")] public string CurrentValue { get; set; } = "";
        [JsonPropertyName("method")] public string Method { get; set; } = "";
        [JsonPropertyName("action")] public string Action { get; set; } = "";
        [JsonPropertyName("proof")] public string Proof { get; set; } = "";
        [JsonPropertyName("hidden")] public bool Hidden { get; set; }
        [JsonPropertyName("scope")] public string Scope { get; set; } = "";
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("start")] public int Start { get; set; }
        [JsonPropertyName("limit")] public int Limit { get; set; }
        [JsonPropertyName("ids")] public List<string> Ids { get; set; } = [];
        [JsonPropertyName("headers")] public List<string> Headers { get; set; } = [];
    }

    private sealed record NativeTicketPageLimitOutcome(int? AppliedLimit, bool Changed, string Message, bool Attempted);

    public async Task<NativeTicketPageLimitPreparation> PrepareNativeTicketPageLimitAsync(
        TableChoice table, AppSettings settings, IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        var frame = await ResolveFrameAsync(table, ct);
        var source = TicketReferenceResolver.ReadHttpUrl(frame.Url, "La liste des tickets");
        var outcome = await PrepareNativeTicketPageLimitOnPageAsync(table.Page!, frame, source, table.Selector, settings,
            notice => { Log?.Invoke("PAGINATION · " + notice.Message); progress?.Report(new(0, 0, null, notice.Message)); }, ct);
        if (outcome.Attempted) table = await WaitForAutoTableAsync(table.Page!, settings, ct);
        return new(table, outcome.AppliedLimit, outcome.Changed, outcome.Message);
    }

    private async Task<NativeTicketPageLimitOutcome> PrepareNativeTicketPageLimitOnPageAsync(
        IPage page, IFrame frame, Uri source, string selector, AppSettings settings, Action<LibraryIndexProgress> report, CancellationToken ct)
    {
        var attempted = false;
        NativeTicketPageLimitOutcome Finish(int? limit, bool changed, string message)
        { report(new("page-limit", "page-limit", message)); return new(limit, changed, message, attempted); }
        if (!source.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal))
            return Finish(null, false, "Réglage natif du nombre de tickets par page non reconnu ; poursuite avec la pagination disponible.");
        Task<NativeTicketPageLimitDom> Read() => ReadJsonAsync<NativeTicketPageLimitDom>(frame, NativeTicketPageLimitScript,
            new { origin = source.GetLeftPart(UriPartial.Authority), path = source.AbsolutePath,
                detailPath = source.AbsolutePath.Replace("/front/ticket.php", "/front/ticket.form.php", StringComparison.Ordinal), selector }, ct);
        var before = await Read();
        if (!before.Ready || !before.DocumentReady || before.Start != 0 || before.Selector.Length == 0)
            return Finish(before.Ready ? before.Limit : null, false, before.Ready
                ? $"Réglage natif absent ou ambigu ; poursuite à {before.Limit:N0} tickets par page, sans changement de préférence."
                : "Réglage natif du nombre de tickets par page non vérifiable ; poursuite avec la pagination disponible.");
        if (before.Value == before.CurrentValue)
            return Finish(before.Limit, false, before.Limit >= 1000
                ? $"Pagination native déjà réglée à {before.Limit:N0} tickets par page."
                : $"GLPI propose au maximum {before.Limit:N0} tickets par page ; lecture poursuivie avec cette limite native.");
        await LibraryCollectionPacer.BeforeNavigationAsync(ct).ConfigureAwait(false);
        var fresh = await Read();
        if (!fresh.Ready || fresh.Selector != before.Selector || fresh.Value != before.Value || fresh.CurrentValue != before.CurrentValue
            || fresh.Action != before.Action || fresh.Proof != before.Proof || fresh.Scope != before.Scope || fresh.Count != before.Count
            || fresh.Start != 0 || !fresh.Headers.SequenceEqual(before.Headers) || !fresh.Ids.SequenceEqual(before.Ids))
            return Finish(fresh.Ready ? fresh.Limit : null, false, "Le contrôle de pagination a changé ; aucun réglage n’a été envoyé. La lecture vérifiera le périmètre courant.");
        using var activity = new NativeTicketPageLimitActivity(page, frame, source, fresh, settings.ListTimeoutSeconds, report);
        report(new("page-limit", "phase-start", $"Réglage natif à {int.Parse(fresh.Value, CultureInfo.InvariantCulture):N0} tickets par page ; attente de la réponse GLPI…"));
        ct.ThrowIfCancellationRequested();
        // This task is issued once. An uncertain submission is never replayed.
        attempted = true;
        var change = frame.Locator(fresh.Selector).SelectOptionAsync(fresh.Value, new() { Force = fresh.Hidden, Timeout = 0 });
        try { await activity.AwaitAsync(change, ct); }
        catch (PlaywrightException) when (activity.RequestRejected && !activity.HasPendingRequest && !page.IsClosed)
        { /* Only a fresh, unchanged native list below can permit a safe fallback. */ }
        var settled = Stopwatch.StartNew();
        NativeTicketPageLimitDom? last = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested(); activity.ThrowIfFailed();
            try
            {
                last = await activity.AwaitAsync(Read(), ct);
                if (last.Reason == "session") throw new GlpiLibraryIndexException("page-limit", "index-session", "La session GLPI a changé pendant le réglage de la pagination. Aucune lecture n’a été poursuivie.");
                if (last.Ready)
                {
                    if (last.Scope != before.Scope || last.Count != before.Count || last.Start != 0 || !last.Headers.SequenceEqual(before.Headers)
                        || !before.Ids.SequenceEqual(last.Ids.Take(before.Ids.Count)))
                        throw new GlpiLibraryIndexException("page-limit", "index-scope", "Le filtre, le périmètre ou les identités ont changé pendant le réglage de la pagination. La collecte a été arrêtée avant la lecture des fiches.");
                    activity.Observe($"{last.Limit}|{last.Count}|{last.Ids.Count}|{last.DocumentReady}");
                    if (!activity.HasPendingRequest && last.DocumentReady && activity.ResponseObserved && settled.ElapsedMilliseconds >= 350)
                    {
                        if (last.Limit == int.Parse(fresh.Value, CultureInfo.InvariantCulture))
                            return Finish(last.Limit, last.Limit != before.Limit, $"Pagination native vérifiée : {last.Limit:N0} tickets par page. Le réglage est conservé dans la session GLPI ; les fiches restent lues une par une.");
                        return Finish(last.Limit, last.Limit != before.Limit, $"GLPI a conservé {last.Limit:N0} tickets par page ; poursuite avec la limite réellement vérifiée, sans renvoyer le réglage.");
                    }
                }
                else settled.Restart();
            }
            catch (PlaywrightException error) when (!page.IsClosed && (activity.HasPendingRequest || activity.NavigationObserved
                && (error.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
                    || error.Message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase))))
            { settled.Restart(); } // Re-read only: the issued native command is never replayed.
            if (!activity.CanWait)
            {
                if (last is { Ready: true, DocumentReady: true } && last.Limit == before.Limit && last.Scope == before.Scope
                    && last.Count == before.Count && last.Start == 0 && last.Ids.SequenceEqual(before.Ids))
                    return Finish(last.Limit, false, $"Le changement de pagination n’a pas été appliqué ; poursuite à {last.Limit:N0} tickets par page, sans nouvelle tentative.");
                throw new GlpiLibraryIndexException("page-limit", "index-stalled", "La réponse au réglage de la pagination ne permet pas de confirmer la liste. Le réglage n’a pas été renvoyé.");
            }
            await Task.Delay(150, ct);
        }
    }

    private sealed class NativeTicketPageLimitActivity : IDisposable
    {
        private readonly IPage _page;
        private readonly IFrame _frame;
        private readonly Uri _source;
        private readonly NativeTicketPageLimitDom _plan;
        private readonly Action<LibraryIndexProgress> _report;
        private readonly LibraryIndexActivityState _state;
        private readonly HashSet<IRequest> _requests = new(ReferenceEqualityComparer.Instance);
        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly CancellationToken _lifetimeToken;
        private GlpiLibraryIndexException? _failure;
        private bool _response;
        private bool _requestRejected;
        private bool _navigationObserved;
        private IRequest? _documentRequest;
        private int _navigationGeneration;
        private bool _disposed;
        public NativeTicketPageLimitActivity(IPage page, IFrame frame, Uri source, NativeTicketPageLimitDom plan, int timeout, Action<LibraryIndexProgress> report)
        {
            _page = page; _frame = frame; _source = source; _plan = plan; _report = report; _state = new(timeout);
            _lifetimeToken = _lifetime.Token;
            page.Request += OnRequest; page.Response += OnResponse; page.RequestFinished += OnFinished; page.RequestFailed += OnFailed;
            page.FrameNavigated += OnFrameNavigated;
        }
        public bool HasPendingRequest { get { lock (_gate) return _state.HasPendingRequest || _state.DocumentLoading; } }
        public bool ResponseObserved { get { lock (_gate) return _response; } }
        public bool RequestRejected { get { lock (_gate) return _requestRejected; } }
        public bool NavigationObserved { get { lock (_gate) return _navigationObserved; } }
        public bool CanWait
        {
            get
            {
                bool canWait, notice; lock (_gate) { canWait = _state.CanWait; notice = _state.TakePendingNotice(); }
                if (notice) _report(new("page-limit", "request-pending", "Le réglage de pagination attend encore la réponse native GLPI. Attendre ou annuler."));
                return canWait;
            }
        }
        public void Observe(string value) { lock (_gate) _state.Observe(value); }
        public void ThrowIfFailed()
        {
            lock (_gate)
            {
                if (_failure is not null) throw _failure;
                if (_page.IsClosed || _frame.IsDetached) throw new GlpiLibraryIndexException("page-limit", "index-browser", "La page a été fermée pendant le réglage de pagination.");
            }
        }
        private bool Native(IRequest request)
        {
            try
            {
                if (request.Frame != _frame) return false;
                if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var address)) return false;
                // A redirect belongs to the issued command only when its parent
                // request has already been matched; unrelated native activity
                // never supplies proof that the page-size command completed.
                if (request.RedirectedFrom is { } previous && _requests.Contains(previous))
                    return request.IsNavigationRequest && request.Method == "GET" && request.ResourceType == "document"
                        && TicketReferenceResolver.SameOrigin(address, _source) && address.AbsolutePath == _source.AbsolutePath;
                // GLPI 10's own AJAX failure handler reloads the current list.
                // Track that continuation only after the exact pager request
                // failed; it cannot act as proof for an unrelated command.
                if (_requestRejected && _plan.Proof == "native-ticket-fluid-pager" && request.IsNavigationRequest
                    && request.Method == "GET" && request.ResourceType == "document"
                    && TicketReferenceResolver.SameOrigin(address, _source) && address.AbsolutePath == _source.AbsolutePath)
                    return true;
                var legacy = _plan.Method == "post" && request.IsNavigationRequest && request.Method == "POST" && request.ResourceType == "document"
                    && address == new Uri(_plan.Action);
                var location = _plan.Proof == "native-ticket-location-pager" && request.IsNavigationRequest && request.Method == "GET"
                    && request.ResourceType == "document" && address == new Uri(_plan.Action);
                var fluid = _plan.Proof == "native-ticket-fluid-pager" && request.ResourceType is "xhr" or "fetch"
                    && LibraryIndexRequestPolicy.IsNative(_source, request.Url, request.Method, request.ResourceType, false, request.PostData);
                var nativeGetForm = _plan.Proof == "native-ticket-pager-form" && _plan.Method == "get" && request.IsNavigationRequest
                    && request.Method == "GET" && TicketReferenceResolver.SameOrigin(address, _source) && address.AbsolutePath == _source.AbsolutePath;
                if (!legacy && !location && !fluid && !nativeGetForm) return false;
                var body = request.Method == "POST" ? request.PostData ?? "" : "";
                if (body.Length > 262144) return false;
                var pairs = TicketReferenceResolver.ParseQuery(address).Concat(TicketReferenceResolver.ParseQuery(new Uri("https://native-pager.invalid/?" + body))).ToList();
                if (fluid && (pairs.Count(p => p.Key == "action") != 1 || pairs.Single(p => p.Key == "action").Value != "display_results")) return false;
                var limits = pairs.Where(p => p.Key == "glpilist_limit").ToList();
                return limits.Count == 1 && limits[0].Value == _plan.Value;
            }
            catch (Exception error) when (error is PlaywrightException or UriFormatException or ArgumentException) { return false; }
        }
        private void OnRequest(object? sender, IRequest request)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (request.RedirectedFrom is { } previous && _requests.Contains(previous) && !Native(request))
                { _state.RequestEnded(previous); _failure ??= new("page-limit", "index-session", "GLPI a redirigé le réglage de pagination hors de la liste native."); return; }
                if (Native(request))
                {
                    _requests.Add(request); _state.RequestStarted(request);
                    if (request.IsNavigationRequest)
                    {
                        _documentRequest = request; _navigationObserved = true; _navigationGeneration++;
                        _state.BeginDocument();
                    }
                }
            }
        }
        private void OnFrameNavigated(object? sender, IFrame frame)
        {
            int generation;
            lock (_gate)
            {
                if (_disposed || frame != _frame || !_state.DocumentLoading) return;
                generation = _navigationGeneration;
            }
            _ = CompleteDocumentAsync(generation);
        }
        private async Task CompleteDocumentAsync(int generation)
        {
            try
            {
                // A finished HTTP body is not the DOMContentLoaded barrier.
                // Wait for the committed frame, including its deferred scripts.
                await _frame.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 0 }).WaitAsync(_lifetimeToken);
                lock (_gate) { if (!_disposed && generation == _navigationGeneration) _state.DocumentLoaded(); }
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
            catch (PlaywrightException)
            {
                lock (_gate)
                {
                    if (_disposed || generation != _navigationGeneration) return;
                    _state.DocumentLoaded();
                    if (_page.IsClosed || _frame.IsDetached)
                        _failure ??= new("page-limit", "index-browser", "La page native a été fermée pendant le rechargement de la pagination.");
                }
            }
        }
        private void OnResponse(object? sender, IResponse response)
        {
            lock (_gate)
            {
                if (!_requests.Contains(response.Request)) return;
                _state.ResponseReceived(response.Request); _response = true;
                if (response.Status is 401 or 403) _failure ??= new("page-limit", "index-session", $"GLPI a refusé la session pendant le réglage de pagination (HTTP {response.Status}). Aucune commande n’a été renvoyée.");
                else if (response.Status >= 400) _requestRejected = true;
            }
        }
        private void OnFinished(object? sender, IRequest request) { lock (_gate) { if (_requests.Contains(request)) _state.RequestEnded(request); } }
        private void OnFailed(object? sender, IRequest request)
        {
            lock (_gate)
            {
                if (!_requests.Contains(request)) return;
                _state.RequestEnded(request); _requestRejected = true; _response = true;
                if (ReferenceEquals(request, _documentRequest)) _state.DocumentLoaded();
            }
        }
        public async Task<T> AwaitAsync<T>(Task<T> task, CancellationToken ct)
        {
            _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            while (!task.IsCompleted)
            {
                ct.ThrowIfCancellationRequested(); ThrowIfFailed();
                if (!CanWait) throw new GlpiLibraryIndexException("page-limit", "index-stalled", "Aucune progression du réglage de pagination ; la commande n’a pas été renvoyée.");
                await Task.Delay(120, ct);
            }
            ct.ThrowIfCancellationRequested(); ThrowIfFailed(); return await task;
        }
        public void Dispose()
        {
            lock (_gate) _disposed = true;
            _page.Request -= OnRequest; _page.Response -= OnResponse; _page.RequestFinished -= OnFinished; _page.RequestFailed -= OnFailed;
            _page.FrameNavigated -= OnFrameNavigated;
            _lifetime.Cancel(); _lifetime.Dispose();
        }
    }
}
