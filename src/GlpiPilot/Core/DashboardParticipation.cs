using System.Text;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

public sealed record DashboardParticipationMeasure(int? Count, int[] Rows);

public sealed record DashboardParticipationParticipant(string Key, string Name, bool IsAggregate,
    DashboardParticipationMeasure Followups, DashboardParticipationMeasure Tasks,
    DashboardParticipationMeasure Both, DashboardParticipationMeasure Writers,
    DashboardParticipationMeasure Assigned, IReadOnlyList<string> ObservedNames);

public sealed class DashboardParticipationOptions
{
    public List<DashboardParticipationRosterEntry> Roster { get; set; } = new();
    public bool IncludeOthers { get; set; } = true;
    public bool IncludeUnspecified { get; set; } = true;
}

public sealed class DashboardParticipationResult
{
    public bool Available { get; init; }
    public bool StatusAvailable { get; init; }
    public bool FollowupsAvailable { get; init; }
    public bool TasksAvailable { get; init; }
    public bool AssignmentAvailable { get; init; }
    public string Grain { get; init; } = "lignes";
    public int Population { get; init; }
    public int[] ActiveRows { get; init; } = Array.Empty<int>();
    public IReadOnlyList<DashboardParticipationParticipant> Participants { get; init; } = Array.Empty<DashboardParticipationParticipant>();
    public DashboardParticipationMeasure WithoutWriters { get; init; } = new(null, Array.Empty<int>());
    public DashboardParticipationMeasure WithoutAssignment { get; init; } = new(null, Array.Empty<int>());
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string Explanation { get; init; } = "";
}

/// <summary>
/// Participation in the active rows of one supplied snapshot. One row per actor
/// and source occurrence, never a count of actions or a measure of productivity.
/// Source indices and duplicate ticket occurrences remain intact.
/// </summary>
public static class DashboardParticipationService
{
    private sealed class Actor
    {
        public required string Key;
        public required string Name;
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> Followups { get; } = new();
        public HashSet<int> Tasks { get; } = new();
        public HashSet<int> Assigned { get; } = new();
    }

