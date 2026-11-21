using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

internal static class RelaisExportRuntimeChecks
{
    private const string Installation = "http://session-glpi.test/glpi";
    private const string FutureInstallation = "https://future-relais.invalid/glpi";
    private const string ExactId = "9007199254740993";
    private const string PrivateBody = "RELAIS_PRIVATE_BODY_SENTINEL · Solution privée synthétique <b>à conserver exactement</b>.";

    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string Name, Func<Task> Run)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add(("Relais export: " + name,
            async () => { await using var fixture = new SessionFixture(); await run(fixture); }));

        Add("the real operation exports the whole viewed installation and a count-only receipt regardless of row filters", async f =>
        {
            var store = Store();
            await SeedContent(store, "42"); await SeedContent(store, ExactId);
            await store.ObserveSeedAsync(Seed("43")); await SeedContent(store, "81", FutureInstallation);
            var selected = await f.Call("library.search", new { text = "42", installation = Installation, limit = 1 });
            Require(selected.GetProperty("rows").GetArrayLength() == 1 && f.App.Result!.Rows.Count == 1,
                "The fixture must expose fewer rows than the admissible installation.");
            var sourceBefore = TicketLibraryRules.Serialize((await store.GetAsync(Seed(ExactId).Ticket))!);
            f.Host.SavePath = Path.Combine(f.Root, "actual-runtime-export.relais");
            var response = await f.Call("library.relais.export", new { installation = FutureInstallation, text = "absent", offset = 999, limit = 1 });
            var report = response.GetProperty("result"); var actual = ReadExport(f.Host.SavePath);
            Require(response.GetProperty("kind").GetString() == "relais-export" && response.GetProperty("exported").GetBoolean()
                && !response.GetProperty("cancelled").GetBoolean() && report.GetProperty("installation").GetString() == Installation
                && report.GetProperty("ticketCount").GetInt64() == 2 && report.GetProperty("sourceTicketCount").GetInt64() == 3
                && report.GetProperty("excludedTicketCount").GetInt64() == 1 && report.GetProperty("textCount").GetInt64() == actual.Texts
                && actual.Installation == Installation && actual.Tickets == 2 && actual.Ids.SetEquals(["42", ExactId])
                && actual.HasPrivate && actual.HasUnknown,
                "The runtime must export exact native identities and all admissible contents of its own installation.");
            var save = f.Host.SaveRequests.Single();
            Require(save.Filter == "Fichier Relais|*.relais" && save.SuggestedName.StartsWith("GLPI-Pilot-Relais-", StringComparison.Ordinal)
                && save.SuggestedName.EndsWith(".relais", StringComparison.Ordinal)
                && !response.GetRawText().Contains("RELAIS_PRIVATE_BODY_SENTINEL", StringComparison.Ordinal)
                && !response.TryGetProperty("tickets", out _) && !response.TryGetProperty("sync", out _)
                && sourceBefore == TicketLibraryRules.Serialize((await store.GetAsync(Seed(ExactId).Ticket))!)
                && !File.Exists(f.Host.SavePath + "-wal") && !File.Exists(f.Host.SavePath + "-shm"),
                "Save As must propose the actual exchange format and the receipt must not expose source bodies or claim a synchronization.");
            Require((await f.Call("operations.list")).GetProperty("entries").EnumerateArray()
                .Any(entry => entry.GetProperty("code").GetString() == "RELAIS_EXPORTED"), "The completed export must have its own durable outcome.");
        });

        Add("zero admissible contents returns quality counts before opening Save As", async f =>
        {
            await Store().ObserveSeedAsync(Seed("42"));
            f.Host.SavePath = Path.Combine(f.Root, "must-not-exist.relais");
            var response = await f.Call("library.relais.export"); var preview = response.GetProperty("preview");
            Require(!response.GetProperty("exported").GetBoolean() && response.GetProperty("kind").GetString() == "relais-export"
                && preview.GetProperty("sourceTicketCount").GetInt64() == 1 && preview.GetProperty("eligibleTicketCount").GetInt64() == 0
                && preview.GetProperty("missingContentCount").GetInt64() == 1 && preview.GetProperty("unconfirmedCount").GetInt64() == 1
                && f.Host.SaveRequests.Count == 0 && !File.Exists(f.Host.SavePath)
                && response.GetProperty("message").GetString()!.Contains("Aucun fichier créé", StringComparison.Ordinal),
                "A nonempty index with no admissible body must not produce an apparently importable empty export.");
        });

        Add("cancelling Save As returns cancellation without a file or success receipt", async f =>
        {
            await SeedContent(Store(), "42"); f.Host.SavePath = null;
            var response = await f.Call("library.relais.export");
            Require(response.GetProperty("cancelled").GetBoolean() && !response.TryGetProperty("result", out _)
                && f.Host.SaveRequests.Count == 1 && !Directory.EnumerateFiles(f.Root, "*.relais", SearchOption.AllDirectories).Any()
                && !(await f.Call("operations.list")).GetProperty("entries").EnumerateArray()
                    .Any(entry => entry.GetProperty("code").GetString() == "RELAIS_EXPORTED"),
                "Cancelling the native dialog must not leave an export or publish a completed outcome.");
        });

        Add("cancellation received during Save As prevents publication", async f =>
        {
            await SeedContent(Store(), "42"); f.Host.SavePath = Path.Combine(f.Root, "cancelled.relais");
            using var cancellation = new CancellationTokenSource();
            f.Host.BeforeSave = () => { cancellation.Cancel(); return Task.CompletedTask; };
            try { await f.Call("library.relais.export", ct: cancellation.Token); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
            Require(f.Host.SaveRequests.Count == 1 && !File.Exists(f.Host.SavePath) && !f.App.IsBusy,
                "A cancelled native save must release the operation gate without publishing a file.");
        });

        Add("a restored GLPI observation retains its installation after another profile is selected for future collection", async f =>
        {
            var store = Store(); await SeedContent(store, "42"); await SeedContent(store, ExactId); await SeedContent(store, "81", FutureInstallation);
            await f.App.RememberSourceCacheFixtureAsync(SessionFixture.Data()); await f.Call("cache.restore");
            var observed = f.App.Result;
            var next = f.App.Settings.Sources.Single(source => source.Id == SessionFixture.ProfileB).Copy();
            next.Url = FutureInstallation + "/front/ticket.php";
            await f.Call("sources.save", new { source = next, revision = f.App.Revision }); await f.SelectB();
            Require(f.App.IsCachedSource && ReferenceEquals(f.App.Result, observed) && f.App.Settings.PortalUrl == next.Url,
                "The fixture must retain an offline observation beside a different future portal.");
            var context = (await f.Call("library.get")).GetProperty("relaisExport");
            f.Host.SavePath = Path.Combine(f.Root, "offline-original-installation.relais");
            await f.Call("library.relais.export"); var actual = ReadExport(f.Host.SavePath);
            Require(context.GetProperty("available").GetBoolean() && context.GetProperty("installation").GetString() == Installation
                && actual.Installation == Installation && actual.Ids.SetEquals(["42", ExactId])
                && f.App.Settings.PortalUrl == next.Url && ReferenceEquals(f.App.Result, observed),
                "The export must use the viewed observation's source without borrowing the next profile or replacing the cache.");
        });

        Add("a source change while Save As is open invalidates the pending export", async f =>
        {
            await SeedContent(Store(), "42"); f.Host.SavePath = Path.Combine(f.Root, "stale-source.relais");
            f.Host.BeforeSave = () =>
            {
                var changed = SessionFixture.Data(); changed.SourceUrl = FutureInstallation + "/front/ticket.php";
                changed.Provenance.RequestedUrl = changed.SourceUrl; f.App.LoadSessionFixture(changed);
                return Task.CompletedTask;
            };
            await Refuse(() => f.Call("library.relais.export"), "changé");
            Require(f.Host.SaveRequests.Count == 1 && !File.Exists(f.Host.SavePath),
                "An old native dialog cannot silently export a newly selected installation.");
        });

        foreach (var simulation in new[] { false, true })
            Add((simulation ? "a simulation" : "an imported file") + " cannot export the real GLPI library", async f =>
            {
                await SeedContent(Store(), "42");
                if (simulation) await f.Call("app.demo");
                else { await OfflineImportChecks.WriteCsv(f); await f.Call("records.import"); }
                f.Host.SavePath = Path.Combine(f.Root, "wrong-context.relais");
                var context = (await f.Call("library.get")).GetProperty("relaisExport");
                await Refuse(() => f.Call("library.relais.export"), "bibliothèque GLPI");
                Require(!context.GetProperty("available").GetBoolean() && f.Host.SaveRequests.Count == 0 && !File.Exists(f.Host.SavePath),
                    "The operation must enforce the unavailable UI context before choosing any destination.");
            });

        return tests;
    }

    private static TicketLibraryStore Store() => new(new LibraryPreferencesStore().Load().DatabasePath);
    private static TicketLibrarySeed Seed(string id, string installation = Installation) => new()
    {
        Ticket = new(id, installation + "/front/ticket.form.php?id=" + id), Title = "Fiche synthétique " + id, Status = "Résolu", StatusCode = 5,
        LastModifiedRaw = "2026-11-01 08:00:00", LastModifiedAt = At(), LastModifiedReliable = true, LastModifiedPrecision = "seconds",
        ObservedAt = At().AddMinutes(5), IndexHeaders = ["ID", "Titre"], IndexValues = [id, "Fiche synthétique " + id]
    };
    private static DateTimeOffset At() => new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static Task<TicketLibraryWriteResult> SeedContent(TicketLibraryStore store, string id, string installation = Installation)
    {
        var seed = Seed(id, installation);
        return store.SaveDetailAsync(seed, new()
        {
            Ticket = seed.Ticket, Title = seed.Title, ReadAt = At().AddMinutes(5),
            Tabs = [new() { Key = "timeline", Name = "Chronologie", Loaded = true }],
            Sections = [new() { Name = "Chronologie", Kind = "timeline", TabKey = "timeline", Data = new()
            {
                Headers = ["Type", "Date", "Auteur", "Contenu", "Informations"],
                Rows = [["Solution", "2026-11-01 08:00:00", "Auteur fictif", PrivateBody, "Privé"]],
                Complete = true, SourceUrl = seed.Ticket.Url, ExtractedAt = At().AddMinutes(5)
            } }]
        }, new() { Complete = true, TabsDiscovered = 1, TabsLoaded = 1 }, TicketLibraryAcquisition.ReaderSignature);
    }

    private static (string Installation, long Tickets, long Texts, HashSet<string> Ids, bool HasPrivate, bool HasUnknown) ReadExport(string path)
    {
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        database.Open();
        using var manifest = database.CreateCommand(); manifest.CommandText = "SELECT installation,ticket_count,text_count FROM relais_manifest";
        string installation; long tickets, texts;
        using (var reader = manifest.ExecuteReader()) { Require(reader.Read(), "Missing export manifest."); installation = reader.GetString(0); tickets = reader.GetInt64(1); texts = reader.GetInt64(2); }
        using var rows = database.CreateCommand(); rows.CommandText = "SELECT ticket_id,text_visibility,body FROM ticket_library_knowledge_v1_texts";
        using var records = rows.ExecuteReader(); var ids = new HashSet<string>(StringComparer.Ordinal); var hasPrivate = false; var hasUnknown = false;
        while (records.Read())
        {
            ids.Add(records.GetString(0)); hasUnknown |= records.GetString(1) == "unknown";
            hasPrivate |= records.GetString(1) == "private" && records.GetString(2).Contains(PrivateBody, StringComparison.Ordinal);
        }
        return (installation, tickets, texts, ids, hasPrivate, hasUnknown);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Refuse(Func<Task> operation, string text)
    {
        try { await operation(); }
        catch (InvalidOperationException error) when (error.Message.Contains(text, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException("The invalid export was not refused with the expected explanation: " + text);
    }
}
