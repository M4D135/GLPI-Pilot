using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class LibraryRuntimeChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string Name, Func<Task> Run)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add((name, async () => { await using var fixture = new SessionFixture(); await run(fixture); }));
        Add("Library dates require an explicit source zone and never guess an ambiguous day/month or DST hour", f =>
        {
            var defaults = new LibraryPreferences(); Require(defaults.SourceTimeZone == ""); defaults.Validate();
            Require(LibraryPreferencesStore.ParseModification("2026-10-22 09:10", "") is null);
            Require(LibraryPreferencesStore.ParseModification("2026-10-22T09:10:22+02:00", "")?.UtcDateTime.Hour == 7);
            Require(LibraryPreferencesStore.ParseModification("22/10/2026 09:10", "UTC")?.Hour == 9);
            Require(LibraryPreferencesStore.ParseModification("05/06/2026 09:10", "UTC") is null);
            Require(LibraryPreferencesStore.ParseModification("05/06/2026 09:10", "UTC", "dmy")?.Month == 6);
            Require(LibraryPreferencesStore.ParseModification("05/06/2026 09:10", "UTC", "mdy")?.Month == 5);
            Require(LibraryPreferencesStore.ParseModification("05/06/2026 09:10", "UTC", "ymd") is null);
            Require(LibraryPreferencesStore.ParseModification("05/06/2026 09:10+02:00", "UTC") is null);
            Require(LibraryPreferencesStore.ParseModification("2026-10-25 02:30", "Europe/Paris") is null);
            return Task.CompletedTask;
        });
        Add("The first team operation initializes its real database and protects edits with local revisions", async f =>
        {
            var item = await f.Call("workspace.save", new { item = new { kind = "knowledge", title = "Solution synthétique", body = "Texte intégral", status = "published", author = "Untrusted injected author", payload = new { password = "not-accepted" } } });
            Require(item.GetProperty("author").GetString() == "Technicien" && item.GetProperty("payload").ValueKind == JsonValueKind.Null);
            var id = item.GetProperty("id").GetString(); var revision = item.GetProperty("revision").GetInt64();
            await Refuse(() => f.Call("workspace.remove", new { id, expectedRevision = revision + 1 }));
            var list = await f.Call("workspace.list"); Require(list.GetProperty("total").GetInt64() == 1);
            await f.Call("workspace.remove", new { id, expectedRevision = revision });
            Require((await f.Call("workspace.list")).GetProperty("total").GetInt64() == 0);
            var state = await f.Call("library.get"); Require(state.GetProperty("team").GetProperty("pending").GetInt64() == 1);
        });
        Add("Library relocation copies tickets and team documents consistently and retains the original database", async f =>
        {
            var settings = new LibraryPreferences(); var source = settings.DatabasePath;
            var store = new TicketLibraryStore(source); await store.ObserveSeedAsync(Seed("71"));
            await f.Call("workspace.save", new { item = new { kind = "note", title = "Note à préserver", body = "Corps de note", status = "draft" } });
            settings.LocalFolder = Path.Combine(f.Root, "moved-library");
            var saved = await f.Call("library.settings.save", new { settings }); Require(saved.GetProperty("copied").GetBoolean());
            Require(File.Exists(source) && File.Exists(settings.DatabasePath));
            Require((await new TicketLibraryStore(source).GetStateAsync()).TicketCount == 1);
            Require((await new TicketLibraryStore(settings.DatabasePath).GetStateAsync()).TicketCount == 1);
            Require((await f.Call("workspace.list")).GetProperty("items")[0].GetProperty("body").GetString() == "Corps de note");
            Require(new LibraryPreferencesStore().Load().LocalFolder == settings.LocalFolder);
        });
        Add("Choosing an existing cache never overwrites it and a network folder is refused before changing settings", async f =>
        {
            var previous = new LibraryPreferences(); await new TicketLibraryStore(previous.DatabasePath).ObserveSeedAsync(Seed("72"));
            await f.Call("library.get");
            var next = new LibraryPreferences { LocalFolder = Path.Combine(f.Root, "existing-library") };
            await new TicketLibraryStore(next.DatabasePath).ObserveSeedAsync(Seed("73"));
            var saved = await f.Call("library.settings.save", new { settings = next }); Require(!saved.GetProperty("copied").GetBoolean());
            var result = await f.Call("library.search"); Require(result.GetProperty("rows")[0].GetProperty("ticket").GetProperty("id").GetString() == "73");
            next.LocalFolder = @"\\server\share\cache"; await Refuse(() => f.Call("library.settings.save", new { settings = next }));
            Require(new LibraryPreferencesStore().Load().LocalFolder.EndsWith("existing-library", StringComparison.Ordinal));
            Require((await new TicketLibraryStore(previous.DatabasePath).GetStateAsync()).TicketCount == 1);
        });
        Add("A session database secret never enters the WebView, diagnostics, portable settings or local preference file", async f =>
        {
            const string secret = "synthetic-password-DO-NOT-ECHO-92371";
            var settings = new LibraryPreferences { Host = "central.synthetic.test", Username = "fixture", PostgresEnabled = true };
            var saved = await f.Call("library.settings.save", new { settings, password = secret, rememberPassword = false });
            Require(saved.GetProperty("settings").GetProperty("hasPassword").GetBoolean() && !saved.GetRawText().Contains(secret, StringComparison.Ordinal));
            Require(!(await f.Call("app.diagnostic")).GetRawText().Contains(secret, StringComparison.Ordinal));
            Require(!File.ReadAllText(Path.Combine(f.Root, "library.local.json")).Contains(secret, StringComparison.Ordinal));
            await f.Call("library.settings.save", new { settings, password = "" });
            Require((await f.Call("library.get")).GetProperty("settings").GetProperty("hasPassword").GetBoolean());
            settings.Host = "different.synthetic.test"; await f.Call("library.settings.save", new { settings, password = "" });
            Require(!(await f.Call("library.get")).GetProperty("settings").GetProperty("hasPassword").GetBoolean());
            await f.Call("library.settings.save", new { settings, password = secret });
            await f.Call("library.settings.save", new { settings, forgetPassword = true });
            Require(!(await f.Call("library.get")).GetProperty("settings").GetProperty("hasPassword").GetBoolean());
        });
        Add("Shared configuration previews apply business sections without changing the live source or local library credentials", async f =>
        {
            var item = await f.Call("workspace.publishConfiguration", new { title = "Configuration synthétique" });
            var payload = item.GetProperty("payload").GetRawText();
            Require(!payload.Contains(SessionFixture.SourceA, StringComparison.Ordinal) && !payload.Contains("ProtectedPassword", StringComparison.OrdinalIgnoreCase));
            var source = f.App.Settings.PortalUrl; var localFolder = new LibraryPreferences().LocalFolder;
            var preview = await f.Call("workspace.configuration.preview", new { id = item.GetProperty("id").GetString() });
            Require(preview.GetProperty("sections").GetArrayLength() == 4);
            await f.Call("configuration.apply", new { token = preview.GetProperty("token").GetString(), parts = 120, merge = true });
            Require(f.App.Settings.PortalUrl == source && (await f.Call("library.get")).GetProperty("settings").GetProperty("localFolder").GetString() == localFolder);
            await Refuse(() => f.Call("configuration.apply", new { token = preview.GetProperty("token").GetString(), parts = 120, merge = true }));
            var statistics = await f.Call("workspace.publishStatistics", new { title = "Situation synthétique" });
            Require(statistics.GetProperty("payload").GetProperty("rowCount").GetInt32() == 1 && statistics.GetProperty("body").GetString()!.Contains("Lignes", StringComparison.Ordinal));
        });
        Add("Library and team exports cannot replace their active SQLite database", async f =>
        {
            var settings = new LibraryPreferences(); await new TicketLibraryStore(settings.DatabasePath).ObserveSeedAsync(Seed("74"));
            f.Host.SavePath = settings.DatabasePath;
            await Refuse(() => f.Call("library.export")); await Refuse(() => f.Call("workspace.export"));
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            { f.Host.SavePath = settings.DatabasePath + suffix; await Refuse(() => f.Call("library.export")); await Refuse(() => f.Call("workspace.export")); }
            Require((await new TicketLibraryStore(settings.DatabasePath).GetStateAsync()).TicketCount == 1);
        });
        Add("Tickets from different GLPI installations cannot acquire the first portal context in an aggregated cache view", async f =>
        {
            var store = new TicketLibraryStore(new LibraryPreferences().DatabasePath); var one = Seed("75"); var other = Seed("75");
            other.Ticket = new("75", "https://other-library.synthetic.test/glpi/front/ticket.form.php?id=75");
            await store.ObserveSeedAsync(one); await store.ObserveSeedAsync(other);
            var cachedDetail = new TicketDetailResult
            {
                Ticket = other.Ticket, Title = other.Title,
                Sections = [new() { Name = "Description", Kind = "text", Data = new() { Headers = ["Contenu"], Rows = [["Contenu déjà conservé dans la bibliothèque."]], Complete = true } }]
            };
            await store.SaveDetailAsync(other, cachedDetail, new() { TabsDiscovered = 1, TabsLoaded = 1, Complete = true }, "fixture-reader");
            await Refuse(() => f.Call("library.loadRecords"));
            Require(f.App.Result?.SourceUrl == SessionFixture.SourceA);
            await f.Call("library.loadRecords", new { installation = ObjectIdentity.Installation(other.Ticket.Url) });
            Require(f.App.Result is { Rows.Count: 1 } && f.App.Result.SourceUrl == ObjectIdentity.Installation(other.Ticket.Url) + "/front/ticket.php"
                && f.App.Result.Provenance.SourceKind == "local");
            var opened = await f.Call("workplace.ticket.open", new { rowIndex = 0, revision = f.App.Revision });
            Require(opened.GetProperty("personal").GetProperty("url").GetString() == other.Ticket.Url
                && !opened.GetProperty("canRead").GetBoolean() && f.App.Settings.PortalUrl == SessionFixture.SourceA);
            Require(opened.GetProperty("loaded").GetBoolean() && opened.GetProperty("sections").GetArrayLength() == 1
                && !(await f.Call("workplace.get")).GetProperty("autoRead").GetBoolean());
        });
        return tests;
    }
    private static TicketLibrarySeed Seed(string id) => new()
    {
        Ticket = new(id, "https://library.synthetic.test/glpi/front/ticket.form.php?id=" + id), Title = "Incident synthétique " + id, Status = "Nouveau", StatusCode = 1,
        IndexHeaders = ["ID", "Titre", "Statut", "Priorité"], IndexValues = [id, "Incident synthétique " + id, "Nouveau", "Haute"]
    };
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Library runtime invariant failed."); }
    private static async Task Refuse(Func<Task> operation)
    {
        try { await operation(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidOperationException("An invalid operation was accepted.");
    }
}