    public static DashboardParticipationResult Build(ExtractionResult data, DashboardSettings settings,
        DashboardSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();
        var columns = snapshot.Columns.ToDictionary(column => column.Role, column => column.Index, StringComparer.Ordinal);
        bool Has(string role) => columns.GetValueOrDefault(role) is int index && index >= 0 && index < data.Headers.Count;
        string Cell(int row, string role) => columns.GetValueOrDefault(role) is int index && index >= 0 && index < data.Headers.Count
            && index < data.Rows[row].Length ? DashboardService.NormalizeValue(data.Rows[row][index], role, data.Headers[index], settings) : "";
        var statusAvailable = Has("status") && snapshot.Metric("active")?.Value.HasValue == true;
        var followupsAvailable = Has("followupAuthor");
        var tasksAvailable = Has("taskAuthor");
        var assignmentAvailable = Has("assignee");
        var activeRows = statusAvailable ? snapshot.Metric("active")!.Rows.Distinct().Order().ToArray() : Array.Empty<int>();
        var scopeRows = snapshot.ScopeRows.ToHashSet();
        if (activeRows.Any(index => index < 0 || index >= data.Rows.Count || !scopeRows.Contains(index)))
            throw new ArgumentException("Le périmètre de participation contient une ligne absente du relevé.", nameof(snapshot));
        var actors = new Dictionary<string, Actor>(StringComparer.Ordinal);
        var withoutWriters = new List<int>();
        var withoutAssignment = new List<int>();
        var warnings = new List<string>();
        if (!statusAvailable) warnings.Add("Les statuts actifs ne peuvent pas être établis ; la participation est indisponible.");
        if (!followupsAvailable) warnings.Add("La colonne des rédacteurs de suivis est absente ou ambiguë ; l’union des rédacteurs reste indisponible.");
        if (!tasksAvailable) warnings.Add("La colonne des rédacteurs de tâches est absente ou ambiguë ; l’union des rédacteurs reste indisponible.");
        if (!assignmentAvailable) warnings.Add("La colonne des techniciens attribués est absente ou ambiguë ; les attributions restent indisponibles.");
        if (snapshot.Metric("unknown-status")?.Value is > 0)
            warnings.Add("Les lignes de statut absent ou inconnu sont exclues des actifs ; aucune activité n’est supposée pour ces lignes.");
        if (snapshot.Grain != "tickets")
            warnings.Add("Les identifiants ne permettent pas de garantir des tickets uniques : les valeurs comptent des occurrences de lignes, sans dédoublonnage implicite.");
        if (!snapshot.Complete)
            warnings.Add("La participation décrit uniquement les lignes disponibles dans ce relevé ; son exhaustivité n’est pas confirmée.");

        for (var position = 0; position < activeRows.Length; position++)
        {
            if ((position & 255) == 0) ct.ThrowIfCancellationRequested();
            var row = activeRows[position];
            var followups = ReadActors(Cell(row, "followupAuthor"), settings.MultiValueSeparators);
            var tasks = ReadActors(Cell(row, "taskAuthor"), settings.MultiValueSeparators);
            var assigned = ReadActors(Cell(row, "assignee"), settings.MultiValueSeparators);
            Add(followups, row, actor => actor.Followups);
            Add(tasks, row, actor => actor.Tasks);
            Add(assigned, row, actor => actor.Assigned);
            if (followupsAvailable && tasksAvailable && followups.Count == 0 && tasks.Count == 0) withoutWriters.Add(row);
            if (assignmentAvailable && assigned.Count == 0) withoutAssignment.Add(row);
        }

        var participants = actors.Values.Select(actor => new DashboardParticipationParticipant(actor.Key, actor.Name, false,
            Measure(actor.Followups, statusAvailable && followupsAvailable),
            Measure(actor.Tasks, statusAvailable && tasksAvailable),
            Measure(actor.Followups.Intersect(actor.Tasks), statusAvailable && followupsAvailable && tasksAvailable),
            Measure(actor.Followups.Union(actor.Tasks), statusAvailable && followupsAvailable && tasksAvailable),
            Measure(actor.Assigned, statusAvailable && assignmentAvailable), actor.Names.ToArray()))
            .OrderByDescending(actor => actor.Writers.Count ?? actor.Followups.Count ?? actor.Tasks.Count ?? 0)
            .ThenByDescending(actor => actor.Assigned.Count ?? 0)
            .ThenBy(actor => actor.Name, StringComparer.OrdinalIgnoreCase).ThenBy(actor => actor.Key, StringComparer.Ordinal).ToArray();
        ct.ThrowIfCancellationRequested();
        return new()
        {
            Available = statusAvailable && (followupsAvailable || tasksAvailable || assignmentAvailable),
            StatusAvailable = statusAvailable, FollowupsAvailable = followupsAvailable,
            TasksAvailable = tasksAvailable, AssignmentAvailable = assignmentAvailable,
            Grain = snapshot.Grain, Population = activeRows.Length, ActiveRows = activeRows, Participants = participants,
            WithoutWriters = Measure(withoutWriters, statusAvailable && followupsAvailable && tasksAvailable),
            WithoutAssignment = Measure(withoutAssignment, statusAvailable && assignmentAvailable),
            Warnings = warnings,
            Explanation = "Statuts actifs reconnus uniquement. Chaque personne compte une fois par ligne de ticket dans chaque mesure. "
                + "Rédacteur = union des suivis et des tâches ; les deux = intersection. L’attribution vient du champ Technicien et peut recouper la rédaction. "
                + "Les volumes entre personnes et entre mesures ne sont pas additifs. Ils ne comptent ni les interventions, ni le temps passé, ni la productivité. "
                + "L’identité utilise le suffixe numérique entre parenthèses lorsqu’il existe ; sinon le nom exact, avec casse et espaces normalisés."
        };

        void Add(IReadOnlyList<(string Key, string Name)> people, int row, Func<Actor, HashSet<int>> select)
        {
            foreach (var person in people)
            {
                if (!actors.TryGetValue(person.Key, out var actor))
                {
                    actor = new() { Key = person.Key, Name = person.Name };
                    actors.Add(person.Key, actor);
                }
                actor.Names.Add(person.Name);
                select(actor).Add(row);
            }
        }
    }

