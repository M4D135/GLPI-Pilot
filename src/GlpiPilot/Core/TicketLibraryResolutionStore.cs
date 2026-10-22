using System.Text.Json;

namespace GlpiPilot.Core;

public sealed partial class TicketLibraryStore
{
    // A second, short snapshot prevents a now-obsolete preliminary hit from being
    // presented as a confirmed solution. No body/chunk collection leaves this method.
    internal Task<TicketLibraryResolutionEvidence?> ReadResolutionEvidenceAsync(string key, string text, CancellationToken ct)
        => Run<TicketLibraryResolutionEvidence?>(connection =>
        {
            ct.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction(deferred: true);
            var document = Read(connection, transaction, key);
            if (!TicketLibraryResolutionService.Eligible(document)) return null;
            var owner = document!;
            var citations = new List<TicketLibraryCitation>();
            void ReadCitations(string sql, params (string Name, object? Value)[] parameters)
            {
                using var command = Command(connection, transaction, sql, parameters);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var json = reader.GetString(0);
                    if (json.Length > 65536) throw new InvalidDataException("Un extrait de bibliothèque dépasse la limite autorisée.");
                    var citation = JsonSerializer.Deserialize<TicketLibraryCitation>(json, TicketLibraryRules.Json)
                        ?? throw new InvalidDataException("Un extrait de bibliothèque est vide.");
                    if (citation.Key != owner.Key || citation.ContentHash != owner.ContentHash
                        || citation.Ticket != owner.Seed.Ticket || citation.Url != owner.Seed.Ticket.Url
                        || citation.Excerpt.Length > TicketLibrarySearch.ChunkCharacters || citation.SectionKey == "index") continue;
                    citation.Current = true;
                    if (citations.All(item => item.ChunkKey != citation.ChunkKey)) citations.Add(citation);
                }
            }
            ReadCitations("SELECT citation_json FROM ticket_library_chunks WHERE ticket_key=$key AND json_extract(citation_json,'$.isSolution')=1 ORDER BY chunk_id LIMIT 2", ("$key", key));
            var match = TicketLibrarySearch.MatchQuery(text);
            if (match.Length > 0)
                ReadCitations("SELECT c.citation_json FROM ticket_library_fts JOIN ticket_library_chunks c ON c.chunk_id=ticket_library_fts.rowid WHERE ticket_library_fts MATCH $match AND c.ticket_key=$key AND json_extract(c.citation_json,'$.sectionKey')<>'index' ORDER BY bm25(ticket_library_fts),c.chunk_id LIMIT 2", ("$key", key), ("$match", match));
            transaction.Commit();
            return new(new()
            {
                Key = owner.Key, Ticket = owner.Seed.Ticket, Installation = owner.Installation,
                Title = owner.Seed.Title, Status = owner.Seed.Status, StatusCode = owner.Seed.StatusCode,
                Category = owner.Seed.Category, ReadAt = owner.ReadAt, DetailsCurrent = true, DetailComplete = true
            }, citations);
        }, ct);
}
