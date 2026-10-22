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
        foreach (var (role, label) in new[] { ("city", "ville"), ("assignee", "technicien") })
        {
            foreach (var (aggregate, title) in new[] { ("median", "Ancienneté médiane"), ("percentile", "P90 d’ancienneté") })
            {
                Add("advanced-age-" + aggregate + "-" + role, title + " des actifs par " + label, "Délais avancés", "active", "role:" + role,
                    "Âge calendaire au moment du calcul, en jours. Échantillons numériques valides uniquement ; aucune durée SLA ou de travail n’est reconstituée.");
                var query = result.Queries[^1]; query.Aggregate = aggregate; query.AggregateField = "calc:age";
                query.ComputedFields.Add(new() { Key = "age", Label = "Âge calendaire", Function = "age-days", Field = "role:created", Unit = "jours" });
            }
            foreach (var (aggregate, title) in new[] { ("average", "Délai moyen"), ("median", "Délai médian") })
            {
                Add("advanced-resolution-" + aggregate + "-" + role, title + " création–résolution par " + label, "Délais avancés", "total", "role:" + role,
                    "Différence calendaire des dates de création et résolution présentes. Les durées négatives sont exclues. Ce délai ne représente pas le temps travaillé ni un engagement SLA.");
                var query = result.Queries[^1]; query.Aggregate = aggregate; query.AggregateField = "calc:resolution";
                query.ComputedFields.Add(new() { Key = "resolution", Label = "Création à résolution", Function = "days-between", Field = "role:created", OtherField = "role:resolved", Unit = "jours" });
                query.Conditions.Add(new() { Field = "calc:resolution", Operator = "number-gte", Value = "0" });
            }
        }
        foreach (var (first, second, title) in new[]
        {
            ("city", "assignee", "Actifs · ville puis technicien"), ("city", "category", "Actifs · ville puis catégorie"),
            ("group", "priority", "Actifs · groupe puis priorité"), ("created", "city", "Créations du relevé · mois puis ville")
        })
        {
            Add("advanced-hierarchy-" + first + "-" + second, title, "Analyses croisées", first == "created" ? "total" : "active", "role:" + first);
            var query = result.Queries[^1]; query.GroupBySecondary = "role:" + second;
            if (first == "created") { query.GroupDateBucket = "month"; query.Description = "Dates de création des tickets présents dans le relevé, regroupées par mois. Les tickets absents ne sont pas reconstitués."; }
        }
        foreach (var (role, title, op, type, initial) in new[]
        {
            ("city", "Actifs d’une ville choisie", "equals", "text", ""),
            ("assignee", "Actifs d’un technicien choisi", "equals", "text", ""),
            ("title", "Tickets contenant un terme choisi", "contains", "text", ""),
            ("created", "Tickets créés dans les N derniers jours", "last-days", "number", "7")
        })
        {
            Add("advanced-parameter-" + role, title, "Requêtes à paramètres", role is "city" or "assignee" ? "active" : "total", "role:category",
                "Renseignez le paramètre dans l’atelier. Un widget utilise la valeur par défaut enregistrée ; un paramètre vide reste indisponible.");
            var query = result.Queries[^1]; query.Parameters.Add(new() { Key = "choix", Label = role == "created" ? "Nombre de jours" : "Valeur recherchée", Type = type, DefaultValue = initial });
            query.Conditions.Add(new() { Field = "role:" + role, Operator = op, ValueSource = "parameter", Value = "choix" });
        }
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
    public string ConditionExpression { get; set; } = "";
    public List<QueryParameter> Parameters { get; set; } = new();
    public List<QueryComputedField> ComputedFields { get; set; } = new();
    public string GroupBySecondary { get; set; } = "";
    public string GroupByTertiary { get; set; } = "";
    public string GroupDateBucket { get; set; } = "none";
    public string SecondaryDateBucket { get; set; } = "none";
    public string TertiaryDateBucket { get; set; } = "none";
    public double Percentile { get; set; } = 90;
    public List<QueryCondition> Conditions { get; set; } = new();

    public SavedQuery Copy() => new()
    {
        Id = Id, Name = Name, Description = Description, Category = Category,
        BaseProbe = BaseProbe, Match = Match, Aggregate = Aggregate, AggregateField = AggregateField,
        GroupBy = GroupBy, Favorite = Favorite, ConditionExpression = ConditionExpression,
        Parameters = Parameters?.Select(parameter => parameter?.Copy()!).ToList()!,
        ComputedFields = ComputedFields?.Select(field => field?.Copy()!).ToList()!,
        GroupBySecondary = GroupBySecondary, GroupByTertiary = GroupByTertiary,
        GroupDateBucket = GroupDateBucket, SecondaryDateBucket = SecondaryDateBucket, TertiaryDateBucket = TertiaryDateBucket,
        Percentile = Percentile,
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
        if (Match is not ("all" or "any") || !QueryCatalog.Aggregates.Contains(Aggregate))
            throw new InvalidDataException("Le mode de combinaison ou l’agrégat de la requête n’est pas reconnu.");
        if (!double.IsFinite(Percentile) || Percentile is < 0 or > 100)
            throw new InvalidDataException("Le percentile doit être compris entre 0 et 100.");
        if (!QueryText.ValidField(AggregateField, Aggregate is "count" or "percent")
            || !QueryText.ValidField(GroupBy, true) || !QueryText.ValidField(GroupBySecondary, true) || !QueryText.ValidField(GroupByTertiary, true))
            throw new InvalidDataException("Les champs de calcul et regroupement doivent désigner un rôle, une colonne exacte ou un champ calculé.");
        if (GroupBy.Length == 0 && (GroupBySecondary.Length > 0 || GroupByTertiary.Length > 0)
            || GroupBySecondary.Length == 0 && GroupByTertiary.Length > 0)
            throw new InvalidDataException("Les regroupements doivent être renseignés dans l’ordre : premier, deuxième, troisième.");
        if (new[] { GroupDateBucket, SecondaryDateBucket, TertiaryDateBucket }.Any(bucket => !QueryCatalog.DateBuckets.Contains(bucket)))
            throw new InvalidDataException("Une période de regroupement n’est pas reconnue.");
        var groupFields = new[] { GroupBy, GroupBySecondary, GroupByTertiary }.Where(field => field.Length > 0).ToArray();
        if (groupFields.Distinct(StringComparer.Ordinal).Count() != groupFields.Length)
            throw new InvalidDataException("Un champ ne peut apparaître qu’une fois dans les niveaux de regroupement.");
        if (Parameters == null || Parameters.Count > 12 || ComputedFields == null || ComputedFields.Count > 12)
            throw new InvalidDataException("Une requête accepte au maximum 12 paramètres et 12 champs calculés.");
        var parameterKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in Parameters)
        {
            if (parameter == null || !QueryText.Identifier(parameter.Key) || !parameterKeys.Add(parameter.Key)
                || !QueryText.Valid(parameter.Label, 120, true) || !QueryText.Valid(parameter.DefaultValue, MaximumValueLength, true, true)
                || parameter.Type is not ("text" or "number" or "date"))
                throw new InvalidDataException("Paramètre invalide : identifiant unique, libellé et type texte/nombre/date requis.");
            if (parameter.DefaultValue.Length > 0 && parameter.Type == "number" && !QueryText.TryNumber(parameter.DefaultValue, out _))
                throw new InvalidDataException("La valeur par défaut du paramètre « " + parameter.Key + " » doit être un nombre fini.");
        }
        var computedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in ComputedFields)
        {
            if (field == null || !QueryText.Identifier(field.Key) || !computedKeys.Add(field.Key)
                || !QueryText.Valid(field.Label, 120, false) || !QueryText.Valid(field.Constant, MaximumValueLength, true, true)
                || !QueryText.Valid(field.Unit, 40, true) || !QueryCatalog.Functions.Contains(field.Function)
                || !QueryText.ValidField(field.Field, false) || !QueryText.ValidField(field.OtherField, true))
                throw new InvalidDataException("Champ calculé invalide : nom unique, fonction autorisée et champs d’entrée requis.");
            if (field.Function is "days-between" or "concat" && field.OtherField.Length == 0)
                throw new InvalidDataException("La fonction « " + field.Function + " » exige deux champs.");
            if (field.Function is "add" or "subtract" or "multiply" or "divide" && field.OtherField.Length == 0
                && !QueryText.TryNumber(field.Constant, out _))
                throw new InvalidDataException("Un calcul numérique exige un second champ ou une constante numérique finie.");
        }
        void RequireComputed(string field)
        {
            if (field.StartsWith("calc:", StringComparison.Ordinal) && !computedKeys.Contains(field[5..]))
                throw new InvalidDataException("Le champ calculé « " + field[5..] + " » n’est pas défini dans cette requête.");
        }
        foreach (var field in new[] { AggregateField, GroupBy, GroupBySecondary, GroupByTertiary }) RequireComputed(field);
        var checking = new HashSet<string>(StringComparer.Ordinal); var dependencyDepths = new Dictionary<string, int>(StringComparer.Ordinal);
        int CheckDependencies(string key)
        {
            if (dependencyDepths.TryGetValue(key, out var knownDepth)) return knownDepth;
            if (!checking.Add(key)) throw new InvalidDataException("Les champs calculés contiennent un cycle ou plus de 8 niveaux de dépendances.");
            var field = ComputedFields.First(value => value.Key == key); var depth = 1;
            foreach (var input in new[] { field.Field, field.OtherField })
            {
                RequireComputed(input);
                if (input.StartsWith("calc:", StringComparison.Ordinal)) depth = Math.Max(depth, 1 + CheckDependencies(input[5..]));
            }
            if (depth > 8) throw new InvalidDataException("Les champs calculés contiennent un cycle ou plus de 8 niveaux de dépendances.");
            checking.Remove(key); dependencyDepths.Add(key, depth); return depth;
        }
        foreach (var key in computedKeys) CheckDependencies(key);
        if (Conditions is null || Conditions.Count > MaximumConditions)
            throw new InvalidDataException($"Une requête contient au maximum {MaximumConditions} conditions.");
        for (var index = 0; index < Conditions.Count; index++)
        {
            var condition = Conditions[index];
            if (condition is null || !QueryText.ValidField(condition.Field, false)
                || !QueryCatalog.Operators.Contains(condition.Operator)
                || !QueryText.Valid(condition.Value, MaximumValueLength, true, true)
                || !QueryText.Valid(condition.Value2, MaximumValueLength, true, true)
                || condition.ValueSource is not ("literal" or "field" or "parameter")
                || condition.Value2Source is not ("literal" or "field" or "parameter"))
                throw new InvalidDataException($"Condition {index + 1} : champ, opérateur, source ou valeur invalide.");
            RequireComputed(condition.Field);
            if (condition.Operator is "empty" or "not-empty") continue;
            void ValidateOperand(string source, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"Condition {index + 1} : une valeur est nécessaire.");
                if (source == "field")
                {
                    if (!QueryText.ValidField(value, false)) throw new InvalidDataException($"Condition {index + 1} : le champ de comparaison est invalide.");
                    RequireComputed(value);
                }
                if (source == "parameter" && !parameterKeys.Contains(value))
                    throw new InvalidDataException($"Condition {index + 1} : paramètre « {value} » non défini.");
            }
            ValidateOperand(condition.ValueSource, condition.Value);
            var range = condition.Operator is "number-between" or "number-outside" or "date-between" or "date-outside";
            if (range) ValidateOperand(condition.Value2Source, condition.Value2);
            if (condition.Operator.StartsWith("number-", StringComparison.Ordinal))
            {
                if (condition.ValueSource == "literal" && !QueryText.TryNumber(condition.Value, out _)
                    || range && condition.Value2Source == "literal" && !QueryText.TryNumber(condition.Value2, out _))
                    throw new InvalidDataException($"Condition {index + 1} : utilisez un nombre fini, avec virgule ou point décimal.");
            }
            if (condition.Operator is "last-days" or "next-days" or "age-at-least"
                && condition.ValueSource == "literal" && (!QueryText.TryNumber(condition.Value, out var days) || days is < 0 or > 36500))
                throw new InvalidDataException($"Condition {index + 1} : le nombre de jours doit être compris entre 0 et 36 500.");
        }
        _ = QueryLogic.Parse(ConditionExpression, Conditions.Count, Match);

    }
}

