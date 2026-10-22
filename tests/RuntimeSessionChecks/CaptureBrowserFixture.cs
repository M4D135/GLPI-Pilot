using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using GlpiPilot.Core;
using Microsoft.Playwright;

/// <summary>In-memory routed native GLPI markup, shared by Runtime and WebView workflow tests.</summary>
public sealed class TicketLibraryCaptureBrowserFixture : IAsyncDisposable
{
    public const string Modified = "2026-10-20T12:34:00Z";
    public string SourceUrl { get; }
    public IBrowserContext Context { get; }
    public BrowserService Service { get; } = new();
    public IPage Page { get; private set; } = null!;
    public List<string> TicketIds { get; } = ["41", "42", "43"];
    public HashSet<string> FailDetails { get; } = new(StringComparer.Ordinal);
    public HashSet<string> PartialDetails { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<string> DetailRequests { get; } = new();
    public int FailIndexNumber { get; set; }
    public int PauseIndexNumber { get; set; }
    public int AllIndexRequests => Volatile.Read(ref _allIndexRequests);
    public string Entity { get; set; } = "Entité synthétique A";
    private int _allIndexRequests;
    public string ScopeFingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new Uri(SourceUrl).GetLeftPart(UriPartial.Path)
        + "\n{\"context\":[],\"header\":[\"" + Entity + "\"]}")));

    private TicketLibraryCaptureBrowserFixture(IBrowserContext context, string source) { Context = context; SourceUrl = source; }
    public static async Task<TicketLibraryCaptureBrowserFixture> OpenAsync(IBrowser browser, string source)
    {
        var fixture = new TicketLibraryCaptureBrowserFixture(await browser.NewContextAsync(), source);
        await fixture.Context.RouteAsync("**/*", fixture.RouteAsync);
        fixture.Page = await fixture.Context.NewPageAsync(); await fixture.Page.GotoAsync(source);
        fixture.Service.AttachForTesting(fixture.Context, source); return fixture;
    }

    public TicketLibraryIndex Index(params string[] ids) => new()
    {
        SourceUrl = SourceUrl, ScopeFingerprint = ScopeFingerprint, Complete = true, AllStatuses = true,
        ExpectedTotal = ids.Length, Tickets = ids.Select(Seed).ToList()
    };
    public TicketLibrarySeed Seed(string id)
    {
        var uri = new Uri(SourceUrl); var title = "Ticket synthétique " + id;
        return new()
        {
            Ticket = new(id, uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.Replace("/front/ticket.php", "/front/ticket.form.php", StringComparison.Ordinal) + "?id=" + id),
            Title = title, Status = "Résolu", StatusCode = 5, LastModifiedRaw = Modified,
            LastModifiedAt = new(2026, 10, 20, 12, 34, 0, TimeSpan.Zero), LastModifiedReliable = true, LastModifiedPrecision = "seconds",
            IndexHeaders = ["Titre", "Statut", "Dernière modification"], IndexValues = [title, "Résolu", Modified],
            Fields = new() { ["Titre"] = title, ["Statut"] = "Résolu", ["Dernière modification"] = Modified }
        };
    }

    public async Task WaitForIndexAsync(int number)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (AllIndexRequests < number) await Task.Delay(20, deadline.Token);
    }

    private async Task RouteAsync(IRoute route)
    {
        var uri = new Uri(route.Request.Url); var source = new Uri(SourceUrl);
        if (uri.Host != source.Host || uri.Port != source.Port || route.Request.Method != "GET") { await route.AbortAsync(); return; }
        if (uri.AbsolutePath == source.AbsolutePath)
        {
            var all = TicketReferenceResolver.ParseQuery(uri).Any(p => p.Key == "criteria[0][value]" && p.Value == "all");
            var number = all ? Interlocked.Increment(ref _allIndexRequests) : 0;
            var bad = all && number == FailIndexNumber; var busy = all && number == PauseIndexNumber;
            await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = ListHtml(all, bad, busy) }); return;
        }
        if (uri.AbsolutePath.EndsWith("/front/ticket.form.php", StringComparison.Ordinal))
        {
            var id = TicketReferenceResolver.ParseQuery(uri).Single(p => p.Key == "id").Value; DetailRequests.Enqueue(id);
            if (FailDetails.Contains(id)) { await route.AbortAsync("failed"); return; }
            await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = DetailHtml(id, PartialDetails.Contains(id)) }); return;
        }
        await route.AbortAsync();
    }

    private string ListHtml(bool all, bool bad, bool busy)
    {
        var source = new Uri(SourceUrl); var basePath = source.AbsolutePath[..source.AbsolutePath.IndexOf("/front/", StringComparison.Ordinal)];
        var ids = all ? TicketIds.ToArray() : TicketIds.Take(1).ToArray();
        var rows = string.Join("", ids.Select(id => $"<tr><td><a href='{basePath}/front/ticket.form.php?id={id}'>Ticket synthétique {id}</a></td><td>Résolu</td><td>{Modified}</td></tr>"));
        return $$"""
            <!doctype html><html lang="fr"><meta charset="utf-8"><title>Index de bibliothèque synthétique</title><body>
            <div class="page"><header class="navbar">{{WebUtility.HtmlEncode(Entity)}}</header></div><a href="{{basePath}}/front/logout.php">Déconnexion</a>
            <form name="searchformticket" class="search-form-container" method="get" action="{{source.AbsolutePath}}">
              <select name="criteria[0][field]"><option value="12" selected>Statut</option></select>
              <select name="criteria[0][searchtype]"><option value="equals" selected>est</option></select>
              <select name="criteria[0][value]"><option value="5">Résolu</option><option value="notold" {{(all ? "" : "selected")}}>Non résolu</option><option value="all" {{(all ? "selected" : "")}}>Tous</option></select>
              <input type="hidden" name="is_deleted" value="0"><input type="hidden" name="as_map" value="0"><input type="hidden" name="browse" value="0">
              <a class="search-reset" href="{{source.AbsolutePath}}?reset=reset">Vider</a><button type="submit" name="search">Rechercher</button>
            </form>
            <form data-search-itemtype="Ticket" data-start="0" data-count="{{ids.Length}}" data-limit="100" action="{{basePath}}/front/massiveaction.php">
              {{(busy ? "<div class='loadingindicator'>Chargement synthétique</div>" : "")}}
              <table class="{{(bad ? "unsupported-results" : "search-results")}}"><thead><tr><th data-searchopt-id="1">Titre</th><th data-searchopt-id="12">Statut</th><th data-searchopt-id="19">Dernière modification</th></tr></thead>
                <tbody>{{rows}}</tbody></table>
            </form></body></html>
            """;
    }

    private static string DetailHtml(string id, bool partial) => $$"""
        <!doctype html><html lang="fr"><meta charset="utf-8"><title>Ticket synthétique {{id}}</title><body>
        <a href="logout.php">Déconnexion</a><ul role="tablist" class="ui-tabs-nav"><li role="tab"><a class="ui-tabs-anchor" href="#itil-form" aria-selected="true">Ticket</a></li></ul>
        <div id="itil-form" role="tabpanel"><form name="form" action="ticket.form.php?id={{id}}">
          <input type="hidden" name="id" value="{{id}}"><label for="title">Titre</label><input id="title" name="name" value="Ticket synthétique {{id}}">
          <label for="description">Description</label><textarea id="description" name="content">Contenu synthétique intégral {{id}}, sans données privées.</textarea>
          <button type="submit">Enregistrer</button></form>{{(partial ? "<button>Voir plus</button>" : "")}}</div></body></html>
        """;

    public async ValueTask DisposeAsync() { await Service.DisposeAsync(); await Context.DisposeAsync(); }
}
