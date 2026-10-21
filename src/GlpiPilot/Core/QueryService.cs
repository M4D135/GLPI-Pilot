using System.IO;

namespace GlpiPilot.Core;

/// <summary>Typed local filters over a snapshot. No SQL, expressions, regular expressions or network access.</summary>
public static class QueryService
{
    public static IReadOnlyList<QueryChoice> BaseProbes => QueryCatalog.BaseProbes;
    public static string MetricKey(string id) => "query:" + id;
    public static string DistributionKey(string id) => "query-group:" + id;

    public static IReadOnlyList<QueryChoice> FieldChoices(IReadOnlyList<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var choices = DashboardService.Roles.Select(role => new QueryChoice("role:" + role.Key, role.Label + " · rôle")).ToList();
        foreach (var group in headers.GroupBy(header => header, StringComparer.Ordinal))
            if (group.Count() == 1 && QueryText.ValidField("column:" + group.Key, false))
                choices.Add(new("column:" + group.Key, group.Key + " · colonne exacte"));
        return choices;
    }

    public static QueryResult Evaluate(ExtractionResult data, DashboardSettings settings,
        DashboardSnapshot snapshot, SavedQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        try
        {
            query.Validate();
            var context = new EvaluationContext(data, settings, snapshot, ct);
            return Evaluate(context, query, ct);
        }
        catch (InvalidDataException exception) { return Unavailable(query, snapshot, exception.Message); }
    }

    public static void AppendDashboardQueries(DashboardSnapshot snapshot, ExtractionResult data,
        DashboardSettings settings, QueryLibrarySettings library, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(library);
        ct.ThrowIfCancellationRequested();
        library.Validate();
        var results = new List<QueryResult>(library.Queries.Count);
        EvaluationContext? context = null;
        string? contextError = null;
        try { context = new EvaluationContext(data, settings, snapshot, ct); }
        catch (InvalidDataException exception) { contextError = exception.Message; }
        foreach (var query in library.Queries)
        {
            ct.ThrowIfCancellationRequested();
            if (contextError is not null) { results.Add(Unavailable(query, snapshot, contextError)); continue; }
            try { results.Add(Evaluate(context!, query, ct)); }
            catch (InvalidDataException exception) { results.Add(Unavailable(query, snapshot, exception.Message)); }
        }
        ct.ThrowIfCancellationRequested();
        // Publish only a complete generation. Cancelling cannot leave half the library on a board.
        snapshot.Metrics.RemoveAll(metric => metric.Key.StartsWith("query:", StringComparison.Ordinal));
        snapshot.Distributions.RemoveAll(distribution => distribution.Key.StartsWith("query-group:", StringComparison.Ordinal));
        snapshot.Metrics.AddRange(results.Select(result => result.Metric));
        snapshot.Distributions.AddRange(results.Where(result => result.Distribution is not null).Select(result => result.Distribution!));
    }

    private sealed record BoundField(string Key, int Index, string Label)
    {
        public bool IsTechnician => Key == "role:assignee";
    }

    private sealed record BoundCondition(BoundField Field, string Operator, string Text, double? Number, DateTime? Date);

    private sealed class EvaluationContext
    {
        public ExtractionResult Data { get; }
        public DashboardSettings Settings { get; }
        public DashboardSnapshot Snapshot { get; }
        private readonly HashSet<int> _scope;
        private readonly IReadOnlyList<DashboardColumn> _columns;
        private readonly Dictionary<string, BoundField> _fields = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (DashboardMetric Metric, int[] Rows)> _populations = new(StringComparer.Ordinal);

        public EvaluationContext(ExtractionResult data, DashboardSettings settings, DashboardSnapshot snapshot, CancellationToken ct)
        {
            try { DashboardService.ValidateSettings(settings); }
            catch (InvalidOperationException exception) { throw new InvalidDataException(exception.Message, exception); }
            Data = data; Settings = settings; Snapshot = snapshot;
            var total = snapshot.Metric("total");
            if (total is null || total.Value is null)
                throw new InvalidDataException("La cohorte totale du tableau de bord n’est pas disponible.");
            _scope = new HashSet<int>();
            for (var index = 0; index < total.Rows.Length; index++)
            {
                if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
                var row = total.Rows[index];
                if (row < 0 || row >= data.Rows.Count)
                    throw new InvalidDataException("Les indices du tableau de bord ne correspondent plus au relevé courant.");
                _scope.Add(row);
            }
            // A configured-but-missing role remains missing. Only synthetic snapshots with no
            // semantic columns at all fall back to the same conservative resolver.
            _columns = snapshot.Columns.Count == 0 ? DashboardService.ResolveColumns(data.Headers, settings) : snapshot.Columns;
        }

