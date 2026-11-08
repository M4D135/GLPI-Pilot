using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public static class SharingBridgeChecks
{
    public static void Register(Action<string, Func<CalendarFixture, Task>> add)
    {
        add("Sharing settings migrate legacy PostgreSQL and keep explicit folder mode independent of credentials", async f =>
        {
            var path = Path.Combine(f.Root, "legacy-library.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 1, LocalFolder = Path.Combine(f.Root, "cache"),
                PostgresEnabled = true, Host = "legacy.test", Database = "glpi", Username = "reader" }));
            var store = new LibraryPreferencesStore(path); var legacy = store.Load();
            Check(legacy.SyncMode == "postgres" && legacy.SharingEnabled && legacy.PostgresEnabled);
            var folder = Settings(f, "folder", Path.Combine(f.Root, "share")); folder.PostgresEnabled = true; folder.Validate();
            Check(folder.SyncMode == "folder" && folder.SharingEnabled && !folder.PostgresEnabled);
            store.Save(folder); var reload = store.Load(); Check(reload.SyncMode == "folder" && reload.SharedFolder == folder.SharedFolder);
            folder.LocalFolder = Path.Combine(folder.SharedFolder, "cache");
            await Refuse(() => { folder.Validate(); return Task.CompletedTask; }, "cache SQLite");
            Check(new LibraryPreferences().SyncMode == "local" && !new LibraryPreferences().SharingEnabled);
        });
        add("Folder sharing synchronizes the calendar automatically at first idle tick without any extract", async f =>
        {
            var shared = Path.Combine(f.Root, "common"); Directory.CreateDirectory(shared); f.Host.FolderPath = shared;
            var picked = await f.Call("library.sharedFolder.pick"); Check(picked.GetProperty("folder").GetString() == shared && f.Host.FolderPicks == 1);
            var settings = Settings(f, "folder", shared);
            Check((await f.Call("library.connection.test", new { settings })).GetProperty("success").GetBoolean());
            await f.Call("library.settings.save", new { settings }); var entry = CalendarFixture.Entry(await f.Save(f.Event("Planning partagé")));
            var sync = await f.Call("library.tick"); Check(sync.GetProperty("pending").GetInt64() == 0);
            var state = await f.Call("library.get"); Check(state.GetProperty("sync").GetProperty("state").GetString() == "ready"
                && state.GetProperty("sync").GetProperty("lastSuccess").ValueKind == JsonValueKind.String
                && state.GetProperty("sync").GetProperty("nextSync").ValueKind == JsonValueKind.String);
            Check((await f.Call("calendar.get", f.Range())).GetProperty("sync").GetProperty("enabled").GetBoolean());
            var second = new TeamWorkspaceStore(Path.Combine(f.Root, "second-cache", "library.db"));
            await TeamWorkspaceSyncService.SynchronizeAsync(second, new FolderTeamWorkspaceReplica(shared));
            Check((await second.GetAsync(entry.Id))?.Title == "Planning partagé");
            Check(f.Events.Any(item => item.Name == "calendar.changed") && f.Events.Any(item => item.Name == "library.changed"));
        });
        add("An unavailable shared folder leaves a durable offline queue and a retry time", async f =>
        {
            var shared = Path.Combine(f.Root, "unavailable-share"); var settings = Settings(f, "folder", shared);
            await f.Call("library.settings.save", new { settings }); var entry = CalendarFixture.Entry(await f.Save(f.Event()));
            Check(!(await f.Call("library.connection.test", new { settings })).GetProperty("success").GetBoolean());
            await Refuse(() => f.Call("library.sync"), "indisponible");
            var state = await f.Call("library.get"); var sync = state.GetProperty("sync");
            Check(sync.GetProperty("state").GetString() == "offline" && sync.GetProperty("pendingContributions").GetInt64() > 0
                && sync.GetProperty("nextSync").ValueKind == JsonValueKind.String && sync.GetProperty("lastSuccess").ValueKind == JsonValueKind.Null);
            Check(!Directory.Exists(shared) && (await f.Call("calendar.item", new { id = entry.Id })).GetProperty("pending").GetBoolean());
            Directory.CreateDirectory(shared); await f.Call("library.sync");
            Check((await f.Call("library.get")).GetProperty("sync").GetProperty("state").GetString() == "ready");
        });
        add("Local mode and disabled automatic sync never open the configured shared folder", async f =>
        {
            var shared = Path.Combine(f.Root, "optional-share"); Directory.CreateDirectory(shared); var settings = Settings(f, "folder", shared); settings.AutoSync = false;
            await f.Call("library.settings.save", new { settings }); await f.Save(f.Event());
            Check((await f.Call("library.tick")).GetProperty("skipped").GetBoolean() && !Directory.Exists(Path.Combine(shared, "GLPI-Pilot-Shared")));
            Check((await f.Call("library.get")).GetProperty("sync").GetProperty("nextSync").ValueKind == JsonValueKind.Null);
            settings.SyncMode = "local"; settings.AutoSync = true; await f.Call("library.settings.save", new { settings });
            Check((await f.Call("library.tick")).GetProperty("skipped").GetBoolean()); await Refuse(() => f.Call("library.sync"), "Choisissez");
            Check(!Directory.Exists(Path.Combine(shared, "GLPI-Pilot-Shared")) && (await f.Call("calendar.get", f.Range())).GetProperty("occurrences").GetArrayLength() == 1);
        });
        add("A real CSV source preserves calendar identity and durably synchronizes its appointments through the configured folder", async f =>
        {
            var shared = Path.Combine(f.Root, "file-source-share"); Directory.CreateDirectory(shared);
            await f.Call("library.settings.save", new { settings = Settings(f, "folder", shared) });
            var identity = await f.Call("identity.create", new { label = "Agent durable", revision = f.App.Revision });
            var identityKey = identity.GetProperty("key").GetString()!;
            var existing = CalendarFixture.Entry(await f.Save(f.Event("Avant le fichier", participants: new[] { identityKey })));
            f.Host.OpenPath = Path.Combine(f.Root, "real-incidents.csv");
            await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre;Statut;Technicien;Groupe\n42;Source de travail;Nouveau;Agent A;Support\n");
            await f.Call("records.import"); Check(f.App.IsFileSource && !f.App.IsTestMode);
            Check((await f.Call("calendar.get", f.Range())).GetProperty("occurrences").GetArrayLength() == 1
                && (await f.Call("identity.get", new { currentOnly = true })).GetProperty("current").GetProperty("key").GetString() == identityKey);
            var row = (await f.Call("calendar.tickets")).GetProperty("rows")[0];
            var linked = f.Event("Planifié depuis le fichier", "2026-11-02T11:00", "2026-11-02T12:00", identityKey);
            linked.Tickets.Add(new CalendarTicketReference { Key = row.GetProperty("key").GetString()!, Installation = row.GetProperty("installation").GetString()!,
                Id = row.GetProperty("id").GetString()!, Url = row.GetProperty("url").GetString()!, Title = row.GetProperty("title").GetString()! });
            var saved = CalendarFixture.Entry(await f.Save(linked));
            Check(saved.Tickets[0].Installation.StartsWith("file:", StringComparison.Ordinal) && saved.Tickets[0].Url.Length == 0);
            await f.Call("library.sync");
            var actual = (await f.Call("library.get")).GetProperty("sync");
            Check(!actual.GetProperty("simulated").GetBoolean() && actual.GetProperty("state").GetString() == "ready");
            var other = new TeamWorkspaceStore(Path.Combine(f.Root, "other-file-source-cache", "library.db"));
            await TeamWorkspaceSyncService.SynchronizeAsync(other, new FolderTeamWorkspaceReplica(shared));
            Check((await other.GetAsync(existing.Id))?.Title == existing.Title && (await other.GetAsync(saved.Id))?.Title == saved.Title);
            await f.Call("records.file.close");
            Check(!f.App.IsFileSource && !f.App.IsTestMode && (await f.Call("calendar.get", f.Range())).GetProperty("occurrences").GetArrayLength() == 2
                && (await f.Call("identity.get", new { currentOnly = true })).GetProperty("current").GetProperty("key").GetString() == identityKey);
        });
        add("Simulation rebases a real shared folder and never probes or writes it", async f =>
        {
            var real = Path.Combine(f.Root, "real-share"); Directory.CreateDirectory(real);
            f.Host.OpenPath = Path.Combine(f.Root, "import.csv"); await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre;Statut;Technicien;Groupe\n42;Test local;Nouveau;Agent A;Support\n");
            await f.Call("records.test.import"); var settings = Settings(f, "folder", real);
            var saved = await f.Call("library.settings.save", new { settings, password = "not-retained" });
            Check(saved.GetProperty("simulated").GetBoolean() && saved.GetProperty("settings").GetProperty("sharedFolder").GetString() != real);
            Check((await f.Call("library.connection.test", new { settings })).GetProperty("simulated").GetBoolean());
            Check((await f.Call("library.sharedFolder.pick")).GetProperty("simulated").GetBoolean() && f.Host.FolderPicks == 0);
            await f.Save(f.Event("Planning simulé")); await f.Call("library.sync");
            Check(!Directory.EnumerateFileSystemEntries(real).Any()); await f.Call("records.test.exit");
            Check((await f.Call("library.get")).GetProperty("settings").GetProperty("syncMode").GetString() == "local");
        });
        add("Last successful sharing status survives reopening and is scoped to its destination", async f =>
        {
            var shared = Path.Combine(f.Root, "first-share"); Directory.CreateDirectory(shared); var settings = Settings(f, "folder", shared);
            await f.Call("library.settings.save", new { settings }); await f.Call("library.sync");
            var first = (await f.Call("library.get")).GetProperty("sync").GetProperty("lastSuccess").GetString();
            await using (var reopened = new PilotApplication(new CalendarHost(), (_, _) => { }, f.App.Settings.Copy(), snapshots: new SnapshotStore(Path.Combine(f.Root, "reopened-history"))))
            {
                var state = JsonSerializer.SerializeToElement(await reopened.ExecuteAsync("library.get", JsonSerializer.SerializeToElement(new { }, PilotApplication.Json)), PilotApplication.Json);
                Check(state.GetProperty("sync").GetProperty("lastSuccess").GetString() == first);
            }
            settings.SharedFolder = Path.Combine(f.Root, "second-share"); await f.Call("library.settings.save", new { settings });
            Check((await f.Call("library.get")).GetProperty("sync").GetProperty("lastSuccess").ValueKind == JsonValueKind.Null);
        });
    }
    private static LibraryPreferences Settings(CalendarFixture f, string mode, string folder) => new()
    { LocalFolder = Path.Combine(f.Root, "Library"), SyncMode = mode, SharedFolder = folder, DisplayName = "Agent de test" };
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Sharing bridge assertion failed."); }
    private static async Task Refuse(Func<Task> action, string text)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        { Check(error.Message.Contains(text, StringComparison.OrdinalIgnoreCase)); return; }
        throw new InvalidOperationException("Expected refusal: " + text);
    }
}
