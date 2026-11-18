using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

public sealed partial class TicketLibraryStore
{
    public Task<TicketLibraryDocument?> ReadCachedTicketAsync(TicketReference ticket, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); ValidateDatabasePath(DatabasePath);
        var connectionString = new SqliteConnectionStringBuilder(ConnectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var connection = new SqliteConnection(connectionString); connection.Open();
        if (!HasReadableCacheSchema(connection)) return null;
        return Read(connection, null, TicketLibraryRules.Key(ticket));
    }, ct);

    /// <summary>One bounded read-only metadata query; ticket bodies are not materialized for list badges.</summary>
    public Task<IReadOnlyDictionary<string, TicketReadCacheEntry>> ReadCacheMetadataAsync(IEnumerable<TicketReference> tickets, CancellationToken ct = default)
    {
        var keys = tickets.DistinctBy(TicketLibraryRules.Key).ToDictionary(TicketLibraryRules.Key, WorkplaceStore.TicketKey, StringComparer.Ordinal);
        if (keys.Count > 500) throw new InvalidOperationException("Les métadonnées du cache sont limitées à 500 tickets par page.");
        return Task.Run<IReadOnlyDictionary<string, TicketReadCacheEntry>>(() =>
        {
            var result = new Dictionary<string, TicketReadCacheEntry>(StringComparer.Ordinal);
            if (keys.Count == 0) return result;
            ct.ThrowIfCancellationRequested(); ValidateDatabasePath(DatabasePath);
            var connectionString = new SqliteConnectionStringBuilder(ConnectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var connection = new SqliteConnection(connectionString); connection.Open();
            if (!HasReadableCacheSchema(connection)) return result;
            using var command = connection.CreateCommand();
            var parameters = keys.Keys.Select((key, index) => (Name: "$key" + index, Key: key)).ToArray();
            command.CommandText = """
                SELECT ticket_key, has_content, details_current,
                    json_extract(document_json,'$.readAt'), json_extract(document_json,'$.detailModifiedRaw'),
                    json_extract(document_json,'$.detailModifiedAt'), json_extract(document_json,'$.seed.lastModifiedReliable'),
                    json_extract(document_json,'$.seed.lastModifiedPrecision'), json_extract(document_json,'$.seed.observedAt'),
                    json_extract(document_json,'$.confirmationPending'), json_extract(document_json,'$.confirmationId'),
                    json_extract(document_json,'$.confirmedContentHash'), content_hash,
                    (json_array_length(json_extract(document_json,'$.detail.sections')) > 0
                        AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(document_json,'$.detail.sections')) s
                            WHERE COALESCE(json_extract(s.value,'$.data.complete'),0) != 1))
                FROM ticket_library_tickets WHERE ticket_key IN (
                """ + string.Join(",", parameters.Select(parameter => parameter.Name)) + ")";
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Key);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                string TextAt(int index) => reader.IsDBNull(index) ? "" : reader.GetString(index);
                DateTimeOffset? DateAt(int index) => DateTimeOffset.TryParse(TextAt(index), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
                bool FlagAt(int index) => !reader.IsDBNull(index) && reader.GetBoolean(index);
                var key = keys[reader.GetString(0)]; var hash = TextAt(12);
                var confirmed = FlagAt(1) && FlagAt(2) && !FlagAt(9) && Guid.TryParseExact(TextAt(10), "N", out _)
                    && hash.Length == 64 && TextAt(11) == hash;
                result[key] = new()
                {
                    HasContent = FlagAt(1), ReadAt = DateAt(3), FromLibrary = true,
                    Version = new()
                    {
                        TicketKey = key, ModifiedRaw = TextAt(4), ModifiedAt = DateAt(5), Reliable = FlagAt(6) && confirmed,
                        Precision = TextAt(7), ObservedAt = DateAt(8) ?? default, MainReadComplete = confirmed && FlagAt(13)
                    }
                };
            }
            return result;
        }, ct);
    }

    private static bool HasReadableCacheSchema(SqliteConnection connection)
    {
        // Calendar/team storage can create this shared file before the library
        // has ever been used. A badge must not initialize or migrate its schema.
        var anyLibraryObject = false;
        var tables = new HashSet<string>(StringComparer.Ordinal);
        using (var objects = connection.CreateCommand())
        {
            objects.CommandText = "SELECT lower(name),type FROM sqlite_master WHERE lower(name) GLOB 'ticket_library_*'";
            using var reader = objects.ExecuteReader();
            while (reader.Read())
            {
                anyLibraryObject = true;
                if (reader.GetString(1) == "table") tables.Add(reader.GetString(0));
            }
        }
        if (!anyLibraryObject) return false;
        if (new[] { "ticket_library_schema", "ticket_library_tickets", "ticket_library_outbox", "ticket_library_conflicts" }.Any(name => !tables.Contains(name)))
            throw new InvalidDataException("Le schéma du cache de tickets est incomplet. La base est conservée.");
        using (var version = connection.CreateCommand())
        {
            version.CommandText = """
                SELECT COUNT(*)=1 AND COALESCE(SUM(singleton=1 AND version=$version
                    AND typeof(singleton)='integer' AND typeof(version)='integer'),0)=1
                FROM ticket_library_schema
                """;
            version.Parameters.AddWithValue("$version", SchemaVersion);
            if (version.ExecuteScalar() is not long valid || valid != 1)
                throw new InvalidDataException("Version du cache de tickets non prise en charge. La base est conservée.");
        }
        // Validate the columns needed by both readers without loading bodies.
        // Partial or damaged library schemas must remain unknown, never absent.
        using var columns = connection.CreateCommand();
        columns.CommandText = """
            SELECT t.ticket_key,t.document_json,t.local_revision,t.central_revision,t.has_content,t.details_current,t.content_hash,
                o.ticket_key,c.ticket_key
            FROM ticket_library_tickets t,ticket_library_outbox o,ticket_library_conflicts c LIMIT 0
            """;
        using var checkedColumns = columns.ExecuteReader();
        return true;
    }
}
