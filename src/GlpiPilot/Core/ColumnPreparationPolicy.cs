namespace GlpiPilot.Core;

/// <summary>Choose offered fields used by the application, respecting the active source's conservative mapping rules.</summary>
public static class ColumnPreparationPolicy
{
    // Operational roles support the built-in views, cohorts and filters even
    // when their current widget is hidden. Large texts are opt-in below.
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    {
        "id", "title", "status", "priority", "urgency", "impact", "type", "created", "updated", "resolved", "closed", "due",
        "assignee", "group", "requester", "category", "entity", "location", "city", "postal", "source", "updatedBy",
        "satisfaction", "satisfaction-invited", "followupAuthor", "taskAuthor", "ttrProgress", "takeIntoAccount",
        "reportedAge", "reportedGroupCount", "reportedAuthorCount"
    };

    private static (HashSet<string> Roles, HashSet<string> ExactHeaders) UsefulFields(DashboardSettings settings, QueryLibrarySettings? queries)
    {
        var roles = new HashSet<string>(Roles, StringComparer.Ordinal);
        var exactHeaders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in DashboardService.Roles)
            if (settings.ColumnMappings.TryGetValue(role.Key, out var configured) && !string.IsNullOrWhiteSpace(configured))
                roles.Add(role.Key);
        if (UsesSolution(settings)) roles.Add("solution");
        foreach (var query in queries?.Queries ?? new())
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Field(string field)
            {
                if (!visited.Add(field)) return;
                if (field is "role:description" or "role:solution") roles.Add(field[5..]);
                if (field.StartsWith("column:", StringComparison.Ordinal) && field.Length > 7) exactHeaders.Add(field[7..]);
                if (field.StartsWith("calc:", StringComparison.Ordinal)
                    && query.ComputedFields.FirstOrDefault(computed => computed.Key == field[5..]) is { } computed)
                { Field(computed.Field); Field(computed.OtherField); }
            }
            Field(query.GroupBy); Field(query.GroupBySecondary); Field(query.GroupByTertiary);
            if (query.Aggregate is not ("count" or "percent")) Field(query.AggregateField);
            foreach (var condition in query.Conditions)
            {
                Field(condition.Field);
                if (condition.Operator is not ("empty" or "not-empty") && condition.ValueSource == "field") Field(condition.Value);
                if (condition.Operator is "number-between" or "number-outside" or "date-between" or "date-outside"
                    && condition.Value2Source == "field") Field(condition.Value2);
            }
        }
        return (roles, exactHeaders);
    }

    private static bool UsesSolution(DashboardSettings settings)
    {
        static bool Metric(string kind, string key) => (kind is "metric" or "history")
            && key is "with-solution" or "completed-no-solution";
        static bool Filters(IEnumerable<DashboardScopeFilter>? filters) =>
            filters?.Any(filter => Metric(filter.Kind, filter.SourceKey)) == true;
        static bool Board(DashboardBoardSettings board) => Filters(board.Filters)
            || board.ComparisonEnabled && Filters(board.ComparisonFilters)
            || Filters(board.SavedSelection?.CrossFilters)
            || board.Widgets.Any(widget => !widget.Hidden
                && !board.Sections.Any(section => section.Hidden && section.Key == widget.Section)
                && (Metric(widget.Kind, widget.SourceKey) || Filters(widget.Filters)));
        // Saved compositions belong to this source's current configuration;
        // unrelated catalog favorites and inactive filter presets do not.
        return settings.Layout.Boards.Concat(settings.PersonalLayout.Boards).Concat(settings.Business.BoardOverrides).Any(Board)
            || settings.Business.WeatherRules.Any(rule => (rule.Visible || rule.WeatherEnabled) && Metric(rule.Kind, rule.SourceKey));
    }

    public static string Header(NativeColumnOption option)
    {
        var group = DashboardService.Normalize(option.Group);
        var label = DashboardService.Normalize(option.Label);
        return group.Length == 0 || label == group || label.StartsWith(group + " ", StringComparison.Ordinal)
            ? option.Label : option.Group + " - " + option.Label;
    }

    public static IReadOnlyList<NativeColumnOption> Available(NativeColumnPreferences preferences)
    {
        var selected = preferences.SelectedIdsKnown ? preferences.Selected.Select(item => item.Value).ToHashSet(StringComparer.Ordinal) : new();
        return preferences.Options.Where(option => !option.Disabled && option.Value.Length > 0 && !selected.Contains(option.Value)).ToArray();
    }

    public static NativeColumnOption? Next(string mode, IReadOnlyList<string> headers, NativeColumnPreferences preferences,
        DashboardSettings settings, IReadOnlyList<NativeColumnOption> added, QueryLibrarySettings? queries = null)
    {
        if (mode == "current") return null;
        if (mode is not ("supervision" or "all")) throw new InvalidOperationException("Le mode de préparation des colonnes est inconnu.");
        var existing = added.Select(option => option.Value).ToHashSet(StringComparer.Ordinal);
        var available = Available(preferences).Where(option => !existing.Contains(option.Value)).ToArray();
        if (mode == "all") return available.FirstOrDefault();
        var useful = UsefulFields(settings, queries);
        var currentHeaders = headers.Concat(added.Select(Header)).ToArray();
        var present = DashboardService.ResolveColumns(currentHeaders, settings)
            .Where(column => column.Index.HasValue).Select(column => column.Role).ToHashSet(StringComparer.Ordinal);
        var candidates = DashboardService.ResolveColumns(available.Select(Header).ToArray(), settings);
        var missing = candidates.FirstOrDefault(column => useful.Roles.Contains(column.Role) && !present.Contains(column.Role) && column.Index.HasValue);
        if (missing?.Index is int index) return available[index];
        // A column: reference has the same exact, case-sensitive identity as in
        // QueryService. Do not substitute an alias, strip its namespace, or choose
        // one of two offered fields that would produce the same header.
        var offered = available.GroupBy(Header, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return available.FirstOrDefault(option => useful.ExactHeaders.Contains(Header(option))
            && !currentHeaders.Contains(Header(option), StringComparer.Ordinal) && offered[Header(option)] == 1);
    }

    public static List<string> MissingRoles(IReadOnlyList<string> headers, DashboardSettings settings, QueryLibrarySettings? queries = null)
    {
        var useful = UsefulFields(settings, queries);
        var missing = DashboardService.ResolveColumns(headers, settings).Where(column => useful.Roles.Contains(column.Role) && !column.Index.HasValue)
            .Select(column => column.Label).ToList();
        missing.AddRange(useful.ExactHeaders.Where(header => headers.Count(value => string.Equals(value, header, StringComparison.Ordinal)) != 1)
            .Select(header => "Colonne exacte « " + header + " »"));
        return missing;
    }

    public static void ApplyReceipt(ExtractionResult data, ColumnPreparationReceipt receipt)
    {
        data.Provenance.ColumnPreparation = receipt;
        foreach (var warning in receipt.Warnings)
            if (!data.Warnings.Contains(warning, StringComparer.Ordinal)) data.Warnings.Add(warning);
        receipt.SourceRevalidated = receipt.SourceChanged && data.Complete
            && data.Provenance.Method == "native-csv" && data.Provenance.ScopeVerified
            && data.Quality is { ReceptionComplete: true, FinalTotalVerified: true, ScopeStable: true,
                SampleIdentityVerified: true, RepresentationVerified: true };
        if (receipt.SourceChanged && !receipt.SourceRevalidated)
        {
            data.Complete = false;
            data.Status = "Extraction à vérifier — liste modifiée pendant la préparation des colonnes";
        }
        else if (receipt.SourceRevalidated)
        {
            const string confirmed = "La liste a évolué avant l’export. Le CSV a ensuite été vérifié sur le nouvel échantillon, le total et le périmètre ; les données de cette collecte restent exploitables.";
            if (!data.Warnings.Contains(confirmed, StringComparer.Ordinal)) data.Warnings.Add(confirmed);
        }
    }

    public static bool PreservesHeaders(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var index = 0;
        foreach (var header in after)
            if (index < before.Count && DashboardService.Normalize(before[index]) == DashboardService.Normalize(header)) index++;
        return index == before.Count;
    }

    public static bool SourceChanged(TableSnapshot before, TableSnapshot after, DashboardSettings settings)
    {
        if (before.ExpectedTotal.HasValue && after.ExpectedTotal.HasValue && before.ExpectedTotal != after.ExpectedTotal) return true;
        static string[] Ids(TableSnapshot sample, DashboardSettings mapping)
        {
            var column = DashboardService.ResolveColumns(sample.Headers, mapping).Single(item => item.Role == "id").Index;
            if (column is null) return Array.Empty<string>();
            return sample.Rows.Take(64).Select(row =>
            {
                try { return column.Value < row.Length ? TicketReferenceResolver.NormalizeId(row[column.Value]) : ""; }
                catch (InvalidOperationException) { return ""; }
            }).ToArray();
        }
        var a = Ids(before, settings); var b = Ids(after, settings);
        return a.Length > 0 && b.Length > 0 && a.All(id => id.Length > 0) && b.All(id => id.Length > 0) && !a.SequenceEqual(b, StringComparer.Ordinal);
    }
}
