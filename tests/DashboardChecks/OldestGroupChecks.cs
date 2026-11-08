using GlpiPilot.Core;

/// <summary>Only synthetic source occurrences; no workbook-specific groups or identities.</summary>
public static class OldestGroupChecks
{
    private static readonly DateTimeOffset Clock = new(2026, 4, 8, 12, 0, 0, TimeSpan.FromHours(2));

    public static void Run(Action<bool, string> assert)
    {
        MembershipAndQuality(assert);
        OrderingAndColours(assert);
        ScopeAndOccurrences(assert);
        MappingsAndTimeZone(assert);
        MissingInputsAndCancellation(assert);
    }

    private static void MembershipAndQuality(Action<bool, string> check)
    {
        var data = Source(
            Row("101", "01/04/2026 09:00", "Groupe Alpha; Groupe Bêta; GROUPE ALPHA"),
            Row("102", "02/04/2026", "Groupe Alpha", type: "Demande"),
            Row("103", "date illisible", "Groupe Alpha"),
            Row("104", "", "Groupe Alpha"),
            Row("105", "09/04/2026", "Groupe Alpha"),
            Row("106", "03/04/2026", "Groupe Bêta", status: "Résolu"),
            Row("107", "04/04/2026", "Groupe Bêta", status: "Clos"),
            Row("108", "05/04/2026", "Groupe Bêta", status: "Statut personnalisé"),
            Row("109", "06/04/2026", "", type: "Type non classé"),
            Row("110", "N/A", "Sans date"));
        var result = Calculate(data);
        var alpha = result.Groups.Single(group => group.Label == "Groupe Alpha");
        var beta = result.Groups.Single(group => group.Label == "Groupe Bêta");
        check(result.Available && result.Count == 7 && result.Rows.SequenceEqual(new[] { 0, 1, 2, 3, 4, 8, 9 }),
            "Oldest: stock contains all recognised active types, excluding resolved, closed and unknown statuses.");
        check(result.UnknownStatusRows.SequenceEqual(new[] { 7 }), "Oldest: unknown statuses stay outside the active stock and remain traceable.");
        check(alpha.Count == 5 && alpha.Rows.SequenceEqual(new[] { 0, 1, 2, 3, 4 }) && beta.Rows.SequenceEqual(new[] { 0 }),
            "Oldest: each source occurrence joins every group once, including case-repeated names in the same cell.");
        check(result.Groups.Sum(group => group.Count) == 8 && result.Count == 7,
            "Oldest: multigroup participation never inflates the global stock or becomes an additive total.");
        check(result.Oldest.Select(ticket => ticket.Index).SequenceEqual(new[] { 0, 1, 8 }),
            "Oldest: the global focus ranking contains each source row once despite memberships in several groups.");
        check(alpha.DatedCount == 2 && alpha.UnrankedCount == 3 && alpha.Oldest.Select(ticket => ticket.Index).SequenceEqual(new[] { 0, 1 }),
            "Oldest: the group stock retains unranked dates while its ranking contains valid openings only.");
        check(result.MissingDateRows.SequenceEqual(new[] { 3, 9 }) && result.InvalidDateRows.SequenceEqual(new[] { 2 })
            && result.FutureDateRows.SequenceEqual(new[] { 4 }) && result.DatedRows.SequenceEqual(new[] { 0, 1, 8 }),
            "Oldest: missing, invalid, future and valid source rows form separate, exact date populations.");
        check(alpha.MissingDateRows.SequenceEqual(new[] { 3 }) && alpha.InvalidDateRows.SequenceEqual(new[] { 2 })
            && alpha.FutureDateRows.SequenceEqual(new[] { 4 }), "Oldest: date-quality drilldowns keep original indexes within each group.");
        check(result.Groups.Single(group => group.Key == "unassigned:").Rows.SequenceEqual(new[] { 8 }),
            "Oldest: missing assignment creates a traceable Sans groupe bucket.");
        var noDate = result.Groups[^1];
        check(noDate.Label == "Sans date" && noDate.Count == 1 && noDate.Oldest.Count == 0 && noDate.MaximumAgeDays is null
            && noDate.OldestOpenedAt is null && noDate.Color == "", "Oldest: unrankable groups remain visible at the end without a fabricated age or colour.");
        check(result.Groups.Select(group => group.Label).SequenceEqual(new[] { "Groupe Alpha", "Groupe Bêta", "Sans groupe", "Sans date" }),
            "Oldest: groups sort by their oldest timestamp, then case-insensitive name, with unrankable groups last.");
        check(result.ReferenceDate == Clock.Date && result.MaximumAgeDays == 7 && alpha.Oldest[0].AgeDays == 7,
            "Oldest: calendar age uses the fixed snapshot reference day, not elapsed hours or the declared source age.");
        check(data.Rows[0][4] == "Groupe Alpha; Groupe Bêta; GROUPE ALPHA" && data.Rows[0][8] == "999",
            "Oldest: group splitting, age recomputation and classification never mutate the original source.");
    }

