using System.Text.Json;
using GlpiPilot.Core;
using static WorkAssert;

public static class WorkOperationChecks
{
    public static List<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string Name, Func<Task> Run)>();
        void Test(string name, Func<WorkFixture, Task> check) => tests.Add((name, async () => { await using var f = new WorkFixture(); await check(f); }));
        Test("Closed dispatcher and stale source revision are rejected", async f =>
        {
            await Refuse(() => f.Call("workplace.execute", new { script = "forbidden" }), "inconnue");
            await Refuse(() => f.Call("workplace.ticket.open", new { rowIndex = 0, revision = f.App.Revision - 1 }), "changé"); Check(f.App.Operations.Count > 40);
        });
        Test("Source pagination resolves exact row identities", async f =>
        {
            var list = await f.Call("workplace.ticket.list", new { revision = f.App.Revision, pageSize = 2, page = 1 }); Check(list.GetProperty("rows").GetArrayLength() == 2 && I(list, "total") == 4);
            var ticket = await f.Open(0); Check(S(ticket.GetProperty("personal"), "id") == "42" && !ticket.GetProperty("loaded").GetBoolean()); Check(S(ticket.GetProperty("personal"), "url") == WorkFixture.TicketUrl);
        });
        Test("Local import cannot fabricate GLPI ticket reference", async f =>
        {
            var local = WorkFixture.SourceData(); local.SourceUrl = ""; local.Provenance.SourceKind = "local"; f.App.LoadFixture(local); await Refuse(() => f.Open(0), "inconnue");
        });
        Test("Notes checklist reminders drafts survive reopening", async f =>
        {
            var key = await f.Key(); var reminder = DateTimeOffset.Now.AddDays(1);
            await f.Call("workplace.personal.save", new { ticketKey = key, notes = "Note personnelle", pinned = true, queued = true, reminderAt = reminder.ToString("O"), checklist = new[] { new { text = "Vérifier", done = true } } });
            await f.Call("workplace.draft.save", new { ticketKey = key, text = "Brouillon local" }); var p = WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")).Tickets.Single();
            Check(p.Notes == "Note personnelle" && p.FollowupDraft == "Brouillon local" && p.Checklist[0].Done && p.QueueOrder == 0 && p.ReminderAt == reminder);
            await f.Call("workplace.ticket.close", new { ticketKey = key }); Check(S((await f.Call("workplace.ticket.open", new { ticketKey = key })).GetProperty("personal"), "notes") == p.Notes);
        });
        Test("Personal patch preserves read evidence and ignores supplied receipts", async f =>
        {
            f.App.ReadFixture(WorkFixture.Detail()); var key = await f.Key(); await f.Call("workplace.personal.save", new { ticketKey = key, notes = "Saved", lastReadAt = "2000-01-01T00:00:00Z", url = "https://evil.invalid", rowFingerprints = new { }, lastActionReceipt = "fabricated" });
            var p = WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")).Tickets.Single(); Check(p.LastReadAt == WorkFixture.Observed && p.RowFingerprints.Count > 0 && p.Url == WorkFixture.TicketUrl && p.LastActionReceipt == "");
        });
        Test("Invalid personal text never replaces valid persisted data", async f =>
        {
            var key = await f.Key(); await f.Call("workplace.personal.save", new { ticketKey = key, notes = "Intact" }); await Refuse(() => f.Call("workplace.personal.save", new { ticketKey = key, notes = new string('x', 8001) }), "texte invalide");
            Check(WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")).Tickets.Single().Notes == "Intact");
        });
        Test("Offline conservation requires real observations and explicit removal", async f =>
        {
            var key = await f.Key(); await Refuse(() => f.Call("workplace.personal.save", new { ticketKey = key, offline = true }), "Lisez"); f.App.ReadFixture(WorkFixture.Detail()); await f.Call("workplace.personal.save", new { ticketKey = key, offline = true });
            Check(WorkplaceStore.LoadOffline(Path.Combine(f.Folder, "TicketCache"), new("42", WorkFixture.TicketUrl))!.ReadAt == WorkFixture.Observed);
            await f.Call("workplace.personal.save", new { ticketKey = key, offline = false }); Check(!File.Exists(Path.Combine(f.Folder, "TicketCache", key + ".json")));
        });
        Test("Chronology filtering preserves original indexes and missing-date count", async f =>
        {
            f.App.ReadFixture(WorkFixture.Detail()); var section = await f.Call("workplace.ticket.section", new { ticketKey = await f.Key(), sectionIndex = 1, type = "Suivi", from = "2026-10-21", to = "2026-10-22" });
            Check(I(section, "total") == 1 && I(section, "unreadableDates") == 1 && I(section.GetProperty("rows")[0], "index") == 0);
        });
        Test("Read comparison highlights only changed rows", async f =>
        {
            f.App.ReadFixture(WorkFixture.Detail()); var updated = WorkFixture.Detail(); updated.ReadAt = WorkFixture.Observed.AddMinutes(5); updated.Sections[1].Data.Rows[0][3] = "Réponse nouvelle"; f.App.ReadFixture(updated);
            var section = await f.Call("workplace.ticket.section", new { ticketKey = await f.Key(), sectionIndex = 1 }); Check(section.GetProperty("rows")[0].GetProperty("changed").GetBoolean() && !section.GetProperty("rows")[1].GetProperty("changed").GetBoolean());
        });
        Test("Whole-ticket search returns collected evidence with source coordinates", async f =>
        {
            f.App.ReadFixture(WorkFixture.Detail()); var found = await f.Call("workplace.ticket.search", new { ticketKey = await f.Key(), search = "reponse" }); Check(found.GetArrayLength() == 1 && I(found[0], "sectionIndex") == 1 && I(found[0], "rowIndex") == 0);
        });
        Test("Long collected cells remain readable in full and reject obsolete observations", async f =>
        {
            var detail = WorkFixture.Detail(); var full = new string('x', 15000) + " · fin observée"; detail.Sections[1].Data.Rows[0][3] = full; f.App.ReadFixture(detail); var key = await f.Key();
            var page = await f.Call("workplace.ticket.section", new { ticketKey = key, sectionIndex = 1 });
            Check(page.GetProperty("rows")[0].GetProperty("truncatedColumns").EnumerateArray().Single().GetInt32() == 3);
            var cell = await f.Call("workplace.ticket.cell", new { ticketKey = key, sectionIndex = 1, rowIndex = 0, columnIndex = 3, readAt = detail.ReadAt }); Check(S(cell, "text") == full);
            await Refuse(() => f.Call("workplace.ticket.cell", new { ticketKey = key, sectionIndex = 1, rowIndex = 0, columnIndex = 3, readAt = detail.ReadAt.AddMinutes(-1) }), "changé");
        });
        Test("Filtered reader restores the source row on its actual result page", async f =>
        {
            var detail = WorkFixture.Detail(); detail.Sections[1].Data.Rows = Enumerable.Range(0, 150).Select(i => new[] { i % 2 == 0 ? "Suivi" : "Tâche", "21/10/2026 10:00", "Alice", "Ligne " + i }).ToList(); f.App.ReadFixture(detail);
            var result = await f.Call("workplace.ticket.section", new { ticketKey = await f.Key(), sectionIndex = 1, type = "Suivi", focusRowIndex = 120, page = 2 });
            Check(I(result, "page") == 1 && result.GetProperty("rows").EnumerateArray().Any(row => I(row, "index") == 120));
        });
        Test("All collected document references remain reachable through bounded pages", async f =>
        {
            var detail = WorkFixture.Detail(); detail.ObservedItems.AddRange(Enumerable.Range(8, 1050).Select(i => new TicketObservedItem { Kind = "document", Identity = i.ToString(), Title = "Document " + i, Url = "http://workplace-glpi.test/glpi/front/document.send.php?docid=" + i + "&tickets_id=42" })); f.App.ReadFixture(detail); var key = await f.Key();
            var overview = await f.Call("workplace.ticket.insights", new { ticketKey = key }); Check(I(overview, "documentTotal") == 1051 && overview.GetProperty("documents").GetArrayLength() == 50);
            var page = await f.Call("workplace.ticket.links", new { ticketKey = key, kind = "document", page = 21, readAt = detail.ReadAt }); Check(I(page, "total") == 1051 && page.GetProperty("rows").GetArrayLength() == 1 && S(page.GetProperty("rows")[0], "kind") == "document");
            await Refuse(() => f.Call("workplace.document.download", new { ticketKey = key, index = 5, readAt = detail.ReadAt.AddMinutes(-1) }), "changé");
        });
        Test("Reader position and day filters persist typed values", async f =>
        {
            var key = await f.Key(); var position = new TicketReadingPosition { SectionKey = "Traitement", Search = "écran", Type = "Suivi", Author = "Alice", From = new DateTime(2026, 10, 21), ViewMode = 1, RowIndex = 3, ScrollOffset = 42, PageIndex = 2 };
            await f.Call("workplace.ticket.position", new { ticketKey = key, position }); await f.Call("workplace.preferences.save", new { daySearch = "Réseau", dayScope = 3, daySelectedKey = key }); var saved = WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")); Check(saved.Tickets.Single().ReadingPosition.ScrollOffset == 42 && saved.DayScope == 3 && saved.DaySelectedKey == key);
        });
        Test("Task dates and author roles remain unknown without proof", async f =>
        {
            f.App.ReadFixture(WorkFixture.Detail()); var report = (await f.Call("workplace.ticket.insights", new { ticketKey = await f.Key() })).GetProperty("report"); var tasks = report.GetProperty("tasks"); Check(tasks.GetArrayLength() == 2 && tasks[1].GetProperty("overdue").ValueKind == JsonValueKind.Null && S(report.GetProperty("lastExchange"), "role") == "Non déterminée");
        });
        Test("Foreign relation is rejected before navigation", async f =>
        {
            f.App.ReadFixture(WorkFixture.Detail()); var key = await f.Key(); var insights = await f.Call("workplace.ticket.insights", new { ticketKey = key }); Check(insights.GetProperty("relations").GetArrayLength() == 2 && !insights.GetProperty("relations")[1].GetProperty("allowed").GetBoolean()); await Refuse(() => f.Call("workplace.relation.open", new { ticketKey = key, index = 4 }), "installation");
        });
        Test("Verified ticket relation opens a local reader without automatic navigation", async f =>
        {
            f.App.ReadFixture(WorkFixture.Detail()); var result = await f.Call("workplace.relation.open", new { ticketKey = await f.Key(), index = 3 }); Check(S(result, "ticketKey") == WorkplaceStore.TicketKey(new("43", WorkFixture.TicketUrl.Replace("42", "43"))));
        });
        Test("Draft composes only explicitly selected context and copies persisted text", async f =>
        {
            f.App.ReadFixture(WorkFixture.Detail()); var key = await f.Key();
            var result = await f.Call("workplace.draft.compose", new
            {
                ticketKey = key, template = "Diagnostic et prochaine action", findings = "Constat vérifié\r\nDeuxième ligne Windows",
                action = "Analyse locale", nextStep = "Rappeler", contextRows = new[] { 0 }
            });
            var text = S(result, "text");
            Check(text.Contains("Constat vérifié\nDeuxième ligne Windows") && !text.Contains("Réponse visible"), "The selected draft must retain its multiline findings and exclude unselected observations.");
            Check(text == WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")).Tickets.Single(t => t.Key == key).FollowupDraft, "The composition response must contain the exact persisted draft.");
            await f.Call("workplace.draft.copy", new { ticketKey = key });
            Check(f.Host.Clipboard == text, "The clipboard must contain exactly the draft returned to the editor.");
            await Refuse(() => f.Call("workplace.draft.compose", new { ticketKey = key, contextRows = new[] { 10000 } }), "contexte");
        });
        Test("Handover includes personal notes without invented GLPI observations", async f =>
        {
            var key = await f.Key(); await f.Call("workplace.personal.save", new { ticketKey = key, notes = "Relais à préciser" }); var result = await f.Call("workplace.ticket.handover", new { ticketKey = key, copy = true }); Check(S(result, "text").Contains("Relais à préciser") && f.Host.Clipboard == S(result, "text"));
        });
        Test("Bulk personal queue deduplicates and reorders stored references", async f =>
        {
            var result = await f.Call("workplace.queue.add", new { revision = f.App.Revision, rows = new[] { 0, 1, 0 } }); Check(I(result, "added") == 2); var keys = WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")).Tickets; await f.Call("workplace.queue.move", new { ticketKey = keys[1].Key, direction = -1 }); Check(WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")).Tickets[1].QueueOrder == 0);
        });
        Test("Checklist templates enforce limits and deletion", async f =>
        {
            await f.Call("workplace.template.save", new { name = "Terrain", items = new[] { "Préparer", "Vérifier" } }); Check(WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")).ChecklistTemplates["Terrain"].Count == 2); await Refuse(() => f.Call("workplace.template.save", new { name = "Trop", items = Enumerable.Repeat("Action", 41).ToArray() }), "long"); await f.Call("workplace.template.remove", new { name = "Terrain" }); Check(!WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")).ChecklistTemplates.ContainsKey("Terrain"));
        });
        AddPlanningTests(tests, Test);
        return tests;
    }

    private static void AddPlanningTests(List<(string Name, Func<Task> Run)> tests, Action<string, Func<WorkFixture, Task>> Test)
    {
        Test("Personal identity drives real day projection", async f =>
        { await f.Call("workplace.technicians.me", new { technicianKey = "Alice" }); var day = await f.Call("workplace.day.get", new { scope = 1 }); Check(I(day, "total") == 2 && day.GetProperty("rows").EnumerateArray().All(row => row.GetProperty("mine").GetBoolean())); });
        Test("Saved absence stays unverified", async f =>
        { var key = await f.Key(); var source = WorkFixture.SourceData(); source.Rows.RemoveAt(0); f.App.LoadFixture(source); var day = await f.Call("workplace.day.get", new { scope = 6 }); var row = day.GetProperty("rows").EnumerateArray().Single(r => S(r, "ticketKey") == key); Check(!row.GetProperty("inSource").GetBoolean() && S(row, "status").Contains("non vérifié")); });
        Test("Historical day inspection preserves current baseline", async f =>
        { await f.Call("workplace.technicians.me", new { technicianKey = "Alice" }); await f.Call("workplace.day.get", new { }); var old = WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")); var archive = WorkFixture.SourceData(); archive.ExtractedAt = archive.ExtractedAt.AddDays(-1); f.App.LoadFixture(archive, new SnapshotDocument { Data = archive, Dashboard = f.App.Settings.Dashboard.Copy() }); await f.Call("workplace.day.get", new { }); var after = WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")); Check(after.DayBaselineAt == old.DayBaselineAt && after.DayBaselineScope == old.DayBaselineScope); });
        Test("Duplicate IDs cannot create dispatch candidates", async f =>
        { var source = WorkFixture.SourceData(); source.Rows.Add(source.Rows[0].ToArray()); f.App.LoadFixture(source); var plan = await f.Call("workplace.planning.get", new { revision = f.App.Revision }); Check(plan.GetProperty("tickets").EnumerateArray().All(row => S(row, "id") != "42")); });
        Test("Quiet and declined notifications preserve pending reminders", async f =>
        { var key = await f.Key(); await f.Call("workplace.personal.save", new { ticketKey = key, reminderAt = DateTimeOffset.Now.AddMinutes(-1).ToString("O") }); NotificationPolicy.CanNotify = () => false; Check(I(await f.Call("workplace.reminders.check", new { }), "count") == 1 && f.Host.Notifications == 0); NotificationPolicy.CanNotify = () => true; await f.Call("workplace.reminders.check", new { }); await f.Call("workplace.reminders.check", new { }); Check(f.Host.Notifications == 2); f.Host.ShowNotification = true; await f.Call("workplace.reminders.check", new { }); await f.Call("workplace.reminders.check", new { }); Check(f.Host.Notifications == 3 && WorkplaceStore.Load(Path.Combine(f.Folder, "workplace.json")).Tickets.Single().ReminderAcknowledgedAt is null); });
        Test("Enrichment prepares without reading and keeps unknown status", async f =>
        { var result = await f.Call("workplace.enrichment.prepare", new { revision = f.App.Revision, rows = new[] { 0, 1 }, label = "Absents", limit = 1 }); Check(I(result.GetProperty("queue"), "total") == 2 && result.GetProperty("queue").GetProperty("rows")[0].GetProperty("outcome").ValueKind == JsonValueKind.Null); Check(TicketEnrichmentService.Load(Path.Combine(f.Folder, "ticket-enrichment.json")).Results.Count == 0); });
        Test("Enrichment refuses other scope installation or ticket ID", async f =>
        { var reference = new TicketReference("42", WorkFixture.TicketUrl); var sourceKey = SnapshotStore.SourceKey(f.Source); await Refuse(() => f.Call("workplace.enrichment.prepare", new { revision = f.App.Revision, sourceKey = "other", references = new[] { reference } }), "autre"); await Refuse(() => f.Call("workplace.enrichment.prepare", new { revision = f.App.Revision, sourceKey, references = new[] { reference with { Url = reference.Url.Replace("/glpi/", "/other/") } } }), "installation"); await Refuse(() => f.Call("workplace.enrichment.prepare", new { revision = f.App.Revision, sourceKey, references = new[] { reference with { Id = "43" } } }), "identité"); });
        Test("Demo cannot start readings or assisted writes", async f =>
        { var key = await f.Key(); var data = WorkFixture.SourceData(); data.Provenance.SourceKind = "demo"; f.App.LoadFixture(data); await Refuse(() => f.Call("workplace.enrichment.run", new { }), "actuel"); await Refuse(() => f.Call("workplace.actions.inspect", new { ticketKey = key }), "actuel"); });
        Test("Enrichment priority and explicit reset persist", async f =>
        { await f.Call("workplace.enrichment.prepare", new { revision = f.App.Revision, rows = new[] { 0, 1 } }); var q = await f.Call("workplace.enrichment.get", new { }); var key = S(q.GetProperty("rows")[1], "ticketKey"); var updated = await f.Call("workplace.enrichment.update", new { priorityKey = key, limit = 2 }); Check(S(updated.GetProperty("rows")[0], "ticketKey") == key); await Refuse(() => f.Call("workplace.enrichment.update", new { reset = true }), "Confirmez"); });
        Test("Team workload drilldown uses original row coordinates", async f =>
        { var team = await f.Call("workplace.technicians.get", new { revision = f.App.Revision }); Check(I(team, "total") == 2); var alice = team.GetProperty("rows").EnumerateArray().Single(r => S(r, "name") == "Alice"); Check(I(alice, "activeCount") == 2); var rows = await f.Call("workplace.technicians.rows", new { revision = f.App.Revision, technicianKey = "Alice", measure = "active" }); Check(rows.GetProperty("rows").EnumerateArray().Select(r => I(r, "rowIndex")).SequenceEqual(new[] { 0, 1 })); });
        Test("Typed calendars and category models persist capacity exceptions", async f =>
        { await f.Call("workplace.technicians.save", new { technicianKey = "Alice", preference = WorkFixture.Preference() }); var read = await f.Call("workplace.technicians.preference", new { technicianKey = "Alice" }); Check(read.GetProperty("preference").GetProperty("workPeriods").GetArrayLength() == 1); var team = await f.Call("workplace.technicians.get", new { revision = f.App.Revision, date = "2026-10-26" }); Check(I(team.GetProperty("rows").EnumerateArray().Single(r => S(r, "name") == "Alice"), "capacityMinutes") == 90); });
        Test("Overlapping presence cannot overwrite valid calendar", async f =>
        { var good = WorkFixture.Preference(); await f.Call("workplace.technicians.save", new { technicianKey = "Alice", preference = good }); var bad = good.Copy(); bad.WorkPeriods.Add(good.WorkPeriods[0].Copy()); await Refuse(() => f.Call("workplace.technicians.save", new { technicianKey = "Alice", preference = bad }), "chevauchent"); Check(TechnicianPreferences.Load(Path.Combine(f.Folder, "technician-preferences.json")).Get("Alice").WorkPeriods.Count == 1); });
        Test("Coverage matrix discloses shared capacity", async f =>
        { await f.Call("workplace.technicians.save", new { technicianKey = "Alice", preference = WorkFixture.Preference() }); var matrix = await f.Call("workplace.technicians.coverage", new { revision = f.App.Revision, date = "2026-10-26", territory = "Nord", skill = "Réseau" }); Check(I(matrix, "total") == 14 && matrix.GetProperty("rows").EnumerateArray().Any(r => S(r, "evidence").Contains("partagée"))); });
        Test("Recommendations and proposal save are real local calculations", async f =>
        { foreach (var key in new[] { "Alice", "Bob" }) await f.Call("workplace.technicians.save", new { technicianKey = key, preference = WorkFixture.Preference() }); await f.Call("workplace.planning.save", new { name = "Terrain", date = "2026-10-26" }); var key42 = await f.Key(); var ranked = await f.Call("workplace.planning.recommend", new { revision = f.App.Revision, ticketKey = key42, requiredSkills = new[] { "Réseau" } }); Check(ranked.GetProperty("recommendations").GetArrayLength() == 2); await f.Call("workplace.planning.assign", new { revision = f.App.Revision, ticketKey = key42, technicianKey = "Bob", minutes = 40, requiredSkills = new[] { "Réseau" } }); Check(TechnicianPlanningService.Load(Path.Combine(f.Folder, "dispatch-scenario.json")).Assignments.Single().TechnicianKey == TechnicianDashboardService.IdentityKey("Bob") && f.Source.Rows[0][5] == "Alice"); });
        Test("Unavailable assignment needs explicit local override", async f =>
        { var p = WorkFixture.Preference(); p.UnavailableFrom = new(2026, 10, 26); p.UnavailableUntil = new(2026, 10, 26); await f.Call("workplace.technicians.save", new { technicianKey = "Bob", preference = p }); await f.Call("workplace.planning.save", new { date = "2026-10-26" }); var key = await f.Key(); await Refuse(() => f.Call("workplace.planning.assign", new { revision = f.App.Revision, ticketKey = key, technicianKey = "Bob", minutes = 30 }), "dérogation"); await f.Call("workplace.planning.assign", new { revision = f.App.Revision, ticketKey = key, technicianKey = "Bob", minutes = 30, overrideConfirmed = true }); });
        Test("Before-after estimates and absent proposals remain separate", async f =>
        { foreach (var key in new[] { "Alice", "Bob" }) await f.Call("workplace.technicians.save", new { technicianKey = key, preference = WorkFixture.Preference() }); await f.Call("workplace.planning.assign", new { revision = f.App.Revision, ticketKey = await f.Key(), technicianKey = "Bob", minutes = 40, overrideConfirmed = true }); var plan = await f.Call("workplace.planning.get", new { revision = f.App.Revision }); Check(plan.GetProperty("loads").EnumerateArray().Any(r => r.GetProperty("beforeMinutes").GetDouble() != r.GetProperty("afterMinutes").GetDouble())); var reduced = WorkFixture.SourceData(); reduced.Rows.RemoveAt(0); f.App.LoadFixture(reduced); plan = await f.Call("workplace.planning.get", new { revision = f.App.Revision }); Check(!plan.GetProperty("proposals")[0].GetProperty("present").GetBoolean()); });
        Test("Team and scenario CSV exports contain actual data", async f =>
        { f.Host.SavePath = Path.Combine(f.Folder, "team.csv"); Check((await f.Call("workplace.technicians.export", new { revision = f.App.Revision })).GetProperty("saved").GetBoolean() && File.ReadAllText(f.Host.SavePath).Contains("Alice")); f.Host.SavePath = Path.Combine(f.Folder, "scenario.csv"); await f.Call("workplace.planning.assign", new { revision = f.App.Revision, ticketKey = await f.Key(), technicianKey = "Bob", minutes = 30 }); await f.Call("workplace.planning.export", new { revision = f.App.Revision }); Check(File.ReadAllText(f.Host.SavePath).Contains("non appliquée dans GLPI")); });
        Test("Forged or unconfirmed preview cannot create a receipt or submit", async f =>
        { var key = await f.Key(); await Refuse(() => f.Call("workplace.actions.confirm", new { ticketKey = key, token = "forged", confirmed = true }), "Aperçu"); await Refuse(() => f.Call("workplace.actions.confirm", new { ticketKey = key, token = "forged", confirmed = false }), "explicite"); Check(!File.Exists(Path.Combine(f.Folder, "workplace-action-receipts.json"))); });
        Test("Malformed local document is never silently replaced", async f =>
        { await File.WriteAllTextAsync(Path.Combine(f.Folder, "workplace.json"), "{broken"); try { await f.Call("workplace.get", new { }); throw new InvalidOperationException("Should reject malformed document"); } catch (JsonException) { } Check(await File.ReadAllTextAsync(Path.Combine(f.Folder, "workplace.json")) == "{broken"); });
    }
}
