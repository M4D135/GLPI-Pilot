using System.Text.Json;
using GlpiPilot.Core;

internal static class ColumnCompatibilityChecks
{
    private static readonly DateTimeOffset Clock = new(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(1));
    private static readonly string[] Canonical = { "ID", "Titre", "Statut", "Dernière modification", "Date d'ouverture", "Priorité",
        "Demandeur - Demandeur", "Attribué à - Technicien", "Catégorie", "Suivis - Rédacteur", "Tâches - Rédacteur",
        "TTR + Progression", "Statistiques - Délai de prise en compte", "Type", "Attribué à - Groupe de techniciens",
        "Âge (jours)", "Nombre de groupes", "Nombre de rédacteurs", "Description" };
    private static readonly string[] Short = { "ID", "Titre", "Statut", "Dernière modif", "Ouverture", "Priorité", "Demandeur",
        "Technicien", "Catégorie", "Suivis-Rédacteur", "Tâches-Rédacteur", "TTR/Progression", "Délai prise en compte", "Type",
        "Groupe", "Âge", "Nb groupes", "Nb rédacteurs", "Description" };
    private static readonly string[] Roles = { "id", "title", "status", "updated", "created", "priority", "requester", "assignee", "category",
        "followupAuthor", "taskAuthor", "ttrProgress", "takeIntoAccount", "type", "group", "reportedAge", "reportedGroupCount", "reportedAuthorCount", "description" };

