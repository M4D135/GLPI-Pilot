using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class SnapshotMetadata
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public string SourceLabel { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string RequestedUrl { get; set; } = "";
    public string RulesFingerprint { get; set; } = "";
    public string SchemaFingerprint { get; set; } = "";
    public string Method { get; set; } = "";
    public string ApplicationVersion { get; set; } = "";
    public int Rows { get; set; }
    public int Columns { get; set; }
    public int? ExpectedTotal { get; set; }
    public bool Complete { get; set; }
    public bool SearchVerified { get; set; }
    public bool ScopeVerified { get; set; }
    public bool IdentityReliable { get; set; }
    public int IdColumn { get; set; } = -1;
    public int StatusColumn { get; set; } = -1;
    public int AssigneeColumn { get; set; } = -1;
    public long CompressedBytes { get; set; }
    public string PayloadSha256 { get; set; } = "";
    public bool Protected { get; set; }
    public Dictionary<string, double?> Metrics { get; set; } = new();
    public string Label => $"{CapturedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss} · {Rows:N0} tickets · {SourceLabel}";
    public string State => Complete && SearchVerified && ScopeVerified && IdentityReliable ? "Comparable" : "À vérifier";
}

public sealed class SnapshotDocument
{
    public SnapshotMetadata Metadata { get; set; } = new();
    public ExtractionResult Data { get; set; } = new();
    public DashboardSettings Dashboard { get; set; } = new();
    public CitySettings Cities { get; set; } = new();
}

public sealed record SnapshotChange(string Kind, string TicketId, string Field, string Before,
    string After, int? PreviousRow, int? CurrentRow);

public sealed class SnapshotComparison
{
    public bool Compatible { get; set; }
    public List<string> Warnings { get; } = new();
    public List<string> AddedColumns { get; } = new();
    public List<string> RemovedColumns { get; } = new();
    public List<SnapshotChange> Changes { get; } = new();
    public int Added { get; set; }
    public int Absent { get; set; }
    public int Changed { get; set; }
    public int Unchanged { get; set; }
    public int ResolutionsObserved { get; set; }
    public int ReopeningsObserved { get; set; }
    public int ReassignmentsObserved { get; set; }
    public bool Truncated { get; set; }
    public string Summary => Compatible
        ? $"{Added:N0} apparus · {Absent:N0} absents du relevé · {Changed:N0} modifiés · {Unchanged:N0} inchangés"
        : "Comparaison métier indisponible : les relevés ne sont pas comparables.";
}

