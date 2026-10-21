using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GlpiPilot.Core;

public sealed record QueryChoice(string Key, string Label);

public sealed class QueryLibrarySettings
{
    public const int MaximumQueries = 128;
    public List<SavedQuery> Queries { get; set; } = new();

    /// <summary>Copies drafts without changing their meaning. Validate before normalization on save/import.</summary>
    public QueryLibrarySettings Copy() => new()
    {
        Queries = Queries?.Select(query => query?.Copy()!).ToList()!
    };

    /// <summary>Recovers local configuration without weakening an invalid query into a broader query.</summary>
    public void Normalize()
    {
        var normalized = new List<SavedQuery>();
        var ids = new HashSet<Guid>();
        if (Queries is not null)
            foreach (var original in Queries.Take(MaximumQueries))
            {
                if (original is null || original.Conditions is null || original.Conditions.Count > SavedQuery.MaximumConditions) continue;
                var query = original.Copy();
                query.Name = QueryText.CleanLabel(query.Name, SavedQuery.MaximumNameLength);
                query.Description = QueryText.CleanLabel(query.Description, SavedQuery.MaximumDescriptionLength, true);
                query.Category = QueryText.CleanLabel(query.Category, SavedQuery.MaximumCategoryLength);
                if (!Guid.TryParseExact(query.Id, "N", out var id) || id == Guid.Empty || ids.Contains(id))
                { query.Id = Guid.NewGuid().ToString("N"); id = Guid.Parse(query.Id); }
                try { query.Validate(); }
                catch (InvalidDataException) { continue; }
                ids.Add(id); normalized.Add(query);
            }
        Queries = normalized;
    }

    public void Validate()
    {
        if (Queries is null || Queries.Count > MaximumQueries)
            throw new InvalidDataException($"La bibliothèque doit contenir au maximum {MaximumQueries} requêtes.");
        var ids = new HashSet<Guid>();
        foreach (var query in Queries)
        {
            if (query is null) throw new InvalidDataException("La bibliothèque contient une requête absente.");
            query.Validate();
            if (!ids.Add(Guid.ParseExact(query.Id, "N")))
                throw new InvalidDataException("Chaque requête doit avoir un identifiant unique dans la bibliothèque.");
        }
    }