    private static void OrderingAndColours(Action<bool, string> check)
    {
        var data = Source(
            Row("10", "01/04/2026 09:00"), Row("2", "01/04/2026 09:00"),
            Row("900", "01/04/2026 08:00"), Row("3", "02/04/2026 09:00"),
            Row("4", "03/04/2026 09:00"), Row("5", "04/04/2026 09:00"),
            Row("6", "05/04/2026 09:00"));
        var result = Calculate(data);
        check(result.TopCount == 5 && result.Groups.Single().Count == 7
            && result.Groups.Single().Oldest.Select(ticket => ticket.Index).SequenceEqual(new[] { 2, 1, 0, 3, 4 }),
            "Oldest: the default top five uses opening timestamp before numeric ID, while stock retains all seven rows.");
        var ten = Calculate(data, topCount: 10);
        check(ten.TopCount == 10 && ten.Groups.Single().Oldest.Count == 7 && ten.Groups.Single().Count == 7,
            "Oldest: the focus projection can request ten occurrences without changing stock, dates or scale.");
        check(result.Oldest.Count == 5 && ten.Oldest.Count == 7 && result.Oldest.Select(ticket => ticket.Index).SequenceEqual(new[] { 2, 1, 0, 3, 4 }),
            "Oldest: the global focus obeys its requested bound and the same timestamp, numeric-ID and source-index order.");
        var one = Calculate(data, topCount: 1);
        check(one.Groups.Single().Oldest.Single().Index == 2 && one.MaximumAgeDays == result.MaximumAgeDays,
            "Oldest: a shorter ranking preserves the same maximum and exact oldest occurrence.");

        var colours = Calculate(Source(
            Row("201", "31/03/2026 23:59", "Groupe Huit; Groupe Partagé"),
            Row("202", "04/04/2026 00:00", "Groupe Quatre; Groupe Partagé"),
            Row("203", "08/04/2026 23:59", "Groupe Zéro")));
        var eight = colours.Groups.Single(group => group.Label == "Groupe Huit").Oldest.Single();
        var four = colours.Groups.Single(group => group.Label == "Groupe Quatre").Oldest.Single();
        var zero = colours.Groups.Single(group => group.Label == "Groupe Zéro").Oldest.Single();
        check(eight.AgeDays == 8 && eight.RelativeAge == 1 && eight.Color == "#FCB38A" && colours.MaximumAgeDays == 8,
            "Oldest: the common maximum uses calendar days and the documented warm endpoint.");
        check(four.AgeDays == 4 && four.RelativeAge == .5 && four.Color == "#F4D4C2",
            "Oldest: a group with half the global maximum does not receive its own maximum colour.");
        check(zero.AgeDays == 0 && zero.RelativeAge == 0 && zero.Color == "#EBF5FB" && colours.FutureDateRows.Length == 0,
            "Oldest: same-calendar-day openings are valid at age zero, including a later hour on that day.");
        check(colours.Groups.Single(group => group.Label == "Groupe Partagé").Oldest[1].Color == four.Color,
            "Oldest: an occurrence has identical age and colour in every group it joins.");
        var allToday = Calculate(Source(Row("1", "08/04/2026 00:01"), Row("2", "08/04/2026 08:00")));
        check(allToday.MaximumAgeDays == 0 && allToday.Groups.Single().Oldest.All(ticket => ticket.RelativeAge == 0 && ticket.Color == "#EBF5FB"),
            "Oldest: an all-zero-age scope has a finite, neutral common scale.");

        var tied = Calculate(Source(Row("1 200", "01/04/2026"), Row("24", "01/04/2026"),
            Row("18446744073709551615", "01/04/2026"), Row("id non numérique", "01/04/2026"), Row("", "01/04/2026")));
        check(tied.Groups.Single().Oldest.Select(ticket => ticket.Index).SequenceEqual(new[] { 1, 0, 2, 3, 4 }),
            "Oldest: grouped and large numeric IDs sort numerically; unusable IDs retain source order after them.");
        var groupTimestamps = Calculate(Source(Row("1", "01/04/2026 20:00", "Groupe A"), Row("2", "01/04/2026 08:00", "Groupe Z")));
        check(groupTimestamps.Groups[0].Label == "Groupe Z", "Oldest: equal calendar ages do not discard the timestamp when ordering groups.");
    }

