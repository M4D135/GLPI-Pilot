using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace GlpiPilot.Core;

public sealed partial class TicketLibraryStore
{
    public Task ExportBootstrapAsync(string path, string installation, CancellationToken ct = default) => Run(c =>
    {
        LibraryBootstrapFormat.ValidateInstallation(installation);
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(".glpilibrary.zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choisissez un fichier .glpilibrary.zip.");
        var directory = Path.GetDirectoryName(full)!; Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".glpi-bootstrap-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var manifest = new LibraryBootstrapManifest { Installation = installation, ExportedAt = DateTimeOffset.UtcNow };
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                using var snapshot = c.BeginTransaction(deferred: true);
                using (var stream = archive.CreateEntry("tickets.jsonl", CompressionLevel.Optimal).Open())
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var query = Command(c, snapshot, "SELECT document_json,(SELECT COUNT(*) FROM ticket_library_conflicts x WHERE x.ticket_key=t.ticket_key) FROM ticket_library_tickets t WHERE installation=$installation ORDER BY ticket_key", ("$installation", installation)))
                using (var reader = query.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested(); var original = TicketLibraryRules.Deserialize(reader.GetString(0)); original.Conflicts = reader.GetInt32(1);
                        var document = LibraryBootstrapFormat.Portable(original);
                        var bytes = Encoding.UTF8.GetBytes(TicketLibraryRules.Serialize(document) + "\n");
                        manifest.PayloadBytes += bytes.Length;
                        if (++manifest.Records > LibraryBootstrapFormat.MaximumRecords || manifest.PayloadBytes > LibraryBootstrapFormat.MaximumPayloadBytes)
                            throw new InvalidDataException("La bibliothèque dépasse les limites de transfert. Aucun paquet partiel n’est publié.");
                        stream.Write(bytes); hash.AppendData(bytes);
                    }
                    manifest.PayloadSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
                ct.ThrowIfCancellationRequested(); snapshot.Commit(); LibraryBootstrapFormat.WriteManifest(archive, manifest);
            }
            _ = LibraryBootstrapFormat.Inspect(temporary, installation, ct);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }, ct);

    public Task<LibraryBootstrapImportResult> ImportBootstrapAsync(string path, string expectedSha256, string installation, CancellationToken ct = default) => Run(c =>
    {
        LibraryBootstrapFormat.ValidateInstallation(installation);
        using var file = LibraryBootstrapFormat.Open(path);
        if (expectedSha256 is null || expectedSha256.Length != 64 || !string.Equals(LibraryBootstrapFormat.Digest(file, ct), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Le paquet a changé depuis son aperçu. Aucun import effectué.");
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, true, Encoding.UTF8); var manifest = LibraryBootstrapFormat.Manifest(archive);
        if (manifest.Installation != installation) throw new InvalidDataException("Installation GLPI différente.");
        using var tx = c.BeginTransaction(); RecoverAbandonedCaptures(c, tx);
        if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_runs WHERE status='running'") != 0) throw new InvalidOperationException("Mettez la collecte locale en pause avant l’import.");
        var total = 0; var added = 0; var merged = 0; var identical = 0; var conflicts = 0;
        // Validation and merging share one transaction. A late corruption or cancellation rolls back every imported row.
        foreach (var source in LibraryBootstrapFormat.Documents(archive, manifest, ct))
        {
            var document = LibraryBootstrapFormat.Portable(source); var existing = Read(c, tx, document.Key); total++;
            if (existing is not null && existing.ContentHash == document.ContentHash && existing.DetailsCurrent == document.DetailsCurrent
                && existing.ConfirmedContentHash == document.ConfirmedContentHash && existing.ReadAt == document.ReadAt)
            { identical++; continue; }
            var outcome = SaveCore(c, tx, document, null, ct, queueChanges: false);
            if (existing is null) added++; else merged++;
            if (outcome.ConflictCreated) conflicts++;
        }
        ct.ThrowIfCancellationRequested(); tx.Commit(); return new LibraryBootstrapImportResult(total, added, merged, identical, conflicts);
    }, ct);
}
