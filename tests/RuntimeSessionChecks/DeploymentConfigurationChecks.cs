using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public static class DeploymentConfigurationChecks
{
    public static void WriteNativeFixture(string path)
    {
        var previousMode = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_MODE"); var previousRoot = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-DeploymentFixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1"); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root);
            var settings = new AppSettings { Theme = "light", InterfaceZoomPercent = 115, PortalUrl = "https://deployment.example.invalid/front/ticket.php" };
            settings.Interface.OnboardingCompleted = true; settings.Automation.ScheduledExtraction = false; settings.Automation.RunCampaign = false;
            settings.History.RestoreLastOnStartup = false; settings.Cities.Sites = [new() { Name = "Site déploiement natif" }]; settings.Normalize();
            new UpdatePolicy { RequireSignature = true, TrustedPublisherSha256 = new('1', 64) }.Save(Path.Combine(root, "update-policy.json"));
            UpdateService.WriteJson(Path.Combine(root, "update-share.json"), Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "deployment-native-share"));
            DeploymentConfigurationService.AtomicWrite(Path.GetFullPath(path), DeploymentConfigurationService.Serialize(DeploymentConfigurationService.Capture(settings, root)));
            _ = DeploymentConfigurationService.Read(Path.GetFullPath(path), root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", previousMode); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", previousRoot);
            Directory.Delete(root, true);
        }
    }
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add(("Deployment: " + name, async () => { await using var f = new SessionFixture(); await run(f); }));
        Add("all application preferences and public external options roundtrip without credentials", async f =>
        {
            f.App.Settings.Interface.FollowWindowsMotion = false; f.App.Settings.Interface.ReduceMotion = true;
            f.App.Settings.InterfaceZoomPercent = 125; f.App.Settings.History.RetentionDays = 400; f.App.Settings.Automation.IntervalMinutes = 90;
            f.App.Settings.Dashboard.PersonalTechnician = "Alex"; f.App.Settings.Sources[0].ExportFolder = Path.Combine(f.Root, "source-exports");
            f.App.Settings.Sources[0].Interpretation!.PersonalTechnician = "Camille";
            new LibraryPreferencesStore().Save(new() { ProtectedPassword = "private-library-ciphertext", PasswordBinding = "local-binding", DisplayName = "Responsable" });
            new LanguageAssistantStore().Save(new() { Provider = "compatible", Endpoint = "https://assistant.example.test/v1/chat/completions", Model = "chosen-model", ProtectedApiKey = "private-assistant-ciphertext", KeyEndpoint = "https://assistant.example.test/v1/chat/completions" });
            UpdateService.WriteJson(Path.Combine(f.Root, "update-share.json"), Path.Combine(f.Root, "shared-production"));
            var path = await Export(f); var bytes = File.ReadAllText(path); var copy = DeploymentConfigurationService.Read(path, f.Root).Configuration;
            Check(!bytes.Contains("private-library") && !bytes.Contains("private-assistant") && !bytes.Contains("ProtectedPassword") && !bytes.Contains("ProtectedApiKey"));
            Check(copy.Settings.InterfaceZoomPercent == 125 && copy.Settings.Interface.ReduceMotion && !copy.Settings.Interface.FollowWindowsMotion
                && copy.Settings.History.RetentionDays == 400 && copy.Settings.Automation.IntervalMinutes == 90 && copy.Settings.Dashboard.PersonalTechnician == "Alex"
                && copy.Settings.Sources[0].Interpretation!.PersonalTechnician == "Camille" && copy.Settings.Sources[0].ExportFolder.EndsWith("source-exports")
                && copy.SharedUpdateFolder.EndsWith("shared-production") && copy.Library.GetProperty("DisplayName").GetString() == "Responsable"
                && copy.Assistant.GetProperty("Model").GetString() == "chosen-model" && copy.Options.Count == 8);
        });
        Add("manual import requires confirmation, replaces settings and produces an importable prior configuration", async f =>
        {
            var path = await Export(f); Change(path, node => node["Settings"]!["Theme"] = "light");
            f.Host.OpenPath = path; var preview = await f.Call("deployment.preview");
            Check(preview.GetProperty("sections").GetArrayLength() == 12);
            await Reject(() => f.Call("deployment.apply", new { token = Token(preview) }));
            var applied = await f.Call("deployment.apply", new { token = Token(preview), confirmed = true });
            var backup = applied.GetProperty("backupPath").GetString()!;
            Check(f.App.Settings.Theme == "light" && File.Exists(backup) && DeploymentConfigurationService.Read(backup, f.Root).Configuration.Settings.Theme == "dark"
                && SettingsRecovery.Read(AppPaths.SettingsFile).Theme == "light");
            f.Host.OpenPath = backup; preview = await f.Call("deployment.preview"); await f.Call("deployment.apply", new { token = Token(preview), confirmed = true });
            Check(f.App.Settings.Theme == "dark");
        });
        Add("a changed file, current preference or business state expires the reviewed import", async f =>
        {
            var path = await Export(f); f.Host.OpenPath = path; var preview = await f.Call("deployment.preview");
            Change(path, node => node["Settings"]!["Theme"] = "light");
            await Reject(() => f.Call("deployment.apply", new { token = Token(preview), confirmed = true }));
            preview = await f.Call("deployment.preview"); f.App.Settings.InterfaceZoomPercent = 110;
            await Reject(() => f.Call("deployment.apply", new { token = Token(preview), confirmed = true }));
            preview = await f.Call("deployment.preview"); UpdateService.WriteJson(Path.Combine(f.Root, "update-share.json"), Path.Combine(f.Root, "changed-share"));
            await Reject(() => f.Call("deployment.apply", new { token = Token(preview), confirmed = true }));
            Check(!Directory.Exists(Path.Combine(f.Root, "DeploymentBackups")));
        });
        Add("a complete replacement invalidates former terrain and team configuration drafts even when settings are identical", async f =>
        {
            var terrain = await f.Call("terrain.reference.state"); var draftVersion = terrain.GetProperty("draftVersion").GetInt32();
            var team = Path.Combine(f.Root, "team.json"); ConfigurationTransferService.Export(team, f.App.Settings);
            f.Host.OpenPath = team; var former = await f.Call("configuration.preview");
            var path = await Export(f); f.Host.OpenPath = path; var preview = await f.Call("deployment.preview");
            await f.Call("deployment.apply", new { token = Token(preview), confirmed = true });
            await Reject(() => f.Call("configuration.apply", new { token = Token(former) }));
            await Reject(() => f.Call("terrain.reference.reset", new { draftVersion }));
            Check(DeploymentConfigurationInitializer.PreservesSites(f.Root));
        });
        Add("the demo cannot export, preview or apply fictitious settings and cancellation does not replace options", async f =>
        {
            f.Host.OpenPath = null; Check((await f.Call("deployment.preview")).GetProperty("cancelled").GetBoolean());
            await f.Call("app.demo");
            foreach (var operation in new[] { "deployment.export", "deployment.preview", "deployment.seed.preview", "deployment.apply", "deployment.decline", "deployment.default.save" })
                await Reject(() => f.Call(operation, new { confirmed = true }));
            Check((await f.Call("deployment.state")).GetProperty("simulation").GetBoolean());
        });
        Add("a truly new profile auto imports once and existing users receive a hash-and-version offer", async f =>
        {
            var pack = Path.Combine(f.Root, "pack"); Directory.CreateDirectory(pack);
            var seed = Path.Combine(pack, DeploymentConfigurationService.FileName);
            File.WriteAllBytes(seed, DeploymentConfigurationService.Serialize(DeploymentConfigurationService.Capture(f.App.Settings, f.Root)));
            var target = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Deployment-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(target, "Logs"));
            try
            {
                Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", target);
                var state = DeploymentConfigurationInitializer.TryInitialize(pack, target, "3.8.0");
                Check(state.SeedStatus == "applied" && !state.Offer.Available && SettingsRecovery.Read(Path.Combine(target, "settings.json")).PortalUrl == SessionFixture.SourceA
                    && DeploymentConfigurationInitializer.PreservesSites(target));
                var library = new LibraryPreferencesStore(Path.Combine(target, "library.local.json")).Load(); Check(library.LocalFolder == Path.Combine(target, "Library"));
                Change(seed, node => node["Settings"]!["Theme"] = "light");
                state = DeploymentConfigurationInitializer.TryInitialize(pack, target, "3.8.0");
                Check(state.Offer.Available && SettingsRecovery.Read(Path.Combine(target, "settings.json")).Theme == "dark");
                DeploymentConfigurationInitializer.RememberChoice(target, state.Offer.Sha256, "3.8.0", false);
                Check(!DeploymentConfigurationInitializer.State(pack, target, "3.8.0").Offer.Available
                    && DeploymentConfigurationInitializer.State(pack, target, "3.8.1").Offer.Available && File.Exists(seed));
            }
            finally { Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", f.Root); Directory.Delete(target, true); }
        });
        Add("missing settings never makes a prior profile new, and malformed seeds cannot prevent startup", async f =>
        {
            var pack = Path.Combine(f.Root, "pack"); Directory.CreateDirectory(pack); var seed = Path.Combine(pack, DeploymentConfigurationService.FileName);
            File.WriteAllBytes(seed, DeploymentConfigurationService.Serialize(DeploymentConfigurationService.Capture(f.App.Settings, f.Root)));
            await File.WriteAllTextAsync(Path.Combine(f.Root, "last-ready-version.txt"), "3.7.0");
            var state = DeploymentConfigurationInitializer.TryInitialize(pack, f.Root, "3.8.0");
            Check(state.Offer.Available && !File.Exists(AppPaths.SettingsFile) && !DeploymentConfigurationInitializer.PreservesSites(f.Root));
            await File.WriteAllTextAsync(seed, "not json"); state = DeploymentConfigurationInitializer.TryInitialize(pack, f.Root, "3.8.0");
            Check(state.SeedStatus == "error" && File.ReadAllText(seed) == "not json" && !File.Exists(AppPaths.SettingsFile));
        });
        Add("a mid-transaction filesystem failure restores exact existing settings and keeps its backup", async f =>
        {
            f.App.Settings.Save(); var before = File.ReadAllBytes(AppPaths.SettingsFile);
            var path = await Export(f); Change(path, node => node["Settings"]!["Theme"] = "light"); var imported = DeploymentConfigurationService.Read(path, f.Root);
            Directory.CreateDirectory(Path.Combine(f.Root, "update-share.json"));
            await Reject(() => Task.Run(() => DeploymentConfigurationService.Apply(imported.Configuration, f.App.Settings, f.Root)));
            Check(File.ReadAllBytes(AppPaths.SettingsFile).SequenceEqual(before) && !File.Exists(Path.Combine(f.Root, "update-policy.json"))
                && !File.Exists(Path.Combine(f.Root, "deployment-pending.json")) && !DeploymentConfigurationInitializer.PreservesSites(f.Root)
                && Directory.EnumerateFiles(Path.Combine(f.Root, "DeploymentBackups"), DeploymentConfigurationService.FileName, SearchOption.AllDirectories).Any());
        });
        Add("local secrets survive only an unchanged destination and never enter a portable backup", async f =>
        {
            var library = new LibraryPreferences { ProtectedPassword = "keep-local-password", PasswordBinding = "binding" }; new LibraryPreferencesStore().Save(library);
            var path = await Export(f); var read = DeploymentConfigurationService.Read(path, f.Root);
            var applied = DeploymentConfigurationService.Apply(read.Configuration, f.App.Settings, f.Root);
            Check(new LibraryPreferencesStore().Load().ProtectedPassword == "keep-local-password" && !File.ReadAllText(applied.BackupPath).Contains("keep-local-password"));
            Change(path, node => node["Library"]!["Host"] = "different.example.test"); read = DeploymentConfigurationService.Read(path, f.Root);
            DeploymentConfigurationService.Apply(read.Configuration, f.App.Settings, f.Root);
            Check(new LibraryPreferencesStore().Load().ProtectedPassword.Length == 0);
        });
        Add("an interrupted transaction is recovered before startup and an unrelated journal target is refused", async f =>
        {
            f.App.Settings.Save(); var original = File.ReadAllBytes(AppPaths.SettingsFile); var id = Guid.NewGuid().ToString("N");
            var backup = Path.Combine(f.Root, "DeploymentBackups", id); Directory.CreateDirectory(backup); File.WriteAllBytes(Path.Combine(backup, "settings.json"), original);
            var path = Path.Combine(f.Root, "deployment-pending.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Id = id, Files = new[] { new { Name = "settings.json", Present = true, Sha256 = DeploymentConfigurationService.Digest(original) } } }));
            File.WriteAllText(AppPaths.SettingsFile, "interrupted replacement");
            var state = DeploymentConfigurationInitializer.TryInitialize(Path.Combine(f.Root, "no-pack"), f.Root, "3.8.0");
            Check(File.ReadAllBytes(AppPaths.SettingsFile).SequenceEqual(original) && !File.Exists(path) && state.SeedStatus == "existing");
            var unrelated = Path.Combine(f.Root, "important.json"); File.WriteAllText(unrelated, "preserve");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Id = id, Files = new[] { new { Name = "important.json", Present = false, Sha256 = "" } } }));
            await Reject(() => Task.Run(() => DeploymentConfigurationService.Recover(f.Root))); Check(File.ReadAllText(unrelated) == "preserve" && File.Exists(path));
        });
        Add("unknown permissions, partial or duplicate configurations and profile overwrite exports are rejected", async f =>
        {
            var path = await Export(f); var valid = File.ReadAllText(path);
            foreach (Action<JsonObject> mutate in new Action<JsonObject>[] { node => node["Admin"] = true,
                node => node["Settings"]!.AsObject().Remove("Sources"), node => node["Assistant"]!["ProtectedApiKey"] = "cannot-import-key",
                node => node.Remove("SharedUpdateFolder"), node => node["UpdatePolicy"]!.AsObject().Remove("RequireSignature"),
                node => node["Library"]!.AsObject().Remove("Host"), node => node["Assistant"]!.AsObject().Remove("Endpoint"),
                node => node["Settings"]!["Interface"]!["FrozenColumns"] = 999 })
            {
                File.WriteAllText(path, valid); Change(path, mutate); await Reject(() => Task.Run(() => DeploymentConfigurationService.Read(path, f.Root)));
            }
            File.WriteAllText(path, valid.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 1, \"schemaVersion\": 1", StringComparison.Ordinal));
            await Reject(() => Task.Run(() => DeploymentConfigurationService.Read(path, f.Root)));
            f.Host.SavePath = AppPaths.SettingsFile; await Reject(() => f.Call("deployment.export")); Check(!File.Exists(AppPaths.SettingsFile));
        });
        Add("null public or secondary values cannot interrupt a fresh profile and legitimate nullable options remain valid", f =>
        {
            new TechnicianPreferences { Technicians = new() { ["tech-a"] = new() { ActiveTarget = 20, Skills = ["réseau"] } } }.Save(Path.Combine(f.Root, "technician-preferences.json"));
            new TourOrderPreferences { Profiles = new() { ["tech-a"] = new() { CityIds = ["site-a"] } } }.Save(Path.Combine(f.Root, "tour-order.json"));
            WorkplaceStore.Save(Path.Combine(f.Root, "workplace.json"), new() { ChecklistTemplates = new() { ["Équipe"] = ["Vérifier"] } });
            TicketActionProfileStore.Save(Path.Combine(f.Root, "ticket-action-adapters.json"), [new() { Name = "Suivi", Installation = "https://example.invalid/", FormSelector = "form[name=followup]", FieldName = "content", SubmitName = "add", ActionPath = "/front/itilfollowup.form.php", StructureHash = new('a', 64) }]);
            File.WriteAllText(Path.Combine(f.Root, "alerts.json"), JsonSerializer.Serialize(new AlertDocument { Rules = [new() { Name = "Retards", Threshold = 3 }] }));
            new NamedPreviewStore(Path.Combine(f.Root, "table-views.json")).Save(new() { Name = "Réunion", HeaderFingerprint = new('b', 64), ColumnsCount = 2, Search = "imprimante" });
            PreviewViewPreferences.Save(Path.Combine(f.Root, "preview-view.json"), new[] { "ID", "Titre" }, [new() { SourceIndex = 0, DisplayIndex = 0, Width = 180 }, new() { SourceIndex = 1, DisplayIndex = 1, Width = 400 }]);
            var original = JsonNode.Parse(DeploymentConfigurationService.Serialize(DeploymentConfigurationService.Capture(f.App.Settings, f.Root)))!;
            var paths = new List<object[]>();
            void Collect(JsonNode? value, object[] path)
            {
                if (value is null) return;
                paths.Add(path);
                if (value is JsonObject obj) foreach (var pair in obj) Collect(pair.Value, [.. path, pair.Key]);
                else if (value is JsonArray array) for (var i = 0; i < array.Count; i++) Collect(array[i], [.. path, i]);
            }
            foreach (var key in new[] { "Library", "Assistant", "Options" }) Collect(original[key], [key]);
            Collect(original["Settings"]!["Automation"], ["Settings", "Automation"]);
            var pack = Path.Combine(f.Root, "pack"); Directory.CreateDirectory(pack); var seed = Path.Combine(pack, DeploymentConfigurationService.FileName);
            var failures = new List<string>(); var required = new HashSet<string> { "Library.Host", "Assistant.Endpoint", "Options.Workplace.ChecklistTemplates", "Settings.Automation.Steps", "Settings.Automation.Steps.0" };
            foreach (var path in paths)
            {
                var candidate = original.DeepClone(); JsonNode parent = candidate;
                foreach (var part in path[..^1]) parent = part is int index ? parent[index]! : parent[(string)part]!;
                if (path[^1] is int lastIndex) parent[lastIndex] = null; else parent[(string)path[^1]] = null;
                File.WriteAllText(seed, candidate.ToJsonString());
                var name = string.Join('.', path); var target = Path.Combine(f.Root, "null-profiles", Guid.NewGuid().ToString("N"));
                try
                {
                    var result = DeploymentConfigurationInitializer.TryInitialize(pack, target, "3.8.0");
                    if (required.Contains(name) && result.SeedStatus != "error") failures.Add(name + ": invalid null accepted");
                    if (result.SeedStatus == "error" && File.Exists(Path.Combine(target, "settings.json"))) failures.Add(name + ": invalid settings persisted");
                }
                catch (Exception error) { failures.Add(name + ": " + error.GetType().Name + " " + error.Message); }
            }
            File.WriteAllText(seed, original.ToJsonString());
            var valid = DeploymentConfigurationService.Read(seed, f.Root).Configuration;
            var technicians = valid.Options["Technicians"].Deserialize<TechnicianPreferences>(DeploymentConfigurationService.Json)!;
            foreach (var item in technicians.Technicians.Values) item.ActiveTarget = null;
            valid.Options["Technicians"] = JsonSerializer.SerializeToElement(technicians, DeploymentConfigurationService.Json);
            File.WriteAllBytes(seed, DeploymentConfigurationService.Serialize(valid));
            Check(DeploymentConfigurationInitializer.TryInitialize(pack, Path.Combine(f.Root, "legitimate-null"), "3.8.0").SeedStatus == "applied");
            if (failures.Count > 0) throw new InvalidOperationException("Null deployment failures: " + string.Join("; ", failures));
            Console.WriteLine($"Checked {paths.Count} null mutations through first-launch initialization.");
            return Task.CompletedTask;
        });
        Add("saving the default creates wad, preserves a legacy root file and backs up only the replaced default", async f =>
        {
            var pack = Path.Combine(f.Root, "pack"); Directory.CreateDirectory(pack); f.App.SetDeploymentFixture(pack, "3.8.0");
            var legacy = Path.Combine(pack, DeploymentConfigurationService.FileName); File.WriteAllText(legacy, "legacy retained exactly");
            await Reject(() => f.Call("deployment.default.save"));
            var saved = await f.Call("deployment.default.save", new { confirmed = true, expectedSha256 = "" }); var path = saved.GetProperty("path").GetString()!;
            Check(path == DeploymentConfigurationInitializer.DefaultPath(pack) && saved.GetProperty("backupPath").GetString() == ""
                && Directory.Exists(Path.Combine(pack, "wad")) && File.ReadAllText(legacy) == "legacy retained exactly"
                && !saved.GetProperty("state").GetProperty("offer").GetProperty("available").GetBoolean()
                && saved.GetProperty("state").GetProperty("seedStatus").GetString() == "default-saved");
            var before = File.ReadAllBytes(path); f.App.Settings.Theme = "light";
            saved = await f.Call("deployment.default.save", new { confirmed = true, expectedSha256 = saved.GetProperty("sha256").GetString() });
            Check(File.ReadAllBytes(saved.GetProperty("backupPath").GetString()!).SequenceEqual(before)
                && DeploymentConfigurationService.Read(path, f.Root).Configuration.Settings.Theme == "light");
            var recipient = Path.Combine(f.Root, "recipient"); var initialized = DeploymentConfigurationInitializer.TryInitialize(pack, recipient, "3.8.0");
            Check(initialized.SeedStatus == "applied" && SettingsRecovery.Read(Path.Combine(recipient, "settings.json")).Theme == "light");
        });
        Add("a failure recording the default leaves the previous pack file intact", async f =>
        {
            var pack = Path.Combine(f.Root, "pack"); Directory.CreateDirectory(pack); f.App.SetDeploymentFixture(pack, "3.8.0");
            var path = DeploymentConfigurationInitializer.DefaultPath(pack); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "previous file kept exactly");
            Directory.CreateDirectory(Path.Combine(f.Root, "deployment-state.json"));
            await Reject(() => f.Call("deployment.default.save", new { confirmed = true, expectedSha256 = DeploymentConfigurationService.Digest(File.ReadAllBytes(path)) }));
            Check(File.ReadAllText(path) == "previous file kept exactly"
                && Directory.EnumerateFiles(Path.Combine(f.Root, "DeploymentDefaults"), DeploymentConfigurationService.FileName, SearchOption.AllDirectories).Any());
        });
        Add("a wad default takes precedence over a legacy root default for first launch and preview", async f =>
        {
            var pack = Path.Combine(f.Root, "priority-pack"); Directory.CreateDirectory(Path.Combine(pack, "wad"));
            var legacy = Path.Combine(pack, DeploymentConfigurationService.FileName); var preferred = DeploymentConfigurationInitializer.DefaultPath(pack);
            var configuration = DeploymentConfigurationService.Capture(f.App.Settings, f.Root);
            File.WriteAllBytes(legacy, DeploymentConfigurationService.Serialize(configuration));
            configuration.Settings.Theme = "light"; File.WriteAllBytes(preferred, DeploymentConfigurationService.Serialize(configuration));
            var legacyBefore = File.ReadAllBytes(legacy); var target = Path.Combine(f.Root, "GLPI-Pilot-Checks-PriorityRecipient");
            try
            {
                Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", target);
                var state = DeploymentConfigurationInitializer.TryInitialize(pack, target, "3.10.1");
                Check(state.SeedPath == preferred && state.DefaultPath == preferred && state.SeedStatus == "applied"
                    && SettingsRecovery.Read(Path.Combine(target, "settings.json")).Theme == "light" && File.ReadAllBytes(legacy).SequenceEqual(legacyBefore));
            }
            finally { Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", f.Root); }
            f.App.SetDeploymentFixture(pack, "3.10.1"); var preview = await f.Call("deployment.seed.preview");
            await f.Call("deployment.apply", new { token = Token(preview), confirmed = true });
            Check(f.App.Settings.Theme == "light" && File.ReadAllBytes(legacy).SequenceEqual(legacyBefore));
        });
        Add("an invalid or non-file wad default never falls back to an older root configuration", f =>
        {
            var pack = Path.Combine(f.Root, "invalid-preferred-pack"); Directory.CreateDirectory(Path.Combine(pack, "wad"));
            var preferred = DeploymentConfigurationInitializer.DefaultPath(pack); var legacy = Path.Combine(pack, DeploymentConfigurationService.FileName);
            File.WriteAllBytes(legacy, DeploymentConfigurationService.Serialize(DeploymentConfigurationService.Capture(f.App.Settings, f.Root)));
            File.WriteAllText(preferred, "invalid preferred JSON");
            foreach (var kind in new[] { "invalid", "directory" })
            {
                if (kind == "directory") { File.Delete(preferred); Directory.CreateDirectory(preferred); }
                var target = Path.Combine(f.Root, "GLPI-Pilot-Checks-" + kind + "-recipient");
                try
                {
                    Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", target);
                    var state = DeploymentConfigurationInitializer.TryInitialize(pack, target, "3.10.1");
                    Check(state.SeedStatus == "error" && state.SeedPath == preferred && !File.Exists(Path.Combine(target, "settings.json")));
                }
                finally { Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", f.Root); }
            }
            return Task.CompletedTask;
        });
        Add("a legacy seed preview expires when a preferred wad default appears even with identical contents", async f =>
        {
            var pack = Path.Combine(f.Root, "preview-priority-pack"); Directory.CreateDirectory(pack); f.App.SetDeploymentFixture(pack, "3.10.1");
            var legacy = Path.Combine(pack, DeploymentConfigurationService.FileName);
            var bytes = DeploymentConfigurationService.Serialize(DeploymentConfigurationService.Capture(f.App.Settings, f.Root)); File.WriteAllBytes(legacy, bytes);
            var preview = await f.Call("deployment.seed.preview"); var preferred = DeploymentConfigurationInitializer.DefaultPath(pack);
            Directory.CreateDirectory(Path.GetDirectoryName(preferred)!); File.WriteAllBytes(preferred, bytes);
            await Reject(() => f.Call("deployment.apply", new { token = Token(preview), confirmed = true }));
            Check(!Directory.Exists(Path.Combine(f.Root, "DeploymentBackups")) && File.ReadAllBytes(legacy).SequenceEqual(bytes));
        });
        Add("saving a default refuses stale expected contents or an unexpected new file", async f =>
        {
            var pack = Path.Combine(f.Root, "concurrent-default-pack"); Directory.CreateDirectory(pack); f.App.SetDeploymentFixture(pack, "3.10.1");
            var initial = await f.Call("deployment.state"); var path = DeploymentConfigurationInitializer.DefaultPath(pack);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "another writer's new default");
            await Reject(() => f.Call("deployment.default.save", new { confirmed = true, expectedSha256 = initial.GetProperty("defaultSha256").GetString() }));
            Check(File.ReadAllText(path) == "another writer's new default");
            var observed = DeploymentConfigurationService.Digest(File.ReadAllBytes(path)); File.WriteAllText(path, "newer default contents");
            await Reject(() => f.Call("deployment.default.save", new { confirmed = true, expectedSha256 = observed }));
            Check(File.ReadAllText(path) == "newer default contents");
        });
        Add("an invalid readable preferred file can be deliberately replaced with its exact backup retained", async f =>
        {
            var pack = Path.Combine(f.Root, "repair-default-pack"); Directory.CreateDirectory(Path.Combine(pack, "wad")); f.App.SetDeploymentFixture(pack, "3.10.1");
            var path = DeploymentConfigurationInitializer.DefaultPath(pack); File.WriteAllText(path, "invalid JSON to keep in the backup"); var before = File.ReadAllBytes(path);
            var state = await f.Call("deployment.state");
            Check(state.GetProperty("seedStatus").GetString() == "error" && state.GetProperty("defaultSha256").GetString() == DeploymentConfigurationService.Digest(before));
            var saved = await f.Call("deployment.default.save", new { confirmed = true, expectedSha256 = state.GetProperty("defaultSha256").GetString() });
            Check(File.ReadAllBytes(saved.GetProperty("backupPath").GetString()!).SequenceEqual(before)
                && DeploymentConfigurationService.Read(path, f.Root).Configuration.Settings.Theme == f.App.Settings.Theme);
        });
        return tests;
    }
    private static async Task<string> Export(SessionFixture f)
    { f.Host.SavePath = Path.Combine(f.Root, DeploymentConfigurationService.FileName); var value = await f.Call("deployment.export"); return value.GetProperty("path").GetString()!; }
    private static string Token(JsonElement value) => value.GetProperty("token").GetString()!;
    private static void Change(string path, Action<JsonObject> mutate) { var value = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); mutate(value); File.WriteAllText(path, value.ToJsonString(new() { WriteIndented = true })); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Deployment assertion failed."); }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException or UnauthorizedAccessException) { return; } throw new InvalidOperationException("Expected deployment refusal."); }
}

namespace GlpiPilot.Runtime
{
    public sealed partial class PilotApplication
    {
        internal void SetDeploymentFixture(string directory, string version) { _deploymentDirectory = directory; _deploymentVersion = version; }
    }
}