    /// <summary>Adds only absent stable template IDs, without replacing user edits or favorites.</summary>
    public int AddMissingTemplates()
    {
        Validate();
        var existing = Queries.Select(query => query.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = CreateDefault().Queries.Where(query => !existing.Contains(query.Id))
            .Take(MaximumQueries - Queries.Count).Select(query => query.Copy()).ToList();
        Queries.AddRange(additions);
        return additions.Count;
    }

    public static QueryLibrarySettings CreateDefault()
    {
        var result = new QueryLibrarySettings();
        void Add(string key, string name, string category, string basis = "total", string groupBy = "",
            string description = "", QueryCondition? condition = null)
        {
            // Stable template identities survive copying, import/export and UI favorites.
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("GLPI-Pilot/query-template/v1/" + key));
            var query = new SavedQuery
            {
                Id = new Guid(hash.AsSpan(0, 16)).ToString("N"), Name = name, Category = category,
                BaseProbe = basis, GroupBy = groupBy,
                Description = description.Length > 0 ? description : "Répartition des lignes du périmètre courant, à partir des colonnes disponibles du relevé."
            };
            if (condition is not null) query.Conditions.Add(condition);
            result.Queries.Add(query);
        }
        foreach (var (key, label) in new[]
        {
            ("total", "Relevé"), ("active", "Actifs"), ("pending", "En attente"), ("high", "Priorités hautes"),
            ("overdue", "Échéances dépassées"), ("soon", "Échéances proches"), ("old", "Actifs anciens"),
            ("stale", "Sans modification récente"), ("unassigned", "Sans affectation"),
            ("without-technician", "Sans technicien"), ("due-today", "Échéances aujourd’hui")
        }) Add("city-" + key, label + " par ville", "Villes", key, "role:city",
            "Champ Ville configuré, natif ou déduit du titre. Dans ce dernier cas, il s’agit d’une inférence, sans géocodage. Les valeurs absentes restent dans un groupe explicite.");

        foreach (var (role, label) in new[] { ("entity", "entité"), ("location", "lieu"), ("postal", "code postal") })
        {
            Add(role + "-active", "Actifs par " + label, "Implantations", "active", "role:" + role);
            Add(role + "-overdue", "Retards par " + label, "Implantations", "overdue", "role:" + role,
                "Échéances calendaires dépassées parmi les dates connues, sans interprétation des pauses ou engagements SLA.");
        }
        foreach (var (basis, label) in new[]
        { ("active", "Actifs"), ("high", "Priorités hautes"), ("overdue", "Retards"), ("pending", "En attente"), ("stale", "Sans modification récente") })
            Add("technician-" + basis, label + " par technicien", "Techniciens", basis, "role:assignee",
                "Une ligne attribuée à plusieurs techniciens apparaît une fois par technicien. La somme des groupes peut donc dépasser le nombre de lignes.");

        Add("city-matched", "Ville identifiée dans le titre", "Qualité des villes", "city-matched", description: "Une ville unique correspond aux variantes configurées. La couverture en pourcentage est également disponible comme sonde native du dashboard.");
        Add("city-unmatched", "Ville non repérée dans le titre", "Qualité des villes", "city-unmatched");
        Add("city-ambiguous", "Plusieurs villes possibles", "Qualité des villes", "city-ambiguous");
        Add("city-no-title", "Titre indisponible pour la ville", "Qualité des villes", "city-no-title");
        Add("new-unassigned", "Nouveaux sans affectation par ville", "Opérations", "new-unassigned", "role:city");
        Add("high-overdue", "Hautes priorités en retard par ville", "Opérations", "high-overdue", "role:city");
        Add("high-stale", "Hautes priorités sans modification par ville", "Opérations", "high-stale", "role:city");
        Add("due-week", "Échéances de la semaine par ville", "Opérations", "due-week", "role:city");
        Add("missing-category", "Catégorie manquante par ville", "Qualité", "missing-category", "role:city");

        foreach (var (role, label) in new[] { ("created", "Ouverts"), ("resolved", "Résolus"), ("closed", "Clos") })
            Add(role + "-7", label + " sur 7 jours par ville", "Activité", "total", "role:city",
                "Dates présentes dans le relevé courant uniquement : ce comptage ne reconstitue pas les tickets absents du relevé ni un historique complet.",
                new() { Field = "role:" + role, Operator = "last-days", Value = "7" });
        Add("active-category", "Actifs par catégorie", "Organisation", "active", "role:category");
        Add("active-type", "Actifs par type", "Organisation", "active", "role:type");
        Add("active-group", "Actifs par groupe", "Organisation", "active", "role:group");
        Add("active-no-due", "Actifs sans échéance par ville", "Qualité", "active-no-due", "role:city");
        Add("multi-technician", "Attributions multiples par ville", "Organisation", "multi-technician", "role:city");
        Add("invalid-dates", "Dates illisibles par ville", "Qualité", "invalid-dates", "role:city");
        foreach (var (basis, label) in new[]
        {
            ("pending-old", "En attente · anciens"), ("pending-stale", "En attente · sans modification"),
            ("new-stale", "Nouveaux sans modification"), ("high-without-technician", "Hautes priorités sans technicien"),
            ("overdue-without-technician", "Retards sans technicien"), ("group-only", "Affectés à un groupe seul"),
            ("planned-overdue", "Planifiés à échéance dépassée"), ("old-without-technician", "Anciens sans technicien"),
            ("active-no-category", "Actifs sans catégorie"), ("resolved-aging", "Résolus · clôture à suivre"),
            ("resolved-no-date", "Résolus sans date"), ("high-incidents", "Incidents en priorité haute")
        }) Add("operations-city-" + basis, label + " par ville", "Suivi opérationnel", basis, "role:city",
            "Cohorte calculée avec les colonnes disponibles et les seuils du dashboard. Les dates sont calendaires ; les transitions de statut, pauses SLA et temps de travail ne sont pas reconstitués.");
        foreach (var (basis, label) in new[]
        {
            ("pending-old", "En attente · anciens"), ("pending-stale", "En attente · sans modification"),
            ("new-stale", "Nouveaux sans modification"), ("planned-overdue", "Planifiés à échéance dépassée"),
            ("active-no-category", "Actifs sans catégorie"), ("resolved-aging", "Résolus · clôture à suivre"),
            ("high-incidents", "Incidents en priorité haute")
        }) Add("operations-technician-" + basis, label + " par technicien", "Suivi techniciens", basis, "role:assignee",
            "Liste de suivi du relevé courant, sans classement de performance. Une attribution multiple apparaît dans chaque groupe de technicien concerné.");
        foreach (var (basis, label) in new[]
        {
            ("group-only", "Affectés à un groupe seul"), ("high-without-technician", "Hautes priorités sans technicien"),
            ("overdue-without-technician", "Retards sans technicien"), ("old-without-technician", "Anciens sans technicien"),
            ("resolved-aging", "Résolus · clôture à suivre")
        }) Add("operations-group-" + basis, label + " par groupe", "Suivi des groupes", basis, "role:group");
        result.Validate();
        return result;
    }
}