    public static void Register(Action<string, Action> test, Action<bool, string> check)
    {
        test("Supervision canonical and shortened workbook headers yield the same active groups, priorities, oldest and writer populations", () =>
        {
            var canonical = DashboardService.Calculate(Source(Canonical), new(), Clock);
            var source = Source(Short); var result = DashboardService.Calculate(source, new(), Clock);
            for (var index = 0; index < Roles.Length; index++)
                check(result.Column(Roles[index])?.Index == index, "Shortened exported header not recognised: " + Short[index]);
            CheckPopulations(canonical, check); CheckPopulations(result, check);
            check(JsonSerializer.Serialize(source.Headers) == JsonSerializer.Serialize(Short), "Source headers must remain unchanged.");
            check(result.Column("due")?.Index is null, "A TTR/progression text column must not become a resolution deadline.");
        });

        test("Mapped column formatting changes preserve supervision values and mark the recovered binding", () =>
        {
            var source = Source(Canonical); var settings = Mapped();
            for (var index = 0; index < source.Headers.Count; index++)
                source.Headers[index] = "  " + source.Headers[index].ToUpperInvariant().Normalize(System.Text.NormalizationForm.FormD).Replace(" ", "\u00a0\n") + "  ";
            var before = JsonSerializer.Serialize(settings);
            var result = DashboardService.Calculate(source, settings, Clock);
            CheckPopulations(result, check);
            check(result.Columns.Where(column => column.Index.HasValue).All(column => column.State == "configured-normalized"),
                "Formatting-only recovery must be distinguishable from an exact manual binding.");
            check(result.Column("status")?.StateLabel == "Libellé configuré normalisé", "The recovery must have a readable mapping state.");
            check(before == JsonSerializer.Serialize(settings), "Reading equivalent headers must not rewrite persisted mappings.");
        });

        test("Previously configured known GLPI aliases recover unique ticket namespaces and explain the rebound headers", () =>
        {
            var source = Source(Short); var settings = Mapped();
            source.Headers[2] = "Ticket : Caractéristiques - Statut";
            source.Headers[5] = "Tickets > Priorité";
            var before = JsonSerializer.Serialize(settings);
            var result = DashboardService.Calculate(source, settings, Clock);
            CheckPopulations(result, check);
            check(result.Column("status")?.State == "configured-alias" && result.Column("group")?.State == "configured-alias",
                "Known aliases must recover only a unique corresponding role.");
            check(result.Column("status")?.StateLabel == "Libellé GLPI équivalent reconnu", "Alias recovery must be visible in column diagnostics.");
            check(result.Warnings.Any(warning => warning.Contains("Statut", StringComparison.Ordinal)
                && warning.Contains("Ticket : Caractéristiques - Statut", StringComparison.Ordinal)), "A rebound must identify the old and actual column labels.");
            check(before == JsonSerializer.Serialize(settings), "Migration must preserve the user's explicit saved mappings.");
        });

        test("Custom mapped labels accept formatting equivalents without switching to an unrelated automatic role", () =>
        {
            var settings = new DashboardSettings { ColumnMappings = new() { ["status"] = "État métier" } };
            var source = Source(Canonical); source.Headers[2] = " e\u0301TAT\u00a0 ME\u0301TIER ";
            var result = DashboardService.Calculate(source, settings, Clock);
            check(result.Column("status") is { Index: 2, State: "configured-normalized" } && result.Metric("active")?.Value == 3,
                "The selected custom column must survive accents, spacing and case changes.");
            source.Headers[2] = "Statut"; result = DashboardService.Calculate(source, settings, Clock);
            check(result.Column("status") is { Index: null, State: "missing-override" } && result.Metric("active")?.Value is null,
                "A genuinely missing custom mapping must not silently fall back to an automatic role.");
        });

        test("Exact manual mappings still win and equivalent competing or importer-renamed columns stay ambiguous", () =>
        {
            var settings = new DashboardSettings { ColumnMappings = new() { ["status"] = "Statut" } };
            DashboardColumn Resolve(params string[] headers) => DashboardService.ResolveColumns(headers, settings).Single(column => column.Role == "status");
            check(Resolve("État", "Statut", "Ticket - Statut") is { Index: 1, State: "configured" }, "An existing explicit header keeps precedence.");
            check(Resolve(" statut ", "STATUT") is { Index: null, State: "ambiguous" }, "Two formatting equivalents cannot be chosen arbitrarily.");
            check(Resolve(" statut ", " statut  (2)") is { Index: null, State: "ambiguous" }, "Formatting recovery cannot choose the first importer-renamed duplicate.");
            check(Resolve("STATUT", "STATUT\u00a0(2) ") is { Index: null, State: "ambiguous" }, "Duplicate detection also accepts nonbreaking and trailing whitespace.");
            check(Resolve("État", "Ticket - Statut") is { Index: null, State: "ambiguous" }, "Two equivalent role aliases remain ambiguous.");
            check(Resolve("Ticket - Statut", "Ticket - Statut (2)") is { Index: null, State: "ambiguous" }, "Importer-renamed duplicates remain visible after alias migration.");
            settings.ColumnMappings["status"] = "Statut (2)";
            check(Resolve("Statut", "Statut (2)") is { Index: 1, State: "configured" }, "An explicit duplicate selection stays valid.");
            check(Resolve("Statut") is { Index: null, State: "missing-override" }, "A missing explicit duplicate never rebinds to its sibling.");
        });

        test("Known role migration never adopts requester, task or other object namespaces", () =>
        {
            var settings = new DashboardSettings { ColumnMappings = new()
                { ["id"] = "ID", ["status"] = "Statut", ["group"] = "Groupe", ["created"] = "Ouverture", ["priority"] = "Priorité" } };
            var columns = DashboardService.ResolveColumns(new[] { "Demandeur - ID", "Tâches - Statut", "Demandeur - Groupe", "Problèmes - Date d'ouverture", "Autre - Priorité" }, settings);
            check(new[] { "id", "status", "group", "created", "priority" }.All(role => columns.Single(column => column.Role == role) is { Index: null, State: "missing-override" }),
                "Equivalent suffixes in foreign namespaces must never be treated as ticket fields.");
        });

        test("Header-keyed value dictionaries survive a recovered binding while explicit role and current-header dictionaries keep precedence", () =>
        {
            var source = Source(Canonical); source.Headers[2] = "État"; source.Rows[0][2] = "À traiter";
            var settings = new DashboardSettings { ColumnMappings = new() { ["status"] = "Statut" },
                ValueDictionaries = new() { ["Statut"] = new() { ["À traiter"] = "Nouveau" } } };
            var result = DashboardService.Calculate(source, settings, Clock);
            check(result.Metric("active")?.Value == 3, "A mapped header rename must retain its explicitly configured value dictionary.");
            settings.ValueDictionaries["État"] = new() { ["À traiter"] = "Résolu" };
            result = DashboardService.Calculate(source, settings, Clock);
            check(result.Metric("active")?.Value == 2, "A dictionary for the current actual header takes precedence over the former header.");
            settings.ValueDictionaries["status"] = new() { ["À traiter"] = "Nouveau" };
            result = DashboardService.Calculate(source, settings, Clock);
            check(result.Metric("active")?.Value == 3, "A role dictionary retains precedence over header dictionaries.");
            settings.ValueDictionaries.Remove("status"); settings.ValueDictionaries.Remove("État");
            settings.ColumnMappings["status"] = "État métier";
            check(DashboardService.NormalizeValue("À traiter", "status", "État", settings) == "À traiter",
                "An unrelated missing mapping cannot lend its dictionary to another role header.");
        });
    }