public sealed class QueryCondition
{
    public string Field { get; set; } = "role:title";
    public string Operator { get; set; } = "contains";
    public string Value { get; set; } = "";
    public string Value2 { get; set; } = "";
    public string ValueSource { get; set; } = "literal";
    public string Value2Source { get; set; } = "literal";
    public QueryCondition Copy() => new() { Field = Field, Operator = Operator, Value = Value, Value2 = Value2,
        ValueSource = ValueSource, Value2Source = Value2Source };
}

public sealed class QueryResult
{
    public string QueryId { get; init; } = "";
    public bool Available { get; init; }
    public string Message { get; init; } = "";
    public int[] Rows { get; init; } = Array.Empty<int>();
    public DashboardMetric Metric { get; init; } = new("", "", null, "", "", Array.Empty<int>());
    public DashboardDistribution? Distribution { get; init; }
    public IReadOnlyList<QueryGroupResult> Groups { get; init; } = Array.Empty<QueryGroupResult>();
    public int UnknownRows { get; init; }
}

internal static class QueryCatalog
{
    public static readonly HashSet<string> Aggregates = new(StringComparer.Ordinal) { "count", "percent", "distinct", "sum", "average", "min", "max", "median", "percentile" };
    public static readonly HashSet<string> DateBuckets = new(StringComparer.Ordinal) { "none", "day", "week", "month", "quarter", "year" };
    public static readonly HashSet<string> Functions = new(StringComparer.Ordinal) { "add", "subtract", "multiply", "divide", "days-between", "age-days", "length", "lower", "concat", "coalesce" };
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
        "number-eq", "number-gt", "number-gte", "number-lt", "number-lte", "number-between", "number-outside",
        "date-before", "date-after", "date-between", "date-outside", "last-days", "next-days", "age-at-least", "in", "not-in", "ends-with"
    };
}