    private static void ScopeAndOccurrences(Action<bool, string> check)
    {
        var data = Source(Row("301", "01/04/2026", "Groupe A"), Row("302", "02/04/2026", "Groupe A"),
            Row("302", "02/04/2026", "Groupe A"), Row("303", "03/04/2026", "Groupe B"));
        var settings = new DashboardSettings();
        var snapshot = DashboardService.Calculate(data, settings, Clock);
        var result = DashboardOldestGroups.Calculate(data, settings, snapshot);
        check(result.Grain == "occurrences" && result.Count == 4 && result.Groups.Single(group => group.Label == "Groupe A").Count == 3,
            "Oldest: repeated IDs remain separate source occurrences, matching the snapshot grain.");
        check(result.Groups.Single(group => group.Label == "Groupe A").Oldest.Where(ticket => ticket.Id == "302")
            .Select(ticket => ticket.Index).SequenceEqual(new[] { 1, 2 }) && snapshot.Metric("duplicate")!.Rows.SequenceEqual(new[] { 1, 2 }),
            "Oldest: identical IDs and timestamps retain both original indexes; duplicate-ID quality remains independent.");
        check(result.Oldest.Where(ticket => ticket.Id == "302").Select(ticket => ticket.Index).SequenceEqual(new[] { 1, 2 }),
            "Oldest: the global focus removes repeated memberships only, never repeated source IDs.");
        var subset = DashboardService.CalculateSubset(data, settings, new[] { 3, 2 }, "Périmètre synthétique", Clock);
        var scoped = DashboardOldestGroups.Calculate(data, settings, subset);
        check(scoped.Rows.SequenceEqual(new[] { 2, 3 }) && scoped.Groups.SelectMany(group => group.Oldest).Select(ticket => ticket.Index).Order()
            .SequenceEqual(new[] { 2, 3 }) && scoped.MaximumAgeDays == 6 && scoped.Grain == "tickets",
            "Oldest: a real subset retains source indexes, recomputes its common maximum and uses its scoped grain.");
        var empty = DashboardService.CalculateSubset(data, settings, Array.Empty<int>(), "Périmètre vide", Clock);
        var none = DashboardOldestGroups.Calculate(data, settings, empty);
        check(none.Available && none.Count == 0 && none.Groups.Count == 0 && none.MaximumAgeDays is null,
            "Oldest: an empty valid scope never falls back to the full extraction.");
        var collision = Calculate(Source(Row("1", "01/04/2026", ""), Row("2", "02/04/2026", "Sans groupe")));
        check(collision.Groups.Count == 2 && collision.Groups.Select(group => group.Key).Distinct().Count() == 2
            && collision.Groups.All(group => group.Count == 1), "Oldest: a literal group named Sans groupe does not absorb missing assignments.");
    }

    private static void MappingsAndTimeZone(Action<bool, string> check)
    {
        var data = Source(Row("401", "2026-04-07T22:30:00Z", "Code A/Code B/CODE A", status: "OUVERT"));
        data.Headers[3] = "Ouverture exportée";
        data.Headers[4] = "Affectations exportées";
        var settings = new DashboardSettings { MultiValueSeparators = "/", ColumnMappings = new()
            { ["created"] = "Ouverture exportée", ["group"] = "Affectations exportées" },
            ValueDictionaries = new() { ["status"] = new() { ["OUVERT"] = "Nouveau" } } };
        var result = Calculate(data, settings);
        check(result.Available && result.Groups.Count == 2 && result.Count == 1
            && result.Groups.All(group => group.Oldest.Single().AgeDays == 0 && group.Oldest.Single().OpenedAt == new DateTime(2026, 4, 8, 0, 30, 0)),
            "Oldest: explicit mappings, value dictionaries, custom group separators and timezone-bearing openings use dashboard semantics.");
        var sourceZone = new DashboardSettings { SourceTimeZone = "UTC" };
        var zoned = Calculate(Source(Row("402", "07/04/2026 22:30")), sourceZone);
        check(zoned.Groups.Single().Oldest.Single().AgeDays == 0,
            "Oldest: a configured source timezone is applied before calendar-day subtraction.");
        var shortRow = Source(new[] { "403", "Titre court", "Nouveau" });
        var shortResult = Calculate(shortRow);
        check(shortResult.Available && shortResult.Count == 1 && shortResult.MissingDateRows.SequenceEqual(new[] { 0 })
            && shortResult.Groups.Single().Key == "unassigned:", "Oldest: short source rows retain stock with missing date and group values.");
        var groupDictionary = new DashboardSettings { ValueDictionaries = new() { ["group"] = new() { ["Brut"] = "Groupe normalisé" } } };
        check(Calculate(Source(Row("404", "01/04/2026", "Brut")), groupDictionary).Groups.Single().Label == "Groupe normalisé",
            "Oldest: configured group value dictionaries also apply to the oldest-groups projection.");
    }

