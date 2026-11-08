using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;

public static class DeploymentOptionChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add(("Deployment: " + name, async () => { await using var f = new SessionFixture(); await run(f); }));
        Add("all secondary options survive export and import, including adapters, table views and technician capacity", f =>
        {
            new TourServiceSettings { WeatherEndpoint = "https://weather.example.invalid/forecast", RoutingEndpoint = "https://route.example.invalid", RoutingProfile = "cycling" }.Save(Path.Combine(f.Root, "terrain-services.json"));
            TicketActionProfileStore.Save(Path.Combine(f.Root, "ticket-action-adapters.json"), [new() { Name = "Suivi local", Installation = "https://example.invalid/", FormSelector = "form[name=followup]", FieldName = "content", SubmitName = "add", ActionPath = "/front/itilfollowup.form.php", StructureHash = new('a', 64) }]);
            new NamedPreviewStore(Path.Combine(f.Root, "table-views.json")).Save(new() { Name = "Réunion", HeaderFingerprint = new('b', 64), ColumnsCount = 2, Search = "imprimante" });
            PreviewViewPreferences.Save(Path.Combine(f.Root, "preview-view.json"), new[] { "ID", "Titre" }, [new() { SourceIndex = 0, DisplayIndex = 1, Width = 180 }, new() { SourceIndex = 1, DisplayIndex = 0, Width = 400 }]);
            new TechnicianPreferences { Technicians = new() { ["tech-a"] = new() { DailyCapacityMinutes = 360, EstimatedTicketMinutes = 45, Skills = ["réseau"], Favorite = true } } }.Save(Path.Combine(f.Root, "technician-preferences.json"));
            new TourOrderPreferences { Profiles = new() { ["tech-a"] = new() { CityIds = ["site-c", "site-a"] } } }.Save(Path.Combine(f.Root, "tour-order.json"));
            var captured = DeploymentConfigurationService.Capture(f.App.Settings, f.Root);
            var target = Path.Combine(f.Root, "recipient"); var config = Read(captured, f, target);
            DeploymentConfigurationService.Apply(config, new(), target);
            Check(TourServiceSettings.Load(Path.Combine(target, "terrain-services.json")).RoutingProfile == "cycling");
            Check(TicketActionProfileStore.Load(Path.Combine(target, "ticket-action-adapters.json")).Single().FieldName == "content");
            Check(new NamedPreviewStore(Path.Combine(target, "table-views.json")).List().Single().Search == "imprimante");
            var columns = PreviewViewPreferences.Load(Path.Combine(target, "preview-view.json"), new[] { "ID", "Titre" });
            Check(columns[1].Width == 400 && columns[1].DisplayIndex == 0);
            Check(TechnicianPreferences.Load(Path.Combine(target, "technician-preferences.json")).Get("tech-a").DailyCapacityMinutes == 360);
            Check(TourOrderPreferences.Load(Path.Combine(target, "tour-order.json")).Profiles["tech-a"].CityIds.SequenceEqual(new[] { "site-c", "site-a" }));
            return Task.CompletedTask;
        });
        Add("workplace options transfer without the donor tickets or notes and preserve the recipient personal work", f =>
        {
            var donor = Personal("41", "DONOR PRIVATE NOTES");
            WorkplaceStore.Save(Path.Combine(f.Root, "workplace.json"), new() { Tickets = [donor], OpenTicketKeys = [donor.Key], DefaultSection = "overview", GroupTimelineByDay = false, DesktopReminders = false, ChecklistTemplates = new() { ["Équipe"] = ["Vérifier", "Terminer"] } });
            var captured = DeploymentConfigurationService.Capture(f.App.Settings, f.Root);
            Check(!System.Text.Encoding.UTF8.GetString(DeploymentConfigurationService.Serialize(captured)).Contains("DONOR PRIVATE NOTES", StringComparison.Ordinal));
            var target = Path.Combine(f.Root, "recipient"); Directory.CreateDirectory(target); var local = Personal("42", "LOCAL NOTES TO KEEP");
            WorkplaceStore.Save(Path.Combine(target, "workplace.json"), new() { Tickets = [local], OpenTicketKeys = [local.Key], ActiveTicketKey = local.Key, DaySelectedKey = local.Key });
            DeploymentConfigurationService.Apply(Read(captured, f, target), new(), target);
            var restored = WorkplaceStore.Load(Path.Combine(target, "workplace.json"));
            Check(restored.Tickets.Single().Notes == "LOCAL NOTES TO KEEP" && restored.ActiveTicketKey == local.Key && restored.DaySelectedKey == local.Key);
            Check(restored.DefaultSection == "overview" && !restored.GroupTimelineByDay && !restored.DesktopReminders && restored.ChecklistTemplates["Équipe"].Count == 2);
            return Task.CompletedTask;
        });
        Add("rules transfer without donor incidents while recipient alert history remains and obsolete incidents close", f =>
        {
            var rule = new AlertRule { Name = "Retards", Threshold = 3 };
            Write(Path.Combine(f.Root, "alerts.json"), new AlertDocument { Rules = [rule], Incidents = [new() { Detail = "DONOR INCIDENT" }] });
            var captured = DeploymentConfigurationService.Capture(f.App.Settings, f.Root);
            Check(!System.Text.Encoding.UTF8.GetString(DeploymentConfigurationService.Serialize(captured)).Contains("DONOR INCIDENT", StringComparison.Ordinal));
            var target = Path.Combine(f.Root, "recipient"); Directory.CreateDirectory(target);
            var obsolete = new AlertIncident { RuleId = Guid.NewGuid().ToString("N"), Active = true, Detail = "RECIPIENT INCIDENT", History = [new(DateTimeOffset.UtcNow, "Lecture", "À conserver")] };
            Write(Path.Combine(target, "alerts.json"), new AlertDocument { Incidents = [obsolete], Observations = [new() { RuleId = "preserved", Value = 12 }] });
            DeploymentConfigurationService.Apply(Read(captured, f, target), new(), target);
            var alerts = new AlertService(Path.Combine(target, "alerts.json")).Document;
            Check(alerts.Rules.Single().Threshold == 3 && alerts.Incidents.Single().Detail == "RECIPIENT INCIDENT" && !alerts.Incidents[0].Active && alerts.Incidents[0].History.Count == 1 && alerts.Observations.Single().Value == 12);
            return Task.CompletedTask;
        });
        Add("unknown, missing or invalid secondary settings fail before any recipient mutation", f =>
        {
            var target = Path.Combine(f.Root, "recipient"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "sentinel"), "unchanged");
            var original = JsonNode.Parse(DeploymentConfigurationService.Serialize(DeploymentConfigurationService.Capture(f.App.Settings, f.Root)))!;
            void Reject(Action<JsonNode> alter)
            {
                var node = original.DeepClone(); alter(node); var path = Path.Combine(f.Root, "bad.json"); File.WriteAllText(path, node.ToJsonString());
                try { _ = DeploymentConfigurationService.Read(path, target); throw new Exception("Invalid deployment accepted"); } catch (InvalidDataException) { }
                Check(Directory.GetFiles(target).Select(Path.GetFileName).SequenceEqual(new[] { "sentinel" }));
            }
            Reject(n => n["Options"]!["Unknown"] = new JsonObject());
            Reject(n => n["Options"]!.AsObject().Remove("Workplace"));
            Reject(n => n["Options"]!["TerrainServices"]!["RoutingEndpoint"] = "file:///invalid");
            Reject(n => n["Options"]!["Workplace"]!["Tickets"] = new JsonArray());
            Reject(n => n["Options"]!["TableColumns"]!["Profiles"]![new string('a', 64)] = JsonSerializer.SerializeToNode(new { Columns = new[] { new { SourceIndex = 0, DisplayIndex = 0, Visible = false, Width = 100 } } }));
            return Task.CompletedTask;
        });
        Add("all optional stores can be absent on a fresh profile and still produce a complete valid deployment", f =>
        {
            var root = Path.Combine(f.Root, "blank"); Directory.CreateDirectory(root);
            var document = DeploymentConfigurationService.Capture(new(), root);
            Check(document.Options.Count == 8);
            var config = Read(document, f, root); DeploymentConfigurationService.Apply(config, new(), root);
            Check(WorkplaceStore.Load(Path.Combine(root, "workplace.json")).Tickets.Count == 0 && new AlertService(Path.Combine(root, "alerts.json")).Document.Incidents.Count == 0);
            Check(File.Exists(Path.Combine(root, "technician-preferences.json")) && File.Exists(Path.Combine(root, "terrain-services.json")));
            return Task.CompletedTask;
        });
        return tests;
    }
    private static PersonalTicket Personal(string id, string notes)
    {
        var ticket = new TicketReference(id, "https://example.invalid/front/ticket.form.php?id=" + id);
        return new() { Id = id, Url = ticket.Url, Key = WorkplaceStore.TicketKey(ticket), Title = "Ticket personnel", Notes = notes };
    }
    private static DeploymentConfiguration Read(DeploymentConfiguration document, SessionFixture f, string target)
    {
        var path = Path.Combine(f.Root, "export.json"); File.WriteAllBytes(path, DeploymentConfigurationService.Serialize(document));
        return DeploymentConfigurationService.Read(path, target).Configuration;
    }
    private static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value));
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Deployment options assertion failed"); }
}
