using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

/// <summary>Statistics of the supplied snapshot only. No network, guessed history or implicit deduplication.</summary>
public static class DashboardService
{
    public static readonly IReadOnlyList<DashboardRole> Roles = new[]
    {
        new DashboardRole("id", "Identifiant du ticket", new[] { "ID", "Identifiant", "Ticket ID", "ID du ticket", "Numéro du ticket" }),
        new DashboardRole("title", "Titre", new[] { "Titre", "Title", "Objet", "Sujet", "Ticket title" }),
        new DashboardRole("status", "Statut", new[] { "Statut", "Status", "État", "État du ticket" }),
        new DashboardRole("priority", "Priorité", new[] { "Priorité", "Priority" }),
        new DashboardRole("urgency", "Urgence", new[] { "Urgence", "Urgency", "Niveau d'urgence" }),
        new DashboardRole("impact", "Impact", new[] { "Impact", "Niveau d'impact" }),
        new DashboardRole("type", "Type", new[] { "Type", "Type de ticket", "Ticket type" }),
        new DashboardRole("created", "Date d’ouverture", new[] { "Date d'ouverture", "Date d’ouverture", "Date d'ouverture du ticket", "Date de création", "Opening date", "Creation date", "Created", "Created at" }),
        new DashboardRole("updated", "Dernière modification", new[] { "Dernière modification", "Date de modification", "Date de dernière modification", "Last update", "Last modification", "Modification date", "Updated at" }),
        new DashboardRole("resolved", "Date de résolution", new[] { "Date de résolution", "Date de resolution", "Resolution date", "Date de solution", "Solved date", "Solved at", "Resolved at" }),
        new DashboardRole("closed", "Date de clôture", new[] { "Date de clôture", "Closing date", "Closed date", "Closed at" }),
        new DashboardRole("due", "Échéance de résolution", new[] { "Temps de résolution", "Date limite de résolution", "Échéance de résolution", "Échéance", "Time to resolve", "Resolution due date", "Due date", "Date d'échéance", "TTR" }),
        new DashboardRole("assignee", "Technicien attribué", new[] { "Attribué à - Technicien", "Attribué à - Techniciens", "Assigned to - Technician", "Assigned to - Technicians", "Technicien", "Techniciens", "Assigned technician", "Assignee" }),
        new DashboardRole("group", "Groupe attribué", new[] { "Attribué à - Groupe de techniciens", "Attribué à - Groupes de techniciens", "Attribué à - Groupe", "Assigned to - Technician group", "Assigned to - Group", "Groupe de techniciens", "Groupe attribué", "Assignment group" }),
        new DashboardRole("requester", "Demandeur", new[] { "Demandeur", "Demandeur - Demandeur", "Demandeur - Utilisateur", "Demandeurs", "Requester", "Requester - Requester", "Requester - User" }),
        new DashboardRole("category", "Catégorie", new[] { "Catégorie", "Catégorie ITIL", "Category", "ITIL category" }),
        new DashboardRole("entity", "Entité", new[] { "Entité", "Entités", "Entity", "Entities" }),
        new DashboardRole("location", "Lieu", new[] { "Lieu", "Localisation", "Location" }),
        new DashboardRole("city", "Ville", new[] { "Ville", "City", "Town" }),
        new DashboardRole("postal", "Code postal", new[] { "Code postal", "Postal code", "Postcode", "ZIP code" }),
        new DashboardRole("source", "Origine de la demande", new[] { "Source de la demande", "Origine de la demande", "Source de demande", "Request source", "Source" }),
        new DashboardRole("updatedBy", "Dernière modification par", new[] { "Dernière modification par", "Last update by", "Last edited by", "Updated by" }),
        new DashboardRole("satisfaction", "Note de satisfaction", new[] { "Satisfaction", "Note de satisfaction", "Satisfaction score", "Satisfaction - Note" }),
        new DashboardRole("satisfaction-invited", "Invitation à l’enquête de satisfaction", new[] { "Date d'envoi de l'enquête", "Enquête envoyée", "Satisfaction - Date d'envoi", "Survey sent" }),
        new DashboardRole("solution", "Solution", new[] { "Solution", "Solution - Description", "Solutions - Description", "Solution description" })
    };

    private sealed class Record
    {
        public int Index, Status, Type, TechnicianCount, DatePresent, DateInvalid, DateFuture;
        public int? Priority;
        public string Id = "", Title = "", Assignee = "", Group = "";
        public DateTime? Created, Updated, Resolved, Closed, Due;
        public bool Active => Status is >= 1 and <= 4;
    }

    public static DashboardSnapshot Calculate(ExtractionResult data, DashboardSettings settings,
        DateTimeOffset? referenceTime = null, CancellationToken ct = default)
        => CalculateCore(data, settings, null, "Équipe entière", referenceTime, ct);

