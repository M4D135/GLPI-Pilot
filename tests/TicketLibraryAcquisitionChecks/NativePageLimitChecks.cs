using System.Collections.Concurrent;
using System.Net;
using GlpiPilot.Core;
using Microsoft.Playwright;

internal static class NativePageLimitChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Create(IBrowser browser)
    {
        yield return ("Pagination 1000 : POST GLPI 9.5, Select2 masqué, deux pagers et 1105 identités exactes", () => Applied(browser, new() { Mode = "legacy", Hidden = true }, 1000, 2));
        yield return ("Pagination 1000 : GLPI 10 fluide, réponse AJAX et 1105 identités exactes", () => Applied(browser, new() { Mode = "fluid" }, 1000, 2));
        yield return ("Pagination 1000 : GLPI 10 navigation native, option 2000 sans inventer 1000", () => Applied(browser, new() { Mode = "location", Sizes = [25, 50, 2000] }, 2000, 1));
        yield return ("Pagination 1000 : maximum natif 50 conservé avec parcours complet", () => Applied(browser, new() { Mode = "legacy", Sizes = [25, 50], Count = 105 }, 50, 3));
        yield return ("Pagination 1000 : limite existante 2000 conservée sans commande", () => Applied(browser, new() { Mode = "fluid", InitialLimit = 2000 }, 2000, 1, commands: 0));
        yield return ("Pagination 1000 : contrôles contradictoires ne provoquent aucun changement", () => Applied(browser, new() { Mode = "legacy", Ambiguous = true, Count = 105 }, 25, 5, commands: 0));
        yield return ("Pagination 1000 : refus non bloquant vérifie la limite réellement appliquée", () => Applied(browser, new() { Mode = "legacy", IgnoreChange = true, Count = 105 }, 25, 5));
        yield return ("Pagination 1000 : refus AJAX 500 conserve la liste initiale vérifiée", () => Applied(browser, new() { Mode = "fluid", AjaxRejected = true, Count = 105 }, 25, 5));
        yield return ("Pagination 1000 : rechargement natif après refus AJAX attendu avant le repli", () => Applied(browser, new() { Mode = "fluid", AjaxRejected = true, ReloadOnRefusal = true, Count = 105 }, 25, 5));
        yield return ("Pagination 1000 : ancien paramètre explicite et limite masquée restaurent le filtre avec 1000", () => Applied(browser, new() { Mode = "legacy", HiddenSearchLimit = true }, 1000, 2));
        yield return ("Pagination 1000 : réponse native étrangère au réglage ne prouve jamais son application", () => UnrelatedResponse(browser));
        yield return ("Pagination 1000 : réponse native lente attendue sans renvoyer la commande", () => Applied(browser, new() { Mode = "fluid", DelayMilliseconds = 6000 }, 1000, 2));
        yield return ("Pagination 1000 : filtre ou entité modifiés refusés avant toute fiche", () => ScopeDrift(browser));
        yield return ("Pagination 1000 : annulation ferme la requête dédiée sans répétition", () => Cancel(browser));
    }

    private static async Task Applied(IBrowser browser, Options options, int expectedLimit, int pages, int commands = 1)
    {
        await using var fixture = await Fixture.Start(browser, options);
        var initial = await fixture.Search.ContentAsync();
        var result = await fixture.Service.ReadTicketLibraryIndexAsync(fixture.Settings, null, CancellationToken.None, "dmy");
        Require(result.Complete && result.AllStatuses && result.ExpectedTotal == options.Count && result.Tickets.Count == options.Count
            && result.PagesRead == pages && result.SearchRestored && result.Limitations.Count == 0,
            "La pagination native ne conserve pas une couverture complète : " + string.Join(" | ", result.Limitations));
        Require(result.Tickets.Select(t => t.Ticket.Id).SequenceEqual(Enumerable.Range(1, options.Count).Select(i => i.ToString())),
            "Identité perdue, ajoutée ou relue après le changement de limite.");
        Require(fixture.Limit == expectedLimit && fixture.Commands == commands,
            $"Limite ou nombre de commandes incorrect : {fixture.Limit}, {fixture.Commands}.");
        Require(fixture.DetailReads == 0 && fixture.Context.Pages.Count == 1 && fixture.Service.ActivePage == fixture.Search
            && await fixture.Search.ContentAsync() == initial, "Une fiche a été ouverte ou la recherche affichée a changé.");
        Require(fixture.Logs.Any(m => m.Contains(expectedLimit.ToString("N0"), StringComparison.Ordinal)), "La limite réelle n’est pas journalisée.");
        if (expectedLimit != options.InitialLimit)
            Require(fixture.Logs.Any(m => m.Contains("conservé dans la session", StringComparison.Ordinal)), "La persistance du réglage de session n’est pas explicitée.");
        Require(fixture.Requests.Last().Contains("notold", StringComparison.Ordinal), "Le filtre de session précédent n’a pas été restauré.");
    }

    private static async Task ScopeDrift(IBrowser browser)
    {
        foreach (var drift in new[] { "entity", "criteria", "identities" })
        {
            await using var fixture = await Fixture.Start(browser, new() { Mode = "legacy", Drift = drift });
            try
            {
                await fixture.Service.ReadTicketLibraryIndexAsync(fixture.Settings, null, CancellationToken.None);
                throw new InvalidOperationException("Le changement de périmètre a été accepté : " + drift);
            }
            catch (GlpiLibraryIndexException error)
            { Require(error.Category == "index-scope", "La dérive n’est pas identifiée comme une différence de périmètre."); }
            Require(fixture.Commands == 1 && fixture.DetailReads == 0 && fixture.Context.Pages.Count == 1,
                "Une commande a été répétée ou une fiche ouverte après la dérive.");
        }
    }

    private static async Task Cancel(IBrowser browser)
    {
        await using var fixture = await Fixture.Start(browser, new() { Mode = "legacy", DelayMilliseconds = 6000 });
        using var cancellation = new CancellationTokenSource();
        fixture.OnCommand = () => cancellation.CancelAfter(350);
        try
        {
            await fixture.Service.ReadTicketLibraryIndexAsync(fixture.Settings, null, cancellation.Token);
            throw new InvalidOperationException("L’annulation de la pagination a été ignorée.");
        }
        catch (OperationCanceledException) { }
        Require(fixture.Commands == 1 && fixture.Context.Pages.Count == 1 && fixture.DetailReads == 0,
            "Une requête annulée reste active ou le changement a été renvoyé.");
    }

    private static async Task UnrelatedResponse(IBrowser browser)
    {
        await using var fixture = await Fixture.Start(browser, new() { Mode = "fluid", UnrelatedOnly = true });
        try
        {
            await fixture.Service.ReadTicketLibraryIndexAsync(fixture.Settings, null, CancellationToken.None);
            throw new InvalidOperationException("Une réponse sans le paramètre de pagination a validé le réglage.");
        }
        catch (GlpiLibraryIndexException error)
        { Require(error.Category == "index-stalled", "La réponse étrangère n’est pas distinguée de la commande de pagination."); }
        Require(fixture.Commands == 0 && fixture.DetailReads == 0 && fixture.Context.Pages.Count == 1,
            "La commande a été fabriquée, répétée ou suivie d’une lecture de fiche.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Escape(string value) => WebUtility.HtmlEncode(value);
    private static string Query(Uri uri, string name) => TicketReferenceResolver.ParseQuery(uri).FirstOrDefault(p => p.Key == name).Value ?? "";

    private sealed class Options
    {
        public string Mode { get; init; } = "legacy";
        public int InitialLimit { get; init; } = 25;
        public int[] Sizes { get; init; } = [25, 50, 1000, 2000];
        public int Count { get; init; } = 1105;
        public bool Hidden { get; init; }
        public bool Ambiguous { get; init; }
        public bool IgnoreChange { get; init; }
        public bool AjaxRejected { get; init; }
        public bool ReloadOnRefusal { get; init; }
        public bool HiddenSearchLimit { get; init; }
        public bool UnrelatedOnly { get; init; }
        public int DelayMilliseconds { get; init; }
        public string Drift { get; init; } = "";
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Root = "http://glpi-page-limit.test/glpi/front/ticket.php";
        private const string Source = Root + "?criteria%5B0%5D%5Bfield%5D=12&criteria%5B0%5D%5Bsearchtype%5D=equals&criteria%5B0%5D%5Bvalue%5D=notold";
        private readonly Options _options;
        private string _entity = "A";
        private bool _changed;
        private bool _rejected;
        public IBrowserContext Context { get; }
        public IPage Search { get; private set; } = null!;
        public BrowserService Service { get; } = new();
        public AppSettings Settings { get; } = new() { PortalUrl = Source, ListTimeoutSeconds = 5, MaxPages = 100, MaxRows = 5000 };
        public ConcurrentQueue<string> Logs { get; } = new();
        public ConcurrentQueue<string> Requests { get; } = new();
        public int Limit { get; private set; }
        public int Commands { get; private set; }
        public int DetailReads { get; private set; }
        public Action? OnCommand { get; set; }

        private Fixture(IBrowserContext context, Options options)
        {
            Context = context; _options = options; Limit = options.InitialLimit;
            if (options.HiddenSearchLimit) Settings.PortalUrl = Source + "&glpilist_limit=" + options.InitialLimit;
        }
        public static async Task<Fixture> Start(IBrowser browser, Options options)
        {
            var fixture = new Fixture(await browser.NewContextAsync(), options);
            await fixture.Context.RouteAsync("**/*", fixture.Route);
            fixture.Search = await fixture.Context.NewPageAsync();
            await fixture.Search.GotoAsync(fixture.Settings.PortalUrl);
            fixture.Service.AttachForTesting(fixture.Context, fixture.Settings.PortalUrl);
            await fixture.Service.FindBestPageAsync(fixture.Settings, fixture.Search, CancellationToken.None);
            fixture.Service.Log += fixture.Logs.Enqueue;
            return fixture;
        }

        private async Task Route(IRoute route)
        {
            var uri = new Uri(route.Request.Url);
            Requests.Enqueue(uri.AbsoluteUri);
            try
            {
                if (uri.Host != "glpi-page-limit.test") { await route.AbortAsync(); return; }
                if (uri.AbsolutePath.EndsWith("/front/ticket.form.php", StringComparison.Ordinal))
                { DetailReads++; await route.AbortAsync(); return; }
                var ajax = uri.AbsolutePath.EndsWith("/ajax/search.php", StringComparison.Ordinal);
                if (!ajax && uri.AbsolutePath != "/glpi/front/ticket.php") { await route.AbortAsync(); return; }
                var body = new Uri("http://fixture-body.test/?" + (route.Request.PostData ?? ""));
                var requested = route.Request.Method == "POST" ? Query(body, "glpilist_limit") : Query(uri, "glpilist_limit");
                if (requested.Length > 0 && (ajax || route.Request.Method == "POST" || Query(uri, "criteria[0][value]") == "all"))
                {
                    Commands++; OnCommand?.Invoke();
                    if (_options.DelayMilliseconds > 0) await Task.Delay(_options.DelayMilliseconds);
                    if (_options.AjaxRejected) { _rejected = true; await route.FulfillAsync(new() { Status = 500, Body = "Synthetic optional pager refusal" }); return; }
                    if (!_options.IgnoreChange) Limit = int.Parse(requested);
                    _changed = true;
                    if (_options.Drift == "entity") _entity = "B";
                }
                else if (requested.Length > 0) Limit = int.Parse(requested);
                if (ajax && _options.UnrelatedOnly) Limit = 1000;
                if (!ajax && _rejected && _options.ReloadOnRefusal && Query(uri, "criteria[0][value]") == "all" && Query(uri, "start") is "" or "0")
                { _rejected = false; await Task.Delay(6000); }
                var target = ajax ? new Uri(Query(route.Request.Method == "POST" ? body : uri, "list_url")) : uri;
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = ajax ? Results(target) : Html(target) });
            }
            catch (PlaywrightException) { /* Cancellation deliberately closes the owned request. */ }
        }

        private string NativeUrl(Uri uri, int start)
        {
            var address = new UriBuilder(uri);
            address.Query = string.Join('&', TicketReferenceResolver.ParseQuery(uri)
                .Where(p => p.Key is not ("start" or "glpilist_limit" or "list_limit" or "reset"))
                .Append(new("start", start.ToString())).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            return address.Uri.AbsoluteUri;
        }

        private string Dropdown(Uri uri, int start, bool second)
        {
            var sizes = second && _options.Ambiguous ? new[] { 25, 50, 2000 } : _options.Sizes;
            var options = string.Join("", sizes.Select(size => $"<option value='{size}'{(Limit == size ? " selected" : "")}>{size}</option>"));
            var id = second ? "native-limit-bottom" : "native-limit-top";
            var hidden = _options.Hidden ? " style='display:none' aria-hidden='true'" : "";
            var marker = _options.Hidden ? " select2-hidden-accessible" : "";
            var action = NativeUrl(uri, start);
            var native = _options.Mode switch
            {
                "legacy" => "<form method='POST' action='" + Escape(action) + "'><select name='glpilist_limit' id='" + id + "' class='" + marker + "' onchange='submit()'" + hidden + ">" + options + "</select></form>",
                "location" => "<select id='" + id + "' class='search-limit-dropdown" + marker + "' onchange=\"" + Escape("location.href='" + action + "&glpilist_limit='+this.value+''") + "\"" + hidden + ">" + options + "</select>",
                _ => "<select id='" + id + "' class='search-limit-dropdown" + marker + "'" + hidden + ">" + options + "</select>"
            };
            // Select2's visible widget is adjacent to its native select.
            if (_options.Hidden) native = native.Replace("</select>", "</select><span class='select2 select2-container'>" + Limit + "</span>", StringComparison.Ordinal);
            return native;
        }

        private string Results(Uri uri)
        {
            var all = Query(uri, "criteria[0][value]") == "all";
            var count = all ? _options.Count : 3;
            var start = int.TryParse(Query(uri, "start"), out var offset) ? offset : 0;
            var ids = Enumerable.Range(start + 1, Math.Min(Limit, Math.Max(0, count - start))).ToArray();
            if (_changed && _options.Drift == "identities" && ids.Length > 0) ids[0] = 999999;
            var rows = string.Join("", ids.Select(id => "<tr><td><a href='/glpi/front/ticket.form.php?id=" + id + "'>Ticket " + id + "</a></td><td>Nouveau</td><td>22/10/2026 14:30</td></tr>"));
            var headers = _options.Mode == "legacy"
                ? "<th><a href='" + Escape(NativeUrl(uri, start) + "&sort=1") + "'>Titre</a></th><th><a href='" + Escape(NativeUrl(uri, start) + "&sort=12") + "'>Statut</a></th><th><a href='" + Escape(NativeUrl(uri, start) + "&sort=19") + "'>Dernière modification</a></th>"
                : "<th data-searchopt-id='1'>Titre</th><th data-searchopt-id='12'>Statut</th><th data-searchopt-id='19'>Dernière modification</th>";
            var table = "<table id='native-results' class='" + (_options.Mode == "legacy" ? "tab_cadrehov" : "search-results") + "'><thead><tr>" + headers + "</tr></thead><tbody>" + rows + "</tbody></table>";
            var nextStart = start + Limit;
            var next = nextStart < count ? "<a " + (_options.Mode == "legacy" ? "" : "class='page-link-next' data-start='" + nextStart + "' ") + "href='" + Escape(NativeUrl(uri, nextStart)) + "'><i class='fa-chevron-right'></i>Suivant</a>" : "";
            var range = $"{start + 1}-{Math.Min(start + Limit, count)}/{count}";
            if (_options.Mode == "legacy")
            {
                string Pager(bool second) => "<table class='tab_cadre_pager'><tbody><tr><td>" + Dropdown(uri, start, second) + "</td><td class='b'>" + range + "</td><td>" + next + "</td></tr></tbody></table>";
                return "<div id='result-region'>" + Pager(false) + "<form name='massformTicket'>" + table + "</form>" + Pager(true) + "</div>";
            }
            return "<form data-search-itemtype='Ticket' data-count='" + count + "' data-start='" + start + "' data-limit='" + Limit + "'>" + table
                + "<div class='search-pager'>" + Dropdown(uri, start, false) + "<p class='page-infos'>" + range + "</p>" + next + "</div></form>";
        }

        private string Html(Uri uri)
        {
            var value = Query(uri, "criteria[0][value]") == "all" ? "all" : "notold";
            if (_changed && _options.Drift == "criteria") value = "notold";
            var ajax = _options.Mode == "fluid" ? "document.addEventListener('change',async e=>{if(!e.target.matches('.search-limit-dropdown'))return;const result=document.querySelector('form[data-search-itemtype]');result.setAttribute('aria-busy','true');const query=new URLSearchParams({action:'display_results',itemtype:'Ticket'," + (_options.UnrelatedOnly ? "" : "glpilist_limit:e.target.value,") + "list_url:location.href});const response=await fetch('/glpi/ajax/search.php?'+query);if(response.ok){result.outerHTML=await response.text()}else{" + (_options.ReloadOnRefusal ? "location.reload();" : "e.target.value=result.getAttribute('data-limit');result.removeAttribute('aria-busy');") + "}});" : "";
            return "<!doctype html><html><meta charset='utf-8'><body><div class='page'><header class='navbar'>Entité " + _entity + "</header></div><a href='/glpi/front/logout.php'>Déconnexion</a>"
                + "<form name='searchformticket' method='get' action='/glpi/front/ticket.php'><select name='criteria[0][field]'><option value='12'>Statut</option></select>"
                + "<select name='criteria[0][searchtype]'><option value='equals'>est</option></select><select name='criteria[0][value]'><option value='1'>Nouveau</option><option value='notold'" + (value == "notold" ? " selected" : "") + ">Non résolu</option><option value='all'" + (value == "all" ? " selected" : "") + ">Tous</option></select>"
                + "<input type='hidden' name='is_deleted' value='0'><input type='hidden' name='as_map' value='0'><input type='hidden' name='browse' value='0'>" + (_options.HiddenSearchLimit ? "<input type='hidden' name='list_limit' value='" + Limit + "'>" : "") + "<a class='search-reset' href='/glpi/front/ticket.php?reset=reset'>Vider</a></form>"
                + Results(uri) + "<script>" + ajax + "</script></body></html>";
        }
        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }
}
