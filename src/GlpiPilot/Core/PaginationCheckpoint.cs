using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class PaginationCheckpoint
{
    public int Version { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
    public string Target { get; set; } = "";
    public string ScopeFingerprint { get; set; } = "";
    public string FirstSignature { get; set; } = "";
    public string LastUrl { get; set; } = "";
    public string LastSignature { get; set; } = "";
    public ExtractionResult Data { get; set; } = new();
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
    public List<string> Signatures { get; set; } = new();
}

public sealed class PaginationCheckpointStore
{
    private readonly string _root;
    public PaginationCheckpointStore(string? root = null) => _root = root ?? Path.Combine(AppPaths.DataRoot, "Checkpoints");
    private string PathFor(string target) => Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TargetUrlPolicy.PersistedTarget(target)))) + ".json.gz");
    public PaginationCheckpoint? Load(AppSettings settings)
    {
        var path = PathFor(settings.PortalUrl); if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) return null;
            using var file = File.OpenRead(path); using var zip = new GZipStream(file, CompressionMode.Decompress); using var data = new MemoryStream();
            var buffer = new byte[65536]; int count;
            while ((count = zip.Read(buffer)) > 0) { if (data.Length + count > 128 * 1024 * 1024) return null; data.Write(buffer, 0, count); }
            var checkpoint = JsonSerializer.Deserialize<PaginationCheckpoint>(data.ToArray());
            if (checkpoint == null || checkpoint.Version != 1 || checkpoint.Target != TargetUrlPolicy.PersistedTarget(settings.PortalUrl)
                || DateTimeOffset.Now - checkpoint.SavedAt > TimeSpan.FromMinutes(settings.CheckpointMaximumAgeMinutes)
                || checkpoint.SavedAt > DateTimeOffset.Now.AddMinutes(1) || checkpoint.Data == null || checkpoint.Data.Rows == null || checkpoint.Data.Headers == null
                || checkpoint.Data.Rows.Count > settings.MaxRows || checkpoint.Data.Pages >= settings.MaxPages || checkpoint.Data.Pages < 1
                || checkpoint.Signatures == null || checkpoint.Signatures.Count != checkpoint.Data.Pages || checkpoint.Keys == null
                || !TargetUrlPolicy.SamePage(checkpoint.LastUrl, settings.PortalUrl)
                || checkpoint.StartedAt > checkpoint.SavedAt || checkpoint.Data.Provenance == null || checkpoint.Data.Warnings == null
                || checkpoint.ScopeFingerprint == null || checkpoint.ScopeFingerprint.Length == 0 || checkpoint.FirstSignature == null || checkpoint.LastSignature == null
                || checkpoint.Signatures.Any(signature => string.IsNullOrEmpty(signature)) || checkpoint.Signatures.Distinct(StringComparer.Ordinal).Count() != checkpoint.Signatures.Count
                || checkpoint.Keys.Count > settings.MaxRows || checkpoint.Keys.Any(pair => string.IsNullOrEmpty(pair.Key) || pair.Value == null)
                || checkpoint.Data.Headers.Count is < 1 or > 2048 || checkpoint.Data.Headers.Any(header => header == null)
                || checkpoint.Data.Rows.Any(row => row == null || row.Length != checkpoint.Data.Headers.Count || row.Any(cell => cell == null))) return null;
            return checkpoint;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }
    public void Save(PaginationCheckpoint checkpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(_root); var path = PathFor(checkpoint.Target); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        checkpoint.SavedAt = DateTimeOffset.Now;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new GZipStream(file, CompressionLevel.Fastest)) JsonSerializer.Serialize(zip, checkpoint);
            ct.ThrowIfCancellationRequested();
            if (new FileInfo(temporary).Length > 32 * 1024 * 1024) throw new IOException("Le point de reprise dépasse la limite de stockage de 32 Mio.");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Delete(string target) { var path = PathFor(target); if (File.Exists(path)) File.Delete(path); }
}