public sealed class SavedQuery
{
    public const int MaximumConditions = 20;
    public const int MaximumNameLength = 120;
    public const int MaximumDescriptionLength = 1200;
    public const int MaximumCategoryLength = 80;
    public const int MaximumFieldLength = 512;
    public const int MaximumValueLength = 500;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nouvelle requête";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "Personnalisées";
    public string BaseProbe { get; set; } = "total";
    public string Match { get; set; } = "all";
    public string Aggregate { get; set; } = "count";
    public string AggregateField { get; set; } = "";
    public string GroupBy { get; set; } = "";
    public bool Favorite { get; set; }
    public List<QueryCondition> Conditions { get; set; } = new();

    public SavedQuery Copy() => new()
    {
        Id = Id, Name = Name, Description = Description, Category = Category,
        BaseProbe = BaseProbe, Match = Match, Aggregate = Aggregate, AggregateField = AggregateField,
        GroupBy = GroupBy, Favorite = Favorite,
        Conditions = Conditions?.Select(condition => condition?.Copy()!).ToList()!
    };

    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out var id) || id == Guid.Empty)
            throw new InvalidDataException("La requête doit avoir un identifiant GUID de 32 caractères, non vide.");
        if (!QueryText.Valid(Name, MaximumNameLength, false) || !QueryText.Valid(Description, MaximumDescriptionLength, true, true)
            || !QueryText.Valid(Category, MaximumCategoryLength, true))
            throw new InvalidDataException($"La requête doit avoir un nom de {MaximumNameLength} caractères au maximum, une description de {MaximumDescriptionLength} et une catégorie de {MaximumCategoryLength}.");
        if (!QueryCatalog.BaseProbes.Any(choice => choice.Key == BaseProbe))
            throw new InvalidDataException("La base de la requête doit être une cohorte native de lignes, jamais une autre requête.");
        if (Match is not ("all" or "any") || Aggregate is not ("count" or "percent" or "distinct" or "sum" or "average" or "min" or "max"))
            throw new InvalidDataException("Le mode de combinaison ou l’agrégat de la requête n’est pas reconnu.");
        if (!QueryText.ValidField(AggregateField, Aggregate is "count" or "percent") || !QueryText.ValidField(GroupBy, true))
            throw new InvalidDataException("Le champ d’agrégat et le regroupement doivent désigner un rôle connu ou un en-tête exact.");
        if (Conditions is null || Conditions.Count > MaximumConditions)
            throw new InvalidDataException($"Une requête contient au maximum {MaximumConditions} conditions.");
        for (var index = 0; index < Conditions.Count; index++)
        {
            var condition = Conditions[index];
            if (condition is null || !QueryText.ValidField(condition.Field, false)
                || !QueryCatalog.Operators.Contains(condition.Operator)
                || !QueryText.Valid(condition.Value, MaximumValueLength, true, true)
                || !QueryText.Valid(condition.Value2, MaximumValueLength, true, true))
                throw new InvalidDataException($"Condition {index + 1} : champ, opérateur ou valeur invalide.");
            if (condition.Operator is not ("empty" or "not-empty") && string.IsNullOrWhiteSpace(condition.Value))
                throw new InvalidDataException($"Condition {index + 1} : une valeur est nécessaire pour cet opérateur.");
            if (condition.Operator.StartsWith("number-", StringComparison.Ordinal)
                && !QueryText.TryNumber(condition.Value, out _))
                throw new InvalidDataException($"Condition {index + 1} : utilisez un nombre fini, avec virgule ou point décimal et espaces éventuels de groupement.");
            if (condition.Operator is "last-days" or "next-days" or "age-at-least"
                && (!QueryText.TryNumber(condition.Value, out var days) || days is < 0 or > 36500))
                throw new InvalidDataException($"Condition {index + 1} : le nombre de jours doit être compris entre 0 et 36 500.");
        }
    }
}

