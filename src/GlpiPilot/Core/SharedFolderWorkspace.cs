using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

/// <summary>An append-only exchange folder. It contains closed JSON transactions, never an open SQLite database.</summary>
public sealed partial class SharedFolderWorkspace
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, MaxDepth = 96 };
    internal const long MaximumEnvelopeBytes = 64L * 1024 * 1024;
    private readonly SemaphoreSlim _local = new(1, 1);
    public string Folder { get; }
    public string DataFolder { get; }
    public SharedFolderWorkspace(string folder)
    { Folder = ValidatePath(folder); DataFolder = Path.Combine(Folder, "GLPI-Pilot-Shared"); }
    /// <summary>Structural validation only: configuring an unavailable share must not touch it or block local work.</summary>
    public static string ValidatePath(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 3000 || folder.Any(char.IsControl) || !Path.IsPathFullyQualified(folder))
            throw new InvalidDataException("Choisissez un chemin absolu pour le dossier partagé (dossier réseau ou lecteur monté).");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }
    internal async Task<T> LockedAsync<T>(Func<Lease, Task<T>> operation, CancellationToken ct)
    {
        if (AppPaths.SimulationRoot is not null) throw new InvalidOperationException("La simulation utilise une réplique locale isolée et ne peut pas ouvrir le dossier partagé.");
        await _local.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(Folder)) throw new IOException("Le dossier partagé est indisponible. Les modifications restent enregistrées sur ce poste.");
            Directory.CreateDirectory(DataFolder); RejectLink(DataFolder);
            var lockPath = Path.Combine(DataFolder, "workspace.lock"); FileStream? held = null; var started = System.Diagnostics.Stopwatch.StartNew();
            while (held is null)
            {
                ct.ThrowIfCancellationRequested(); RejectLink(lockPath);
                try { held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough); }
                catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(8))
                { await Task.Delay(80, ct).ConfigureAwait(false); }
                catch (IOException error) { throw new IOException("Le dossier partagé est occupé ou indisponible. Le lot reste en attente et pourra être repris.", error); }
            }
            await using (held)
            {
                var identity = await ReadIdentityAsync(ct).ConfigureAwait(false);
                return await operation(new Lease(DataFolder, identity, ct)).ConfigureAwait(false);
            }
        }
        finally { _local.Release(); }
    }
    private async Task<string> ReadIdentityAsync(CancellationToken ct)
    {
        var path = Path.Combine(DataFolder, "workspace.json"); RejectLink(path);
        if (!File.Exists(path))
        {
            if (Directory.EnumerateDirectories(DataFolder).Any(directory => Directory.EnumerateFiles(directory, "*.json").Any()))
                throw new InvalidDataException("Le dossier partagé contient des transactions mais son identité est absente. Restaurez son fichier workspace.json ; aucun nouveau registre n’a été créé.");
            var next = new FolderIdentity(1, "GLPI-Pilot-Shared", Guid.NewGuid().ToString("N"));
            await AtomicWriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(next, Json), ct).ConfigureAwait(false);
        }
        var info = new FileInfo(path); if (info.Length is < 1 or > 4096) throw new InvalidDataException("Identité du dossier partagé invalide.");
        FolderIdentity identity;
        try { identity = JsonSerializer.Deserialize<FolderIdentity>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), Json) ?? throw new InvalidDataException("Identité partagée vide."); }
        catch (JsonException error) { throw new InvalidDataException("Identité du dossier partagé illisible.", error); }
        if (identity.Format != 1 || identity.Application != "GLPI-Pilot-Shared" || !Regex.IsMatch(identity.Id ?? "", "^[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Ce dossier n’est pas un registre partagé GLPI Pilot compatible.");
        return identity.Id!;
    }
    internal static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Un fichier interne du registre partagé a été remplacé par une liaison ; aucun échange n’a été effectué.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
    }
    internal static async Task AtomicWriteAsync(string destination, byte[] bytes, CancellationToken ct, bool replace = false)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, overwrite: replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record FolderIdentity(int Format, string Application, string Id);
    internal sealed class Lease(string directory, string identity, CancellationToken ct)
    {
        public string Identity { get; } = identity;
        public CancellationToken Cancellation { get; } = ct;
        public string DomainPath(string domain)
        {
            if (domain is not ("team" or "tickets")) throw new InvalidDataException("Collection partagée inconnue.");
            var path = Path.Combine(directory, domain); Directory.CreateDirectory(path); RejectLink(path); return path;
        }
    }
}

