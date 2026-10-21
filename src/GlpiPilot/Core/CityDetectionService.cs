namespace GlpiPilot.Core;

/// <summary>Local, literal inference from an explicitly configured dictionary; never a geocoder.</summary>
public static class CityDetectionService
{
    private sealed class TrieNode
    {
        public Dictionary<string, TrieNode> Children { get; } = new(StringComparer.Ordinal);
        public List<int> Cities { get; } = new();
    }

    private readonly record struct Occurrence(int Start, int Length, TrieNode Node);

    public static CityEnrichmentResult Enrich(ExtractionResult source, CitySettings settings,
        DashboardSettings dashboard, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(source.Headers);
        ArgumentNullException.ThrowIfNull(source.Rows);
        ct.ThrowIfCancellationRequested();
        var configuration = settings.Copy();
        if (!configuration.Enabled)
            return PassThrough(source, "Le repérage des villes est désactivé dans les options.");
        if (configuration.Cities.Count == 0)
            return PassThrough(source, "Ajoutez une ville et ses variantes dans les options pour activer le repérage dans les titres.");

        for (var index = 0; index < source.Rows.Count; index++)
        {
            if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
            if (source.Rows[index] is { } row && row.Length > source.Headers.Count)
                return PassThrough(source, "Repérage des villes indisponible : certaines cellules n’ont pas d’en-tête. Le relevé source est conservé intégralement.");
        }

        var titleColumn = ResolveTitle(source.Headers, configuration, dashboard);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var header in source.Headers)
        {
            ct.ThrowIfCancellationRequested();
            names.Add(string.Join(' ', CityText.Tokens(header, ct)));
        }
        string Unique(string name)
        {
            var candidate = name;
            for (var suffix = 2; !names.Add(string.Join(' ', CityText.Tokens(candidate, ct))); suffix++)
                candidate = $"{name} ({suffix})";
            return candidate;
        }
        var cityColumnName = Unique("Ville détectée");
        var stateColumnName = Unique("Repérage ville");
        var headers = new List<string>(source.Headers) { cityColumnName, stateColumnName };
        var data = new ExtractionResult
        {
            Headers = headers, Rows = new List<string[]>(source.Rows.Count),
            SourceUrl = source.SourceUrl, Title = source.Title, ExtractedAt = source.ExtractedAt,
            Pages = source.Pages, ExpectedTotal = source.ExpectedTotal, Scope = source.Scope,
            Complete = source.Complete, Warnings = new List<string>(source.Warnings), Status = source.Status
        };
        var trie = BuildTrie(configuration.Cities, ct);
        var matches = new List<CityMatch>(source.Rows.Count);
        var matchedCount = 0;
        var unmatchedCount = 0;
        var ambiguousCount = 0;
        var noTitleCount = 0;
        for (var rowIndex = 0; rowIndex < source.Rows.Count; rowIndex++)
        {
            if ((rowIndex & 255) == 0) ct.ThrowIfCancellationRequested();
            var original = source.Rows[rowIndex];
            var row = new string[headers.Count];
            Array.Fill(row, "");
            if (original is not null) Array.Copy(original, row, original.Length);
            var title = titleColumn.Index is int column && original is not null && column < original.Length
                ? original[column] : null;
            CityMatch match;
            if (titleColumn.Index is null || string.IsNullOrWhiteSpace(title))
            {
                match = new(rowIndex, null, null, CityMatchState.TitleUnavailable, Array.Empty<string>());
                noTitleCount++;
            }
            else
            {
                var cityIndexes = FindCities(CityText.Tokens(title, ct), trie, ct);
                if (cityIndexes.Count == 0)
                {
                    match = new(rowIndex, null, null, CityMatchState.Unmatched, Array.Empty<string>());
                    unmatchedCount++;
                }
                else if (cityIndexes.Count == 1)
                {
                    var city = configuration.Cities[cityIndexes.Single()];
                    match = new(rowIndex, city.Id, city.Name, CityMatchState.Matched, new[] { city.Id });
                    matchedCount++;
                }
                else
                {
                    var candidates = cityIndexes.Select(index => configuration.Cities[index].Id)
                        .Order(StringComparer.Ordinal).ToArray();
                    match = new(rowIndex, null, null, CityMatchState.Ambiguous, candidates);
                    ambiguousCount++;
                }
            }
            row[^2] = match.CityName ?? "";
            row[^1] = StateLabel(match.State);
            data.Rows.Add(row);
            matches.Add(match);
        }
        ct.ThrowIfCancellationRequested();
        var message = titleColumn.Index.HasValue
            ? $"Villes déduites des titres : {matchedCount:N0} lignes reconnues, {unmatchedCount:N0} sans correspondance, {ambiguousCount:N0} ambiguës, {noTitleCount:N0} sans titre exploitable."
            : titleColumn.State == "ambiguous"
                ? "Repérage des villes suspendu : plusieurs colonnes de titre sont possibles. Choisissez la colonne exacte dans les options Villes."
                : titleColumn.State == "missing-override"
                    ? "Repérage des villes suspendu : la colonne de titre configurée est absente. Vérifiez les options Villes et les correspondances du dashboard."
                    : "Repérage des villes suspendu : aucune colonne de titre n’est reconnue. Choisissez la colonne exacte dans les options Villes.";
        return new CityEnrichmentResult
        {
            Source = source, Data = data, Applied = true, Matches = matches,
            CityColumnName = cityColumnName, StateColumnName = stateColumnName,
            TitleColumnName = titleColumn.Index.HasValue ? titleColumn.Header : "",
            MatchedCount = matchedCount, UnmatchedCount = unmatchedCount,
            AmbiguousCount = ambiguousCount, TitleUnavailableCount = noTitleCount, Message = message
        };
    }

    /// <summary>Adds local inference-quality probes for exactly the snapshot's original row indices.</summary>
    public static void AppendDashboardProbes(DashboardSnapshot snapshot, CityEnrichmentResult report,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(report);
        ct.ThrowIfCancellationRequested();
        var requested = snapshot.Metric("total")?.Rows;
        var scope = requested is null ? null : new HashSet<int>(requested);
        var groups = new Dictionary<CityMatchState, List<int>>
        {
            [CityMatchState.Matched] = new(), [CityMatchState.Unmatched] = new(),
            [CityMatchState.Ambiguous] = new(), [CityMatchState.TitleUnavailable] = new()
        };
        var seen = new HashSet<int>();
        var available = report.Applied && scope is not null;
        if (available)
            for (var index = 0; index < report.Matches.Count; index++)
            {
                if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
                var match = report.Matches[index];
                if (scope!.Contains(match.RowIndex) && seen.Add(match.RowIndex)
                    && groups.TryGetValue(match.State, out var rows)) rows.Add(match.RowIndex);
            }
        // A malformed report must not turn uncovered rows into convincing zeroes.
        available &= scope is not null && groups.Sum(pair => pair.Value.Count) == scope.Count;
        var explanation = "Ville inférée uniquement à partir des noms et variantes configurés présents dans le titre, sans validation géographique. "
            + "Les états sont exclusifs et portent sur les lignes de ce périmètre. ";
        var unavailable = !report.Applied ? report.Message
            : "Le repérage des villes ne couvre pas exactement les indices de ce tableau de bord.";
        var sample = available ? scope!.Count : (int?)null;
        var additions = new List<DashboardMetric>();
        var buckets = new List<DashboardBucket>();
        foreach (var (state, key, label, detail, tone) in new[]
        {
            (CityMatchState.Matched, "city-matched", "Ville reconnue", "Une seule ville correspond au titre.", "normal"),
            (CityMatchState.Unmatched, "city-unmatched", "Ville non repérée", "Le titre est présent, mais aucun nom ou alias configuré ne correspond.", "warning"),
            (CityMatchState.Ambiguous, "city-ambiguous", "Ville ambiguë", "Plusieurs villes correspondent : aucune n’est choisie arbitrairement.", "warning"),
            (CityMatchState.TitleUnavailable, "city-no-title", "Sans titre exploitable", "La colonne de titre est absente ou ambiguë, ou sa valeur est vide sur cette ligne.", "warning")
        })
        {
            var rows = available ? groups[state].ToArray() : Array.Empty<int>();
            additions.Add(new(key, label, available ? rows.Length : null, snapshot.Grain,
                available ? explanation + detail : unavailable, rows, sample, tone));
            if (available) buckets.Add(new(StateLabel(state), rows.Length, rows));
        }
        var confirmedRows = additions[0].Rows;
        additions.Add(new("city-coverage", "Couverture des villes", available && sample > 0 ? 100d * confirmedRows.Length / sample.Value : null,
            "%", available ? explanation + "Part des lignes avec une ville unique reconnue, sur toutes les lignes du périmètre, titres absents et ambiguïtés inclus."
                : unavailable, confirmedRows, sample));
        ct.ThrowIfCancellationRequested();
        var keys = new HashSet<string>(additions.Select(metric => metric.Key), StringComparer.Ordinal);
        snapshot.Metrics.RemoveAll(metric => keys.Contains(metric.Key));
        snapshot.Metrics.AddRange(additions);
        snapshot.Distributions.RemoveAll(distribution => distribution.Key == "city-detection");
        snapshot.Distributions.Add(new("city-detection", "Repérage des villes", available, sample ?? 0,
            available ? explanation + "Quatre états disjoints ; les variantes sont recherchées par mots entiers, sans recherche approximative."
                : unavailable, buckets));
    }

    private static CityEnrichmentResult PassThrough(ExtractionResult source, string message)
        => new() { Source = source, Data = source, Message = message };

    private static DashboardColumn ResolveTitle(IReadOnlyList<string> headers, CitySettings settings,
        DashboardSettings dashboard)
    {
        if (settings.TitleColumn.Length > 0)
        {
            var columns = Enumerable.Range(0, headers.Count)
                .Where(index => string.Equals(headers[index], settings.TitleColumn, StringComparison.Ordinal)).ToArray();
            return new("title", "Titre", columns.Length == 1 ? columns[0] : null,
                settings.TitleColumn, columns.Length == 1 ? "configured" : columns.Length > 1 ? "ambiguous" : "missing-override");
        }
        var mappings = new DashboardSettings { ColumnMappings = dashboard.ColumnMappings ?? new(StringComparer.OrdinalIgnoreCase) };
        return DashboardService.ResolveColumns(headers, mappings).First(column => column.Role == "title");
    }

    private static TrieNode BuildTrie(IReadOnlyList<CityDefinition> cities, CancellationToken ct)
    {
        var root = new TrieNode();
        for (var index = 0; index < cities.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var term in cities[index].Aliases.Prepend(cities[index].Name))
            {
                var node = root;
                var tokens = CityText.Tokens(term, ct);
                if (tokens.Count == 0) continue;
                foreach (var token in tokens)
                {
                    if (!node.Children.TryGetValue(token, out var next))
                    { next = new TrieNode(); node.Children.Add(token, next); }
                    node = next;
                }
                if (!node.Cities.Contains(index)) node.Cities.Add(index);
            }
        }
        return root;
    }

    private static HashSet<int> FindCities(IReadOnlyList<string> words, TrieNode trie, CancellationToken ct)
    {
        var occurrences = new List<Occurrence>();
        var longestAtWord = new int[words.Count];
        for (var start = 0; start < words.Count; start++)
        {
            if ((start & 255) == 0) ct.ThrowIfCancellationRequested();
            var node = trie;
            Occurrence? longest = null;
            for (var end = start; end < words.Count && node.Children.TryGetValue(words[end], out node); end++)
                if (node.Cities.Count > 0) longest = new Occurrence(start, end - start + 1, node);
            // All shorter terms at the same start overlap this longer term and are superseded.
            if (longest is not { } occurrence) continue;
            occurrences.Add(occurrence);
            for (var word = start; word < start + occurrence.Length; word++)
                longestAtWord[word] = Math.Max(longestAtWord[word], occurrence.Length);
        }
        var cities = new HashSet<int>();
        for (var index = 0; index < occurrences.Count; index++)
        {
            if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
            var occurrence = occurrences[index];
            var superseded = false;
            for (var word = occurrence.Start; word < occurrence.Start + occurrence.Length; word++)
                if (longestAtWord[word] > occurrence.Length) { superseded = true; break; }
            if (!superseded) cities.UnionWith(occurrence.Node.Cities);
        }
        return cities;
    }

    private static string StateLabel(CityMatchState state) => state switch
    {
        CityMatchState.Matched => "Reconnue",
        CityMatchState.Unmatched => "Non repérée",
        CityMatchState.Ambiguous => "Ambiguë",
        CityMatchState.TitleUnavailable => "Titre indisponible",
        _ => "Indisponible"
    };
}
