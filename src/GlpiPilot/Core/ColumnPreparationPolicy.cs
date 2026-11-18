namespace GlpiPilot.Core;

/// <summary>Choose only offered native options; resolution shares the supervision's conservative mapping rules.</summary>
public static class ColumnPreparationPolicy
{
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    {
        "id", "title", "status", "priority", "urgency", "impact", "type", "created", "updated", "resolved", "closed", "due",
        "assignee", "group", "requester", "category", "entity", "location", "city", "postal", "source", "updatedBy",
        "satisfaction", "satisfaction-invited", "followupAuthor", "taskAuthor", "ttrProgress", "takeIntoAccount",
        "reportedAge", "reportedGroupCount", "reportedAuthorCount"
    };

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
        DashboardSettings settings, IReadOnlyList<NativeColumnOption> added)
    {
        if (mode == "current") return null;
        if (mode is not ("supervision" or "all")) throw new InvalidOperationException("Le mode de préparation des colonnes est inconnu.");
        var existing = added.Select(option => option.Value).ToHashSet(StringComparer.Ordinal);
        var available = Available(preferences).Where(option => !existing.Contains(option.Value)).ToArray();
        if (mode == "all") return available.FirstOrDefault();
        var present = DashboardService.ResolveColumns(headers.Concat(added.Select(Header)).ToArray(), settings)
            .Where(column => column.Index.HasValue).Select(column => column.Role).ToHashSet(StringComparer.Ordinal);
        var candidates = DashboardService.ResolveColumns(available.Select(Header).ToArray(), settings);
        var missing = candidates.FirstOrDefault(column => Roles.Contains(column.Role) && !present.Contains(column.Role) && column.Index.HasValue);
        return missing?.Index is int index ? available[index] : null;
    }

    public static List<string> MissingRoles(IReadOnlyList<string> headers, DashboardSettings settings) =>
        DashboardService.ResolveColumns(headers, settings).Where(column => Roles.Contains(column.Role) && !column.Index.HasValue)
            .Select(column => column.Label).ToList();

    public static void ApplyReceipt(ExtractionResult data, ColumnPreparationReceipt receipt)
    {
        data.Provenance.ColumnPreparation = receipt;
        foreach (var warning in receipt.Warnings)
            if (!data.Warnings.Contains(warning, StringComparer.Ordinal)) data.Warnings.Add(warning);
        if (receipt.SourceChanged)
        {
            data.Complete = false;
            data.Status = "Extraction à vérifier — liste modifiée pendant la préparation des colonnes";
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
