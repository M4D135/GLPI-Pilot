using System.Text.Json;
using GlpiPilot.Core;

var checks = 0;
void Check(string name, bool condition) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
void Reject(string name, Action action)
{
    try { action(); } catch (InvalidDataException) { Check(name, true); return; }
    throw new Exception(name + " : invalid document was accepted");
}
var root = Path.Combine(Path.GetTempPath(), "glpi-workplace-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    var ticket = new TicketReference("42", "https://one.example/front/ticket.form.php?id=42");
    var other = new TicketReference("42", "https://two.example/front/ticket.form.php?id=42");
    var alternate = new TicketReference("42", "https://one.example/other/front/ticket.form.php?id=42");
    Check("Ticket identity includes portal", WorkplaceStore.TicketKey(ticket) != WorkplaceStore.TicketKey(other));
    Check("Ticket identity includes GLPI subdirectory", WorkplaceStore.TicketKey(ticket) != WorkplaceStore.TicketKey(alternate));
    Check("Ticket identity ignores transient URL fragment", WorkplaceStore.TicketKey(ticket) == WorkplaceStore.TicketKey(ticket with { Url = ticket.Url + "#treatment" }));
    Check("Ticket identity includes numeric ID", WorkplaceStore.TicketKey(ticket) != WorkplaceStore.TicketKey(ticket with { Id = "43", Url = ticket.Url.Replace("42", "43") }));
    Reject("Reject non-web ticket reference", () => WorkplaceStore.TicketKey(new("42", "file:///C:/ticket.html")));
    Reject("Reject embedded credentials", () => WorkplaceStore.TicketKey(new("42", "https://user:secret@one.example/front/ticket.form.php?id=42")));
    var document = new WorkplaceDocument(); var record = WorkplaceStore.Remember(document, ticket, "Marseille · imprimante");
    Check("Remember opens ticket", document.OpenTicketKeys.Single() == record.Key && document.ActiveTicketKey == record.Key);
    record.Pinned = true; record.QueueOrder = 0; record.Notes = "Notes\nprivées";
    record.Checklist.Add(new() { Text = "Vérifier le résultat", Done = true });
    record.ReminderAt = DateTimeOffset.Now.AddHours(2); record.SnoozedUntil = DateTimeOffset.Now.AddDays(1); record.SnoozeReason = "Rendez-vous";
    var second = WorkplaceStore.Remember(document, other, "Autre portail"); second.QueueOrder = 1;
    WorkplaceStore.Remember(document, ticket, "Marseille · imprimante");
    Check("Reopening keeps stable tab order", document.OpenTicketKeys[0] == record.Key);
    WorkplaceStore.MoveQueue(document, second.Key, -1);
    Check("Personal queue move swaps order", second.QueueOrder == 0 && record.QueueOrder == 1);
    WorkplaceStore.MoveQueue(document, second.Key, -1);
    Check("Queue upper boundary stable", second.QueueOrder == 0);
    var copy = document.Copy(); copy.Tickets[0].Checklist[0].Done = false; copy.FavoriteSections.Clear();
    Check("Personal document copy isolates checklist", document.Tickets[0].Checklist[0].Done);
    Check("Personal document copy isolates favorites", document.FavoriteSections.Count > 0);
    var path = Path.Combine(root, "workplace.json"); WorkplaceStore.Save(path, document); var loaded = WorkplaceStore.Load(path);
    Check("Persist notes with accents and newline", loaded.Tickets[0].Notes == record.Notes);
    Check("Persist reminders and snoozes", loaded.Tickets[0].ReminderAt == record.ReminderAt && loaded.Tickets[0].SnoozedUntil == record.SnoozedUntil);
    Check("Persist pinned and queue state", loaded.Tickets[0].Pinned && loaded.Tickets[0].QueueOrder == 1);
    Check("Persist open ticket context", loaded.OpenTicketKeys.SequenceEqual(document.OpenTicketKeys));
    Check("Default checklist templates available", loaded.ChecklistTemplates.ContainsKey("Intervention"));
    loaded.DesktopReminders = false; WorkplaceStore.Save(path, loaded);
    Check("Desktop reminder preference remains off after copy and reload", !WorkplaceStore.Load(path).Copy().DesktopReminders);
    var broken = document.Copy(); broken.Tickets[0].Notes = new string('x', WorkplaceStore.MaximumNotes + 1);
    Reject("Bounded notes reject without truncation", () => WorkplaceStore.Save(path, broken));
    Check("Invalid save preserves previous file", WorkplaceStore.Load(path).Tickets[0].Notes == record.Notes);
    broken = document.Copy(); broken.Tickets.Add(broken.Tickets[0].Copy()); Reject("Reject duplicate ticket identity", () => WorkplaceStore.Validate(broken));
    broken = document.Copy(); broken.Tickets[0].Checklist.Add(null!); Reject("Reject null checklist item", () => WorkplaceStore.Validate(broken));
    broken = document.Copy(); broken.Tickets[0].Key = "../bad"; Reject("Reject unsafe ticket key", () => WorkplaceStore.Validate(broken));
    broken = document.Copy(); broken.Version = 99; Reject("Reject unsupported document version", () => WorkplaceStore.Validate(broken));
    broken = document.Copy(); broken.OpenTicketKeys.Add("missing");
    Check("Drop stale tab key without inventing ticket", !WorkplaceStore.Validate(broken).OpenTicketKeys.Contains("missing"));
    Check("Display title safely limits presentation only", WorkplaceStore.DisplayTitle("a\nb" + new string('x', 2200)).Length == 2000 && !WorkplaceStore.DisplayTitle("a\nb").Contains('\n'));

    TicketDetailResult Details() => new()
    {
        Ticket = ticket, Title = "Marseille imprimante", ReadAt = DateTimeOffset.Parse("2026-10-21T10:00:00+02:00"),
        Sections = new()
        {
            new() { Name = "Description", Kind = "fields", Data = new() { Headers = new() { "Champ", "Valeur" }, Rows = new() { new[] { "Description", "Panne réseau" } }, Complete = true } },
            new() { Name = "Traitement", Kind = "timeline", TabKey = "treatment", Data = new() { Headers = new() { "Type", "Date", "Auteur", "Contenu", "Informations" }, Rows = new()
            {
                new[] { "Suivi", "20/10/2026 09:00", "Alice", "Premier contrôle", "" },
                new[] { "Tâche", "21/10/2026 08:00", "Bob", "Réparer réseau", "Planification non fournie" },
                new[] { "Suivi", "illisible", "Alice", "Sans date exploitable", "" }
            }, Complete = true } }
        }, Tabs = new() { new() { Key = "documents", Name = "Documents", Loaded = false } }
    };
    var detail = Details(); var hits = TicketReadingService.Search(detail, "reseau");
    Check("Search across all loaded sections and accents", hits.Count == 2 && hits.Select(hit => hit.Section).Distinct().Count() == 2);
    Check("Search has original row and section coordinates", hits[1].SectionIndex == 1 && hits[1].RowIndex == 1);
    Check("Empty whole-ticket query stays empty", TicketReadingService.Search(detail, " ").Count == 0);
    Check("Bounded whole-ticket results", TicketReadingService.Search(detail, "e", 1).Count == 1);
    var timeline = detail.Sections[1].Data.Rows;
    var filtered = TicketReadingService.Filter(timeline, new("Suivi", "alice", new DateTime(2026, 10, 20), new DateTime(2026, 10, 20)));
    Check("Timeline filter combines type author and date", filtered.Rows.Count == 1 && filtered.Rows[0][3] == "Premier contrôle");
    Check("Unreadable timeline dates explicitly counted", filtered.UnreadableDates == 1);
    Check("Date-free timeline filter retains unreadable dates", TicketReadingService.Filter(timeline, new("Suivi")).Rows.Count == 2);
    var firstObservation = WorkplaceStore.ObserveRead(record, detail);
    Check("Initial observation does not claim past changes", !firstObservation.HasPrevious);
    var unchanged = WorkplaceStore.ObserveRead(record, Details()); Check("Same content has no modification", unchanged.ChangedSections.Count == 0 && unchanged.AddedSections.Count == 0);
    detail.Sections[1].Data.Rows[0][3] = "Contrôle terminé"; detail.ReadAt = detail.ReadAt.AddHours(2);
    var changed = WorkplaceStore.ObserveRead(record, detail);
    Check("Detect changed treatment content", changed.ChangedSections.Count == 1 && changed.ChangedSections[0].Contains("Traitement"));
    var less = Details(); less.Sections.RemoveAt(1);
    var missing = WorkplaceStore.ObserveRead(record, less);
    Check("Missing section is non-reread, not deletion", missing.MissingSections.Count == 1 && missing.Summary.Contains("non relue"));
    var summary = TicketReadingService.TreatmentSummary(detail);
    Check("Treatment summary reports unread tabs", summary.Any(row => row[1].Contains("1 onglets non lus")));
    Check("Latest followup selected from dated entries", summary.Any(row => row[0].StartsWith("Suivi · dernière") && row[1].Contains("Contrôle terminé")));
    Check("Event date is not inferred task deadline", summary.Single(row => row[0] == "Prochaine tâche planifiée lue")[1] == "Aucune prochaine date explicite collectée" && TicketInsightService.Analyze(detail, detail.ReadAt).Tasks.Count == 0);
    var handover = TicketReadingService.Handover(record, detail);
    Check("Handover includes selected personal actions and provenance", handover.Contains("Notes\nprivées") && handover.Contains("[x] Vérifier") && handover.Contains(ticket.Url));
    var cache = Path.Combine(root, "cache"); WorkplaceStore.SaveOffline(cache, detail);
    Check("Offline detail keeps read timestamp", WorkplaceStore.LoadOffline(cache, ticket)?.ReadAt == detail.ReadAt);
    Check("Offline cache separates portals", WorkplaceStore.LoadOffline(cache, other) == null);
    Check("Offline detail retains all loaded sections", WorkplaceStore.LoadOffline(cache, ticket)?.Sections.Count == 2);
    WorkplaceStore.RemoveOffline(cache, ticket); Check("Explicit removal deletes offline copy", WorkplaceStore.LoadOffline(cache, ticket) == null);

    var preferences = new TechnicianPreferences().With("Alice", new()
    {
        Skills = new() { "Réseau", "Impression" }, PrimaryTerritories = new() { "Marseille" }, BackupTerritories = new() { "Aubagne" },
        DailyCapacityMinutes = 420, EstimatedTicketMinutes = 30, UnavailableFrom = new DateTime(2026, 10, 22), UnavailableUntil = new DateTime(2026, 10, 23), AvailabilityNote = "Matin sur site"
    }).With("Bob", new() { Skills = new() { "Réseau" }, BackupTerritories = new() { "Marseille" }, DailyCapacityMinutes = 300, EstimatedTicketMinutes = 60 });
    var prefPath = Path.Combine(root, "technicians.json"); preferences.Save(prefPath); var prefRead = TechnicianPreferences.Load(prefPath);
    Check("Planning preferences persist skills and territories", prefRead.Get("Alice").Skills.Contains("Réseau") && prefRead.Get("Alice").PrimaryTerritories.Contains("Marseille"));
    Check("Planning preferences persist declared minutes", prefRead.Get("Bob").EstimatedTicketMinutes == 60);
    Check("Preference with only skills is retained", new TechnicianPreferences().With("OnlySkills", new() { Skills = new() { "Réseau" } }).Technicians.Count == 1);
    Reject("Reject half an absence interval", () => preferences.With("Alice", new() { UnavailableFrom = DateTime.Today }));
    Reject("Reject backwards absence interval", () => preferences.With("Alice", new() { UnavailableFrom = DateTime.Today, UnavailableUntil = DateTime.Today.AddDays(-1) }));
    Reject("Reject impossible daily capacity", () => preferences.With("Alice", new() { DailyCapacityMinutes = 1441 }));
    Check("Declared absence includes final day", TechnicianPlanningService.IsUnavailable(prefRead.Get("Alice"), new DateTime(2026, 10, 23)));
    Check("Absent outside declared interval is not inferred", !TechnicianPlanningService.IsUnavailable(prefRead.Get("Alice"), new DateTime(2026, 10, 24)));
    var source = new ExtractionResult
    {
        Complete = true, Headers = new() { "ID", "Titre", "Statut", "Attribué à - Technicien", "Date d’ouverture" },
        Rows = new() { new[] { "42", "Marseille réseau", "En cours (attribué)", "Alice", "20/10/2026" }, new[] { "43", "Aubagne réseau", "En attente", "Bob", "20/10/2026" } }
    };
    var workload = TechnicianWorkloadService.Calculate(source, new());
    Check("Planning fixture detects two technicians", workload.Technicians.Count == 2);
    var planned = new DispatchTicket(record.Key, "42", "Marseille réseau", "Marseille", "Réseau", new[] { TechnicianDashboardService.IdentityKey("Alice") }, 0);
    var recommends = TechnicianPlanningService.Recommend(planned, workload, preferences, new DateTime(2026, 10, 22), new[] { "Réseau" });
    Check("Declared absence excludes recommendation", !recommends.Single(item => item.Name == "Alice").AvailableForScenario);
    Check("Eligible backup precedes absent primary", recommends[0].Name == "Bob");
    Check("Recommendation explains sector and unknown presence", recommends[0].Evidence.Contains("remplacement") && recommends[0].Evidence.Contains("présence non vérifiée"));
    Check("Missing required skill blocks eligibility", TechnicianPlanningService.Recommend(planned, workload, preferences, new DateTime(2026, 10, 22), new[] { "Inconnue" }).All(item => !item.AvailableForScenario));
    var noCapacity = preferences.With("Bob", new() { DailyCapacityMinutes = 0 });
    Check("Zero declared capacity blocks recommendation", !TechnicianPlanningService.Recommend(planned, workload, noCapacity, new DateTime(2026, 10, 22)).Single(item => item.Name == "Bob").AvailableForScenario);
    var cover = TechnicianPlanningService.Coverage(workload, preferences, new[] { "Marseille", "Paris" }, new DateTime(2026, 10, 22));
    Check("Coverage includes declared primary and backup", cover.Single(item => item.Territory == "Marseille").Primary == "Alice" && cover.Single(item => item.Territory == "Marseille").Backup == "Bob");
    Check("Coverage leaves unavailable primary out of available list", cover.Single(item => item.Territory == "Marseille").AvailableDeclared == "Bob");
    Check("Coverage reports unserved territory explicitly", cover.Single(item => item.Territory == "Paris").Gaps == "Aucun secteur déclaré");
    var scenario = new DispatchScenario { Assignments = new() { new() { TicketKey = planned.Key, TicketId = planned.Id, TechnicianKey = TechnicianDashboardService.IdentityKey("Bob"), EstimatedMinutes = 45 } } };
    var estimated = TechnicianPlanningService.EstimateLoads(workload, preferences, new[] { planned }, scenario);
    Check("Reassignment simulation subtracts old estimate", estimated.Single(item => item.Technician == "Alice").BeforeMinutes == 30 && estimated.Single(item => item.Technician == "Alice").AfterMinutes == 0);
    Check("Reassignment simulation adds explicit new estimate", estimated.Single(item => item.Technician == "Bob").BeforeMinutes == 60 && estimated.Single(item => item.Technician == "Bob").AfterMinutes == 105);
    scenario.Assignments[0].TechnicianKey = TechnicianDashboardService.IdentityKey("Alice");
    Check("Same technician scenario does not double count", TechnicianPlanningService.EstimateLoads(workload, preferences, new[] { planned }, scenario).Single(item => item.Technician == "Alice").AfterMinutes == 45);
    Check("Absent scenario ticket excluded from estimated load", TechnicianPlanningService.EstimateLoads(workload, preferences, Array.Empty<DispatchTicket>(), scenario).Single(item => item.Technician == "Alice").AfterMinutes == 30);
    var scenarioPath = Path.Combine(root, "scenario.json"); TechnicianPlanningService.Save(scenarioPath, scenario);
    Check("Scenario persists proposed assignment", TechnicianPlanningService.Load(scenarioPath).Assignments.Single().EstimatedMinutes == 45);
    scenario.Assignments.Add(scenario.Assignments[0]); Reject("Duplicate ticket proposal rejected", () => TechnicianPlanningService.Validate(scenario));

    var relatedSource = new ExtractionResult
    {
        SourceUrl = "https://one.example/front/ticket.php", ExtractedAt = detail.ReadAt, Complete = false,
        Headers = new() { "ID", "Titre", "Statut", "Catégorie", "Ville", "Solution", "Date de résolution" },
        Rows = new()
        {
            new[] { "42", "Marseille panne réseau", "En cours", "Réseau", "Marseille", "", "" },
            new[] { "43", "MARSEILLE panne reseau", "Résolu", "Réseau", "Marseille", "Remplacer le câble défectueux", "21/10/2026" },
            new[] { "44", "Marseille panne réseau imprimante", "En attente", "Réseau", "Marseille", "Ne pas proposer ce texte non résolu", "" },
            new[] { "45", "Création de compte", "Résolu", "Compte", "Marseille", "Créer le compte", "20/10/2026" }
        }
    };
    var intelligence = TicketIntelligenceService.Analyze(detail, relatedSource, new());
    Check("Local intelligence finds only meaningful title similarities", intelligence.Available && intelligence.Related.Count == 2 && intelligence.Related[0].Id == "43");
    Check("Exact normalized title/category/city explains possible recurrence", intelligence.Related[0].Relationship == "Récurrence possible" && intelligence.Related[0].Evidence.Contains("même ville"));
    Check("Solved source retains real solution and observation date", intelligence.Related[0].Solution == "Remplacer le câble défectueux" && intelligence.Related[0].ObservedAt == relatedSource.ExtractedAt);
    Check("Unresolved source never fabricates a solution suggestion", !intelligence.Related.Single(item => item.Id == "44").HasSolution);
    Check("Local relationship preserves source row and read-only ticket URL", intelligence.Related[0].SourceIndex == 1 && intelligence.Related[0].Ticket.Url == "https://one.example/front/ticket.form.php?id=43");
    Check("Partial source limits are shown by local intelligence", intelligence.Message.Contains("partiel"));
    Check("Normalized title retains asset numbers", TicketIntelligenceService.Normalize("RÉSEAU — 123") == "reseau 123");
    var differentPortal = Details(); differentPortal.Ticket = other;
    Check("Local relationships cannot mix portal identities", !TicketIntelligenceService.Analyze(differentPortal, relatedSource, new()).Available);
    differentPortal.Ticket = alternate;
    Check("Local relationships cannot mix installations on same host", !TicketIntelligenceService.Analyze(differentPortal, relatedSource, new()).Available);
    relatedSource.Rows.Add(relatedSource.Rows[0].ToArray());
    Check("Ambiguous target identity prevents relationships", !TicketIntelligenceService.Analyze(detail, relatedSource, new()).Available);
    relatedSource.Rows.RemoveAt(relatedSource.Rows.Count - 1); relatedSource.Rows.Add(relatedSource.Rows[1].ToArray());
    Check("Duplicate candidate identity excluded without merging", TicketIntelligenceService.Analyze(detail, relatedSource, new()).Related.All(item => item.Id != "43"));
    relatedSource.Rows.RemoveAt(relatedSource.Rows.Count - 1);
    var solutionDetail = new TicketDetailResult
    {
        Ticket = new("43", "https://one.example/front/ticket.form.php?id=43"), ReadAt = detail.ReadAt.AddHours(1),
        Sections = new() { new() { Kind = "timeline", Data = new() { Rows = new() { new[] { "Solution", "21/10/2026 12:00", "Alex", "Solution réellement lue dans la chronologie", "Proposée" } } } } }
    };
    var observedSolution = TicketIntelligenceService.Analyze(detail, relatedSource, new(), new[] { solutionDetail }).Related[0];
    Check("Already read timeline solution is cited with its own read date", observedSolution.Solution.Contains("réellement lue") && observedSolution.ObservedAt == solutionDetail.ReadAt && observedSolution.SolutionSource.Contains("Chronologie"));
    solutionDetail.Ticket = new("43", "https://two.example/front/ticket.form.php?id=43");
    Check("A solution from another portal never overrides this source", TicketIntelligenceService.Analyze(detail, relatedSource, new(), new[] { solutionDetail }).Related[0].Solution == "Remplacer le câble défectueux");
    var cancelled = false; using (var cancellation = new CancellationTokenSource())
    { cancellation.Cancel(); try { TicketIntelligenceService.Analyze(detail, relatedSource, new(), ct: cancellation.Token); } catch (OperationCanceledException) { cancelled = true; } }
    Check("Superseded ticket intelligence cancels before publishing", cancelled);
    var calendar = new TechnicianPreference
    {
        WorkPeriods = TechnicianCalendar.ParsePeriods("1;08:00;12:00;180\n1;13:00;17:00;200"),
        CapacityDays = TechnicianCalendar.ParseDays("2026-10-26;0;90\n2026-10-27;120;"),
        CategoryEstimates = TechnicianCalendar.ParseEstimates("Réseau;20;40;80"), DailyCapacityMinutes = 420, EstimatedTicketMinutes = 30
    };
    TechnicianCalendar.Validate(calendar);
    Check("Recurring capacity adds the two declared periods", TechnicianPlanningService.CapacityForDate(calendar, new DateTime(2026, 11, 2)) == 380);
    Check("Morning and afternoon exceptions replace recurring periods", TechnicianPlanningService.CapacityForDate(calendar, new DateTime(2026, 10, 26)) == 90 && TechnicianPlanningService.CapacityForDate(calendar, new DateTime(2026, 10, 26), "morning") == 0);
    Check("A missing recurrent weekday is unavailable", TechnicianPlanningService.IsUnavailable(calendar, new DateTime(2026, 10, 28)));
    Check("Declared dated capacity works outside a recurring weekday", TechnicianPlanningService.CapacityForDate(calendar, new DateTime(2026, 10, 27)) == 120);
    Check("A lone half-day exception never invents the other half-day", TechnicianPlanningService.CapacityForDate(new() { CapacityDays = TechnicianCalendar.ParseDays("2026-10-26;120;") }, new DateTime(2026, 10, 26)) == null);
    Check("Daily fallback is not divided into invented half-days", TechnicianPlanningService.CapacityForDate(new() { DailyCapacityMinutes = 420 }, DateTime.Today, "morning") == null);
    var crossesNoon = new TechnicianPreference { WorkPeriods = TechnicianCalendar.ParsePeriods("1;10:00;14:00;120") };
    Check("Capacity across noon is prorated without double counting", TechnicianPlanningService.CapacityForDate(crossesNoon, new DateTime(2026, 11, 2), "morning") == 60 && TechnicianPlanningService.CapacityForDate(crossesNoon, new DateTime(2026, 11, 2)) == 120);
    var overlap = calendar.Copy(); overlap.WorkPeriods.Add(new() { Day = DayOfWeek.Monday, StartMinute = 600, EndMinute = 780, CapacityMinutes = 60 });
    Reject("Overlapping recurrent periods are rejected", () => TechnicianCalendar.Validate(overlap));
    var badRange = calendar.Copy(); badRange.CategoryEstimates[0].MinimumMinutes = 100;
    Reject("Category bounds must enclose the central estimate", () => TechnicianCalendar.Validate(badRange));
    var calPreferences = new TechnicianPreferences().With("Alice", calendar); calPreferences.Save(prefPath);
    Check("Recurring calendar, exceptions and ranges survive persistence", TechnicianPreferences.Load(prefPath).Get("Alice").WorkPeriods.Count == 2 && TechnicianPreferences.Load(prefPath).Get("Alice").CategoryEstimates[0].MaximumMinutes == 80);
    var copiedCalendar = calendar.Copy(); copiedCalendar.WorkPeriods[0].CapacityMinutes = 1;
    Check("Calendar copies do not share mutable periods", calendar.WorkPeriods[0].CapacityMinutes == 180);
    Check("Category model explicitly retains its uncertainty range", TechnicianPlanningService.Estimate(calendar, "Réseau") is { Minutes: 40, MinimumMinutes: 20, MaximumMinutes: 80 });
    Check("Unknown category retains unknown uncertainty", TechnicianPlanningService.Estimate(calendar, "Autre") is { Minutes: 30, MinimumMinutes: null, MaximumMinutes: null });
    var categoryLoad = TechnicianPlanningService.EstimateLoads(workload, calPreferences, new[] { planned }, new());
    Check("Estimated load uses identified ticket category models", categoryLoad.Single(item => item.Technician == "Alice") is { BeforeMinutes: 40, BeforeMinimumMinutes: 20, BeforeMaximumMinutes: 80 });
    calendar.Skills = new() { "Réseau" }; calendar.PrimaryTerritories = new() { "Marseille" };
    var matrix = TechnicianPlanningService.CoverageMatrix(workload, new TechnicianPreferences().With("Alice", calendar), new[] { "Marseille" }, new[] { "Réseau" }, new DateTime(2026, 11, 2), 2);
    Check("Coverage matrix spans dates, skills and half-days", matrix.Count == 4 && matrix[0].Technicians == "Alice" && matrix[0].CapacityMinutes == 180 && matrix[2].CapacityMinutes == 0);

    var observed = Details(); observed.ReadAt = new DateTimeOffset(new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Local));
    observed.ObservedItems = new()
    {
        new() { Kind = "task", Identity = "101", Title = "Contrôle", Status = "À faire", Due = "21/10/2026 09:00", Owner = "Alice" },
        new() { Kind = "task", Identity = "102", Title = "Terminée", Status = "Fait", Due = "21/10/2026" },
        new() { Kind = "task", Identity = "103", Title = "Sans état", Due = "21/10/2026" },
        new() { Kind = "task", Identity = "104", Title = "Sans échéance", Status = "À faire", Date = "21/10/2026" },
        new() { Kind = "task", Identity = "105", Title = "Prochaine", Status = "À faire", PlannedStart = "23/10/2026 10:00", PlannedEnd = "23/10/2026 11:00" },
        new() { Kind = "validation", Identity = "201", Title = "Accord", Owner = "Responsable A", Status = "En attente", RequestedAt = "20/10/2026 10:00" },
        new() { Kind = "validation", Identity = "202", Title = "Accord obtenu", Status = "Acceptée", RequestedAt = "20/10/2026" },
        new() { Kind = "waiting", Identity = "301", WaitingReason = "Pièce commandée", WaitingStart = "21/10/2026 10:00", WaitingEnd = "21/10/2026 12:00" },
        new() { Kind = "waiting", Identity = "302", WaitingReason = "Demandeur absent", WaitingStart = "22/10/2026 09:00", Status = "En attente" },
        new() { Kind = "waiting", Identity = "303", WaitingReason = "Motif seul" },
        new() { Kind = "followup", Identity = "401", Title = "Question", Date = "21/10/2026 08:00", Author = "Demandeur explicite", AuthorRole = "requester" },
        new() { Kind = "followup", Identity = "402", Title = "Dernier message", Date = "22/10/2026 09:00", Author = "Alice", AuthorRole = "" }
    };
    var insights = TicketInsightService.Analyze(observed, observed.ReadAt.AddDays(2));
    Check("Only overdue and explicitly incomplete tasks are counted", insights.OverdueTasks == 1 && insights.Tasks.Single(item => item.Title == "Sans état").Overdue == null && insights.Tasks.Single(item => item.Title == "Sans échéance").Overdue == null);
    Check("Task overdue counters are anchored to the observation time", insights.Tasks.Single(item => item.Title == "Prochaine").Overdue == false && insights.Coverage.Contains("Actualiser"));
    Check("Validation queue uses explicit state, owner and request date", insights.PendingValidations == 1 && insights.Validations[0].Responsible == "Responsable A" && insights.Validations[0].AgeDays == 2);
    Check("Waiting duration uses displayed intervals, not ticket age", insights.Waits[0].Hours == 2 && insights.Waits[1].Hours == 1 && insights.Waits[2].Hours == null);
    Check("Latest exchange never infers a role from an author name", insights.LastExchange is { Author: "Alice", Role: "Non déterminée" });
    observed.ObservedItems.Last().AuthorRole = "technician";
    Check("Explicit exchange origin describes only an unobserved reply", TicketInsightService.Analyze(observed, observed.ReadAt).LastExchange!.WaitingFor.Contains("non observée"));
    Check("Summary points to the next explicitly scheduled task", TicketInsightService.Summary(observed).Single(row => row[0] == "Prochaine tâche planifiée lue")[1].Contains("23/10/2026"));
    observed.Sections.Add(new() { Name = "Tâches", Kind = "table", Data = new() { Headers = new() { "ID", "Description", "Statut", "Échéance" }, Rows = new() { new[] { "106", "Tâche tabulaire", "À faire", "20/10/2026" } } } });
    Check("Explicit task table headers also produce reliable deadlines", TicketInsightService.Analyze(observed, observed.ReadAt).OverdueTasks == 2);
    observed.ObservedItems.Add(observed.ObservedItems[0]);
    Check("Duplicated visible observation with the same identity is counted once", TicketInsightService.Analyze(observed, observed.ReadAt).OverdueTasks == 2);
    WorkplaceStore.SaveOffline(cache, observed);
    Check("Structured observations retain dates and roles offline", WorkplaceStore.LoadOffline(cache, ticket)!.ObservedItems.Any(item => item.AuthorRole == "technician"));
    var malformedObserved = Details(); malformedObserved.ObservedItems.Add(new() { Kind = null! });
    Reject("Null structured fields are rejected before offline use", () => WorkplaceStore.SaveOffline(cache, malformedObserved));
    var readingRecord = new PersonalTicket(); var baselineDetail = Details(); WorkplaceStore.ObserveRead(readingRecord, baselineDetail);
    baselineDetail.Sections[1].Data.Rows[0][3] = "Nouvelle action précise"; baselineDetail.ReadAt = baselineDetail.ReadAt.AddMinutes(1);
    var rowChanges = WorkplaceStore.ObserveRead(readingRecord, baselineDetail);
    Check("Row differences highlight modified content within a section", rowChanges.NewOrChangedRows.Values.Sum(values => values.Count) == 1);
    Check("Repeated identical read does not mark rows new", WorkplaceStore.ObserveRead(readingRecord, baselineDetail).NewOrChangedRows.Values.All(values => values.Count == 0));
    var persisted = document.Copy(); persisted.SectionOrder = new() { "overview", "treatment" }; persisted.DaySearch = "Marseille"; persisted.DayScope = 2; persisted.DaySelectedKey = record.Key;
    persisted.Tickets[0].ReadingPosition = new() { SectionKey = "treatment", Search = "Contrôle", Type = "Suivi", Author = "Alice", ScrollOffset = 456.5, RowIndex = 7, PageIndex = 2 };
    persisted.Tickets[0].FollowupDraft = "Constat\nRéseau vérifié\nSuite attendue\nConfirmation utilisateur";
    persisted.Tickets[0].LastActionReceipt = "Aperçu confirmé ; résultat indéterminé, vérifier GLPI.";
    WorkplaceStore.Save(path, persisted); var positionRead = WorkplaceStore.Load(path);
    Check("Exact reading position and filters survive reopening", positionRead.Tickets[0].ReadingPosition is { Search: "Contrôle", ScrollOffset: 456.5, RowIndex: 7, Author: "Alice", PageIndex: 2 });
    Check("Section order and day filters persist together", positionRead.SectionOrder.SequenceEqual(persisted.SectionOrder) && positionRead.DayScope == 2 && positionRead.DaySearch == "Marseille");
    Check("Followup drafts and ambiguous-action receipts persist", positionRead.Tickets[0].FollowupDraft.Contains("Réseau vérifié") && positionRead.Tickets[0].LastActionReceipt.Contains("indéterminé"));
    persisted.Tickets[0].ReadingPosition.ScrollOffset = double.NaN; Reject("Nonfinite reading offsets are rejected", () => WorkplaceStore.Validate(persisted));

    var observationBefore = new PersonalDayObservation(record.Key, "42", "Réseau", "Nouveau", "23/10/2026", false, 3);
    var observationAfter = observationBefore with { Status = "En cours", Due = "21/10/2026", Mine = true, Priority = 5 };
    var daily = TicketDailyBriefing.Build(new[] { observationBefore }, "same", observed.ReadAt.AddDays(-1), new[] { observationAfter }, "same", observed.ReadAt.AddDays(-1), observed.ReadAt, observed.ReadAt, true);
    Check("Daily briefing separates status, assignment, priority and deadlines", daily.Lines.Select(line => line.Kind).ToHashSet().IsSupersetOf(new[] { "État modifié", "Affectation", "Priorité", "Échéance modifiée", "Échéance" }));
    var wrongScope = TicketDailyBriefing.Build(new[] { observationBefore }, "other", observed.ReadAt.AddDays(-1), new[] { observationAfter }, "same", observed.ReadAt, observed.ReadAt, observed.ReadAt, true);
    Check("Different briefing scopes never invent session changes", wrongScope.Lines.All(line => line.Kind is not ("État modifié" or "Affectation" or "Apparition")));
    Check("Daily briefing keeps absent tickets separate from closure", TicketDailyBriefing.Build(new[] { observationBefore }, "same", observed.ReadAt.AddDays(-1), Array.Empty<PersonalDayObservation>(), "same", observed.ReadAt, observed.ReadAt, observed.ReadAt, false).Lines.Count == 0);

    var validDocument = new TicketObservedItem { Kind = "document", Identity = "7", Url = "https://one.example/front/document.send.php?docid=7&tickets_id=42" };
    Check("Document links preserve the ticket installation", TicketLinkPolicy.Validate(ticket, validDocument).AbsolutePath.EndsWith("/front/document.send.php"));
    void RejectLink(string name, TicketObservedItem item) { try { TicketLinkPolicy.Validate(ticket, item); } catch (InvalidDataException) { Check(name, true); return; } throw new Exception(name); }
    RejectLink("Cross-portal document link refused", new() { Kind = "document", Identity = "7", Url = "https://two.example/front/document.send.php?docid=7" });
    RejectLink("Cross-installation relation refused", new() { Kind = "relation", Identity = "7", RelatedType = "problem", Url = "https://one.example/other/front/problem.form.php?id=7" });
    RejectLink("Business parameters in links are refused", new() { Kind = "relation", Identity = "7", RelatedType = "problem", Url = "https://one.example/front/problem.form.php?id=7&delete=1" });
    RejectLink("Document associated with another ticket is refused", new() { Kind = "document", Identity = "7", Url = "https://one.example/front/document.send.php?docid=7&tickets_id=43" });
    RejectLink("Repeated document identity parameters are refused", new() { Kind = "document", Identity = "7", Url = "https://one.example/front/document.send.php?docid=7&docid=8" });

    var recurrenceData = new ExtractionResult { SourceUrl = relatedSource.SourceUrl, ExtractedAt = observed.ReadAt, Complete = true, Headers = new() { "ID", "Titre", "Catégorie", "Équipement", "Date d’ouverture" }, Rows = new() { new[] { "42", "Panne port réseau", "Réseau", "PC-42", "21/10/2026" }, new[] { "43", "Autre signal réseau", "Réseau", "PC-42", "22/10/2026" }, new[] { "44", "Panne port réseau", "Autre catégorie", "PC-99", "22/10/2026" } } };
    var recurrenceOlder = new ExtractionResult { SourceUrl = recurrenceData.SourceUrl, ExtractedAt = recurrenceData.ExtractedAt.AddDays(-2), Headers = recurrenceData.Headers, Rows = recurrenceData.Rows.Take(1).ToList() };
    var recurrences = TicketRecurrenceService.Analyze(ticket, new[] { new TicketRecurrenceSource(recurrenceOlder, new(), "Ancien"), new TicketRecurrenceSource(recurrenceData, new(), "Actuel") });
    Check("Longitudinal recurrence counts distinct incidents, not snapshots", recurrences.DistinctTickets == 2 && recurrences.RepeatedObservations == 1);
    Check("Equipment recurrence can match different titles", recurrences.Incidents.Any(item => item.Id == "43" && item.Equipment == "PC-42"));
    Check("Incompatible categories are not silently merged", recurrences.Incidents.All(item => item.Id != "44"));
    Check("Each repeated incident preserves first and last observation", recurrences.Incidents.Single(item => item.Id == "42").Observations == 2 && recurrences.Incidents.Single(item => item.Id == "42").FirstSeen == recurrenceOlder.ExtractedAt);
    Check("Different installation histories do not mix", TicketRecurrenceService.Analyze(other, new[] { new TicketRecurrenceSource(recurrenceData, new(), "Autre portail") }).DistinctTickets == 0);
    var interpretedRecurrence = new ExtractionResult { SourceUrl = recurrenceData.SourceUrl, ExtractedAt = recurrenceData.ExtractedAt, Headers = recurrenceData.Headers, Rows = recurrenceData.Rows.Select(row => row.ToArray()).ToList() };
    interpretedRecurrence.Rows[0][2] = "NET"; interpretedRecurrence.Rows[0][3] = "Terminal"; interpretedRecurrence.Rows[0][4] = "CREATED";
    var recurrenceRules = new DashboardSettings { DateCulture = "en-US", SourceTimeZone = "UTC" };
    recurrenceRules.ValueDictionaries["category"] = new() { ["NET"] = "Réseau" }; recurrenceRules.ValueDictionaries["Équipement"] = new() { ["Terminal"] = "PC-42" };
    recurrenceRules.ValueDictionaries["created"] = new() { ["CREATED"] = "10/12/2026 09:15" };
    var interpretedReport = TicketRecurrenceService.Analyze(ticket, new[] { new TicketRecurrenceSource(interpretedRecurrence, recurrenceRules, "Formats déclarés") });
    Check("Recurrence applies the recorded category and equipment dictionaries", interpretedReport.DistinctTickets == 2 && interpretedReport.Incidents.Single(item => item.Id == "42").Equipment == "PC-42");
    Check("Recurrence preserves source date culture and timezone", interpretedReport.Incidents.Single(item => item.Id == "42").CreatedAt == new DateTimeOffset(2026, 10, 12, 9, 15, 0, TimeSpan.Zero).LocalDateTime);
    var plannedReads = TicketEnrichmentService.Plan(new[] { ticket, other, ticket }, 1, WorkplaceStore.TicketKey(other));
    Check("Enrichment deduplicates and applies priority before global limit", plannedReads.Single() == other);
    Reject("Enrichment global limit cannot be exceeded", () => TicketEnrichmentService.Plan(new[] { ticket }, 101));
    var readCount = 0; using var stopQueue = new CancellationTokenSource();
    var queueOutcomes = await TicketEnrichmentService.ExecuteAsync(new[] { ticket, other }, (reference, token) => { readCount++; return Task.FromResult(Details()); }, accepted: _ => stopQueue.Cancel(), ct: stopQueue.Token);
    Check("Cancelling enrichment leaves unread items unknown", readCount == 1 && queueOutcomes[1] is { State: "Non lu", ReadAt: null, ObservedStatus: "Inconnu" });
    var statuses = Details(); statuses.Sections[0].Data.Rows.Add(new[] { "Statut", "Clos" });
    Check("Enrichment reports only explicitly read status", TicketEnrichmentService.ObservedStatus(statuses) == "Clos");
    statuses.Sections[0].Data.Rows.Add(new[] { "État", "En attente" });
    Check("Conflicting read status stays explicitly ambiguous", TicketEnrichmentService.ObservedStatus(statuses).Contains("contradictoires"));
    var queuePath = Path.Combine(root, "enrichment.json"); TicketEnrichmentService.Save(queuePath, new() { Label = "Absents de la comparaison", Tickets = new() { ticket, other }, Results = queueOutcomes.ToList() });
    Check("Enrichment keeps dated outcomes separate and persistent", TicketEnrichmentService.Load(queuePath).Results[0].ReadAt == Details().ReadAt);
    var adapter = new TicketActionAdapter { Kind = "followup", Installation = "https://one.example/", FormSelector = "#followup-form", FieldName = "content", SubmitName = "add", SubmitValue = "Ajouter", ActionPath = "/front/itilfollowup.form.php", StructureHash = new string('a', 64) };
    Check("Assisted adapter is bound to its installation", TicketActionService.Validate(adapter, ticket).Installation == "https://one.example/");
    Reject("Assisted adapter cannot move to another installation", () => TicketActionService.Validate(adapter, alternate));
    var forbiddenAdapter = adapter.Copy(); forbiddenAdapter.ActionPath = "/front/user.form.php";
    Reject("Assisted adapter cannot target unrelated business endpoint", () => TicketActionService.Validate(forbiddenAdapter, ticket));
    Reject("Assisted assignment must use a positive numeric choice", () => TicketActionService.ValidateValue("assignment", "javascript:alert(1)"));
    var adapterPath = Path.Combine(root, "adapters.json"); TicketActionProfileStore.Save(adapterPath, new[] { adapter });
    Check("Learned native adapter survives reload without executable configuration", TicketActionProfileStore.Load(adapterPath).Single().FormSelector == "#followup-form");
    var draft = TicketInsightService.DraftFollowup(record, observed, "Diagnostic", "Constat déclaré", "Action déclarée", "Prochaine vérification");
    Check("Structured followup drafts retain user facts and source date", draft.Contains("Constat déclaré") && draft.Contains("Prochaine vérification") && draft.Contains(observed.Ticket.Url));

    Console.WriteLine($"{checks} workplace checks passed.");
}
finally { Directory.Delete(root, recursive: true); }