internal sealed class SharedFolderJournal
{
    private readonly string _domain;
    public SharedFolderJournal(string domain) => _domain = domain;
    internal sealed class Envelope
    {
        public int Format { get; set; } = 1;
        public string Domain { get; set; } = "";
        public long Sequence { get; set; }
        public string PreviousHash { get; set; } = "";
        public string Kind { get; set; } = "";
        public JsonElement Request { get; set; }
        public string ReceiptHash { get; set; } = "";
        public string Hash { get; set; } = "";
    }
    private sealed record Head(int Format, string Domain, long Count, string Hash);
    public static string Digest<T>(T value) => SharedFolderWorkspace.DigestBounded(value);
    private static string Hash(Envelope value) => Digest(new { value.Format, value.Domain, value.Sequence, value.PreviousHash, value.Kind, value.Request, value.ReceiptHash });
    public async Task RefreshAsync(SharedFolderWorkspace.Lease lease, LocalFolderReplicaIndex.Session index, Func<string, JsonElement, Task<string>> apply)
    {
        var domainPath = lease.DomainPath(_domain); var headPath = Path.Combine(domainPath, "head.json"); SharedFolderWorkspace.RejectLink(headPath);
        // Keep bounded short names, not 100 000 copies of a potentially long UNC path.
        var files = Directory.EnumerateFiles(domainPath).Select(path => Path.GetFileName(path)!)
            .Where(name => !name.EndsWith(".tmp", StringComparison.Ordinal) && name != "head.json")
            .Take(100001).Order(StringComparer.Ordinal).ToArray();
        Head head;
        if (!File.Exists(headPath))
        {
            if (files.Length > 0) throw new InvalidDataException("Le point de contrôle du journal partagé est absent ; restaurez-le avant de poursuivre.");
            head = new(1, _domain, 0, ""); await SharedFolderWorkspace.AtomicWriteAsync(headPath, JsonSerializer.SerializeToUtf8Bytes(head, SharedFolderWorkspace.Json), lease.Cancellation).ConfigureAwait(false);
        }
        else
        {
            if (new FileInfo(headPath).Length is < 1 or > 4096) throw new InvalidDataException("Point de contrôle partagé invalide.");
            try { head = JsonSerializer.Deserialize<Head>(await File.ReadAllTextAsync(headPath, lease.Cancellation).ConfigureAwait(false), SharedFolderWorkspace.Json) ?? throw new InvalidDataException("Point de contrôle partagé vide."); }
            catch (JsonException error) { throw new InvalidDataException("Point de contrôle partagé illisible.", error); }
            if (head.Format != 1 || head.Domain != _domain || head.Count < 0 || head.Hash is null || (head.Count == 0 ? head.Hash.Length != 0 : head.Hash.Length != 64)) throw new InvalidDataException("Point de contrôle partagé incohérent.");
        }
        if (files.Length > 100000) throw new InvalidDataException("Le journal partagé dépasse 100 000 transactions ; archivez-le avec une migration contrôlée avant de poursuivre.");
        if (files.Length < head.Count) throw new InvalidDataException("Des transactions validées ont disparu du dossier partagé. L’échange est arrêté pour préserver la cohérence.");
        await index.EnsureIdentityAsync(lease.Identity).ConfigureAwait(false);
        var loaded = (await index.JournalAsync().ConfigureAwait(false)).ToList();
        if (files.Length < loaded.Count) throw new InvalidDataException("Des transactions déjà validées ont disparu du dossier partagé. L’index ne peut pas masquer une perte du journal.");
        var lastHash = loaded.LastOrDefault()?.Hash ?? "";
        for (var i = 0; i < files.Length; i++)
        {
            lease.Cancellation.ThrowIfCancellationRequested();
            var expected = (i + 1).ToString("D20", CultureInfo.InvariantCulture) + ".json";
            if (files[i] != expected) throw new InvalidDataException("Le journal partagé contient une transaction manquante ou un nom inattendu.");
            var file = Path.Combine(domainPath, files[i]);
            SharedFolderWorkspace.RejectLink(file); var info = new FileInfo(file);
            if (info.Length is < 1 or > SharedFolderWorkspace.MaximumEnvelopeBytes) throw new InvalidDataException("Une transaction partagée est vide ou dépasse 64 Mio.");
            if (i < loaded.Count)
            {
                if (loaded[i].Length != info.Length || loaded[i].ModifiedTicks != info.LastWriteTimeUtc.Ticks) throw new InvalidDataException("Une transaction déjà validée a été modifiée dans le dossier partagé. Aucun échange n’a été poursuivi.");
                continue;
            }
            Envelope envelope;
            try { envelope = JsonSerializer.Deserialize<Envelope>(await File.ReadAllTextAsync(file, lease.Cancellation).ConfigureAwait(false), SharedFolderWorkspace.Json) ?? throw new InvalidDataException("Transaction partagée vide."); }
            catch (JsonException error) { throw new InvalidDataException("Transaction partagée incomplète ou illisible.", error); }
            if (envelope.Format != 1 || envelope.Domain != _domain || envelope.Sequence != i + 1 || envelope.PreviousHash != lastHash || envelope.Hash != Hash(envelope) || envelope.Request.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("La chaîne d’intégrité du journal partagé est invalide ; le registre reste inchangé.");
            using (index.Begin())
            {
                var receiptHash = await apply(envelope.Kind, envelope.Request).ConfigureAwait(false);
                if (receiptHash != envelope.ReceiptHash) throw new InvalidDataException("Le résultat d’une transaction partagée ne correspond pas à son reçu. Une version compatible ou une restauration du registre est nécessaire.");
                var entry = new LocalFolderReplicaIndex.JournalEntry(envelope.Sequence, info.Length, info.LastWriteTimeUtc.Ticks, envelope.Hash);
                await index.RecordJournalAsync(entry).ConfigureAwait(false); index.Commit(); loaded.Add(entry); lastHash = envelope.Hash;
            }
        }
        if (head.Count > 0 && loaded[checked((int)head.Count - 1)].Hash != head.Hash) throw new InvalidDataException("L’empreinte du point de contrôle partagé ne correspond pas au journal.");
        if (head.Count < loaded.Count) await WriteHeadAsync(lease, loaded.Count, lastHash).ConfigureAwait(false);
    }
    public async Task AppendAsync<T>(SharedFolderWorkspace.Lease lease, LocalFolderReplicaIndex.Session index, string kind, T request, string receiptHash)
    {
        var previous = await index.LastJournalAsync().ConfigureAwait(false);
        if (previous?.Sequence >= 100000) throw new InvalidDataException("Le journal partagé dépasse 100 000 transactions ; une migration contrôlée est nécessaire.");
        using var body = JsonDocument.Parse(SharedFolderWorkspace.SerializeBounded(request));
        var envelope = new Envelope { Domain = _domain, Sequence = (previous?.Sequence ?? 0) + 1, PreviousHash = previous?.Hash ?? "", Kind = kind, Request = body.RootElement, ReceiptHash = receiptHash };
        envelope.Hash = Hash(envelope); var bytes = SharedFolderWorkspace.SerializeBounded(envelope);
        var path = Path.Combine(lease.DomainPath(_domain), envelope.Sequence.ToString("D20", CultureInfo.InvariantCulture) + ".json");
        await SharedFolderWorkspace.AtomicWriteAsync(path, bytes, lease.Cancellation).ConfigureAwait(false);
        var info = new FileInfo(path);
        await index.RecordJournalAsync(new(envelope.Sequence, info.Length, info.LastWriteTimeUtc.Ticks, envelope.Hash)).ConfigureAwait(false);
        await WriteHeadAsync(lease, envelope.Sequence, envelope.Hash).ConfigureAwait(false);
    }
    private Task WriteHeadAsync(SharedFolderWorkspace.Lease lease, long count, string hash) => SharedFolderWorkspace.AtomicWriteAsync(Path.Combine(lease.DomainPath(_domain), "head.json"),
        JsonSerializer.SerializeToUtf8Bytes(new Head(1, _domain, count, hash), SharedFolderWorkspace.Json), lease.Cancellation, replace: true);
}
