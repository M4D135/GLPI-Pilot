using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>Explicit filters over an existing inventory. They never broaden a GLPI search.</summary>
public sealed class LibraryCollectionScope
{
    public int Version { get; set; } = 1;
    // Old collection states keep their explicit global mode. The UI offers a targeted scope for a new selection.
    public bool Enabled { get; set; }
    public string DateRole { get; set; } = "created";
    public string FromDate { get; set; } = "";
    public string ToDate { get; set; } = "";
    public List<string> TicketIds { get; set; } = new();
    public string Text { get; set; } = "";
    public string SimilarSubject { get; set; } = "";
    public List<LibraryCollectionFieldFilter> Fields { get; set; } = new();
    public LibraryCollectionScope Copy() => TicketLibraryRules.Copy(this);
    public void Validate()
    {
        if (Version != 1 || DateRole is not ("created" or "updated" or "resolved" or "closed" or "due")
            || FromDate is null || ToDate is null || !ValidDate(FromDate) || !ValidDate(ToDate)
            || FromDate.Length > 0 && ToDate.Length > 0 && string.CompareOrdinal(FromDate, ToDate) > 0
            || TicketIds is null || TicketIds.Count > 10000 || TicketIds.Any(id => id is null || id.Length is 0 or > 20
                || !id.All(char.IsAsciiDigit) || !ulong.TryParse(id, CultureInfo.InvariantCulture, out var number) || number == 0)
            || Text is null || Text.Length > 2000 || SimilarSubject is null || SimilarSubject.Length > 2000
            || Fields is null || Fields.Count > 20 || Fields.Any(field => field is null || string.IsNullOrWhiteSpace(field.Column)
                || field.Column.Length > 10000 || field.Values is null || field.Values.Count is 0 or > 2048
                || field.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 2000))
            || Fields.Select(field => field.Column).Distinct(StringComparer.Ordinal).Count() != Fields.Count)
            throw new InvalidDataException("Le périmètre de collecte contient un filtre invalide, une date incorrecte ou trop de valeurs.");
    }
    private static bool ValidDate(string value) => value.Length == 0 || DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

public sealed class LibraryCollectionFieldFilter
{
    public string Column { get; set; } = "";
    public List<string> Values { get; set; } = new();
}

public sealed record LibraryCollectionDateRole(string Role, string Label, string Column, bool Available);
public sealed record LibraryCollectionFacetValue(string Value, int Count);
public sealed class LibraryCollectionFacet
{
    public string Column { get; set; } = "";
    public string Role { get; set; } = "";
    public string Label { get; set; } = "";
    public List<LibraryCollectionFacetValue> Values { get; set; } = new();
    public bool Truncated { get; set; }
    public bool ValuesLoaded { get; set; }
}

