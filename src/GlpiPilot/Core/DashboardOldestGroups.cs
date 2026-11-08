using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

/// <summary>A source occurrence, never an implicitly deduplicated ticket identifier.</summary>
public sealed record DashboardOldestTicket(int Index, string Id, string Title, string Status,
    string Priority, string Assignee, DateTime OpenedAt, int AgeDays, double RelativeAge, string Color);

public sealed record DashboardOldestGroup(string Key, string Label, int[] Rows, int[] DatedRows,
    int[] MissingDateRows, int[] InvalidDateRows, int[] FutureDateRows, DateTime? OldestOpenedAt,
    int? MaximumAgeDays, string Color, IReadOnlyList<DashboardOldestTicket> Oldest)
{
    public int Count => Rows.Length;
    public int DatedCount => DatedRows.Length;
    public int MissingDateCount => MissingDateRows.Length;
    public int InvalidDateCount => InvalidDateRows.Length;
    public int FutureDateCount => FutureDateRows.Length;
    public int UnrankedCount => MissingDateRows.Length + InvalidDateRows.Length + FutureDateRows.Length;
}

public sealed record DashboardOldestGroupsSnapshot(bool Available, string UnavailableReason,
    DateTime ReferenceDate, string Grain, int TopCount, int[] Rows, int[] DatedRows,
    int[] MissingDateRows, int[] InvalidDateRows, int[] FutureDateRows, int[] UnknownStatusRows,
    int? MaximumAgeDays, IReadOnlyList<DashboardOldestGroup> Groups, string Explanation)
{
    // One ranking for the complete selected scope. Multigroup membership never
    // repeats an index here; repeated IDs in different source rows remain distinct.
    public IReadOnlyList<DashboardOldestTicket> Oldest { get; init; } = Array.Empty<DashboardOldestTicket>();
    public int Count => Rows.Length;
    public int DatedCount => DatedRows.Length;
    public int MissingDateCount => MissingDateRows.Length;
    public int InvalidDateCount => InvalidDateRows.Length;
    public int FutureDateCount => FutureDateRows.Length;
    public int UnknownStatusCount => UnknownStatusRows.Length;
    public int UnrankedCount => MissingDateRows.Length + InvalidDateRows.Length + FutureDateRows.Length;
}

/// <summary>
/// Oldest active occurrences per assigned group, with original source indexes.
/// A repeated group within one cell creates one membership; repeated source IDs still
/// create separate occurrences. Snapshot duplicate-ID diagnostics remain independent.
/// The common colour scale describes relative calendar age, never SLA compliance.
/// </summary>
public static class DashboardOldestGroups
{
    public const int TicketsPerGroup = 5;
    private const string Explanation = "Stock actif reconnu, tous types de tickets, dans le périmètre sélectionné. "
        + "Une ligne multigroupe participe à chaque groupe ; les stocks des groupes ne s’additionnent pas. "
        + "Les identifiants répétés dans le relevé restent des occurrences distinctes ; le contrôle des ID répétés du relevé est indépendant des participations multigroupes. "
        + "Le stock inclut les dates d’ouverture absentes, invalides ou postérieures au jour de référence, présentées séparément et exclues du classement. "
        + "Âge = jour de référence − jour d’ouverture, en jours calendaires ; une ouverture le même jour a un âge de zéro. "
        + "Les ouvertures retenues sont triées par date et heure, puis ID numérique, puis index source pour départager les égalités. "
        + "Les groupes suivent leur ouverture la plus ancienne, puis leur nom ; les groupes sans date valide sont placés à la fin. "
        + "La couleur compare l’âge au maximum commun du périmètre : repère visuel relatif, sans seuil SLA ni interprétation météo. "
        + "Les statuts inconnus sont exclus du stock actif et signalés séparément.";