/// <summary>Immutable compressed receipts with an independently readable, bounded metadata catalogue.</summary>
public sealed class SnapshotStore
{
    public const int MaximumUncompressedBytes = 512 * 1024 * 1024;
    private const int MaximumChanges = 100000;
    private readonly string _root;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 32, WriteIndented = false };
    public SnapshotStore(string? root = null) => _root = Path.GetFullPath(root ?? Path.Combine(AppPaths.DataRoot, "History"));

    public IReadOnlyList<SnapshotMetadata> List(int limit = 5000)
    {
        if (!Directory.Exists(_root)) return Array.Empty<SnapshotMetadata>();
        var entries = new List<SnapshotMetadata>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.meta.json").Take(10000))
        {
            try
            {
                if (new FileInfo(path).Length > 512 * 1024) continue;
                var metadata = JsonSerializer.Deserialize<SnapshotMetadata>(File.ReadAllText(path), Json);
                if (metadata is not { Version: 1 } || !ValidId(metadata.Id) || !ValidMetadata(metadata)
                    || !string.Equals(Path.GetFileName(path), metadata.Id + ".meta.json", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(PayloadPath(metadata.Id))) continue;
                entries.Add(metadata);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return entries.OrderByDescending(item => item.CapturedAt).Take(Math.Clamp(limit, 1, 5000)).ToArray();
    }

    public SnapshotMetadata Save(ExtractionResult data, AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();
        if (data.Headers.Count > 16384 || data.Rows.Count > 1000000 || data.Rows.Any(row => row == null || row.Length > data.Headers.Count))
            throw new InvalidDataException("Le relevé dépasse les limites de l’historique ou possède une structure invalide.");
        var provenance = data.Provenance ?? new();
        lock (Gate)
        {
            var existing = List().FirstOrDefault(item => item.RunId.Length > 0 && item.RunId == provenance.RunId);
            if (existing != null) return existing;
            Directory.CreateDirectory(_root);
            var dashboard = DashboardService.Calculate(data, settings.Dashboard, data.ExtractedAt, ct);
            int Column(string role) => dashboard.Columns.FirstOrDefault(column => column.Role == role)?.Index ?? -1;
            var metadata = new SnapshotMetadata
            {
                RunId = provenance.RunId, CapturedAt = data.ExtractedAt, SourceLabel = AppPaths.SafeUrl(data.SourceUrl),
                RequestedUrl = provenance.SearchVerified ? TargetUrlPolicy.PersistedTarget(provenance.RequestedUrl) : "",
                SourceKey = SourceKey(data),
                RulesFingerprint = RulesFingerprint(settings), SchemaFingerprint = SchemaFingerprint(data.Headers),
                Method = provenance.Method, ApplicationVersion = AppPaths.ApplicationVersion,
                Rows = data.Rows.Count, Columns = data.Headers.Count, ExpectedTotal = data.ExpectedTotal,
                Complete = data.Complete && (!data.ExpectedTotal.HasValue || data.ExpectedTotal == data.Rows.Count), SearchVerified = provenance.SearchVerified, ScopeVerified = provenance.ScopeVerified,
                IdColumn = Column("id"), StatusColumn = Column("status"), AssigneeColumn = Column("assignee"),
                Metrics = dashboard.Metrics.ToDictionary(metric => metric.Key, metric => metric.Value, StringComparer.Ordinal)
            };
            if (metadata.SourceLabel.Length == 0) metadata.SourceLabel = "Import local · provenance non confirmée";
            metadata.IdentityReliable = TryIdentityIndex(data, metadata.IdColumn, out _);
            var document = new SnapshotDocument { Metadata = metadata, Data = data, Dashboard = settings.Dashboard.Copy(), Cities = settings.Cities.Copy() };
            var temporary = Path.Combine(_root, "." + metadata.Id + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                using (var bounded = new BoundedStream(gzip, MaximumUncompressedBytes, reading: false))
                    JsonSerializer.SerializeAsync(bounded, document, Json, ct).GetAwaiter().GetResult();
                ct.ThrowIfCancellationRequested();
                using (var input = File.OpenRead(temporary)) metadata.PayloadSha256 = Convert.ToHexString(SHA256.HashData(input));
                metadata.CompressedBytes = new FileInfo(temporary).Length;
                File.Move(temporary, PayloadPath(metadata.Id));
                WriteMetadata(metadata);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Prune(settings.History);
            return metadata;
        }
    }

    public SnapshotDocument Load(string id, CancellationToken ct = default)
    {
        RequireId(id); ct.ThrowIfCancellationRequested();
        var metadata = List().FirstOrDefault(item => item.Id == id) ?? throw new FileNotFoundException("Le relevé historique n’est plus disponible.");
        var path = PayloadPath(id);
        using var file = File.OpenRead(path);
        if (file.Length > MaximumUncompressedBytes) throw new InvalidDataException("Le relevé historique dépasse la taille autorisée.");
        var digest = Convert.ToHexString(SHA256.HashData(file));
        if (!string.Equals(digest, metadata.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("L’empreinte du relevé historique ne correspond plus au fichier enregistré.");
        file.Position = 0;
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var bounded = new BoundedStream(gzip, MaximumUncompressedBytes, reading: true);
        var document = JsonSerializer.DeserializeAsync<SnapshotDocument>(bounded, Json, ct).GetAwaiter().GetResult()
            ?? throw new InvalidDataException("Le relevé historique est vide.");
        var stored = document.Metadata;
        if (stored.Id != id || document.Data.Rows.Count != metadata.Rows || document.Data.Headers.Count != metadata.Columns
            || stored.SourceKey != metadata.SourceKey || stored.RulesFingerprint != metadata.RulesFingerprint || stored.SchemaFingerprint != metadata.SchemaFingerprint
            || stored.Complete != metadata.Complete || stored.SearchVerified != metadata.SearchVerified || stored.ScopeVerified != metadata.ScopeVerified
            || stored.IdentityReliable != metadata.IdentityReliable || stored.IdColumn != metadata.IdColumn || stored.StatusColumn != metadata.StatusColumn
            || stored.AssigneeColumn != metadata.AssigneeColumn || stored.CapturedAt != metadata.CapturedAt || stored.RunId != metadata.RunId
            || SchemaFingerprint(document.Data.Headers) != metadata.SchemaFingerprint)
            throw new InvalidDataException("Le contenu du relevé historique ne correspond pas à son catalogue.");
        document.Metadata = metadata;
        return document;
    }

    public void Protect(string id, bool protect)
    {
        RequireId(id);
        lock (Gate)
        {
            var metadata = List().FirstOrDefault(item => item.Id == id) ?? throw new FileNotFoundException("Relevé introuvable.");
            metadata.Protected = protect; WriteMetadata(metadata);
        }
    }

    public void Prune(HistorySettings settings)
    {
        settings = settings.Copy(); settings.Normalize();
        lock (Gate)
        {
            var entries = List();
            long bytes = 0; int retained = 0;
            var keepLatestPerSource = entries.GroupBy(item => item.SourceKey).Select(group => group.First().Id).ToHashSet(StringComparer.Ordinal);
            // A failed or partial refresh must not evict the last usable receipt for that source.
            keepLatestPerSource.UnionWith(entries.Where(item => item.Complete && item.SearchVerified && item.ScopeVerified && item.IdentityReliable)
                .GroupBy(item => item.SourceKey).Select(group => group.First().Id));
            foreach (var entry in entries)
            {
                var keep = entry.Protected || keepLatestPerSource.Contains(entry.Id)
                    || (entry.CapturedAt >= DateTimeOffset.Now.AddDays(-settings.RetentionDays)
                        && retained < settings.MaximumSnapshots && bytes + entry.CompressedBytes <= settings.StorageBudgetMb * 1024L * 1024L);
                if (keep) { bytes += entry.CompressedBytes; retained++; continue; }
                try { File.Delete(MetadataPath(entry.Id)); File.Delete(PayloadPath(entry.Id)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public static SnapshotComparison Compare(SnapshotDocument before, SnapshotDocument after, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        var result = new SnapshotComparison();
        result.AddedColumns.AddRange(after.Data.Headers.Except(before.Data.Headers, StringComparer.Ordinal));
        result.RemovedColumns.AddRange(before.Data.Headers.Except(after.Data.Headers, StringComparer.Ordinal));
        if (before.Metadata.SourceKey.Length == 0 || before.Metadata.SourceKey != after.Metadata.SourceKey)
            result.Warnings.Add("Les sources ou leurs critères diffèrent, ou leur provenance n’est pas confirmée.");
        if (!before.Metadata.Complete || !after.Metadata.Complete)
            result.Warnings.Add("Les deux relevés doivent avoir une extraction complète confirmée.");
        if (!before.Metadata.SearchVerified || !after.Metadata.SearchVerified || !before.Metadata.ScopeVerified || !after.Metadata.ScopeVerified)
            result.Warnings.Add("Les critères de recherche n’ont pas été observés : mémorisez une URL contenant les filtres ou affichez les contrôles de recherche GLPI.");
        if (before.Metadata.RulesFingerprint != after.Metadata.RulesFingerprint)
            result.Warnings.Add("Les règles de correspondance, de date ou de localisation ont changé.");
        if (before.Metadata.SchemaFingerprint != after.Metadata.SchemaFingerprint)
            result.Warnings.Add("La structure des colonnes a changé. Les différences de schéma sont affichées séparément.");
        if (before.Metadata.CapturedAt >= after.Metadata.CapturedAt)
            result.Warnings.Add("Choisissez deux relevés différents dans l’ordre chronologique.");
        var validBefore = TryIdentityIndex(before.Data, before.Metadata.IdColumn, out var oldRows);
        var validAfter = TryIdentityIndex(after.Data, after.Metadata.IdColumn, out var newRows);
        if (!validBefore || !validAfter) result.Warnings.Add("Les identifiants doivent être présents, reconnus et uniques dans chaque relevé.");
        result.Compatible = result.Warnings.Count == 0;
        if (!result.Compatible) return result;
        void Add(SnapshotChange change)
        {
            if (result.Changes.Count < MaximumChanges) result.Changes.Add(change);
            else result.Truncated = true;
        }
        var iteration = 0;
        foreach (var (id, current) in newRows)
        {
            if ((iteration++ & 255) == 0) ct.ThrowIfCancellationRequested();
            if (!oldRows.TryGetValue(id, out var previous))
            { result.Added++; Add(new("Apparu", id, "Présence", "Absent du relevé précédent", "Présent", null, current)); continue; }
            var changed = false;
            for (var column = 0; column < after.Data.Headers.Count; column++)
            {
                var oldValue = Cell(before.Data, previous, column); var newValue = Cell(after.Data, current, column);
                if (oldValue == newValue) continue;
                changed = true; Add(new("Modifié", id, after.Data.Headers[column], oldValue, newValue, previous, current));
            }
            if (changed) result.Changed++; else result.Unchanged++;
            if (before.Metadata.StatusColumn >= 0 && after.Metadata.StatusColumn >= 0)
            {
                var oldStatus = DashboardService.Status(Cell(before.Data, previous, before.Metadata.StatusColumn));
                var newStatus = DashboardService.Status(Cell(after.Data, current, after.Metadata.StatusColumn));
                if (oldStatus is >= 1 and <= 4 && newStatus is 5 or 6) result.ResolutionsObserved++;
                if (oldStatus is 5 or 6 && newStatus is >= 1 and <= 4) result.ReopeningsObserved++;
            }
            if (before.Metadata.AssigneeColumn >= 0 && after.Metadata.AssigneeColumn >= 0
                && Cell(before.Data, previous, before.Metadata.AssigneeColumn) != Cell(after.Data, current, after.Metadata.AssigneeColumn))
                result.ReassignmentsObserved++;
        }
        foreach (var (id, previous) in oldRows)
            if (!newRows.ContainsKey(id))
            { result.Absent++; Add(new("Absent du relevé", id, "Présence", "Présent", "Statut inconnu hors du relevé", previous, null)); }
        if (result.Truncated) result.Warnings.Add($"L’affichage des différences est limité à {MaximumChanges:N0} cellules ; les compteurs portent sur toutes les lignes.");
        result.Warnings.Add("Une absence ne prouve pas une clôture. Les transitions sont constatées entre les deux relevés, leur heure exacte est inconnue.");
        return result;
    }

    public static string SourceKey(string requestedUrl)
    {
        if (string.IsNullOrWhiteSpace(requestedUrl)) return "";
        var uri = new Uri(TargetUrlPolicy.PersistedTarget(requestedUrl));
        var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !Uri.UnescapeDataString(part.Split('=', 2)[0]).Equals("start", StringComparison.OrdinalIgnoreCase))
            .OrderBy(part => part, StringComparer.Ordinal);
        return Hash(uri.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', parameters));
    }

    public static string SourceKey(ExtractionResult data)
    {
        if (data.Provenance is not { SearchVerified: true } provenance) return "";
        var key = SourceKey(provenance.RequestedUrl);
        return key.Length == 0 ? "" : Hash(key + "|" + provenance.ScopeFingerprint);
    }
    public static string SchemaFingerprint(IReadOnlyList<string> headers) => Hash(JsonSerializer.Serialize(headers));

    public static string RulesFingerprint(AppSettings settings) => Hash(JsonSerializer.Serialize(new
    {
        Version = 2, settings.Dashboard.DateCulture, settings.Dashboard.StaleDays, settings.Dashboard.OldDays, settings.Dashboard.DueSoonHours,
        settings.Dashboard.Weather, settings.Dashboard.AssignmentCountingMode,
        PriorityRules = settings.Dashboard.PriorityRules.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        Mappings = settings.Dashboard.ColumnMappings.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        settings.Cities
    }, Json));

    private static bool TryIdentityIndex(ExtractionResult data, int column, out Dictionary<string, int> rows)
    {
        rows = new(StringComparer.Ordinal);
        if (column < 0 || column >= data.Headers.Count) return false;
        for (int index = 0; index < data.Rows.Count; index++)
        {
            try
            {
                var id = TicketReferenceResolver.NormalizeId(Cell(data, index, column));
                if (!rows.TryAdd(id, index)) return false;
            }
            catch (InvalidOperationException) { return false; }
        }
        return true;
    }
    private static string Cell(ExtractionResult data, int row, int column) => column >= 0 && row >= 0 && row < data.Rows.Count && column < data.Rows[row].Length ? data.Rows[row][column] ?? "" : "";
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool ValidId(string id) => Guid.TryParseExact(id, "N", out var guid) && guid != Guid.Empty;
    private static bool ValidMetadata(SnapshotMetadata metadata) =>
        metadata.RunId != null && metadata.SourceLabel != null && metadata.SourceKey != null && metadata.RequestedUrl != null
        && metadata.RulesFingerprint != null && metadata.SchemaFingerprint != null && metadata.Method != null && metadata.ApplicationVersion != null
        && metadata.Rows is >= 0 and <= 1000000 && metadata.Columns is >= 0 and <= 16384
        && metadata.ExpectedTotal is null or >= 0 && metadata.CompressedBytes is > 0 and <= MaximumUncompressedBytes
        && metadata.PayloadSha256 is { Length: 64 } && metadata.PayloadSha256.All(Uri.IsHexDigit)
        && metadata.Metrics != null && metadata.Metrics.Values.All(value => value is null || double.IsFinite(value.Value));
    private static void RequireId(string id) { if (!ValidId(id)) throw new InvalidDataException("Identifiant de relevé invalide."); }
    private string PayloadPath(string id) { RequireId(id); return Path.Combine(_root, id + ".json.gz"); }
    private string MetadataPath(string id) { RequireId(id); return Path.Combine(_root, id + ".meta.json"); }
    private void WriteMetadata(SnapshotMetadata metadata)
    {
        var path = MetadataPath(metadata.Id); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(metadata, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class BoundedStream(Stream inner, long maximum, bool reading) : Stream
    {
        private long _count;
        private void Count(int length) { _count += length; if (_count > maximum) throw new InvalidDataException("Le relevé décompressé dépasse la limite autorisée."); }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); Count(read); return read; }
        public override int Read(Span<byte> buffer) { var read = inner.Read(buffer); Count(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var read = await inner.ReadAsync(buffer, ct); Count(read); return read; }
        public override void Write(byte[] buffer, int offset, int count) { Count(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Count(buffer.Length); inner.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Count(buffer.Length); await inner.WriteAsync(buffer, ct); }
        public override bool CanRead => reading; public override bool CanSeek => false; public override bool CanWrite => !reading;
        public override long Length => _count; public override long Position { get => _count; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