    private static DashboardSettings Mapped() => new()
    {
        ColumnMappings = Roles.Select((role, index) => new KeyValuePair<string, string>(role, Canonical[index]))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
    };

    private static ExtractionResult Source(IEnumerable<string> headers) => new()
    {
        Headers = headers.ToList(), Complete = true, ExpectedTotal = 4, ExtractedAt = Clock,
        Rows = new()
        {
            new[] { "101", "Premier", "Nouveau", "17/11/2026 10:00", "01/11/2026 10:00", "Haute", "Usager", "Alice", "Postes", "Alice", "Alice", "25 %", "12", "Incident", "Support;Terrain", "17", "2", "1", "Demande A" },
            new[] { "102", "Second", "En attente", "17/11/2026 10:00", "02/11/2026 10:00", "Basse", "Usager", "Bob", "Réseau", "Alice", "Bob", "50 %", "15", "Demande", "Support", "16", "1", "2", "Demande B" },
            new[] { "103", "Troisième", "En cours (Attribué)", "17/11/2026 10:00", "03/11/2026 10:00", "Moyenne", "Usager", "Alice", "Postes", "Bob", "", "75 %", "10", "Incident", "Terrain", "15", "1", "1", "Demande C" },
            new[] { "104", "Fermé", "Clos", "17/11/2026 10:00", "01/10/2026 10:00", "Majeure", "Usager", "Alice", "Postes", "Alice", "Alice", "100 %", "10", "Incident", "Support", "48", "1", "1", "Demande D" }
        }
    };

    private static void CheckPopulations(DashboardSnapshot result, Action<bool, string> check)
    {
        check(result.Metric("active")?.Value == 3, "Exactly the three open source occurrences must remain active.");
        var priorities = result.Distributions.Single(distribution => distribution.Key == "active-priority");
        check(priorities.Available && priorities.Buckets.Sum(bucket => bucket.Count) == 3, "Active priorities must be available and exclude the closed major ticket.");
        var groups = result.Matrices.Single(matrix => matrix.Key == "group-status");
        check(groups.Available && groups.Cells.Sum(cell => cell.Value) == 4, "Group/status must retain four memberships from three active tickets.");
        check(result.OldestGroups is { Available: true } oldest && oldest.Oldest.Select(ticket => ticket.Id).SequenceEqual(new[] { "101", "102", "103" }),
            "Both oldest rankings must use real active opening dates.");
        var participation = result.Participation;
        check(participation.StatusAvailable && participation.FollowupsAvailable && participation.TasksAvailable, "Writer participation fields must remain available.");
        var alice = participation.Participants.Single(person => person.Name == "Alice");
        check(alice.Followups.Count == 2 && alice.Tasks.Count == 1 && alice.Both.Count == 1 && alice.Writers.Count == 2,
            "Followups, tasks, intersection and union must retain their original populations.");
    }
}