internal static class QueryText
{
    public static bool Identifier(string? value) => value is { Length: > 0 and <= 40 }
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
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
            : field.StartsWith("calc:", StringComparison.Ordinal) ? Identifier(field[5..])
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

public sealed class QueryParameter
{
    public string Key { get; set; } = "parametre";
    public string Label { get; set; } = "Paramètre";
    public string Type { get; set; } = "text";
    public string DefaultValue { get; set; } = "";
    public QueryParameter Copy() => (QueryParameter)MemberwiseClone();
}

public sealed class QueryComputedField
{
    public string Key { get; set; } = "calcul";
    public string Label { get; set; } = "Champ calculé";
    public string Function { get; set; } = "age-days";
    public string Field { get; set; } = "role:created";
    public string OtherField { get; set; } = "";
    public string Constant { get; set; } = "";
    public string Unit { get; set; } = "";
    public QueryComputedField Copy() => (QueryComputedField)MemberwiseClone();
}

public sealed record QueryGroupResult(string Label, string[] Path, int Count, double? Value, string Unit,
    int Sample, int[] Rows, int[] ContributionRows, int Denominator);
public sealed record QueryConditionExplanation(int Number, string Field, string Operator, string Value,
    string Comparison, string Decision, string Explanation);
public sealed class QueryRowExplanation
{
    public int Row { get; init; }
    public bool WithinBase { get; init; }
    public string Decision { get; init; } = "unknown";
    public string Logic { get; init; } = "";
    public string Message { get; init; } = "";
    public IReadOnlyList<QueryConditionExplanation> Conditions { get; init; } = Array.Empty<QueryConditionExplanation>();
}

/// <summary>A bounded boolean grammar over numbered conditions, never executable code.</summary>
public static class QueryLogic
{
    public sealed record Node(string Kind, int Index, IReadOnlyList<Node> Children)
    {
        public bool? Evaluate(IReadOnlyList<bool?> values)
        {
            if (Kind == "leaf") return values[Index];
            if (Kind == "not") { var value = Children[0].Evaluate(values); return value.HasValue ? !value.Value : null; }
            if (Children.Count == 0) return true;
            var unknown = false;
            foreach (var child in Children)
            {
                var value = child.Evaluate(values);
                if (!value.HasValue) unknown = true;
                else if (Kind == "all" && !value.Value) return false;
                else if (Kind == "any" && value.Value) return true;
            }
            return unknown ? null : Kind == "all";
        }
        public string Describe() => Kind switch
        {
            "leaf" => (Index + 1).ToString(CultureInfo.InvariantCulture),
            "not" => "NON (" + Children[0].Describe() + ")",
            _ => Children.Count == 0 ? "Toutes les lignes" : "(" + string.Join(Kind == "all" ? " ET " : " OU ", Children.Select(child => child.Describe())) + ")"
        };
    }

