using System.Collections.Concurrent;
using GlpiPilot.Core;
using Microsoft.Playwright;

internal static class ColdTicketChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(IBrowser browser, string html, string source, string ticket)
    {
        yield return ("Démarrage fiche : journal de colonnes en attente conservé, aucune liste ni préférence chargée",
            () => PendingRecovery(browser, html, source, ticket));
        yield return ("Démarrage fiche : SSO naturel dans l’onglet dédié sans collecte ni restauration",
            () => PendingRecovery(browser, html, source, ticket, sso: true));
        yield return ("Démarrage fiche : références étrangères et actions refusées avant création du navigateur",
            () => UnsafeReference(browser, html, source, ticket));
        yield return ("Démarrage fiche : changement d’installation et de navigateur dans un contexte isolé",
            () => ChangedContext(browser, html, source, ticket));
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static async Task PendingRecovery(IBrowser browser, string html, string source, string ticket, bool sso = false)
    {
        await using var fixture = new Fixture(browser, html, source, sso);
        var journal = fixture.SeedPendingRecovery();
        var before = await File.ReadAllBytesAsync(journal);
        Require(!fixture.Service.IsOpen && fixture.Contexts.Count == 0, "Le scénario doit démarrer sans contexte navigateur.");
        var result = await fixture.Service.ReadTicketDetailsAsync(new("42", ticket), fixture.Settings, null, CancellationToken.None);
        Require(result.Ticket.Id == "42" && result.Sections.Count > 0, "La fiche n’a pas été lue malgré sa référence valide.");
        Require(fixture.Contexts.Count == 1 && fixture.Service.TicketPage is { IsClosed: false }, "La lecture n’a pas ouvert son contexte et son onglet dédiés.");
        Require(fixture.Service.ActivePage is null, "La fiche a été adoptée comme recherche active.");
        Require((await fixture.Service.GetPagesAsync()).Count == 0, "Une fiche est proposée comme source de collecte.");
        var after = await File.ReadAllBytesAsync(journal);
        Require(fixture.Service.HasPendingTemporaryColumns(fixture.Settings)
            && before.SequenceEqual(after), "La lecture de fiche a modifié ou clôturé le journal de restauration.");
        fixture.AssertNoCollection();
        Require(fixture.Requests.Count(request => request.Path.EndsWith("ticket.form.php", StringComparison.Ordinal)) == (sso ? 2 : 1),
            "Le parcours de fiche n’a pas suivi uniquement son ouverture et son éventuel retour SSO.");
        if (sso) Require(fixture.Requests.Any(request => request.Host == "idp-ticket.test"), "Le scénario n’a pas exercé la connexion SSO.");
    }

    private static async Task UnsafeReference(IBrowser browser, string html, string source, string ticket)
    {
        await using var fixture = new Fixture(browser, html, source);
        foreach (var target in new[]
        {
            ticket.Replace("glpi-ticket.test", "foreign-ticket.test", StringComparison.Ordinal),
            ticket.Replace("/glpi/", "/other/", StringComparison.Ordinal),
            ticket + "&purge=1", ticket.Replace("id=42", "id=99", StringComparison.Ordinal)
        })
        {
            var rejected = false;
            try { await fixture.Service.ReadTicketDetailsAsync(new("42", target), fixture.Settings, null, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Une référence étrangère ou une action a été acceptée à froid.");
        }
        Require(!fixture.Service.IsOpen && fixture.Contexts.Count == 0 && fixture.Requests.IsEmpty,
            "Le navigateur a été ouvert avant de refuser la référence de fiche.");
    }

    private static async Task ChangedContext(IBrowser browser, string html, string source, string ticket)
    {
        await using var fixture = new Fixture(browser, html, source);
        await fixture.Service.ReadTicketDetailsAsync(new("42", ticket), fixture.Settings, null, CancellationToken.None);
        var firstPage = fixture.Service.TicketPage!;
        fixture.Settings.PortalUrl = source.Replace("/glpi/", "/other/", StringComparison.Ordinal);
        var otherTicket = ticket.Replace("/glpi/", "/other/", StringComparison.Ordinal);
        var second = await fixture.Service.ReadTicketDetailsAsync(new("42", otherTicket), fixture.Settings, null, CancellationToken.None);
        var secondPage = fixture.Service.TicketPage!;
        Require(fixture.Contexts.Count == 2 && firstPage.IsClosed && !ReferenceEquals(firstPage.Context, secondPage.Context)
            && second.Ticket.Url == otherTicket, "Le changement d’installation a réutilisé le contexte de la source précédente.");
        fixture.Settings.BrowserChannel = BrowserChannels.Chrome;
        var third = await fixture.Service.ReadTicketDetailsAsync(new("42", otherTicket), fixture.Settings, null, CancellationToken.None);
        Require(fixture.Contexts.Count == 3 && secondPage.IsClosed && !ReferenceEquals(secondPage.Context, fixture.Service.TicketPage!.Context)
            && third.Sections.Count > 0, "Le changement de navigateur a réutilisé le contexte précédent.");
        fixture.AssertNoCollection();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _previousRoot = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_ROOT");
        private readonly string? _previousMode = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_MODE");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-ColdTicket-" + Guid.NewGuid().ToString("N"));
        public BrowserService Service { get; }
        public AppSettings Settings { get; }
        public List<IBrowserContext> Contexts { get; } = new();
        public ConcurrentQueue<(string Host, string Path, string Method)> Requests { get; } = new();

        public Fixture(IBrowser browser, string html, string source, bool sso = false)
        {
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1");
            Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", _root);
            Settings = new() { PortalUrl = source, TimeoutSeconds = 7, ListTimeoutSeconds = 5, DetailTimeoutSeconds = 5,
                TicketDetails = new() { AutoReadTreatment = false } };
            var ticketRequests = 0;
            Service = new BrowserService(async ct =>
            {
                ct.ThrowIfCancellationRequested();
                var context = await browser.NewContextAsync();
                Contexts.Add(context);
                await context.RouteAsync("**/*", async route =>
                {
                    var uri = new Uri(route.Request.Url);
                    Requests.Enqueue((uri.Host, uri.AbsolutePath, route.Request.Method));
                    if (sso && uri.Host == "idp-ticket.test")
                    {
                        await route.FulfillAsync(new() { ContentType = "text/html", Body = "<title>Connexion SSO</title><input type='password'><script>setTimeout(()=>location.href='http://glpi-ticket.test/glpi/front/central.php',300)</script>" });
                        return;
                    }
                    if (uri.Host != "glpi-ticket.test") { await route.AbortAsync(); return; }
                    if (uri.AbsolutePath.EndsWith("/front/ticket.form.php", StringComparison.Ordinal))
                    {
                        var installationPath = uri.AbsolutePath[..^"front/ticket.form.php".Length];
                        var pageHtml = html.Replace("/glpi/", installationPath, StringComparison.Ordinal)
                            .Replace("%2Fglpi%2F", Uri.EscapeDataString(installationPath), StringComparison.Ordinal);
                        var body = sso && Interlocked.Increment(ref ticketRequests) == 1
                            ? "<!doctype html><script>location.href='http://idp-ticket.test/login'</script>" : pageHtml;
                        await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = body });
                    }
                    else if (sso && uri.AbsolutePath.EndsWith("/front/central.php", StringComparison.Ordinal))
                        await route.FulfillAsync(new() { ContentType = "text/html", Body = "<a href='/glpi/front/logout.php'>Déconnexion</a><h1>Accueil</h1>" });
                    else await route.AbortAsync();
                });
                return context;
            });
        }

        public string SeedPendingRecovery()
        {
            var store = new TemporaryColumnJournalStore();
            var original = new TemporaryColumnEntry { Value = "2", PreferenceId = "20", Label = "Titre" };
            var added = new TemporaryColumnEntry { Value = "12", PreferenceId = "21", Label = "Statut" };
            var source = new Uri(Settings.PortalUrl);
            store.Create(Settings, new()
            {
                InstallationKey = ObjectIdentity.InstallationKey(Settings.PortalUrl), BrowserChannel = Settings.BrowserChannel,
                Origin = source.GetLeftPart(UriPartial.Authority),
                PreferencesUrl = source.GetLeftPart(UriPartial.Authority) + "/glpi/front/displaypreference.form.php?itemtype=Ticket",
                UsersId = "7", Layout = "modern", SourceScopeFingerprint = new('A', 64), ContextFingerprint = new('B', 64),
                State = "pending", Original = new() { PersonalActive = true, Columns = new() { original } },
                Expected = new() { PersonalActive = true, Columns = new() { original, added } }, Added = new() { added }
            });
            return store.PathFor(Settings);
        }

        public void AssertNoCollection()
        {
            Require(!Requests.Any(request => request.Path.EndsWith("/front/ticket.php", StringComparison.Ordinal)
                || request.Path.Contains("displaypreference", StringComparison.Ordinal)), "La lecture a chargé la liste ou les préférences de colonnes.");
            Require(Requests.All(request => request.Method == "GET"), "Une commande de modification a été envoyée pendant la lecture.");
        }

        public async ValueTask DisposeAsync()
        {
            try { await Service.DisposeAsync(); }
            finally
            {
                Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", _previousRoot);
                Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", _previousMode);
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
        }
    }
}