    /// <summary>
    /// Optional explicit roster. A name never merges two different numbered
    /// homonyms. Explicit identities win over names; ambiguous candidates stay
    /// in Others. Aggregates retain row unions, rather than adding actor counts.
    /// </summary>
    public static DashboardParticipationResult Project(DashboardParticipationResult source,
        DashboardParticipationOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        DashboardParticipationRosterEntry.ValidateList(options.Roster);
        ct.ThrowIfCancellationRequested();
        if (options.Roster.Count == 0)
        {
            if (!options.IncludeUnspecified) return source;
            var dynamicParticipants = source.Participants.Where(person => person.Key != "unspecified").ToList();
            dynamicParticipants.Add(Unspecified(source));
            return WithParticipants(source, dynamicParticipants, source.Warnings,
                source.Explanation + " Non renseigné indique l’absence de tout rédacteur ou de tout technicien dans les colonnes disponibles.");
        }
        var roster = options.Roster.Select(entry => entry.Copy()).ToArray();
        var identities = roster.Select(entry => CanonicalRosterIdentity(entry.Identity)).ToArray();
        var identityTargets = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var nameTargets = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var i = 0; i < roster.Length; i++)
        {
            if (identities[i].Length > 0) Index(identityTargets, identities[i], i);
            foreach (var name in roster[i].Aliases.Append(roster[i].Name)
                .Append(identities[i].StartsWith("NOM:", StringComparison.Ordinal) ? identities[i][4..] : ""))
            {
                var key = NameMatchKey(name);
                if (key.Length > 0) Index(nameTargets, key, i);
            }
        }
        var numberedNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var actor in source.Participants.Where(actor => !actor.IsAggregate && actor.Key.StartsWith("ID:", StringComparison.Ordinal)))
            foreach (var name in actor.ObservedNames.Append(actor.Name))
            {
                var key = NameMatchKey(name);
                if (key.Length == 0) continue;
                if (!numberedNames.TryGetValue(key, out var keys)) numberedNames.Add(key, keys = new(StringComparer.Ordinal));
                keys.Add(actor.Key);
            }
        var buckets = roster.Select(_ => new List<DashboardParticipationParticipant>()).ToArray();
        var others = new List<DashboardParticipationParticipant>();
        var ambiguous = 0;
        var position = 0;
        foreach (var actor in source.Participants.Where(actor => !actor.IsAggregate))
        {
            if ((position++ & 255) == 0) ct.ThrowIfCancellationRequested();
            var candidates = new HashSet<int>();
            var ambiguousName = false;
            if (identityTargets.TryGetValue(actor.Key, out var exact)) candidates.UnionWith(exact);
            else
            {
                foreach (var name in actor.ObservedNames.Append(actor.Name))
                {
                    var key = NameMatchKey(name);
                    if (!nameTargets.TryGetValue(key, out var possible)) continue;
                    if (numberedNames.TryGetValue(key, out var sameNameIds) && sameNameIds.Count > 1)
                    { ambiguousName = true; continue; }
                    foreach (var candidate in possible)
                    {
                        // An explicit numbered identity cannot be replaced by a
                        // different numbered person just because the name agrees.
                        if (actor.Key.StartsWith("ID:", StringComparison.Ordinal)
                            && identities[candidate].StartsWith("ID:", StringComparison.Ordinal)
                            && identities[candidate] != actor.Key) continue;
                        // A bare name is accepted for an explicit ID only when
                        // its sole numbered namesake, if observed, is that ID.
                        if (actor.Key.StartsWith("NOM:", StringComparison.Ordinal)
                            && identities[candidate].StartsWith("ID:", StringComparison.Ordinal)
                            && numberedNames.TryGetValue(key, out var knownIds)
                            && knownIds.Count == 1 && !knownIds.Contains(identities[candidate])) continue;
                        candidates.Add(candidate);
                    }
                }
            }
            if (candidates.Count == 1) buckets[candidates.Single()].Add(actor);
            else
            {
                others.Add(actor);
                if (candidates.Count > 1 || ambiguousName) ambiguous++;
            }
        }
        var participants = new List<DashboardParticipationParticipant>();
        for (var i = 0; i < roster.Length; i++)
        {
            var name = Collapse(roster[i].Name);
            if (name.Length == 0) name = identities[i];
            participants.Add(Aggregate("roster:" + i, name, buckets[i]));
        }
        if (options.IncludeOthers) participants.Add(Aggregate("other", "Autres", others));
        if (options.IncludeUnspecified) participants.Add(Unspecified(source));
        var warnings = source.Warnings.ToList();
        if (ambiguous > 0)
            warnings.Add($"{ambiguous:N0} identités ne peuvent pas être rattachées sans ambiguïté à la liste configurée ; elles restent dans Autres, sans choix automatique.");
        ct.ThrowIfCancellationRequested();
        return WithParticipants(source, participants, warnings,
            source.Explanation + " La liste configurée regroupe les alias explicites ; chaque ligne compte une fois par regroupement. "
                + "Pour un regroupement, Les deux signifie qu’un rédacteur de suivi et un rédacteur de tâche y figurent, éventuellement deux personnes différentes. "
                + "Non renseigné indique l’absence de tout rédacteur ou de tout technicien dans les colonnes disponibles.");

        DashboardParticipationParticipant Aggregate(string key, string name, IReadOnlyList<DashboardParticipationParticipant> members)
        {
            var followups = members.SelectMany(member => member.Followups.Rows).Distinct().Order().ToArray();
            var tasks = members.SelectMany(member => member.Tasks.Rows).Distinct().Order().ToArray();
            return new(key, name, true,
                Measure(followups, source.StatusAvailable && source.FollowupsAvailable),
                Measure(tasks, source.StatusAvailable && source.TasksAvailable),
                Measure(followups.Intersect(tasks), source.StatusAvailable && source.FollowupsAvailable && source.TasksAvailable),
                Measure(followups.Union(tasks), source.StatusAvailable && source.FollowupsAvailable && source.TasksAvailable),
                Measure(members.SelectMany(member => member.Assigned.Rows), source.StatusAvailable && source.AssignmentAvailable),
                members.SelectMany(member => member.ObservedNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    private static DashboardParticipationParticipant Unspecified(DashboardParticipationResult source) =>
        new("unspecified", "Non renseigné", true, Unavailable(), Unavailable(), Unavailable(),
            source.WithoutWriters, source.WithoutAssignment, Array.Empty<string>());

    private static DashboardParticipationResult WithParticipants(DashboardParticipationResult source,
        IReadOnlyList<DashboardParticipationParticipant> participants, IReadOnlyList<string> warnings, string explanation) => new()
    {
        Available = source.Available, StatusAvailable = source.StatusAvailable,
        FollowupsAvailable = source.FollowupsAvailable, TasksAvailable = source.TasksAvailable,
        AssignmentAvailable = source.AssignmentAvailable, Grain = source.Grain,
        Population = source.Population, ActiveRows = source.ActiveRows, Participants = participants,
        WithoutWriters = source.WithoutWriters, WithoutAssignment = source.WithoutAssignment,
        Warnings = warnings, Explanation = explanation
    };

    private static IReadOnlyList<(string Key, string Name)> ReadActors(string raw, string separators) =>
        DashboardService.SplitTechnicianNames(raw, separators).Select(name => (Key: ActorKey(name), Name: Collapse(name))).ToArray();

    private static DashboardParticipationMeasure Measure(IEnumerable<int> rows, bool available)
    {
        if (!available) return Unavailable();
        var indices = rows.Distinct().Order().ToArray();
        return new(indices.Length, indices);
    }

    private static DashboardParticipationMeasure Unavailable() => new(null, Array.Empty<int>());
    private static string Collapse(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();

    private static string ActorKey(string name)
    {
        name = Collapse(name);
        var match = Regex.Match(name, @"\(([0-9]+)\)$");
        return match.Success ? "ID:" + match.Groups[1].Value : "NOM:" + name.ToUpperInvariant();
    }

    private static string CanonicalRosterIdentity(string value)
    {
        value = Collapse(value);
        if (value.Length == 0) return "";
        if (value.StartsWith("ID:", StringComparison.OrdinalIgnoreCase))
        {
            var digits = value[3..].Trim();
            if (digits.Length == 0 || digits.Any(character => character is < '0' or > '9'))
                throw new InvalidDataException("Une identité ID: doit être suivie uniquement de chiffres.");
            return "ID:" + digits;
        }
        if (value.StartsWith("NOM:", StringComparison.OrdinalIgnoreCase))
        {
            var name = Collapse(value[4..]);
            if (name.Length == 0) throw new InvalidDataException("Une identité NOM: demande un nom non vide.");
            return "NOM:" + name.ToUpperInvariant();
        }
        if (value.All(character => character is >= '0' and <= '9')) return "ID:" + value;
        return ActorKey(value);
    }

    // A roster name is an explicit identity hint, not a fuzzy search term.
    // Keep accents and punctuation: variants require an explicit alias.
    private static string NameMatchKey(string name) =>
        Regex.Replace(Collapse(name), @"\s*\([0-9]+\)$", "").ToUpperInvariant();

    private static void Index(Dictionary<string, HashSet<int>> dictionary, string key, int value)
    {
        if (!dictionary.TryGetValue(key, out var values)) dictionary.Add(key, values = new());
        values.Add(value);
    }
}
