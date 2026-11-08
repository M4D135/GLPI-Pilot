using System.Globalization;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

var checks = new List<(string Name, Func<Task> Run)>();
void Add(string name, Func<CalendarFixture, Task> check) => checks.Add((name, async () => { await using var fixture = new CalendarFixture(); await check(fixture); }));
Add("Free appointments can be planned and reopened before any extract is loaded", async f =>
{
    var empty = await f.Call("calendar.get", f.Range()); Check(!empty.GetProperty("sourceLoaded").GetBoolean());
    var saved = await f.Save(f.Event()); Check(saved.GetProperty("saved").GetBoolean()); var entry = CalendarFixture.Entry(saved);
    var item = await f.Call("calendar.item", new { id = entry.Id }); Check(item.GetProperty("intervention").GetProperty("title").GetString() == "Intervention libre");
    var view = await f.Call("calendar.get", f.Range()); Check(view.GetProperty("occurrences").GetArrayLength() == 1 && view.GetProperty("summary").GetProperty("interventions").GetInt32() == 1);
});
Add("A declared resource and personal identity do not require a ticket in the current data", async f =>
{
    var resource = await f.Call("calendar.resource.save", new { resource = new CalendarResource { Key = "agent-stable-1", Label = "Agent A", TimeZoneId = "Europe/Paris" }, chooseAsMe = true });
    Check(resource.GetProperty("personalTechnician").GetString() == "agent-stable-1");
    var saved = await f.Save(f.Event(participants: new[] { "agent-stable-1" })); Check(saved.GetProperty("saved").GetBoolean());
    var mine = await f.Call("calendar.get", f.Range("mine")); Check(mine.GetProperty("occurrences").GetArrayLength() == 1);
    var all = await f.Call("calendar.get", f.Range()); Check(all.GetProperty("occurrences")[0].GetProperty("key").GetString() == mine.GetProperty("occurrences")[0].GetProperty("key").GetString());
});
Add("Mine without a chosen identity is explicitly empty and never widened to all", async f =>
{
    await f.Save(f.Event()); var result = await f.Call("calendar.get", f.Range("mine"));
    Check(result.GetProperty("identityRequired").GetBoolean() && result.GetProperty("occurrences").GetArrayLength() == 0);
    var summary = await f.Call("calendar.summary", f.Range("mine")); Check(summary.GetProperty("identityRequired").GetBoolean());
});
Add("One shared event counts once for two technicians and retains three ticket identities", async f =>
{
    var value = f.Event(start: "2026-11-02T09:00", end: "2026-11-02T10:30", participants: new[] { "agent-a", "agent-b" });
    value.Tickets = Enumerable.Range(1, 3).Select(index => new CalendarTicketReference { Key = "ticket-" + index, Installation = "https://calendar-glpi.test", Id = index.ToString(), Title = "Ticket " + index }).ToList();
    await f.Save(value); var view = await f.Call("calendar.get", f.Range()); var summary = view.GetProperty("summary");
    Check(summary.GetProperty("interventions").GetInt32() == 1 && summary.GetProperty("linkedTickets").GetInt32() == 3 && summary.GetProperty("personHours").GetDouble() == 3);
    Check(view.GetProperty("resources").GetArrayLength() == 2 && view.GetProperty("occurrences")[0].GetProperty("tickets").GetArrayLength() == 3);
});
Add("Concurrent edits cannot overwrite an appointment or its local history", async f =>
{
    var saved = await f.Save(f.Event()); var old = CalendarFixture.Entry(saved); var current = old.Copy(); current.Title = "Version récente";
    var changed = await f.Save(current, old.Revision); var fresh = CalendarFixture.Entry(changed); old.Title = "Version ancienne";
    await Refuse(() => f.Save(old, old.Revision), "changé"); var item = await f.Call("calendar.item", new { id = fresh.Id });
    Check(item.GetProperty("intervention").GetProperty("title").GetString() == "Version récente" && item.GetProperty("history").GetArrayLength() == 2);
});
Add("Overlapping bookings require an explicit override and are still visible afterwards", async f =>
{
    await f.Save(f.Event(participants: new[] { "agent-a" })); var second = f.Event("Deuxième", "2026-11-02T09:30", "2026-11-02T10:30", "agent-a");
    var blocked = await f.Save(second); Check(!blocked.GetProperty("saved").GetBoolean() && blocked.GetProperty("requiresConfirmation").GetBoolean());
    Check((await f.Call("calendar.get", f.Range())).GetProperty("occurrences").GetArrayLength() == 1);
    await f.Save(second, force: true); var view = await f.Call("calendar.get", f.Range()); Check(view.GetProperty("issues").EnumerateArray().Any(issue => issue.GetProperty("kind").GetString() == "overlap"));
});
Add("Changing an appointment does not change the GLPI assignment or ticket status", async f =>
{
    var data = CalendarFixture.Data(new[] { "42", "Incident", "En cours (attribué)", "Agent A", "Support" }); f.App.CalendarLoadFixture(data);
    var ticket = (await f.Call("calendar.tickets", new { rowIndex = 0, revision = f.App.Revision })).GetProperty("rows")[0].Deserialize<CalendarTicketReference>(PilotApplication.Json)!;
    var value = f.Event(participants: new[] { "agent-b" }); value.State = "done"; value.Tickets.Add(ticket); await f.Save(value);
    Check(data.Rows[0][2] == "En cours (attribué)" && data.Rows[0][3] == "Agent A");
});
Add("A linked ticket can leave the current extract without removing its appointment", async f =>
{
    f.App.CalendarLoadFixture(CalendarFixture.Data(new[] { "42", "Incident", "Nouveau", "Agent A", "Support" }));
    var row = (await f.Call("calendar.tickets", new { rowIndex = 0, revision = f.App.Revision })).GetProperty("rows")[0]; var ticket = row.Deserialize<CalendarTicketReference>(PilotApplication.Json)!;
    var value = f.Event(); value.Tickets.Add(ticket); await f.Save(value); f.App.CalendarLoadFixture(CalendarFixture.Data());
    var view = await f.Call("calendar.get", f.Range()); Check(view.GetProperty("occurrences").GetArrayLength() == 1 && view.GetProperty("occurrences")[0].GetProperty("warnings").GetArrayLength() > 0);
    var linked = await f.Call("calendar.forTicket", new { ticketKey = ticket.Key, from = "2026-11-01", to = "2026-12-01" }); Check(linked.GetProperty("total").GetInt32() == 1);
});
Add("Ticket scheduling prefills retain full installation and reject stale row coordinates", async f =>
{
    f.App.CalendarLoadFixture(CalendarFixture.Data(new[] { "42", "Incident", "Nouveau", "Agent A", "Support" })); var revision = f.App.Revision;
    var rows = await f.Call("calendar.tickets", new { indices = new[] { 0 }, revision }); var ticket = rows.GetProperty("rows")[0];
    Check(ticket.GetProperty("installation").GetString() == "https://calendar-glpi.test" && ticket.GetProperty("url").GetString()!.Contains("ticket.form.php?id=42"));
    f.App.CalendarLoadFixture(CalendarFixture.Data(new[] { "99", "Autre", "Nouveau", "Agent B", "Support" }));
    await Refuse(() => f.Call("calendar.tickets", new { indices = new[] { 0 }, revision }), "changé");
});
Add("Backlog excludes a booked ticket across filtered technicians in the selected period", async f =>
{
    f.App.CalendarLoadFixture(CalendarFixture.Data(new[] { "42", "Incident", "Nouveau", "Agent A", "Support" }, new[] { "43", "Demande", "Nouveau", "Agent A", "Support" }));
    var rows = await f.Call("calendar.tickets"); var value = f.Event(participants: new[] { "agent-b" }); value.Tickets.Add(rows.GetProperty("rows")[0].Deserialize<CalendarTicketReference>(PilotApplication.Json)!); await f.Save(value);
    var backlog = await f.Call("calendar.tickets", new { from = "2026-11-02", to = "2026-11-09", unplanned = true, technicians = new[] { TechnicianDashboardService.IdentityKey("Agent A") } });
    Check(backlog.GetProperty("total").GetInt32() == 1 && backlog.GetProperty("rows")[0].GetProperty("id").GetString() == "43");
});
Add("Recurring single-occurrence moves keep the other dates and their opaque identities", async f =>
{
    var value = f.Event(); value.Recurrence = new() { Frequency = "daily", Count = 3 };
    var saved = await f.Save(value); var entry = CalendarFixture.Entry(saved); var before = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences").Clone();
    var changed = entry.Copy(); changed.StartLocal = "2026-11-03T14:00"; changed.EndLocal = "2026-11-03T15:00";
    await f.Call("calendar.save", new { intervention = changed, expectedRevision = entry.Revision, scope = "occurrence", originalStartLocal = "2026-11-03T09:00" });
    var after = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences"); Check(after.GetArrayLength() == 3);
    var moved = after.EnumerateArray().Single(row => row.GetProperty("startLocal").GetString() == "2026-11-03T14:00"); Check(moved.GetProperty("key").GetString() == before[1].GetProperty("key").GetString());
});
Add("A following-series edit is atomic and one Undo restores the original series", async f =>
{
    var value = f.Event(); value.Recurrence = new() { Frequency = "daily", Count = 5 }; var entry = CalendarFixture.Entry(await f.Save(value));
    var next = entry.Copy(); next.StartLocal = "2026-11-04T11:00"; next.EndLocal = "2026-11-04T12:00";
    var split = await f.Call("calendar.save", new { intervention = next, expectedRevision = entry.Revision, scope = "following", originalStartLocal = "2026-11-04T09:00" });
    Check(split.GetProperty("saved").GetBoolean()); var view = await f.Call("calendar.get", f.Range()); Check(view.GetProperty("occurrences").GetArrayLength() == 5);
    await f.Call("calendar.undo", new { token = split.GetProperty("undoToken").GetString() }); var restored = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences");
    Check(restored.GetArrayLength() == 5 && restored.EnumerateArray().All(row => row.GetProperty("startLocal").GetString()!.EndsWith("T09:00", StringComparison.Ordinal)));
});
Add("Editing following from the first occurrence never duplicates the first booking", async f =>
{
    var value = f.Event(); value.Recurrence = new() { Frequency = "daily", Count = 3 }; var entry = CalendarFixture.Entry(await f.Save(value));
    entry.StartLocal = "2026-11-02T11:00"; entry.EndLocal = "2026-11-02T12:00";
    await f.Call("calendar.save", new { intervention = entry, expectedRevision = entry.Revision, scope = "following", originalStartLocal = "2026-11-02T09:00" });
    var rows = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences"); Check(rows.GetArrayLength() == 3 && rows.EnumerateArray().Select(row => row.GetProperty("key").GetString()).Distinct().Count() == 3);
});
Add("Whole-series time edits preserve occurrence UID and moved exception identity", async f =>
{
    var value = f.Event(); value.Recurrence = new() { Frequency = "daily", Count = 3 }; var entry = CalendarFixture.Entry(await f.Save(value));
    var before = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences").EnumerateArray().Select(row => row.GetProperty("key").GetString()).ToArray();
    entry.StartLocal = "2026-11-02T11:00"; entry.EndLocal = "2026-11-02T12:00"; await f.Save(entry, entry.Revision);
    var after = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences").EnumerateArray().Select(row => row.GetProperty("key").GetString()).ToArray(); Check(before.SequenceEqual(after));
});
Add("Removing one recurrence and Undo never removes another day", async f =>
{
    var value = f.Event(); value.Recurrence = new() { Frequency = "daily", Count = 3 }; var entry = CalendarFixture.Entry(await f.Save(value));
    var removed = await f.Call("calendar.remove", new { id = entry.Id, expectedRevision = entry.Revision, scope = "occurrence", originalStartLocal = "2026-11-03T09:00" });
    Check((await f.Call("calendar.get", f.Range())).GetProperty("occurrences").EnumerateArray().Count(row => row.GetProperty("state").GetString() != "cancelled") == 2);
    await f.Call("calendar.undo", new { token = removed.GetProperty("undoToken").GetString() }); Check((await f.Call("calendar.get", f.Range())).GetProperty("occurrences").EnumerateArray().Count(row => row.GetProperty("state").GetString() != "cancelled") == 3);
});
Add("Undo refuses to erase a later edit", async f =>
{
    var saved = await f.Save(f.Event()); var entry = CalendarFixture.Entry(saved); entry.Title = "Modification suivante"; await f.Save(entry, entry.Revision);
    await Refuse(() => f.Call("calendar.undo", new { token = saved.GetProperty("undoToken").GetString() }), "depuis");
});
Add("Bulk move previews before mutation and Undo restores the entire batch", async f =>
{
    var one = CalendarFixture.Entry(await f.Save(f.Event("Un"))); var two = CalendarFixture.Entry(await f.Save(f.Event("Deux", "2026-11-02T11:00", "2026-11-02T12:00")));
    var preview = await f.Call("calendar.shift", new { items = new[] { new { id = one.Id, expectedRevision = one.Revision }, new { id = two.Id, expectedRevision = two.Revision } }, minutes = 60, preview = true });
    Check((await f.Call("calendar.item", new { id = one.Id })).GetProperty("intervention").GetProperty("startLocal").GetString() == one.StartLocal);
    var applied = await f.Call("calendar.shift", new { token = preview.GetProperty("token").GetString(), allowConflicts = true }); Check(applied.GetProperty("saved").GetBoolean());
    await f.Call("calendar.undo", new { token = applied.GetProperty("undoToken").GetString() });
    Check((await f.Call("calendar.item", new { id = two.Id })).GetProperty("intervention").GetProperty("startLocal").GetString() == two.StartLocal);
});
Add("Bulk move refuses the whole batch if any previewed event changed", async f =>
{
    var one = CalendarFixture.Entry(await f.Save(f.Event("Un"))); var two = CalendarFixture.Entry(await f.Save(f.Event("Deux", "2026-11-02T11:00", "2026-11-02T12:00")));
    var preview = await f.Call("calendar.shift", new { items = new[] { new { id = one.Id, expectedRevision = one.Revision }, new { id = two.Id, expectedRevision = two.Revision } }, minutes = 60 });
    two.Title = "Plus récent"; await f.Save(two, two.Revision);
    await Refuse(() => f.Call("calendar.shift", new { token = preview.GetProperty("token").GetString() }), "changé");
    Check((await f.Call("calendar.item", new { id = one.Id })).GetProperty("intervention").GetProperty("startLocal").GetString() == one.StartLocal);
});
Add("A DST gap is never persisted by overriding scheduling conflicts", async f =>
{
    // The conflict check only expands occurrences from about a month before today: anchor the series on the next real
    // Europe/Paris spring-forward night, so the second daily occurrence falls in the gap whatever the current date.
    var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"); var gap = DateTime.UtcNow.Date.AddDays(2);
    while (!paris.IsInvalidTime(DateTime.SpecifyKind(gap.AddHours(2).AddMinutes(15), DateTimeKind.Unspecified))) gap = gap.AddDays(1);
    var eve = gap.AddDays(-1);
    var value = f.Event(start: $"{eve:yyyy-MM-dd}T02:15", end: $"{eve:yyyy-MM-dd}T02:45"); value.Recurrence = new() { Frequency = "daily", Count = 3 };
    await Refuse(() => f.Save(value, force: true), "horaire");
    var view = await f.Call("calendar.get", new { from = eve.ToString("yyyy-MM-dd"), to = eve.AddDays(3).ToString("yyyy-MM-dd"), timeZoneId = "Europe/Paris" }); Check(view.GetProperty("occurrences").GetArrayLength() == 0);
});
Add("Observed sites and their opening windows constrain free interventions", async f =>
{
    var city = new CityDefinition { Name = "Ville" }; var site = new SiteDefinition { Name = "Bureau", CityId = city.Id, Address = "Rue du test", Openings = new() { new() { Day = DayOfWeek.Monday, From = "08:00", Until = "12:00" } } };
    f.App.Settings.Cities.Cities.Add(city); f.App.Settings.Cities.Sites.Add(site);
    var value = f.Event(start: "2026-11-02T14:00", end: "2026-11-02T15:00"); value.SiteId = site.Id;
    var preview = await f.Save(value); Check(!preview.GetProperty("saved").GetBoolean() && preview.GetProperty("issues").EnumerateArray().Any(issue => issue.GetProperty("kind").GetString() == "site-closed"));
});
Add("Slot suggestions respect an existing appointment and declared working hours", async f =>
{
    await f.Call("calendar.resource.save", new { resource = new CalendarResource { Key = "agent-a", Label = "Agent A", TimeZoneId = "Europe/Paris", WorkPeriods = new() { new() { Day = 1, StartMinute = 540, EndMinute = 720, CapacityMinutes = 180 } } } });
    await f.Save(f.Event(participants: new[] { "agent-a" })); var candidate = f.Event("À planifier", "2026-11-02T09:00", "2026-11-02T10:00", "agent-a");
    var suggestions = await f.Call("calendar.suggest", new { intervention = candidate, from = "2026-11-02", to = "2026-11-03", timeZoneId = "Europe/Paris" });
    Check(suggestions.GetProperty("suggestions").GetArrayLength() > 0 && suggestions.GetProperty("suggestions").EnumerateArray().All(slot => string.CompareOrdinal(slot.GetProperty("startLocal").GetString(), "2026-11-02T10:00") >= 0));
    var city = new CityDefinition { Name = "Ville" }; var site = new SiteDefinition { Name = "Bureau tardif", CityId = city.Id,
        Openings = new() { new() { Day = DayOfWeek.Monday, From = "11:00", Until = "12:00" } } };
    f.App.Settings.Cities.Cities.Add(city); f.App.Settings.Cities.Sites.Add(site); candidate.SiteId = site.Id;
    suggestions = await f.Call("calendar.suggest", new { intervention = candidate, from = "2026-11-02", to = "2026-11-03", timeZoneId = "Europe/Paris", limit = 1 });
    Check(suggestions.GetProperty("suggestions").GetArrayLength() == 1 && suggestions.GetProperty("suggestions")[0].GetProperty("startLocal").GetString() == "2026-11-02T11:00");
});
Add("ICS and printable briefing use actual stored appointment times and escaped content", async f =>
{
    var value = f.Event("Réunion <script>alert(1)</script>"); await f.Save(value); f.Host.SavePath = Path.Combine(f.Root, "planning.ics");
    await f.Call("calendar.export", new { from = "2026-11-02", to = "2026-11-09", timeZoneId = "Europe/Paris", format = "ics" });
    var ics = await File.ReadAllTextAsync(f.Host.SavePath); Check(ics.Contains("DTSTART:20261102T080000Z", StringComparison.Ordinal));
    f.Host.SavePath = Path.Combine(f.Root, "planning.html"); await f.Call("calendar.export", new { from = "2026-11-02", to = "2026-11-09", format = "html" });
    var html = await File.ReadAllTextAsync(f.Host.SavePath); Check(!html.Contains("<script>alert", StringComparison.Ordinal) && html.Contains("&lt;script&gt;", StringComparison.Ordinal));
});
Add("Simulation planning and the live profile remain isolated through CSV import and exit", async f =>
{
    await f.Save(f.Event("Planning habituel"));
    f.Host.OpenPath = Path.Combine(f.Root, "incidents.csv"); await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre;Statut;Technicien;Groupe\n42;Import local;Nouveau;Agent A;Support\n");
    await f.Call("records.test.import"); Check(f.App.IsTestMode);
    var fresh = await f.Call("calendar.get", f.Range()); Check(fresh.GetProperty("occurrences").GetArrayLength() == 0);
    await f.Save(f.Event("Planning simulé")); await f.Call("library.sync");
    var sim = await f.Call("calendar.get", f.Range()); Check(sim.GetProperty("occurrences").GetArrayLength() == 1 && sim.GetProperty("sync").GetProperty("simulated").GetBoolean());
    await f.Call("records.test.exit"); var live = await f.Call("calendar.get", f.Range()); Check(live.GetProperty("occurrences").GetArrayLength() == 1 && live.GetProperty("occurrences")[0].GetProperty("title").GetString() == "Planning habituel");
});
Add("A reminder list read does not suppress a subsequent requested notification", async f =>
{
    var start = DateTime.UtcNow.AddMinutes(5); var value = f.Event(start: start.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), end: start.AddMinutes(30).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));
    value.TimeZoneId = "UTC"; value.ReminderMinutes = 10; await f.Save(value);
    var early = f.Event("Préparation la semaine précédente", start.AddDays(6).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), start.AddDays(6).AddMinutes(30).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));
    early.TimeZoneId = "UTC"; early.ReminderMinutes = 10080; await f.Save(early);
    await f.Call("calendar.reminders.check", new { scope = "all", notify = false }); Check(f.Host.Notifications == 0);
    await f.Call("calendar.reminders.check", new { scope = "all", notify = true }); await f.Call("calendar.reminders.check", new { scope = "all", notify = true }); Check(f.Host.Notifications == 2);
});
Add("Real automatic ticks notify only the chosen identity once outside quiet or editing periods and never in simulation", async f =>
{
    var start = DateTime.UtcNow.AddMinutes(5); var own = f.Event("Mon rappel", start.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), start.AddMinutes(30).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), "agent-me");
    own.TimeZoneId = "UTC"; own.ReminderMinutes = 10; await f.Save(own);
    var other = own.Copy(); other.Id = Guid.NewGuid().ToString("N"); other.Title = "Rappel collègue"; other.ParticipantKeys = new() { "agent-other" }; await f.Save(other);
    f.App.Settings.Automation.QuietHours = false;
    await f.App.TickAsync(); Check(f.Host.Notifications == 0);
    await f.Call("calendar.identity", new { key = "agent-me" });
    f.App.Settings.Automation.QuietHours = true; f.App.Settings.Automation.QuietStartHour = 0; f.App.Settings.Automation.QuietEndHour = 0;
    await f.App.TickAsync(); Check(f.Host.Notifications == 0);
    f.App.Settings.Automation.QuietHours = false; f.App.SessionLocked = true;
    await f.App.TickAsync(); Check(f.Host.Notifications == 0); f.App.SessionLocked = false; f.App.EditorOpen = true;
    await f.App.TickAsync(); Check(f.Host.Notifications == 0); f.App.EditorOpen = false;
    await f.App.TickAsync(); Check(f.Host.Notifications == 1);
    var checks = f.Events.Count(item => item.Name == "calendar.reminders.due"); await f.App.TickAsync();
    Check(f.Host.Notifications == 1 && f.Events.Count(item => item.Name == "calendar.reminders.due") == checks);
    await f.Call("calendar.reminders.check", new { scope = "mine", notify = true }); Check(f.Host.Notifications == 1);
    f.Host.OpenPath = Path.Combine(f.Root, "reminder-import.csv"); await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre;Statut;Technicien;Groupe\n42;Test local;Nouveau;Agent A;Support\n");
    await f.Call("records.test.import"); own.Id = Guid.NewGuid().ToString("N"); await f.Save(own); await f.Call("calendar.identity", new { key = "agent-me" });
    await f.App.TickAsync(); var simulated = await f.Call("calendar.reminders.check", new { scope = "mine", notify = true });
    Check(simulated.GetProperty("count").GetInt32() == 1 && f.Host.Notifications == 1);
});
Add("Saved tour visits remain visible without an extract at their actual start and finish", async f =>
{
    var tour = SavedTour(f, 2); new ScheduledTourStore().With(tour).Save(Path.Combine(f.Root, "scheduled-tours.json"));
    var view = await f.Call("calendar.get", f.Range()); var rows = view.GetProperty("occurrences");
    Check(rows.GetArrayLength() == 2 && rows[0].GetProperty("startLocal").GetString() == "2026-11-02T08:45" && rows[0].GetProperty("endLocal").GetString() == "2026-11-02T09:15");
    Check(rows[1].GetProperty("startLocal").GetString() == "2026-11-02T09:35" && rows[1].GetProperty("readOnly").GetBoolean());
    Check(view.GetProperty("summary").GetProperty("interventionHours").GetDouble() == 1 && view.GetProperty("resources").GetArrayLength() == 1);
});
Add("A new intervention checks overlaps with every saved tour before saving", async f =>
{
    var tour = SavedTour(f); new ScheduledTourStore().With(tour).Save(Path.Combine(f.Root, "scheduled-tours.json"));
    var result = await f.Save(f.Event("Collision tournée", "2026-11-02T09:00", "2026-11-02T10:00", "agent-a"));
    Check(!result.GetProperty("saved").GetBoolean() && result.GetProperty("issues").EnumerateArray().Any(issue => issue.GetProperty("kind").GetString() == "overlap"));
});
Add("The common calendar also detects overlaps between two independent tours", async f =>
{
    var one = SavedTour(f); var two = one.Copy(); two.Id = Guid.NewGuid().ToString("N"); two.Name = "Autre tournée"; two.Stops[0].Id = Guid.NewGuid().ToString("N");
    new ScheduledTourStore().With(one).With(two).Save(Path.Combine(f.Root, "scheduled-tours.json"));
    var view = await f.Call("calendar.get", f.Range()); Check(view.GetProperty("issues").EnumerateArray().Any(issue => issue.GetProperty("kind").GetString() == "overlap"));
});
Add("A tour with an unresolved DST time is reported and cannot imply a free working day", async f =>
{
    var tour = SavedTour(f); tour.Date = "2027-03-28"; tour.StartTime = "02:00"; new ScheduledTourStore().With(tour).Save(Path.Combine(f.Root, "scheduled-tours.json"));
    var view = await f.Call("calendar.get", new { from = "2027-03-28", to = "2027-03-29", timeZoneId = "Europe/Paris" });
    Check(view.GetProperty("issues").EnumerateArray().Any(issue => issue.GetProperty("kind").GetString() == "tour-time-invalid"));
    await Refuse(() => f.Save(f.Event("À vérifier", "2027-03-28T10:00", "2027-03-28T11:00", "agent-a"), force: true), "horaire");
});
Add("Tour calculation includes common reservations and ready validation requires examined conflicts", async f =>
{
    var prepared = SavedTour(f); var data = CalendarFixture.Data(new[] { "1", "Bureau 1 Incident", "Nouveau", "Agent A", "Support" }); f.App.CalendarLoadFixture(data);
    var enriched = CityDetectionService.Enrich(data, f.App.Settings.Cities, f.App.Settings.Dashboard);
    var context = TourPlanningService.Prepare(enriched, f.App.Settings.Cities, f.App.Settings.Dashboard);
    var key = TechnicianDashboardService.IdentityKey("Agent A"); var tour = ScheduledTourService.Create(context, new[] { 0 }, "Tournée validée", new DateOnly(2026, 11, 2), key); tour.TimeZoneId = "Europe/Paris";
    Check(tour.Stops.Count == 1, "Fixture needs a recognized site.");
    await f.Save(f.Event("Réservation", "2026-11-02T09:00", "2026-11-02T10:00", key)); new ScheduledTourStore().With(tour).Save(Path.Combine(f.Root, "scheduled-tours.json"));
    var opened = await f.Call("terrain.tours.open", new { id = tour.Id, revision = f.App.Revision }); var version = opened.GetProperty("draftVersion").GetInt32();
    var calculated = await f.Call("terrain.tours.calculate", new { revision = f.App.Revision, draftVersion = version });
    Check(calculated.GetProperty("calendarIssues").EnumerateArray().Any(issue => issue.GetProperty("kind").GetString() == "overlap"));
    await Refuse(() => f.Call("terrain.tours.transition", new { revision = f.App.Revision, draftVersion = version, state = "ready", reason = "Contrôle" }), "calendrier commun");
    var ready = await f.Call("terrain.tours.transition", new { revision = f.App.Revision, draftVersion = version, state = "ready", reason = "Arbitrage explicite", allowCalendarConflicts = true,
        calendarConflictFingerprint = calculated.GetProperty("calendarConflictFingerprint").GetString() });
    Check(ready.GetProperty("draft").GetProperty("state").GetString() == "ready");
});
Add("An override cannot silently accept a newly appeared conflict", async f =>
{
    await f.Save(f.Event("Un", "2026-11-02T09:00", "2026-11-02T10:00", "agent-a"));
    var value = f.Event("Projet", "2026-11-02T09:30", "2026-11-02T11:00", "agent-a");
    var preview = await f.Call("calendar.preview", new { intervention = value });
    await f.Save(f.Event("Autre réservation", "2026-11-02T10:15", "2026-11-02T10:45", "agent-a"));
    var result = await f.Call("calendar.save", new { intervention = value, allowConflicts = true, conflictFingerprint = preview.GetProperty("conflictFingerprint").GetString() });
    Check(!result.GetProperty("saved").GetBoolean() && result.GetProperty("requiresConfirmation").GetBoolean());
});
Add("A rejected new future series remains visible in grouped versions outside the displayed date range", async f =>
{
    await FutureConflict(f); var list = await f.Call("calendar.conflicts.list"); Check(list.GetProperty("total").GetInt64() == 1);
    var group = await f.Call("calendar.conflicts.get", new { id = list.GetProperty("groups")[0].GetProperty("id").GetString() });
    Check(group.GetProperty("items").GetArrayLength() == 2 && group.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("current").GetProperty("deleted").GetBoolean() && !item.GetProperty("incoming").GetProperty("deleted").GetBoolean()));
    var distant = await f.Call("calendar.get", new { from = "2028-01-01", to = "2028-01-08" }); Check(distant.GetProperty("sync").GetProperty("conflicts").GetInt64() == 1);
});
Add("Choosing the incoming version restores every split member together and clears the unresolved group", async f =>
{
    var id = await FutureConflict(f); var group = await f.Call("calendar.conflicts.get", new { id });
    var result = await f.Call("calendar.conflicts.resolve", new { token = group.GetProperty("token").GetString(), choice = "incoming" }); Check(result.GetProperty("resolved").GetBoolean());
    var view = await f.Call("calendar.get", f.Range()); Check(view.GetProperty("occurrences").GetArrayLength() == 3 && view.GetProperty("occurrences").EnumerateArray().Any(row => row.GetProperty("startLocal").GetString() == "2026-11-04T14:00"));
    Check(view.GetProperty("sync").GetProperty("conflicts").GetInt64() == 0 && view.GetProperty("sync").GetProperty("pending").GetInt64() == 2);
    var batch = await f.App.CalendarStoreFixture.PendingCalendarBatchAsync(default); Check(batch?.Mutations.Count == 2);
});
Add("A newer edit invalidates a grouped conflict choice and preserves all versions", async f =>
{
    var id = await FutureConflict(f); var group = await f.Call("calendar.conflicts.get", new { id });
    var current = group.GetProperty("items").EnumerateArray().First(item => !item.GetProperty("current").GetProperty("deleted").GetBoolean()).GetProperty("current");
    var entry = current.GetProperty("intervention").Deserialize<CalendarIntervention>(PilotApplication.Json)!; entry.Title = "Plan actualisé"; await f.Save(entry, current.GetProperty("revision").GetInt64());
    await Refuse(() => f.Call("calendar.conflicts.resolve", new { token = group.GetProperty("token").GetString(), choice = "incoming" }), "changé");
    Check((await f.Call("calendar.conflicts.list")).GetProperty("total").GetInt64() == 1);
});
Add("Generic team deletion cannot break a calendar series or grouped transaction", async f =>
{
    var entry = CalendarFixture.Entry(await f.Save(f.Event()));
    await Refuse(() => f.Call("workspace.remove", new { id = entry.Id, expectedRevision = entry.Revision }), "calendrier");
    Check((await f.Call("calendar.get", f.Range())).GetProperty("occurrences").GetArrayLength() == 1);
});
SharingBridgeChecks.Register(Add);
IdentityBridgeChecks.Register(Add);