    private static void MissingInputsAndCancellation(Action<bool, string> check)
    {
        foreach (var column in new[] { 2, 3, 4 })
        {
            var data = Source(Row("501", "01/04/2026"));
            data.Headers[column] = "Sans correspondance";
            var result = Calculate(data);
            check(!result.Available && result.Groups.Count == 0 && result.UnavailableReason.Contains("Correspondances", StringComparison.Ordinal),
                "Oldest: a missing status, opening or group column stays unavailable instead of fabricating counts or ages.");
        }
        var unknown = Calculate(Source(Row("502", "01/04/2026", status: "Inconnu")));
        check(!unknown.Available && unknown.Count == 0 && unknown.UnknownStatusRows.SequenceEqual(new[] { 0 }),
            "Oldest: a scope with no known statuses cannot claim an empty, healthy active stock.");
        var inactive = Calculate(Source(Row("503", "01/04/2026", status: "Résolu")));
        check(inactive.Available && inactive.Count == 0 && inactive.Groups.Count == 0,
            "Oldest: a completely known, completed-only scope has a valid zero active stock.");

        var source = Source(Row("504", "01/04/2026"));
        var settings = new DashboardSettings();
        var snapshot = DashboardService.Calculate(source, settings, Clock);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Throws<OperationCanceledException>(() => DashboardOldestGroups.Calculate(source, settings, snapshot, ct: cancelled.Token), check,
            "Oldest: cancellation is honoured before projection work starts.");
        foreach (var limit in new[] { 0, 31 }) Throws<ArgumentOutOfRangeException>(
            () => DashboardOldestGroups.Calculate(source, settings, snapshot, topCount: limit), check,
            "Oldest: a ranking limit outside 1..30 is rejected explicitly.");
        var badScope = new DashboardSnapshot { SourceRowCount = 1, RowCount = 1, ScopeRows = new[] { 3 } };
        Throws<ArgumentException>(() => DashboardOldestGroups.Calculate(source, settings, badScope), check,
            "Oldest: a missing source index never produces a plausible result from another row.");
        var duplicateScope = new DashboardSnapshot { SourceRowCount = 1, RowCount = 2, ScopeRows = new[] { 0, 0 } };
        Throws<ArgumentException>(() => DashboardOldestGroups.Calculate(source, settings, duplicateScope), check,
            "Oldest: repeated scope indexes are rejected separately from repeated ticket IDs.");
        var wrongPopulation = new DashboardSnapshot { SourceRowCount = 1, RowCount = 0, ScopeRows = Array.Empty<int>(),
            Metrics = new() { new("active", "Actifs", 1, "tickets", "", new[] { 0 }) } };
        Throws<ArgumentException>(() => DashboardOldestGroups.Calculate(source, settings, wrongPopulation), check,
            "Oldest: active-population indexes cannot escape the selected snapshot scope.");
    }

    private static DashboardOldestGroupsSnapshot Calculate(ExtractionResult data, DashboardSettings? settings = null, int topCount = 5)
    {
        settings ??= new();
        return DashboardOldestGroups.Calculate(data, settings, DashboardService.Calculate(data, settings, Clock), topCount);
    }
    private static ExtractionResult Source(params string[][] rows) => new()
    {
        Headers = new() { "ID", "Titre", "Statut", "Date d'ouverture", "Attribué à - Groupe de techniciens", "Attribué à - Technicien", "Priorité", "Type", "Âge (jours)" },
        Rows = rows.ToList(), ExpectedTotal = rows.Length, Complete = true, ExtractedAt = Clock,
        SourceUrl = "https://glpi.example.invalid/front/ticket.php", Scope = "Données synthétiques"
    };
    private static string[] Row(string id, string date, string group = "Groupe Alpha", string status = "Nouveau", string type = "Incident")
        => new[] { id, "Ticket synthétique " + id, status, date, group, "Agent exemple", "Moyenne", type, "999" };
    private static void Throws<T>(Action action, Action<bool, string> check, string message) where T : Exception
    {
        var thrown = false;
        try { action(); } catch (T) { thrown = true; }
        check(thrown, message);
    }
}
