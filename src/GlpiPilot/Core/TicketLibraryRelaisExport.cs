using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

public sealed record RelaisExportPreview(string Installation, long SourceTicketCount, long EligibleTicketCount,
    long MissingContentCount, long IncompleteCount, long UnconfirmedCount, long PendingCount, long ConflictCount)
{
    public long ExcludedTicketCount => SourceTicketCount - EligibleTicketCount;
    public bool CanExport => EligibleTicketCount > 0;
    public string Message => $"{EligibleTicketCount:N0} / {SourceTicketCount:N0} fiches complètes, confirmées et sans conflit."
        + (ExcludedTicketCount > 0 ? $" {ExcludedTicketCount:N0} fiches restent hors de l’export ; les motifs peuvent se recouper : {MissingContentCount:N0} sans contenu, {IncompleteCount:N0} incomplètes, {UnconfirmedCount:N0} non confirmées, {PendingCount:N0} en attente et {ConflictCount:N0} avec conflit." : "");
}

public sealed record RelaisExportProgress(string Phase, long Tickets, long Texts, long JsonBytes, string Message);

public sealed record RelaisExportResult(string Path, string Installation, DateTimeOffset ExportedAt,
    long TicketCount, long TextCount, long SourceTicketCount, long ExcludedTicketCount,
    long MissingContentCount, long IncompleteCount, long UnconfirmedCount, long PendingCount, long ConflictCount,
    long FileBytes, string Sha256);

/// <summary>A bounded, allowlisted SQLite exchange file, never a backup of an operational database.</summary>
public static class RelaisExportFormat
{
    public const string Protocol = "glpi-pilot-relais";
    public const int Version = 1;
    public const int MaximumTexts = 200_000;
    public const long MaximumJsonBytes = 256L * 1024 * 1024;
    public const long MaximumBodyBytes = 2L * 1024 * 1024;
    public const long MaximumFileBytes = 512L * 1024 * 1024;
    internal static readonly string[] TicketColumns = ["installation", "ticket_key", "version_id", "version_kind", "access_basis",
        "detail_complete", "content_confirmed", "confirmation_pending", "has_content", "conflict_count"];
    internal static readonly string[] TextColumns = ["ticket_key", "installation", "ticket_id", "ticket_url", "title", "version_id", "version_kind",
        "content_hash", "local_revision", "central_revision", "read_at", "last_modified_raw", "last_modified_at", "observed_at", "detail_modified_raw", "detail_modified_at",
        "detail_complete", "content_confirmed", "confirmation_pending", "has_content", "coverage_complete", "conflict_count", "source_path", "source_kind",
        "section", "section_key", "row_index", "source_url", "source_observed_at", "text_visibility", "access_basis", "body", "citation_key"];
    internal static readonly HashSet<string> IntegerColumns = new(["detail_complete", "content_confirmed", "confirmation_pending", "has_content",
        "coverage_complete", "conflict_count", "row_index"], StringComparer.Ordinal);
    internal static readonly string ManifestSql = """
        CREATE TABLE ticket_library_schema(singleton INTEGER PRIMARY KEY CHECK(singleton=1),version INTEGER NOT NULL CHECK(version=1));
        INSERT INTO ticket_library_schema VALUES(1,1);
        CREATE TABLE relais_manifest(
          singleton INTEGER PRIMARY KEY CHECK(singleton=1),protocol TEXT NOT NULL,version INTEGER NOT NULL,knowledge_version INTEGER NOT NULL,
          application_version TEXT NOT NULL,exported_at TEXT NOT NULL,installation TEXT NOT NULL,scope TEXT NOT NULL,complete INTEGER NOT NULL,
          ticket_count INTEGER NOT NULL,text_count INTEGER NOT NULL,source_ticket_count INTEGER NOT NULL,excluded_ticket_count INTEGER NOT NULL,
          missing_content_count INTEGER NOT NULL,incomplete_count INTEGER NOT NULL,unconfirmed_count INTEGER NOT NULL,pending_count INTEGER NOT NULL,conflict_count INTEGER NOT NULL);
        """;
    internal static readonly string SchemaSql = ManifestSql
        + "CREATE TABLE relais_tickets(" + string.Join(',', TicketColumns.Select(name => name + (IntegerColumns.Contains(name) ? " INTEGER" : " TEXT")
            + " NOT NULL" + (name == "ticket_key" ? " PRIMARY KEY" : ""))) + ");\n"
        + "CREATE TABLE relais_texts(" + string.Join(',', TextColumns.Select(name => name + (IntegerColumns.Contains(name) ? " INTEGER" : " TEXT")
            + (name is "ticket_key" or "citation_key" ? " NOT NULL" : "") + (name == "citation_key" ? " PRIMARY KEY" : "")))
            + ",FOREIGN KEY(ticket_key) REFERENCES relais_tickets(ticket_key));\n"
        + "CREATE INDEX relais_texts_ticket ON relais_texts(ticket_key,source_path);\n"
        + "CREATE VIEW ticket_library_knowledge_v1_tickets AS SELECT " + string.Join(',', TicketColumns) + " FROM relais_tickets;\n"
        + "CREATE VIEW ticket_library_knowledge_v1_texts AS SELECT " + string.Join(',', TextColumns) + " FROM relais_texts;\n";
}