        public (DashboardMetric Metric, int[] Rows) Population(string key, CancellationToken ct)
        {
            if (_populations.TryGetValue(key, out var existing)) return existing;
            var metric = Snapshot.Metric(key);
            if (metric is null || metric.Value is null)
                throw new InvalidDataException("La base « " + BaseProbes.First(choice => choice.Key == key).Label
                    + " » est indisponible. " + (metric?.Explanation ?? "Les colonnes ou sondes nécessaires ne sont pas disponibles."));
            var seen = new HashSet<int>();
            var rows = new List<int>();
            for (var index = 0; index < metric.Rows.Length; index++)
            {
                if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
                var row = metric.Rows[index];
                if (row < 0 || row >= Data.Rows.Count)
                    throw new InvalidDataException("Une cohorte de la requête contient un indice absent du relevé courant.");
                if (_scope.Contains(row) && seen.Add(row)) rows.Add(row);
            }
            var population = (metric, rows.Count == metric.Rows.Length ? metric.Rows : rows.ToArray());
            _populations.Add(key, population);
            return population;
        }

        public BoundField Bind(string field)
        {
            if (_fields.TryGetValue(field, out var existing)) return existing;
            int index;
            string label;
            if (field.StartsWith("role:", StringComparison.Ordinal))
            {
                var role = field[5..];
                var column = _columns.FirstOrDefault(candidate => candidate.Role == role);
                if (column?.Index is not int found)
                    throw new InvalidDataException("Le champ « " + (column?.Label ?? role) + " » est absent, ambigu ou ne correspond plus à la colonne configurée.");
                if (found < 0 || found >= Data.Headers.Count || !string.Equals(Data.Headers[found], column.Header, StringComparison.Ordinal))
                    throw new InvalidDataException("La correspondance du champ « " + column.Label + " » ne correspond plus aux en-têtes du relevé.");
                index = found; label = column.Label;
            }
            else
            {
                var header = field[7..];
                var matches = Enumerable.Range(0, Data.Headers.Count)
                    .Where(position => string.Equals(Data.Headers[position], header, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException("La colonne exacte « " + header + " » est " + (matches.Length == 0 ? "absente" : "présente plusieurs fois") + ".");
                index = matches[0]; label = header;
            }
            var result = new BoundField(field, index, label);
            _fields.Add(field, result);
            return result;
        }

        public string Cell(int row, BoundField field)
        {
            var cells = Data.Rows[row];
            return cells is not null && field.Index < cells.Length ? cells[field.Index]?.Trim() ?? "" : "";
        }
    }

    private static QueryResult Evaluate(EvaluationContext context, SavedQuery query, CancellationToken ct)
    {
        var (baseMetric, population) = context.Population(query.BaseProbe, ct);
        var conditions = new List<BoundCondition>(query.Conditions.Count);
        foreach (var condition in query.Conditions)
        {
            ct.ThrowIfCancellationRequested();
            var field = context.Bind(condition.Field);
            double? number = null; DateTime? date = null;
            if (condition.Operator.StartsWith("number-", StringComparison.Ordinal)
                || condition.Operator is "last-days" or "next-days" or "age-at-least")
            {
                if (!QueryText.TryNumber(condition.Value, out var parsedNumber))
                    throw new InvalidDataException("Une condition numérique contient une valeur de comparaison illisible.");
                number = parsedNumber;
            }
            if (condition.Operator is "date-before" or "date-after")
            {
                if (!DashboardService.TryDate(condition.Value, context.Settings.DateCulture, context.Snapshot.CalculatedAt.Offset, out var parsedDate))
                    throw new InvalidDataException("Une condition de date contient une borne illisible pour la culture configurée.");
                date = parsedDate;
            }
            conditions.Add(new(field, condition.Operator, condition.Value.Trim(), number, date));
        }
        var aggregateField = query.Aggregate is "count" or "percent" ? null : context.Bind(query.AggregateField);
        var groupField = query.GroupBy.Length == 0 ? null : context.Bind(query.GroupBy);
        var unreadableConditions = 0;
        var excludedUnreadableConditions = 0;
        int[] filtered;
        if (conditions.Count == 0) filtered = population;
        else
        {
            var selected = new List<int>();
            for (var index = 0; index < population.Length; index++)
            {
                if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
                var row = population[index];
                var any = false; var all = true; var invalid = false;
                foreach (var condition in conditions)
                {
                    var matched = Matches(context, row, condition);
                    if (!matched.HasValue) { invalid = true; continue; }
                    any |= matched.Value; all &= matched.Value;
                }
                if (invalid) unreadableConditions++;
                // An absent column was already rejected during binding. For a present column,
                // a true OR branch is sufficient; AND still requires every condition to be true.
                var include = query.Match == "all" ? all && !invalid : any;
                if (include) selected.Add(row);
                else if (invalid) excludedUnreadableConditions++;
            }
            filtered = selected.ToArray();
        }

        var explanation = $"Base : {BaseProbes.First(choice => choice.Key == query.BaseProbe).Label} ({population.Length:N0} lignes du périmètre). "
            + baseMetric.Explanation + " "
            + $"Calculé au {context.Snapshot.CalculatedAt:dd/MM/yyyy HH:mm:ss zzz}. ";
        if (!context.Snapshot.Complete) explanation += "Le relevé source est incomplet ou son exhaustivité n’est pas confirmée. ";
        explanation += conditions.Count == 0 ? "Aucune condition supplémentaire. "
            : $"{conditions.Count} condition(s), {(query.Match == "all" ? "toutes nécessaires et lisibles" : "une condition vraie suffit, même si une autre valeur est illisible")}. Comparaisons textuelles littérales, insensibles à la casse, espaces de bord ignorés. ";
        if (unreadableConditions > 0)
        {
            explanation += $"{unreadableConditions:N0} ligne(s) avec un nombre ou une date absent/illisible dans une condition ; {excludedUnreadableConditions:N0} de ces lignes non retenue(s). ";
            if (unreadableConditions > excludedUnreadableConditions)
                explanation += $"{unreadableConditions - excludedUnreadableConditions:N0} retenue(s) grâce à une autre condition OU vraie. ";
        }

        double? value;
        var unit = context.Snapshot.Grain;
        var contributions = filtered;
        var sample = population.Length;
        var unreadableAggregate = 0;
        if (query.Aggregate == "count") value = filtered.Length;
        else if (query.Aggregate == "percent")
        {
            value = population.Length == 0 ? null : 100d * filtered.Length / population.Length;
            unit = "%";
            explanation += "Part des lignes retenues sur la population de base, avant conditions ; les lignes avec des valeurs de condition illisibles restent dans ce dénominateur. ";
        }
        else if (query.Aggregate == "distinct")
        {
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<int>();
            for (var index = 0; index < filtered.Length; index++)
            {
                if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
                var row = filtered[index];
                var text = context.Cell(row, aggregateField!);
                var values = aggregateField!.IsTechnician ? DashboardService.SplitTechnicianNames(text)
                    : text.Length == 0 ? Array.Empty<string>() : new[] { text };
                if (values.Count == 0) continue;
                distinct.UnionWith(values); rows.Add(row);
            }
            value = distinct.Count; unit = "valeurs distinctes";
            contributions = rows.ToArray(); sample = contributions.Length;
            explanation += "Valeurs distinctes non vides, insensibles à la casse. Le détail conserve toutes les lignes contributrices ; le relevé n’est pas dédoublonné. ";
            if (aggregateField!.IsTechnician) explanation += "Les techniciens sont séparés selon les mêmes règles que le dashboard. ";
        }
        else
        {
            var rows = new List<int>();
            var sum = 0d; var correction = 0d; var minimum = double.PositiveInfinity; var maximum = double.NegativeInfinity;
            for (var index = 0; index < filtered.Length; index++)
            {
                if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
                var row = filtered[index];
                if (!QueryText.TryNumber(context.Cell(row, aggregateField!), out var number)) { unreadableAggregate++; continue; }
                rows.Add(row); minimum = Math.Min(minimum, number); maximum = Math.Max(maximum, number);
                var next = sum + number;
                correction += Math.Abs(sum) >= Math.Abs(number) ? (sum - next) + number : (number - next) + sum;
                sum = next;
            }
            contributions = rows.ToArray(); sample = contributions.Length; unit = "valeur";
            value = rows.Count == 0 ? null : query.Aggregate switch
            {
                "sum" => sum + correction,
                "average" => (sum + correction) / rows.Count,
                "min" => minimum,
                "max" => maximum,
                _ => null
            };
            if (value.HasValue && !double.IsFinite(value.Value)) value = null;
            explanation += "Agrégat sur les nombres finis uniquement : virgule ou point décimal, groupement des milliers par espaces ; aucun symbole de devise, pourcentage ou conversion d’unité. ";
            if (unreadableAggregate > 0) explanation += $"{unreadableAggregate:N0} valeur(s) d’agrégat absente(s) ou illisible(s), exclue(s). ";
            if (sample == 0) explanation += "Aucune valeur numérique contributrice : résultat non disponible, même pour une somme. ";
        }
        var metric = new DashboardMetric(MetricKey(query.Id), query.Name, value, unit, explanation.Trim(), contributions, sample);
        DashboardDistribution? distribution = null;
        if (groupField is not null)
        {
            var groups = new Dictionary<string, (string Label, List<int> Rows)>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < filtered.Length; index++)
            {
                if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
                var row = filtered[index]; var raw = context.Cell(row, groupField);
                var values = groupField.IsTechnician ? DashboardService.SplitTechnicianNames(raw)
                    : raw.Length == 0 ? Array.Empty<string>() : new[] { raw };
                if (values.Count == 0) AddGroup("empty:", "Non renseigné (cellule vide)", row);
                else foreach (var label in values) AddGroup("value:" + label, label, row);
            }
            var buckets = groups.Values.OrderByDescending(group => group.Rows.Count)
                .ThenBy(group => group.Label, StringComparer.OrdinalIgnoreCase)
                .Select(group => new DashboardBucket(group.Label, group.Rows.Count, group.Rows.ToArray())).ToArray();
            var note = "Comptage des lignes retenues par « " + groupField.Label + " », indépendant de l’agrégat de la carte. Les cellules vides restent visibles. ";
            if (groupField.IsTechnician)
                note += "Une ligne apparaît une fois par technicien détecté : la somme des groupes peut dépasser le nombre de lignes. Aucun nom n’est séparé sur une virgule ou sur un simple espace. ";
            distribution = new(DistributionKey(query.Id), query.Name + " · " + groupField.Label, true, filtered.Length,
                note + explanation.Trim(), buckets);
            void AddGroup(string key, string label, int row)
            {
                if (!groups.TryGetValue(key, out var group)) { group = (label, new()); groups.Add(key, group); }
                group.Rows.Add(row);
            }
        }
        ct.ThrowIfCancellationRequested();
        var message = $"{filtered.Length:N0} ligne(s) retenue(s) sur {population.Length:N0}.";
        if (unreadableConditions > 0) message += $" {unreadableConditions:N0} ligne(s) avec une valeur numérique/date absente ou illisible, dont {excludedUnreadableConditions:N0} non retenue(s).";
        if (unreadableAggregate > 0) message += $" {unreadableAggregate:N0} valeur(s) absente(s) ou illisible(s) exclue(s) de l’agrégat.";
        if (value is null) message += " Agrégat non calculable sur cet échantillon.";
        return new() { QueryId = query.Id, Available = true, Message = message, Rows = filtered, Metric = metric, Distribution = distribution };
    }

