using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record SharedOperationReview(string Token, string ItemId, long ItemRevision, string ItemHash,
        string Context, string Baseline, DateTimeOffset ExpiresAt);
    private sealed record SharedOperationChange(string Label, string Current, string Incoming);
    private sealed class SharedOperationPlan
    {
        public string Title { get; init; } = "";
        public string DestinationLabel { get; init; } = "";
        public string Summary { get; init; } = "";
        public string FileName { get; init; } = "";
        public string Baseline { get; set; } = "";
        public List<SharedOperationChange> Changes { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool CanApply { get; set; } = true;
        public object? LocalCopy { get; set; }
        public string TicketKey { get; set; } = "";
        public ScheduledTour? Tour { get; set; }
    }
    private SharedOperationReview? _sharedOperationReview;

    private void RegisterSharedOperationalOperations()
    {
        Register("workspace.publishPersonalNote", PublishSharedPersonalNoteAsync);
        Register("workspace.publishTour", PublishSharedTourAsync);
        Register("workspace.publishDispatch", PublishSharedDispatchAsync);
        Register("workspace.publishTechnicians", PublishSharedTechniciansAsync);
        Register("workspace.operation.preview", PreviewSharedOperationAsync);
        Register("workspace.operation.apply", ApplySharedOperationAsync);
    }
    private string SharedInstallation() => TeamWorkspaceBusinessDocuments.Installation(Settings.PortalUrl);
    private string SharedObservedInstallation()
    {
        var installation = SharedInstallation();
        if (TeamWorkspaceBusinessDocuments.Installation(RequireResult().SourceUrl) != installation)
            throw new InvalidOperationException("Le relevé ne correspond pas à l’installation configurée pour ce partage.");
        return installation;
    }
    private string SharedOperationContext() => JsonSerializer.Serialize(new
    {
        Revision, Settings.PortalUrl, Settings.ActiveSourceId, LibrarySettings.DatabasePath,
        tourDraftVersion = _terrainTourDraftVersion, tourDirty = _terrainTourDirty
    }, Json);
    private async Task<object?> PublishSharedPersonalNoteAsync(JsonElement args, CancellationToken ct)
    {
        var personal = WorkplaceStore.Load(DataPath("workplace.json")).Tickets.SingleOrDefault(t => t.Key == Text(args, "ticketKey", 64))
            ?? throw new InvalidOperationException("Enregistrez la note de ce ticket avant de la partager.");
        var reference = TeamWorkspaceBusinessDocuments.PublicReference(new(personal.Id, personal.Url));
        var installation = SharedInstallation();
        if (TeamWorkspaceBusinessDocuments.Installation(reference.Url) != installation)
            throw new InvalidOperationException("Cette note appartient à une autre installation GLPI.");
        var document = new SharedPersonalNoteDocument { Installation = installation, Ticket = reference, Title = personal.Title, Notes = personal.Notes };
        return await SaveSharedOperationAsync("personal-note", "Note du ticket " + reference.Id, personal.Notes, document, TicketLibraryRules.Key(reference), ct).ConfigureAwait(false);
    }
    private async Task<object?> PublishSharedTourAsync(JsonElement args, CancellationToken ct)
    {
        var id = Text(args, "id", 80);
        var tour = ScheduledTourStore.Load(DataPath("scheduled-tours.json")).Tours.SingleOrDefault(t => t.Id == id)
            ?? throw new InvalidOperationException("Enregistrez cette tournée avant de la partager.");
        var installation = SharedInstallation();
        if (tour.SourceKey != installation) throw new InvalidOperationException("Cette tournée appartient à une autre installation GLPI.");
        var document = new SharedTourDocument { Installation = installation, Tour = tour };
        return await SaveSharedOperationAsync("tour", "Tournée · " + ClipSharedTitle(tour.Name),
            $"{tour.Name}\n{tour.Date} · {tour.StartTime}–{tour.EndTime}\n{tour.Tickets.Count} tickets · {tour.Stops.Count} étapes.\nLa réception crée un nouveau brouillon à rapprocher du relevé local.", document, "", ct).ConfigureAwait(false);
    }
    private async Task<object?> PublishSharedDispatchAsync(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); var installation = SharedObservedInstallation(); var scenario = WorkScenario(); var source = RequireResult();
        var references = new Dictionary<string, List<TicketReference>>(StringComparer.Ordinal);
        foreach (var row in source.Rows)
        {
            ct.ThrowIfCancellationRequested(); TicketReference reference;
            try { reference = TeamWorkspaceBusinessDocuments.PublicReference(TicketReferenceResolver.Resolve(source, row, EffectiveSettings)); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { continue; }
            var key = WorkplaceStore.TicketKey(reference);
            if (!references.TryGetValue(key, out var found)) references[key] = found = new();
            found.Add(reference);
        }
        var selected = new List<TicketReference>();
        foreach (var assignment in scenario.Assignments)
        {
            if (!references.TryGetValue(assignment.TicketKey, out var found) || found.Count != 1 || found[0].Id != assignment.TicketId)
                throw new InvalidOperationException("Une proposition n’a pas de fiche unique dans le relevé courant. Actualisez le scénario avant de le partager.");
            selected.Add(found[0]);
        }
        var document = new SharedDispatchDocument { Installation = installation, Scenario = scenario, References = selected };
        return await SaveSharedOperationAsync("dispatch", "Répartition · " + ClipSharedTitle(scenario.Name),
            $"{scenario.Name}\n{scenario.Date:yyyy-MM-dd} · {scenario.Assignments.Count} propositions locales.\nAucune affectation n’est envoyée dans GLPI.", document, "", ct).ConfigureAwait(false);
    }
    private async Task<object?> PublishSharedTechniciansAsync(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); var installation = SharedObservedInstallation(); var preferences = WorkTechnicianPreferences();
        var known = WorkWorkload(ct).Technicians.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var document = new SharedTechniciansDocument { Installation = installation };
        foreach (var pair in preferences.Technicians.Where(pair => known.Contains(pair.Key)))
        {
            var business = SharedTechnicianBusiness.From(pair.Value);
            if (new TechnicianPreferences().With(pair.Key, business.ToPreference()).Technicians.Count > 0)
                document.Technicians.Add(pair.Key, business);
        }
        if (document.Technicians.Count == 0) throw new InvalidOperationException("Aucune compétence ou capacité enregistrée pour les techniciens du relevé courant.");
        return await SaveSharedOperationAsync("technicians", "Compétences et capacités de l’équipe",
            $"{document.Technicians.Count} techniciens présents dans le relevé.\nCompétences, secteurs, capacités, calendriers et estimations déclarés.\nLes favoris, objectifs personnels et notes restent sur leur poste.", document, "", ct).ConfigureAwait(false);
    }
    private async Task<TeamWorkspaceItem> SaveSharedOperationAsync(string kind, string title, string body, TeamBusinessDocument document, string ticketKey, CancellationToken ct)
    {
        var item = new TeamWorkspaceItem
        {
            Id = Guid.NewGuid().ToString("N"), Kind = kind, Title = title, Body = body, TicketKey = ticketKey,
            Status = "published", Author = LibrarySettings.DisplayName, Payload = TeamWorkspaceBusinessDocuments.Payload(document)
        };
        var saved = await TeamWorkspace.SaveAsync(item, null, ct).ConfigureAwait(false);
        Publish("library.changed", new { team = true }); return saved;
    }
    private async Task<object?> PreviewSharedOperationAsync(JsonElement args, CancellationToken ct)
    {
        _sharedOperationReview = null;
        var item = await RequireWorkspaceItemAsync(args, ct).ConfigureAwait(false);
        var document = TeamWorkspaceBusinessDocuments.Read(item);
        var plan = PrepareSharedOperation(item, document); ct.ThrowIfCancellationRequested();
        var token = plan.CanApply ? Guid.NewGuid().ToString("N") : "";
        if (plan.CanApply) _sharedOperationReview = new(token, item.Id, item.Revision, TeamWorkspaceCodec.ContentHash(item),
            SharedOperationContext(), plan.Baseline, DateTimeOffset.UtcNow.AddMinutes(10));
        return new { token, plan.Title, item.Kind, plan.Summary, plan.Changes, plan.Warnings, plan.CanApply, plan.DestinationLabel };
    }
    private SharedOperationPlan PrepareSharedOperation(TeamWorkspaceItem item, TeamBusinessDocument document)
    {
        var (title, destination, summary, file) = item.Kind switch
        {
            "personal-note" => ("Reprendre la note partagée", "Ajouter à ma note", "Le texte partagé sera ajouté à la note personnelle du ticket. Les autres données personnelles sont conservées.", "workplace.json"),
            "tour" => ("Reprendre la tournée partagée", "Créer une tournée brouillon", "Une nouvelle tournée locale sera créée à partir de la version partagée. Son rapprochement avec les tickets et les lieux locaux reste nécessaire.", "scheduled-tours.json"),
            "dispatch" => ("Reprendre la répartition partagée", "Remplacer mon scénario local", "Le scénario local précédent sera sauvegardé avant de reprendre les propositions partagées. Aucune affectation GLPI ne sera exécutée.", "dispatch-scenario.json"),
            "technicians" => ("Reprendre les compétences et capacités", "Appliquer les données métier", "Seules les données métier des techniciens nommés seront reprises. Les favoris, objectifs personnels et notes du poste sont conservés.", "technician-preferences.json"),
            _ => throw new InvalidDataException("Ce type de contribution ne dispose pas d’une reprise métier.")
        };
        var plan = new SharedOperationPlan { Title = title, DestinationLabel = destination, Summary = summary, FileName = file };
        if (document.Installation != SharedInstallation())
        {
            plan.CanApply = false; plan.Warnings.Add("Cette contribution appartient à une autre installation GLPI. Sélectionnez son installation avant de la reprendre."); return plan;
        }
        plan.Baseline = SharedFileFingerprint(DataPath(file));
        switch (document)
        {
            case SharedPersonalNoteDocument note:
                var workplace = WorkplaceStore.Load(DataPath(file)); var key = WorkplaceStore.TicketKey(note.Ticket);
                var personal = workplace.Tickets.SingleOrDefault(t => t.Key == key); var current = personal?.Notes ?? "";
                plan.Changes.Add(new("Ticket", personal is null ? "Non conservé sur ce poste" : "Ticket " + personal.Id, "Ticket " + note.Ticket.Id));
                plan.Changes.Add(new("Note", current, note.Notes));
                var block = "Note d’équipe — " + item.Author + "\n" + note.Notes;
                var merged = current.Length == 0 ? note.Notes : current + "\n\n" + block;
                if (current == note.Notes || current.Contains(block, StringComparison.Ordinal))
                { plan.CanApply = false; plan.Warnings.Add("Ce texte partagé figure déjà dans la note locale."); }
                if (merged.Length > WorkplaceStore.MaximumNotes)
                { plan.CanApply = false; plan.Warnings.Add("L’ajout dépasserait la limite de la note locale. Réorganisez le texte avant de reprendre cette contribution ; aucun texte ne sera tronqué."); }
                if (personal is null && workplace.Tickets.Count >= WorkplaceStore.MaximumTickets)
                { plan.CanApply = false; plan.Warnings.Add("L’espace personnel est plein. Libérez un dossier avant d’ajouter cette note."); }
                if (plan.CanApply)
                {
                    personal ??= WorkplaceStore.Remember(workplace, note.Ticket, note.Title, open: false);
                    personal.Notes = merged; plan.LocalCopy = WorkplaceStore.Validate(workplace); plan.TicketKey = key;
                }
                break;
            case SharedTourDocument shared:
                var store = ScheduledTourStore.Load(DataPath(file));
                plan.Changes.Add(new("Tournées enregistrées", store.Tours.Count.ToString(CultureInfo.InvariantCulture), "Une nouvelle copie"));
                plan.Changes.Add(new("Tournée", "Les tournées existantes sont conservées", shared.Tour.Name));
                plan.Changes.Add(new("Date et horaires", "—", shared.Tour.Date + " · " + shared.Tour.StartTime + "–" + shared.Tour.EndTime + " · " + shared.Tour.TimeZoneId));
                plan.Changes.Add(new("État de la copie", "—", "Brouillon à rapprocher du relevé local"));
                plan.Changes.Add(new("Tickets / étapes", "—", shared.Tour.Tickets.Count + " / " + shared.Tour.Stops.Count));
                plan.Warnings.Add("Les trajets routiers, observations météo et validations précédentes ne sont pas repris. Les identifiants de lieux doivent correspondre à la configuration locale.");
                if (_terrainTourDirty) { plan.CanApply = false; plan.Warnings.Add("Enregistrez ou fermez d’abord la tournée en cours de modification sur ce poste."); }
                if (store.Tours.Count >= ScheduledTourStore.MaximumTours) { plan.CanApply = false; plan.Warnings.Add("Le registre local a atteint sa limite de tournées."); }
                if (plan.CanApply)
                {
                    var copy = ScheduledTourService.Duplicate(shared.Tour, shared.Tour.Name);
                    plan.Tour = copy; plan.LocalCopy = store.With(copy);
                }
                break;
            case SharedDispatchDocument dispatch:
                var oldScenario = TechnicianPlanningService.Load(DataPath(file));
                plan.Changes.Add(new("Scénario", oldScenario.Name, dispatch.Scenario.Name));
                plan.Changes.Add(new("Date", oldScenario.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dispatch.Scenario.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                plan.Changes.Add(new("Propositions", oldScenario.Assignments.Count.ToString(CultureInfo.InvariantCulture), dispatch.Scenario.Assignments.Count.ToString(CultureInfo.InvariantCulture)));
                foreach (var assignment in dispatch.Scenario.Assignments.Take(80))
                {
                    var prior = oldScenario.Assignments.SingleOrDefault(a => a.TicketKey == assignment.TicketKey);
                    plan.Changes.Add(new("Ticket " + assignment.TicketId, prior is null ? "Aucune proposition" : SharedAssignmentText(prior), SharedAssignmentText(assignment)));
                }
                if (dispatch.Scenario.Assignments.Count > 80) plan.Warnings.Add("L’aperçu détaille les 80 premières propositions sur " + dispatch.Scenario.Assignments.Count + ". La reprise conserve le scénario partagé entier et sauvegarde le scénario local précédent.");
                plan.Warnings.Add("Vérifiez les tickets, compétences et disponibilités dans le relevé local avant d’utiliser ce scénario. Les propositions ne modifient pas GLPI.");
                plan.LocalCopy = dispatch.Scenario;
                break;
            case SharedTechniciansDocument technicians:
                var preferences = TechnicianPreferences.Load(DataPath(file));
                foreach (var pair in technicians.Technicians.Take(80))
                    plan.Changes.Add(new(pair.Key, SharedBusinessText(SharedTechnicianBusiness.From(preferences.Get(pair.Key))), SharedBusinessText(pair.Value)));
                if (technicians.Technicians.Count > 80) plan.Warnings.Add("L’aperçu détaille les 80 premières identités sur " + technicians.Technicians.Count + ". Les autres identités du poste sont conservées.");
                plan.Warnings.Add("Les identités sont celles du relevé partagé : vérifiez les homonymes et les disponibilités déclarées. Les notes personnelles ne sont ni publiées ni remplacées.");
                plan.LocalCopy = TeamWorkspaceBusinessDocuments.Merge(preferences, technicians);
                break;
        }
        if (plan.Baseline != SharedFileFingerprint(DataPath(file))) throw new InvalidOperationException("Le document local a changé pendant l’aperçu. Relancez la comparaison.");
        return plan;
    }
    private async Task<object?> ApplySharedOperationAsync(JsonElement args, CancellationToken ct)
    {
        var token = Text(args, "token", 64); var review = _sharedOperationReview; _sharedOperationReview = null;
        if (review is null || token != review.Token || review.ExpiresAt <= DateTimeOffset.UtcNow || review.Context != SharedOperationContext())
            throw new InvalidOperationException("Cet aperçu a expiré ou le contexte a changé. Examinez à nouveau la contribution.");
        var item = await TeamWorkspace.GetAsync(review.ItemId, ct).ConfigureAwait(false);
        if (item is null || item.Deleted || item.Revision != review.ItemRevision || TeamWorkspaceCodec.ContentHash(item) != review.ItemHash)
            throw new InvalidOperationException("La contribution partagée a changé. Examinez sa nouvelle version avant de l’appliquer.");
        var document = TeamWorkspaceBusinessDocuments.Read(item); var plan = PrepareSharedOperation(item, document);
        if (!plan.CanApply || plan.LocalCopy is null || plan.Baseline != review.Baseline)
            throw new InvalidOperationException("Le document local a changé ou ne peut plus recevoir cette contribution. Relancez la comparaison.");
        ct.ThrowIfCancellationRequested();
        var backupName = ""; string route; object parameters;
        switch (plan.LocalCopy)
        {
            case WorkplaceDocument workplace:
                WorkSave(workplace); route = "tickets"; parameters = new { ticketKey = plan.TicketKey }; break;
            case ScheduledTourStore tours:
                tours.Save(DataPath(plan.FileName)); route = "tours"; parameters = new { tourId = plan.Tour!.Id };
                Publish("terrain.changed", new { revision = Revision }); break;
            case DispatchScenario scenario:
                backupName = BackupSharedOperation(plan.FileName, "dispatch", plan.Baseline);
                TechnicianPlanningService.Save(DataPath(plan.FileName), scenario); route = "planning"; parameters = new { };
                Publish("workplace.changed", new { revision = Revision }); break;
            case TechnicianPreferences preferences:
                backupName = BackupSharedOperation(plan.FileName, "technicians", plan.Baseline);
                preferences.Save(DataPath(plan.FileName)); route = "technicians"; parameters = new { };
                Publish("technicians.changed", new { shared = true }); break;
            default: throw new InvalidDataException("La copie locale de cette contribution est invalide.");
        }
        return new { message = "Le travail partagé est disponible sur ce poste.", route, @params = parameters, backupName };
    }
    private static string SharedFileFingerprint(string path)
    {
        if (!File.Exists(path)) return "missing";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Le document local dépasse la limite de comparaison.");
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static string BackupSharedOperation(string fileName, string kind, string expected)
    {
        var path = DataPath(fileName); if (!File.Exists(path)) return "";
        var folder = DataPath("SharedImports"); Directory.CreateDirectory(folder);
        var name = kind + "-" + Guid.NewGuid().ToString("N") + ".json"; var backup = Path.Combine(folder, name);
        File.Copy(path, backup, overwrite: false);
        if (SharedFileFingerprint(backup) != expected || SharedFileFingerprint(path) != expected)
        {
            File.Delete(backup); throw new InvalidOperationException("Le document local a changé avant sa sauvegarde. Relancez la comparaison.");
        }
        return name;
    }
    private static string ClipSharedTitle(string text) => text.Length <= 260 ? text : text[..260];
    private static string SharedAssignmentText(DispatchAssignment assignment) => assignment.TechnicianKey + " · " + assignment.EstimatedMinutes + " min" + (assignment.Note.Length > 0 ? "\n" + assignment.Note : "");
    private static string SharedBusinessText(SharedTechnicianBusiness value) =>
        "Compétences : " + string.Join(", ", value.Skills) + "\nSecteurs : " + string.Join(", ", value.PrimaryTerritories) + "\nRemplacement : " + string.Join(", ", value.BackupTerritories)
        + "\nCapacité/jour : " + (value.DailyCapacityMinutes?.ToString(CultureInfo.InvariantCulture) ?? "Non renseignée") + " min"
        + "\nEstimation/ticket : " + (value.EstimatedTicketMinutes?.ToString(CultureInfo.InvariantCulture) ?? "Non renseignée") + " min"
        + "\nIndisponibilité : " + (value.UnavailableFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—") + " → " + (value.UnavailableUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—")
        + "\nPrésence (jour 1=lundi à 7=dimanche ; début ; fin ; capacité en minutes) :\n" + (value.WorkPeriods.Count == 0 ? "Non renseignée" : TechnicianCalendar.FormatPeriods(value.WorkPeriods))
        + "\nExceptions (date ; capacité matin ; capacité après-midi en minutes) :\n" + (value.CapacityDays.Count == 0 ? "Aucune" : TechnicianCalendar.FormatDays(value.CapacityDays))
        + "\nDurées par catégorie (catégorie ; minimum ; estimation ; maximum en minutes) :\n" + (value.CategoryEstimates.Count == 0 ? "Non renseignées" : TechnicianCalendar.FormatEstimates(value.CategoryEstimates));
}