public sealed class QueryCondition
{
    public string Field { get; set; } = "role:title";
    public string Operator { get; set; } = "contains";
    public string Value { get; set; } = "";
    public string Value2 { get; set; } = "";
    public QueryCondition Copy() => new() { Field = Field, Operator = Operator, Value = Value, Value2 = Value2 };
}

public sealed class QueryResult
{
    public string QueryId { get; init; } = "";
    public bool Available { get; init; }
    public string Message { get; init; } = "";
    public int[] Rows { get; init; } = Array.Empty<int>();
    public DashboardMetric Metric { get; init; } = new("", "", null, "", "", Array.Empty<int>());
    public DashboardDistribution? Distribution { get; init; }
}

internal static class QueryCatalog
{
    public static readonly IReadOnlyList<QueryChoice> BaseProbes = new QueryChoice[]
    {
        new("total", "Toutes les lignes du périmètre"), new("active", "Actifs reconnus"),
        new("new", "Nouveaux"), new("processing", "En cours"), new("pending", "En attente"),
        new("resolved", "Résolus"), new("closed", "Clos"), new("unknown-status", "Statuts non classés"),
        new("high", "Priorités hautes actives"), new("major", "Priorités majeures actives"),
        new("high-urgency", "Urgences hautes actives"), new("high-impact", "Impacts hauts actifs"),
        new("unassigned", "Actifs sans affectation disponible"), new("without-technician", "Actifs sans technicien"),
        new("without-group", "Actifs sans groupe"), new("multi-technician", "Actifs avec plusieurs techniciens"),
        new("overdue", "Actifs à échéance dépassée"), new("soon", "Actifs à échéance proche"),
        new("stale", "Actifs sans modification récente"), new("old", "Actifs anciens"),
        new("active-no-due", "Actifs sans échéance renseignée"), new("due-today", "Échéances aujourd’hui"),
        new("due-tomorrow", "Échéances demain"), new("due-week", "Échéances restantes de la semaine"),
        new("new-unassigned", "Nouveaux sans technicien ni groupe"), new("high-overdue", "Hautes priorités à échéance dépassée"),
        new("high-stale", "Hautes priorités sans modification récente"),
        new("pending-old", "En attente · tickets anciens"), new("pending-stale", "En attente · sans modification récente"),
        new("new-stale", "Nouveaux sans modification récente"), new("high-without-technician", "Hautes priorités sans technicien"),
        new("overdue-without-technician", "Retards sans technicien"), new("group-only", "Actifs affectés à un groupe seul"),
        new("planned-overdue", "Planifiés à échéance dépassée"), new("old-without-technician", "Tickets anciens sans technicien"),
        new("active-no-category", "Actifs sans catégorie"), new("resolved-aging", "Résolus · clôture à suivre"),
        new("resolved-no-date", "Résolus sans date de résolution"), new("high-incidents", "Incidents actifs en priorité haute"),
        new("missing-category", "Catégorie non renseignée"), new("missing-requester", "Demandeur non renseigné"),
        new("duplicate", "Identifiants de ticket répétés"), new("missing-id", "Identifiant inexploitable"),
        new("invalid-dates", "Dates illisibles"), new("future-dates", "Dates historiques futures"),
        new("inconsistent-dates", "Chronologies incohérentes"), new("active-incidents", "Incidents actifs"),
        new("active-requests", "Demandes actives"), new("city-matched", "Ville unique reconnue dans le titre"),
        new("city-unmatched", "Ville non repérée dans le titre"), new("city-ambiguous", "Ville ambiguë dans le titre"),
        new("city-no-title", "Titre indisponible pour le repérage de ville")
    };
    public static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "contains", "not-contains", "equals", "not-equals", "starts-with", "empty", "not-empty",
        "number-eq", "number-gt", "number-gte", "number-lt", "number-lte",
        "date-before", "date-after", "last-days", "next-days", "age-at-least"
    };
}