// Tests may tighten the production ceilings; no caller can raise them.
internal sealed record RelaisExportLimits(int Texts, long JsonBytes, long BodyBytes, long FileBytes)
{
    internal static readonly RelaisExportLimits Default = new(RelaisExportFormat.MaximumTexts, RelaisExportFormat.MaximumJsonBytes,
        RelaisExportFormat.MaximumBodyBytes, RelaisExportFormat.MaximumFileBytes);
    internal void Validate()
    {
        if (Texts is < 1 or > RelaisExportFormat.MaximumTexts || JsonBytes is < 1 or > RelaisExportFormat.MaximumJsonBytes
            || BodyBytes is < 1 or > RelaisExportFormat.MaximumBodyBytes || FileBytes is < 1 or > RelaisExportFormat.MaximumFileBytes)
            throw new ArgumentOutOfRangeException(nameof(Texts));
    }
}

public sealed partial class TicketLibraryStore
{
    private const string RelaisEligibleSql = """
        version_id='selected' AND version_kind='selected' AND detail_complete=1 AND content_confirmed=1
        AND confirmation_pending=0 AND has_content=1 AND conflict_count=0
        """;
    private static readonly Regex RelaisSensitiveParameter = new(@"password|passwd|token|csrf|secret|cookie|session|authorization|api[._-]?key|credential|samlresponse|samlrequest",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // This serializer only counts bytes. Its output is never rendered in HTML or persisted as ticket content.
    private static readonly JsonSerializerOptions RelaisJsonBudget = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Advisory counts only. Export takes its own fresh read-only snapshot and returns the actual counts.</summary>
    public Task<RelaisExportPreview> PreviewRelaisExportAsync(string installation, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); ValidateRelaisInstallation(installation); ValidateDatabasePath(DatabasePath);
        if (!File.Exists(DatabasePath)) return new RelaisExportPreview(installation, 0, 0, 0, 0, 0, 0, 0);
        using var source = OpenRelaisSource(); using var snapshot = source.BeginTransaction(deferred: true);
        var preview = ReadRelaisPreview(source, snapshot, installation, ct);
        ct.ThrowIfCancellationRequested(); snapshot.Commit(); return preview;
    }, ct);

    public Task<RelaisExportResult> ExportRelaisAsync(string destination, string installation,
        IProgress<RelaisExportProgress>? progress = null, CancellationToken ct = default)
        => ExportRelaisWithLimitsAsync(destination, installation, RelaisExportLimits.Default, progress, ct);

    internal Task<RelaisExportResult> ExportRelaisWithLimitsAsync(string destination, string installation, RelaisExportLimits limits,
        IProgress<RelaisExportProgress>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); limits.Validate(); ValidateRelaisInstallation(installation);
        var full = ValidateRelaisDestination(destination); var directory = Path.GetDirectoryName(full)!;
        string? localTemporary = null, transferTemporary = null;
        try
        {
            RelaisExportPreview preview; long ticketCount = 0, textCount = 0, jsonBytes = 2;
            var exportedAt = DateTimeOffset.UtcNow;
            using (var source = OpenRelaisSource())
            using (var snapshot = source.BeginTransaction(deferred: true))
            {
                preview = ReadRelaisPreview(source, snapshot, installation, ct);
                if (!preview.CanExport) throw new InvalidOperationException("Aucun texte admissible à exporter pour Relais. " + preview.Message);
                progress?.Report(new("snapshot", 0, 0, jsonBytes, "Instantané de la bibliothèque établi. " + preview.Message));
                ct.ThrowIfCancellationRequested();
                var candidate = ValidateDatabasePath(Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Relais-" + Guid.NewGuid().ToString("N") + ".sqlite"));
                using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { localTemporary = candidate; }
                using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = localTemporary, Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false, DefaultTimeout = 10 }.ToString()))
                {
                    target.Open(); Execute(target, null, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA page_size=4096;");
                    Execute(target, null, "PRAGMA max_page_count=" + Math.Max(1, limits.FileBytes / 4096).ToString(CultureInfo.InvariantCulture));
                    using var transaction = target.BeginTransaction(); Execute(target, transaction, RelaisExportFormat.SchemaSql);
                    using var insertText = RelaisInsert(target, transaction, "relais_texts", RelaisExportFormat.TextColumns);
                    using var insertTicket = RelaisInsert(target, transaction, "relais_tickets", RelaisExportFormat.TicketColumns);
                    using (var query = Command(source, snapshot, "SELECT " + string.Join(',', RelaisExportFormat.TextColumns)
                        + " FROM ticket_library_knowledge_v1_texts WHERE installation=$installation AND " + RelaisEligibleSql
                        + " AND body<>'' ORDER BY ticket_key,citation_key LIMIT $limit", ("$installation", installation), ("$limit", (long)limits.Texts + 1)))
                    using (var reader = query.ExecuteReader())
                    {
                        string? previousKey = null, previousHash = null;
                        while (reader.Read())
                        {
                            ct.ThrowIfCancellationRequested();
                            if (++textCount > limits.Texts) throw new InvalidDataException("L’export dépasse la limite de fragments Relais. Aucun fichier partiel n’a été publié.");
                            var row = ReadRelaisRow(reader); ValidateRelaisRow(row, installation);
                            var body = (string)row["body"]!;
                            if (Encoding.UTF8.GetByteCount(body) > limits.BodyBytes)
                                throw new InvalidDataException("Un fragment dépasse la taille de corps admise par Relais. Aucun fichier partiel n’a été publié.");
                            // Relaxed escaping preserves French/Unicode byte sizes. Retained null integers and array separators conservatively exceed the consumer's row budget.
                            jsonBytes = checked(jsonBytes + JsonSerializer.SerializeToUtf8Bytes(row, RelaisJsonBudget).LongLength + 1);
                            if (jsonBytes > limits.JsonBytes) throw new InvalidDataException("L’export dépasse la taille JSON admise par Relais. Aucun fichier partiel n’a été publié.");
                            var key = (string)row["ticket_key"]!; var hash = (string)row["content_hash"]!;
                            if (key != previousKey)
                            {
                                BindRelaisRow(insertTicket, RelaisExportFormat.TicketColumns, row); insertTicket.ExecuteNonQuery();
                                previousKey = key; previousHash = hash; ticketCount++;
                            }
                            else if (hash != previousHash) throw new InvalidDataException("Les fragments d’un ticket ne portent pas la même version. Aucun fichier n’a été publié.");
                            BindRelaisRow(insertText, RelaisExportFormat.TextColumns, row); insertText.ExecuteNonQuery();
                            if (textCount == 1 || textCount % 256 == 0)
                                progress?.Report(new("export", ticketCount, textCount, jsonBytes, $"Export pour Relais · {ticketCount:N0} fiches · {textCount:N0} fragments copiés."));
                        }
                    }
                    if (textCount == 0) throw new InvalidOperationException("Aucun texte admissible à exporter pour Relais. " + preview.Message);
                    if (ticketCount > preview.EligibleTicketCount || ticketCount > preview.SourceTicketCount)
                        throw new InvalidDataException("Les comptes de l’instantané sont incohérents. Aucun fichier n’a été publié.");
                    WriteRelaisManifest(target, transaction, preview, exportedAt, ticketCount, textCount);
                    ct.ThrowIfCancellationRequested(); transaction.Commit();
                }
                ct.ThrowIfCancellationRequested(); snapshot.Commit();
            }
            progress?.Report(new("validate", ticketCount, textCount, jsonBytes, "Vérification du fichier Relais complet…"));
            ct.ThrowIfCancellationRequested();
            var fileBytes = new FileInfo(localTemporary!).Length;
            if (fileBytes > limits.FileBytes) throw new InvalidDataException("Le fichier dépasse la taille admise par Relais. Aucun fichier partiel n’a été publié.");
            VerifyRelaisFile(localTemporary!, installation, ticketCount, textCount, ct);
            _ = ValidateRelaisDestination(full);
            progress?.Report(new("transfer", ticketCount, textCount, jsonBytes, "Enregistrement du fichier Relais vérifié…"));
            ct.ThrowIfCancellationRequested();
            var neighbor = Path.Combine(directory, ".glpi-relais-" + Guid.NewGuid().ToString("N") + ".tmp");
            string digest;
            using (var input = new FileStream(localTemporary!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            using (var output = new FileStream(neighbor, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                transferTemporary = neighbor;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536]; int read; long copied = 0;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    ct.ThrowIfCancellationRequested(); copied += read;
                    if (copied > limits.FileBytes) throw new InvalidDataException("Le fichier dépasse la taille admise par Relais.");
                    output.Write(buffer, 0, read); hash.AppendData(buffer, 0, read);
                }
                if (copied != fileBytes) throw new InvalidDataException("Le fichier Relais a changé pendant son transfert.");
                ct.ThrowIfCancellationRequested(); output.Flush(flushToDisk: true);
                digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            ct.ThrowIfCancellationRequested(); _ = ValidateRelaisDestination(full); ct.ThrowIfCancellationRequested();
            File.Move(transferTemporary!, full, overwrite: false); transferTemporary = null;
            return new RelaisExportResult(full, installation, exportedAt, ticketCount, textCount, preview.SourceTicketCount,
                preview.SourceTicketCount - ticketCount, preview.MissingContentCount, preview.IncompleteCount, preview.UnconfirmedCount,
                preview.PendingCount, preview.ConflictCount, fileBytes, digest);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 13)
        { throw new InvalidDataException("L’espace disponible ou la taille maximale du fichier Relais ne permet pas cet export. Aucun fichier partiel n’a été publié.", error); }
        finally
        {
            if (transferTemporary is not null) DeleteRelaisTemporary(transferTemporary);
            if (localTemporary is not null)
                foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" }) DeleteRelaisTemporary(localTemporary + suffix);
        }
    }, ct);

    private SqliteConnection OpenRelaisSource()
    {
        ValidateDatabasePath(DatabasePath);
        if (!File.Exists(DatabasePath)) throw new InvalidOperationException("La bibliothèque locale ne contient aucune fiche à exporter pour Relais.");
        var source = new SqliteConnection(new SqliteConnectionStringBuilder(ConnectionString) { Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { source.Open(); Execute(source, null, "PRAGMA query_only=ON;"); return source; }
        catch { source.Dispose(); throw; }
    }

    private static RelaisExportPreview ReadRelaisPreview(SqliteConnection source, SqliteTransaction snapshot, string installation, CancellationToken ct)
    {
        if (ScalarLong(source, snapshot, "SELECT version FROM ticket_library_schema WHERE singleton=1") != SchemaVersion)
            throw new InvalidDataException("Le contrat de bibliothèque n’est pas compatible avec l’export Relais.");
        using var query = Command(source, snapshot, $"""
            SELECT COUNT(*),
              COALESCE(SUM(CASE WHEN {RelaisEligibleSql}
              THEN 1 ELSE 0 END),0),
              COALESCE(SUM(COALESCE(has_content,0)<>1),0),
              COALESCE(SUM(COALESCE(detail_complete,0)<>1),0),
              COALESCE(SUM(COALESCE(content_confirmed,0)<>1),0),
              COALESCE(SUM(COALESCE(confirmation_pending,1)<>0),0),
              COALESCE(SUM(COALESCE(conflict_count,0)>0),0)
            FROM ticket_library_knowledge_v1_tickets
            WHERE installation=$installation AND version_id='selected' AND version_kind='selected'
            """, ("$installation", installation));
        using var reader = query.ExecuteReader(); reader.Read(); ct.ThrowIfCancellationRequested();
        return new(installation, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
    }

    private string ValidateRelaisDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || destination.Any(char.IsControl)) throw new InvalidDataException("Choisissez un nouveau fichier .relais.");
        var full = Path.GetFullPath(destination); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm", DatabasePath + "-journal" }.Any(path => string.Equals(full, path, comparison)))
            throw new InvalidDataException("L’export Relais ne peut pas remplacer la bibliothèque active ni ses journaux.");
        if (!full.EndsWith(".relais", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choisissez un fichier portant l’extension .relais.");
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try
            {
                _ = File.GetAttributes(full + suffix); // Includes dangling links, unlike File.Exists.
                throw new InvalidDataException("Ce fichier ou l’un de ses journaux existe déjà. Choisissez un nouveau nom pour l’export Relais.");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Le chemin d’export Relais ne peut pas utiliser une liaison de fichier ou de dossier."); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new IOException("Le dossier choisi pour l’export Relais est indisponible.");
        return full;
    }

    private static Dictionary<string, object?> ReadRelaisRow(SqliteDataReader reader)
    {
        var row = new Dictionary<string, object?>(RelaisExportFormat.TextColumns.Length, StringComparer.Ordinal);
        for (var i = 0; i < RelaisExportFormat.TextColumns.Length; i++)
        {
            var name = RelaisExportFormat.TextColumns[i]; var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            if (value is not null && RelaisExportFormat.IntegerColumns.Contains(name) && (value is not long integer || integer is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L))
                throw new InvalidDataException("Un indicateur de connaissance n’est pas un entier SQLite exact.");
            if (!RelaisExportFormat.IntegerColumns.Contains(name) && value is not null && value is not string)
            {
                if (name is "local_revision" or "central_revision" && value is long revision) value = revision.ToString(CultureInfo.InvariantCulture);
                else throw new InvalidDataException("Une métadonnée de connaissance n’est pas un texte exact.");
            }
            row.Add(name, value);
        }
        return row;
    }

    private static void ValidateRelaisRow(IReadOnlyDictionary<string, object?> row, string installation)
    {
        string TextValue(string name) => row[name] as string ?? "";
        bool FlagValue(string name, long expected) => row[name] is long actual && actual == expected;
        if (TextValue("installation") != installation || TextValue("version_id") != "selected" || TextValue("version_kind") != "selected"
            || TextValue("access_basis") != "collecting-session" || TextValue("text_visibility") is not ("unknown" or "private")
            || !FlagValue("detail_complete", 1) || !FlagValue("content_confirmed", 1) || !FlagValue("confirmation_pending", 0)
            || !FlagValue("has_content", 1) || !FlagValue("conflict_count", 0))
            throw new InvalidDataException("Un fragment n’est pas admissible selon le contrat Relais. Aucun fichier n’a été publié.");
        var ticket = new TicketReference(TextValue("ticket_id"), TextValue("ticket_url"));
        if (TextValue("ticket_key").Length is 0 or > 8192 || TextValue("ticket_key").Contains('\0') || ticket.Id.TrimStart('0').Length == 0
            || TicketLibraryRules.Key(ticket) != TextValue("ticket_key") || ObjectIdentity.Installation(ticket.Url) != installation)
            throw new InvalidDataException("L’identité d’un fragment Relais est incohérente.");
        foreach (var name in new[] { "local_revision", "central_revision" })
            if (row[name] is not null && (TextValue(name).Length is 0 or > 20 || !TextValue(name).All(char.IsAsciiDigit)))
                throw new InvalidDataException("Une révision de connaissance n’est pas un texte décimal exact.");
        var hash = TextValue("content_hash"); var path = TextValue("source_path");
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !path.StartsWith("/", StringComparison.Ordinal) || path.Length > 32768 || path.Contains('\0')
            || TextValue("body").Length == 0 || TextValue("citation_key") != TextValue("ticket_key") + "@" + hash + "/selected" + path)
            throw new InvalidDataException("La citation d’un fragment Relais est invalide.");
        ValidateRelaisUrl(ticket.Url); var sourceUrl = TextValue("source_url"); if (sourceUrl.Length > 0) ValidateRelaisUrl(sourceUrl);
    }

    private static void ValidateRelaisUrl(string value)
    {
        if (value.Length > 32768 || value.Any(c => c <= '\u0020' || c == '\u007f')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || uri.UserInfo.Length > 0)
            throw new InvalidDataException("Une URL de métadonnées Relais est invalide ou contient des identifiants intégrés.");
        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Uri.UnescapeDataString(parameter.Split('=', 2)[0].Replace('+', ' '));
            if (RelaisSensitiveParameter.IsMatch(name))
                throw new InvalidDataException("Une URL de métadonnées contient un paramètre d’authentification sensible. Aucun fichier Relais n’a été publié.");
        }
        if (uri.Fragment.Length > 0 && RelaisSensitiveParameter.IsMatch(Uri.UnescapeDataString(uri.Fragment)))
            throw new InvalidDataException("Une URL de métadonnées contient une référence sensible. Aucun fichier Relais n’a été publié.");
    }

    private static void ValidateRelaisInstallation(string installation)
    {
        LibraryBootstrapFormat.ValidateInstallation(installation);
        if (installation.Length > 2048 || installation.Any(c => c <= '\u0020' || c == '\u007f'))
            throw new InvalidDataException("L’installation GLPI ne respecte pas les bornes du format Relais.");
    }

    private static SqliteCommand RelaisInsert(SqliteConnection target, SqliteTransaction transaction, string table, IReadOnlyList<string> columns)
    {
        var command = Command(target, transaction, "INSERT INTO " + table + "(" + string.Join(',', columns) + ") VALUES(" + string.Join(',', columns.Select(name => "$" + name)) + ")");
        foreach (var name in columns) command.Parameters.Add("$" + name, RelaisExportFormat.IntegerColumns.Contains(name) ? SqliteType.Integer : SqliteType.Text);
        command.Prepare(); return command;
    }

    private static void BindRelaisRow(SqliteCommand command, IEnumerable<string> columns, IReadOnlyDictionary<string, object?> row)
    { foreach (var name in columns) command.Parameters["$" + name].Value = row[name] ?? DBNull.Value; }

    private static void WriteRelaisManifest(SqliteConnection target, SqliteTransaction transaction, RelaisExportPreview preview,
        DateTimeOffset exportedAt, long tickets, long texts)
        => Execute(target, transaction, """
            INSERT INTO relais_manifest VALUES(1,$protocol,1,1,$application,$at,$installation,'installation-admissible',1,
              $tickets,$texts,$source,$excluded,$missing,$incomplete,$unconfirmed,$pending,$conflicts)
            """, ("$protocol", RelaisExportFormat.Protocol), ("$application", AppPaths.ApplicationVersion), ("$at", exportedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$installation", preview.Installation), ("$tickets", tickets), ("$texts", texts), ("$source", preview.SourceTicketCount),
            ("$excluded", preview.SourceTicketCount - tickets), ("$missing", preview.MissingContentCount), ("$incomplete", preview.IncompleteCount),
            ("$unconfirmed", preview.UnconfirmedCount), ("$pending", preview.PendingCount), ("$conflicts", preview.ConflictCount));

    private static void VerifyRelaisFile(string path, string installation, long tickets, long texts, CancellationToken ct)
    {
        using var file = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        file.Open(); Execute(file, null, "PRAGMA query_only=ON;");
        using (var check = Command(file, null, "PRAGMA quick_check"))
            if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw new InvalidDataException("La vérification du fichier Relais a échoué.");
        using (var check = Command(file, null, "PRAGMA foreign_key_check"))
        using (var reader = check.ExecuteReader()) if (reader.Read()) throw new InvalidDataException("Les références du fichier Relais sont incohérentes.");
        if (ScalarLong(file, null, "SELECT COUNT(*) FROM relais_tickets") != tickets || ScalarLong(file, null, "SELECT COUNT(*) FROM relais_texts") != texts
            || ScalarLong(file, null, "SELECT COUNT(DISTINCT ticket_key) FROM relais_texts") != tickets
            || ScalarLong(file, null, "SELECT COUNT(*) FROM relais_manifest WHERE singleton=1 AND installation=$installation AND ticket_count=$tickets AND text_count=$texts AND complete=1",
                ("$installation", installation), ("$tickets", tickets), ("$texts", texts)) != 1)
            throw new InvalidDataException("Les décomptes du fichier Relais sont incohérents.");
        ct.ThrowIfCancellationRequested();
    }

    private static void DeleteRelaisTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { AppPaths.AppendLog("RELAIS · un fichier temporaire d’export n’a pas pu être retiré : " + AppPaths.SafeError(error)); }
    }
}