    private enum OpeningState { Dated, Missing, Invalid, Future }
    private sealed record Occurrence(int Index, string Id, ulong? NumericId, string Title, string Status,
        string Priority, string Assignee, DateTime? OpenedAt, int? AgeDays, OpeningState State);
    private sealed class GroupBuilder(string key, string label)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public List<Occurrence> Members { get; } = new();
    }

    /// <summary>
    /// Call after the snapshot's columns and active-status metric have been calculated.
    /// The supplied snapshot fixes both the scope and the common reference clock; an
    /// empty scope stays empty and is never replaced by the complete extraction.
    /// </summary>
    public static DashboardOldestGroupsSnapshot Calculate(ExtractionResult data, DashboardSettings settings,
        DashboardSnapshot snapshot, int topCount = TicketsPerGroup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (topCount is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(topCount), "Le classement accepte de 1 à 30 ouvertures par groupe.");
        ct.ThrowIfCancellationRequested();
        DashboardService.ValidateSettings(settings);

        var scope = new HashSet<int>();
        if (snapshot.SourceRowCount != data.Rows.Count || snapshot.RowCount != snapshot.ScopeRows.Length)
            throw new ArgumentException("Le périmètre des plus anciens ne correspond pas au relevé fourni.", nameof(snapshot));
        foreach (var index in snapshot.ScopeRows)
        {
            if ((scope.Count & 255) == 0) ct.ThrowIfCancellationRequested();
            if (index < 0 || index >= data.Rows.Count || !scope.Add(index))
                throw new ArgumentException("Le périmètre des plus anciens contient une ligne absente ou répétée.", nameof(snapshot));
        }

        int[] MetricRows(string key)
        {
            var metric = snapshot.Metric(key);
            if (metric?.Value is null) return Array.Empty<int>();
            var rows = metric.Rows.ToArray();
            var seen = new HashSet<int>();
            foreach (var index in rows)
            {
                if ((seen.Count & 255) == 0) ct.ThrowIfCancellationRequested();
                if (!scope.Contains(index) || !seen.Add(index))
                    throw new ArgumentException("Une population des plus anciens sort du périmètre ou répète une ligne source.", nameof(snapshot));
            }
            return rows;
        }
        var activeRows = MetricRows("active");
        var unknownStatusRows = MetricRows("unknown-status");
        var referenceDate = snapshot.CalculatedAt.Date;
        var grain = snapshot.Grain == "tickets" ? "tickets" : "occurrences";
        int? ColumnIndex(string role)
        {
            var column = snapshot.Column(role);
            return column?.Index is int index && index >= 0 && index < data.Headers.Count
                && string.Equals(column.Header, data.Headers[index], StringComparison.Ordinal) ? index : null;
        }
        var indexes = new[] { "id", "title", "status", "priority", "assignee", "group", "created" }
            .ToDictionary(role => role, ColumnIndex, StringComparer.Ordinal);
        var missing = new[] { "status", "group", "created" }.Where(role => !indexes[role].HasValue)
            .Select(role => snapshot.Column(role)?.Label ?? role).ToArray();
        if (missing.Length > 0 || snapshot.Metric("active")?.Value is null)
        {
            var reason = missing.Length > 0
                ? "Correspondances nécessaires : " + string.Join(", ", missing) + "."
                : "Aucun statut reconnu ne permet de déterminer le stock actif.";
            return new(false, reason, referenceDate, grain, topCount, activeRows, Array.Empty<int>(),
                Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), unknownStatusRows, null,
                Array.Empty<DashboardOldestGroup>(), Explanation);
        }

        string Cell(string[] row, string role) => indexes[role] is int index && index < row.Length
            ? DashboardService.NormalizeValue(row[index], role, data.Headers[index], settings) : "";
        var groups = new Dictionary<string, GroupBuilder>(StringComparer.OrdinalIgnoreCase);
        var records = new List<Occurrence>(activeRows.Length);
        var maximumAge = (int?)null;
        foreach (var index in activeRows)
        {
            if ((records.Count & 255) == 0) ct.ThrowIfCancellationRequested();
            var row = data.Rows[index];
            var rawDate = Cell(row, "created");
            DateTime? opened = null;
            int? age = null;
            OpeningState state;
            if (MissingDate(rawDate)) state = OpeningState.Missing;
            else if (!DashboardService.TrySourceDate(rawDate, settings, snapshot.CalculatedAt.Offset, out var parsed)) state = OpeningState.Invalid;
            else if (parsed.Date > referenceDate) state = OpeningState.Future;
            else
            {
                state = OpeningState.Dated; opened = parsed; age = (referenceDate - parsed.Date).Days;
                maximumAge = maximumAge.HasValue ? Math.Max(maximumAge.Value, age.Value) : age;
            }
            var id = Cell(row, "id");
            var record = new Occurrence(index, id, NumericId(id), Cell(row, "title"),
                DashboardService.StatusLabel(DashboardService.Status(Cell(row, "status"))), Cell(row, "priority"),
                Cell(row, "assignee"), opened, age, state);
            records.Add(record);
            var names = DashboardService.SplitTechnicianNames(Cell(row, "group"), settings.MultiValueSeparators)
                .Select(name => Regex.Replace(name.Normalize(NormalizationForm.FormC), @"\s+", " ").Trim())
                .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length == 0) Add("unassigned:", "Sans groupe", record);
            else foreach (var name in names) Add("group:" + name.ToUpperInvariant(), name, record);
        }

        void Add(string key, string label, Occurrence record)
        {
            if (!groups.TryGetValue(key, out var group))
            {
                // Stable, bounded bridge selectors even for very long group labels.
                var selector = key == "unassigned:" ? key : "group:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
                groups[key] = group = new(selector, label);
            }
            group.Members.Add(record);
        }
        static int[] Rows(IEnumerable<Occurrence> rows) => rows.Select(row => row.Index).ToArray();
        static int[] StateRows(IEnumerable<Occurrence> rows, OpeningState state) => Rows(rows.Where(row => row.State == state));
        IEnumerable<Occurrence> First(IEnumerable<Occurrence> rows) => rows.Where(row => row.OpenedAt.HasValue)
            .OrderBy(row => row.OpenedAt).ThenBy(row => !row.NumericId.HasValue).ThenBy(row => row.NumericId)
            .ThenBy(row => row.Index).Take(topCount);
        DashboardOldestTicket Ticket(Occurrence row) => new(row.Index, row.Id, row.Title, row.Status, row.Priority,
            row.Assignee, row.OpenedAt!.Value, row.AgeDays!.Value, RelativeAge(row.AgeDays.Value, maximumAge), AgeColor(row.AgeDays.Value, maximumAge));
        var projected = new List<DashboardOldestGroup>(groups.Count);
        foreach (var group in groups.Values)
        {
            ct.ThrowIfCancellationRequested();
            var first = First(group.Members).ToArray();
            var oldest = first.FirstOrDefault();
            var tickets = first.Select(Ticket).ToArray();
            projected.Add(new(group.Key, group.Label, Rows(group.Members), StateRows(group.Members, OpeningState.Dated),
                StateRows(group.Members, OpeningState.Missing), StateRows(group.Members, OpeningState.Invalid),
                StateRows(group.Members, OpeningState.Future), oldest?.OpenedAt, oldest?.AgeDays,
                oldest is null ? "" : AgeColor(oldest.AgeDays!.Value, maximumAge), tickets));
        }
        ct.ThrowIfCancellationRequested();
        var ordered = projected.OrderBy(group => !group.OldestOpenedAt.HasValue).ThenBy(group => group.OldestOpenedAt)
            .ThenBy(group => group.Label, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
        return new(true, "", referenceDate, grain, topCount, activeRows, StateRows(records, OpeningState.Dated),
            StateRows(records, OpeningState.Missing), StateRows(records, OpeningState.Invalid), StateRows(records, OpeningState.Future),
            unknownStatusRows, maximumAge, ordered, Explanation) { Oldest = First(records).Select(Ticket).ToArray() };
    }

    private static bool MissingDate(string text) => DashboardService.Normalize(text) is "" or "aucun" or "aucune" or "none"
        or "null" or "non renseigne" or "non renseignee" or "n a" or "n d" or "0";
    private static ulong? NumericId(string text)
    {
        // Match the dashboard's numeric-ID interpretation, including grouped digits;
        // keep the original displayed value and never use it to merge source rows.
        text = text.Trim();
        if (text.Length is 0 or > 64) return null;
        var groups = text.Split(new[] { ' ', '\u00a0', '\u202f' }, StringSplitOptions.RemoveEmptyEntries);
        if (groups.Length > 1 && (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(group => group.Length != 3))) return null;
        var digits = string.Concat(groups);
        return digits.Length > 0 && digits.All(c => c is >= '0' and <= '9')
            && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }
    private static double RelativeAge(int age, int? maximum) => maximum > 0 ? Math.Clamp((double)age / maximum.Value, 0, 1) : 0;
    private static string AgeColor(int age, int? maximum)
    {
        var weight = RelativeAge(age, maximum);
        var red = (int)Math.Round(235 + 17 * weight, MidpointRounding.ToEven);
        var green = (int)Math.Round(245 - 66 * weight, MidpointRounding.ToEven);
        var blue = (int)Math.Round(251 - 113 * weight, MidpointRounding.ToEven);
        return $"#{red:X2}{green:X2}{blue:X2}";
    }
}
