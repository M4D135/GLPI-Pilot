using GlpiPilot.Core;

internal static class ParticipationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var clock = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(2));
        var settings = new DashboardSettings();
        var data = Sample();
        var snapshot = DashboardService.Calculate(data, settings, clock);
        var result = DashboardParticipationService.Build(data, settings, snapshot);
        DashboardParticipationParticipant Person(string key) => result.Participants.Single(person => person.Key == key);
        check(result.Available && result.Population == 7 && result.Grain == "tickets", "Participation uses recognised active statuses only.");
        check(result.ActiveRows.SequenceEqual(new[] { 0, 1, 2, 5, 6, 7, 8 }), "Participation preserves original indices and excludes completed/unknown statuses.");
        var alice = Person("ID:11");
        check(alice.Followups.Count == 1 && alice.Tasks.Count == 1 && alice.Both.Count == 1 && alice.Writers.Count == 1,
            "Repeated names and cross-field aliases with the same numeric actor ID count once per ticket.");
        check(alice.Assigned.Count == 2 && alice.Assigned.Rows.SequenceEqual(new[] { 0, 7 }), "Assignments remain an independent, overlapping measure.");
        check(alice.ObservedNames.Contains("Alias (11)") && alice.ObservedNames.Contains("Alice autre (11)"), "Observed labels are retained for explicit roster aliases.");
        var bob = Person("ID:22");
        check(bob.Followups.Count == 2 && bob.Tasks.Count == 1 && bob.Both.Count == 1 && bob.Writers.Count == 2,
            "Distinct writer tickets equal followups plus tasks minus their intersection.");
        check(Person("NOM:NOM SANS ID").Both.Count == 1, "Bare actor names use case-insensitive whitespace-normalised identities.");
        check(Person("NOM:ALICE").Writers.Count == 1 && alice.Writers.Count == 1, "Bare names never silently merge into a numbered actor.");
        check(Person("ID:001").Writers.Count == 1 && Person("ID:1").Writers.Count == 1, "Numeric suffixes preserve exact digits, including leading zeroes.");
        check(result.WithoutWriters.Count == 1 && result.WithoutWriters.Rows.SequenceEqual(new[] { 2 })
            && result.WithoutAssignment.Rows.SequenceEqual(new[] { 2 }), "Absent writers and absent assignments are separate, inspectable measures.");
        check(result.Participants.All(person => !person.Name.Contains("Ghost", StringComparison.Ordinal)), "Unknown-status evidence is not converted to active participation.");
        check(result.Warnings.Any(warning => warning.Contains("statut absent ou inconnu", StringComparison.Ordinal)), "Unknown statuses remain visible as a quality limit.");
        check(result.Participants.Sum(person => person.Writers.Count ?? 0) > result.Population, "Participation totals across people are intentionally non-additive.");

        var subset = DashboardService.CalculateSubset(data, settings, new[] { 6, 5 }, "Périmètre test", clock);
        var scoped = DashboardParticipationService.Build(data, settings, subset);
        check(scoped.Population == 2 && scoped.ActiveRows.SequenceEqual(new[] { 5, 6 }), "A scoped calculation keeps its source indices and population.");
        check(scoped.Participants.SelectMany(person => person.Writers.Rows).All(row => row is 5 or 6), "Participation drill-down cannot escape the selected scope.");

        var roster = new DashboardParticipationOptions
        {
            Roster = new()
            {
                new() { Identity = "11", Name = "Alice", Aliases = new() { "Alias", "Alice autre" } },
                new() { Name = "Alex" },
                new() { Identity = "ID:42", Name = "Alex identifié" },
                new() { Name = "Personne absente" }
            }
        };
        var projected = DashboardParticipationService.Project(result, roster);
        check(projected.Participants[0].Name == "Alice" && projected.Participants[0].Writers.Count == 2
            && projected.Participants[0].Writers.Rows.SequenceEqual(new[] { 0, 7 }), "An explicit roster can join a bare alias to its sole known numbered identity.");
        check(projected.Participants[0].Assigned.Count == 2, "Roster projection unions source rows rather than adding duplicate aliases.");
        check(projected.Participants[1].Writers.Count == 0 && projected.Participants[2].Writers.Count == 1,
            "A name-only roster cannot choose between numbered homonyms; an exact numeric identity still works.");
        check(projected.Participants[3].Writers.Count == 0 && projected.Participants[3].Assigned.Count == 0,
            "A configured roster member with no observed ticket remains visible with measured zeroes.");
        check(projected.Warnings.Any(warning => warning.Contains("sans ambiguïté", StringComparison.Ordinal)), "Ambiguous roster identities are reported.");
        var other = projected.Participants.Single(person => person.Key == "other");
        check(other.Writers.Count == 5 && other.Writers.Rows.SequenceEqual(new[] { 0, 1, 5, 6, 8 }), "Other participants are counted once per ticket, even when several unknown actors participate.");
        check(other.Both.Count == 4 && other.Followups.Count + other.Tasks.Count - other.Both.Count == other.Writers.Count,
            "Aggregated followup/task intersection is defined within that bucket, not as a sum of per-person intersections.");
        var unspecified = projected.Participants.Single(person => person.Key == "unspecified");
        check(unspecified.Writers.Count == 1 && unspecified.Assigned.Count == 1 && unspecified.Followups.Count is null && unspecified.Both.Count is null,
            "Unspecified means no writer in either field; unrelated per-field absence counts are not fabricated.");
        var dynamicResult = DashboardParticipationService.Project(result, new());
        check(dynamicResult.Participants.Count == result.Participants.Count + 1
            && dynamicResult.Participants.Where(person => !person.IsAggregate).SequenceEqual(result.Participants)
            && dynamicResult.Participants.All(person => person.Key != "other"),
            "An empty roster preserves every dynamic actor and adds unspecified without inventing an Others bucket.");
        var dynamicUnspecified = dynamicResult.Participants.Single(person => person.Key == "unspecified");
        check(dynamicUnspecified.Writers.Rows.SequenceEqual(new[] { 2 }) && dynamicUnspecified.Assigned.Rows.SequenceEqual(new[] { 2 })
            && dynamicUnspecified.Followups.Count is null && dynamicUnspecified.Tasks.Count is null && dynamicUnspecified.Both.Count is null,
            "Dynamic unspecified uses the independent absence measures and never fabricates per-field counts.");
        check(ReferenceEquals(DashboardParticipationService.Project(result, new() { IncludeUnspecified = false }), result),
            "An empty roster with unspecified disabled keeps the original model unchanged.");
        check(result.Participants.All(person => !person.IsAggregate) && !ReferenceEquals(result, dynamicResult),
            "Adding dynamic unspecified creates a separate result without mutating the shared snapshot.");
        var noExtras = DashboardParticipationService.Project(result, new() { Roster = roster.Roster, IncludeOthers = false, IncludeUnspecified = false });
        check(noExtras.Participants.Count == 4, "Optional aggregate rows can be hidden without changing the selected population.");
        check(result.Participants.Count != projected.Participants.Count && result.Participants.All(person => !person.IsAggregate), "Projection does not mutate the source participation model.");

        var collision = DashboardParticipationService.Project(result, new()
        {
            Roster = new() { new() { Name = "Alice", Aliases = new() { "Alias" } }, new() { Name = "Second label", Aliases = new() { "Alias", "Alice" } } }
        });
        check(collision.Participants.Take(2).All(person => person.Writers.Count == 0)
            && collision.Warnings.Any(warning => warning.Contains("sans ambiguïté", StringComparison.Ordinal)), "Conflicting configured aliases never use first-match ordering to choose a person.");

        var distinctNames = new ExtractionResult
        {
            Headers = new() { "ID", "Statut", "Suivis - Rédacteur", "Tâches - Rédacteur" },
            Complete = true, ExpectedTotal = 5,
            Rows = new()
            {
                new[] { "1", "Nouveau", "Anne-Marie", "" },
                new[] { "2", "Nouveau", "Anne Marie", "" },
                new[] { "3", "Nouveau", "René", "" },
                new[] { "4", "Nouveau", "Rene", "" },
                new[] { "5", "Nouveau", "Rene\u0301", "" }
            }
        };
        var distinctNamesResult = DashboardParticipationService.Build(distinctNames, settings,
            DashboardService.Calculate(distinctNames, settings, clock));
        var exactNames = DashboardParticipationService.Project(distinctNamesResult, new()
        {
            Roster = new() { new() { Name = "anne   marie" }, new() { Name = "Rene" }, new() { Name = "René" } }
        });
        check(exactNames.Participants[0].Writers.Rows.SequenceEqual(new[] { 1 })
            && exactNames.Participants[1].Writers.Rows.SequenceEqual(new[] { 3 })
            && exactNames.Participants[2].Writers.Rows.SequenceEqual(new[] { 2, 4 })
            && exactNames.Participants.Single(person => person.Key == "other").Writers.Rows.SequenceEqual(new[] { 0 }),
            "Roster names preserve accents and punctuation while normalising case, spaces and canonically equivalent Unicode.");
        var explicitNameAliases = DashboardParticipationService.Project(distinctNamesResult, new()
        {
            Roster = new()
            {
                new() { Name = "Anne Marie", Aliases = new() { "Anne-Marie" } },
                new() { Name = "Rene", Aliases = new() { "René" } }
            }
        });
        check(explicitNameAliases.Participants[0].Writers.Rows.SequenceEqual(new[] { 0, 1 })
            && explicitNameAliases.Participants[1].Writers.Rows.SequenceEqual(new[] { 2, 3, 4 }),
            "Accent and punctuation variants join a roster member only through explicitly configured aliases.");

        var partial = new ExtractionResult
        {
            Headers = new() { "ID", "Statut", "Suivis - Rédacteur", "Attribué à - Technicien" },
            Rows = new() { new[] { "1", "Nouveau", "Case (9)", "Case (9)" } }, Complete = false
        };
        var partialResult = DashboardParticipationService.Build(partial, settings, DashboardService.Calculate(partial, settings, clock));
        var partialActor = partialResult.Participants.Single();
        check(partialActor.Followups.Count == 1 && partialActor.Assigned.Count == 1 && partialActor.Tasks.Count is null
            && partialActor.Both.Count is null && partialActor.Writers.Count is null && partialResult.WithoutWriters.Count is null,
            "A missing author column is unknown, not an empty column or a reliable union.");
        check(partialResult.Warnings.Any(warning => warning.Contains("exhaustivité", StringComparison.Ordinal)), "Partial receipt remains an explicit limitation.");
        var missingStatus = new ExtractionResult { Headers = new() { "ID", "Suivis - Rédacteur" }, Rows = new() { new[] { "1", "Case (9)" } } };
        var missingStatusResult = DashboardParticipationService.Build(missingStatus, settings, DashboardService.Calculate(missingStatus, settings, clock));
        check(!missingStatusResult.Available && !missingStatusResult.StatusAvailable && missingStatusResult.Participants.Count == 0,
            "Without a status column no active participation is inferred.");
        var allUnknown = new ExtractionResult { Headers = new() { "ID", "Statut", "Suivis - Rédacteur" }, Rows = new() { new[] { "1", "Personnalisé", "Case (9)" } } };
        check(!DashboardParticipationService.Build(allUnknown, settings, DashboardService.Calculate(allUnknown, settings, clock)).Available,
            "An entirely unknown status population does not become a measured empty active population.");

        var duplicates = Sample();
        duplicates.Rows.Add((string[])duplicates.Rows[0].Clone());
        duplicates.ExpectedTotal = duplicates.Rows.Count;
        var duplicateResult = DashboardParticipationService.Build(duplicates, settings, DashboardService.Calculate(duplicates, settings, clock));
        check(duplicateResult.Grain == "lignes" && duplicateResult.Participants.Single(person => person.Key == "ID:11").Writers.Count == 2,
            "Duplicate ticket IDs preserve every observed occurrence and do not claim distinct ticket counts.");

        var custom = new ExtractionResult
        {
            Headers = new() { "ID", "État maison", "Auteur S", "Auteur T", "Attribué à - Technicien" },
            Rows = new() { new[] { "1", "Ouvert maison", "Texte initial", "Beta (2)~Alpha (1)", "Alpha (1)" } }, Complete = true, ExpectedTotal = 1
        };
        var customSettings = new DashboardSettings
        {
            MultiValueSeparators = "~",
            ColumnMappings = new(StringComparer.OrdinalIgnoreCase) { ["status"] = "État maison", ["followupAuthor"] = "Auteur S", ["taskAuthor"] = "Auteur T" },
            ValueDictionaries = new(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = new(StringComparer.OrdinalIgnoreCase) { ["Ouvert maison"] = "Nouveau" },
                ["followupAuthor"] = new(StringComparer.OrdinalIgnoreCase) { ["Texte initial"] = "Alpha (1)~Beta (2)" }
            }
        };
        var customResult = DashboardParticipationService.Build(custom, customSettings, DashboardService.Calculate(custom, customSettings, clock));
        check(customResult.Participants.Count == 2 && customResult.Participants.All(person => person.Both.Count == 1),
            "Column mappings, dictionaries and configured separators are applied consistently with the dashboard.");
        check(custom.Rows[0][2] == "Texte initial", "Participation never rewrites source values.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try { DashboardParticipationService.Build(data, settings, snapshot, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "Participation construction observes cancellation.");
        cancelled = false;
        try { DashboardParticipationService.Project(result, roster, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "Roster projection observes cancellation.");
        var rejected = false;
        try { DashboardParticipationService.Project(result, new() { Roster = new() { new() { Identity = "ID:invalid" } } }); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "Malformed explicit numeric identities are rejected.");
    }

    private static ExtractionResult Sample() => new()
    {
        Headers = new() { "ID", "Statut", "Suivis - Rédacteur", "Tâches - Rédacteur", "Attribué à - Technicien", "Attribué à - Groupe de techniciens" },
        Complete = true, ExpectedTotal = 9,
        Rows = new()
        {
            new[] { "101", "Nouveau", "Alice (11)\nAlias (11)\nBob (22)", "Alice autre (11)\nCharlie (33)", "Alice (11)", "Nord\nSud" },
            new[] { "102", "En attente", "bob (22)", "Bob changé (22)", "Charlie (33)\nCHARLIE (33)", "Nord" },
            new[] { "103", "En cours (attribué)", "", "", "", "Sud" },
            new[] { "104", "Résolu", "Alice (11)", "Bob (22)", "Charlie (33)", "Nord" },
            new[] { "105", "Personnalisé", "Ghost (99)", "Ghost (99)", "Ghost (99)", "Nord" },
            new[] { "106", "En cours (planifié)", "Nom Sans Id", "nom sans id", "Autre", "Sud" },
            new[] { "107", "Nouveau", "Alex (41)", "Alex (42)", "ALEX (42)", "Nord" },
            new[] { "108", "Nouveau", "Alice", "", "Alice (11)", "Nord" },
            new[] { "109", "Nouveau", "Dana (001)", "Dana (1)", "Dana (1)", "Sud" }
        }
    };
}
