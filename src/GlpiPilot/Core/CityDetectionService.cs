using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

/// <summary>Local, literal inference from an explicitly configured dictionary; never a geocoder.</summary>
public static class CityDetectionService
{
    private sealed class TrieNode
    {
        public Dictionary<string, TrieNode> Children { get; } = new(StringComparer.Ordinal);
        public List<LocationPattern> Patterns { get; } = new();
    }

    private sealed record LocationPattern(int City, string Term, CityAliasRule? Rule, SiteDefinition? Site, bool Canonical);
    private sealed record Occurrence(int Start, int Length, LocationPattern Pattern, bool? Accepted, string Detail);
    private sealed class Matcher
    {
        public TrieNode Trie { get; } = new();
        public required CitySettings Settings { get; init; }
        public required Dictionary<string, int[]> ContextColumns { get; init; }
    }

    public static CityEnrichmentResult Enrich(ExtractionResult source, CitySettings settings,
        DashboardSettings dashboard, CancellationToken ct = default, IReadOnlyList<CityTicketOverride>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(source.Headers);
        ArgumentNullException.ThrowIfNull(source.Rows);
        ct.ThrowIfCancellationRequested();
        settings.Validate();
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
        var cityColumnIndex = source.Headers.Count; var stateColumnIndex = cityColumnIndex + 1;
        string Extra(string name, bool required) { if (!required) return ""; var unique = Unique(name); headers.Add(unique); return unique; }
        var siteColumnName = Extra("Site détecté", configuration.Sites.Count > 0);
        var regionColumnName = Extra("Région de la ville", configuration.Cities.Any(city => city.Region.Length > 0));
        var departmentColumnName = Extra("Département de la ville", configuration.Cities.Any(city => city.Department.Length > 0));
        var sectorColumnName = Extra("Secteur de la ville", configuration.Cities.Any(city => city.Sector.Length > 0));
        var extraColumns = new[] { siteColumnName, regionColumnName, departmentColumnName, sectorColumnName }.Where(name => name.Length > 0).ToDictionary(name => name, headers.IndexOf, StringComparer.Ordinal);
        var citiesById = configuration.Cities.ToDictionary(city => city.Id, StringComparer.Ordinal);
        var data = new ExtractionResult
        {
            Headers = headers, Rows = new List<string[]>(source.Rows.Count),
            SourceUrl = source.SourceUrl, Title = source.Title, ExtractedAt = source.ExtractedAt,
            Pages = source.Pages, ExpectedTotal = source.ExpectedTotal, Scope = source.Scope,
            Complete = source.Complete, Warnings = new List<string>(source.Warnings), Status = source.Status,
            Provenance = source.Provenance
        };
        var matcher = BuildMatcher(configuration, source.Headers, ct);
        var sourceKey = CityOverrideStore.SourceKey(source.SourceUrl);
        var idColumn = DashboardService.ResolveColumns(source.Headers, dashboard).First(column => column.Role == "id").Index;
        string RowId(int row) => idColumn is int column && source.Rows[row] is { } cells && column < cells.Length ? cells[column]?.Trim() ?? "" : "";
        var idCounts = Enumerable.Range(0, source.Rows.Count).GroupBy(RowId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var local = (overrides ?? Array.Empty<CityTicketOverride>()).Where(item => item.SourceKey == sourceKey && sourceKey.Length > 0)
            .GroupBy(item => item.TicketId, StringComparer.Ordinal).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
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
            var match = string.IsNullOrWhiteSpace(title) || titleColumn.Index is null
                ? new CityMatch(rowIndex, null, null, CityMatchState.TitleUnavailable, Array.Empty<string>()) { Explanation = "La colonne de titre est absente, ambiguë ou vide." }
                : MatchTitle(title, original ?? Array.Empty<string>(), rowIndex, matcher, ct);
            var id = RowId(rowIndex);
            if (id.Length > 0 && idCounts[id] == 1 && local.TryGetValue(id, out var correction))
                match = ApplyCorrection(match, correction, configuration);
            switch (match.State)
            {
                case CityMatchState.Matched: matchedCount++; break;
                case CityMatchState.Unmatched: unmatchedCount++; break;
                case CityMatchState.Ambiguous: ambiguousCount++; break;
                case CityMatchState.TitleUnavailable: noTitleCount++; break;
            }
            row[cityColumnIndex] = match.CityName ?? "";
            row[stateColumnIndex] = StateLabel(match.State);
            var city = match.CityId != null ? citiesById.GetValueOrDefault(match.CityId) : null;
            void SetExtra(string name, string? value) { if (name.Length > 0) row[extraColumns[name]] = value ?? ""; }
            SetExtra(siteColumnName, match.SiteName); SetExtra(regionColumnName, city?.Region);
            SetExtra(departmentColumnName, city?.Department); SetExtra(sectorColumnName, city?.Sector);
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
            SiteColumnName = siteColumnName, RegionColumnName = regionColumnName, DepartmentColumnName = departmentColumnName,
            SectorColumnName = sectorColumnName, SiteMatchedCount = matches.Count(match => match.SiteId != null), ManualCount = matches.Count(match => match.IsOverride),
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
        var scopedMatches = available ? report.Matches.Where(match => scope!.Contains(match.RowIndex)).ToArray() : Array.Empty<CityMatch>();
        foreach (var (key, label, predicate) in new (string, string, Func<CityMatch, bool>)[]
        {
            ("site-matched", "Site précis reconnu", match => match.SiteId != null),
            ("site-ambiguous", "Site ambigu dans une ville", match => match.SiteState == "ambiguous"),
            ("city-manual", "Lieux corrigés localement", match => match.IsOverride),
            ("city-context", "Villes reconnues par contexte", match => match.Confidence == "contexte")
        })
        {
            var rows = scopedMatches.Where(predicate).Select(match => match.RowIndex).ToArray();
            additions.Add(new(key, label, available ? rows.Length : null, snapshot.Grain, available
                ? "Lieu issu du référentiel explicite ou d’une correction locale par ticket. Le détail de chaque décision est disponible dans Villes · Lieux à vérifier et Simuler." : unavailable, rows, sample));
        }
        var locationGroups = new List<DashboardDistribution>();
        foreach (var (key, label, header) in new[] { ("site-reference", "Sites du référentiel", report.SiteColumnName), ("region-reference", "Régions du référentiel", report.RegionColumnName),
            ("department-reference", "Départements du référentiel", report.DepartmentColumnName), ("sector-reference", "Secteurs du référentiel", report.SectorColumnName) })
        {
            var column = report.Data.Headers.IndexOf(header); var usable = available && header.Length > 0 && column >= 0;
            var grouped = usable ? scopedMatches.GroupBy(match => column < report.Data.Rows[match.RowIndex].Length
                && report.Data.Rows[match.RowIndex][column].Trim() is { Length: > 0 } value ? value : "(Non renseigné)", StringComparer.Ordinal)
                .Select(group => new DashboardBucket(group.Key, group.Count(), group.Select(match => match.RowIndex).ToArray())).OrderByDescending(bucket => bucket.Count).ToArray() : Array.Empty<DashboardBucket>();
            locationGroups.Add(new(key, label, usable, sample ?? 0, usable ? "Répartition selon les références locales. Les valeurs inconnues restent visibles. Les lignes appartiennent au périmètre courant."
                : "Ajoutez cette information au référentiel, puis appliquez les villes au relevé.", grouped));
        }
        ct.ThrowIfCancellationRequested();
        var keys = new HashSet<string>(additions.Select(metric => metric.Key), StringComparer.Ordinal);
        snapshot.Metrics.RemoveAll(metric => keys.Contains(metric.Key));
        snapshot.Metrics.AddRange(additions);
        snapshot.Distributions.RemoveAll(distribution => distribution.Key == "city-detection");
        snapshot.Distributions.Add(new("city-detection", "Repérage des villes", available, sample ?? 0,
            available ? explanation + "Quatre états disjoints ; les variantes sont recherchées par mots entiers, sans recherche approximative."
                : unavailable, buckets));
        var groupKeys = locationGroups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        snapshot.Distributions.RemoveAll(group => groupKeys.Contains(group.Key)); snapshot.Distributions.AddRange(locationGroups);
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

    /// <summary>Runs the exact production matcher without storing the supplied test title or context.</summary>
    public static CityMatch ExplainTitle(string title, CitySettings settings, IReadOnlyDictionary<string, string>? context = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(title); ArgumentNullException.ThrowIfNull(settings); settings.Validate(); ct.ThrowIfCancellationRequested();
        if (!settings.Enabled || settings.Cities.Count == 0)
            return new(0, null, null, CityMatchState.Unmatched, Array.Empty<string>()) { Explanation = "Le dictionnaire est désactivé ou vide." };
        if (string.IsNullOrWhiteSpace(title)) return new(0, null, null, CityMatchState.TitleUnavailable, Array.Empty<string>()) { Explanation = "Titre vide." };
        var pairs = (context ?? new Dictionary<string, string>()).ToArray();
        return MatchTitle(title, pairs.Select(pair => pair.Value).ToArray(), 0, BuildMatcher(settings, pairs.Select(pair => pair.Key).ToArray(), ct), ct);
    }

    public static IReadOnlyList<string> InspectDictionary(CitySettings settings)
    {
        settings.Validate(); var warnings = new List<string>();
        var terms = settings.Cities.SelectMany(city => city.Aliases.Prepend(city.Name).Concat(city.Rules.Select(rule => rule.Term))
            .Select(term => (City: city, Term: term, Key: string.Join(' ', CityText.Tokens(term)))));
        foreach (var group in terms.GroupBy(item => item.Key, StringComparer.Ordinal))
        {
            var cities = group.Select(item => item.City).DistinctBy(city => city.Id).ToArray();
            if (cities.Length > 1) warnings.Add("Terme partagé « " + group.First().Term + " » : " + string.Join(", ", cities.Select(city => city.Name)) + ". Vérifiez les contextes et priorités.");
            if (group.Key.Length <= 2) warnings.Add("Terme court « " + group.First().Term + " » : un contexte explicite réduit les faux rapprochements.");
        }
        return warnings.Take(200).ToArray();
    }

    private static Matcher BuildMatcher(CitySettings settings, IReadOnlyList<string> headers, CancellationToken ct)
    {
        var matcher = new Matcher { Settings = settings, ContextColumns = headers.Select((header, index) => (header, index))
            .GroupBy(item => item.header, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(item => item.index).ToArray(), StringComparer.Ordinal) };
        void Add(LocationPattern pattern)
        {
            var node = matcher.Trie;
            foreach (var token in CityText.Tokens(pattern.Term, ct))
            {
                if (!node.Children.TryGetValue(token, out var next)) { next = new(); node.Children.Add(token, next); }
                node = next;
            }
            node.Patterns.Add(pattern);
        }
        for (var index = 0; index < settings.Cities.Count; index++)
        {
            ct.ThrowIfCancellationRequested(); var city = settings.Cities[index];
            var contextual = city.Rules.Select(rule => string.Join(' ', CityText.Tokens(rule.Term))).ToHashSet(StringComparer.Ordinal);
            foreach (var term in city.Aliases.Prepend(city.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!contextual.Contains(string.Join(' ', CityText.Tokens(term)))) Add(new(index, term, null, null, term == city.Name));
            foreach (var rule in city.Rules) Add(new(index, rule.Term, rule, null, false));
        }
        foreach (var site in settings.Sites)
        {
            ct.ThrowIfCancellationRequested(); var city = settings.Cities.FindIndex(item => item.Id == site.CityId);
            foreach (var term in site.Aliases.Prepend(site.Name).Distinct(StringComparer.OrdinalIgnoreCase)) Add(new(city, term, null, site, false));
        }
        return matcher;
    }

    private static CityMatch MatchTitle(string title, IReadOnlyList<string> row, int rowIndex, Matcher matcher, CancellationToken ct)
    {
        var words = CityText.Tokens(title, ct); var occurrences = new List<Occurrence>();
        var evidence = new Dictionary<LocationPattern, CityMatchEvidence>();
        for (var start = 0; start < words.Count; start++)
        {
            if ((start & 255) == 0) ct.ThrowIfCancellationRequested(); var node = matcher.Trie;
            for (var end = start; end < words.Count && node.Children.TryGetValue(words[end], out node); end++)
                foreach (var pattern in node.Patterns)
                {
                    var city = matcher.Settings.Cities[pattern.City];
                    if (!evidence.TryGetValue(pattern, out var proof))
                    {
                        var (accepted, detail) = Accept(pattern, city, words, row, matcher, ct);
                        proof = new(city.Id, city.Name, pattern.Term, pattern.Rule?.Id ?? "", pattern.Rule?.Priority ?? 0,
                            accepted == true ? "Retenue" : accepted == false ? "Écartée" : "Indéterminée", detail);
                        evidence.Add(pattern, proof);
                    }
                    if (proof.Decision != "Écartée") occurrences.Add(new(start, end - start + 1, pattern, proof.Decision == "Retenue" ? true : null, proof.Explanation));
                    if (occurrences.Count > 20_000) throw new InvalidDataException("Un titre produit trop de correspondances de lieu pour être analysé. Réduisez les termes du dictionnaire.");
                }
        }
        // The longest overlapping literal wins only among eligible rules of the same kind.
        // A refused long context cannot suppress a shorter valid city occurrence elsewhere.
        var eligible = new List<Occurrence>();
        foreach (var kind in occurrences.GroupBy(occurrence => occurrence.Pattern.Site != null))
        {
            var longest = new int[words.Count];
            foreach (var occurrence in kind)
                for (var i = occurrence.Start; i < occurrence.Start + occurrence.Length; i++) longest[i] = Math.Max(longest[i], occurrence.Length);
            eligible.AddRange(kind.Where(occurrence => !Enumerable.Range(occurrence.Start, occurrence.Length).Any(i => longest[i] > occurrence.Length)));
        }
        var cityOccurrences = eligible.Where(item => item.Pattern.Site == null).ToArray();
        if (cityOccurrences.Length > 0)
        {
            var priority = cityOccurrences.Max(item => item.Pattern.Rule?.Priority ?? 0);
            cityOccurrences = cityOccurrences.Where(item => (item.Pattern.Rule?.Priority ?? 0) == priority).ToArray();
        }
        var sites = eligible.Where(item => item.Pattern.Site != null && item.Accepted == true).Select(item => item.Pattern.Site!).DistinctBy(site => site.Id).ToArray();
        var candidates = cityOccurrences.Select(item => matcher.Settings.Cities[item.Pattern.City].Id).Concat(sites.Select(site => site.CityId))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var proofList = evidence.Values.OrderByDescending(item => item.Priority).ThenBy(item => item.CityName, StringComparer.Ordinal).Take(80).ToArray();
        if (candidates.Length == 0)
            return new(rowIndex, null, null, CityMatchState.Unmatched, candidates) { Evidence = proofList,
                Explanation = proofList.Length == 0 ? "Aucun nom, alias ou site configuré ne correspond par mots entiers." : "Les termes présents ont été écartés par leurs contextes ou exclusions." };
        if (candidates.Length != 1 || !cityOccurrences.Any(item => item.Accepted == true) && sites.Length == 0)
            return new(rowIndex, null, null, CityMatchState.Ambiguous, candidates) { Evidence = proofList, Explanation = candidates.Length > 1
                ? "Plusieurs villes restent possibles après contexte, termes les plus longs et priorités explicites. Aucune ville n’est choisie." : "Le terme est présent mais son contexte est indisponible : aucune ville n’est attribuée." };
        var selected = matcher.Settings.Cities.First(city => city.Id == candidates[0]);
        var selectedSites = sites.Where(site => site.CityId == selected.Id).ToArray();
        var confidence = selectedSites.Length == 1 ? "site" : cityOccurrences.Any(item => item.Pattern.Canonical) ? "nom" : cityOccurrences.Any(item => item.Pattern.Rule != null) ? "contexte" : "alias";
        return new(rowIndex, selected.Id, selected.Name, CityMatchState.Matched, candidates)
        {
            SiteId = selectedSites.Length == 1 ? selectedSites[0].Id : null, SiteName = selectedSites.Length == 1 ? selectedSites[0].Name : "",
            SiteState = selectedSites.Length > 1 ? "ambiguous" : selectedSites.Length == 1 ? "matched" : "unmatched",
            Confidence = confidence, Evidence = proofList,
            Explanation = "Ville unique « " + selected.Name + " » après comparaison littérale et contextes. Origine : " + confidence + "."
                + (selectedSites.Length > 1 ? " Plusieurs sites correspondent dans cette ville ; le site reste indéterminé." : selectedSites.Length == 1 ? " Site unique : " + selectedSites[0].Name + "." : " Aucun site précis attribué.")
        };
    }

    private static (bool? Accepted, string Detail) Accept(LocationPattern pattern, CityDefinition city, IReadOnlyList<string> words,
        IReadOnlyList<string> row, Matcher matcher, CancellationToken ct)
    {
        if (city.ExcludedTerms.Any(term => Contains(words, CityText.Tokens(term, ct)))) return (false, "Une exclusion de cette ville est présente dans le titre.");
        if (pattern.Rule is not { } rule) return (true, pattern.Site != null ? "Nom ou alias exact du site." : pattern.Canonical ? "Nom de ville exact." : "Alias par mots entiers.");
        IReadOnlyList<string> context = words;
        if (rule.ContextColumn.Length > 0)
        {
            if (!matcher.ContextColumns.TryGetValue(rule.ContextColumn, out var positions) || positions.Length != 1 || positions[0] >= row.Count || string.IsNullOrWhiteSpace(row[positions[0]]))
                return (null, "Contexte absent, vide ou ambigu : colonne « " + rule.ContextColumn + " ».");
            context = CityText.Tokens(row[positions[0]], ct);
        }
        if (rule.ExcludedTerms.Any(term => Contains(context, CityText.Tokens(term, ct)))) return (false, "Un terme exclu par la règle est présent dans le contexte.");
        var missing = rule.RequiredTerms.Where(term => !Contains(context, CityText.Tokens(term, ct))).ToArray();
        if (missing.Length > 0) return (false, "Contexte requis absent : " + string.Join(" ; ", missing));
        return (true, "Tous les contextes requis sont présents, aucune exclusion ; priorité " + rule.Priority + ".");
    }

    private static bool Contains(IReadOnlyList<string> words, IReadOnlyList<string> term)
    {
        if (term.Count == 0 || term.Count > words.Count) return false;
        for (var start = 0; start <= words.Count - term.Count; start++)
        {
            var match = true;
            for (var part = 0; part < term.Count; part++) if (words[start + part] != term[part]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }

    private static CityMatch ApplyCorrection(CityMatch inferred, CityTicketOverride correction, CitySettings settings)
    {
        var city = settings.Cities.FirstOrDefault(item => item.Id == correction.CityId);
        var site = correction.SiteId.Length == 0 ? null : settings.Sites.FirstOrDefault(item => item.Id == correction.SiteId && item.CityId == correction.CityId);
        if (city == null || correction.SiteId.Length > 0 && site == null)
            return inferred with { CityId = null, CityName = null, SiteId = null, SiteName = "", State = CityMatchState.Ambiguous,
                Explanation = "La correction locale fait référence à une ville ou un site retiré du dictionnaire. Revoyez cette correction.", IsOverride = true, Confidence = "manual-invalid" };
        return inferred with { CityId = city.Id, CityName = city.Name, SiteId = site?.Id, SiteName = site?.Name ?? "", SiteState = site == null ? "unmatched" : "matched",
            State = CityMatchState.Matched, CandidateIds = new[] { city.Id }, IsOverride = true, Confidence = "manual",
            Explanation = "Correction locale explicite du ticket, enregistrée le " + correction.UpdatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm") + ". Les cellules GLPI sont conservées." };
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

/// <summary>Local corrections keyed by a GLPI installation and a unique source ticket identifier.</summary>
public sealed class CityTicketOverride
{
    public string SourceKey { get; set; } = "";
    public string TicketId { get; set; } = "";
    public string CityId { get; set; } = "";
    public string SiteId { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public CityTicketOverride Copy() => new() { SourceKey = SourceKey, TicketId = TicketId, CityId = CityId, SiteId = SiteId, UpdatedUtc = UpdatedUtc };
}

/// <summary>Never included in portable configuration. No title, cookie or credentials are stored.</summary>
public sealed class CityOverrideStore
{
    public const int MaximumOverrides = 10_000;
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 6, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public int Version { get; set; } = 1;
    public List<CityTicketOverride> Overrides { get; set; } = new();
    public static string SourceKey(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "";
        var path = uri.AbsolutePath; var front = path.IndexOf("/front/", StringComparison.OrdinalIgnoreCase);
        var prefix = front < 0 ? path.TrimEnd('/') : path[..front];
        var safe = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri;
        return safe.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + prefix;
    }
    public void Validate()
    {
        if (Version != 1 || Overrides is null || Overrides.Count > MaximumOverrides) throw new InvalidDataException("Le fichier des corrections locales est invalide.");
        var keys = new HashSet<(string, string)>();
        foreach (var item in Overrides)
            if (item == null || item.SourceKey is null || item.SourceKey.Length is 0 or > 1000 || item.SourceKey.Any(char.IsControl)
                || SourceKey(item.SourceKey) != item.SourceKey || !CityText.ValidText(item.TicketId, false)
                || !Guid.TryParse(item.CityId, out var city) || city == Guid.Empty || item.SiteId is null || item.SiteId.Length > 0 && (!Guid.TryParse(item.SiteId, out var site) || site == Guid.Empty)
                || !keys.Add((item.SourceKey, item.TicketId))) throw new InvalidDataException("Une correction locale contient une source, un ticket ou un lieu invalide ou dupliqué.");
    }
    public CityOverrideStore With(CityTicketOverride? correction, string sourceKey, string ticketId)
    {
        Validate(); var copy = new CityOverrideStore { Overrides = Overrides.Where(item => item.SourceKey != sourceKey || item.TicketId != ticketId).Select(item => item.Copy()).ToList() };
        if (correction != null)
        {
            var item = correction.Copy(); item.SourceKey = sourceKey; item.TicketId = ticketId; item.UpdatedUtc = DateTimeOffset.UtcNow;
            copy.Overrides.Add(item);
        }
        copy.Validate(); return copy;
    }
    public static CityOverrideStore Load(string path)
    {
        if (!File.Exists(path)) return new();
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Le fichier des corrections locales est trop volumineux.");
        var store = JsonSerializer.Deserialize<CityOverrideStore>(stream, Options) ?? throw new InvalidDataException("Corrections locales vides.");
        store.Validate(); return store;
    }
    public void Save(string path)
    {
        Validate(); var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Les corrections locales dépassent la taille autorisée.");
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