    /// <summary>Calculates a real subset without renumbering the rows used by drill-down links.</summary>
    public static DashboardSnapshot CalculateSubset(ExtractionResult data, DashboardSettings settings,
        IReadOnlyList<int> sourceRows, string scopeLabel, DateTimeOffset? referenceTime = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRows);
        return CalculateCore(data, settings, sourceRows, scopeLabel, referenceTime, ct);
    }

    private static DashboardSnapshot CalculateCore(ExtractionResult data, DashboardSettings settings,
        IReadOnlyList<int>? sourceRows, string scopeLabel, DateTimeOffset? referenceTime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettings(settings);
        ct.ThrowIfCancellationRequested();
        var now = referenceTime ?? DateTimeOffset.Now;
        var wallNow = now.DateTime;
        var selectedRows = sourceRows?.Distinct().Order().ToArray();
        if (selectedRows?.Any(i => i < 0 || i >= data.Rows.Count) == true)
            throw new ArgumentOutOfRangeException(nameof(sourceRows), "Le périmètre contient une ligne absente du relevé.");
        var sourceComplete = data.Complete && (!data.ExpectedTotal.HasValue || data.ExpectedTotal == data.Rows.Count);
        var receptionComplete = data.Quality is { } quality
            ? quality.ReceptionComplete && quality.SourceRows == data.Rows.Count && quality.ExpectedRows == data.ExpectedTotal
            : sourceComplete;
        var columns = ResolveColumns(data.Headers, settings);
        var index = columns.ToDictionary(x => x.Role, x => x.Index);
        bool Has(string key) => index[key].HasValue;
        string Cell(string[] row, string key) => index[key] is int n && n < row.Length ? NormalizeValue(row[n], key, data.Headers[n], settings) : "";
        var result = new DashboardSnapshot
        {
            CalculatedAt = now, ExtractedAt = data.ExtractedAt, RowCount = selectedRows?.Length ?? data.Rows.Count,
            ExpectedTotal = selectedRows is null ? data.ExpectedTotal : receptionComplete ? selectedRows.Length : null, Complete = sourceComplete,
            ReceptionComplete = receptionComplete, Quality = data.Quality,
            SourceUrl = data.SourceUrl, Scope = selectedRows is null ? data.Scope : data.Scope + " · " + scopeLabel, Columns = columns,
            IsFiltered = selectedRows is not null, ScopeLabel = scopeLabel,
            SourceRowCount = data.Rows.Count, SourceExpectedTotal = data.ExpectedTotal,
            ScopeRows = selectedRows ?? Enumerable.Range(0, data.Rows.Count).ToArray(),
            Warnings = data.Warnings.Distinct(StringComparer.Ordinal).ToList()
        };
        result.RecalculateScope = (rows, label, cancellation) => CalculateSubset(data, settings, rows, label, now, cancellation);
        if (!result.Complete) result.Warnings.Add(receptionComplete
            ? "Toutes les lignes annoncées sont chargées. Les réserves de contrôle restent à vérifier avant une comparaison historique."
            : "Périmètre incomplet ou non confirmé : les statistiques décrivent uniquement les lignes récupérées.");
        if (data.ExpectedTotal.HasValue && data.ExpectedTotal != data.Rows.Count)
            result.Warnings.Add($"Total annoncé {data.ExpectedTotal:N0} ; lignes récupérées {data.Rows.Count:N0}.");
        foreach (var col in columns.Where(x => x.State is "ambiguous" or "missing-override"))
            result.Warnings.Add($"{col.Label} : {(col.State == "ambiguous" ? "plusieurs colonnes possibles" : "colonne configurée absente")}. À régler dans les correspondances.");

        var invalid = new Dictionary<string, int>();
        var future = new Dictionary<string, int>();
        var records = new List<Record>(selectedRows?.Length ?? data.Rows.Count);
        var iteration = 0;
        foreach (var i in selectedRows ?? Enumerable.Range(0, data.Rows.Count))
        {
            if ((iteration++ & 255) == 0) ct.ThrowIfCancellationRequested();
            var row = data.Rows[i];
            var datePresent = 0; var dateInvalid = 0; var dateFuture = 0;
            DateTime? Date(string key, bool mayBeFuture = false)
            {
                var raw = Cell(row, key);
                if (IsMissing(raw)) return null;
                var bit = DateFieldBit(key);
                datePresent |= bit;
                if (!TrySourceDate(raw, settings, now.Offset, out var value))
                { dateInvalid |= bit; invalid[key] = invalid.GetValueOrDefault(key) + 1; return null; }
                if (!mayBeFuture && value > wallNow)
                { dateFuture |= bit; future[key] = future.GetValueOrDefault(key) + 1; return null; }
                return value;
            }
            var assignee = Cell(row, "assignee");
            records.Add(new Record
            {
                Index = i, Id = NormalizeId(Cell(row, "id")), Title = Cell(row, "title"),
                Status = Status(Cell(row, "status")), Priority = Priority(Cell(row, "priority")),
                Type = TypeKind(Cell(row, "type")), Assignee = assignee, TechnicianCount = SplitTechnicianNames(assignee, settings.MultiValueSeparators).Count, Group = Cell(row, "group"),
                Created = Date("created"), Updated = Date("updated"), Resolved = Date("resolved"), Closed = Date("closed"), Due = Date("due", true),
                DatePresent = datePresent, DateInvalid = dateInvalid, DateFuture = dateFuture
            });
        }
        foreach (var pair in invalid)
            result.Warnings.Add($"{columns.First(x => x.Role == pair.Key).Label} : {pair.Value:N0} valeurs non interprétables, exclues des calculs de date.");
        foreach (var pair in future)
            result.Warnings.Add($"{columns.First(x => x.Role == pair.Key).Label} : {pair.Value:N0} dates futures, exclues des calculs historiques.");
        var active = records.Where(r => r.Active).ToList();
        var known = records.Where(r => r.Status != 0).ToList();
        var unknown = records.Count - known.Count;
        if (Has("status") && unknown > 0) result.Warnings.Add($"{unknown:N0} statuts absents ou non reconnus ; ces lignes ne sont classées ni actives ni terminées.");
        var activeKnown = Has("status") && (records.Count == 0 || known.Count > 0);
        var identityGroups = records.Where(x => x.Id.Length > 0).GroupBy(x => x.Id).ToList();
        var missingIds = records.Where(x => x.Id.Length == 0).ToList();
        var duplicateRows = identityGroups.Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        if (Has("id") && missingIds.Count == 0 && duplicateRows.Count == 0) result.Grain = "tickets";
        if (Has("id") && duplicateRows.Count > 0)
            result.Warnings.Add($"{duplicateRows.Count:N0} lignes partagent un identifiant avec une autre ligne. Aucun dédoublonnage implicite ; les totaux comptent des occurrences.");
        if (Has("id") && missingIds.Count > 0) result.Warnings.Add($"{missingIds.Count:N0} identifiants absents ou non numériques ; le nombre de tickets uniques est partiel.");

        int[] Rows(IEnumerable<Record> rows)
        {
            ct.ThrowIfCancellationRequested();
            return rows.Select((record, position) =>
            {
                if ((position & 255) == 0) ct.ThrowIfCancellationRequested();
                return record.Index;
            }).ToArray();
        }
        void Count(string key, string title, IEnumerable<Record> rows, bool available, string explanation, int? sample = null, string tone = "normal")
        {
            var ids = available ? Rows(rows) : Array.Empty<int>();
            result.Metrics.Add(new(key, title, available ? ids.Length : null, result.Grain, explanation, ids, sample, tone));
        }
        void Number(string key, string title, double? value, string unit, string explanation, int sample, int[]? rows = null) =>
            result.Metrics.Add(new(key, title, value, unit, explanation, rows ?? Array.Empty<int>(), sample));
        Count("total", selectedRows is null ? "Relevé" : "Tickets du périmètre", records, true,
            selectedRows is null ? "Toutes les lignes récupérées ; aucun filtre de l’aperçu appliqué."
                : "Lignes du périmètre « " + scopeLabel + " » présentes dans le relevé. Les filtres de l’aperçu n’interviennent pas dans ce calcul.");
        Number("unique", "Identifiants distincts", Has("id") ? identityGroups.Count : null, "identifiants", "Identifiants numériques non vides. Aucune ligne n’est supprimée du relevé.", records.Count - missingIds.Count);
        Count("duplicate", "Lignes avec ID répété", duplicateRows, Has("id"), "Toutes les occurrences des identifiants présents plusieurs fois.", records.Count, "warning");
        Count("missing-id", "Sans ID exploitable", missingIds, Has("id"), "Identifiant absent, non numérique ou nul.", records.Count, "warning");
        Count("active", "Actifs", active, activeKnown, "Nouveau, en cours attribué/planifié ou en attente. Les statuts inconnus sont exclus.", known.Count);
        Count("new", "Nouveaux", records.Where(r => r.Status == 1), activeKnown, "Statut Nouveau parmi les statuts reconnus.", known.Count);
        Count("processing", "En cours", records.Where(r => r.Status is 2 or 3), activeKnown, "En cours attribué ou planifié parmi les statuts reconnus.", known.Count);
        Count("pending", "En attente", records.Where(r => r.Status == 4), activeKnown, "Inclus dans les actifs reconnus. Les échéances affichées ne tiennent pas compte des règles de pause du SLA.", known.Count);
        Count("resolved", "Résolus", records.Where(r => r.Status == 5), activeKnown, "Statut Résolu parmi les statuts reconnus au moment du relevé.", known.Count);
        Count("closed", "Clos", records.Where(r => r.Status == 6), activeKnown, "Statut Clos parmi les statuts reconnus au moment du relevé.", known.Count);
        Count("unknown-status", "Statuts non classés", records.Where(r => r.Status == 0), Has("status"), "Statut vide ou non reconnu ; aucune hypothèse sur son état.", records.Count, "warning");
        var completed = records.Where(r => r.Status is 5 or 6).ToList();
        Number("completed-share", "Part résolue ou close", activeKnown && known.Count > 0 ? 100d * completed.Count / known.Count : null, "%",
            "Part des lignes résolues ou closes parmi les statuts reconnus de ce relevé. Ce n’est pas un taux de résolution historique.", known.Count, Rows(completed));

        var prioritySample = active.Where(x => x.Priority.HasValue).ToList();
        var high = prioritySample.Where(x => x.Priority >= 4).ToList();
        var unassignedAvailable = activeKnown && (Has("assignee") || Has("group"));
        var unassignedLabel = Has("assignee") && Has("group") ? "Sans affectation" : Has("assignee") ? "Sans technicien renseigné" : "Sans groupe renseigné";
        var unassigned = active.Where(x => (!Has("assignee") || x.TechnicianCount == 0) && (!Has("group") || IsMissing(x.Group))).ToList();
        var datedActive = active.Where(x => x.Created.HasValue).ToList();
        var updatedActive = active.Where(x => x.Updated.HasValue && (!x.Created.HasValue || x.Updated >= x.Created)).ToList();
        var dueActive = active.Where(x => x.Due.HasValue).ToList();
        var overdue = dueActive.Where(x => x.Due < wallNow).ToList();
        var soon = dueActive.Where(x => x.Due >= wallNow && x.Due <= wallNow.AddHours(settings.DueSoonHours)).ToList();
        var stale = updatedActive.Where(x => (wallNow - x.Updated!.Value).TotalDays >= settings.StaleDays).ToList();
        var old = datedActive.Where(x => (wallNow - x.Created!.Value).TotalDays >= settings.OldDays).ToList();
        Count("high", "Priorités hautes actives", high, activeKnown && Has("priority") && (active.Count == 0 || prioritySample.Count > 0), "Haute, très haute ou majeure, parmi les actifs dont la priorité est reconnue.", prioritySample.Count, "warning");
        Count("major", "Priorités majeures actives", active.Where(x => x.Priority == 6), activeKnown && Has("priority") && (active.Count == 0 || prioritySample.Count > 0), "Priorité majeure GLPI (niveau 6).", prioritySample.Count, "error");
        foreach (var (key, title) in new[] { ("urgency", "Urgences hautes actives"), ("impact", "Impacts hauts actifs") })
        {
            var valid = active.Where(x => Severity(Cell(data.Rows[x.Index], key)).HasValue).ToList();
            Count("high-" + key, title, valid.Where(x => Severity(Cell(data.Rows[x.Index], key)) >= 4), activeKnown && Has(key) && (active.Count == 0 || valid.Count > 0),
                "Niveaux haut et très haut de ce champ, parmi les actifs. L’urgence et l’impact sont distincts de la priorité et ne la remplacent pas.", valid.Count, "warning");
        }
        Count("with-solution", "Solution renseignée", records.Where(x => !IsMissing(Cell(data.Rows[x.Index], "solution"))), Has("solution"),
            "Présence d’un texte dans la colonne Solution du relevé ; cela ne prouve pas son approbation ni son exhaustivité.", records.Count);
        Count("completed-no-solution", "Terminés sans solution visible", completed.Where(x => IsMissing(Cell(data.Rows[x.Index], "solution"))), activeKnown && Has("solution"),
            "Statut résolu ou clos avec une cellule Solution vide dans cet export. Une solution peut exister dans un autre onglet ou champ.", completed.Count, "warning");
        Count("unassigned", unassignedLabel, unassigned, unassignedAvailable,
            Has("assignee") && Has("group") ? "Actifs sans technicien et sans groupe renseignés." : "Une seule dimension d’affectation est disponible ; l’autre reste inconnue.", active.Count, "warning");
        Count("overdue", "Échéance dépassée", overdue, activeKnown && Has("due") && (active.Count == 0 || dueActive.Count > 0),
            "Date d’échéance affichée antérieure à l’heure du calcul. Échéances absentes exclues. Indication calendaire, pas un verdict de conformité SLA.", dueActive.Count, "error");
        Count("soon", $"À échéance sous {settings.DueSoonHours} h", soon, activeKnown && Has("due") && (active.Count == 0 || dueActive.Count > 0), "Actifs avec échéance comprise entre maintenant et la fin de la fenêtre configurée.", dueActive.Count, "warning");
        Count("stale", $"Sans modification ≥ {settings.StaleDays} j", stale, activeKnown && Has("updated") && (active.Count == 0 || updatedActive.Count > 0), "Ancienneté de la dernière modification disponible ; cela ne prouve pas une absence de travail.", updatedActive.Count, "warning");
        Count("old", $"Ouverts depuis ≥ {settings.OldDays} j", old, activeKnown && Has("created") && (active.Count == 0 || datedActive.Count > 0), "Âge calendaire des actifs dont la date d’ouverture est valide.", datedActive.Count, "warning");
        AddDurations("age", "Âge des actifs", datedActive.Select(x => (wallNow - x.Created!.Value).TotalDays).ToArray(), datedActive, "j", "Depuis l’ouverture jusqu’au calcul, en jours calendaires.");
        var resolution = records.Where(x => x.Created.HasValue && x.Resolved.HasValue && x.Resolved >= x.Created).ToList();
        var closure = records.Where(x => x.Created.HasValue && x.Closed.HasValue && x.Closed >= x.Created).ToList();
        var inconsistent = records.Where(x => (x.Created.HasValue && x.Resolved < x.Created) || (x.Created.HasValue && x.Closed < x.Created)
            || (x.Resolved.HasValue && x.Closed < x.Resolved) || (x.Created.HasValue && x.Updated < x.Created)).ToList();
        if (inconsistent.Count > 0) result.Warnings.Add($"{inconsistent.Count:N0} lignes présentent une chronologie ouverture/modification/résolution/clôture incohérente ; les durées invalides sont exclues.");
        AddDurations("resolution", "Délai ouverture → résolution", resolution.Select(x => (x.Resolved!.Value - x.Created!.Value).TotalHours).ToArray(), resolution, "h", "Durées calendaires des paires de dates valides du relevé. Ni temps de travail, ni durée contractuelle SLA.");
        AddDurations("closure", "Délai ouverture → clôture", closure.Select(x => (x.Closed!.Value - x.Created!.Value).TotalHours).ToArray(), closure, "h", "Durées calendaires des paires de dates valides du relevé.");
        foreach (var (key, title, selector) in new (string, string, Func<Record, DateTime?>)[] { ("created", "Créés", x => x.Created), ("resolved", "Résolutions datées", x => x.Resolved), ("closed", "Clôtures datées", x => x.Closed) })
            foreach (var days in new[] { 7, 30 })
                Count($"{key}-{days}", $"{title} sur {days} j", records.Where(x => selector(x) >= wallNow.AddDays(-days) && selector(x) <= wallNow), Has(key),
                    $"Événements datés sur les {days} derniers jours glissants, parmi les lignes de ce relevé uniquement. Les tickets absents de la recherche ne sont pas comptés.");

        void Distribution(string key, string title, IEnumerable<Record> pool, bool available, Func<Record, string> label, string note, string[]? order = null)
        {
            ct.ThrowIfCancellationRequested();
            var list = pool as IReadOnlyCollection<Record> ?? pool.ToArray();
            var groups = available ? list.GroupBy(label, StringComparer.OrdinalIgnoreCase).Select(g => new DashboardBucket(g.Key, g.Count(), Rows(g))) : Enumerable.Empty<DashboardBucket>();
            var buckets = order == null ? groups.OrderByDescending(x => x.Count).ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToList()
                : groups.OrderBy(x => Array.IndexOf(order, x.Label) is var rank && rank >= 0 ? rank : int.MaxValue).ThenBy(x => x.Label).ToList();
            result.Distributions.Add(new(key, title, available, list.Count, note, buckets));
        }
        Distribution("status", "Statuts", records, Has("status"), x => StatusLabel(x.Status), "Toutes les lignes. Les statuts non reconnus sont visibles.", new[] { "Nouveau", "En cours — attribué", "En cours — planifié", "En attente", "Résolu", "Clos", "Non classé" });
        Distribution("priority", "Priorités", records, Has("priority"), x => PriorityLabel(x.Priority), "Toutes les lignes ; les valeurs inconnues ne sont pas considérées comme faibles.", new[] { "Majeure", "Très haute", "Haute", "Moyenne", "Basse", "Très basse", "Non renseignée / inconnue" });
        Distribution("active-priority", "Priorités des actifs", active, activeKnown && Has("priority"), x => PriorityLabel(x.Priority), "Actifs uniquement.", new[] { "Majeure", "Très haute", "Haute", "Moyenne", "Basse", "Très basse", "Non renseignée / inconnue" });
        foreach (var (key, title) in new[] { ("urgency", "Urgences"), ("impact", "Impacts") })
            Distribution(key, title, records, Has(key), x => SeverityLabel(Severity(Cell(data.Rows[x.Index], key)), key == "impact"),
                "Toutes les lignes du périmètre. Ce champ est conservé séparément de la priorité.", key == "impact"
                    ? new[] { "Très haut", "Haut", "Moyen", "Bas", "Très bas", "Non renseigné / inconnu" }
                    : new[] { "Très haute", "Haute", "Moyenne", "Basse", "Très basse", "Non renseignée / inconnue" });
        Distribution("age", "Âge des actifs", active, activeKnown && Has("created"), x => !x.Created.HasValue ? "Date indisponible" : (wallNow - x.Created.Value).TotalDays switch { < 1 => "Moins de 1 j", < 7 => "1 à 6 j", < 14 => "7 à 13 j", < 30 => "14 à 29 j", < 90 => "30 à 89 j", _ => "90 j et plus" }, "Jours calendaires depuis l’ouverture.", new[] { "Moins de 1 j", "1 à 6 j", "7 à 13 j", "14 à 29 j", "30 à 89 j", "90 j et plus", "Date indisponible" });
        Distribution("inactivity", "Dernière modification des actifs", active, activeKnown && Has("updated"), x => !x.Updated.HasValue || x.Created.HasValue && x.Updated < x.Created ? "Date indisponible" : (wallNow - x.Updated.Value).TotalDays switch { < 1 => "Moins de 1 j", < 3 => "1 à 2 j", < 7 => "3 à 6 j", < 14 => "7 à 13 j", < 30 => "14 à 29 j", _ => "30 j et plus" }, "Une modification du ticket n’est pas une mesure du travail accompli. Les dates antérieures à l’ouverture connue sont classées indisponibles.", new[] { "Moins de 1 j", "1 à 2 j", "3 à 6 j", "7 à 13 j", "14 à 29 j", "30 j et plus", "Date indisponible" });
        Distribution("type", "Incidents et demandes", records, Has("type"), x => TypeLabel(Cell(data.Rows[x.Index], "type")), "Toutes les lignes.");
        Distribution("active-type", "Incidents et demandes — actifs", active, activeKnown && Has("type"), x => TypeLabel(Cell(data.Rows[x.Index], "type")),
            "Statuts actifs reconnus uniquement. Les types absents ou personnalisés restent visibles, sans être reclassés.");
        foreach (var role in new[] { "group", "assignee", "category", "entity", "requester", "location", "city", "postal", "source", "updatedBy" })
        {
            string Label(Record record) { var raw = Cell(data.Rows[record.Index], role); return IsMissing(raw) ? "Non renseigné" : raw; }
            var roleTitle = Roles.First(r => r.Key == role).Label;
            Distribution(role, roleTitle + " — relevé", records, Has(role), Label, role == "updatedBy"
                ? "Dernier auteur de modification exposé dans le relevé. Ce n’est ni l’historique des interventions ni une mesure de productivité."
                : "Valeur complète de chaque cellule ; les affectations multiples ne sont pas réparties artificiellement.");
            if (role is "group" or "assignee" or "category" or "entity" or "location" or "city" or "postal" or "source")
                Distribution("active-" + role, roleTitle + " — actifs", active, activeKnown && Has(role), Label, "Volume d’actifs par valeur complète. Ce volume ne mesure pas la capacité ou la performance d’une personne.");
        }
        for (var i = 29; i >= 0; i--)
        {
            var day = wallNow.Date.AddDays(-i);
            result.Timeline.Add(new(day, Rows(records.Where(r => r.Created?.Date == day)), Rows(records.Where(r => r.Resolved?.Date == day)), Rows(records.Where(r => r.Closed?.Date == day))));
        }
        var signals = new List<DashboardSignal>();
        void Signal(string label, List<Record> affected, List<Record> sample, int weight, string explanation)
        { if (activeKnown && sample.Count > 0 && weight > 0) signals.Add(new(label, affected.Count, sample.Count, active.Count, weight, Rows(affected), explanation)); }
        if (Has("due")) Signal("Échéance dépassée", overdue, dueActive, settings.Weather.OverdueWeight, "Sur les actifs ayant une échéance lisible ; comparaison calendaire uniquement.");
        if (Has("priority")) Signal("Priorité haute à majeure", high, prioritySample, settings.Weather.PriorityWeight, "Sur les actifs dont la priorité est reconnue.");
        if (Has("assignee") && Has("group")) Signal("Sans affectation", unassigned, active, settings.Weather.AssignmentWeight, "Technicien et groupe tous deux absents.");
        if (Has("updated")) Signal($"Sans modification ≥ {settings.StaleDays} j", stale, updatedActive, settings.Weather.StaleWeight, "Sur les actifs avec une date de dernière modification lisible.");
        if (Has("created")) Signal($"Âge ≥ {settings.OldDays} j", old, datedActive, settings.Weather.OldWeight, "Sur les actifs avec une date d’ouverture lisible.");
        result.Weather = Weather(result, active.Count, unknown, signals, Has("status"), duplicateRows.Count == 0 && (!Has("id") || missingIds.Count == 0), settings.Weather);
        var overdueSet = Rows(overdue).ToHashSet(); var highSet = Rows(high).ToHashSet(); var unassignedSet = unassignedAvailable ? Rows(unassigned).ToHashSet() : new HashSet<int>();
        var staleSet = Rows(stale).ToHashSet(); var oldSet = Rows(old).ToHashSet();
        AddAdvancedProbes();
        AddAnalysisSeries();
        foreach (var r in active.OrderByDescending(x => overdueSet.Contains(x.Index)).ThenByDescending(x => x.Priority ?? 0).ThenBy(x => x.Created ?? DateTime.MaxValue))
        {
            if (result.Attention.Count == 50) break;
            var reasons = new List<string>();
            if (overdueSet.Contains(r.Index)) reasons.Add("Échéance dépassée");
            if (highSet.Contains(r.Index)) reasons.Add("Priorité haute");
            if (unassignedSet.Contains(r.Index)) reasons.Add(unassignedLabel);
            if (staleSet.Contains(r.Index)) reasons.Add("Sans modification récente");
            if (oldSet.Contains(r.Index)) reasons.Add("Ancien");
            if (reasons.Count > 0) result.Attention.Add(new(r.Index, r.Id, r.Title, StatusLabel(r.Status), PriorityLabel(r.Priority), r.Group,
                r.Created.HasValue ? (wallNow - r.Created.Value).TotalDays : null, string.Join(" · ", reasons)));
        }
        ct.ThrowIfCancellationRequested();
        var dueBoundaries = settings.DueHorizonHours.Order().Distinct().ToArray();
        var horizonBuckets = new List<DashboardBucket>();
        if (Has("due") && activeKnown)
        {
            var overdueRows = active.Where(r => r.Due.HasValue && r.Due < wallNow).Select(r => r.Index).ToArray();
            horizonBuckets.Add(new("Dépassée", overdueRows.Length, overdueRows));
            for (var i = 0; i < dueBoundaries.Length; i++)
            {
                var low = dueBoundaries[i]; var upperBound = i + 1 < dueBoundaries.Length ? dueBoundaries[i + 1] : double.PositiveInfinity;
                var rows = active.Where(r => r.Due.HasValue && (r.Due.Value - wallNow).TotalHours >= low && (r.Due.Value - wallNow).TotalHours < upperBound).Select(r => r.Index).ToArray();
                horizonBuckets.Add(new(double.IsPositiveInfinity(upperBound) ? $"≥ {low:0.##} h" : $"[{low:0.##} ; {upperBound:0.##}[ h", rows.Length, rows));
            }
            var absentDue = active.Where(r => !r.Due.HasValue).Select(r => r.Index).ToArray(); horizonBuckets.Add(new("Sans échéance exploitable", absentDue.Length, absentDue));
        }
        result.Distributions.Add(new("due-exclusive", "Échéances · tranches exclusives", Has("due") && activeKnown, active.Count,
            "Chaque actif figure exactement dans une tranche : dépassée, intervalle de temps restant ou sans échéance exploitable. Bornes configurées dans les réglages ; aucune règle SLA supposée.", horizonBuckets));
        var scores = Has("satisfaction") ? records.Select(r => (Record: r, Raw: Cell(data.Rows[r.Index], "satisfaction")))
            .Select(x => (x.Record, Value: TrySourceNumber(x.Raw, settings, out var number) ? (double?)number : null))
            .Where(x => x.Value is double score && score >= 0 && score <= settings.SatisfactionScale).ToArray() : Array.Empty<(Record Record, double? Value)>();
        var scoreRows = scores.Select(x => x.Record.Index).ToArray();
        Number("satisfaction-score", "Satisfaction moyenne", scores.Length == 0 ? null : scores.Average(x => x.Value!.Value), "sur " + settings.SatisfactionScale.ToString("G", CultureInfo.CurrentCulture),
            "Moyenne des seules notes présentes, finies et dans l’échelle configurée. Une absence de réponse n’est pas une note zéro.", scores.Length, scoreRows);
        Number("satisfaction-responses", "Réponses de satisfaction", Has("satisfaction") ? scores.Length : null, "réponses", "Notes exploitables dans le relevé courant.", records.Count, scoreRows);
        Number("satisfaction-coverage", "Couverture de satisfaction du relevé", Has("satisfaction") && records.Count > 0 ? 100d * scores.Length / records.Count : null, "%",
            "Notes exploitables rapportées à tous les tickets du relevé, pas taux de réponse des seuls destinataires d’une enquête.", records.Count, scoreRows);
        var invitedRows = Has("satisfaction-invited") ? records.Where(r => { var raw = Cell(data.Rows[r.Index], "satisfaction-invited"); return Normalize(raw) is "oui" or "yes" or "true" or "1" || TrySourceDate(raw, settings, now.Offset, out _); }).Select(r => r.Index).ToArray() : Array.Empty<int>();
        Number("satisfaction-response-rate", "Réponse aux enquêtes envoyées", Has("satisfaction") && Has("satisfaction-invited") && invitedRows.Length > 0 ? 100d * scoreRows.Intersect(invitedRows).Count() / invitedRows.Length : null,
            "%", "Notes exploitables parmi les invitations explicitement datées ou marquées envoyées. Les invitations inconnues restent hors du dénominateur.", invitedRows.Length, scoreRows.Intersect(invitedRows).ToArray());
        return result;

        void AddAnalysisSeries()
        {
            foreach (var (role, selector) in new (string, Func<Record, DateTime?>)[]
                { ("created", x => x.Created), ("updated", x => x.Updated), ("resolved", x => x.Resolved), ("closed", x => x.Closed), ("due", x => x.Due) })
                if (Has(role)) result.DateValues[role] = records.Where(x => selector(x).HasValue).Select(x => new DashboardDateValue(x.Index, selector(x)!.Value)).ToArray();
            result.NumericSeries.Add(new("age", "Âge des actifs", "j", datedActive.Select(x => new DashboardNumericValue(x.Index, (wallNow - x.Created!.Value).TotalDays, x.Title)).ToArray(), "Jours calendaires depuis l’ouverture, parmi les actifs ayant une date exploitable."));
            result.NumericSeries.Add(new("inactivity", "Dernière modification des actifs", "j", updatedActive.Select(x => new DashboardNumericValue(x.Index, (wallNow - x.Updated!.Value).TotalDays, x.Title)).ToArray(), "Ancienneté d’une modification visible, sans déduction du travail accompli."));
            result.NumericSeries.Add(new("resolution", "Ouverture → résolution", "h", resolution.Select(x => new DashboardNumericValue(x.Index, (x.Resolved!.Value - x.Created!.Value).TotalHours, x.Title)).ToArray(), "Durée calendaire des paires de dates valides ; ni temps de travail ni conformité SLA."));
            result.NumericSeries.Add(new("overdue-delay", "Retard des échéances", "h", overdue.Select(x => new DashboardNumericValue(x.Index, (wallNow - x.Due!.Value).TotalHours, x.Title)).ToArray(), "Heures calendaires après l’échéance visible, parmi les actifs en retard."));
            var assignmentNote = settings.AssignmentCountingMode switch
            {
                "fractional" => "Chaque ticket est réparti à parts égales entre ses techniciens explicitement renseignés.",
                "cell" => "Chaque cellule d’affectation complète forme une seule catégorie.",
                _ => "Chaque technicien explicitement renseigné reçoit une participation ; la somme peut dépasser le nombre de tickets distincts."
            };
            void Matrix(string key, string title, string rowRole, string colRole, bool activeOnly = false)
            {
                var available = Has(rowRole) && Has(colRole) && (!activeOnly || activeKnown);
                var cells = new Dictionary<(string Row, string Col), (double Value, List<int> Rows)>();
                foreach (var record in available ? activeOnly ? active : records : Enumerable.Empty<Record>())
                {
                    if ((cells.Count & 255) == 0) ct.ThrowIfCancellationRequested();
                    string Label(string role) => role switch { "status" => StatusLabel(record.Status), "urgency" or "impact" => SeverityLabel(Severity(Cell(data.Rows[record.Index], role)), role == "impact"), _ => IsMissing(Cell(data.Rows[record.Index], role)) ? "Non renseigné" : Cell(data.Rows[record.Index], role) };
                    var raw = Label(rowRole);
                    var names = rowRole == "assignee" && settings.AssignmentCountingMode != "cell" ? SplitTechnicianNames(raw, settings.MultiValueSeparators) : new[] { raw };
                    if (names.Count == 0) names = new[] { "Non renseigné" };
                    var share = rowRole == "assignee" && settings.AssignmentCountingMode == "fractional" ? 1d / names.Count : 1d;
                    foreach (var name in names)
                    {
                        var cellKey = (name, Label(colRole));
                        if (!cells.TryGetValue(cellKey, out var item)) item = (0, new());
                        item.Value += share; item.Rows.Add(record.Index); cells[cellKey] = item;
                    }
                }
                result.Matrices.Add(new(key, title, available, rowRole == "assignee" && settings.AssignmentCountingMode == "fractional" ? "parts" : "occurrences",
                    cells.OrderByDescending(x => x.Value.Value).ThenBy(x => x.Key.Row).ThenBy(x => x.Key.Col).Select(x => new DashboardMatrixCell(x.Key.Row, x.Key.Col, x.Value.Value, x.Value.Rows.ToArray())).ToArray(),
                    (activeOnly ? "Actifs reconnus. " : "Lignes du périmètre. ") + (rowRole == "assignee" ? assignmentNote : "Les valeurs non reconnues restent visibles.")));
            }
            Matrix("technician-city", "Techniciens × villes", "assignee", "city", true);
            Matrix("city-status", "Villes × statuts", "city", "status");
            Matrix("category-status", "Catégories × statuts", "category", "status");
            Matrix("urgency-impact", "Urgence × impact", "urgency", "impact");
            foreach (var series in result.NumericSeries.Where(x => x.Key is "age" or "resolution"))
                foreach (var role in new[] { "city", "assignee", "category" })
                {
                    var boxes = Has(role) ? series.Values.GroupBy(x => IsMissing(Cell(data.Rows[x.Row], role)) ? "Non renseigné" : Cell(data.Rows[x.Row], role))
                        .Select(group => { var values = group.Select(x => x.Value).Order().ToArray(); return new DashboardBox(group.Key, values.Length, values[0], Percentile(values, .25)!.Value, Percentile(values, .5)!.Value, Percentile(values, .75)!.Value, values[^1], Percentile(values, .9)!.Value, group.Select(x => x.Row).ToArray()); })
                        .OrderByDescending(x => x.Median).ThenBy(x => x.Label).ToArray() : Array.Empty<DashboardBox>();
                    result.BoxSeries.Add(new(series.Key + "-" + role, series.Title + " par " + Roles.Single(x => x.Key == role).Label.ToLowerInvariant(), Has(role), series.Unit, boxes,
                        series.Explanation + " Quartiles et percentiles par interpolation linéaire ; effectifs affichés. Affectation complète conservée."));
                }
            result.Scatters.Add(new("age-deadline", "Âge et échéance des actifs", "Âge · jours", "Avant échéance · heures",
                active.Where(x => x.Created.HasValue && x.Due.HasValue).Select(x => new DashboardScatterPoint(x.Index, "#" + x.Id + " · " + x.Title,
                    (wallNow - x.Created!.Value).TotalDays, (x.Due!.Value - wallNow).TotalHours)).ToArray(), "Chaque point est une ligne du relevé. Une ordonnée négative signale une échéance calendaire dépassée."));
            if (settings.PriorityRules.Count > 0 && Has("priority") && Has("urgency") && Has("impact"))
            {
                var valid = records.Where(x => x.Priority.HasValue && Severity(Cell(data.Rows[x.Index], "impact")) is int impact
                    && Severity(Cell(data.Rows[x.Index], "urgency")) is int urgency && settings.PriorityRules.ContainsKey(impact + ":" + urgency)).ToList();
                var mismatch = valid.Where(x => settings.PriorityRules[Severity(Cell(data.Rows[x.Index], "impact")) + ":" + Severity(Cell(data.Rows[x.Index], "urgency"))] != x.Priority).ToArray();
                Count("priority-rule-mismatch", "Écarts à la matrice de priorité locale", mismatch, true, "Comparaison avec les règles configurées localement ; aucune règle GLPI n’est présumée.", valid.Count, "warning");
            }
            else Number("priority-rule-mismatch", "Écarts à la matrice de priorité locale", null, "lignes", "Renseignez les règles locales et les colonnes urgence, impact et priorité pour activer cette comparaison.", 0, Array.Empty<int>());
            RefreshWeatherProfiles(result, settings);
        }

        void AddDurations(string key, string title, double[] values, List<Record> sample, string unit, string explanation)
        {
            ct.ThrowIfCancellationRequested();
            Array.Sort(values);
            var sampleRows = Rows(sample);
            foreach (var (suffix, label, value) in new[]
            {
                ("mean", "moyenne", values.Length > 0 ? (double?)values.Average() : null),
                ("median", "médiane", Percentile(values, .5)),
                ("p90", "90e percentile", Percentile(values, .9)),
                ("max", "maximum", values.Length > 0 ? (double?)values[^1] : null)
            }) Number($"{key}-{suffix}", $"{title} · {label}", value, unit, explanation + " Percentiles calculés par interpolation linéaire.", sample.Count, sampleRows);
        }

        void AddAdvancedProbes()
        {
            ct.ThrowIfCancellationRequested();
            var at = $"Calculé au {now:dd/MM/yyyy HH:mm:ss zzz}. ";
            void Share(string key, string label, string affectedKey, int sample, bool available, string explanation)
            {
                var affected = result.Metric(affectedKey)!;
                var measured = available && affected.Value.HasValue && sample > 0;
                result.Metrics.Add(new(key, label, measured ? 100d * affected.Value!.Value / sample : null, "%", explanation,
                    measured ? affected.Rows : Array.Empty<int>(), sample));
            }

            Share("active-share", "Part active des statuts reconnus", "active", known.Count, activeKnown,
                "Actifs / statuts reconnus du périmètre. Les statuts inconnus sont exclus du numérateur et du dénominateur ; ce n’est pas une tendance historique.");
            Share("pending-share", "Part des actifs en attente", "pending", active.Count, activeKnown,
                "En attente / actifs reconnus. Un ticket en attente reste actif ; aucune règle de pause SLA n’est déduite.");
            Share("unassigned-share", "Part des actifs sans affectation", "unassigned", active.Count, activeKnown && Has("assignee") && Has("group"),
                "Actifs sans technicien et sans groupe / actifs reconnus. Les deux colonnes d’affectation doivent être disponibles.");
            Share("overdue-share", "Part des échéances dépassées", "overdue", dueActive.Count, activeKnown && Has("due"),
                at + "Actifs dont l’échéance est passée / actifs ayant une échéance lisible. Les échéances absentes ou illisibles sont exclues ; comparaison calendaire uniquement.");
            Share("high-share", "Part des actifs en priorité haute", "high", prioritySample.Count, activeKnown && Has("priority"),
                "Priorités haute, très haute ou majeure / actifs dont la priorité est reconnue. Les valeurs inconnues ne sont pas assimilées à une priorité faible.");

            var withoutTechnician = active.Where(record => record.TechnicianCount == 0);
            var withoutGroup = active.Where(record => IsMissing(record.Group));
            Count("without-technician", "Actifs sans technicien", withoutTechnician, activeKnown && Has("assignee"),
                "Aucun technicien renseigné dans la colonne d’affectation ; un groupe peut être présent.", active.Count, "warning");
            Count("without-group", "Actifs sans groupe", withoutGroup, activeKnown && Has("group"),
                "Aucun groupe renseigné dans la colonne d’affectation ; un technicien peut être présent.", active.Count, "warning");
            Share("without-technician-share", "Part des actifs sans technicien", "without-technician", active.Count, activeKnown && Has("assignee"),
                "Actifs sans technicien renseigné / actifs reconnus. La présence d’un groupe ne change pas ce ratio.");
            Share("without-group-share", "Part des actifs sans groupe", "without-group", active.Count, activeKnown && Has("group"),
                "Actifs sans groupe renseigné / actifs reconnus. La présence d’un technicien ne change pas ce ratio.");
            Count("multi-technician", "Actifs avec plusieurs techniciens", active.Where(record => record.TechnicianCount > 1), activeKnown && Has("assignee"),
                "Au moins deux identités distinctes dans une cellule d’affectation. Retours à la ligne, points-virgules et barres entourées d’espaces séparent les noms ; virgules et espaces ordinaires ne sont pas découpés.", active.Count);
            Count("new-unassigned", "Nouveaux sans affectation", active.Where(record => record.Status == 1 && unassignedSet.Contains(record.Index)),
                activeKnown && Has("assignee") && Has("group"), "Statut Nouveau, aucun technicien et aucun groupe renseignés. Les deux colonnes d’affectation sont requises.",
                active.Count(record => record.Status == 1), "warning");
            Distribution("assignment-coverage", "Affectation des actifs", active, activeKnown && Has("assignee") && Has("group"),
                record => record.TechnicianCount == 0 ? IsMissing(record.Group) ? "Sans technicien ni groupe" : "Groupe seul"
                    : IsMissing(record.Group) ? "Technicien seul" : "Technicien et groupe",
                "Chaque actif reconnu apparaît une seule fois. Une affectation renseignée ne mesure pas la capacité de l’équipe.",
                new[] { "Sans technicien ni groupe", "Groupe seul", "Technicien seul", "Technicien et groupe" });

            Count("active-no-due", "Actifs sans échéance renseignée", active.Where(record => (record.DatePresent & DateFieldBit("due")) == 0), activeKnown && Has("due"),
                "Cellule d’échéance vide ou explicitement non renseignée. Une date illisible n’est pas comptée comme absente ; consulter Qualité de l’échéance.", active.Count, "warning");
            var datesAvailable = activeKnown && Has("due") && (active.Count == 0 || dueActive.Count > 0);
            var tomorrow = wallNow.Date.AddDays(1);
            var weekEnd = wallNow.Date.AddDays(7 - ((int)wallNow.DayOfWeek + 6) % 7);
            Count("due-today", "Échéances aujourd’hui", dueActive.Where(record => record.Due >= wallNow.Date && record.Due < tomorrow), datesAvailable,
                at + "Actifs ayant une échéance pendant le jour civil du calcul, de 00:00 inclus au lendemain exclu. Inclut les échéances déjà passées aujourd’hui.", dueActive.Count, "warning");
            Count("due-tomorrow", "Échéances demain", dueActive.Where(record => record.Due >= tomorrow && record.Due < tomorrow.AddDays(1)), datesAvailable,
                at + "Actifs ayant une échéance pendant le prochain jour civil, dans le fuseau de référence du calcul.", dueActive.Count);
            Count("due-week", "Échéances d’ici la fin de semaine", dueActive.Where(record => record.Due >= wallNow && record.Due < weekEnd), datesAvailable,
                at + "Échéances à venir jusqu’au lundi suivant à 00:00 exclu. Les échéances déjà dépassées sont exclues. Semaine calendaire du lundi au dimanche.", dueActive.Count, "warning");

            var jointPriorityDue = active.Count(record => record.Priority.HasValue && record.Due.HasValue);
            var jointPriorityUpdated = updatedActive.Count(record => record.Priority.HasValue);
            Count("high-overdue", "Hautes priorités à échéance dépassée", high.Where(record => overdueSet.Contains(record.Index)),
                activeKnown && Has("priority") && Has("due") && (active.Count == 0 || jointPriorityDue > 0),
                at + "Intersection priorité haute à majeure et échéance passée. Échantillon : actifs avec priorité reconnue et échéance lisible. Indication calendaire, pas un verdict SLA.", jointPriorityDue, "error");
            Count("high-stale", "Hautes priorités sans modification récente", high.Where(record => staleSet.Contains(record.Index)),
                activeKnown && Has("priority") && Has("updated") && (active.Count == 0 || jointPriorityUpdated > 0),
                at + $"Intersection priorité haute à majeure et dernière modification depuis au moins {settings.StaleDays} jours. Échantillon : actifs avec priorité et modification exploitables ; ce n’est pas une preuve d’absence de travail.", jointPriorityUpdated, "warning");

            Count("missing-category", "Catégorie non renseignée", records.Where(record => IsMissing(Cell(data.Rows[record.Index], "category"))), Has("category"),
                "Toutes les lignes du périmètre dont la catégorie est vide ou explicitement non renseignée.", records.Count, "warning");
            Count("missing-requester", "Demandeur non renseigné", records.Where(record => IsMissing(Cell(data.Rows[record.Index], "requester"))), Has("requester"),
                "Toutes les lignes du périmètre dont le demandeur est vide ou explicitement non renseigné.", records.Count, "warning");
            var dateRoles = new[] { "created", "updated", "resolved", "closed", "due" };
            var observedDates = records.Count(record => record.DatePresent != 0);
            var observedHistory = records.Count(record => (record.DatePresent & ~record.DateInvalid & 15) != 0);
            Count("invalid-dates", "Lignes avec une date illisible", records.Where(record => record.DateInvalid != 0),
                dateRoles.Any(Has) && (records.Count == 0 || observedDates > 0),
                "Au moins une date non vide ne respecte pas le format configuré. Une ligne compte une seule fois, même si plusieurs champs sont illisibles. Les cellules vides et les chronologies incohérentes sont suivies séparément.", observedDates, "warning");
            Count("future-dates", "Dates historiques dans le futur", records.Where(record => record.DateFuture != 0),
                dateRoles.Take(4).Any(Has) && (records.Count == 0 || observedHistory > 0),
                at + "Au moins une date d’ouverture, modification, résolution ou clôture se trouve après l’heure du calcul et est exclue des calculs historiques. Une échéance future est normale et n’entre pas dans cette sonde.", observedHistory, "warning");
            var chronologySample = records.Count(record => record.Created.HasValue && (record.Updated.HasValue || record.Resolved.HasValue || record.Closed.HasValue)
                || record.Resolved.HasValue && record.Closed.HasValue);
            var chronologyAvailable = Has("created") && (Has("updated") || Has("resolved") || Has("closed")) || Has("resolved") && Has("closed");
            Count("inconsistent-dates", "Chronologies incohérentes", inconsistent,
                chronologyAvailable && (records.Count == 0 || chronologySample > 0),
                "Modification, résolution ou clôture antérieure à l’ouverture, ou clôture antérieure à la résolution. Échantillon : lignes avec au moins une paire de dates comparable. Chaque durée utilise seulement ses paires valides.", chronologySample, "warning");
            foreach (var role in dateRoles)
            {
                var bit = DateFieldBit(role);
                var title = Roles.First(item => item.Key == role).Label;
                Distribution("quality-" + role, "Qualité · " + title, records, Has(role),
                    record => (record.DatePresent & bit) == 0 ? "Non renseignée" : (record.DateInvalid & bit) != 0 ? "Illisible"
                        : (record.DateFuture & bit) != 0 ? "Future exclue" : "Lisible",
                    at + (role == "due" ? "Les échéances futures sont normales. " : "Les dates historiques futures sont exclues des calculs. ")
                        + "Une date lisible peut être incohérente avec une autre date ; consulter Chronologies incohérentes.",
                    new[] { "Lisible", "Non renseignée", "Illisible", "Future exclue" });
            }

            AddDurations("inactivity", "Depuis la dernière modification", updatedActive.Select(record => (wallNow - record.Updated!.Value).TotalDays).ToArray(), updatedActive, "j",
                at + "Durées calendaires des actifs avec une modification lisible, non future et non antérieure à l’ouverture connue. Ce n’est pas une mesure du temps travaillé.");
            var resolvedToClosed = records.Where(record => record.Resolved.HasValue && record.Closed.HasValue && record.Closed >= record.Resolved).ToList();
            AddDurations("resolution-closure", "Délai résolution → clôture", resolvedToClosed.Select(record => (record.Closed!.Value - record.Resolved!.Value).TotalHours).ToArray(), resolvedToClosed, "h",
                "Durées calendaires des paires résolution/clôture présentes, lisibles, non futures et dans le bon ordre. Aucune durée n’est déduite du seul statut.");
            var knownTypes = active.Count(record => record.Type != 0);
            var typesAvailable = activeKnown && Has("type") && (active.Count == 0 || knownTypes > 0);
            Count("active-incidents", "Incidents actifs", active.Where(record => record.Type == 1), typesAvailable,
                "Actifs dont le type est Incident (ou code GLPI 1). Les types absents et personnalisés sont exclus.", knownTypes);
            Count("active-requests", "Demandes actives", active.Where(record => record.Type == 2), typesAvailable,
                "Actifs dont le type est Demande / Request (ou code GLPI 2). Les types absents et personnalisés sont exclus.", knownTypes);

            // Current-state intersections keep the source row identity and use only
            // the dates that can actually be read. No transition history is inferred.
            var pending = active.Where(record => record.Status == 4).ToList();
            var pendingDated = datedActive.Where(record => record.Status == 4).ToList();
            var pendingUpdated = updatedActive.Where(record => record.Status == 4).ToList();
            var newTickets = active.Where(record => record.Status == 1).ToList();
            var newUpdated = updatedActive.Where(record => record.Status == 1).ToList();
            Count("pending-old", "En attente · tickets anciens", old.Where(record => record.Status == 4),
                activeKnown && Has("created") && (pending.Count == 0 || pendingDated.Count > 0),
                at + $"Statut actuellement En attente, ouverture depuis au moins {settings.OldDays} jours. L’âge est celui du ticket ; la date de passage en attente n’est pas connue.", pendingDated.Count, "warning");
            Count("pending-stale", "En attente · sans modification récente", stale.Where(record => record.Status == 4),
                activeKnown && Has("updated") && (pending.Count == 0 || pendingUpdated.Count > 0),
                at + $"Statut actuellement En attente, dernière modification exploitable depuis au moins {settings.StaleDays} jours. Ce n’est ni la durée en attente ni une preuve d’inactivité.", pendingUpdated.Count, "warning");
            Count("new-stale", "Nouveaux · sans modification récente", stale.Where(record => record.Status == 1),
                activeKnown && Has("updated") && (newTickets.Count == 0 || newUpdated.Count > 0),
                at + $"Statut Nouveau et dernière modification exploitable depuis au moins {settings.StaleDays} jours. Les dates futures ou antérieures à l’ouverture connue sont exclues.", newUpdated.Count, "warning");
            Count("high-without-technician", "Hautes priorités sans technicien", high.Where(record => record.TechnicianCount == 0),
                activeKnown && Has("priority") && Has("assignee") && (active.Count == 0 || prioritySample.Count > 0),
                "Actifs en priorité haute, très haute ou majeure, sans technicien nommé. Un groupe peut être affecté. Échantillon : actifs dont la priorité est reconnue.", prioritySample.Count, "error");
            Count("overdue-without-technician", "Retards sans technicien", overdue.Where(record => record.TechnicianCount == 0),
                datesAvailable && Has("assignee"),
                at + "Actifs à échéance calendaire dépassée sans technicien nommé. Un groupe peut être présent. Les pauses et calendriers SLA ne sont pas interprétés.", dueActive.Count, "error");
            Count("group-only", "Actifs affectés à un groupe seul", active.Where(record => record.TechnicianCount == 0 && !IsMissing(record.Group)),
                activeKnown && Has("assignee") && Has("group"),
                "Un groupe est renseigné mais aucun technicien n’est nommé. Cela ne signifie pas que le ticket n’est pas pris en charge par l’équipe.", active.Count);
            var planned = active.Where(record => record.Status == 3).ToList();
            var plannedDated = dueActive.Count(record => record.Status == 3);
            Count("planned-overdue", "Planifiés à échéance dépassée", overdue.Where(record => record.Status == 3),
                activeKnown && Has("due") && (planned.Count == 0 || plannedDated > 0),
                at + "Statut En cours (planifié) et échéance affichée dépassée. L’échéance n’est pas la date prévue d’intervention ; aucune planification de tâche n’est déduite.", plannedDated, "warning");
            Count("old-without-technician", "Tickets anciens sans technicien", old.Where(record => record.TechnicianCount == 0),
                activeKnown && Has("created") && Has("assignee") && (active.Count == 0 || datedActive.Count > 0),
                at + $"Actifs ouverts depuis au moins {settings.OldDays} jours, sans technicien nommé. Âge calendaire depuis une ouverture lisible ; un groupe peut être affecté.", datedActive.Count, "warning");
            Count("active-no-category", "Actifs sans catégorie", active.Where(record => IsMissing(Cell(data.Rows[record.Index], "category"))),
                activeKnown && Has("category"), "Actifs reconnus dont la catégorie est vide ou explicitement non renseignée.", active.Count, "warning");
            var awaitingClosure = records.Where(record => record.Status == 5).ToList();
            var resolvedDated = awaitingClosure.Where(record => record.Resolved.HasValue
                && (!record.Created.HasValue || record.Resolved >= record.Created)).ToList();
            Count("resolved-aging", "Résolus · clôture à suivre", resolvedDated.Where(record => (wallNow - record.Resolved!.Value).TotalDays >= settings.StaleDays),
                activeKnown && Has("resolved") && (awaitingClosure.Count == 0 || resolvedDated.Count > 0),
                at + $"Statut actuellement Résolu et date de résolution depuis au moins {settings.StaleDays} jours. La règle de clôture automatique et l’état d’approbation ne sont pas connus.", resolvedDated.Count, "warning");
            Count("resolved-no-date", "Résolus sans date de résolution", awaitingClosure.Where(record => (record.DatePresent & DateFieldBit("resolved")) == 0),
                activeKnown && Has("resolved"),
                "Statut actuellement Résolu avec une cellule de résolution vide ou explicitement non renseignée. Les valeurs illisibles sont suivies séparément, pas assimilées à des dates absentes.", awaitingClosure.Count, "warning");
            AddDurations("resolved-waiting", "Résolus · depuis la résolution", resolvedDated.Select(record => (wallNow - record.Resolved!.Value).TotalDays).ToArray(), resolvedDated, "j",
                at + "Jours calendaires depuis la résolution des lignes actuellement au statut Résolu. Dates lisibles, non futures, non antérieures à l’ouverture connue. Ce délai ne mesure ni l’approbation de la solution ni une obligation de clôture.");
            var incidentPriority = active.Where(record => record.Type == 1 && record.Priority.HasValue).ToList();
            Count("high-incidents", "Incidents actifs en priorité haute", incidentPriority.Where(record => record.Priority >= 4),
                typesAvailable && Has("priority") && (active.All(record => record.Type != 1) || incidentPriority.Count > 0),
                "Actifs de type Incident en priorité haute, très haute ou majeure. Les types et priorités non reconnus sont exclus.", incidentPriority.Count, "warning");

            string HoursBucket(double hours) => hours switch
            {
                < 1 => "Moins de 1 h", < 4 => "1 à moins de 4 h", < 24 => "4 à moins de 24 h",
                < 72 => "1 à moins de 3 j", < 168 => "3 à moins de 7 j", < 720 => "7 à moins de 30 j", _ => "30 j et plus"
            };
            var durationOrder = new[] { "Moins de 1 h", "1 à moins de 4 h", "4 à moins de 24 h", "1 à moins de 3 j", "3 à moins de 7 j", "7 à moins de 30 j", "30 j et plus" };
            Distribution("overdue-delay", "Retard calendaire des actifs", overdue, datesAvailable,
                record => HoursBucket((wallNow - record.Due!.Value).TotalHours),
                at + "Actifs déjà à échéance dépassée, répartis par durée écoulée depuis l’échéance affichée. Aucune règle SLA de calendrier ou de pause n’est appliquée.", durationOrder);
            Distribution("due-horizon", "Échéances à venir des actifs", dueActive.Where(record => record.Due >= wallNow), datesAvailable,
                record => HoursBucket((record.Due!.Value - wallNow).TotalHours),
                at + "Actifs ayant une échéance lisible égale ou postérieure à l’heure du calcul, répartis par temps restant. Les échéances dépassées ou absentes sont exclues.", durationOrder);
        }
    }

    public static List<DashboardColumn> ResolveColumns(IReadOnlyList<string> headers, DashboardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = headers.Select(Normalize).ToArray();
        return Roles.Select(role =>
        {
            settings.ColumnMappings.TryGetValue(role.Key, out var configured);
            var exact = !string.IsNullOrWhiteSpace(configured);
            var matches = Enumerable.Range(0, headers.Count).Where(i => exact
                ? string.Equals(headers[i], configured, StringComparison.Ordinal)
                : HeaderMatches(role, headers[i]) || IsRenamedDuplicate(role, headers[i], normalized)).ToArray();
            return new DashboardColumn(role.Key, role.Label, matches.Length == 1 ? matches[0] : null,
                matches.Length == 1 ? headers[matches[0]] : configured ?? "", matches.Length == 1 ? exact ? "configured" : "auto" : matches.Length > 1 ? "ambiguous" : exact ? "missing-override" : "missing");
        }).ToList();
    }

    private static bool HeaderMatches(DashboardRole role, string? header)
    {
        var normalized = Normalize(header);
        if (role.Aliases.Any(alias => Normalize(alias) == normalized)) return true;
        // Accept known ticket namespaces, not an arbitrary suffix match. For example,
        // "Demandeur - ID" must never become the identifier of the ticket itself.
        var parts = Regex.Split(header?.Trim() ?? "", @"\s+(?:-|–|—|>|/)\s+|\s*[:：>]\s*|[\r\n]+")
            .Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        if (parts.Count < 2) return false;
        if (Normalize(parts[0]) is "ticket" or "tickets") parts.RemoveAt(0);
        if (parts.Count > 1 && Normalize(parts[0]) is "general" or "caracteristiques" or "characteristics") parts.RemoveAt(0);
        if (role.Key is "city" or "postal" && parts.Count == 2 && Normalize(parts[0]) is "lieu" or "location" or "localisation")
            parts.RemoveAt(0);
        return role.Aliases.Any(alias => Normalize(alias) == Normalize(string.Join(" - ", parts)));
    }

    private static bool IsRenamedDuplicate(DashboardRole role, string header, string[] normalizedHeaders)
    {
        // The native importer renames duplicate labels to "Label (2)". Keep this
        // ambiguity visible instead of silently binding only the first occurrence.
        var match = Regex.Match(header ?? "", @"^(.*) \((?:[2-9]|[1-9][0-9]+)\)$");
        return match.Success && normalizedHeaders.Contains(Normalize(match.Groups[1].Value), StringComparer.Ordinal)
            && HeaderMatches(role, match.Groups[1].Value);
    }

    public static void ValidateSettings(DashboardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.DueHorizonHours == null || settings.DueHorizonHours.Count is < 1 or > 20 || settings.DueHorizonHours[0] != 0
            || settings.DueHorizonHours.Any(v => !double.IsFinite(v) || v < 0 || v > 87600)
            || !settings.DueHorizonHours.SequenceEqual(settings.DueHorizonHours.Distinct().Order())
            || !double.IsFinite(settings.SatisfactionScale) || settings.SatisfactionScale <= 0 || settings.SatisfactionScale > 100
            || settings.HistoryCadenceHours is < 1 or > 720)
            throw new InvalidOperationException("Tranches d’échéance : 0 puis des bornes croissantes en heures ; échelle satisfaction de 0 à 100 ; cadence de 1 à 720 h.");
        try { if (settings.NumberCulture != "invariant") CultureInfo.GetCultureInfo(settings.NumberCulture); }
        catch (CultureNotFoundException) { throw new InvalidOperationException("Le format numérique de la source n’est pas reconnu."); }
        if (!string.IsNullOrWhiteSpace(settings.SourceTimeZone))
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(settings.SourceTimeZone); }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new InvalidOperationException("Le fuseau horaire de la source n’est pas reconnu."); }
        }

        if (settings.StaleDays is < 1 or > 3650 || settings.OldDays < settings.StaleDays || settings.OldDays > 3650 || settings.DueSoonHours is < 1 or > 720)
            throw new InvalidOperationException("Tableau de bord : seuils de 1 à 3 650 jours, âge ancien ≥ inactivité, échéance de 1 à 720 heures.");
        if (settings.DateCulture is not ("fr-FR" or "en-GB" or "en-US" or "invariant"))
            throw new InvalidOperationException("Tableau de bord : format de dates non reconnu.");
        if (settings.ColumnMappings == null || settings.ColumnMappings.Any(x => !Roles.Any(r => string.Equals(r.Key, x.Key, StringComparison.OrdinalIgnoreCase)) || (x.Value?.Length ?? 0) > 512))
            throw new InvalidOperationException("Tableau de bord : correspondances de colonnes invalides.");
        if (settings.Weather is null) throw new InvalidOperationException("La configuration météo est absente.");
        settings.Weather.Validate();
        if (settings.AssignmentCountingMode is not ("participation" or "fractional" or "cell"))
            throw new InvalidOperationException("Le mode de comptage des affectations est invalide.");
        if (settings.PriorityRules is null || settings.PriorityRules.Count > 25 || settings.PriorityRules.Any(x => !Regex.IsMatch(x.Key, "^[1-5]:[1-5]$") || x.Value is < 1 or > 6))
            throw new InvalidOperationException("La matrice locale accepte des couples impact:urgence de 1 à 5 et une priorité de 1 à 6.");
    }

    private static string WeatherSourceSuffix(DashboardSnapshot snapshot) => snapshot.Quality is
        { ScopeStable: false } or { SampleIdentityVerified: false } or { FinalTotalVerified: false } ? " · périmètre à vérifier"
        : snapshot.Complete || snapshot.ReceptionComplete ? "" : " · collecte non confirmée";

    private static string WeatherSourceDescription(DashboardSnapshot snapshot) => snapshot.Complete ? ""
        : snapshot.ReceptionComplete ? "Réception des lignes complète ; les contrôles du relevé restent à vérifier. "
        : "La réception de toutes les lignes n’est pas confirmée. ";

    private static DashboardWeather Weather(DashboardSnapshot snapshot, int active, int unknown, List<DashboardSignal> signals, bool hasStatus, bool identityClean, DashboardWeatherSettings formula)
    {
        if (!hasStatus || snapshot.RowCount == 0 || active == 0 && unknown > 0)
            return new("unknown", "Météo indéterminée", "◌", null, 0, "Aucun ensemble d’actifs fiable à évaluer. Vérifiez les colonnes et le périmètre du relevé.", signals);
        if (active == 0)
            return new(snapshot.Complete && unknown == 0 ? "sun" : "partial", "Aucun actif dans ce relevé" + WeatherSourceSuffix(snapshot), "☀", null, 0,
                "Les statuts reconnus sont résolus ou clos. Cette météo concerne la recherche extraite uniquement. " + WeatherSourceDescription(snapshot), signals);
        var weight = signals.Sum(s => s.Weight);
        if (weight == 0) return new("unknown", "Météo indéterminée", "◌", null, 0,
            "Aucun indicateur exploitable avec une pondération active. Vérifiez les correspondances, les valeurs renseignées et les pondérations de la météo. Les données indisponibles ne sont pas assimilées à zéro. " + WeatherSourceDescription(snapshot), signals);
        var score = signals.Sum(s => s.Weight * (100d * s.Count / s.Sample)) / weight;
        var signalsSufficient = identityClean && unknown == 0 && weight >= formula.TotalWeight * formula.MinimumWeightPercent / 100d
            && signals.All(s => s.Sample >= Math.Ceiling(active * formula.MinimumCoveragePercent / 100d));
        var sufficient = snapshot.Complete && signalsSufficient;
        var state = score >= formula.StormThreshold ? "storm" : score >= formula.RainThreshold ? "rain" : score >= formula.WatchThreshold ? "watch" : "sun";
        var title = state switch { "storm" => "Orage · vigilance forte", "rain" => "Pluie · points à traiter", "watch" => "Vigilance · situation à surveiller", _ => "Soleil · peu de signaux détectés" };
        if (!signalsSufficient)
            title = score < formula.WatchThreshold ? "Météo des indicateurs disponibles" : title + " · indicateurs disponibles";
        else title += WeatherSourceSuffix(snapshot);
        if (!sufficient && score < formula.WatchThreshold) state = "partial";
        var coverage = new List<string>();
        if (weight < formula.TotalWeight * formula.MinimumWeightPercent / 100d)
            coverage.Add($"Le poids des indicateurs disponibles reste inférieur au seuil de couverture de {formula.MinimumWeightPercent} %.");
        if (signals.Any(s => s.Sample < Math.Ceiling(active * formula.MinimumCoveragePercent / 100d)))
            coverage.Add($"Au moins un indicateur couvre moins de {formula.MinimumCoveragePercent} % des actifs.");
        if (!identityClean) coverage.Add("Les identifiants absents ou répétés limitent la fiabilité du comptage.");
        if (unknown > 0) coverage.Add($"{unknown:N0} statuts non reconnus restent exclus des actifs identifiés.");
        var symbol = state switch { "sun" => "☀", "watch" => "⛅", "rain" => "☂", "storm" => "ϟ", _ => "◌" };
        return new(state, title, symbol, score, weight,
            $"Indice indicatif {score:0.#}/100. Pondérations disponibles : {weight}/{formula.TotalWeight}. Formule locale v{formula.Version} ; seuils {formula.WatchThreshold:0.#}/{formula.RainThreshold:0.#}/{formula.StormThreshold:0.#}. " +
            "L’indice utilise seulement les indicateurs disponibles, chacun sur son échantillon renseigné. Les indicateurs indisponibles ne sont pas assimilés à zéro. " +
            (signalsSufficient ? $"Les indicateurs disponibles couvrent au moins {formula.MinimumCoveragePercent} % des actifs chacun. " : string.Join(" ", coverage) + " ") +
            WeatherSourceDescription(snapshot) +
            "Ce n’est ni une prévision, ni une mesure de capacité, ni un contrôle contractuel du SLA.", signals);
    }

    public static int[] ResolveScope(DashboardSnapshot snapshot, IReadOnlyList<DashboardScopeFilter> filters, CancellationToken ct = default)
    {
        DashboardScopeFilter.ValidateList(filters);
        var available = snapshot.ScopeRows.Length == snapshot.RowCount ? snapshot.ScopeRows : snapshot.Metric("total")?.Rows ?? Array.Empty<int>();
        var scope = available.ToHashSet();
        foreach (var filter in filters)
        {
            ct.ThrowIfCancellationRequested();
            int[] rows;
            if (filter.Kind == "metric")
            {
                var metric = snapshot.Metric(filter.SourceKey);
                if (metric?.Value is null) throw new System.IO.InvalidDataException("Filtre indisponible : " + filter.SourceKey + ". Vérifiez les colonnes nécessaires ou retirez ce filtre.");
                rows = metric.Rows;
            }
            else if (filter.Kind == "date")
            {
                if (!snapshot.DateValues.TryGetValue(filter.SourceKey, out var values))
                    throw new System.IO.InvalidDataException("Le champ de date du filtre est absent : " + filter.SourceKey);
                var start = DateTime.ParseExact(filter.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var end = DateTime.ParseExact(filter.Value2, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                rows = values.Where(x => x.Date.Date >= start && x.Date.Date <= end).Select(x => x.Row).ToArray();
                // Missing dates stay unknown even when an interval is excluded.
                if (filter.Exclude) scope.IntersectWith(values.Select(x => x.Row));
            }
            else
            {
                var distribution = snapshot.Distributions.FirstOrDefault(x => x.Key == filter.SourceKey);
                if (distribution?.Available != true) throw new System.IO.InvalidDataException("Répartition du filtre indisponible : " + filter.SourceKey);
                rows = distribution.Buckets.Where(x => string.Equals(x.Label, filter.Value, StringComparison.OrdinalIgnoreCase)).SelectMany(x => x.Rows).Distinct().ToArray();
            }
            if (filter.Exclude) scope.ExceptWith(rows); else scope.IntersectWith(rows);
        }
        return scope.Order().ToArray();
    }

    public static DashboardDistribution Histogram(DashboardNumericSeries series, IReadOnlyList<double> edges)
    {
        if (edges.Count > 30 || edges.Any(x => !double.IsFinite(x))) throw new ArgumentException("Tranches invalides.", nameof(edges));
        var boundaries = edges.Distinct().Order().ToArray();
        var values = series.Values.Where(x => double.IsFinite(x.Value)).ToArray();
        var grouped = values.GroupBy(x => Array.FindIndex(boundaries, edge => x.Value < edge) is var index && index >= 0 ? index : boundaries.Length).ToDictionary(x => x.Key, x => x.ToArray());
        var buckets = new List<DashboardBucket>();
        for (var index = 0; index <= boundaries.Length; index++)
        {
            var items = grouped.GetValueOrDefault(index) ?? Array.Empty<DashboardNumericValue>();
            var label = boundaries.Length == 0 ? "Toutes les valeurs" : index == 0 ? $"< {boundaries[0]:G} {series.Unit}"
                : index == boundaries.Length ? $"≥ {boundaries[^1]:G} {series.Unit}" : $"[{boundaries[index - 1]:G} ; {boundaries[index]:G}[ {series.Unit}";
            buckets.Add(new(label, items.Length, items.Select(x => x.Row).ToArray()));
        }
        return new("histogram:" + series.Key, series.Title, true, values.Length, series.Explanation + " Tranches définies dans le widget ; les valeurs manquantes ou non finies sont exclues.", buckets);
    }

    public static void RefreshWeatherProfiles(DashboardSnapshot snapshot, DashboardSettings settings)
    {
        snapshot.WeatherProfiles["overall"] = snapshot.Weather;
        DashboardWeather Profile(string title, params string[] keys)
        {
            var signals = keys.Select(key => snapshot.Metric(key)).Where(metric => metric?.Value is not null && (metric.Sample ?? snapshot.RowCount) > 0).Select(metric =>
                new DashboardSignal(metric!.Label, (int)Math.Clamp(metric.Value!.Value, 0, int.MaxValue), metric.Sample ?? snapshot.RowCount, snapshot.RowCount, 1, metric.Rows, metric.Explanation)).ToList();
            if (signals.Count == 0) return new("unknown", title + " · indisponible", "◌", null, 0, "Aucun échantillon exploitable pour ce profil.", signals);
            var score = signals.Average(x => Math.Min(100, 100d * x.Count / x.Sample));
            var state = score >= settings.Weather.StormThreshold ? "storm" : score >= settings.Weather.RainThreshold ? "rain" : score >= settings.Weather.WatchThreshold ? "watch" : "sun";
            var complete = snapshot.Complete && signals.Count == keys.Length;
            if (!complete && state == "sun") state = "partial";
            return new(state, title + (signals.Count < keys.Length ? " · indicateurs disponibles" : WeatherSourceSuffix(snapshot)), state switch { "storm" => "ϟ", "rain" => "☂", "watch" => "⛅", "sun" => "☀", _ => "◌" }, score, signals.Count,
                $"Moyenne des {signals.Count} taux disponibles sur {keys.Length} signaux, pondérés également. Les données absentes restent exclues, pas assimilées à zéro. Seuils locaux {settings.Weather.WatchThreshold:G}/{settings.Weather.RainThreshold:G}/{settings.Weather.StormThreshold:G}. Indice descriptif du relevé. " + WeatherSourceDescription(snapshot), signals);
        }
        snapshot.WeatherProfiles["deadlines"] = Profile("Météo des échéances", "overdue", "active-no-due");
        snapshot.WeatherProfiles["quality"] = Profile("Météo de qualité", "unknown-status", "missing-id", "invalid-dates", "inconsistent-dates");
        snapshot.WeatherProfiles["geography"] = Profile("Météo des lieux", "city-unmatched", "city-ambiguous", "city-no-title");
        var assigned = snapshot.Distributions.FirstOrDefault(x => x.Key == "active-assignee");
        var unassignedRows = snapshot.Metric("without-technician")?.Rows.ToHashSet() ?? new HashSet<int>();
        var loads = assigned?.Buckets.Where(x => x.Rows.Any(row => !unassignedRows.Contains(row))).Select(x => (Bucket: x, Load: x.Rows.Count(row => !unassignedRows.Contains(row)))).Where(x => x.Load > 0).ToArray();
        if (assigned?.Available != true || loads is null || loads.Length < 2 || snapshot.Metric("active")?.Value is not double activeCount || activeCount <= 0)
            snapshot.WeatherProfiles["workload"] = new("unknown", "Météo de la charge · observation insuffisante", "◌", null, 0,
                "Deux techniciens affectés au moins et des statuts exploitables sont nécessaires pour décrire la concentration de charge. Les capacités, absences et temps de travail ne sont pas déduits du relevé.", Array.Empty<DashboardSignal>());
        else
        {
            var median = Percentile(loads.Select(x => (double)x.Load).Order().ToArray(), .5)!.Value;
            var concentrated = loads.Where(x => x.Load > 2 * median).SelectMany(x => x.Bucket.Rows).Distinct().ToArray();
            var signals = new[] {
                new DashboardSignal("Actifs dans une charge supérieure à deux médianes", concentrated.Length, (int)activeCount, (int)activeCount, 1, concentrated,
                    $"Médiane {median:0.##} tickets parmi {loads.Length} techniciens présents dans les affectations. Un ticket partagé est compté une seule fois dans ce signal."),
                new DashboardSignal("Actifs sans technicien", unassignedRows.Count, (int)activeCount, (int)activeCount, 1, unassignedRows.ToArray(), "Absence d’affectation explicite dans le relevé.") };
            var score = signals.Average(x => Math.Min(100, 100d * x.Count / x.Sample));
            var state = score >= settings.Weather.StormThreshold ? "storm" : score >= settings.Weather.RainThreshold ? "rain" : score >= settings.Weather.WatchThreshold ? "watch" : "sun";
            if (!snapshot.Complete && state == "sun") state = "partial";
            snapshot.WeatherProfiles["workload"] = new(state, "Météo de la charge observée" + WeatherSourceSuffix(snapshot), state switch { "storm" => "ϟ", "rain" => "☂", "watch" => "⛅", "sun" => "☀", _ => "◌" }, score, 2,
                "Moyenne des parts d’actifs sans technicien et présents dans une affectation supérieure à deux fois la médiane observée. Décrit une concentration ; ne mesure ni productivité, ni capacité, ni surcharge réelle. Les techniciens sans ticket dans ce relevé ne sont pas observés. " + WeatherSourceDescription(snapshot), signals);
        }
    }

    public static long EstimatedExportRows(DashboardSnapshot snapshot) => 4L + (snapshot.IsFiltered ? 2 : 0) + snapshot.Weather.Signals.Count + snapshot.Metrics.Count
        + snapshot.Distributions.Sum(x => (long)x.Buckets.Count + (x.Available ? 0 : 1)) + 3L * snapshot.Timeline.Count + snapshot.Columns.Count + snapshot.Warnings.Count
        + snapshot.WeatherProfiles.Where(x => x.Key != "overall").Sum(x => 1L + x.Value.Signals.Count)
        + snapshot.Matrices.Sum(x => Math.Max(1L, x.Cells.Count))
        + snapshot.BoxSeries.Sum(x => Math.Max(1L, 6L * x.Boxes.Count))
        + snapshot.Scatters.Sum(x => Math.Max(1L, 2L * x.Points.Count))
        + snapshot.History.Sum(x => Math.Max(1L, x.Points.Count));

    public static ExtractionResult ToExport(DashboardSnapshot snapshot)
    {
        var data = new ExtractionResult
        {
            Headers = new() { "Rubrique", "Indicateur", "Valeur", "Unité", "Échantillon / dénominateur", "Explication" },
            SourceUrl = snapshot.SourceUrl, ExtractedAt = snapshot.CalculatedAt, Title = "Bilan du relevé GLPI", Scope = snapshot.Scope,
            Complete = snapshot.Complete, Quality = snapshot.Quality, Warnings = new(snapshot.Warnings), Status = $"Bilan de {snapshot.RowCount:N0} {snapshot.Grain}"
        };
        void Add(params string[] values) => data.Rows.Add(values);
        Add("Périmètre", "Calcul", snapshot.CalculatedAt.ToString("O"), "", "", "Calcul local sur les lignes du relevé uniquement.");
        Add("Périmètre", "Extraction", snapshot.ExtractedAt.ToString("O"), "", "", snapshot.Scope);
        Add("Périmètre", snapshot.IsFiltered ? "Lignes du périmètre vérifié" : "Total annoncé", snapshot.ExpectedTotal?.ToString(CultureInfo.InvariantCulture) ?? "Non disponible", "lignes", "", snapshot.Complete ? "Relevé source complet confirmé" : snapshot.ReceptionComplete ? "Toutes les lignes annoncées sont reçues ; les réserves du relevé restent à vérifier" : "Relevé source incomplet ou non confirmé");
        if (snapshot.IsFiltered)
        {
            Add("Périmètre", "Vue sélectionnée", snapshot.ScopeLabel, "", "", "Toutes les valeurs de ce bilan sont recalculées sur ce périmètre du relevé.");
            Add("Périmètre", "Relevé source", snapshot.SourceRowCount.ToString(CultureInfo.InvariantCulture), "lignes récupérées", snapshot.SourceExpectedTotal?.ToString(CultureInfo.InvariantCulture) ?? "Total GLPI inconnu", "Un ticket affecté à plusieurs techniciens peut figurer dans plusieurs vues individuelles ; ne pas additionner leurs totaux.");
        }
        Add("Météo", snapshot.Weather.Title, snapshot.Weather.Score?.ToString("G", CultureInfo.InvariantCulture) ?? "Non disponible", "/100", snapshot.Weather.MeasuredWeight + " de pondérations disponibles", snapshot.Weather.Description);
        foreach (var signal in snapshot.Weather.Signals) Add("Météo", signal.Label, signal.Count.ToString(), snapshot.Grain, $"{signal.Sample} / {signal.Population} actifs", signal.Explanation);
        foreach (var metric in snapshot.Metrics) Add("Indicateurs", metric.Label, metric.Value?.ToString("G", CultureInfo.InvariantCulture) ?? "Non disponible", metric.Unit, metric.Sample?.ToString() ?? "", metric.Explanation);
        foreach (var dist in snapshot.Distributions)
        {
            if (!dist.Available) Add("Répartitions", dist.Title, "Non disponible", "", "", "Données nécessaires indisponibles ou sans échantillon exploitable. " + dist.Note);
            foreach (var bucket in dist.Buckets) Add(dist.Title, bucket.Label, bucket.IsAggregate ? bucket.Value?.ToString("G", CultureInfo.InvariantCulture) ?? "Non disponible" : bucket.Count.ToString(), bucket.IsAggregate ? bucket.Unit : snapshot.Grain, bucket.Sample?.ToString() ?? dist.Denominator.ToString(), dist.Note);
        }
        foreach (var day in snapshot.Timeline)
            foreach (var (role, title, values) in new[] { ("created", "Créations", day.Created), ("resolved", "Résolutions", day.Resolved), ("closed", "Clôtures", day.Closed) })
                Add("Activité datée sur 30 jours", day.Date.ToString("yyyy-MM-dd") + " · " + title, snapshot.Column(role)?.Index.HasValue == true ? values.Length.ToString() : "Non disponible", snapshot.Grain, "", "Événements présents dans le relevé seulement ; pas un historique global.");
        foreach (var profile in snapshot.WeatherProfiles.Where(x => x.Key != "overall").Select(x => x.Value))
        {
            Add("Météo spécialisée", profile.Title, profile.Score?.ToString("G", CultureInfo.InvariantCulture) ?? "Non disponible", "/100", profile.Signals.Count.ToString(), profile.Description);
            foreach (var signal in profile.Signals) Add(profile.Title, signal.Label, signal.Count.ToString(), snapshot.Grain, signal.Sample.ToString(), signal.Explanation);
        }
        foreach (var matrix in snapshot.Matrices)
        {
            if (matrix.Cells.Count == 0) Add("Matrice", matrix.Title, matrix.Available ? "Aucune occurrence" : "Non disponible", matrix.Unit, "0", matrix.Explanation);
            foreach (var cell in matrix.Cells) Add(matrix.Title, cell.RowLabel + " × " + cell.ColumnLabel, cell.Value.ToString("G", CultureInfo.InvariantCulture), matrix.Unit, cell.Rows.Length.ToString(), matrix.Explanation);
        }
        foreach (var series in snapshot.BoxSeries)
        {
            if (series.Boxes.Count == 0) Add("Dispersion", series.Title, "Non disponible", series.Unit, "0", series.Explanation);
            foreach (var box in series.Boxes)
                foreach (var (label, value) in new[] { ("Minimum", box.Minimum), ("Q1", box.FirstQuartile), ("Médiane", box.Median), ("Q3", box.ThirdQuartile), ("P90", box.P90), ("Maximum", box.Maximum) })
                    Add(series.Title, box.Label + " · " + label, value.ToString("G", CultureInfo.InvariantCulture), series.Unit, box.Sample.ToString(), series.Explanation);
        }
        foreach (var series in snapshot.Scatters)
        {
            if (series.Points.Count == 0) Add("Nuage de points", series.Title, "Non disponible", "", "0", series.Explanation);
            foreach (var point in series.Points)
            {
                Add(series.Title, point.Label + " · " + series.XLabel, point.X.ToString("G", CultureInfo.InvariantCulture), series.XLabel, "1", series.Explanation);
                Add(series.Title, point.Label + " · " + series.YLabel, point.Y.ToString("G", CultureInfo.InvariantCulture), series.YLabel, "1", series.Explanation);
            }
        }
        foreach (var series in snapshot.History)
        {
            if (series.Points.Count == 0) Add("Historique", series.Title, "Non disponible", series.Unit, "0", series.Explanation);
            foreach (var point in series.Points) Add(series.Title, point.CapturedAt.ToString("O"),
                point.Comparable && point.Complete && point.Value.HasValue ? point.Value.Value.ToString("G", CultureInfo.InvariantCulture) : "Non comparable",
                series.Unit, point.Rows.ToString(), point.Note + " " + series.Explanation);
        }
        foreach (var col in snapshot.Columns) Add("Correspondances", col.Label, col.Header, "", col.StateLabel, col.Index.HasValue ? "Colonne utilisée" : "Indisponible");
        foreach (var warning in snapshot.Warnings) Add("Qualité et limites", "Avertissement", "", "", "", warning);
        return data;
    }

    /// <summary>Shared conservative GLPI actor splitting for dashboards and technician workspaces.</summary>
    public static IReadOnlyList<string> SplitTechnicianNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        var parts = Regex.Split(value, @"\r\n|[\r\n;]|\s+\|\s+");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var part in parts)
        {
            var name = Regex.Replace(part.Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
            if (name.Length == 0 || IsMissing(name)) continue;
            if (seen.Add(name.ToUpperInvariant())) result.Add(name);
        }
        return result;
    }

    public static string NormalizeValue(string? value, string role, string header, DashboardSettings settings)
    {
        var raw = (value ?? "").Trim();
        if (settings.ValueDictionaries.TryGetValue(role, out var dictionary) || settings.ValueDictionaries.TryGetValue(header, out dictionary))
        {
            var match = dictionary.FirstOrDefault(p => string.Equals(p.Key, raw, StringComparison.OrdinalIgnoreCase));
            if (match.Key != null) return match.Value;
        }
        return raw;
    }
    public static bool TrySourceNumber(string raw, DashboardSettings settings, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 200) return false;
        try
        {
            var culture = settings.NumberCulture == "invariant" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(settings.NumberCulture);
            var text = raw.Trim().Replace('\u00a0', ' ').Replace('\u202f', ' ');
            var decimalSeparator = culture.NumberFormat.NumberDecimalSeparator;
            var groupSeparator = culture.NumberFormat.NumberGroupSeparator.Replace('\u00a0', ' ').Replace('\u202f', ' ');
            // Legacy exports use either decimal comma or decimal point in otherwise French cells.
            if (decimalSeparator == "," && groupSeparator != "." && text.Contains('.') && !text.Contains(',')) decimalSeparator = ".";
            var sign = ""; if (text[0] is '+' or '-') { sign = text[..1]; text = text[1..]; }
            var exponent = ""; var exponentAt = text.IndexOfAny(new[] { 'e', 'E' });
            if (exponentAt >= 0)
            {
                exponent = text[exponentAt..]; text = text[..exponentAt];
                if (!Regex.IsMatch(exponent, @"^[eE][+-]?[0-9]{1,4}$")) return false;
            }
            var pieces = text.Split(decimalSeparator, StringSplitOptions.None);
            if (pieces.Length > 2 || pieces.Length == 2 && (pieces[1].Length == 0 || pieces[1].Any(ch => ch is < '0' or > '9'))) return false;
            var integer = pieces[0].Length == 0 && pieces.Length == 2 ? "0" : pieces[0];
            var groups = groupSeparator.Length > 0 ? integer.Split(groupSeparator, StringSplitOptions.None) : new[] { integer };
            if (groups.Any(g => g.Length == 0 || g.Any(ch => ch is < '0' or > '9'))) return false;
            if (groups.Length > 1)
            {
                var sizes = culture.NumberFormat.NumberGroupSizes; var position = 0;
                for (var i = groups.Length - 1; i > 0; i--)
                {
                    var size = sizes[Math.Min(position++, sizes.Length - 1)]; if (size == 0 || groups[i].Length != size) return false;
                }
                var leading = sizes[Math.Min(position, sizes.Length - 1)]; if (leading != 0 && groups[0].Length > leading) return false;
            }
            var normalized = sign + string.Concat(groups) + (pieces.Length == 2 ? "." + pieces[1] : "") + exponent;
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }
        catch (CultureNotFoundException) { return false; }
    }
    public static bool TrySourceDate(string raw, DashboardSettings settings, TimeSpan referenceOffset, out DateTime value)
    {
        value = default;
        if (!TryDate(raw, settings.DateCulture, referenceOffset, out var parsed)) return false;
        if (string.IsNullOrWhiteSpace(settings.SourceTimeZone) || Regex.IsMatch(raw, @"(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)) { value = parsed; return true; }
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.SourceTimeZone);
            var wall = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(wall) || zone.IsAmbiguousTime(wall)) return false;
            value = new DateTimeOffset(wall, zone.GetUtcOffset(wall)).ToOffset(referenceOffset).DateTime; return true;
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return false; }
    }
    public static IReadOnlyList<string> SplitTechnicianNames(string? value, string separators)
    {
        if (separators == ";\n|" || string.IsNullOrEmpty(separators)) return SplitTechnicianNames(value);
        return (value ?? "").Split(separators.ToCharArray(), StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(v => !IsMissing(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int DateFieldBit(string key) => key switch { "created" => 1, "updated" => 2, "resolved" => 4, "closed" => 8, "due" => 16, _ => 0 };
    private static int TypeKind(string text) => Normalize(text) switch { "1" or "incident" => 1, "2" or "demande" or "request" => 2, _ => 0 };

    public static string Normalize(string? text)
    {
        var builder = new StringBuilder();
        foreach (var c in (text ?? "").Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }
    private static bool IsMissing(string? text) => Normalize(text) is "" or "aucun" or "aucune" or "none" or "null" or "non renseigne" or "non renseignee" or "non attribue" or "non attribuee" or "non affecte" or "non affectee" or "n a" or "n d" or "0";
    private static string NormalizeId(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 64) return "";
        var groups = text.Split(new[] { ' ', '\u00a0', '\u202f' }, StringSplitOptions.RemoveEmptyEntries);
        if (groups.Length > 1 && (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(group => group.Length != 3))) return "";
        var digits = string.Concat(groups);
        return digits.Length > 0 && digits.All(c => c is >= '0' and <= '9')
            && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture) : "";
    }
    public static int? Severity(string text) => Normalize(text) switch
    {
        "1" or "tres bas" or "tres basse" or "very low" => 1,
        "2" or "bas" or "basse" or "low" => 2,
        "3" or "moyen" or "moyenne" or "medium" => 3,
        "4" or "haut" or "haute" or "high" => 4,
        "5" or "tres haut" or "tres haute" or "very high" => 5,
        _ => null
    };
    private static string SeverityLabel(int? value, bool masculine) => !masculine ? PriorityLabel(value) : value switch
    { 1 => "Très bas", 2 => "Bas", 3 => "Moyen", 4 => "Haut", 5 => "Très haut", _ => "Non renseigné / inconnu" };
    public static int Status(string text) => Normalize(text) switch
    {
        "1" or "nouveau" or "new" => 1,
        "2" or "en cours attribue" or "processing assigned" or "assigned" or "attribue" => 2,
        "3" or "en cours planifie" or "processing planned" or "planned" or "planifie" => 3,
        "4" or "en attente" or "pending" or "waiting" => 4,
        "5" or "resolu" or "solved" or "resolved" => 5,
        "6" or "clos" or "closed" or "cloture" => 6,
        _ => 0
    };
    public static int? Priority(string text) => Normalize(text) switch
    {
        "1" or "tres basse" or "very low" => 1, "2" or "basse" or "low" => 2,
        "3" or "moyenne" or "medium" => 3, "4" or "haute" or "high" => 4,
        "5" or "tres haute" or "very high" => 5, "6" or "majeure" or "major" => 6, _ => null
    };
    public static string StatusLabel(int value) => value switch { 1 => "Nouveau", 2 => "En cours — attribué", 3 => "En cours — planifié", 4 => "En attente", 5 => "Résolu", 6 => "Clos", _ => "Non classé" };
    public static string PriorityLabel(int? value) => value switch { 1 => "Très basse", 2 => "Basse", 3 => "Moyenne", 4 => "Haute", 5 => "Très haute", 6 => "Majeure", _ => "Non renseignée / inconnue" };
    private static string TypeLabel(string text) => Normalize(text) switch { "1" or "incident" => "Incident", "2" or "demande" or "request" => "Demande", "" => "Non renseigné", _ => text };
    public static double? Percentile(double[] sorted, double probability)
    {
        if (sorted.Length == 0) return null;
        var rank = (sorted.Length - 1) * probability; var low = (int)Math.Floor(rank); var high = (int)Math.Ceiling(rank);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }
    public static bool TryDate(string raw, string cultureName, TimeSpan offset, out DateTime value)
    {
        value = default;
        raw = raw.Trim().Replace('\u00a0', ' ').Replace('\u202f', ' ');
        if (raw.Length > 100 || !Regex.IsMatch(raw, @"\b(?:19|20|21)\d{2}\b")) return false;
        if (Regex.IsMatch(raw, @"^\d{4}-\d{2}-\d{2}[T ].*(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var zoned))
        { value = zoned.ToOffset(offset).DateTime; return true; }
        var isoFormats = new[] { "yyyy-MM-dd", "yyyy-MM-dd H:mm", "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd H:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm" };
        if (DateTime.TryParseExact(raw, isoFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value)) return true;
        if (cultureName == "invariant") return false;
        var date = cultureName == "en-US" ? "M/d/yyyy" : "d/M/yyyy";
        var dateDash = cultureName == "en-US" ? "M-d-yyyy" : "d-M-yyyy";
        var formats = new[] { date, date + " H:mm", date + " H:mm:ss", date + " h:mm tt", date + " h:mm:ss tt", dateDash, dateDash + " H:mm", dateDash + " H:mm:ss", "d MMM yyyy", "d MMM yyyy H:mm", "d MMMM yyyy", "d MMMM yyyy H:mm" };
        return DateTime.TryParseExact(raw, formats, CultureInfo.GetCultureInfo(cultureName), DateTimeStyles.AllowWhiteSpaces, out value);
    }
}
