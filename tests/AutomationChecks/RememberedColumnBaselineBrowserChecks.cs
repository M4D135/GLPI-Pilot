using System.Net;
using GlpiPilot.Core;
using Microsoft.Playwright;

internal static class RememberedColumnBaselineBrowserChecks
{
    private const string Origin = "http://glpi-column-baseline.test";
    private const string Target = Origin + "/glpi/front/ticket.php?criteria%5B0%5D%5Bfield%5D=12&criteria%5B0%5D%5Bvalue%5D=1";
    private static readonly string[] BaselineHeaders = ["Titre", "ID", "Statut"];
    private static readonly string[] CollectedHeaders = ["Titre", "ID", "Statut", "Priorité"];

    public static async Task CheckAsync(IBrowser browser)
    {
        using var storage = new ColumnTestStorageScope();
        await using var fixture = await Fixture.CreateAsync(browser, storage.Root);
        var tables = await fixture.Service.InspectAsync(fixture.Page, CancellationToken.None);
        var original = tables.Single(table => table.Selector.StartsWith("#remembered_a_", StringComparison.Ordinal));
        var competitor = tables.Single(table => table.Selector == "#competitor_b");
        var automaticChoice = await fixture.Service.ChooseAutoTableAsync(fixture.Page, fixture.Settings, CancellationToken.None);
        Require(automaticChoice.Selector == competitor.Selector && original.Headers.SequenceEqual(BaselineHeaders),
            "Without an explicit remembered selection, the visible native B table must outrank A; otherwise this fixture cannot reproduce the regression.");
        var untouchedB = await fixture.Page.Locator("#competitor-region").EvaluateAsync<string>("element => element.outerHTML");
        fixture.Settings.RememberTable(original);
        fixture.Settings.Save();

        for (var collection = 1; collection <= 2; collection++)
        {
            var settings = AppSettings.Load();
            Require(settings.TableHeaderFingerprint == original.HeaderFingerprint && settings.PortalUrl == Target && settings.TableTargetUrl == Target,
                "Each collection must start from A's persisted baseline and the unchanged filtered search.");
            var run = await new AutomaticExtractionService(fixture.Service).RunAsync(settings, null, CancellationToken.None);
            var baseline = run.TableToRemember ?? throw new InvalidOperationException("Automatic collection must return the original table selection to persist.");
            Require(run.Table.Selector.StartsWith("#remembered_a_", StringComparison.Ordinal)
                && run.Table.Headers.SequenceEqual(CollectedHeaders) && run.Data.Headers.SequenceEqual(CollectedHeaders)
                && run.Data.Rows.Count == 1 && run.Data.Rows[0].SequenceEqual(new[] { "Ticket A mémorisé", "17", "Nouveau", "Haute" })
                && File.Exists(run.PrimaryPath), "Both real automatic collections must export the enriched A table and its ticket, never B.");
            Require(!ReferenceEquals(baseline, run.Table)
                && baseline.Headers.SequenceEqual(BaselineHeaders) && baseline.HeaderFingerprint == original.HeaderFingerprint
                && baseline.HeaderFingerprint != run.Table.HeaderFingerprint && baseline.FrameOrigin == original.FrameOrigin
                && baseline.FrameName == original.FrameName && baseline.Selector.StartsWith("#remembered_a_", StringComparison.Ordinal),
                "The returned table to remember must retain the original selection separately from the enriched collection table.");
            Require(settings.TableHeaderFingerprint == original.HeaderFingerprint && settings.TableSelector == baseline.Selector
                && settings.TableFrameOrigin == baseline.FrameOrigin && settings.TableFrameName == baseline.FrameName,
                "RunAsync must restore the mutable settings to A's baseline before its caller saves them.");
            Require(run.Data.Provenance.ColumnPreparation is
                { AddedCount: 1, RestoredCount: 1, TemporaryView: true, RecoveryPending: false, RestorationState: "restored" }
                && !fixture.PriorityAdded && fixture.Additions == collection && fixture.Removals == collection
                && fixture.ExportsA == collection && !fixture.Service.HasPendingTemporaryColumns(settings)
                && !new TemporaryColumnJournalStore().Exists(settings),
                "Each collection must add and restore exactly its own temporary column and durably finish recovery.");

            // Exercise the same result contract as the runtime, then a genuine
            // settings round trip before the next navigation/collection.
            settings.RememberTable(run.TableToRemember ?? run.Table);
            settings.Save();
            var persisted = AppSettings.Load();
            Require(persisted.TableHeaderFingerprint == original.HeaderFingerprint && persisted.TableSelector == baseline.Selector
                && persisted.TableFrameOrigin == original.FrameOrigin && persisted.TableFrameName == original.FrameName
                && persisted.PortalUrl == Target && persisted.TableTargetUrl == Target,
                "Persisting the collection result must retain A's initial fingerprint, frame identity and original search criteria.");
            await fixture.Page.GotoAsync(Target);
            var next = await fixture.Service.ChooseAutoTableAsync(fixture.Page, persisted, CancellationToken.None);
            Require(next.Headers.SequenceEqual(BaselineHeaders) && next.Selector.StartsWith("#remembered_a_", StringComparison.Ordinal),
                "After the native view is restored and its HTML IDs change, the persisted baseline must still select A.");

            var overwritten = persisted.Copy();
            overwritten.RememberTable(run.Table);
            var wrong = await fixture.Service.ChooseAutoTableAsync(fixture.Page, overwritten, CancellationToken.None);
            Require(wrong.Selector == competitor.Selector,
                "Remembering the enriched fingerprint must reproduce the old failure by selecting B on the restored page.");
            Require(fixture.PreferencesB == 0 && fixture.WritesB == 0 && fixture.ExportsB == 0
                && await fixture.Page.Locator("#competitor-region").EvaluateAsync<string>("element => element.outerHTML") == untouchedB,
                "B must retain all its initial controls and cells; neither its preferences nor its native export may be used.");
        }
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture(string root) : IAsyncDisposable
    {
        private int _render;
        private int _preferenceId = 800;
        public BrowserService Service { get; } = new();
        public IPage Page { get; private set; } = null!;
        public bool PriorityAdded { get; private set; }
        public int Additions { get; private set; }
        public int Removals { get; private set; }
        public int ExportsA { get; private set; }
        public int ExportsB { get; private set; }
        public int PreferencesB { get; private set; }
        public int WritesB { get; private set; }
        public AppSettings Settings { get; } = new()
        {
            PortalUrl = Target, SelectorOrigin = Origin, ColumnPreparationMode = "supervision", AutoMode = "native",
            ExportFolder = Path.Combine(root, "Exports"), AutoSaveXlsx = false, ResumePagination = false,
            Cities = new() { Enabled = false }, TimeoutSeconds = 5, ListTimeoutSeconds = 5, DownloadTimeoutSeconds = 5,
            ExpectedTotal = 1, MaxRows = 20, MaxPages = 5
        };

        public static async Task<Fixture> CreateAsync(IBrowser browser, string root)
        {
            var fixture = new Fixture(root);
            var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
            await context.RouteAsync("**/*", fixture.HandleAsync);
            fixture.Service.AttachForTesting(context, Target);
            fixture.Page = await context.NewPageAsync();
            await fixture.Page.GotoAsync(Target);
            return fixture;
        }

        private static string Hidden(string name, string value) => "<input type='hidden' name='" + name + "' value='" + value + "'>";
        private static string Html(string body) => "<!doctype html><html lang='fr'><meta charset='utf-8'><title>GLPI — Deux tableaux</title>"
            + "<style>body{font:14px sans-serif}iframe{width:900px;height:600px}.ui-dialog{position:fixed;inset:8px;background:white;z-index:10;padding:12px}button,input,select{min-height:28px}td{padding:4px}[hidden]{display:none!important}</style>"
            + "<body><a href='/glpi/logout.php'>Déconnexion</a>" + body + "</body></html>";
        private static string PersonalForm(string body) => "<form method='post' action='/glpi/front/displaypreference.form.php'>"
            + Hidden("itemtype", "Ticket") + Hidden("users_id", "17") + Hidden("_glpi_csrf_token", "synthetic") + body + "</form>";
        private static string Selected(string label, string id) => "<tr><td>" + label + "</td><td>"
            + PersonalForm(Hidden("id", id) + "<button name='purge' type='submit'>Supprimer</button>") + "</td><td></td><td></td></tr>";
        private string Preferences() => Html("<select name='profiles_id'><option value='4' selected>Assistance</option></select>"
            + "<select name='active_entity'><option value='2' selected>Entité synthétique</option></select><div id='tabsbody'><table><tr><th>"
            + PersonalForm("<input name='disable' type='submit' value='Désactiver'>") + "</th></tr><tr><td colspan='4'>"
            + PersonalForm("<select name='num'>" + (PriorityAdded ? "" : "<option value='3'>Priorité</option>")
                + "</select><input name='add' type='submit' value='Ajouter'>") + "</td></tr>"
            + "<tr><td>Titre</td><td colspan='3'></td></tr>" + Selected("ID", "102") + Selected("Statut", "112")
            + (PriorityAdded ? Selected("Priorité", _preferenceId.ToString()) : "") + "</table></div>");
        private static string ExportForm(string table) => "<div class='pager'><table class='tab_cadre_pager'><tr><td>"
            + "<form method='GET' action='/glpi/front/report.dynamic.php'>" + Hidden("item_type", "Ticket") + Hidden("fixture_table", table)
            + Hidden("start", "0") + Hidden("criteria[0][field]", "12") + Hidden("criteria[0][value]", "1")
            + "<select name='display_type'><option value='4'>Page courante en CSV</option><option value='-4'>Toutes les pages en CSV</option></select>"
            + "<button type='submit' name='export' title='Exporter'>Exporter</button></form></td><td>De 1 à 1 sur 1</td></tr></table></div>";

        private string ListPage() => Html("<form name='searchformTicket'>" + Hidden("criteria[0][field]", "12") + Hidden("criteria[0][value]", "1") + "</form>"
            + "<div class='legacy-search'><form id='massformTicket1' data-search-itemtype='Ticket' data-count='1' data-start='0' data-limit='20'>"
            + "<table id='remembered_a_" + ++_render + "' class='tab_cadre_fixehov'><thead><tr><th>Titre</th><th>ID</th><th>Statut</th>"
            + (PriorityAdded ? "<th>Priorité</th>" : "") + "</tr></thead><tbody><tr><td>Ticket A mémorisé</td><td>17</td><td>Nouveau</td>"
            + (PriorityAdded ? "<td>Haute</td>" : "") + "</tr></tbody></table></form>" + ExportForm("A") + "</div>"
            + "<div id='competitor-region' class='search-card'><button type='button' class='show_displaypreference_modal' id='competitor-columns'>Colonnes B</button>"
            + "<form id='massformTicket2' data-search-itemtype='Ticket' data-count='1' data-start='0' data-limit='20'>"
            + "<table id='competitor_b' class='search-results'><thead><tr><th>ID</th><th>Catégorie</th><th>Entité</th><th>Priorité</th><th>Groupe de techniciens</th></tr></thead>"
            + "<tbody><tr><td>99</td><td>Tableau B</td><td>Autre entité</td><td>Basse</td><td>Autre groupe</td></tr></tbody></table></form>" + ExportForm("B") + "</div>"
            + "<span class='fa-wrench pointer' onclick=\"$('#search_config_top').dialog('open');\">Colonnes A</span>"
            + "<div id='dialog' class='ui-dialog' role='dialog' hidden><button class='ui-dialog-titlebar-close'>Fermer</button>"
            + "<div id='search_config_top'><iframe id='Iframesearch_config_top'></iframe></div></div>"
            + "<script>window.$=()=>({dialog:()=>{document.querySelector('#dialog').hidden=false;document.querySelector('iframe').src='/glpi/front/displaypreference.form.php?itemtype=Ticket';}});"
            + "document.querySelector('.ui-dialog-titlebar-close').onclick=()=>location.reload();"
            + "document.querySelector('#competitor-columns').onclick=()=>{document.querySelector('#dialog').hidden=false;document.querySelector('iframe').src='/glpi/front/displaypreference.form.php?itemtype=Ticket&fixture_table=B';};</script>");

        private async Task HandleAsync(IRoute route)
        {
            var uri = new Uri(route.Request.Url);
            var query = Pairs(uri.Query);
            if (uri.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal))
            { await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = ListPage() }); return; }
            if (uri.AbsolutePath.EndsWith("/front/report.dynamic.php", StringComparison.Ordinal))
            {
                Require(route.Request.Method == "GET" && query.GetValueOrDefault("display_type") == "-4"
                    && query.GetValueOrDefault("criteria[0][value]") == "1", "Native exports must preserve all-pages mode and the recorded search filter.");
                var competitor = query.GetValueOrDefault("fixture_table") == "B";
                if (competitor) ExportsB++; else ExportsA++;
                var csv = competitor ? "ID;Catégorie;Entité;Priorité;Groupe de techniciens\r\n99;Tableau B;Autre entité;Basse;Autre groupe\r\n"
                    : string.Join(';', PriorityAdded ? CollectedHeaders : BaselineHeaders) + "\r\nTicket A mémorisé;17;Nouveau" + (PriorityAdded ? ";Haute" : "") + "\r\n";
                await route.FulfillAsync(new() { ContentType = "text/csv; charset=utf-8", Headers = new Dictionary<string, string>
                    { ["Content-Disposition"] = "attachment; filename=\"glpi.csv\"" }, Body = "\uFEFF" + csv }); return;
            }
            if (!uri.AbsolutePath.EndsWith("/front/displaypreference.form.php", StringComparison.Ordinal))
            { await route.AbortAsync(); return; }
            var values = Pairs(route.Request.PostData);
            if (query.GetValueOrDefault("fixture_table") == "B" || values.GetValueOrDefault("fixture_table") == "B")
            {
                PreferencesB++; if (route.Request.Method == "POST") WritesB++;
                await route.FulfillAsync(new() { Status = 403, ContentType = "text/html", Body = "Les préférences de B ne doivent pas être utilisées." }); return;
            }
            if (route.Request.Method == "POST")
            {
                Require(values.GetValueOrDefault("itemtype") == "Ticket" && values.GetValueOrDefault("users_id") == "17",
                    "Only A's personal Ticket view may receive a mutation.");
                var command = new TemporaryColumnJournalStore().Load(Settings)?.PendingCommand;
                if (values.ContainsKey("add"))
                {
                    Require(values.GetValueOrDefault("num") == "3" && !PriorityAdded && command is { Kind: "add", OptionValue: "3" },
                        "Each temporary priority addition must have its own durable intent and must never duplicate an initial column.");
                    PriorityAdded = true; Additions++; _preferenceId++;
                }
                else
                {
                    Require(values.ContainsKey("purge") && values.GetValueOrDefault("id") == _preferenceId.ToString() && PriorityAdded
                        && command is { Kind: "remove" } && command.TargetIdentity == "preference:" + _preferenceId,
                        "Restoration must remove only the recorded temporary preference, preserving A's original columns.");
                    PriorityAdded = false; Removals++;
                }
            }
            await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Preferences() });
        }

        private static Dictionary<string, string> Pairs(string? value) => (value ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).GroupBy(parts => WebUtility.UrlDecode(parts[0]))
            .ToDictionary(group => group.Key, group => WebUtility.UrlDecode(group.First().Length > 1 ? group.First()[1] : ""), StringComparer.Ordinal);
        public async ValueTask DisposeAsync() => await Service.DisposeAsync();
    }
}