var failures = 0;
foreach (var check in checks)
{
    try { await check.Run().WaitAsync(TimeSpan.FromSeconds(60)); Console.WriteLine("PASS " + check.Name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + check.Name + "\n" + error); }
}
Console.WriteLine($"{checks.Count - failures}/{checks.Count} Calendar bridge checks passed."); return failures == 0 ? 0 : 1;
static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
static async Task Refuse(Func<Task> action, string message)
{
    try { await action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
    { Check(error.Message.Contains(message, StringComparison.OrdinalIgnoreCase), "Unexpected refusal: " + error.Message); return; }
    throw new InvalidOperationException("Expected refusal: " + message);
}
static ScheduledTour SavedTour(CalendarFixture f, int stops = 1)
{
    f.App.Settings.Cities.Enabled = true; var city = new CityDefinition { Name = "Ville", Aliases = new() { "Ville" } }; f.App.Settings.Cities.Cities.Add(city);
    var tour = new ScheduledTour { Name = "Tournée", Date = "2026-11-02", StartTime = "08:30", EndTime = "17:30", TimeZoneId = "Europe/Paris", TechnicianKey = "agent-a", SourceKey = "https://calendar-glpi.test" };
    for (var index = 1; index <= stops; index++)
    {
        var site = new SiteDefinition { Name = "Bureau " + index, CityId = city.Id, Address = "Rue " + index, Aliases = new() { "Bureau " + index } }; f.App.Settings.Cities.Sites.Add(site);
        tour.Tickets.Add(new() { TicketId = index.ToString(CultureInfo.InvariantCulture), PlaceKey = "site:" + site.Id, BaselineStatus = 1, BaselineFingerprint = new string('a', 64) });
        tour.Stops.Add(new() { CityId = city.Id, SiteId = site.Id, TicketIds = new() { index.ToString(CultureInfo.InvariantCulture) }, DurationMinutes = 30, TravelMinutes = index == 1 ? 15 : 20 });
    }
    return tour;
}
static async Task<string> FutureConflict(CalendarFixture f)
{
    var initial = f.Event("Série initiale"); initial.Recurrence = new() { Frequency = "daily", Count = 3 };
    var entry = CalendarFixture.Entry(await f.Save(initial)); var store = f.App.CalendarStoreFixture;
    var first = (await store.PendingCalendarBatchAsync(default))!; var accepted = first.Mutations[0].Item.Copy(); accepted.CentralRevision = 1; accepted.Pending = false;
    await store.AcknowledgeCalendarBatchAsync(first, new("accepted", new[] { new TeamWorkspaceReceipt("accepted", accepted) }), default);
    var canonical = (await store.GetAsync(entry.Id))!; entry = CalendarWorkspaceDocuments.ReadIntervention(canonical); var next = entry.Copy(); next.StartLocal = "2026-11-04T14:00"; next.EndLocal = "2026-11-04T15:00";
    await f.Call("calendar.save", new { intervention = next, expectedRevision = entry.Revision, scope = "following", originalStartLocal = "2026-11-04T09:00" });
    var batch = (await store.PendingCalendarBatchAsync(default))!; var receipts = new List<TeamWorkspaceReceipt>(); var revision = 2L;
    foreach (var mutation in batch.Mutations)
    {
        var current = mutation.Item.Id == canonical.Id ? canonical.Copy() : mutation.Item.Copy(); current.CentralRevision = revision++; current.Pending = false;
        if (mutation.Item.Id != canonical.Id) current.Deleted = true;
        receipts.Add(new("conflict", current));
    }
    await store.AcknowledgeCalendarBatchAsync(batch, new("conflict", receipts), default); return batch.MutationId;
}
