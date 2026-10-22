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
    Check("Event date is not inferred task deadline", !summary.Any(row => row[0].Contains("prochaine tâche", StringComparison.OrdinalIgnoreCase)));
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
    Console.WriteLine($"{checks} workplace checks passed.");
}
finally { Directory.Delete(root, recursive: true); }