public sealed class LibraryCollectionScopeCatalog
{
    public string SourceFingerprint { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
    public bool InventoryComplete { get; set; }
    public int InventoryCount { get; set; }
    public bool TitleAvailable { get; set; }
    public List<LibraryCollectionDateRole> DateRoles { get; set; } = new();
    public List<LibraryCollectionFacet> Fields { get; set; } = new();
}

/// <summary>The immutable worklist chosen from one local inventory, before a fresh targeted GLPI check.</summary>
public sealed class LibraryCollectionPlan
{
    public int Version { get; set; } = 1;
    public LibraryCollectionScope Scope { get; set; } = new();
    public string Fingerprint { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public string Installation { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string SourceScopeFingerprint { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public int EvidenceCount { get; set; }
    public int NextOrdinal { get; set; }
    public bool InventoryComplete { get; set; }
    public int InventoryCount { get; set; }
    public int Selected { get; set; }
    public int DuplicateRows { get; set; }
    public int InvalidRows { get; set; }
    public int AmbiguousRows { get; set; }
    public int UnknownDates { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<TicketLibrarySeed> Tickets { get; set; } = new();

    internal LibraryCollectionPlan Header() => new()
    {
        Version = Version, Scope = Scope.Copy(), Fingerprint = Fingerprint, SourceFingerprint = SourceFingerprint,
        Installation = Installation, SourceUrl = SourceUrl, SourceScopeFingerprint = SourceScopeFingerprint,
        ObservedAt = ObservedAt, VerifiedAt = VerifiedAt, EvidenceCount = EvidenceCount, NextOrdinal = NextOrdinal,
        InventoryComplete = InventoryComplete, InventoryCount = InventoryCount,
        Selected = Selected, DuplicateRows = DuplicateRows, InvalidRows = InvalidRows, AmbiguousRows = AmbiguousRows,
        UnknownDates = UnknownDates, Warnings = Warnings.ToList()
    };
}

public sealed class LibraryCollectionCoverage
{
    public int Selected { get; set; }
    public int Complete { get; set; }
    public int Missing { get; set; }
    public int Partial { get; set; }
    public int Changed { get; set; }
    public int Unconfirmed { get; set; }
    public int ToRead => Selected - Complete;
    public double? Percent => Selected == 0 ? null : Math.Round(100d * Complete / Selected, 1);
    internal void Add(string state)
    {
        Selected++;
        switch (state)
        {
            case "complete": Complete++; break;
            case "missing": Missing++; break;
            case "partial": Partial++; break;
            case "changed": Changed++; break;
            case "unconfirmed": Unconfirmed++; break;
            default: throw new InvalidDataException("État de couverture de collecte inconnu.");
        }
    }
}

/// <summary>Pure local selection, bounded facets and cache decisions. No browser or network activity.</summary>
public static class LibraryCollectionScopePlanner
{
    private static readonly HashSet<string> DateRoles = new(["created", "updated", "resolved", "closed", "due"], StringComparer.Ordinal);
    private static readonly HashSet<string> ParticipantRoles = new(["assignee", "group", "requester", "updatedBy", "followupAuthor", "taskAuthor", "team"], StringComparer.Ordinal);
    private static readonly HashSet<string> InitialFacets = new(["assignee", "group", "requester", "status", "category", "entity", "team"], StringComparer.Ordinal);
    private static readonly HashSet<string> StopWords = new("avec alors dans depuis des les une pour par pas plus sur sous est sont aux du de la le un et en au ce ces se sa son ses ne ou que qui the and for with from this that not".Split(' '), StringComparer.Ordinal);

    public static LibraryCollectionScopeCatalog Catalog(ExtractionResult data, AppSettings settings,
        string column = "", string search = "", int limit = 200, CancellationToken ct = default)
    {
        ValidateInventory(data, settings); ct.ThrowIfCancellationRequested();
        if (column is null || search is null || column.Length > 10000 || search.Length > 2000 || limit is < 1 or > 500)
            throw new InvalidDataException("La recherche de valeurs de filtre est invalide.");
        var roles = DashboardService.ResolveColumns(data.Headers, settings.Dashboard);
        if (column.Length > 0 && data.Headers.Count(header => header == column) != 1)
            throw new InvalidDataException("Cette colonne de filtre est absente ou ambiguë dans le relevé.");
        var result = new LibraryCollectionScopeCatalog
        {
            SourceFingerprint = SourceFingerprint(data, settings), SourceUrl = data.SourceUrl, ObservedAt = data.ExtractedAt,
            InventoryCount = data.Rows.Count, InventoryComplete = Complete(data), TitleAvailable = roles.Any(role => role.Role == "title" && role.Index.HasValue),
            DateRoles = roles.Where(role => DateRoles.Contains(role.Role))
                .Select(role => new LibraryCollectionDateRole(role.Role, role.Label, role.Index.HasValue ? role.Header : "", role.Index.HasValue)).ToList()
        };
        var query = DashboardService.Normalize(search);
        var unique = data.Headers.GroupBy(header => header, StringComparer.Ordinal).Where(group => group.Count() == 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < data.Headers.Count; index++)
        {
            ct.ThrowIfCancellationRequested(); var header = data.Headers[index];
            if (header.Length == 0 || !unique.Contains(header)) continue;
            var resolved = roles.FirstOrDefault(role => role.Index == index);
            var roleKey = resolved?.Role ?? (DashboardService.Normalize(header) is "equipe" or "equipes" or "team" or "teams" ? "team" : "");
            if (roleKey is "id" or "title" or "description" or "solution" || DateRoles.Contains(roleKey)) continue;
            var facet = new LibraryCollectionFacet { Column = header, Role = roleKey, Label = resolved?.Label ?? header };
            result.Fields.Add(facet);
            if (column.Length > 0 ? column != header : !InitialFacets.Contains(roleKey)) continue;
            facet.ValuesLoaded = true;
            var counts = new Dictionary<string, (string Label, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in data.Rows)
            {
                ct.ThrowIfCancellationRequested();
                if (row is null || index >= row.Length) continue;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in Values(row[index], roleKey, settings.Dashboard))
                {
                    var normalized = DashboardService.Normalize(value);
                    if (normalized.Length == 0 || !seen.Add(value) || query.Length > 0 && !normalized.Contains(query, StringComparison.Ordinal)) continue;
                    if (counts.TryGetValue(value, out var count)) counts[value] = (count.Label, count.Count + 1);
                    else counts[value] = (value, 1);
                }
            }
            facet.Truncated = counts.Count > limit;
            facet.Values = counts.Values.OrderByDescending(value => value.Count).ThenBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
                .Take(limit).Select(value => new LibraryCollectionFacetValue(value.Label, value.Count)).ToList();
        }
        return result;
    }

    public static LibraryCollectionPlan Build(ExtractionResult data, AppSettings settings, LibraryCollectionScope scope,
        LibraryPreferences preferences, CancellationToken ct = default)
    {
        ValidateInventory(data, settings); ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(preferences); ct.ThrowIfCancellationRequested();
        scope = scope.Copy(); scope.Validate();
        if (!scope.Enabled) throw new InvalidDataException("Activez un périmètre ciblé avant de préparer sa collecte.");
        var roles = DashboardService.ResolveColumns(data.Headers, settings.Dashboard).ToDictionary(role => role.Role, StringComparer.Ordinal);
        var dateIndex = scope.FromDate.Length == 0 && scope.ToDate.Length == 0 ? null : roles[scope.DateRole].Index;
        if ((scope.FromDate.Length > 0 || scope.ToDate.Length > 0) && dateIndex is null)
            throw new InvalidDataException("La colonne de date choisie est absente ou ambiguë. Vérifiez les correspondances du relevé.");
        var titleIndex = roles["title"].Index;
        if ((scope.Text.Trim().Length > 0 || scope.SimilarSubject.Trim().Length > 0) && titleIndex is null)
            throw new InvalidDataException("Le filtre de sujet nécessite une colonne Titre identifiée sans ambiguïté.");
        var fields = scope.Fields.Select(field =>
        {
            var matches = data.Headers.Select((header, index) => (header, index)).Where(pair => pair.header == field.Column).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Une colonne filtrée est absente ou ambiguë. Aucun périmètre élargi n’a été préparé.");
            var role = roles.Values.FirstOrDefault(value => value.Index == matches[0].index)?.Role
                ?? (DashboardService.Normalize(field.Column) is "equipe" or "equipes" or "team" or "teams" ? "team" : "");
            return (Index: matches[0].index, Role: role, Values: field.Values.Select(value => value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }).ToArray();
        var ids = scope.TicketIds.Select(id => ulong.Parse(id, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        var textTerms = Terms(scope.Text, false); var similarTerms = Terms(scope.SimilarSubject, true);
        if (scope.SimilarSubject.Trim().Length > 0 && similarTerms.Length == 0)
            throw new InvalidDataException("Le sujet similaire doit contenir au moins un mot significatif.");
        // The inventory may have hundreds of columns. A durable worklist needs only selection evidence;
        // the subsequent targeted GLPI index supplies the actual acquisition metadata.
        var auditColumns = fields.Select(field => field.Index).Concat(dateIndex is { } auditDate ? new[] { auditDate } : Array.Empty<int>())
            .Distinct().Order().ToArray();
        var auditHeaders = auditColumns.Select(index => data.Headers[index]).ToList();
        var plan = new LibraryCollectionPlan
        {
            Scope = scope, SourceFingerprint = SourceFingerprint(data, settings), SourceUrl = data.SourceUrl,
            Installation = ObjectIdentity.Installation(data.SourceUrl), SourceScopeFingerprint = data.Provenance.ScopeFingerprint,
            ObservedAt = data.ExtractedAt, InventoryComplete = Complete(data), InventoryCount = data.Rows.Count
        };
        // Resolve identity once per row. Ambiguous duplicate rows are excluded before a field filter can hide the ambiguity.
        var firstRows = new Dictionary<string, int>(StringComparer.Ordinal);
        var references = new Dictionary<string, TicketReference>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        for (var rowIndex = 0; rowIndex < data.Rows.Count; rowIndex++)
        {
            ct.ThrowIfCancellationRequested(); var row = data.Rows[rowIndex]; TicketReference reference;
            try { reference = TicketReferenceResolver.Resolve(data, row, settings); _ = TicketLibraryRules.Key(reference); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException or UriFormatException)
            { plan.InvalidRows++; continue; }
            if (firstRows.TryGetValue(reference.Id, out var previous))
            {
                plan.DuplicateRows++;
                if (!data.Rows[previous].SequenceEqual(row, StringComparer.Ordinal)) ambiguous.Add(reference.Id);
            }
            else { firstRows[reference.Id] = rowIndex; references[reference.Id] = reference; }
        }
        plan.AmbiguousRows = ambiguous.Count;
        var dateSettings = settings.Dashboard.Copy();
        foreach (var pair in firstRows)
        {
            ct.ThrowIfCancellationRequested();
            if (ambiguous.Contains(pair.Key) || ids.Count > 0 && !ids.Contains(pair.Key)) continue;
            var row = data.Rows[pair.Value];
            if (fields.Any(field => !Values(row[field.Index], field.Role, settings.Dashboard)
                .Any(value => field.Values.Contains(value)))) continue;
            var title = titleIndex is { } titleColumn ? row[titleColumn] : "";
            if (textTerms.Length > 0)
            {
                var normalized = DashboardService.Normalize(title);
                if (textTerms.Any(term => !normalized.Contains(term, StringComparison.Ordinal))) continue;
            }
            if (similarTerms.Length > 0)
            {
                var titleWords = Terms(title, true).ToHashSet(StringComparer.Ordinal);
                var required = Math.Max(Math.Min(2, similarTerms.Length), (int)Math.Ceiling(similarTerms.Length * .6));
                if (similarTerms.Count(titleWords.Contains) < required) continue;
            }
            if (dateIndex is { } dateColumn)
            {
                if (!TryFilterDate(row[dateColumn], dateSettings, data.ExtractedAt.Offset, out var date))
                { plan.UnknownDates++; continue; }
                var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (scope.FromDate.Length > 0 && string.CompareOrdinal(day, scope.FromDate) < 0
                    || scope.ToDate.Length > 0 && string.CompareOrdinal(day, scope.ToDate) > 0) continue;
            }
            var modified = roles["updated"].Index is { } modifiedColumn ? row[modifiedColumn] : "";
            var modification = BrowserService.ReadLibraryModification(modified, roles["updated"].Index.HasValue, preferences.SourceDateOrder);
            var modifiedAt = modification.Reliable ? LibraryPreferencesStore.ParseModification(modified, preferences.SourceTimeZone, preferences.SourceDateOrder) : null;
            var status = roles["status"].Index is { } statusColumn ? row[statusColumn] : "";
            var seed = new TicketLibrarySeed
            {
                Ticket = references[pair.Key], Title = title, Status = status, StatusCode = StatusCode(status),
                Category = roles["category"].Index is { } categoryColumn ? row[categoryColumn] : "",
                LastModifiedRaw = modified, LastModifiedAt = modifiedAt, LastModifiedReliable = modification.Reliable && modifiedAt.HasValue,
                LastModifiedPrecision = modification.Precision, ObservedAt = data.ExtractedAt,
                IndexHeaders = auditHeaders, IndexValues = auditColumns.Select(column => row[column]).ToList()
            };
            plan.Tickets.Add(TicketLibraryRules.ValidateSeed(seed));
        }
        plan.Tickets = plan.Tickets.OrderBy(seed => TicketLibraryRules.Key(seed.Ticket), StringComparer.Ordinal).ToList();
        plan.Selected = plan.Tickets.Count;
        if (!plan.InventoryComplete) plan.Warnings.Add("Le relevé de départ est partiel : la sélection et le taux de couverture concernent uniquement les tickets connus dans ce relevé.");
        if (plan.InvalidRows > 0) plan.Warnings.Add("Certaines lignes sans identité GLPI vérifiable ont été exclues.");
        if (plan.AmbiguousRows > 0) plan.Warnings.Add("Des identifiants figurent sur plusieurs lignes contradictoires ; ces tickets ont été exclus.");
        if (plan.UnknownDates > 0) plan.Warnings.Add("Des tickets sans date interprétable ont été exclus du filtre de dates.");
        plan.Fingerprint = Fingerprint(plan);
        return plan;
    }

    /// <summary>A fresh targeted index proves the version; elapsed cache age alone never forces another body read.</summary>
    public static bool CanReuse(TicketLibrarySeed seed, TicketLibraryDocument? stored, string readerSignature)
    {
        if (stored is null || string.IsNullOrWhiteSpace(readerSignature) || !stored.DetailComplete || !TicketLibraryRules.IsContentConfirmed(stored) || stored.Conflicts > 0
            || !TicketLibraryAcquisition.Continues(stored.ReaderSignature, readerSignature)
            || !seed.LastModifiedReliable || !stored.Seed.LastModifiedReliable || seed.LastModifiedAt is not { } modified
            || stored.ReadAt is not { } read || seed.LastModifiedRaw.Length == 0 || stored.DetailModifiedRaw != seed.LastModifiedRaw
            || stored.Seed.LastModifiedRaw != seed.LastModifiedRaw || stored.DetailModifiedAt != modified
            || stored.Seed.LastModifiedAt != modified || stored.Seed.LastModifiedPrecision != seed.LastModifiedPrecision
            || TicketLibraryRules.Key(seed.Ticket) != stored.Key) return false;
        var interval = TicketLibraryRules.Interval(seed);
        return interval > TimeSpan.Zero && read <= DateTimeOffset.UtcNow && read >= modified + interval + new TicketLibraryCachePolicy().SafetyMargin;
    }

    public static string Classify(TicketLibrarySeed seed, TicketLibraryDocument? stored, string readerSignature)
    {
        if (stored?.Detail is null) return "missing";
        if (!stored.DetailComplete) return "partial";
        if (CanReuse(seed, stored, readerSignature)) return "complete";
        if (seed.LastModifiedReliable && stored.Seed.LastModifiedReliable && stored.DetailModifiedAt.HasValue && seed.LastModifiedAt.HasValue
            && (stored.DetailModifiedRaw != seed.LastModifiedRaw || stored.DetailModifiedAt != seed.LastModifiedAt
                || stored.Seed.LastModifiedPrecision != seed.LastModifiedPrecision)) return "changed";
        return "unconfirmed";
    }

    public static LibraryCollectionPlan Restrict(LibraryCollectionPlan plan, IReadOnlySet<string> ticketKeys)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(ticketKeys);
        var members = plan.Tickets.Select(seed => TicketLibraryRules.Key(seed.Ticket)).ToHashSet(StringComparer.Ordinal);
        if (ticketKeys.Any(key => !members.Contains(key)))
            throw new InvalidDataException("Une reprise ciblée ne peut pas ajouter un ticket absent du périmètre initial.");
        var restricted = plan.Header(); restricted.VerifiedAt = null; restricted.EvidenceCount = 0; restricted.NextOrdinal = 0;
        restricted.Tickets = plan.Tickets.Where(seed => ticketKeys.Contains(TicketLibraryRules.Key(seed.Ticket)))
            .Select(TicketLibraryRules.Copy).ToList();
        restricted.Selected = restricted.Tickets.Count;
        restricted.Warnings.Add($"Reprise limitée à {restricted.Selected} tickets du périmètre initial de {plan.Selected} tickets.");
        restricted.Fingerprint = Fingerprint(restricted); return restricted;
    }

    internal static string Fingerprint(LibraryCollectionPlan plan)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value) { var bytes = Encoding.UTF8.GetBytes(value); hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }
        Append(JsonSerializer.Serialize(new
        {
            plan.Version, plan.SourceFingerprint, plan.Installation, plan.SourceUrl, plan.SourceScopeFingerprint,
            plan.ObservedAt, plan.InventoryComplete, plan.InventoryCount, plan.Selected,
            plan.DuplicateRows, plan.InvalidRows, plan.AmbiguousRows, plan.UnknownDates,
            scope = new
            {
                plan.Scope.Version, plan.Scope.Enabled, plan.Scope.DateRole, plan.Scope.FromDate, plan.Scope.ToDate,
                ticketIds = plan.Scope.TicketIds.Order(StringComparer.Ordinal), text = DashboardService.Normalize(plan.Scope.Text),
                similarSubject = DashboardService.Normalize(plan.Scope.SimilarSubject),
                fields = plan.Scope.Fields.OrderBy(field => field.Column, StringComparer.Ordinal)
                    .Select(field => new { field.Column, values = field.Values.Select(value => value.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal) })
            }
        }, TicketLibraryRules.Json));
        foreach (var seed in plan.Tickets)
        {
            Append(TicketLibraryRules.Key(seed.Ticket));
            // Do not create, serialize and deserialize a whole library document merely to hash small plan metadata.
            Append(JsonSerializer.Serialize(new
            {
                seed.Title, seed.Status, seed.StatusCode, seed.Category, seed.LastModifiedRaw, seed.LastModifiedAt,
                seed.LastModifiedReliable, seed.LastModifiedPrecision, seed.ObservedAt,
                fields = seed.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal), seed.IndexHeaders, seed.IndexValues
            }, TicketLibraryRules.Json));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string SourceFingerprint(ExtractionResult data, AppSettings settings) => TicketReadCache.Fingerprint(new
    {
        data.SourceUrl, data.ExtractedAt, data.Provenance.RunId, data.Provenance.ScopeFingerprint, data.Provenance.SourceKind,
        data.Complete, data.ExpectedTotal, count = data.Rows.Count, data.Headers,
        settings.TicketDetails, settings.Dashboard.ColumnMappings, settings.Dashboard.DateCulture,
        settings.Dashboard.SourceTimeZone, settings.Dashboard.MultiValueSeparators
    });

    private static void ValidateInventory(ExtractionResult data, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(settings);
        if (data.Headers is null || data.Headers.Count is 0 or > 500 || data.Headers.Any(header => header is null || header.Length > 10000)
            || data.Rows is null || data.Rows.Count > TicketLibraryStore.CaptureTicketLimit || data.Provenance is null
            || string.IsNullOrWhiteSpace(data.SourceUrl)) throw new InvalidDataException("Le relevé de départ ne permet pas de préparer une collecte ciblée.");
        var source = TicketReferenceResolver.ReadHttpUrl(data.SourceUrl, "La source du relevé");
        var portal = TicketReferenceResolver.ReadHttpUrl(settings.PortalUrl, "La source GLPI configurée");
        if (!TicketReferenceResolver.SameOrigin(source, portal) || source.AbsolutePath != portal.AbsolutePath)
            throw new InvalidDataException("Le relevé et la collecte doivent appartenir à la même source GLPI.");
    }

    private static bool Complete(ExtractionResult data) => data.Complete && (!data.ExpectedTotal.HasValue || data.ExpectedTotal.Value == data.Rows.Count);
    private static bool TryFilterDate(string raw, DashboardSettings settings, TimeSpan offset, out DateTime date)
    {
        if (!DashboardService.TrySourceDate(raw, settings, offset, out date)) return false;
        if (settings.SourceTimeZone.Length == 0) return true;
        try
        {
            // Date boundaries are source-calendar dates, including across DST, not dates in the workstation's offset.
            var instant = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), offset);
            date = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(settings.SourceTimeZone)).DateTime;
            return true;
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return false; }
    }
    private static IEnumerable<string> Values(string? value, string role, DashboardSettings settings) => ParticipantRoles.Contains(role)
        ? DashboardService.SplitTechnicianNames(value, settings.MultiValueSeparators)
        : string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value.Trim() };
    private static string[] Terms(string text, bool meaningful) => DashboardService.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(term => !meaningful || term.Length > 2 && !StopWords.Contains(term)).Distinct(StringComparer.Ordinal).ToArray();
    private static int? StatusCode(string value) => DashboardService.Normalize(value) switch
    {
        "1" or "nouveau" or "new" => 1, "2" or "en cours attribue" or "processing assigned" => 2,
        "3" or "en cours planifie" or "processing planned" => 3, "4" or "en attente" or "pending" => 4,
        "5" or "resolu" or "solved" or "resolved" => 5, "6" or "clos" or "ferme" or "closed" => 6, _ => null
    };
}
