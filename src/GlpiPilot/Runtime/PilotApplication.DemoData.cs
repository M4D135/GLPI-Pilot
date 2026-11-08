using System.Globalization;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private static readonly string[] DemoTechnicians = ["Alex Exemple", "Camille Exemple", "Robin Exemple", "Nora Exemple", "Sacha Exemple", "Lou Exemple"];
    private static readonly (string Title, string Category, string Description, string Task, string Solution)[] DemoCases =
    [
        ("Imprimante en attente", "Impression", "Les documents restent dans la file de l'imprimante fictive IMPR-DEMO. Le poste voisin imprime normalement.", "Contrôler la file et réaliser une page de test.", "File de démonstration vidée et pilote fictif réinstallé ; page de contrôle validée."),
        ("Connexion réseau intermittente", "Réseau > Connexion", "Le poste de formation perd sa connexion quelques minutes après l'ouverture de session. Aucun incident réel n'est représenté.", "Comparer câble et prise sur le banc de formation.", "Câble de démonstration remplacé ; trois essais de connexion sont concluants."),
        ("Préparer un poste d'accueil", "Poste de travail > Installation", "Un nouveau poste fictif doit être préparé avec les applications de formation et les raccourcis du site.", "Installer le profil de formation et vérifier les applications.", "Poste fictif préparé, contrôlé et remis au demandeur de démonstration."),
        ("Application de formation indisponible", "Poste de travail > Logiciel", "L'application fictive Atelier ne s'ouvre plus. Le message d'exercice indique une configuration utilisateur absente.", "Recréer la configuration sur la copie de formation.", "Configuration fictive régénérée ; ouverture et sauvegarde d'un document d'exercice vérifiées."),
        ("Écran externe non détecté", "Poste de travail > Matériel", "Le second écran du bureau fictif reste noir après raccordement. L'écran intégré est opérationnel.", "Vérifier alimentation, raccordement et profil d'affichage.", "Entrée vidéo de démonstration corrigée et double affichage confirmé."),
        ("Accès au dossier de formation", "Accès > Dossier partagé", "Le demandeur fictif ne peut pas ouvrir le dossier d'exercices. L'accès attendu concerne uniquement l'environnement de formation.", "Vérifier le groupe fictif et refaire le test d'accès.", "Groupe de formation corrigé ; lecture du document d'exercice confirmée.")
    ];

    private static ExtractionResult CreateDemo()
    {
        var data = new ExtractionResult
        {
            Headers = ["ID", "Titre", "Statut", "Dernière modification", "Date d'ouverture", "Priorité", "Demandeur - Demandeur", "Attribué à - Technicien", "Catégorie",
                "Suivis - Rédacteur", "Tâches - Rédacteur", "TTR + Progression", "Statistiques - Délai de prise en compte", "Type", "Attribué à - Groupe de techniciens", "Âge (jours)",
                "Nombre de groupes", "Nombre de rédacteurs", "Description", "Lieu", "Date de résolution", "Échéance", "Solution"],
            Title = "Démonstration · 60 tickets fictifs", Scope = "Données entièrement fictives de démonstration", Complete = false,
            Provenance = new() { SourceKind = "demo", Method = "synthetic" }
        };
        string Stamp(DateTime date) => date.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        for (var i = 1; i <= 60; i++)
        {
            var example = DemoCases[(i - 1) % DemoCases.Length];
            var status = new[] { "Nouveau", "En cours (attribué)", "En cours (planifié)", "En attente", "Résolu", "Clos" }[i % 6];
            var age = i % 30 + 2; var site = "Site exemple " + (char)('A' + i % 4);
            var technician = i % 8 == 0 ? "" : DemoTechnicians[(i - 1) % DemoTechnicians.Length];
            var updated = DateTime.Today.AddDays(-(i % 2)).AddHours(15);
            var groups = i % 7 == 0 ? "Groupe Support exemple\nGroupe Expertise exemple" : i % 4 == 0 ? "" : i % 2 == 0 ? "Groupe Support exemple" : "Groupe Expertise exemple";
            data.Rows.Add([
                i.ToString(CultureInfo.InvariantCulture), $"{site} · {example.Title} · cas fictif {i}", status, Stamp(updated), Stamp(DateTime.Today.AddDays(-age).AddHours(9)),
                new[] { "Basse", "Moyenne", "Haute", "Très haute" }[i % 4], "Demandeur fictif " + (i % 10 + 1), technician, example.Category,
                DemoTechnicians[(i - 1) % DemoTechnicians.Length], technician, (i % 101) + " %", (i % 120) + " min", i % 3 == 0 ? "Demande" : "Incident",
                groups, age.ToString(CultureInfo.InvariantCulture), (groups.Length == 0 ? 0 : groups.Contains('\n') ? 2 : 1).ToString(CultureInfo.InvariantCulture), "2",
                example.Description, site, status is "Résolu" or "Clos" ? Stamp(updated) : "", Stamp(DateTime.Today.AddDays(i % 9 - 4).AddHours(17)),
                status is "Résolu" or "Clos" ? example.Solution : ""
            ]);
        }
        data.Status = "Démonstration · 60 tickets complets · 6 techniciens fictifs";
        data.Warnings.Add("Démonstration : aucune personne, organisation ou donnée de ticket réelle.");
        return data;
    }

    private async Task SeedCompleteDemoAsync(CancellationToken ct, Action<string> progress, IReadOnlyList<DashboardColumn> columns)
    {
        // This method is called only inside the disposable demo's storage scope.
        // File imports never enter it and never receive invented ticket content.
        if (!IsSimulationInstance || _simulationLiveData?.Provenance.SourceKind != "demo") throw new InvalidOperationException("Base de démonstration attendue.");
        var contents = new List<(TicketLibrarySeed, TicketDetailResult, TicketLibraryCoverage)>();
        for (var index = 0; index < _simulationLiveData.Rows.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var example = DemoCases[index % DemoCases.Length]; var reference = SimulationReference(index);
            var author = DemoTechnicians[index % DemoTechnicians.Length]; var created = SimulationCell(index, "created", columns);
            _simulationTimeline[WorkplaceStore.TicketKey(reference)] =
            [
                ["Suivi", created, "Demandeur fictif " + ((index + 1) % 10 + 1), example.Description],
                ["Suivi", created, author, "Diagnostic d'exercice : symptôme reproduit sur le matériel fictif ; vérifications préparées."],
                ["Tâche", SimulationCell(index, "updated", columns), author, example.Task + " Durée fictive : 30 minutes."]
            ];
            var detail = SimulationDetail(reference, columns);
            contents.Add((SimulationSeed(index, columns), detail, SimulationCoverage(detail)));
        }
        progress("Contenus fictifs préparés");
        await TicketLibrary.SeedSimulationDetailsAsync(contents, SimulationReaderSignature, ct).ConfigureAwait(false);
        progress("Fiches fictives enregistrées");
        var people = TechnicianDashboardService.Detect(RequireResult(), Settings.Dashboard, ct).Technicians.ToArray();
        foreach (var person in people)
        {
            var resource = new CalendarResource { Key = person.Key, Label = person.Name, TimeZoneId = "Europe/Paris" };
            foreach (var day in Enumerable.Range(1, 5))
            {
                resource.WorkPeriods.Add(new() { Day = day, StartMinute = 540, EndMinute = 720, CapacityMinutes = 180 });
                resource.WorkPeriods.Add(new() { Day = day, StartMinute = 780, EndMinute = 1020, CapacityMinutes = 240 });
            }
            InterventionCalendarService.ValidateResource(resource);
            await TeamWorkspace.SaveAsync(CalendarWorkspaceDocuments.ForResource(resource, "Démonstration"), null, ct).ConfigureAwait(false);
        }
        var firstDay = DateTime.Today.AddDays(1);
        while (firstDay.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) firstDay = firstDay.AddDays(1);
        var catalog = CalendarCatalog(ct);
        for (var index = 0; index < Math.Min(6, people.Length); index++)
        {
            var ticket = catalog[index]; var start = firstDay.AddDays(index < 2 ? -7 : 0).AddHours(9 + index % 3 * 2);
            var visit = new CalendarIntervention
            {
                Title = "Démonstration · " + DemoCases[index].Title, StartLocal = start.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                EndLocal = start.AddHours(1).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), TimeZoneId = "Europe/Paris",
                ParticipantKeys = [people[index].Key], Tickets = [ticket.Reference()], SiteId = ticket.SiteId, Location = ticket.Location,
                Notes = "Intervention entièrement fictive. Essayez le déplacement, la modification et l'annulation.",
                Contact = "Contact fictif du site", Equipment = "Matériel de formation", TravelBeforeMinutes = 15,
                State = index < 2 ? "done" : "planned", ReminderMinutes = index < 2 ? null : 30,
                Recurrence = index == 5 ? new CalendarRecurrence { Frequency = "weekly", Count = 4 } : null
            };
            InterventionCalendarService.Validate(visit);
            await TeamWorkspace.SaveAsync(CalendarWorkspaceDocuments.ForIntervention(visit, "Démonstration"), null, ct).ConfigureAwait(false);
        }
        var absence = new CalendarIntervention
        {
            Title = "Démonstration · formation de " + people[1].Name, Kind = "unavailability", ParticipantKeys = [people[1].Key],
            StartLocal = firstDay.AddDays(1).AddHours(9).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            EndLocal = firstDay.AddDays(1).AddHours(12).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            TimeZoneId = "Europe/Paris", Notes = "Indisponibilité fictive pour essayer le contrôle des conflits."
        };
        InterventionCalendarService.Validate(absence);
        await TeamWorkspace.SaveAsync(CalendarWorkspaceDocuments.ForIntervention(absence, "Démonstration"), null, ct).ConfigureAwait(false);
        progress("Planning fictif enregistré");

        var context = TerrainContext(ct); var tours = new ScheduledTourStore();
        for (var index = 0; index < 4; index++)
        {
            var date = DateOnly.FromDateTime(firstDay.AddDays(index < 2 ? -7 + index : index - 2));
            var tour = ScheduledTourService.Create(context, Enumerable.Range(12 + index * 4, 3),
                "Démonstration · " + (index < 2 ? "tournée effectuée " : "tournée à venir ") + (index + 1), date, people[(index + 2) % people.Length].Key, ct);
            tour.Notes = "Tournée entièrement fictive. Les temps de trajet sont des estimations de démonstration.";
            tour.TimeZoneId = "Europe/Paris";
            foreach (var stop in tour.Stops) { stop.DurationMinutes = 30; stop.TravelMinutes = 15; stop.Notes = "Vérifier le matériel fictif et consigner le résultat."; }
            if (index < 3) tour = ScheduledTourService.Transition(tour, context, "ready", "Tournée fictive préparée");
            if (index < 2) tour = ScheduledTourService.Transition(tour, context, "done", "Visites fictives effectuées, compte rendu de démonstration enregistré");
            tours = tours.With(tour);
        }
        tours.Save(DataPath("scheduled-tours.json"));
        var personal = WorkDocument().Copy();
        for (var index = 0; index < 8; index++)
        {
            var detail = SimulationDetail(SimulationReference(index));
            var ticket = WorkplaceStore.Remember(personal, detail.Ticket, detail.Title, false);
            ticket.Notes = "Note personnelle fictive : préparer le matériel de formation et confirmer le créneau avec le contact du site.";
            ticket.Pinned = index < 3; ticket.QueueOrder = index < 5 ? index : null;
            ticket.ReminderAt = index < 2 ? DateTimeOffset.Now.AddHours(index + 1) : null;
            ticket.Checklist = [new() { Text = "Vérifier le contexte fictif", Done = true }, new() { Text = "Préparer le matériel" }, new() { Text = "Consigner le résultat" }];
            ticket.LastReadAt = detail.ReadAt;
        }
        WorkSave(personal);
        progress("Tournées et notes fictives enregistrées");
    }
}
