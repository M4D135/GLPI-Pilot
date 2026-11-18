using System.Text;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

public sealed partial class TicketLibraryStore
{
    private const int KnowledgeBackfillTickets = 16;
    private const int KnowledgeBackfillBytes = 8 * 1024 * 1024;
    private const string KnowledgeIndexSchemaSql = """
        CREATE TABLE IF NOT EXISTS ticket_library_knowledge_index (
          ticket_key TEXT PRIMARY KEY REFERENCES ticket_library_tickets(ticket_key) ON DELETE CASCADE,
          content_hash TEXT NOT NULL,version INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ticket_library_knowledge_history_key ON ticket_library_history(ticket_key,history_id);
        CREATE TRIGGER IF NOT EXISTS ticket_library_knowledge_chunks_ai AFTER INSERT ON ticket_library_chunks BEGIN
          DELETE FROM ticket_library_knowledge_index WHERE ticket_key=new.ticket_key; END;
        CREATE TRIGGER IF NOT EXISTS ticket_library_knowledge_chunks_ad AFTER DELETE ON ticket_library_chunks BEGIN
          DELETE FROM ticket_library_knowledge_index WHERE ticket_key=old.ticket_key; END;
        """;

    internal static void InitializeKnowledge(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, KnowledgeIndexSchemaSql);
        Execute(connection, transaction, TicketLibraryKnowledge.SqliteSchemaSql);
    }

    // Each batch commits only derived chunks and its completion markers. Source
    // documents, proofs, revisions, conflicts, outboxes and cursors are untouched.
    // A cancelled upgrade resumes at the first unmarked ticket on its next open.
    internal static int BackfillKnowledgeBatch(SqliteConnection connection, CancellationToken ct, int limit = KnowledgeBackfillTickets)
        => BackfillKnowledgeBatch(connection, ct, "", out _, limit);

    private static int BackfillKnowledgeBatch(SqliteConnection connection, CancellationToken ct, string afterKey, out string lastKey, int limit = KnowledgeBackfillTickets)
    {
        lastKey = afterKey;
        ct.ThrowIfCancellationRequested();
        using var tx = connection.BeginTransaction();
        var documents = new List<(string Key, string Json)>(); var bytes = 0;
        using (var command = Command(connection, tx, """
            SELECT t.ticket_key,t.document_json FROM ticket_library_tickets t
              LEFT JOIN ticket_library_knowledge_index k ON k.ticket_key=t.ticket_key
              WHERE t.ticket_key>$after AND (k.ticket_key IS NULL OR k.version<>$version OR k.content_hash<>t.content_hash)
              ORDER BY t.ticket_key LIMIT $limit
            """, ("$after", afterKey), ("$version", TicketLibraryKnowledge.Version), ("$limit", Math.Clamp(limit, 1, KnowledgeBackfillTickets))))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested(); var json = reader.GetString(1); var nextBytes = Encoding.UTF8.GetByteCount(json);
                if (documents.Count > 0 && bytes + nextBytes > KnowledgeBackfillBytes) break;
                documents.Add((reader.GetString(0), json)); bytes += nextBytes;
            }
        }
        foreach (var entry in documents)
        {
            ct.ThrowIfCancellationRequested(); var document = TicketLibraryRules.Deserialize(entry.Json);
            RebuildKnowledgeChunks(connection, tx, document, ct);
        }
        ct.ThrowIfCancellationRequested(); tx.Commit();
        if (documents.Count > 0) lastKey = documents[^1].Key;
        return documents.Count;
    }

    internal static void RebuildKnowledgeChunks(SqliteConnection connection, SqliteTransaction tx, TicketLibraryDocument document, CancellationToken ct)
    {
        Execute(connection, tx, "DELETE FROM ticket_library_chunks WHERE ticket_key=$key", ("$key", document.Key));
        using var chunk = Command(connection, tx, "INSERT INTO ticket_library_chunks(ticket_key,citation_json,body) VALUES($key,$json,$body)", ("$key", document.Key), ("$json", ""), ("$body", ""));
        foreach (var citation in TicketLibrarySearch.Chunks(document))
        {
            ct.ThrowIfCancellationRequested(); chunk.Parameters["$json"].Value = TicketLibraryRules.Serialize(citation); chunk.Parameters["$body"].Value = citation.Excerpt; chunk.ExecuteNonQuery();
        }
        Execute(connection, tx, """
            INSERT INTO ticket_library_knowledge_index(ticket_key,content_hash,version) VALUES($key,$hash,$version)
            ON CONFLICT(ticket_key) DO UPDATE SET content_hash=excluded.content_hash,version=excluded.version
            """, ("$key", document.Key), ("$hash", document.ContentHash), ("$version", TicketLibraryKnowledge.Version));
    }
}