    private static bool? Matches(EvaluationContext context, int row, BoundCondition condition)
    {
        var raw = context.Cell(row, condition.Field);
        if (condition.Operator.StartsWith("number-", StringComparison.Ordinal))
        {
            if (!QueryText.TryNumber(raw, out var number)) return null;
            return condition.Operator switch
            {
                "number-eq" => number == condition.Number, "number-gt" => number > condition.Number,
                "number-gte" => number >= condition.Number, "number-lt" => number < condition.Number,
                "number-lte" => number <= condition.Number, _ => null
            };
        }
        if (condition.Operator is "date-before" or "date-after" or "last-days" or "next-days" or "age-at-least")
        {
            if (!DashboardService.TryDate(raw, context.Settings.DateCulture, context.Snapshot.CalculatedAt.Offset, out var date)) return null;
            var now = context.Snapshot.CalculatedAt.DateTime;
            return condition.Operator switch
            {
                "date-before" => date < condition.Date, "date-after" => date > condition.Date,
                "last-days" => date <= now && (now - date).TotalDays <= condition.Number,
                "next-days" => date >= now && (date - now).TotalDays <= condition.Number,
                "age-at-least" => date <= now && (now - date).TotalDays >= condition.Number,
                _ => null
            };
        }
        var values = condition.Field.IsTechnician ? DashboardService.SplitTechnicianNames(raw)
            : raw.Length == 0 ? Array.Empty<string>() : new[] { raw };
        return condition.Operator switch
        {
            "empty" => values.Count == 0,
            "not-empty" => values.Count > 0,
            "contains" => values.Any(value => value.Contains(condition.Text, StringComparison.OrdinalIgnoreCase)),
            "not-contains" => values.All(value => !value.Contains(condition.Text, StringComparison.OrdinalIgnoreCase)),
            "equals" => values.Any(value => string.Equals(value, condition.Text, StringComparison.OrdinalIgnoreCase)),
            "not-equals" => values.All(value => !string.Equals(value, condition.Text, StringComparison.OrdinalIgnoreCase)),
            "starts-with" => values.Any(value => value.StartsWith(condition.Text, StringComparison.OrdinalIgnoreCase)),
            _ => null
        };
    }

    private static QueryResult Unavailable(SavedQuery query, DashboardSnapshot snapshot, string message)
    {
        var id = query.Id ?? "";
        var label = QueryText.CleanLabel(query.Name, SavedQuery.MaximumNameLength);
        return new()
        {
            QueryId = id, Available = false, Message = message,
            Metric = new(MetricKey(id), label, null, query.Aggregate == "percent" ? "%" : snapshot.Grain,
                message, Array.Empty<int>(), null),
            Distribution = string.IsNullOrEmpty(query.GroupBy) ? null
                : new(DistributionKey(id), label, false, 0, message, Array.Empty<DashboardBucket>())
        };
    }
}
