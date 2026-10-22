using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text;

namespace GlpiPilot.Core;

/// <summary>Bounded typed local queries over a snapshot. No SQL, executable expressions or network access.</summary>
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
        DashboardSnapshot snapshot, SavedQuery query, CancellationToken ct = default, IReadOnlyDictionary<string, string>? parameters = null)
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
            return Evaluate(context, query, ct, parameters);
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

    private sealed record BoundField(string Key, int Index, string Label, QueryComputedField? Computation = null,
        BoundField? Left = null, BoundField? Right = null)
    {
        public bool IsTechnician => Key == "role:assignee";
        public string Unit => Computation?.Unit is { Length: > 0 } unit ? unit
            : Computation?.Function is "days-between" or "age-days" ? "jours"
            : Computation?.Function == "length" ? "caractères" : "valeur";
    }
    private sealed record BoundOperand(BoundField? Field, string Text, string Label);
    private sealed record BoundCondition(BoundField Field, QueryCondition Definition, BoundOperand First, BoundOperand Second);
    private sealed record AggregateValue(double? Value, string Unit, int[] Contributions, int Sample, int Unreadable);

    private sealed class EvaluationContext
    {
        private SavedQuery _query = new();
        private IReadOnlyDictionary<string, string>? _parameters;
        public void Configure(SavedQuery query, IReadOnlyDictionary<string, string>? parameters)
        {
            _query = query; _parameters = parameters;
            foreach (var key in _fields.Keys.Where(key => key.StartsWith("calc:", StringComparison.Ordinal)).ToArray()) _fields.Remove(key);
        }
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
            if (field.StartsWith("calc:", StringComparison.Ordinal))
            {
                var computed = _query.ComputedFields.FirstOrDefault(item => item.Key == field[5..])
                    ?? throw new InvalidDataException("Champ calculé non défini : " + field);
                var left = Bind(computed.Field);
                var right = computed.OtherField.Length == 0 ? null : Bind(computed.OtherField);
                var calculated = new BoundField(field, -1, computed.Label, computed, left, right);
                _fields.Add(field, calculated); return calculated;
            }
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

        public BoundOperand Operand(QueryCondition condition, bool second = false)
        {
            var source = second ? condition.Value2Source : condition.ValueSource;
            var value = second ? condition.Value2 : condition.Value;
            if (source == "field") return new(Bind(value), "", value);
            if (source != "parameter") return new(null, value.Trim(), value.Trim());
            var definition = _query.Parameters.First(parameter => parameter.Key == value);
            var text = (_parameters != null && _parameters.TryGetValue(value, out var supplied) ? supplied : definition.DefaultValue)?.Trim() ?? "";
            if (text.Length == 0 || text.Length > SavedQuery.MaximumValueLength)
                throw new InvalidDataException("Renseignez le paramètre « " + (definition.Label.Length > 0 ? definition.Label : value) + " » avant le calcul.");
            if (!QueryText.Valid(text, SavedQuery.MaximumValueLength, false, true)
                || definition.Type == "number" && !QueryText.TryNumber(text, out _)
                || definition.Type == "date" && !DashboardService.TryDate(text, Settings.DateCulture, Snapshot.CalculatedAt.Offset, out _))
                throw new InvalidDataException("La valeur du paramètre « " + value + " » ne correspond pas à son type « " + definition.Type + " ».");
            return new(null, text, "$" + value + " = " + text);
        }

        public string Cell(int row, BoundField field)
        {
            if (field.Computation is { } definition)
            {
                if (definition.Function is "lower" or "concat" or "coalesce")
                {
                    var left = Cell(row, field.Left!);
                    return definition.Function switch
                    {
                        "lower" => left.ToLowerInvariant(),
                        "concat" => BoundedConcat(left, definition.Constant, Cell(row, field.Right!)),
                        "coalesce" => left.Length == 0 ? definition.Constant : left,
                        _ => ""
                    };
                }
                return Number(row, field, out var number) ? number.ToString("R", CultureInfo.InvariantCulture) : "";
            }
            var cells = Data.Rows[row];
            return cells is not null && field.Index < cells.Length ? cells[field.Index]?.Trim() ?? "" : "";
        }

        public bool Number(int row, BoundField field, out double result)
        {
            result = default;
            if (field.Computation is not { } definition) return QueryText.TryNumber(Cell(row, field), out result);
            if (definition.Function is "lower" or "concat" or "coalesce") return QueryText.TryNumber(Cell(row, field), out result);
            if (definition.Function == "length") { result = Cell(row, field.Left!).EnumerateRunes().Count(); return true; }
            if (definition.Function is "days-between" or "age-days")
            {
                if (!Date(row, field.Left!, out var first)) return false;
                var last = Snapshot.CalculatedAt.DateTime;
                if (definition.Function == "days-between" && !Date(row, field.Right!, out last)) return false;
                result = (last - first).TotalDays; return double.IsFinite(result);
            }
            if (!Number(row, field.Left!, out var left)) return false;
            double right;
            if (field.Right is { } other) { if (!Number(row, other, out right)) return false; }
            else if (!QueryText.TryNumber(definition.Constant, out right)) return false;
            result = definition.Function switch
            {
                "add" => left + right, "subtract" => left - right, "multiply" => left * right,
                "divide" => right == 0 ? double.NaN : left / right, _ => double.NaN
            };
            return double.IsFinite(result);
        }
        public bool Date(int row, BoundField field, out DateTime date) =>
            DashboardService.TryDate(Cell(row, field), Settings.DateCulture, Snapshot.CalculatedAt.Offset, out date);

        private static string BoundedConcat(string left, string separator, string right)
        {
            // Nested concatenations must never multiply source cells into unbounded allocations.
            // Failing the query preserves unknown/empty semantics instead of fabricating a blank.
            if ((long)left.Length + separator.Length + right.Length > 100_000)
                throw new InvalidDataException("Un champ calculé produit plus de 100 000 caractères. Réduisez les concaténations de cette requête.");
            return left + separator + right;
        }
    }

    private static (QueryLogic.Node Logic, BoundCondition[] Conditions, BoundField? Aggregate, (BoundField Field, string Bucket)[] Groups)
        Bind(EvaluationContext context, SavedQuery query, IReadOnlyDictionary<string, string>? parameters)
    {
        context.Configure(query, parameters);
        var logic = QueryLogic.Parse(query.ConditionExpression, query.Conditions.Count, query.Match);
        var conditions = query.Conditions.Select(condition =>
        {
            var unused = new BoundOperand(null, "", "");
            var first = condition.Operator is "empty" or "not-empty" ? unused : context.Operand(condition);
            var second = condition.Operator is "number-between" or "number-outside" or "date-between" or "date-outside"
                ? context.Operand(condition, true) : unused;
            var bound = new BoundCondition(context.Bind(condition.Field), condition, first, second);
            foreach (var operand in second == unused ? new[] { first } : new[] { first, second })
            {
                if (operand.Field != null || operand == unused) continue;
                if (condition.Operator.StartsWith("number-", StringComparison.Ordinal)
                    && !QueryText.TryNumber(operand.Text, out _)) throw new InvalidDataException("Une borne numérique est illisible.");
                if (condition.Operator is "last-days" or "next-days" or "age-at-least"
                    && (!QueryText.TryNumber(operand.Text, out var days) || days is < 0 or > 36500))
                    throw new InvalidDataException("Une durée de condition doit être comprise entre 0 et 36 500 jours.");
                if (condition.Operator.StartsWith("date-", StringComparison.Ordinal)
                    && !DashboardService.TryDate(operand.Text, context.Settings.DateCulture, context.Snapshot.CalculatedAt.Offset, out _))
                    throw new InvalidDataException("Une borne de date est illisible pour la culture configurée.");
            }
            if (first.Field == null && second.Field == null && second != unused)
            {
                if (condition.Operator.StartsWith("number-", StringComparison.Ordinal)
                    && QueryText.TryNumber(first.Text, out var low) && QueryText.TryNumber(second.Text, out var high) && low > high)
                    throw new InvalidDataException("La borne basse de l’intervalle numérique doit précéder la borne haute.");
                if (condition.Operator.StartsWith("date-", StringComparison.Ordinal)
                    && DashboardService.TryDate(first.Text, context.Settings.DateCulture, context.Snapshot.CalculatedAt.Offset, out var from)
                    && DashboardService.TryDate(second.Text, context.Settings.DateCulture, context.Snapshot.CalculatedAt.Offset, out var to) && from > to)
                    throw new InvalidDataException("La date de début de l’intervalle doit précéder sa fin.");
            }
            return bound;
        }).ToArray();
        var aggregate = query.Aggregate is "count" or "percent" ? null : context.Bind(query.AggregateField);
        var groups = new[] { (query.GroupBy, query.GroupDateBucket), (query.GroupBySecondary, query.SecondaryDateBucket), (query.GroupByTertiary, query.TertiaryDateBucket) }
            .Where(group => group.Item1.Length > 0).Select(group => (context.Bind(group.Item1), group.Item2)).ToArray();
        return (logic, conditions, aggregate, groups);
    }

    private static QueryResult Evaluate(EvaluationContext context, SavedQuery query, CancellationToken ct,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var (baseMetric, population) = context.Population(query.BaseProbe, ct);
        var bound = Bind(context, query, parameters);
        var selected = new List<int>(); var unreadableConditions = 0; var excludedUnreadableConditions = 0; var unknownRows = 0;
        for (var index = 0; index < population.Length; index++)
        {
            if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
            var row = population[index];
            var states = bound.Conditions.Select(condition => Matches(context, row, condition)).ToArray();
            var decision = bound.Logic.Evaluate(states); var invalid = states.Any(state => !state.HasValue);
            if (invalid) unreadableConditions++;
            if (decision == true) selected.Add(row); else if (invalid) excludedUnreadableConditions++;
            if (!decision.HasValue) unknownRows++;
        }
        var filtered = selected.ToArray();
        var calculation = Aggregate(context, query, bound.Aggregate, filtered, population.Length, ct);
        var explanation = $"Base : {BaseProbes.First(choice => choice.Key == query.BaseProbe).Label} ({population.Length:N0} lignes du périmètre). "
            + baseMetric.Explanation + $" Calculé au {context.Snapshot.CalculatedAt:dd/MM/yyyy HH:mm:ss zzz}. "
            + (bound.Conditions.Length == 0 ? "Aucune condition supplémentaire. " : $"Logique : {bound.Logic.Describe()}. Une ligne n’est retenue que si l’expression est vraie. Les valeurs illisibles donnent un résultat indéterminé ; NON ne les transforme pas en valeurs vraies. ")
            + "Comparaisons textuelles littérales, insensibles à la casse, espaces de bord ignorés. ";
        if (!context.Snapshot.Complete) explanation += "Le relevé source est incomplet ou son exhaustivité n’est pas confirmée. ";
        if (unreadableConditions > 0)
        {
            explanation += $"{unreadableConditions:N0} ligne(s) avec un nombre ou une date absent/illisible dans une condition ; {excludedUnreadableConditions:N0} de ces lignes non retenue(s). ";
            if (unreadableConditions > excludedUnreadableConditions) explanation += $"{unreadableConditions - excludedUnreadableConditions:N0} retenue(s) grâce à une autre condition OU vraie. ";
        }
        explanation += query.Aggregate switch
        {
            "count" => "Comptage des lignes retenues. ",
            "percent" => "Part des lignes retenues sur la population de base avant conditions ; les valeurs de condition illisibles restent dans ce dénominateur. ",
            "distinct" => "Valeurs distinctes non vides, insensibles à la casse ; toutes les lignes contributrices sont conservées. ",
            "median" => "Médiane par interpolation linéaire des nombres finis uniquement (rang (n−1) × 0,5). ",
            "percentile" => $"Percentile P{query.Percentile:G} par interpolation linéaire des nombres finis (rang (n−1) × P/100). ",
            _ => "Agrégat sur les nombres finis uniquement : virgule ou point décimal, milliers séparés par espaces ; aucune conversion implicite d’unité. "
        };
        if (calculation.Unreadable > 0) explanation += $"{calculation.Unreadable:N0} valeur(s) d’agrégat absente(s) ou illisible(s), exclue(s). ";
        if (calculation.Value is null) explanation += "Aucune valeur calculable sur cet échantillon ; un résultat absent reste indisponible. ";
        var metric = new DashboardMetric(MetricKey(query.Id), query.Name, calculation.Value, calculation.Unit,
            explanation.Trim(), calculation.Contributions, calculation.Sample);
        DashboardDistribution? distribution = null; IReadOnlyList<QueryGroupResult> groupResults = Array.Empty<QueryGroupResult>();
        if (bound.Groups.Length > 0)
        {
            var baseGroups = GroupRows(context, bound.Groups, population, ct);
            var selectedGroups = GroupRows(context, bound.Groups, filtered, ct);
            var groups = new List<QueryGroupResult>();
            foreach (var pair in query.Aggregate == "percent" ? baseGroups : selectedGroups)
            {
                ct.ThrowIfCancellationRequested();
                var rows = selectedGroups.TryGetValue(pair.Key, out var chosen) ? chosen.Rows.ToArray() : Array.Empty<int>();
                var denominator = baseGroups.TryGetValue(pair.Key, out var basis) ? basis.Rows.Count : 0;
                var value = Aggregate(context, query, bound.Aggregate, rows, denominator, ct);
                groups.Add(new(string.Join(" › ", pair.Value.Labels), pair.Value.Labels, rows.Length,
                    value.Value, value.Unit, value.Sample, rows, value.Contributions, denominator));
            }
            groupResults = groups.OrderByDescending(group => group.Value ?? double.NegativeInfinity)
                .ThenBy(group => group.Label, StringComparer.OrdinalIgnoreCase).ToArray();
            var buckets = groupResults.Select(group => new DashboardBucket(group.Label, group.Count, group.Rows,
                group.Value, group.Unit, group.Sample, query.Aggregate != "count")).ToArray();
            var note = "Calcul « " + query.Aggregate + " » dans chaque groupe, avec effectif et contributions propres. Les cellules vides ou dates illisibles restent dans des groupes explicites. ";
            if (query.Aggregate == "percent") note += "Chaque pourcentage utilise les lignes de base de son propre groupe avant conditions. ";
            if (bound.Groups.Any(group => group.Field.IsTechnician)) note += "Une ligne apparaît une fois par technicien détecté ; les groupes ne sont pas additionnables. Aucun nom n’est séparé sur une virgule ou un simple espace. ";
            distribution = new(DistributionKey(query.Id), query.Name + " · " + string.Join(" › ", bound.Groups.Select(group => group.Field.Label)),
                true, filtered.Length, note + explanation.Trim(), buckets);
        }
        ct.ThrowIfCancellationRequested();
        var message = $"{filtered.Length:N0} ligne(s) retenue(s) sur {population.Length:N0}.";
        if (unreadableConditions > 0) message += $" {unreadableConditions:N0} ligne(s) avec une valeur numérique/date absente ou illisible, dont {excludedUnreadableConditions:N0} non retenue(s).";
        if (calculation.Unreadable > 0) message += $" {calculation.Unreadable:N0} valeur(s) absente(s) ou illisible(s) exclue(s) de l’agrégat.";
        if (calculation.Value is null) message += " Agrégat non calculable sur cet échantillon.";
        return new() { QueryId = query.Id, Available = true, Message = message, Rows = filtered, Metric = metric,
            Distribution = distribution, Groups = groupResults, UnknownRows = unknownRows };
    }

    private static AggregateValue Aggregate(EvaluationContext context, SavedQuery query, BoundField? field,
        int[] rows, int denominator, CancellationToken ct)
    {
        if (query.Aggregate == "count") return new(rows.Length, context.Snapshot.Grain, rows, denominator, 0);
        if (query.Aggregate == "percent") return new(denominator == 0 ? null : 100d * rows.Length / denominator, "%", rows, denominator, 0);
        var contributions = new List<int>();
        if (query.Aggregate == "distinct")
        {
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < rows.Length; index++)
            {
                if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
                var values = Values(context, rows[index], field!);
                if (values.Count == 0) continue;
                distinct.UnionWith(values); contributions.Add(rows[index]);
            }
            return new(distinct.Count, "valeurs distinctes", contributions.ToArray(), contributions.Count, rows.Length - contributions.Count);
        }
        var numbers = new List<double>(); var sum = 0d; var correction = 0d;
        for (var index = 0; index < rows.Length; index++)
        {
            if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
            if (!context.Number(rows[index], field!, out var value)) continue;
            numbers.Add(value); contributions.Add(rows[index]);
            var next = sum + value;
            correction += Math.Abs(sum) >= Math.Abs(value) ? (sum - next) + value : (value - next) + sum;
            sum = next;
        }
        double? result = numbers.Count == 0 ? null : query.Aggregate switch
        {
            "sum" => sum + correction, "average" => (sum + correction) / numbers.Count,
            "min" => numbers.Min(), "max" => numbers.Max(),
            "median" => Percentile(numbers, 50), "percentile" => Percentile(numbers, query.Percentile), _ => null
        };
        if (result.HasValue && !double.IsFinite(result.Value)) result = null;
        return new(result, field!.Unit, contributions.ToArray(), contributions.Count, rows.Length - contributions.Count);
    }
    private static double Percentile(List<double> values, double percentile)
    {
        values.Sort(); var rank = (values.Count - 1) * percentile / 100d; var low = (int)Math.Floor(rank); var high = (int)Math.Ceiling(rank);
        return low == high ? values[low] : values[low] * (1 - (rank - low)) + values[high] * (rank - low);
    }
    private sealed record GroupMembers(string[] Labels, List<int> Rows);
    private static Dictionary<string, GroupMembers> GroupRows(EvaluationContext context, (BoundField Field, string Bucket)[] fields,
        int[] rows, CancellationToken ct)
    {
        var result = new Dictionary<string, GroupMembers>(StringComparer.OrdinalIgnoreCase); var memberships = 0;
        for (var index = 0; index < rows.Length; index++)
        {
            if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
            var row = rows[index]; var paths = new List<(string[] Keys, string[] Labels)> { (Array.Empty<string>(), Array.Empty<string>()) };
            foreach (var field in fields)
            {
                var components = GroupValues(context, row, field.Field, field.Bucket);
                if ((long)paths.Count * components.Count > 128) throw new InvalidDataException("Un ticket produit plus de 128 combinaisons de regroupement ; réduisez les niveaux à valeurs multiples.");
                paths = paths.SelectMany(path => components.Select(component =>
                    (path.Keys.Append(component.Key).ToArray(), path.Labels.Append(component.Label).ToArray()))).ToList();
            }
            foreach (var path in paths)
            {
                if (++memberships > 2000000) throw new InvalidDataException("Le regroupement dépasse deux millions de contributions ; réduisez le périmètre ou les niveaux.");
                var key = JsonSerializer.Serialize(path.Keys);
                if (!result.TryGetValue(key, out var group)) { group = new(path.Labels, new()); result.Add(key, group); }
                group.Rows.Add(row);
            }
        }
        return result;
    }
    private static IReadOnlyList<(string Key, string Label)> GroupValues(EvaluationContext context, int row, BoundField field, string bucket)
    {
        if (bucket != "none")
        {
            if (context.Cell(row, field).Length == 0) return new[] { ("empty:", "Non renseigné (cellule vide)") };
            if (!context.Date(row, field, out var date)) return new[] { ("invalid-date:", "Date illisible") };
            var label = bucket switch
            {
                "day" => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "week" => $"{ISOWeek.GetYear(date)}-S{ISOWeek.GetWeekOfYear(date):00}",
                "month" => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                "quarter" => $"{date.Year}-T{(date.Month - 1) / 3 + 1}",
                "year" => date.Year.ToString(CultureInfo.InvariantCulture), _ => ""
            };
            return new[] { ("period:" + label, label) };
        }
        var values = Values(context, row, field);
        return values.Count == 0 ? new[] { ("empty:", "Non renseigné (cellule vide)") }
            : values.Select(value => ("value:" + value, value)).ToArray();
    }
    private static IReadOnlyList<string> Values(EvaluationContext context, int row, BoundField field)
    {
        var raw = context.Cell(row, field);
        return field.IsTechnician ? DashboardService.SplitTechnicianNames(raw) : raw.Length == 0 ? Array.Empty<string>() : new[] { raw };
    }
    private static bool? Matches(EvaluationContext context, int row, BoundCondition condition)
    {
        var op = condition.Definition.Operator;
        string Text(BoundOperand operand) => operand.Field == null ? operand.Text : context.Cell(row, operand.Field);
        bool Number(BoundOperand operand, out double number) => operand.Field == null
            ? QueryText.TryNumber(operand.Text, out number) : context.Number(row, operand.Field, out number);
        bool Date(BoundOperand operand, out DateTime date) => operand.Field == null
            ? DashboardService.TryDate(operand.Text, context.Settings.DateCulture, context.Snapshot.CalculatedAt.Offset, out date)
            : context.Date(row, operand.Field, out date);
        if (op.StartsWith("number-", StringComparison.Ordinal))
        {
            if (!context.Number(row, condition.Field, out var number) || !Number(condition.First, out var first)) return null;
            if (op is "number-between" or "number-outside")
            {
                if (!Number(condition.Second, out var second) || first > second) return null;
                var inside = number >= first && number <= second; return op == "number-between" ? inside : !inside;
            }
            return op switch
            {
                "number-eq" => number == first, "number-gt" => number > first, "number-gte" => number >= first,
                "number-lt" => number < first, "number-lte" => number <= first, _ => null
            };
        }
        if (op.StartsWith("date-", StringComparison.Ordinal) || op is "last-days" or "next-days" or "age-at-least")
        {
            if (!context.Date(row, condition.Field, out var date)) return null;
            if (op is "last-days" or "next-days" or "age-at-least")
            {
                if (!Number(condition.First, out var days) || days is < 0 or > 36500) return null;
                var now = context.Snapshot.CalculatedAt.DateTime;
                return op switch
                {
                    "last-days" => date <= now && (now - date).TotalDays <= days,
                    "next-days" => date >= now && (date - now).TotalDays <= days,
                    "age-at-least" => date <= now && (now - date).TotalDays >= days, _ => null
                };
            }
            if (!Date(condition.First, out var first)) return null;
            if (op is "date-between" or "date-outside")
            {
                if (!Date(condition.Second, out var second) || first > second) return null;
                var inside = date >= first && date <= second; return op == "date-between" ? inside : !inside;
            }
            return op == "date-before" ? date < first : date > first;
        }
        var values = Values(context, row, condition.Field);
        if (op == "empty") return values.Count == 0;
        if (op == "not-empty") return values.Count > 0;
        IReadOnlyList<string> compared = condition.First.Field is { } right ? Values(context, row, right) : new[] { Text(condition.First) };
        if (condition.First.Field != null && (compared.Count == 0 || values.Count == 0)) return null;
        if (op is "in" or "not-in")
        {
            var set = compared.SelectMany(text => text.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0) return null;
            var found = values.Any(set.Contains); return op == "in" ? found : !found;
        }
        return op switch
        {
            "contains" => values.Any(value => compared.Any(text => value.Contains(text, StringComparison.OrdinalIgnoreCase))),
            "not-contains" => values.All(value => compared.All(text => !value.Contains(text, StringComparison.OrdinalIgnoreCase))),
            "equals" => values.Any(value => compared.Any(text => string.Equals(value, text, StringComparison.OrdinalIgnoreCase))),
            "not-equals" => values.All(value => compared.All(text => !string.Equals(value, text, StringComparison.OrdinalIgnoreCase))),
            "starts-with" => values.Any(value => compared.Any(text => value.StartsWith(text, StringComparison.OrdinalIgnoreCase))),
            "ends-with" => values.Any(value => compared.Any(text => value.EndsWith(text, StringComparison.OrdinalIgnoreCase))),
            _ => null
        };
    }

    public static QueryRowExplanation Explain(ExtractionResult data, DashboardSettings settings, DashboardSnapshot snapshot,
        SavedQuery query, int row, CancellationToken ct = default, IReadOnlyDictionary<string, string>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        if (row < 0 || row >= data.Rows.Count) throw new ArgumentOutOfRangeException(nameof(row));
        try
        {
            query.Validate(); var context = new EvaluationContext(data, settings, snapshot, ct);
            var (_, population) = context.Population(query.BaseProbe, ct); var within = population.Contains(row);
            var bound = Bind(context, query, parameters);
            var states = new List<bool?>(); var trace = new List<QueryConditionExplanation>();
            for (var index = 0; index < bound.Conditions.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var condition = bound.Conditions[index]; var state = Matches(context, row, condition); states.Add(state);
                string Operand(BoundOperand operand) => operand.Field == null ? operand.Label
                    : operand.Field.Label + " = " + context.Cell(row, operand.Field);
                var comparison = Operand(condition.First);
                if (condition.Definition.Operator.EndsWith("between", StringComparison.Ordinal) || condition.Definition.Operator.EndsWith("outside", StringComparison.Ordinal))
                    comparison += " … " + Operand(condition.Second);
                trace.Add(new(index + 1, condition.Field.Label, condition.Definition.Operator, context.Cell(row, condition.Field), comparison,
                    state == true ? "Vrai" : state == false ? "Faux" : "Indéterminé",
                    state.HasValue ? "Comparaison évaluée sur la valeur du relevé." : "Valeur absente, illisible, calcul impossible ou intervalle invalide ; aucun zéro n’est inventé."));
            }
            var decision = bound.Logic.Evaluate(states);
            return new()
            {
                Row = row, WithinBase = within, Decision = !within ? "false" : decision == true ? "true" : decision == false ? "false" : "unknown",
                Logic = bound.Logic.Describe(), Conditions = trace,
                Message = !within ? "Ce ticket est hors du périmètre de départ de la requête. Les conditions ci-dessous sont expliquées à titre de contrôle."
                    : decision == true ? "Ce ticket est retenu : l’expression logique est vraie."
                    : decision == false ? "Ce ticket n’est pas retenu : l’expression logique est fausse."
                    : "Ce ticket n’est pas retenu : les informations disponibles ne permettent pas de décider."
            };
        }
        catch (InvalidDataException error) { return new() { Row = row, Message = error.Message }; }
    }

    private static QueryResult Unavailable(SavedQuery query, DashboardSnapshot snapshot, string message)
    {
        var id = query.Id ?? ""; var label = QueryText.CleanLabel(query.Name, SavedQuery.MaximumNameLength);
        return new()
        {
            QueryId = id, Available = false, Message = message,
            Metric = new(MetricKey(id), label, null, query.Aggregate == "percent" ? "%" : snapshot.Grain, message, Array.Empty<int>(), null),
            Distribution = string.IsNullOrEmpty(query.GroupBy) ? null
                : new(DistributionKey(id), label, false, 0, message, Array.Empty<DashboardBucket>())
        };
    }
}
