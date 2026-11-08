using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

public sealed class LibraryBootstrapManifest
{
    public string Protocol { get; set; } = "glpi-pilot-library-bootstrap";
    public int Version { get; set; } = 1;
    public string Installation { get; set; } = "";
    public DateTimeOffset ExportedAt { get; set; }
    public int Records { get; set; }
    public long PayloadBytes { get; set; }
    public string PayloadSha256 { get; set; } = "";
    public bool ContainsPrivateData { get; set; } = true;
}
public sealed record LibraryBootstrapPreview(LibraryBootstrapManifest Manifest, string ArchiveSha256, int Complete, int Confirmed);
public sealed record LibraryBootstrapImportResult(int Read, int Added, int Merged, int Identical, int Conflicts);

/// <summary>Internal exchange only. No SQLite files, device identity, settings, credentials or acquisition leases.</summary>
public static class LibraryBootstrapFormat
{
    public const long MaximumPayloadBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumArchiveBytes = MaximumPayloadBytes + 16L * 1024 * 1024;
    public const int MaximumRecords = 1_000_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions ManifestJson = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static string Digest(Stream stream, CancellationToken ct = default)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; int read;
        try
        {
            ct.ThrowIfCancellationRequested();
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { stream.Position = 0; }
    }
    public static void ValidateInstallation(string value)
    {
        if (value is null || value.Length is 0 or > 3000 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || ObjectIdentity.Installation(value + "/front/ticket.php") != value)
            throw new InvalidDataException("Installation du paquet de bibliothèque invalide.");
    }
    internal static LibraryBootstrapManifest Manifest(ZipArchive archive)
    {
        if (archive.Entries.Count != 2 || archive.Entries.Count(e => e.FullName == "manifest.json") != 1 || archive.Entries.Count(e => e.FullName == "tickets.jsonl") != 1)
            throw new InvalidDataException("Le paquet doit contenir uniquement manifest.json et tickets.jsonl, sans chemin ni entrée supplémentaire.");
        var entry = archive.GetEntry("manifest.json")!;
        if (entry.Length is < 1 or > 65536) throw new InvalidDataException("Manifeste de bibliothèque hors limites.");
        using var stream = entry.Open();
        var bytes = new byte[65537]; var length = 0; int read;
        while (length < bytes.Length && (read = stream.Read(bytes, length, bytes.Length - length)) != 0) length += read;
        if (length != entry.Length || length > 65536) throw new InvalidDataException("Manifeste incomplet ou trop volumineux.");
        var json = Utf8.GetString(bytes, 0, length); RejectDuplicateProperties(json);
        using (var shape = JsonDocument.Parse(json))
        {
            if (shape.RootElement.ValueKind != JsonValueKind.Object || new[] { "protocol", "version", "installation", "exportedAt", "records", "payloadBytes", "payloadSha256", "containsPrivateData" }
                .Any(name => !shape.RootElement.TryGetProperty(name, out _))) throw new InvalidDataException("Manifeste de bibliothèque incomplet.");
        }
        var manifest = JsonSerializer.Deserialize<LibraryBootstrapManifest>(json, ManifestJson) ?? throw new InvalidDataException("Manifeste vide.");
        if (manifest.Protocol != "glpi-pilot-library-bootstrap" || manifest.Version != 1 || !manifest.ContainsPrivateData || manifest.ExportedAt == default
            || manifest.Records is < 0 or > MaximumRecords || manifest.PayloadBytes is < 0 or > MaximumPayloadBytes
            || manifest.PayloadSha256 is null || manifest.PayloadSha256.Length != 64 || !manifest.PayloadSha256.All(Uri.IsHexDigit)
            || archive.GetEntry("tickets.jsonl")!.Length != manifest.PayloadBytes)
            throw new InvalidDataException("Version, décompte ou empreinte de manifeste invalide.");
        ValidateInstallation(manifest.Installation); return manifest;
    }
    internal static IEnumerable<TicketLibraryDocument> Documents(ZipArchive archive, LibraryBootstrapManifest manifest, CancellationToken ct)
    {
        using var payload = archive.GetEntry("tickets.jsonl")!.Open();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; using var line = new MemoryStream();
        long bytes = 0; var count = 0; var keys = new HashSet<string>(StringComparer.Ordinal);
        int read;
        while ((read = payload.Read(buffer, 0, buffer.Length)) != 0)
        {
            ct.ThrowIfCancellationRequested(); bytes += read;
            if (bytes > MaximumPayloadBytes || bytes > manifest.PayloadBytes) throw new InvalidDataException("Le contenu dépasse la taille annoncée.");
            digest.AppendData(buffer, 0, read); var start = 0;
            while (start < read)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', start, read - start); var end = newline < 0 ? read : newline;
                if (line.Length + end - start > TicketLibraryRules.MaximumDocumentBytes) throw new InvalidDataException("Une fiche dépasse 8 Mio.");
                line.Write(buffer, start, end - start); start = end + 1;
                if (newline < 0) continue;
                if (line.Length == 0) throw new InvalidDataException("Ligne vide dans le paquet de bibliothèque.");
                var json = Utf8.GetString(line.GetBuffer(), 0, checked((int)line.Length)); line.SetLength(0);
                RejectDuplicateProperties(json);
                var document = TicketLibraryRules.Deserialize(json);
                if (document.Installation != manifest.Installation || !keys.Add(document.Key) || ++count > manifest.Records)
                    throw new InvalidDataException("Identité, installation ou décompte des fiches incohérent.");
                yield return document;
            }
        }
        var actual = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        if (line.Length != 0 || bytes != manifest.PayloadBytes || count != manifest.Records || !string.Equals(actual, manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Le paquet est incomplet ou son empreinte ne correspond pas au contenu.");
    }
    internal static TicketLibraryDocument Portable(TicketLibraryDocument document)
    {
        document = TicketLibraryRules.Copy(document);
        document.LocalRevision = 0; document.CentralRevision = 0; document.Pending = false;
        // An unresolved version is not promoted to a complete shared knowledge base.
        if (document.Conflicts > 0 || document.ConfirmationPending)
        { document.DetailsCurrent = false; document.ConfirmationPending = false; document.ConfirmationId = ""; document.ConfirmedContentHash = ""; }
        document.Conflicts = 0; return TicketLibraryRules.Validate(document);
    }
    public static LibraryBootstrapPreview Inspect(string path, string installation, CancellationToken ct = default)
    {
        ValidateInstallation(installation);
        using var file = Open(path); var hash = Digest(file, ct); using var archive = new ZipArchive(file, ZipArchiveMode.Read, true, Utf8);
        var manifest = Manifest(archive);
        if (manifest.Installation != installation) throw new InvalidDataException("Cette bibliothèque appartient à une autre installation GLPI.");
        var complete = 0; var confirmed = 0;
        foreach (var document in Documents(archive, manifest, ct))
        { if (document.DetailComplete) complete++; if (TicketLibraryRules.IsContentConfirmed(document)) confirmed++; }
        return new(manifest, hash, complete, confirmed);
    }
    internal static FileStream Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (stream.Length is < 1 or > MaximumArchiveBytes) { stream.Dispose(); throw new InvalidDataException("Taille de paquet hors limites."); }
        return stream;
    }
    internal static void WriteManifest(ZipArchive archive, LibraryBootstrapManifest manifest)
    {
        using var stream = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
        JsonSerializer.Serialize(stream, manifest, ManifestJson);
    }
    private static void RejectDuplicateProperties(string json)
    {
        using var parsed = JsonDocument.Parse(json, new() { MaxDepth = 64 });
        static void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("Propriété JSON dupliquée."); Check(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Check(child);
        }
        Check(parsed.RootElement);
    }
}

