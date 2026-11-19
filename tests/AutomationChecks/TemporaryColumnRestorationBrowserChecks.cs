using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Playwright;

internal static class TemporaryColumnRestorationBrowserChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(IBrowser browser)
    {
        yield return ("Colonnes temporaires : legacy, export enrichi puis ordre personnel initial restauré", () => CheckCompleted(browser, modern: false));
        yield return ("Colonnes temporaires : moderne, colonnes personnelles hors supervision conservées", () => CheckCompleted(browser, modern: true));
        yield return ("Colonnes temporaires : vue créée pour toutes les colonnes puis désactivée sans toucher la globale", () => CheckActivated(browser));
        yield return ("Colonnes temporaires : activation GLPI 11 déjà complète, retrait d’une copie puis retour global", () => CheckCompleteActivation(browser));
        yield return ("Colonnes temporaires : deuxième ajout refusé, premier ajout restauré sans export", () => CheckPartialFailure(browser));
        yield return ("Colonnes temporaires : annulation entre ajouts, récupération avant la prochaine liste", () => CheckReconnect(browser, cancel: true));
        yield return ("Colonnes temporaires : réponse perdue après écriture, nouvelle session et aucun ajout rejoué", () => CheckReconnect(browser, cancel: false));
        yield return ("Colonnes temporaires : sauvegarde initiale impossible, aucune mutation native", () => CheckBackupFailure(browser));
        yield return ("Colonnes temporaires : autre utilisateur connecté, journal conservé sans toucher sa vue", () => CheckDrift(browser, "user"));
        yield return ("Colonnes temporaires : profil modifié, restauration suspendue puis reprise vérifiée", () => CheckDrift(browser, "profile"));
        yield return ("Colonnes temporaires : origine redirigée, aucune commande étrangère ni globale", () => CheckDrift(browser, "origin"));
        yield return ("Colonnes temporaires : modifications concurrentes préservées en legacy et en moderne", () => CheckConcurrentChanges(browser));
        yield return ("Colonnes temporaires : restauration terminée, nouvelle session sans répétition de retrait", () => CheckNoReplay(browser));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class ImmediateProgress(Action<ExtractionProgress> report) : IProgress<ExtractionProgress>
    { public void Report(ExtractionProgress value) => report(value); }

    private static async Task CheckCompleted(IBrowser browser, bool modern)
    {
        using var storage = new ColumnTestStorageScope();
        await using var fixture = await RestorationFixture.CreateAsync(browser, storage.Root, modern);
        var result = await new AutomaticExtractionService(fixture.Service).RunAsync(fixture.Settings, null, CancellationToken.None);
        Require(fixture.Adds == 2 && fixture.Removals == 2 && fixture.Exports == 1 && fixture.Disables == 0
            && fixture.Columns.SequenceEqual(fixture.Original) && fixture.PersonalActive,
            "The real export must use the two temporary columns, then leave the exact initial personal order active.");
        Require(result.Data.Headers.SequenceEqual(fixture.ExportHeaders) && result.Data.Headers.Count == fixture.Original.Count + 3
            && await File.ReadAllTextAsync(result.PrimaryPath) == fixture.ExportedCsv.TrimStart('\uFEFF')
            && result.Data.Provenance.ColumnPreparation is { AddedCount: 2, RecoveryPending: false, RestorationState: "restored" },
            "The saved native CSV must retain its enriched schema after the browser preference has been restored.");
        fixture.AssertClean();
    }

    private static async Task CheckActivated(IBrowser browser)
    {
        using var storage = new ColumnTestStorageScope();
        await using var fixture = await RestorationFixture.CreateAsync(browser, storage.Root, modern: true, active: false);
        fixture.Settings.ColumnPreparationMode = "all";
        var result = await new AutomaticExtractionService(fixture.Service).RunAsync(fixture.Settings, null, CancellationToken.None);
        Require(fixture.Activations == 1 && fixture.Adds == 2 && fixture.Removals == 2 && fixture.Disables == 1 && !fixture.PersonalActive
            && fixture.Columns.SequenceEqual(fixture.Original) && fixture.Exports == 1,
            "The temporary personal view must be returned to its initial inactive state using only native personal commands.");
        Require(fixture.DisableHiddenDuringFullView && result.Data.Headers.Count == fixture.Original.Count + 3,
            "The fixture must hide modern disable while all options are present, and still export every temporary option before cleanup.");
        fixture.AssertClean();
    }

    private static async Task CheckPartialFailure(IBrowser browser)
    {
        using var storage = new ColumnTestStorageScope();
        await using var fixture = await RestorationFixture.CreateAsync(browser, storage.Root, modern: false);
        fixture.RejectSecondAddition = true;
        Exception? failure = null;
        try { await new AutomaticExtractionService(fixture.Service).RunAsync(fixture.Settings, null, CancellationToken.None); }
        catch (InvalidOperationException error) { failure = error; }
        Require(failure is not null && failure.Message.Contains("403", StringComparison.Ordinal)
            && fixture.AddAttempts == 2 && fixture.Adds == 1 && fixture.Removals == 1 && fixture.Exports == 0
            && fixture.Columns.SequenceEqual(fixture.Original),
            "An explicit second-add rejection must keep its original error, restore the first confirmed addition and never export partial success.");
        fixture.AssertClean();
    }

    private static async Task CheckCompleteActivation(IBrowser browser)
    {
        using var storage = new ColumnTestStorageScope();
        await using var fixture = await RestorationFixture.CreateAsync(browser, storage.Root, modern: true, active: false, complete: true);
        fixture.Settings.ColumnPreparationMode = "all";
        var result = await new AutomaticExtractionService(fixture.Service).RunAsync(fixture.Settings, null, CancellationToken.None);
        Require(fixture.Activations == 1 && fixture.AddAttempts == 0 && fixture.Removals == 1 && fixture.Disables == 1
            && fixture.DisableHiddenDuringFullView && !fixture.PersonalActive && fixture.Columns.SequenceEqual(fixture.Original)
            && fixture.Exports == 1 && result.Data.Headers.SequenceEqual(fixture.Original.Select(fixture.Label).Prepend("Titre")),
            "When activation copies every global option, one owned personal copy may be removed to reveal native disable, then the original global view must be restored unchanged.");
        Require(result.Data.Provenance.ColumnPreparation is { AddedCount: 0, RecoveryPending: false, RestorationState: "restored" },
            "The export must retain its complete original schema without falsely reporting a useful-column addition.");
        fixture.AssertClean();
    }

    private static async Task CheckReconnect(IBrowser browser, bool cancel)
    {
        using var storage = new ColumnTestStorageScope();
        await using var fixture = await RestorationFixture.CreateAsync(browser, storage.Root, modern: !cancel);
        fixture.LoseFirstAdditionResponse = !cancel;
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(value =>
        {
            if (cancel && value.Message.StartsWith("Ajout de colonne 2", StringComparison.Ordinal))
            { fixture.BlockRecoveryUntilReconnect = true; cancellation.Cancel(); }
        });
        Exception? interruption = null;
        try
        {
            if (cancel) await new AutomaticExtractionService(fixture.Service).RunAsync(fixture.Settings, progress, cancellation.Token);
            else await fixture.PrepareAsync(progress, cancellation.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { interruption = error; }
        Require(interruption is not null && (cancel ? interruption is OperationCanceledException : fixture.ResponseLost)
            && fixture.Adds == 1 && fixture.Removals == 0 && fixture.Exports == 0
            && fixture.Service.HasPendingTemporaryColumns(fixture.Settings),
            "The interrupted preparation must leave one attributable server-side addition and a durable recovery record.");
        if (cancel) Require(fixture.BlockedRecoveryReads > 0,
            "Cancellation must attempt its independent cleanup immediately; the durable journal may remain only because GLPI is unavailable in this fixture.");
        if (!cancel)
            Require(new TemporaryColumnJournalStore().Load(fixture.Settings)?.PendingCommand is { Kind: "add", ResponseObserved: false },
                "A response lost after persistence must remain an unresolved add intent, not a fictional successful response.");
        fixture.BlockRecoveryUntilReconnect = false; await fixture.RestartAsync();
        fixture.Events.Clear(); fixture.RequireRestorationBeforeList = true;
        fixture.Settings.ColumnPreparationMode = "current";
        var result = await new AutomaticExtractionService(fixture.Service).RunAsync(fixture.Settings, null, CancellationToken.None);
        var remove = fixture.Events.FindIndex(value => value.StartsWith("remove:", StringComparison.Ordinal));
        var list = fixture.Events.FindIndex(value => value == "list");
        Require(remove >= 0 && list > remove && fixture.AddAttempts == 1 && fixture.Adds == 1 && fixture.Removals == 1
            && fixture.Exports == 1 && fixture.Columns.SequenceEqual(fixture.Original)
            && result.Data.Headers.SequenceEqual(fixture.Original.Select(fixture.Label).Prepend("Titre")),
            "The new browser session must finish recovery before loading tickets and must never replay the interrupted add.");
        fixture.AssertClean();
    }

    private static async Task CheckBackupFailure(IBrowser browser)
    {
        using var storage = new ColumnTestStorageScope();
        await using var fixture = await RestorationFixture.CreateAsync(browser, storage.Root, modern: false);
        File.WriteAllText(Path.Combine(storage.Root, "Recovery"), "fixture: not a directory");
        AutomaticRunResult? result = null; Exception? failure = null;
        try { result = await new AutomaticExtractionService(fixture.Service).RunAsync(fixture.Settings, null, CancellationToken.None); }
        catch (Exception error) when (error is InvalidOperationException or IOException) { failure = error; }
        Require(fixture.AddAttempts == 0 && fixture.Activations == 0 && fixture.Removals == 0 && fixture.Disables == 0
            && fixture.Columns.SequenceEqual(fixture.Original) && fixture.JournalBeforeCommands == 0,
            "A failed durable snapshot must stop every native preference mutation.");
        Require(failure is not null || result?.Data.Provenance.ColumnPreparation is { State: "backup-failed", AddedCount: 0 },
            "A backup failure must remain explicit, even if safe collection of the unchanged current columns is allowed.");
        Require(File.ReadAllText(Path.Combine(storage.Root, "Recovery")) == "fixture: not a directory", "The unrelated blocking file must remain intact.");
    }

    private static async Task CheckDrift(IBrowser browser, string drift)
    {
        using var storage = new ColumnTestStorageScope();
        await using var fixture = await RestorationFixture.CreateAsync(browser, storage.Root, modern: true);
        await fixture.PrepareAsync(null, CancellationToken.None);
        Require(fixture.Adds == 2 && fixture.Service.HasPendingTemporaryColumns(fixture.Settings), "The fixture must create a real temporary view before changing its recovery context.");
        await fixture.RestartAsync();
        if (drift == "user") fixture.UsersId = "18";
        else if (drift == "profile") fixture.ProfileId = "9";
        else fixture.RedirectPreferencesOutsideOrigin = true;
        var blocked = await fixture.Service.RecoverTemporaryColumnsAsync(fixture.Page, fixture.Settings, null, CancellationToken.None);
        Require(blocked.Pending && fixture.Removals == 0 && fixture.Disables == 0 && fixture.AddAttempts == 2
            && fixture.ForeignPosts == 0 && fixture.GlobalPosts == 0 && fixture.Service.HasPendingTemporaryColumns(fixture.Settings),
            "A different user, observable profile or origin must leave the durable journal pending without modifying any account or global view.");
        fixture.UsersId = "17"; fixture.ProfileId = "4"; fixture.RedirectPreferencesOutsideOrigin = false;
        var recovered = await fixture.Service.RecoverTemporaryColumnsAsync(fixture.Page, fixture.Settings, null, CancellationToken.None);
        Require(!recovered.Pending && fixture.Removals == 2 && fixture.AddAttempts == 2 && fixture.Columns.SequenceEqual(fixture.Original),
            "Returning to the saved identity and context must permit one verified cleanup without preparing again.");
        fixture.AssertClean();
    }

    private static async Task CheckConcurrentChanges(IBrowser browser)
    {
        foreach (var modern in new[] { false, true })
        {
            using var storage = new ColumnTestStorageScope();
            await using var fixture = await RestorationFixture.CreateAsync(browser, storage.Root, modern);
            await fixture.PrepareAsync(null, CancellationToken.None);
            var prepared = fixture.Columns.ToList();
            fixture.Columns.Remove("12"); fixture.Columns.Insert(0, "901"); fixture.Columns.Insert(2, "12");
            var concurrent = fixture.Columns.ToList();
            var cleanup = await fixture.Service.RestoreTemporaryColumnsAsync(fixture.Settings, null, CancellationToken.None);
            if (modern)
            {
                Require(cleanup.Pending && cleanup.State == "concurrent-change" && fixture.Removals == 0
                    && fixture.Columns.SequenceEqual(concurrent) && fixture.Service.HasPendingTemporaryColumns(fixture.Settings),
                    "Modern update_order rewrites the whole list: a concurrent insertion must suspend that command and preserve the user's exact order.");
                fixture.Columns.Clear(); fixture.Columns.AddRange(prepared);
                cleanup = await fixture.Service.RestoreTemporaryColumnsAsync(fixture.Settings, null, CancellationToken.None);
                Require(!cleanup.Pending && fixture.Columns.SequenceEqual(fixture.Original), "Once the conflict is resolved, the original owned additions can be removed exactly once.");
            }
            else
            {
                var expected = concurrent.Where(value => value is not ("3" or "8")).ToArray();
                Require(!cleanup.Pending && cleanup.State == "restored-preserving-changes" && fixture.Columns.SequenceEqual(expected),
                    "Legacy purge must remove only its exact owned preference IDs while preserving the concurrent insertion and order.");
            }
            Require(fixture.AddAttempts == 2 && fixture.Removals == 2 && fixture.Disables == 0 && fixture.Exports == 0,
                "Conflict handling must not replay additions, clear a personal view, or start an export.");
            fixture.AssertClean();
        }
    }

    private static async Task CheckNoReplay(IBrowser browser)
    {
        using var storage = new ColumnTestStorageScope();
        await using var fixture = await RestorationFixture.CreateAsync(browser, storage.Root, modern: false);
        await new AutomaticExtractionService(fixture.Service).RunAsync(fixture.Settings, null, CancellationToken.None);
        fixture.AssertClean(); var reads = fixture.PreferenceGets; var writes = fixture.JournalBeforeCommands;
        await fixture.RestartAsync(); fixture.Settings.ColumnPreparationMode = "current";
        var result = await new AutomaticExtractionService(fixture.Service).RunAsync(fixture.Settings, null, CancellationToken.None);
        Require(fixture.AddAttempts == 2 && fixture.Removals == 2 && fixture.PreferenceGets == reads
            && fixture.JournalBeforeCommands == writes && fixture.Exports == 2 && result.Data.Headers.Count == fixture.Original.Count + 1,
            "A new session after durable completion must collect the current view without re-opening preferences or replaying cleanup.");
        fixture.AssertClean();
    }

    private sealed class RestorationFixture(IBrowser browser, string root, bool modern, bool active, bool complete) : IAsyncDisposable
    {
        private const string Origin = "http://glpi-column-restoration.test";
        private const string Target = Origin + "/glpi/front/ticket.php?criteria%5B0%5D%5Bfield%5D=12&criteria%5B0%5D%5Bvalue%5D=1";
        private static readonly string[] All = ["12", "900", "2", "3", "8", "901"];
        private readonly Dictionary<string, string> _preferenceIds = new(StringComparer.Ordinal)
        { ["12"] = "711", ["900"] = "712", ["2"] = "713", ["3"] = "714", ["8"] = "715", ["901"] = "990" };
        private BrowserService? _service;
        private readonly bool _initiallyActive = active;
        private readonly bool _initiallyComplete = complete;
        private string? _activationCopyRemoved;
        private int _nextPreferenceId = 800;
        private int _render;

        public BrowserService Service => _service!;
        public IPage Page { get; private set; } = null!;
        public List<string> Original { get; } = complete ? ["12", "900", "2", "3", "8"] : ["12", "900", "2"];
        public List<string> Columns { get; } = complete ? ["12", "900", "2", "3", "8"] : ["12", "900", "2"];
        public List<string> Events { get; } = new();
        public bool PersonalActive { get; private set; } = active;
        public string UsersId { get; set; } = "17";
        public string ProfileId { get; set; } = "4";
        public bool RejectSecondAddition { get; set; }
        public bool LoseFirstAdditionResponse { get; set; }
        public bool ResponseLost { get; private set; }
        public bool RedirectPreferencesOutsideOrigin { get; set; }
        public bool BlockRecoveryUntilReconnect { get; set; }
        public bool RequireRestorationBeforeList { get; set; }
        public bool DisableHiddenDuringFullView { get; private set; }
        public int AddAttempts { get; private set; }
        public int Adds { get; private set; }
        public int Activations { get; private set; }
        public int Removals { get; private set; }
        public int Disables { get; private set; }
        public int Exports { get; private set; }
        public int PreferenceGets { get; private set; }
        public int BlockedRecoveryReads { get; private set; }
        public int ForeignPosts { get; private set; }
        public int GlobalPosts { get; private set; }
        public int JournalBeforeCommands { get; private set; }
        public string ExportedCsv { get; private set; } = "";
        public string[] ExportHeaders { get; private set; } = [];
        public AppSettings Settings { get; } = new()
        {
            PortalUrl = Target, SelectorOrigin = Origin, ColumnPreparationMode = "supervision", AutoMode = "native",
            ExportFolder = Path.Combine(root, "Exports"), AutoSaveXlsx = false, ResumePagination = false,
            Cities = new() { Enabled = false }, TimeoutSeconds = 5, ListTimeoutSeconds = 5, DownloadTimeoutSeconds = 5,
            ExpectedTotal = 1, MaxRows = 20, MaxPages = 5
        };

        public static async Task<RestorationFixture> CreateAsync(IBrowser browser, string root, bool modern, bool active = true, bool complete = false)
        {
            var fixture = new RestorationFixture(browser, root, modern, active, complete);
            await fixture.RestartAsync(); await fixture.Page.GotoAsync(Target); return fixture;
        }

        public async Task RestartAsync()
        {
            if (_service is not null) await _service.DisposeAsync();
            var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
            await context.RouteAsync("**/*", HandleAsync);
            _service = new BrowserService(); _service.AttachForTesting(context, Target);
            Page = await context.NewPageAsync();
        }

        public async Task<ColumnPreparationResult> PrepareAsync(IProgress<ExtractionProgress>? progress, CancellationToken ct)
        {
            var original = await Service.ChooseAutoTableAsync(Page, Settings, ct);
            return await Service.PrepareColumnsAsync(original, Settings, progress, ct);
        }

        public void AssertClean()
        {
            Require(!Service.HasPendingTemporaryColumns(Settings) && !new TemporaryColumnJournalStore().Exists(Settings),
                "A verified restoration must durably finish and remove its journal.");
            Require(ForeignPosts == 0 && GlobalPosts == 0 && JournalBeforeCommands == Activations + AddAttempts + Removals + Disables,
                "Each native command must have one preceding durable intent and must target only this personal Ticket view.");
        }

        public string Label(string value) => value switch
        { "2" => "ID", "12" => "Statut", "3" => "Priorité", "8" => "Groupe de techniciens", "900" => "Champ personnel", "901" => "Ajout utilisateur concurrent", _ => throw new InvalidOperationException("Unknown fixture column.") };

        private string Value(string column) => column switch
        { "2" => "7", "12" => "Nouveau", "3" => "Haute", "8" => "Support", _ => "Valeur synthétique" };
        private static string Hidden(string name, string value) => "<input type='hidden' name='" + name + "' value='" + value + "'>";
        private string Identity() => Hidden("itemtype", "Ticket") + Hidden("users_id", UsersId);
        private string Form(string content) => "<form method='post' action='/glpi/front/displaypreference.form.php'>" + Identity()
            + Hidden("_glpi_csrf_token", "private-csrf-token") + content + "</form>";
        private static string Html(string content) => "<!doctype html><html lang='fr'><meta charset='utf-8'><title>GLPI — Tickets synthétiques</title>"
            + "<style>body{font:14px sans-serif}iframe{width:900px;height:650px}.modal,.ui-dialog{position:fixed;inset:8px;background:white;z-index:10;padding:12px}button,input,select{min-height:28px}td{padding:4px}[hidden]{display:none!important}</style>"
            + "<body><a href='/glpi/logout.php'>Déconnexion</a>" + content + "</body></html>";
        private string[] Available() => All.Where(value => value != "901" && !Columns.Contains(value)).ToArray();
        private string Options() => string.Concat(Available().Select(value => "<option value='" + value + "'>" + Label(value) + "</option>"));

        private string Preferences()
        {
            string content;
            if (!PersonalActive) content = modern ? Identity() + "<button type='button' name='activate'>Créer la vue personnelle</button>"
                : Form("<input type='submit' name='activate' value='Créer la vue personnelle'>");
            else if (modern)
            {
                var available = Available();
                if (available.Length == 0) DisableHiddenDuringFullView = true;
                content = Identity() + (available.Length > 0 ? "<button type='button' name='disable'>Désactiver</button><select name='num'>" + Options()
                    + "</select><button type='button' name='add_opt'>Ajouter</button>" : "")
                    + "<ul class='list-group'><li data-opt-id='1' data-fixed='true'><span>Titre</span></li>"
                    + string.Concat(Columns.Select(value => "<li data-opt-id='" + value + "'><span class='flex-fill'>" + Label(value)
                        + "</span><button type='button' name='remove_opt'>Supprimer</button></li>")) + "</ul>";
            }
            else content = "<table><tr><th>" + Form("<input type='submit' name='disable' value='Désactiver'>") + "</th></tr><tr><td colspan='4'>"
                + Form("<select name='num'>" + Options() + "</select><input type='submit' name='add' value='Ajouter'>")
                + "</td></tr><tr><td>Titre</td><td colspan='3'></td></tr>"
                + string.Concat(Columns.Select(value => "<tr><td>" + Label(value) + "</td><td>"
                    + Form(Hidden("id", _preferenceIds[value]) + "<button name='purge' type='submit'>Supprimer</button>") + "</td><td></td><td></td></tr>")) + "</table>";
            var context = "<label>Profil<select name='profiles_id'><option value='" + ProfileId + "' selected>Assistance</option></select></label>"
                + "<select name='active_entity'><option value='2' selected>Entité synthétique</option></select>";
            return Html(context + "<div id='tabsbody'" + (modern ? " class='display_preference_config'" : "") + ">" + content + "</div>" + (modern ? ModernHandlers : ""));
        }

        private const string ModernHandlers = """
            <script>
            async function send(body){const reply=await fetch('/glpi/ajax/displaypreference.php',{method:'POST',body});await reply.text();}
            const identity=()=>({itemtype:document.querySelector('input[name=itemtype]').value,users_id:document.querySelector('input[name=users_id]').value});
            async function saveOrder(){const body=new URLSearchParams({action:'update_order',...identity()});
              document.querySelectorAll('li[data-opt-id]:not([data-fixed])').forEach(li=>body.append('opts[]',li.dataset.optId));await send(body);}
            document.querySelector('[name=activate]')?.addEventListener('click',async()=>{await send(new URLSearchParams({activate:'1',...identity()}));location.reload();});
            document.querySelector('[name=disable]')?.addEventListener('click',async()=>{await send(new URLSearchParams({disable:'1',...identity()}));location.reload();});
            document.querySelector('[name=add_opt]')?.addEventListener('click',async()=>{
              const select=document.querySelector('select[name=num]'), option=select.selectedOptions[0];
              const li=document.createElement('li');li.dataset.optId=option.value;
              const span=document.createElement('span');span.className='flex-fill';span.textContent=option.textContent;li.append(span);
              const remove=document.createElement('button');remove.type='button';remove.name='remove_opt';remove.textContent='Supprimer';li.append(remove);
              document.querySelector('.list-group').append(li);option.remove();
              if(!select.options.length){document.querySelector('[name=disable]')?.remove();document.querySelector('[name=add_opt]')?.remove();select.remove();}
              await saveOrder();
            });
            document.addEventListener('click',async event=>{const remove=event.target.closest('button[name=remove_opt]');if(!remove)return;remove.closest('li').remove();await saveOrder();});
            </script>
            """;

        private string Card()
        {
            var number = ++_render;
            return "<div class='" + (modern ? "search-card" : "legacy-search") + "'>"
                + (modern ? "<button type='button' id='column-opener' class='show_displaypreference_modal'>Colonnes</button>" : "")
                + "<form id='massformTicket" + number + "' data-search-itemtype='Ticket' data-count='1' data-start='0' data-limit='20'>"
                + "<table id='search_" + number + "' class='" + (modern ? "search-results" : "tab_cadre_fixehov") + "'><thead><tr><th>Titre</th>"
                + string.Concat(Columns.Select(value => "<th>" + Label(value) + "</th>")) + "</tr></thead><tbody><tr><td>Private ticket title</td>"
                + string.Concat(Columns.Select(value => "<td>" + Value(value) + "</td>")) + "</tr></tbody></table></form>"
                + "<div class='pager'><table class='tab_cadre_pager'><tr><td><form method='GET' action='/glpi/front/report.dynamic.php'>"
                + Hidden("item_type", "Ticket") + Hidden("start", "0") + Hidden("criteria[0][field]", "12") + Hidden("criteria[0][value]", "1")
                + "<select name='display_type'><option value='4'>Page courante en CSV</option><option value='-4'>Toutes les pages en CSV</option></select>"
                + "<button type='submit' name='export' title='Exporter'>Exporter</button></form></td><td>De 1 à 1 sur 1</td></tr></table></div></div>";
        }

        private string ListPage()
        {
            var body = "<form name='searchformTicket'>" + Hidden("criteria[0][field]", "12") + Hidden("criteria[0][value]", "1") + "</form>" + Card();
            if (!modern) return Html(body + "<span class='fa-wrench pointer' onclick=\"$('#search_config_top').dialog('open');\">Colonnes</span>"
                + "<div id='dialog' class='ui-dialog' role='dialog' hidden><button class='ui-dialog-titlebar-close'>Fermer</button><div id='search_config_top'><iframe id='Iframesearch_config_top'></iframe></div></div>"
                + "<script>window.$=()=>({dialog:()=>{document.querySelector('#dialog').hidden=false;document.querySelector('iframe').src='/glpi/front/displaypreference.form.php?itemtype=Ticket';}});document.querySelector('.ui-dialog-titlebar-close').onclick=()=>location.reload();</script>");
            return Html(body + """
                <script>
                function bindColumns(){document.querySelector('#column-opener').onclick=()=>{
                  let modal=document.querySelector('#displaypreference_modal_sticky');
                  if(!modal){modal=document.createElement('div');modal.className='modal';modal.id='displaypreference_modal_sticky';modal.setAttribute('role','dialog');
                    modal.innerHTML='<button type="button" data-bs-dismiss="modal">Fermer</button><iframe src="/glpi/front/displaypreference.form.php?itemtype=Ticket"></iframe>';document.body.append(modal);
                    modal.querySelector('button').onclick=async()=>{modal.hidden=true;const reply=await fetch('/glpi/ajax/search.php',{method:'POST',body:new URLSearchParams(location.search)});
                      const fresh=new DOMParser().parseFromString(await reply.text(),'text/html');document.querySelector('.search-card').replaceWith(fresh.querySelector('.search-card'));bindColumns();};}
                  modal.hidden=false;
                };}bindColumns();
                </script>
                """);
        }

        private void VerifyDurableIntent(string kind, string option = "", string identity = "")
        {
            var store = new TemporaryColumnJournalStore(); var record = store.Load(Settings);
            Require(record is not null && record.UsersId == "17" && record.PendingCommand is { } command && command.Kind == kind
                && (kind != "add" || command.OptionValue == option) && (kind != "remove" || command.TargetIdentity == identity),
                "The exact native command must already be committed to disk before the fixture receives its POST.");
            var serialized = File.ReadAllText(store.PathFor(Settings));
            Require(!serialized.Contains("private-csrf-token", StringComparison.Ordinal) && !serialized.Contains("Private ticket title", StringComparison.Ordinal)
                && !serialized.Contains("criteria[", StringComparison.Ordinal), "The rollback journal must not persist CSRF tokens, ticket cells or raw criteria.");
            JournalBeforeCommands++;
        }

        private async Task HandleAsync(IRoute route)
        {
            var uri = new Uri(route.Request.Url);
            if (uri.GetLeftPart(UriPartial.Authority) != Origin)
            {
                if (route.Request.Method == "POST") ForeignPosts++;
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Preferences() }); return;
            }
            if (uri.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal))
            {
                if (RequireRestorationBeforeList) Require(Columns.SequenceEqual(Original) && !Service.HasPendingTemporaryColumns(Settings),
                    "Startup must finish durable preference recovery before requesting the potentially heavy ticket list.");
                Events.Add("list"); await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = ListPage() }); return;
            }
            if (uri.AbsolutePath.EndsWith("/ajax/search.php", StringComparison.Ordinal))
            { await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Html(Card()) }); return; }
            if (uri.AbsolutePath.EndsWith("/front/report.dynamic.php", StringComparison.Ordinal))
            {
                var query = Pairs(uri.Query);
                Require(route.Request.Method == "GET" && query.GetValueOrDefault("display_type") == "-4" && query.GetValueOrDefault("criteria[0][value]") == "1",
                    "Every native export must retain the original all-pages Ticket scope.");
                Exports++; Events.Add("export"); ExportHeaders = Columns.Select(Label).Prepend("Titre").ToArray();
                ExportedCsv = "\uFEFF" + string.Join(';', ExportHeaders) + "\r\nPrivate ticket title;" + string.Join(';', Columns.Select(Value)) + "\r\n";
                await route.FulfillAsync(new() { ContentType = "text/csv; charset=utf-8", Headers = new Dictionary<string, string>
                    { ["Content-Disposition"] = "attachment; filename=\"glpi.csv\"" }, Body = ExportedCsv }); return;
            }
            if (!uri.AbsolutePath.EndsWith("/front/displaypreference.form.php", StringComparison.Ordinal)
                && !uri.AbsolutePath.EndsWith("/ajax/displaypreference.php", StringComparison.Ordinal))
            { await route.AbortAsync(); return; }
            if (route.Request.Method != "POST")
            {
                PreferenceGets++; Events.Add("preferences");
                if (BlockRecoveryUntilReconnect)
                {
                    BlockedRecoveryReads++;
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8",
                        Body = Html("<div id='tabsbody'><p>Les préférences sont momentanément indisponibles.</p></div>") }); return;
                }
                if (RedirectPreferencesOutsideOrigin)
                {
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8",
                        Body = "<!doctype html><script>location.replace('http://other-glpi.test/glpi/front/displaypreference.form.php?itemtype=Ticket')</script>" }); return;
                }
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Preferences() }); return;
            }
            var values = Pairs(route.Request.PostData); var fields = Fields(route.Request.PostData);
            if (values.GetValueOrDefault("users_id") == "0") GlobalPosts++;
            Require(values.GetValueOrDefault("itemtype") == "Ticket" && values.GetValueOrDefault("users_id") == "17" && UsersId == "17",
                "No command may touch a global view, another user or another item type.");
            if (values.ContainsKey("activate"))
            {
                VerifyDurableIntent("activate"); Require(!PersonalActive, "Activation must happen once.");
                PersonalActive = true; Activations++; Events.Add("activate");
            }
            else if (values.ContainsKey("disable"))
            {
                VerifyDurableIntent("disable"); Require(!_initiallyActive && PersonalActive
                    && Columns.SequenceEqual(_activationCopyRemoved is null ? Original : Original.Where(value => value != _activationCopyRemoved)),
                    "Only this collection's fully restored temporary activation may be disabled.");
                PersonalActive = false; Columns.Clear(); Columns.AddRange(Original); Disables++; Events.Add("disable");
            }
            else
            {
                string? added = null; string? removed = null; List<string>? submitted = null;
                if (modern)
                {
                    Require(values.GetValueOrDefault("action") == "update_order", "Only the real modern display order handler may post.");
                    submitted = fields.Where(pair => pair.Key == "opts[]").Select(pair => pair.Value).ToList();
                    if (submitted.Count == Columns.Count + 1 && submitted.Take(Columns.Count).SequenceEqual(Columns)) added = submitted[^1];
                    else if (submitted.Count == Columns.Count - 1)
                    {
                        removed = Columns.Except(submitted).SingleOrDefault();
                        Require(removed is not null && submitted.SequenceEqual(Columns.Where(value => value != removed)),
                            "Native removal must preserve the exact order of every remaining column.");
                    }
                    else throw new InvalidOperationException("An unexpected modern whole-list write must be refused by the fixture.");
                }
                else if (values.ContainsKey("purge")) removed = _preferenceIds.Single(pair => pair.Value == values.GetValueOrDefault("id")).Key;
                else { Require(values.GetValueOrDefault("add") == "Ajouter", "Only an offered native add may post."); added = values.GetValueOrDefault("num"); }
                if (added is not null)
                {
                    VerifyDurableIntent("add", added); AddAttempts++;
                    Require(added is "3" or "8" && !Columns.Contains(added), "Only missing useful columns may be added by this fixture.");
                    if (RejectSecondAddition && AddAttempts == 2)
                    { Events.Add("add-rejected"); await route.FulfillAsync(new() { Status = 403, ContentType = "text/html", Body = "Accès refusé" }); return; }
                    Columns.Add(added); _preferenceIds[added] = (_nextPreferenceId++).ToString(); Adds++; Events.Add("add:" + added);
                    if (LoseFirstAdditionResponse && Adds == 1)
                    { ResponseLost = true; Events.Add("persisted-without-response"); await route.AbortAsync("failed"); return; }
                }
                else
                {
                    if (!_initiallyActive && _initiallyComplete && Adds == 0 && Activations == 1 && Removals == 0 && Original.Contains(removed!))
                        _activationCopyRemoved = removed;
                    else Require(removed is "3" or "8", "Restoration must never delete an initial or concurrent personal column.");
                    var identity = modern ? "option:" + removed : "preference:" + _preferenceIds[removed!];
                    VerifyDurableIntent("remove", identity: identity);
                    Columns.Remove(removed!); Removals++; Events.Add("remove:" + removed);
                }
                if (submitted is not null) Require(submitted.SequenceEqual(Columns), "The persisted modern order must equal the exact native submission.");
            }
            await route.FulfillAsync(new() { ContentType = modern ? "application/json" : "text/html; charset=utf-8", Body = modern ? "{}" : Preferences() });
        }

        private static (string Key, string Value)[] Fields(string? value) => (value ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Select(parts => (WebUtility.UrlDecode(parts[0]), WebUtility.UrlDecode(parts.Length > 1 ? parts[1] : ""))).ToArray();
        private static Dictionary<string, string> Pairs(string? value) => Fields(value).GroupBy(pair => pair.Key)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
        public async ValueTask DisposeAsync() { if (_service is not null) await _service.DisposeAsync(); }
    }
}
