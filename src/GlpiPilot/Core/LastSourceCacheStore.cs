using System.Security.Cryptography;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record LastSourceCacheEntry(string Generation, string SnapshotId, DateTimeOffset SavedAt, string SourceKind,
    string FileName, string Title, string SourceUrl, DateTimeOffset ExtractedAt, int Rows, int Columns, bool Complete, string PayloadSha256);
public sealed record LastSourceCacheRead(SnapshotDocument Document, LastSourceCacheEntry Entry, bool Recovered);

/// <summary>Two bounded, immutable SnapshotStore receipts, selected by an atomic local index.</summary>
public sealed class LastSourceCacheStore
{
    private sealed class Index
    {
        public int Version { get; set; } = 1;
        public LastSourceCacheEntry? Current { get; set; }
        public LastSourceCacheEntry? Previous { get; set; }
    }
    private sealed record Envelope(Index Index, string Sha256);
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 32 };
    private readonly string _root;
    public LastSourceCacheStore(string? root = null) => _root = Path.GetFullPath(root ?? Path.Combine(AppPaths.DataRoot, "LastSource"));
    private string IndexPath => Path.Combine(_root, "current.json");
    private string BackupIndexPath => Path.Combine(_root, "current.previous.json");
    private static bool ValidId(string value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;
    private static bool Valid(LastSourceCacheEntry value) => ValidId(value.Generation) && ValidId(value.SnapshotId)
        && value.SourceKind is "file" or "glpi" or "browser" or "unknown" && value.Rows is >= 0 and <= 1000000 && value.Columns is >= 0 and <= 16384
        && value.FileName is { Length: <= 1000 } && value.Title is { Length: <= 8192 } && value.SourceUrl is { Length: <= 8192 }
        && UpdateService.IsHash(value.PayloadSha256);
    private static string Digest(Index value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    private static void RealOnly()
    {
        if (AppPaths.SimulationRoot is not null) throw new InvalidOperationException("La démonstration ne peut pas lire ou modifier le dernier relevé réel conservé.");
    }
    private Index ReadIndexFile(string path)
    {
        UpdateService.EnsureNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 32768) throw new InvalidDataException("L’index du dernier relevé dépasse la taille autorisée.");
        var bytes = new byte[32769]; var count = 0; int read;
        while ((read = stream.Read(bytes, count, bytes.Length - count)) > 0)
        { count += read; if (count == bytes.Length) throw new InvalidDataException("Index de cache trop volumineux."); }
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes.AsSpan(0, count), Json) ?? throw new InvalidDataException("Index de cache vide.");
        var value = envelope.Index;
        if (value is null || value.Version != 1 || envelope.Sha256 != Digest(value)
            || value.Current is not null && !Valid(value.Current) || value.Previous is not null && !Valid(value.Previous))
            throw new InvalidDataException("L’index du dernier relevé est incohérent.");
        return value;
    }
    private (Index Value, bool Recovered) ReadIndex()
    {
        if (!File.Exists(IndexPath) && !File.Exists(BackupIndexPath)) return (new(), false);
        try { return (ReadIndexFile(IndexPath), false); }
        catch (Exception error) when (IsStorageError(error))
        {
            try { return (ReadIndexFile(BackupIndexPath), true); }
            catch (Exception backupError) when (IsStorageError(backupError))
            { throw new InvalidDataException("Le cache local est illisible. Importez une nouvelle liste ou effacez uniquement ce cache ; votre base et vos réglages restent conservés."); }
        }
    }
    internal static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException;
    private void WriteIndex(Index value)
    {
        var temporary = Path.Combine(_root, ".index-" + Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(value, Digest(value)), Json);
        if (bytes.Length > 32768) throw new InvalidDataException("L’index du dernier relevé dépasse la taille autorisée.");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(flushToDisk: true); }
            if (File.Exists(IndexPath)) File.Replace(temporary, IndexPath, BackupIndexPath);
            else File.Move(temporary, IndexPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private FileStream AcquireLock()
    {
        var path = Path.Combine(_root, ".cache.lock"); UpdateService.EnsureNoLinks(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public LastSourceCacheEntry? Describe()
    {
        RealOnly(); lock (Gate)
        {
            UpdateService.EnsureNoLinks(_root); if (!Directory.Exists(_root)) return null;
            using var lease = AcquireLock(); return ReadIndex().Value.Current;
        }
    }
    public LastSourceCacheEntry Save(ExtractionResult source, AppSettings settings, CityEnrichmentResult? locations = null,
        IReadOnlyList<SitePopulationEntry>? populations = null, CancellationToken ct = default)
    {
        RealOnly(); ct.ThrowIfCancellationRequested();
        if (source.Provenance.SourceKind is not ("file" or "glpi" or "browser" or "unknown") || source.SourceUrl.Contains("simulation.glpi-pilot.invalid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Seul un relevé réel peut remplacer le cache de consultation.");
        // SnapshotDocument persists only the interpretation, never the full app,
        // browser profile, password store, cookies or source acquisition settings.
        var provenance = JsonSerializer.Deserialize<ExtractionProvenance>(JsonSerializer.Serialize(source.Provenance, Json), Json)!;
        provenance.RequestedUrl = AppPaths.SafeUrl(provenance.RequestedUrl);
        provenance.OriginalFileName = Path.GetFileName(provenance.OriginalFileName);
        var data = new ExtractionResult
        {
            Headers = source.Headers, Rows = source.Rows, SourceUrl = AppPaths.SafeUrl(source.SourceUrl), Title = source.Title,
            ExtractedAt = source.ExtractedAt, Pages = source.Pages, ExpectedTotal = source.ExpectedTotal, Scope = source.Scope,
            Complete = source.Complete, Warnings = source.Warnings, Status = source.Status, Provenance = provenance, Quality = source.Quality
        };
        // Rebind captured decisions to the sanitized receipt without recomputing
        // locations or consulting today's correction files during restoration.
        var evidence = SnapshotLocationEvidence.Capture(source, locations);
        var cleanLocations = evidence?.ToReport(data);
        // The opaque fingerprint retains historical continuity without retaining
        // source query parameters or credentials in the offline receipt.
        var sourceKey = SnapshotStore.SourceKey(source);
        lock (Gate)
        {
            UpdateService.EnsureNoLinks(_root); Directory.CreateDirectory(_root);
            using var lease = AcquireLock();
            Index previous;
            try { previous = ReadIndex().Value; } catch (InvalidDataException) { previous = new(); }
            // A crash may have left an unreferenced receipt. Refuse additional
            // growth if it cannot be removed, keeping the active receipts intact.
            Prune(previous.Current?.Generation, previous.Previous?.Generation, required: true);
            LastSourceCacheEntry? verifiedPrevious = null;
            foreach (var candidate in new[] { previous.Current, previous.Previous }.Where(value => value is not null).Cast<LastSourceCacheEntry>())
            {
                try { ReadEntry(candidate, ct); verifiedPrevious = candidate; break; }
                catch (Exception error) when (IsStorageError(error)) { }
            }
            var generation = Guid.NewGuid().ToString("N"); var staging = Path.Combine(_root, ".pending-" + generation); var destination = Path.Combine(_root, generation);
            var committed = false;
            try
            {
                Directory.CreateDirectory(staging);
                var snapshot = new SnapshotStore(staging).Save(data, settings, ct, cleanLocations, populations, sourceKey);
                var entry = new LastSourceCacheEntry(generation, snapshot.Id, DateTimeOffset.UtcNow, data.Provenance.SourceKind,
                    data.Provenance.OriginalFileName, data.Title, data.SourceUrl, data.ExtractedAt, data.Rows.Count, data.Headers.Count, data.Complete, snapshot.PayloadSha256);
                if (!Valid(entry)) throw new InvalidDataException("Métadonnées du cache invalides.");
                ct.ThrowIfCancellationRequested(); Directory.Move(staging, destination);
                WriteIndex(new() { Current = entry, Previous = verifiedPrevious }); committed = true;
                // The committed pointer is the cancellation boundary. Cancellation
                // afterwards must not report this successful replacement as failed.
                try { Prune(entry.Generation, verifiedPrevious?.Generation); }
                catch (Exception error) when (IsStorageError(error)) { /* Retry cleanup before accepting any subsequent save. */ }
                return entry;
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                if (!committed && Directory.Exists(destination)) Directory.Delete(destination, true);
            }
        }
    }
    public LastSourceCacheRead? Load(CancellationToken ct = default)
    {
        RealOnly(); lock (Gate)
        {
            UpdateService.EnsureNoLinks(_root); if (!Directory.Exists(_root)) return null;
            using var lease = AcquireLock(); var (index, recovered) = ReadIndex();
            if (index.Current is null) return null;
            foreach (var entry in new[] { index.Current, index.Previous }.Where(value => value is not null).Cast<LastSourceCacheEntry>())
            {
                try
                {
                    var document = ReadEntry(entry, ct);
                    return new(document, entry, recovered || entry != index.Current);
                }
                catch (Exception error) when (IsStorageError(error)) { }
            }
            throw new InvalidDataException("Le dernier relevé et sa copie de secours ne peuvent pas être vérifiés. Aucune donnée n’a été chargée ; importez une nouvelle source.");
        }
    }
    private SnapshotDocument ReadEntry(LastSourceCacheEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var folder = Path.Combine(_root, entry.Generation); UpdateService.EnsureNoLinks(folder);
        UpdateService.EnsureNoLinks(Path.Combine(folder, entry.SnapshotId + ".meta.json"));
        UpdateService.EnsureNoLinks(Path.Combine(folder, entry.SnapshotId + ".json.gz"));
        var document = new SnapshotStore(folder).Load(entry.SnapshotId, ct);
        if (document.Metadata.PayloadSha256 != entry.PayloadSha256 || document.Data.Provenance.SourceKind != entry.SourceKind
            || document.Data.Rows.Count != entry.Rows || document.Data.Headers.Count != entry.Columns || document.Data.ExtractedAt != entry.ExtractedAt)
            throw new InvalidDataException("Le relevé ne correspond pas à son index de cache.");
        return document;
    }
    private void Prune(string? current, string? previous, bool required = false)
    {
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(directory);
            if ((!ValidId(name) && !name.StartsWith(".pending-", StringComparison.Ordinal)) || name == current || name == previous) continue;
            try { UpdateService.EnsureNoLinks(directory); Directory.Delete(directory, true); }
            catch (Exception error) when (!required && error is IOException or UnauthorizedAccessException) { }
        }
        foreach (var path in Directory.EnumerateFiles(_root, ".index-*.tmp"))
        {
            try { UpdateService.EnsureNoLinks(path); File.Delete(path); }
            catch (Exception error) when (!required && error is IOException or UnauthorizedAccessException) { }
        }
    }
    public void Clear(CancellationToken ct = default)
    {
        RealOnly(); lock (Gate)
        {
            UpdateService.EnsureNoLinks(_root); if (!Directory.Exists(_root)) return; ct.ThrowIfCancellationRequested();
            using var lease = AcquireLock();
            // A valid empty index is a durable tombstone, never a request to fall
            // back to an older receipt. Deletion failure is reported, not hidden.
            WriteIndex(new());
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                var name = Path.GetFileName(directory);
                if (!ValidId(name) && !name.StartsWith(".pending-", StringComparison.Ordinal)) continue;
                UpdateService.EnsureNoLinks(directory); Directory.Delete(directory, true);
            }
            if (File.Exists(BackupIndexPath)) File.Delete(BackupIndexPath);
        }
    }
}
