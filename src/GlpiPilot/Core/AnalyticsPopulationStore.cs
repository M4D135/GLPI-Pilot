using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class SitePopulationEntry
{
    public string SourceKey { get; set; } = "";
    public string SiteId { get; set; } = "";
    public string SiteLabel { get; set; } = "";
    public string Unit { get; set; } = "people";
    public string FromDate { get; set; } = "";
    public string UntilDate { get; set; } = "";
    public int Count { get; set; }
    public string Reference { get; set; } = "";
    public string UnitLabel => Unit == "people" ? "personnes" : "postes";
    public SitePopulationEntry Copy() => (SitePopulationEntry)MemberwiseClone();
}

/// <summary>Dated, source-bound denominators. No value is inferred from ticket counts.</summary>
public sealed class AnalyticsPopulationStore
{
    public const int MaximumEntries = 10000, MaximumBytes = 4 * 1024 * 1024;
    private readonly string _path;
    private sealed class Document { public int Version { get; set; } = 1; public List<SitePopulationEntry> Entries { get; set; } = new(); }
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 12, WriteIndented = true };
    public AnalyticsPopulationStore(string? path = null) => _path = Path.GetFullPath(path ?? Path.Combine(AppPaths.DataRoot, "site-populations.json"));
    public IReadOnlyList<SitePopulationEntry> Load() => File.Exists(_path) ? Read(_path) : Array.Empty<SitePopulationEntry>();
    public IReadOnlyList<SitePopulationEntry> LoadOrEmpty(out string warning)
    {
        try { warning = ""; return Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { warning = "Inventaire daté indisponible : " + AppPaths.SafeError(error); return Array.Empty<SitePopulationEntry>(); }
    }
    public static IReadOnlyList<SitePopulationEntry> Read(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Le référentiel d’effectifs dépasse 4 Mio.");
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Json);
        if (document is not { Version: 1 } || document.Entries is null) throw new InvalidDataException("Référentiel d’effectifs au format 1 attendu.");
        Validate(document.Entries); return document.Entries;
    }
    public void Save(IEnumerable<SitePopulationEntry> entries) => Write(_path, entries);
    public static void Write(string path, IEnumerable<SitePopulationEntry> entries)
    {
        var copy = entries.Take(MaximumEntries + 1).Select(e => e.Copy()).ToList(); Validate(copy);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document { Entries = copy }, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Le référentiel d’effectifs dépasse 4 Mio.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Validate(IReadOnlyList<SitePopulationEntry> entries)
    {
        if (entries.Count > MaximumEntries) throw new InvalidDataException("10 000 lignes d’effectifs au maximum.");
        foreach (var e in entries)
            if (e is null || string.IsNullOrWhiteSpace(e.SourceKey) || e.SourceKey.Length > 200 || !Guid.TryParse(e.SiteId, out _) || string.IsNullOrWhiteSpace(e.SiteLabel) || e.SiteLabel.Length > 300
                || e.Unit is not ("people" or "workstations") || e.Count is < 1 or > 1000000000 || e.Reference is null || e.Reference.Length > 1000
                || !DateOnly.TryParseExact(e.FromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
                || !DateOnly.TryParseExact(e.UntilDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until) || until < from)
                throw new InvalidDataException("Chaque effectif demande une source confirmée, un site, une unité, un nombre positif et deux dates ISO de validité dans l’ordre.");
        foreach (var group in entries.GroupBy(e => (e.SourceKey, e.SiteId, e.Unit)))
        {
            SitePopulationEntry? previous = null;
            foreach (var entry in group.OrderBy(e => e.FromDate, StringComparer.Ordinal))
            {
                if (previous is not null && string.CompareOrdinal(previous.UntilDate, entry.FromDate) >= 0)
                    throw new InvalidDataException("Périodes d’effectifs qui se chevauchent pour « " + entry.SiteLabel + " » (" + entry.UnitLabel + ").");
                previous = entry;
            }
        }
    }

    public static void Append(DashboardSnapshot snapshot, CityEnrichmentResult report, CitySettings geography, string sourceKey, IReadOnlyList<SitePopulationEntry> entries)
    {
        var date = snapshot.ExtractedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); var scope = snapshot.ScopeRows.ToHashSet();
        var matches = report.Matches.Where(m => scope.Contains(m.RowIndex) && m.State == CityMatchState.Matched && m.SiteId != null).ToArray();
        foreach (var unit in new[] { "people", "workstations" })
        {
            var valid = entries.Where(e => e.SourceKey == sourceKey && e.Unit == unit && string.CompareOrdinal(e.FromDate, date) <= 0 && string.CompareOrdinal(e.UntilDate, date) >= 0)
                .GroupBy(e => e.SiteId).Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
            var buckets = new List<DashboardBucket>(); var includedRows = new List<int>(); long population = 0;
            foreach (var site in geography.Sites)
            {
                var rows = matches.Where(m => m.SiteId == site.Id).Select(m => m.RowIndex).Distinct().Order().ToArray();
                var denominator = valid.FirstOrDefault(e => e.SiteId == site.Id);
                if (denominator == null)
                { if (rows.Length > 0) buckets.Add(new(site.Name + " · dénominateur absent ou périmé", rows.Length, rows, null, "pour 100 " + (unit == "people" ? "personnes" : "postes"), null, true)); continue; }
                // A filtered ticket view has no observed zero for an excluded site.
                if (snapshot.IsFiltered && rows.Length == 0) continue;
                population += denominator.Count; includedRows.AddRange(rows);
                buckets.Add(new(site.Name + $" · {denominator.Count:N0} {denominator.UnitLabel} · {denominator.FromDate} → {denominator.UntilDate}", rows.Length, rows,
                    100d * rows.Length / denominator.Count, "pour 100 " + denominator.UnitLabel, denominator.Count, true));
            }
            var unitLabel = unit == "people" ? "personnes" : "postes";
            var rowIds = includedRows.Distinct().Order().ToArray();
            snapshot.Distributions.Add(new("site-per100-" + unit, "Tickets pour 100 " + unitLabel + " par site", buckets.Any(b => b.Value.HasValue), snapshot.RowCount,
                "Numérateur : tickets du relevé attribués sans ambiguïté à ce site. Dénominateur : inventaire daté valide à l’extraction, pour le même contexte GLPI. Pas un taux d’incidence sur une période ; les sites sans référence valide restent indisponibles. Un filtre de tickets ne réduit pas l’effectif du site.", buckets));
            snapshot.Metrics.Add(new("site-per100-" + unit, "Tickets pour 100 " + unitLabel + " couverts", population > 0 ? 100d * rowIds.Length / population : null,
                "pour 100 " + unitLabel, $"{rowIds.Length:N0} tickets couverts / {snapshot.RowCount:N0} lignes ; dénominateur daté {population:N0} {unitLabel}. Seuls les sites avec référence valide sont inclus. " + (snapshot.IsFiltered ? "Périmètre filtré : seuls les sites observés dans ce périmètre sont retenus." : "Les sites référencés sans ticket contribuent un numérateur zéro dans ce relevé."), rowIds,
                population <= int.MaxValue ? (int)population : null));
        }
    }
}
