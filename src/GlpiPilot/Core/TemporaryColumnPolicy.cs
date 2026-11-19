namespace GlpiPilot.Core;

/// <summary>Pure identity/order checks shared by live preparation and crash recovery.</summary>
public static class TemporaryColumnPolicy
{
    public static TemporaryColumnEntry Copy(NativeSelectedColumn column) => new()
    { Value = column.Value, PreferenceId = column.PreferenceId, Label = column.Label, Fixed = column.Fixed };

    public static TemporaryColumnView Capture(NativeColumnPreferences preferences) => new()
    { PersonalActive = preferences.PersonalActive, Columns = preferences.PersonalActive ? preferences.Selected.Select(Copy).ToList() : new() };

    public static bool SameEntry(TemporaryColumnEntry before, TemporaryColumnEntry after) => before.Identity == after.Identity
        && before.Value == after.Value && before.PreferenceId == after.PreferenceId && before.Fixed == after.Fixed
        && before.Label == after.Label;

    public static bool Same(TemporaryColumnView before, TemporaryColumnView after) => before.PersonalActive == after.PersonalActive
        && before.Columns.Count == after.Columns.Count && before.Columns.Zip(after.Columns).All(pair => SameEntry(pair.First, pair.Second));

    public static TemporaryColumnEntry? ConfirmAdded(TemporaryColumnCommand command, NativeColumnPreferences after)
    {
        var current = Capture(after);
        if (command.Kind != "add" || !command.Before.PersonalActive || !current.PersonalActive
            || current.Columns.Count != command.Before.Columns.Count + 1
            || !command.Before.Columns.Zip(current.Columns).All(pair => SameEntry(pair.First, pair.Second))) return null;
        var extra = current.Columns[^1];
        if (extra.Fixed || extra.Identity.Length == 0 || command.Before.Columns.Any(column => column.Identity == extra.Identity)) return null;
        if (after.SelectedIdsKnown) return extra.Value == command.OptionValue ? extra : null;
        // Legacy GLPI exposes preference IDs, not search-option IDs. The exact
        // appended preference, its unchanged prefix and the consumed offered
        // option together identify this single native addition.
        return extra.PreferenceId.Length > 0 && !after.Options.Any(option => option.Value == command.OptionValue)
            && (DashboardService.Normalize(extra.Label) == DashboardService.Normalize(command.OptionLabel)
                || DashboardService.Normalize(extra.Label) == DashboardService.Normalize(command.OptionHeader)) ? extra : null;
    }

    public static bool RemovedOnly(TemporaryColumnView before, TemporaryColumnView after, string identity,
        bool allowImplicitDeactivation = false) =>
        before.PersonalActive
        && before.Columns.Count(column => column.Identity == identity) == 1
        && (after.PersonalActive
                && Same(new() { PersonalActive = true, Columns = before.Columns.Where(column => column.Identity != identity).ToList() }, after)
            || allowImplicitDeactivation && !after.PersonalActive && after.Columns.Count == 0
                && before.Columns.Where(column => !column.Fixed).All(column => column.Identity == identity)
                && before.Columns.Any(column => !column.Fixed && column.Identity == identity));

    public static bool CanDisable(TemporaryColumnJournalRecord record, TemporaryColumnView current) =>
        !record.Original.PersonalActive && record.ActivationBaseline is not null
        && record.PendingCommand is null && Same(record.Expected, current);

    public static bool OwnEntryUnchanged(TemporaryColumnJournalRecord record, TemporaryColumnEntry current) =>
        record.Added.Any(added => SameEntry(added, current)) && !record.Original.Columns.Any(original => original.Identity == current.Identity);

    public static bool OnlyOwnEntriesMissing(TemporaryColumnJournalRecord record, TemporaryColumnView current)
    {
        if (!record.Expected.PersonalActive || !current.PersonalActive) return false;
        var present = current.Columns.Select(column => column.Identity).ToHashSet(StringComparer.Ordinal);
        var missing = record.Expected.Columns.Where(column => !present.Contains(column.Identity)).ToArray();
        return missing.All(column => OwnEntryUnchanged(record, column)) && Same(new()
        { PersonalActive = true, Columns = record.Expected.Columns.Where(column => present.Contains(column.Identity)).ToList() }, current);
    }

    public static TemporaryColumnEntry? ActivationCopyForDisable(TemporaryColumnJournalRecord record, NativeColumnPreferences preferences)
    {
        var current = Capture(preferences);
        if (preferences.DisableSelector.Length > 0 || !CanDisable(record, current)
            || record.ActivationBaseline is null || !Same(record.ActivationBaseline, current)) return null;
        // GLPI 11 hides disable when no option remains available to add.
        // An exact copy created by our confirmed activation is personal data
        // owned by this session; it is never an original/global preference.
        var candidate = preferences.Selected.LastOrDefault(column => !column.Fixed && column.RemoveSelector.Length > 0);
        if (candidate is null) return null;
        var entry = Copy(candidate);
        return record.ActivationBaseline.Columns.Any(column => SameEntry(column, entry)) ? entry : null;
    }
}
