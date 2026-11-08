using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class SharedOperationsRuntimeChecks
{
    private const string SharedNote = "Texte partagé synthétique <b>littéral</b>\nSeconde ligne.";
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string Name, Func<Task> Run)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add(("Shared tools: " + name, async () => { await using var f = new SessionFixture(); await run(f); }));
        Add("publication reads the saved note only and does not require an aspirated library", async f =>
        {
            var key = await Note(f, SharedNote);
            await f.Call("workplace.draft.save", new { ticketKey = key, text = "PRIVATE_DRAFT" });
            var before = await File.ReadAllBytesAsync(Path.Combine(f.Root, "workplace.json"));
            var item = Item(await f.Call("workspace.publishPersonalNote", new { ticketKey = key, notes = "UNSAVED_INJECTION" }));
            var doc = (SharedPersonalNoteDocument)TeamWorkspaceBusinessDocuments.Read(item);
            Require(doc.Notes == SharedNote && item.Body == SharedNote && doc.Ticket.Id == "42");
            Require(item.TicketKey == TicketLibraryRules.Key(doc.Ticket) && item.TicketKey != key);
            Require((await File.ReadAllBytesAsync(Path.Combine(f.Root, "workplace.json"))).SequenceEqual(before));
            Require(!TeamWorkspaceCodec.Write(item).Contains("PRIVATE_DRAFT") && !TeamWorkspaceCodec.Write(item).Contains("PRIVATE_CHECKLIST")
                && !TeamWorkspaceCodec.Write(item).Contains("UNSAVED_INJECTION"));
            Require((await new TicketLibraryStore(new LibraryPreferences().DatabasePath).GetStateAsync()).TicketCount == 0);
        });
        Add("explicit note reception appends shared text and preserves private work with a single-use token", async f =>
        {
            var key = await Note(f, SharedNote); var item = Item(await f.Call("workspace.publishPersonalNote", new { ticketKey = key }));
            await f.Call("workplace.personal.save", new { ticketKey = key, notes = "NOTE PRIVÉE À CONSERVER" });
            await f.Call("workplace.draft.save", new { ticketKey = key, text = "BROUILLON PRIVÉ" });
            var before = await File.ReadAllBytesAsync(Path.Combine(f.Root, "workplace.json"));
            var review = await f.Call("workspace.operation.preview", new { id = item.Id });
            Require(review.GetProperty("canApply").GetBoolean() && (await File.ReadAllBytesAsync(Path.Combine(f.Root, "workplace.json"))).SequenceEqual(before));
            var token = review.GetProperty("token").GetString(); var result = await f.Call("workspace.operation.apply", new { token });
            var personal = WorkplaceStore.Load(Path.Combine(f.Root, "workplace.json")).Tickets.Single();
            Require(personal.Notes.StartsWith("NOTE PRIVÉE À CONSERVER", StringComparison.Ordinal) && personal.Notes.Contains(SharedNote, StringComparison.Ordinal));
            Require(personal.Pinned && personal.Checklist.Single().Text == "PRIVATE_CHECKLIST" && personal.FollowupDraft == "BROUILLON PRIVÉ");
            Require(result.GetProperty("route").GetString() == "tickets" && result.GetProperty("params").GetProperty("ticketKey").GetString() == key);
            await Refuse(() => f.Call("workspace.operation.apply", new { token }));
            Require(!(await f.Call("workspace.operation.preview", new { id = item.Id })).GetProperty("canApply").GetBoolean());
        });
        Add("a changed destination, shared revision or installation invalidates the reviewed operation", async f =>
        {
            var key = await Note(f, SharedNote); var item = Item(await f.Call("workspace.publishPersonalNote", new { ticketKey = key }));
            await f.Call("workplace.personal.save", new { ticketKey = key, notes = "LOCAL A" });
            var token = (await f.Call("workspace.operation.preview", new { id = item.Id })).GetProperty("token").GetString();
            await f.Call("workplace.personal.save", new { ticketKey = key, notes = "LOCAL B" });
            await Refuse(() => f.Call("workspace.operation.apply", new { token }));
            token = (await f.Call("workspace.operation.preview", new { id = item.Id })).GetProperty("token").GetString();
            var changed = item.Copy(); var doc = (SharedPersonalNoteDocument)TeamWorkspaceBusinessDocuments.Read(changed);
            doc.Notes = "Nouvelle version partagée"; changed.Body = doc.Notes; changed.Payload = TeamWorkspaceBusinessDocuments.Payload(doc);
            var store = new TeamWorkspaceStore(new LibraryPreferences().DatabasePath); changed = await store.SaveAsync(changed, item.Revision);
            await Refuse(() => f.Call("workspace.operation.apply", new { token }));
            token = (await f.Call("workspace.operation.preview", new { id = item.Id })).GetProperty("token").GetString();
            var settings = f.App.Settings.Copy(); settings.PortalUrl = "https://other-operation.invalid/front/ticket.php";
            await f.Call("settings.save", new { settings, revision = f.App.Revision });
            await Refuse(() => f.Call("workspace.operation.apply", new { token }));
            Require(!(await f.Call("workspace.operation.preview", new { id = item.Id })).GetProperty("canApply").GetBoolean());
            Require(WorkplaceStore.Load(Path.Combine(f.Root, "workplace.json")).Tickets.Single().Notes == "LOCAL B");
        });
        Add("typed documents reject foreign identities, unsupported formats and private technician fields", async f =>
        {
            var key = await Note(f, SharedNote); var original = Item(await f.Call("workspace.publishPersonalNote", new { ticketKey = key }));
            var bad = original.Copy(); bad.TicketKey = "foreign"; Reject(() => TeamWorkspaceCodec.Write(bad));
            bad = original.Copy(); bad.Body = "Different announced text"; Reject(() => TeamWorkspaceCodec.Write(bad));
            bad = original.Copy(); var node = JsonNode.Parse(bad.Payload!.Value.GetRawText())!; node["format"] = 2;
            bad.Payload = JsonSerializer.SerializeToElement(node); Reject(() => TeamWorkspaceCodec.Write(bad));
            bad = original.Copy(); node = JsonNode.Parse(bad.Payload!.Value.GetRawText())!; node["ticket"]!["url"] = "https://foreign.invalid/front/ticket.form.php?id=42";
            bad.Payload = JsonSerializer.SerializeToElement(node); Reject(() => TeamWorkspaceCodec.Write(bad));
            var business = new SharedTechniciansDocument { Installation = "http://session-glpi.test/glpi", Technicians = new() { ["ALICE"] = new() { Skills = ["Réseau"] } } };
            var typed = new TeamWorkspaceItem { Id = Guid.NewGuid().ToString("N"), Kind = "technicians", Title = "Données métier", Payload = TeamWorkspaceBusinessDocuments.Payload(business) };
            node = JsonNode.Parse(typed.Payload!.Value.GetRawText())!; node["technicians"]!["ALICE"]!["notes"] = "PRIVATE_INJECTED";
            typed.Payload = JsonSerializer.SerializeToElement(node); Reject(() => TeamWorkspaceCodec.Write(typed));
        });
        Add("a shared tour becomes a distinct draft while saved tours and original validation remain untouched", async f =>
        {
            var original = Tour(); new ScheduledTourStore { Tours = [original] }.Save(Path.Combine(f.Root, "scheduled-tours.json"));
            var item = Item(await f.Call("workspace.publishTour", new { id = original.Id }));
            var before = await File.ReadAllBytesAsync(Path.Combine(f.Root, "scheduled-tours.json"));
            var review = await f.Call("workspace.operation.preview", new { id = item.Id });
            Require((await File.ReadAllBytesAsync(Path.Combine(f.Root, "scheduled-tours.json"))).SequenceEqual(before));
            var result = await f.Call("workspace.operation.apply", new { token = review.GetProperty("token").GetString() });
            var id = result.GetProperty("params").GetProperty("tourId").GetString();
            var store = ScheduledTourStore.Load(Path.Combine(f.Root, "scheduled-tours.json")); var copy = store.Tours.Single(t => t.Id == id);
            Require(id != original.Id && result.GetProperty("route").GetString() == "tours" && store.Tours.Count == 2);
            Require(copy.State == "draft" && copy.StateHistory.Count == 0 && copy.Tickets.Single().TicketId == "42");
            Require(store.Tours.Single(t => t.Id == original.Id).State == "ready" && copy.SourceKey == original.SourceKey);
        });
        Add("dispatch reception backs up the exact prior scenario and publication refuses unmatched tickets", async f =>
        {
            var key = await Note(f, "note"); var reference = new TicketReference("42", "http://session-glpi.test/glpi/front/ticket.form.php?id=42");
            Require(WorkplaceStore.TicketKey(reference) == key);
            var scenario = new DispatchScenario { Name = "Scénario partagé", Date = new(2030, 1, 7), Assignments = [new() { TicketKey = key, TicketId = "42", TechnicianKey = "ALICE", EstimatedMinutes = 45, Note = "Proposition locale" }] };
            var path = Path.Combine(f.Root, "dispatch-scenario.json"); TechnicianPlanningService.Save(path, scenario);
            var item = Item(await f.Call("workspace.publishDispatch", new { revision = f.App.Revision }));
            scenario.Name = "Ancien scénario privé"; scenario.Assignments[0].EstimatedMinutes = 12; TechnicianPlanningService.Save(path, scenario);
            var before = await File.ReadAllBytesAsync(path); var review = await f.Call("workspace.operation.preview", new { id = item.Id });
            var result = await f.Call("workspace.operation.apply", new { token = review.GetProperty("token").GetString() });
            Require((await File.ReadAllBytesAsync(Path.Combine(f.Root, "SharedImports", result.GetProperty("backupName").GetString()!))).SequenceEqual(before));
            Require(TechnicianPlanningService.Load(path).Name == "Scénario partagé" && TechnicianPlanningService.Load(path).Assignments.Single().EstimatedMinutes == 45);
            Require(result.GetProperty("route").GetString() == "planning");
            scenario.Assignments[0].TicketId = "99"; TechnicianPlanningService.Save(path, scenario);
            await Refuse(() => f.Call("workspace.publishDispatch", new { revision = f.App.Revision }));
            var payload = (SharedDispatchDocument)TeamWorkspaceBusinessDocuments.Read(item); payload.References[0] = new("42", "http://foreign.invalid/front/ticket.form.php?id=42");
            Reject(() => TeamWorkspaceBusinessDocuments.Payload(payload));
        });
        Add("technician sharing excludes personal fields and reception preserves them and other local identities", async f =>
        {
            TechnicianSource(f); var path = Path.Combine(f.Root, "technician-preferences.json");
            var origin = new TechnicianPreference { Favorite = true, ActiveTarget = 9, Notes = "ORIGIN_PRIVATE", AvailabilityNote = "ORIGIN_AVAILABILITY", Skills = ["Réseau"], DailyCapacityMinutes = 240,
                WorkPeriods = [new() { Day = DayOfWeek.Monday, StartMinute = 540, EndMinute = 720, CapacityMinutes = 120 }],
                CapacityDays = [new() { Date = new(2030, 1, 7), MorningMinutes = 60, AfternoonMinutes = 90 }],
                CategoryEstimates = [new() { Category = "Réseau", MinimumMinutes = 15, Minutes = 30, MaximumMinutes = 60 }] };
            new TechnicianPreferences().With("Alice", origin).With("Unobserved", new() { Skills = ["MUST_NOT_SHARE"] }).Save(path);
            var item = Item(await f.Call("workspace.publishTechnicians", new { revision = f.App.Revision })); var text = TeamWorkspaceCodec.Write(item);
            Require(!text.Contains("ORIGIN_PRIVATE") && !text.Contains("ORIGIN_AVAILABILITY") && !text.Contains("MUST_NOT_SHARE") && !text.Contains("activeTarget"));
            var local = new TechnicianPreference { Favorite = false, ActiveTarget = 17, Notes = "RECIPIENT_PRIVATE", AvailabilityNote = "RECIPIENT_AVAILABILITY", Skills = ["Ancienne compétence"], DailyCapacityMinutes = 60,
                WorkPeriods = [new() { Day = DayOfWeek.Friday, StartMinute = 780, EndMinute = 1020, CapacityMinutes = 180 }],
                CapacityDays = [new() { Date = new(2030, 1, 7), MorningMinutes = 120, AfternoonMinutes = 150 }],
                CategoryEstimates = [new() { Category = "Réseau", MinimumMinutes = 90, Minutes = 180, MaximumMinutes = 240 }] };
            new TechnicianPreferences().With("Alice", local).With("Bob", new() { Favorite = true, Notes = "BOB_PRIVATE" }).Save(path);
            var before = await File.ReadAllBytesAsync(path); var review = await f.Call("workspace.operation.preview", new { id = item.Id });
            var comparison = review.GetProperty("changes")[0];
            Require(comparison.GetProperty("current").GetString()!.Contains("5;13:00;17:00;180", StringComparison.Ordinal)
                && comparison.GetProperty("incoming").GetString()!.Contains("1;09:00;12:00;120", StringComparison.Ordinal)
                && comparison.GetProperty("current").GetString()!.Contains("Réseau;90;180;240", StringComparison.Ordinal)
                && comparison.GetProperty("incoming").GetString()!.Contains("Réseau;15;30;60", StringComparison.Ordinal)
                && comparison.GetProperty("incoming").GetString()!.Contains("2030-01-07;60;90", StringComparison.Ordinal));
            var result = await f.Call("workspace.operation.apply", new { token = review.GetProperty("token").GetString() });
            var preferences = TechnicianPreferences.Load(path); var received = preferences.Get("Alice");
            Require(received.Skills.SequenceEqual(["Réseau"]) && received.DailyCapacityMinutes == 240 && !received.Favorite && received.ActiveTarget == 17);
            Require(received.Notes == "RECIPIENT_PRIVATE" && received.AvailabilityNote == "RECIPIENT_AVAILABILITY" && preferences.Get("Bob").Notes == "BOB_PRIVATE");
            Require((await File.ReadAllBytesAsync(Path.Combine(f.Root, "SharedImports", result.GetProperty("backupName").GetString()!))).SequenceEqual(before));
        });
        Add("note limits and cancellation preserve saved work, while a valid unknown ticket can be adopted", async f =>
        {
            var key = await Note(f, SharedNote); var item = Item(await f.Call("workspace.publishPersonalNote", new { ticketKey = key }));
            await f.Call("workplace.personal.save", new { ticketKey = key, notes = new string('x', WorkplaceStore.MaximumNotes) });
            var review = await f.Call("workspace.operation.preview", new { id = item.Id }); Require(!review.GetProperty("canApply").GetBoolean());
            Require(WorkplaceStore.Load(Path.Combine(f.Root, "workplace.json")).Tickets.Single().Notes.Length == WorkplaceStore.MaximumNotes);
            await f.Call("workplace.personal.save", new { ticketKey = key, notes = "Local" });
            review = await f.Call("workspace.operation.preview", new { id = item.Id });
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await f.Call("workspace.operation.apply", new { token = review.GetProperty("token").GetString() }, cancellation.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Require(WorkplaceStore.Load(Path.Combine(f.Root, "workplace.json")).Tickets.Single().Notes == "Local");
            var foreignTicket = new SharedPersonalNoteDocument { Installation = "http://session-glpi.test/glpi", Ticket = new("88", "http://session-glpi.test/glpi/front/ticket.form.php?id=88"), Title = "Nouveau dossier synthétique", Notes = "Note à conserver" };
            var incoming = new TeamWorkspaceItem { Id = Guid.NewGuid().ToString("N"), Kind = "personal-note", Title = "Note 88", Body = foreignTicket.Notes, TicketKey = TicketLibraryRules.Key(foreignTicket.Ticket), Payload = TeamWorkspaceBusinessDocuments.Payload(foreignTicket) };
            await new TeamWorkspaceStore(new LibraryPreferences().DatabasePath).SaveAsync(incoming, null);
            review = await f.Call("workspace.operation.preview", new { id = incoming.Id }); Require(review.GetProperty("canApply").GetBoolean());
            await f.Call("workspace.operation.apply", new { token = review.GetProperty("token").GetString() });
            Require(WorkplaceStore.Load(Path.Combine(f.Root, "workplace.json")).Tickets.Single(t => t.Id == "88").Notes == foreignTicket.Notes);
        });
        return tests;
    }
    private static async Task<string> Note(SessionFixture f, string text)
    {
        var opened = await f.Call("workplace.ticket.open", new { revision = f.App.Revision, rowIndex = 0 });
        var key = opened.GetProperty("personal").GetProperty("key").GetString()!;
        await f.Call("workplace.personal.save", new { ticketKey = key, notes = text, pinned = true, checklist = new[] { new { text = "PRIVATE_CHECKLIST", done = false } } }); return key;
    }
    private static TeamWorkspaceItem Item(JsonElement element) => TeamWorkspaceCodec.Read(element.GetRawText());
    private static void TechnicianSource(SessionFixture f)
    {
        f.App.Settings.Dashboard.ColumnMappings["assignee"] = "Technicien";
        var data = SessionFixture.Data(); data.Headers.Add("Technicien"); data.Rows[0] = data.Rows[0].Concat(["Alice"]).ToArray(); f.App.LoadSessionFixture(data);
    }
    private static ScheduledTour Tour() => new()
    {
        Name = "Tournée synthétique", SourceKey = "http://session-glpi.test/glpi", Date = "2030-01-07", TimeZoneId = "UTC", State = "ready",
        StateHistory = [new("draft", "ready", DateTimeOffset.UtcNow, "Validation de la source")],
        Tickets = [new() { TicketId = "42", PlaceKey = "", BaselineStatus = 1, BaselineFingerprint = new string('a', 64) }]
    };
    private static void Require(bool condition) { if (!condition) throw new Exception("Shared operation assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected strict shared document refusal.");
    }
    private static async Task Refuse(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { return; }
        throw new Exception("Expected reviewed-operation refusal.");
    }
}