internal static class QueryText
{
    public static bool Valid(string? value, int maximum, bool allowEmpty, bool allowLines = false)
    {
        if (value is null || value.Length > maximum || !allowEmpty && string.IsNullOrWhiteSpace(value)) return false;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (char.IsControl(ch) && !(allowLines && ch is '\r' or '\n' or '\t')) return false;
            if (char.IsHighSurrogate(ch))
            { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(ch)) return false;
        }
        return true;
    }

    public static bool ValidField(string? field, bool allowEmpty)
    {
        if (!Valid(field, SavedQuery.MaximumFieldLength, allowEmpty, true)) return false;
        if (field!.Length == 0) return allowEmpty;
        return field.StartsWith("role:", StringComparison.Ordinal)
            ? DashboardService.Roles.Any(role => role.Key == field[5..])
            : field.StartsWith("column:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(field[7..]);
    }

    public static string CleanLabel(string? text, int maximum, bool allowLines = false)
    {
        if (text is null) return "";
        var buffer = new StringBuilder(Math.Min(text.Length, maximum));
        foreach (var rune in text.EnumerateRunes())
        {
            var value = Rune.IsControl(rune) && !(allowLines && rune.Value is '\r' or '\n' or '\t')
                || rune == Rune.ReplacementChar ? " " : rune.ToString();
            if (buffer.Length + value.Length > maximum) break;
            buffer.Append(value);
        }
        return buffer.ToString().Trim();
    }

    /// <summary>Decimal comma or point; only spaces may group thousands, so 1,234 always means 1.234.</summary>
    public static bool TryNumber(string? raw, out double value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 200) return false;
        var text = raw.Trim().Replace('\u00a0', ' ').Replace('\u202f', ' ');
        var sign = "";
        if (text[0] is '+' or '-') { sign = text[..1]; text = text[1..]; }
        if (text.Length == 0) return false;
        var decimalAt = -1;
        for (var index = 0; index < text.Length; index++)
            if (text[index] is '.' or ',')
            { if (decimalAt >= 0) return false; decimalAt = index; }
        var integer = decimalAt >= 0 ? text[..decimalAt] : text;
        var fraction = decimalAt >= 0 ? text[(decimalAt + 1)..] : "";
        if (decimalAt >= 0 && (fraction.Length == 0 || fraction.Any(ch => ch is < '0' or > '9'))) return false;
        if (integer.Length == 0) integer = "0";
        var groups = integer.Split(' ');
        if (groups.Any(group => group.Length == 0 || group.Any(ch => ch is < '0' or > '9'))
            || groups.Length > 1 && (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(group => group.Length != 3))) return false;
        var normalized = sign + string.Concat(groups) + (decimalAt >= 0 ? "." + fraction : "");
        return double.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
}
