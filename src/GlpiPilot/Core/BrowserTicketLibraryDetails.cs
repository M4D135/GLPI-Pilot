using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private const string LibraryDetailDomScript = """
        arg => {
          const visible=e=>{if(!(e instanceof Element)||!e.isConnected||e.getClientRects().length===0)return false;for(let n=e;n instanceof Element;n=n.parentElement){if(n.hidden||n.getAttribute('aria-hidden')==='true'||getComputedStyle(n).display==='none'||getComputedStyle(n).visibility==='hidden')return false}return true};
          const text=e=>(e?.innerText||'').trim(), norm=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().trim();
          const css=e=>{const p=[];for(let n=e;n instanceof Element;n=n.parentElement){const id=n.getAttribute('id');if(id&&document.querySelectorAll('#'+CSS.escape(id)).length===1){p.unshift('#'+CSS.escape(id));break}const peers=n.parentElement?[...n.parentElement.children].filter(x=>x.localName===n.localName):[n];p.unshift(n.localName+':nth-of-type('+(peers.indexOf(n)+1)+')')}return p.join(' > ')};
          const business=e=>/^(?:supprimer|purger|enregistrer|mettre a jour|delete|purge|save|update|ajouter|assigner|clore|fermer le ticket|close ticket)\b/.test(norm(text(e))) || /^(?:purge|delete|update|save|add|restore|massiveaction)(?:_|$)/i.test(e.getAttribute('name')||'');
          if(arg.origin!==location.origin)return {ready:false};
          if(arg.mode==='inventory'){
            const candidates=[...document.querySelectorAll('[role=tablist] a,[role=tablist] button,.nav-tabs a,.nav-tabs button,.ui-tabs-nav a,.ui-tabs-nav button')].filter(visible).filter(e=>!business(e)&&!e.matches(':disabled,[aria-disabled=true],.disabled'));
            let unsupported=0;const keys=[];
            for(const e of candidates.slice(0,500)){const proof=window.__glpiPilotTicket.tab({...arg,selector:css(e)});if(!proof.allowed||!proof.targetSelector)unsupported++;else keys.push(proof.key)}
            const unverifiedPagination=candidates.length===0&&[...document.querySelectorAll('#itil-form .search-pager,#itil-form .tab_cadre_pager,#tabsbody .search-pager,#tabsbody .tab_cadre_pager')].some(visible);
            return {ready:true,discovered:candidates.length,unsupported,keys,overflow:candidates.length>60,unverifiedPagination};
          }
          const proof=window.__glpiPilotTicket.tab(arg);
          if(!proof.allowed||!proof.alreadyActive||proof.key!==arg.key||!proof.targetSelector)return {ready:false};
          const roots=[...document.querySelectorAll(proof.targetSelector)];
          if(roots.length!==1||!visible(roots[0]))return {ready:false};
          const root=roots[0];
          if(root.closest('[aria-busy=true]')||[...document.querySelectorAll('#loadingtabs,.loadingindicator')].some(visible))return {ready:false};
          const pagers=[...root.querySelectorAll('.search-pager,.tab_cadre_pager')].filter(visible);
          const ranges=pagers.flatMap(p=>[...p.querySelectorAll('.page-infos,td.b')].filter(visible).map(e=>{
            const numbers=text(e).match(/\d[\d \u00a0\u202f]*/g)||[];
            const values=numbers.map(n=>Number(n.replace(/[ \u00a0\u202f]/g,'')));
            return values.length===3&&values.every(Number.isSafeInteger)&&values[0]>=0&&values[0]<=values[1]&&values[1]<=values[2]?values:null;
          }).filter(Boolean));
          const range=ranges[0]||null,rangeValid=!!range&&ranges.every(r=>JSON.stringify(r)===JSON.stringify(range));
          const forwards=pagers.flatMap(p=>[...p.querySelectorAll('a[href]')].filter(a=>a.matches('.page-link-next')||a.querySelector('.fa-chevron-right,.ti-chevron-right'))).filter(visible).filter(a=>a.getAttribute('aria-disabled')!=='true'&&!a.closest('.disabled'));
          const decoded=forwards.map(a=>{const href=a.getAttribute('href')||'',match=/^javascript:reloadTab\((['"])start=(\d+)\1\);?$/.exec(href);let inline=false;for(let n=a;n&&n!==root;n=n.parentElement)if([...n.attributes].some(x=>/^on/i.test(x.name)))inline=true;return {selector:css(a),href,start:match?Number(match[2]):null,allowed:!!match&&!inline&&!business(a)&&!a.hasAttribute('download')&&Number.isSafeInteger(Number(match?.[2]))}});
          const same=decoded.length>0&&decoded.every(d=>d.allowed&&d.href===decoded[0].href);
          const unexpected=[...root.querySelectorAll('a[rel=next],button,[role=button],a')].filter(visible).filter(e=>!pagers.some(p=>p.contains(e))).some(e=>e.getAttribute('rel')==='next'||/^(?:charger plus|voir plus|afficher plus|load more|show more|suivant|next)(?:\s|$)/i.test(norm(text(e))));
          const hiddenEvents=[...root.querySelectorAll('.h_item,.timeline-item,[data-timeline-item]')].some(e=>!visible(e));
          const links=[];let linkOverflow=false;
          for(const a of root.querySelectorAll('a[href]')){
            if(!visible(a)||business(a)||a.closest('.nav-tabs,.ui-tabs-nav,[role=tablist],.search-pager,.tab_cadre_pager,.timeline_form,.h_controls'))continue;
            let u;try{u=new URL(a.href)}catch{continue}
            if(!/^https?:$/.test(u.protocol)||u.username||u.password||u.href.length>8192||[...u.searchParams.keys()].some(k=>/(?:password|token|csrf|secret|cookie|session|authorization|api.?key|delete|purge|update|save|add|restore|massiveaction)/i.test(k)))continue;
            const label=text(a);if(!label)continue;
            if(links.length>=5000){linkOverflow=true;break}
            const kind=/\/front\/document\.(?:send|form)\.php$/i.test(u.pathname)||a.hasAttribute('download')?'document':'link';
            links.push({label,url:u.href,kind});
          }
          return {ready:true,discovered:0,unsupported:0,keys:[],overflow:false,pager: pagers.length>0,
            rangeStart:rangeValid?range[0]:null,rangeEnd:rangeValid?range[1]:null,rangeTotal:rangeValid?range[2]:null,
            nextSelector:same?decoded[0].selector:'',nextHref:same?decoded[0].href:'',nextStart:same?decoded[0].start:null,
            unverifiedPagination:unexpected||pagers.length>0&&!rangeValid||forwards.length>0&&!same,
            hiddenEvents,links,linkOverflow};
        }
        """;

    private sealed class LibraryDetailDom
    {
        [JsonPropertyName("ready")] public bool Ready { get; set; }
        [JsonPropertyName("discovered")] public int Discovered { get; set; }
        [JsonPropertyName("unsupported")] public int Unsupported { get; set; }
        [JsonPropertyName("keys")] public List<string> Keys { get; set; } = new();
        [JsonPropertyName("overflow")] public bool Overflow { get; set; }
        [JsonPropertyName("pager")] public bool Pager { get; set; }
        [JsonPropertyName("rangeStart")] public int? RangeStart { get; set; }
        [JsonPropertyName("rangeEnd")] public int? RangeEnd { get; set; }
        [JsonPropertyName("rangeTotal")] public int? RangeTotal { get; set; }
        [JsonPropertyName("nextSelector")] public string NextSelector { get; set; } = "";
        [JsonPropertyName("nextHref")] public string NextHref { get; set; } = "";
        [JsonPropertyName("nextStart")] public int? NextStart { get; set; }
        [JsonPropertyName("unverifiedPagination")] public bool UnverifiedPagination { get; set; }
        [JsonPropertyName("hiddenEvents")] public bool HiddenEvents { get; set; }
        [JsonPropertyName("linkOverflow")] public bool LinkOverflow { get; set; }
        [JsonPropertyName("links")] public List<LibraryDetailLink> Links { get; set; } = new();
    }
    private sealed class LibraryDetailLink
    {
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    }

    public async Task<TicketLibraryDetailRead> ReadTicketLibraryDetailsAsync(TicketReference ticket, AppSettings settings,
        IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        var coverage = new TicketLibraryCoverage();
        var detail = await ReadTicketDetailsCoreAsync(ticket, settings, null, progress, ct, coverage);
        return new() { Detail = detail, Coverage = coverage };
    }

    private async Task ReadLibraryConsultationTabsAsync(IPage page, TicketReference ticket, AppSettings settings,
        TicketDetailSettings options, TicketDetailResult result, TicketLibraryCoverage coverage,
        IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        var initial = await ReadLibraryTabInventoryAsync(page, ticket, ct);
        coverage.TabsDiscovered = initial.Sum(item => item.Discovered);
        var knownKeys = result.Tabs.Select(tab => tab.Key).ToHashSet(StringComparer.Ordinal);
        if (initial.Any(item => item.Unsupported > 0 || item.Overflow || !item.Ready) || !knownKeys.SetEquals(initial.SelectMany(item => item.Keys)))
            coverage.Limitations.Add("Certains onglets de consultation sont absents, ambigus, non reconnus ou au-delà de la limite de 60 onglets.");
        if (initial.Any(item => item.UnverifiedPagination))
            coverage.Limitations.Add("Une pagination de la fiche ne possède pas de panneau de consultation reconnu.");
        if (result.Warnings.Count > 0)
            coverage.Limitations.Add("Certaines sections configurées de la fiche n’ont pas pu être lues complètement.");
        var hasDocuments = false;
        foreach (var tab in result.Tabs)
        {
            ct.ThrowIfCancellationRequested();
            if (result.Sections.Sum(section => section.Data.Rows.Count) >= Math.Clamp(settings.MaxRows, 1, 1000000))
            { coverage.Limitations.Add("La limite de lignes de détail a été atteinte avant la lecture de tous les onglets."); break; }
            tab.Loaded = false;
            var tabComplete = true;
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            int? expectedTotal = null;
            var expectedStart = 1;
            var pageNumber = 0;
            while (true)
            {
                var part = new TicketDetailResult { Ticket = ticket, ReadAt = result.ReadAt };
                await ReadDiscoveredTicketTabAsync(page, ticket, options, part, tab, progress, ct);
                if (!tab.Loaded)
                {
                    result.Warnings.AddRange(part.Warnings);
                    coverage.Limitations.Add("Un onglet de consultation n’est pas devenu lisible.");
                    tabComplete = false; break;
                }
                var located = await ReadLibraryDetailDomAsync(page, ticket, tab, ct);
                if (located.Frame == null || located.Dom == null || !located.Dom.Ready)
                { coverage.Limitations.Add("Le panneau de l’onglet a changé pendant la lecture."); tabComplete = false; break; }
                var dom = located.Dom;
                var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(part.Sections.Select(section => new { section.Selector, section.Kind, section.Data.Headers, section.Data.Rows })))));
                if (!signatures.Add(signature))
                { coverage.Limitations.Add("Une page de détail a été répétée ; la pagination a été arrêtée."); tabComplete = false; break; }
                if (dom.Pager && dom.RangeStart.HasValue && dom.RangeTotal.HasValue)
                {
                    expectedTotal ??= dom.RangeTotal;
                    if (dom.RangeTotal != expectedTotal || dom.RangeStart != expectedStart)
                    { coverage.Limitations.Add("La plage ou le total d’un onglet ne correspond pas à la pagination attendue."); tabComplete = false; break; }
                }
                if (dom.HiddenEvents || dom.UnverifiedPagination || dom.LinkOverflow)
                {
                    if (dom.HiddenEvents) coverage.Limitations.Add("Des événements sont masqués dans un onglet ; leur contenu n’a pas été déclaré lu.");
                    if (dom.UnverifiedPagination) coverage.Limitations.Add("Une pagination ou un chargement supplémentaire n’a pas pu être reconnu en lecture seule.");
                    if (dom.LinkOverflow) coverage.Limitations.Add("La limite de références de documents ou de liens a été atteinte.");
                    tabComplete = false;
                }
                if (part.Sections.Any(section => !section.Data.Complete || section.Data.Warnings.Any(warning => warning.Contains("limite", StringComparison.OrdinalIgnoreCase) || warning.Contains("masqu", StringComparison.OrdinalIgnoreCase))))
                { coverage.Limitations.Add("Une section de détail est limitée ou comporte du contenu masqué."); tabComplete = false; }
                foreach (var link in dom.Links)
                {
                    hasDocuments |= link.Kind == "document";
                    if (!part.ObservedItems.Any(item => item.Url == link.Url && item.Title == link.Label))
                        part.ObservedItems.Add(new() { Kind = link.Kind, Title = link.Label, Url = link.Url, SourceTabKey = tab.Key, SourceTabSelector = tab.Selector, SourceSection = "Liens et documents", ObservedAt = result.ReadAt });
                }
                if (dom.Links.Count > 0)
                    part.Sections.Add(new()
                    {
                        Name = "Liens et documents", Kind = "table", Selector = "library:references:" + tab.Key, TabKey = tab.Key, TabName = tab.Name, TabSelector = tab.Selector,
                        Data = new() { Headers = new() { "Type", "Nom", "Adresse" }, Rows = dom.Links.Select(link => new[] { link.Kind == "document" ? "Document" : "Lien", link.Label, link.Url }).ToList(),
                            Complete = !dom.LinkOverflow, Pages = 1, ExtractedAt = result.ReadAt, Scope = "Références consultables ; contenu binaire non téléchargé" }
                    });
                MergeLibraryDetailPart(result, part, tab, pageNumber > 0);
                pageNumber++;
                if (!tabComplete || dom.NextSelector.Length == 0)
                {
                    if (dom.Pager && dom.RangeEnd != dom.RangeTotal)
                    { coverage.Limitations.Add("La pagination d’un onglet s’est arrêtée avant son total annoncé."); tabComplete = false; }
                    break;
                }
                if (pageNumber >= Math.Clamp(settings.MaxPages, 1, 10000) || result.Sections.Sum(section => section.Data.Rows.Count) >= Math.Clamp(settings.MaxRows, 1, 1000000))
                { coverage.Limitations.Add("La limite de pages ou de lignes de détail a été atteinte."); tabComplete = false; break; }
                if (dom.NextStart != dom.RangeEnd)
                { coverage.Limitations.Add("Le lien suivant d’un onglet ne correspond pas à la plage attendue."); tabComplete = false; break; }
                var again = await ReadLibraryDetailDomAsync(page, ticket, tab, ct);
                if (again.Frame != located.Frame || again.Dom == null || !again.Dom.Ready || again.Dom.NextHref != dom.NextHref || again.Dom.NextSelector != dom.NextSelector || again.Dom.RangeEnd != dom.RangeEnd)
                { coverage.Limitations.Add("Le contrôle de pagination d’un onglet a changé avant la lecture suivante."); tabComplete = false; break; }
                EnsureTicketPageIdentity(page, ticket);
                await located.Frame.Locator(dom.NextSelector).ClickAsync(new() { Timeout = _ticketDetailTimeoutMilliseconds }).WaitAsync(ct);
                expectedStart = dom.RangeEnd!.Value + 1;
                var timer = Stopwatch.StartNew();
                var changed = false;
                do
                {
                    ct.ThrowIfCancellationRequested(); await Task.Delay(150, ct);
                    var next = await ReadLibraryDetailDomAsync(page, ticket, tab, ct);
                    if (next.Dom?.Ready == true && next.Dom.RangeStart == expectedStart) { changed = true; break; }
                } while (timer.Elapsed.TotalMilliseconds < _ticketDetailTimeoutMilliseconds);
                if (!changed) { coverage.Limitations.Add("La page suivante d’un onglet n’a pas été confirmée."); tabComplete = false; break; }
            }
            if (tabComplete) coverage.TabsLoaded++;
            progress?.Report(new(coverage.TabsLoaded, result.Sections.Sum(section => section.Data.Rows.Count), coverage.TabsDiscovered, "Lecture des onglets consultables de la bibliothèque…"));
        }
        var final = await ReadLibraryTabInventoryAsync(page, ticket, ct);
        if (final.Any(item => !item.Ready || item.Unsupported > 0 || item.Overflow) || !knownKeys.SetEquals(final.SelectMany(item => item.Keys)) || final.Sum(item => item.Discovered) != coverage.TabsDiscovered)
            coverage.Limitations.Add("La liste des onglets consultables a changé pendant la lecture.");
        if (result.Sections.Count == 0 || result.Sections.Any(section => !section.Data.Complete || section.Data.Warnings.Any(warning => warning.Contains("limite", StringComparison.OrdinalIgnoreCase) || warning.Contains("masqu", StringComparison.OrdinalIgnoreCase))))
            coverage.Limitations.Add("La fiche ne comporte pas uniquement des sections complètement lues.");
        if (result.Sections.Sum(section => section.Data.Rows.Count) > Math.Clamp(settings.MaxRows, 1, 1000000))
            coverage.Limitations.Add("Le nombre de lignes de détail dépasse la limite configurée ; la couverture complète n’est pas déclarée.");
        coverage.Limitations = coverage.Limitations.Distinct(StringComparer.Ordinal).ToList();
        coverage.Complete = coverage.Limitations.Count == 0 && coverage.TabsLoaded == coverage.TabsDiscovered;
        if (hasDocuments || result.ObservedItems.Any(item => item.Kind == "document")) coverage.Limitations.Add("Les pièces jointes sont référencées ; leur contenu binaire n’est pas téléchargé automatiquement.");
        result.Warnings.AddRange(coverage.Limitations);
        result.Warnings = result.Warnings.Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task<List<LibraryDetailDom>> ReadLibraryTabInventoryAsync(IPage page, TicketReference ticket, CancellationToken ct)
    {
        var result = new List<LibraryDetailDom>();
        foreach (var frame in await TicketFramesAsync(page, ticket, ct))
        {
            await EnsureTicketBridgeAsync(frame, ct);
            result.Add(await ReadTicketJsonAsync<LibraryDetailDom>(frame, LibraryDetailDomScript,
                new { mode = "inventory", id = ticket.Id, origin = Origin(ticket.Url), ticketPath = new Uri(ticket.Url).AbsolutePath }, ct));
        }
        return result;
    }

    private async Task<(IFrame? Frame, LibraryDetailDom? Dom)> ReadLibraryDetailDomAsync(IPage page, TicketReference ticket, TicketDetailTab tab, CancellationToken ct)
    {
        EnsureTicketPageIdentity(page, ticket);
        var found = new List<(IFrame Frame, LibraryDetailDom Dom)>();
        foreach (var frame in await TicketFramesAsync(page, ticket, ct))
        {
            await EnsureTicketBridgeAsync(frame, ct);
            var dom = await ReadTicketJsonAsync<LibraryDetailDom>(frame, LibraryDetailDomScript,
                new { mode = "page", id = ticket.Id, origin = Origin(ticket.Url), ticketPath = new Uri(ticket.Url).AbsolutePath, selector = tab.Selector, key = tab.Key }, ct);
            if (dom.Ready) found.Add((frame, dom));
        }
        EnsureTicketPageIdentity(page, ticket);
        return found.Count == 1 ? found[0] : (null, null);
    }

    private static void MergeLibraryDetailPart(TicketDetailResult result, TicketDetailResult part, TicketDetailTab tab, bool appendPage)
    {
        foreach (var section in part.Sections)
        {
            section.TabKey = tab.Key; section.TabName = tab.Name; section.TabSelector = tab.Selector;
            var existing = result.Sections.FirstOrDefault(item => item.Selector == section.Selector && item.Kind == section.Kind
                && (item.TabKey == tab.Key || item.TabKey.Length == 0));
            if (existing == null) { result.Sections.Add(section); continue; }
            if (!existing.Data.Headers.SequenceEqual(section.Data.Headers, StringComparer.Ordinal))
            { result.Warnings.Add("Les colonnes d’une section ont changé pendant la lecture des détails."); existing.Data.Complete = false; continue; }
            if (!appendPage)
            {
                existing.TabKey = tab.Key; existing.TabName = tab.Name; existing.TabSelector = tab.Selector;
                if (JsonSerializer.Serialize(existing.Data.Rows) != JsonSerializer.Serialize(section.Data.Rows))
                { existing.Data = section.Data; existing.Data.Complete = false; result.Warnings.Add("Une section a changé entre son ouverture et sa lecture dans l’onglet."); }
                continue;
            }
            existing.Data.Rows.AddRange(section.Data.Rows);
            existing.Data.Pages++;
            existing.Data.ExpectedTotal = existing.Data.Rows.Count;
            existing.Data.Complete &= section.Data.Complete;
            existing.Data.Warnings.AddRange(section.Data.Warnings);
        }
        var known = result.ObservedItems.Select(item => (item.Kind, item.Identity, item.Title, item.Url, item.Date, item.SourceTabKey)).ToHashSet();
        foreach (var item in part.ObservedItems)
            if (known.Add((item.Kind, item.Identity, item.Title, item.Url, item.Date, item.SourceTabKey))) result.ObservedItems.Add(item);
        result.Warnings.AddRange(part.Warnings);
    }
}
