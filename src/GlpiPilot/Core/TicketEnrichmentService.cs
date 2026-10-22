using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record TicketEnrichmentOutcome(TicketReference Ticket, string State, DateTimeOffset? ReadAt, string ObservedStatus, int Sections, string Message);
public sealed class TicketEnrichmentQueue
{
    public string Label { get; set; } = "";
    public int Limit { get; set; } = 20;
    public List<TicketReference> Tickets { get; set; } = new();
    public List<TicketEnrichmentOutcome> Results { get; set; } = new();
}
public static class TicketEnrichmentService
{
    public const int MaximumCandidates = 1000;
    public const int MaximumReads = 100;
    public static IReadOnlyList<TicketReference> Plan(IEnumerable<TicketReference> tickets, int limit, string priorityKey = "")
    {
        if (limit is < 1 or > MaximumReads) throw new InvalidDataException("La limite globale doit être comprise entre 1 et 100 fiches.");
        return tickets.Take(MaximumCandidates).Select(ValidateReference).DistinctBy(WorkplaceStore.TicketKey)
            .OrderBy(ticket => WorkplaceStore.TicketKey(ticket) == priorityKey ? 0 : 1).Take(limit).ToArray();
    }
    public static TicketReference ValidateReference(TicketReference ticket)
    {
        _ = WorkplaceStore.TicketKey(ticket);
        if (!ticket.Id.All(char.IsAsciiDigit) || ticket.Id.Length > 20 || ticket.Id == "0") throw new InvalidDataException("Une référence de la file n’a pas d’identifiant numérique positif.");
        var url = new Uri(ticket.Url);
        if (!url.AbsolutePath.EndsWith("/front/ticket.form.php", StringComparison.Ordinal) || url.Fragment.Length > 0)
            throw new InvalidDataException("Une référence de la file n’est pas une fiche ticket GLPI.");
        return ticket;
    }
    public static string ObservedStatus(TicketDetailResult detail)
    {
        var fields = detail.Sections.Where(section => section.Kind == "fields").SelectMany(section => section.Data.Rows)
            .Where(row => row.Length >= 2 && TicketIntelligenceService.Normalize(row[0]) is "statut" or "status" or "etat")
            .Select(row => row[1]).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return fields.Length == 1 ? fields[0] : fields.Length == 0 ? "Non collecté" : "Champs contradictoires ; vérifier GLPI";
    }
    public static async Task<IReadOnlyList<TicketEnrichmentOutcome>> ExecuteAsync(IEnumerable<TicketReference> planned, Func<TicketReference, CancellationToken, Task<TicketDetailResult>> read,
        Action<TicketDetailResult>? accepted = null, IProgress<TicketEnrichmentOutcome>? progress = null, CancellationToken ct = default)
    {
        var tickets = planned.Take(MaximumReads).ToArray(); var results = new List<TicketEnrichmentOutcome>();
        foreach (var ticket in tickets)
        {
            if (ct.IsCancellationRequested) break;
            TicketEnrichmentOutcome result;
            try
            {
                var detail = await read(ticket, ct); ct.ThrowIfCancellationRequested();
                if (detail.Ticket != ticket) throw new InvalidDataException("La lecture ne correspond pas au ticket demandé.");
                accepted?.Invoke(detail);
                result = new(ticket, "Lu", detail.ReadAt, ObservedStatus(detail), detail.Sections.Count, "État observé lors de cette lecture ; aucune modification du relevé ou des archives.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { result = new(ticket, "Échec", null, "Inconnu", 0, error is InvalidOperationException or InvalidDataException or TimeoutException ? error.Message : "La fiche n’a pas pu être lue. Vérifiez la session GLPI."); }
            results.Add(result); progress?.Report(result);
        }
        foreach (var ticket in tickets.Skip(results.Count))
        { var result = new TicketEnrichmentOutcome(ticket, "Non lu", null, "Inconnu", 0, "File interrompue ; aucune conclusion sur l’état du ticket."); results.Add(result); progress?.Report(result); }
        return results;
    }
    public static TicketEnrichmentQueue Load(string path)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("La file conservée dépasse 4 Mo.");
        return Validate(JsonSerializer.Deserialize<TicketEnrichmentQueue>(File.ReadAllText(path)) ?? new());
    }
    public static void Save(string path, TicketEnrichmentQueue queue)
    {
        _ = Validate(queue); var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(queue)); File.Move(temp, full, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static TicketEnrichmentQueue Validate(TicketEnrichmentQueue queue)
    {
        if (queue.Label == null || queue.Label.Length > 1000 || queue.Tickets == null || queue.Results == null || queue.Tickets.Count > MaximumCandidates || queue.Results.Count > MaximumCandidates || queue.Limit is < 1 or > MaximumReads)
            throw new InvalidDataException("La file d’enrichissement est invalide.");
        foreach (var ticket in queue.Tickets) _ = ValidateReference(ticket);
        foreach (var result in queue.Results)
        {
            if (result == null || result.Message == null || result.Message.Length > 10000 || result.State is not ("Lu" or "Échec" or "Non lu") || result.ObservedStatus == null || result.ObservedStatus.Length > 1000)
                throw new InvalidDataException("Un résultat de la file est invalide.");
            _ = ValidateReference(result.Ticket);
        }
        return queue;
    }
}