    public static Node Parse(string? expression, int conditionCount, string fallbackMatch = "all")
    {
        if (conditionCount is < 0 or > SavedQuery.MaximumConditions || fallbackMatch is not ("all" or "any"))
            throw new InvalidDataException("La combinaison des conditions est invalide.");
        if (expression is null || expression.Length > 1024 || expression.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character)))
            throw new InvalidDataException("L’expression logique doit contenir au maximum 1 024 caractères.");
        if (string.IsNullOrWhiteSpace(expression))
            return new(fallbackMatch, -1, Enumerable.Range(0, conditionCount).Select(index => new Node("leaf", index, Array.Empty<Node>())).ToArray());
        var parser = new Parser(expression, conditionCount);
        return parser.Parse();
    }

    private sealed class Parser
    {
        private readonly List<string> _tokens = new();
        private readonly int _conditionCount;
        private readonly HashSet<int> _used = new();
        private int _position, _nodes;
        public Parser(string expression, int count)
        {
            _conditionCount = count;
            for (var index = 0; index < expression.Length;)
            {
                if (char.IsWhiteSpace(expression[index])) { index++; continue; }
                var start = index;
                if (expression[index] is '(' or ')') index++;
                else if (char.IsAsciiDigit(expression[index])) { while (index < expression.Length && char.IsAsciiDigit(expression[index])) index++; }
                else if (char.IsAsciiLetter(expression[index])) { while (index < expression.Length && char.IsAsciiLetter(expression[index])) index++; }
                else throw new InvalidDataException("L’expression accepte les numéros de conditions, ET, OU, NON et les parenthèses uniquement.");
                var token = expression[start..index].ToUpperInvariant();
                _tokens.Add(token switch { "AND" => "ET", "OR" => "OU", "NOT" => "NON", _ => token });
                if (_tokens.Count > 192) throw new InvalidDataException("L’expression logique contient trop d’éléments.");
            }
        }
        public Node Parse()
        {
            var result = Or(0);
            if (_position != _tokens.Count) throw Error("Élément inattendu");
            var missing = Enumerable.Range(0, _conditionCount).Where(index => !_used.Contains(index)).ToArray();
            if (missing.Length > 0) throw new InvalidDataException("L’expression n’utilise pas les conditions " + string.Join(", ", missing.Select(index => index + 1)) + ". Ajoutez-les à l’expression ou retirez-les de la liste.");
            return result;
        }
        private Node Or(int depth)
        {
            var nodes = new List<Node> { And(depth) };
            while (Take("OU")) nodes.Add(And(depth));
            return nodes.Count == 1 ? nodes[0] : Make("any", -1, nodes);
        }
        private Node And(int depth)
        {
            var nodes = new List<Node> { Unary(depth) };
            while (Take("ET")) nodes.Add(Unary(depth));
            return nodes.Count == 1 ? nodes[0] : Make("all", -1, nodes);
        }
        private Node Unary(int depth)
        {
            if (depth > 8) throw new InvalidDataException("L’expression logique est limitée à huit niveaux de parenthèses ou de négations.");
            if (Take("NON")) return Make("not", -1, new[] { Unary(depth + 1) });
            if (Take("("))
            {
                var node = Or(depth + 1);
                if (!Take(")")) throw Error("Parenthèse fermante attendue");
                return node;
            }
            if (_position >= _tokens.Count || !int.TryParse(_tokens[_position++], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number < 1 || number > _conditionCount)
                throw Error("Numéro de condition absent ou hors limites");
            _used.Add(number - 1); return Make("leaf", number - 1, Array.Empty<Node>());
        }
        private bool Take(string token)
        {
            if (_position >= _tokens.Count || _tokens[_position] != token) return false;
            _position++; return true;
        }
        private Node Make(string kind, int index, IReadOnlyList<Node> children)
        {
            if (++_nodes > 128) throw new InvalidDataException("L’expression logique contient trop de branches.");
            return new(kind, index, children);
        }
        private InvalidDataException Error(string message) => new(message + $" dans l’expression logique (élément {_position + 1}).");
    }
}
