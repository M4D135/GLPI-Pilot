using System.Globalization;
using System.Text;

namespace GlpiPilot.Core;

public sealed record TicketTaskInsight(string Title, string Owner, DateTime? Due, DateTime? Start, DateTime? End, bool? Completed, bool? Overdue, string Evidence) { public DateTimeOffset? ObservedAt { get; init; } }
public sealed record TicketValidationInsight(string Title, string Responsible, string Status, DateTime? RequestedAt, double? AgeDays, bool? Pending, string Evidence) { public DateTimeOffset? ObservedAt { get; init; } }
public sealed record TicketWaitingInsight(string Reason, DateTime? Start, DateTime? End, double? Hours, bool Ongoing, string Evidence) { public DateTimeOffset? ObservedAt { get; init; } }
public sealed record TicketExchangeInsight(string Author, string Role, DateTime? Date, string Content, string WaitingFor);
public sealed record TicketInsightReport(IReadOnlyList<TicketTaskInsight> Tasks, IReadOnlyList<TicketValidationInsight> Validations,
    IReadOnlyList<TicketWaitingInsight> Waits, TicketExchangeInsight? LastExchange, DateTimeOffset ReadAt, string Coverage)
{
    public int OverdueTasks => Tasks.Count(t => t.Overdue == true);
    public int PendingValidations => Validations.Count(v => v.Pending == true);
}

public static class TicketInsightService
{
    public static TicketInsightReport Analyze(TicketDetailResult detail, DateTimeOffset now)
    {
        var items = (detail.ObservedItems ?? new()).Concat(ObserveTables(detail)).GroupBy(item => (item.Kind, Identity: item.Identity.Length > 0 ? item.Identity : string.Join("|", item.Title, item.Date, item.Status, item.Due, item.PlannedStart, item.PlannedEnd, item.RequestedAt, item.WaitingStart, item.WaitingEnd)))
            .Select(group => group.OrderByDescending(item => item.ObservedAt ?? DateTimeOffset.MinValue).First()).ToArray();
        DateTime? Date(string value) => TicketReadingService.TryDate(value, out var parsed) ? parsed : null;
        var tasks = items.Where(i => i.Kind == "task").Select(i =>
        {
            var readAt = i.ObservedAt ?? detail.ReadAt;
            var completed = Completion(i.Status, i.CompletedAt);
            var due = Date(i.Due.Length > 0 ? i.Due : i.PlannedEnd);
            return new TicketTaskInsight(i.Title, i.Owner, due, Date(i.PlannedStart), Date(i.PlannedEnd), completed,
                due.HasValue && completed.HasValue ? due.Value < readAt.LocalDateTime && !completed.Value : null,
                i.SourceSection + " · état et dates explicitement affichés ; une date de création n’est jamais une échéance") { ObservedAt = readAt };
        }).ToArray();
        var validations = items.Where(i => i.Kind == "validation").Select(i =>
        {
            var readAt = i.ObservedAt ?? detail.ReadAt;
            var date = Date(i.RequestedAt);
            bool? pending = Key(i.Status) switch { "en attente" or "waiting" or "pending" or "demande" or "demandee" or "2" => true,
                "acceptee" or "accepted" or "refusee" or "refused" or "3" or "4" => false, _ => null };
            return new TicketValidationInsight(i.Title, i.Owner, i.Status, date,
                pending == true && date.HasValue && date <= readAt.LocalDateTime ? (readAt.LocalDateTime - date.Value).TotalDays : null,
                pending, i.SourceSection + " · ancienneté calculée à la date de lecture") { ObservedAt = readAt };
        }).ToArray();
        var waits = items.Where(i => i.WaitingStart.Length > 0 || i.WaitingReason.Length > 0).Select(i =>
        {
            var readAt = i.ObservedAt ?? detail.ReadAt;
            var start = Date(i.WaitingStart); var end = Date(i.WaitingEnd);
            var ongoing = Key(i.Status) is "en attente" or "waiting" or "pending";
            var until = end ?? (ongoing ? readAt.LocalDateTime : (DateTime?)null);
            return new TicketWaitingInsight(i.WaitingReason, start, end,
                start.HasValue && until.HasValue && until >= start ? (until.Value - start.Value).TotalHours : null, ongoing,
                "Période explicitement affichée · aucune durée déduite de la date de création du ticket") { ObservedAt = readAt };
        }).ToArray();
        var last = items.Where(i => i.Kind == "followup").Select(i => (Item: i, Date: Date(i.Date))).Where(x => x.Date.HasValue)
            .OrderByDescending(x => x.Date).FirstOrDefault();
        TicketExchangeInsight? exchange = last.Item == null ? null : new(last.Item.Author, Role(last.Item.AuthorRole), last.Date, last.Item.Title,
            Role(last.Item.AuthorRole) switch { "Demandeur" => "Réponse d’un intervenant non observée après cet échange", "Technicien" => "Réponse du demandeur non observée après cet échange", _ => "Origine de l’auteur non renseignée ; attente de réponse indéterminée" });
        return new(tasks, validations, waits, exchange, detail.ReadAt,
            $"{detail.Sections.Count} rubriques lues · {detail.Tabs.Count(t => !t.Loaded)} onglets non lus. Les compteurs portent sur les éléments visibles, à leur propre date de lecture. Sans décalage fourni par GLPI, les heures sont interprétées dans le fuseau du poste."
            + (now - detail.ReadAt > TimeSpan.FromHours(1) ? " Actualiser avant toute décision sur les échéances." : ""));
    }

    public static IReadOnlyList<string[]> Summary(TicketDetailResult detail)
    {
        var report = Analyze(detail, DateTimeOffset.Now); var rows = new List<string[]>();
        rows.Add(new[] { "Tâches avec échéance dépassée", $"{report.OverdueTasks} confirmées sur {report.Tasks.Count} lues ; {report.Tasks.Count(t => t.Overdue == null)} indéterminées" });
        var next = report.Tasks.Where(t => t.Completed != true && t.Start.HasValue && t.Start >= detail.ReadAt.LocalDateTime).OrderBy(t => t.Start).FirstOrDefault();
        rows.Add(new[] { "Prochaine tâche planifiée lue", next == null ? "Aucune prochaine date explicite collectée" : $"{next.Start:dd/MM/yyyy HH:mm} · {next.Title} · {next.Owner}" });
        rows.Add(new[] { "Validations en attente", $"{report.PendingValidations} confirmées ; {report.Validations.Count(v => v.Pending == null)} états indéterminés" });
        if (report.LastExchange is { } exchange) rows.Add(new[] { "Dernier échange daté et origine", $"{exchange.Date:dd/MM/yyyy HH:mm} · {exchange.Author} · {exchange.Role}\n{exchange.WaitingFor}" });
        foreach (var wait in report.Waits) rows.Add(new[] { "Mise en attente observée", $"{wait.Reason} · {wait.Start:dd/MM/yyyy HH:mm} → {(wait.End.HasValue ? wait.End.Value.ToString("dd/MM/yyyy HH:mm") : wait.Ongoing ? "date de lecture" : "fin inconnue")} · {(wait.Hours.HasValue ? wait.Hours.Value.ToString("N1") + " h" : "durée indéterminée")}" });
        return rows;
    }

    public static string DraftFollowup(PersonalTicket ticket, TicketDetailResult? detail, string template, string findings, string action, string nextStep)
    {
        var text = new StringBuilder();
        text.AppendLine(template).AppendLine("Ticket " + ticket.Id + " · " + ticket.Title).AppendLine();
        foreach (var part in new[] { ("Constat", findings), ("Action réalisée / prévue", action), ("Suite attendue", nextStep) })
            if (!string.IsNullOrWhiteSpace(part.Item2)) text.AppendLine(part.Item1).AppendLine(part.Item2.Trim()).AppendLine();
        if (detail != null) text.AppendLine("Contexte lu le " + detail.ReadAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm") + " · " + detail.Ticket.Url);
        return text.ToString().Trim();
    }

    private static IEnumerable<TicketObservedItem> ObserveTables(TicketDetailResult detail)
    {
        foreach (var section in detail.Sections.Where(section => section.Kind == "table"))
        {
            var name = Key(section.TabName + " " + section.Name);
            var headers = section.Data.Headers.Select(Key).ToArray();
            string Column(string[] row, params string[] labels)
            {
                var matching = headers.Select((label, index) => (label, index)).Where(pair => labels.Contains(pair.label, StringComparer.Ordinal)).ToArray();
                return matching.Length == 1 && matching[0].index < row.Length ? row[matching[0].index] : "";
            }
            var kind = name.Contains("validation", StringComparison.Ordinal) ? "validation" : name.Contains("tache", StringComparison.Ordinal) || name.Contains("task", StringComparison.Ordinal) ? "task"
                : name.Contains("attente", StringComparison.Ordinal) || name.Contains("waiting", StringComparison.Ordinal) ? "waiting" : "";
            if (kind.Length == 0) continue;
            foreach (var row in section.Data.Rows.Take(5000))
            {
                var item = new TicketObservedItem
                {
                    Kind = kind, SourceSection = section.Name, ObservedAt = section.Data.ExtractedAt, Identity = Column(row, "id", "identifiant"),
                    Title = Column(row, "titre", "title", "description", "contenu", "tache", "task", "commentaire"),
                    Owner = Column(row, "responsable", "assigne a", "assigned to", "validateur", "approbateur", "approver"),
                    Status = Column(row, "statut", "status", "etat"), Due = Column(row, "echeance", "date limite", "due date"),
                    PlannedStart = Column(row, "debut planifie", "date de debut", "planned start", "start date"), PlannedEnd = Column(row, "fin planifiee", "date de fin", "planned end", "end date"),
                    CompletedAt = Column(row, "termine le", "date achevement", "completed at"), RequestedAt = Column(row, "date de demande", "requested at"),
                    WaitingStart = Column(row, "debut de mise en attente", "waiting start"), WaitingEnd = Column(row, "fin de mise en attente", "waiting end"), WaitingReason = Column(row, "motif de mise en attente", "motif attente", "waiting reason")
                };
                if (item.Title.Length + item.Identity.Length + item.Status.Length + item.WaitingReason.Length > 0) yield return item;
            }
        }
    }

    private static bool? Completion(string status, string completedAt)
    {
        if (TicketReadingService.TryDate(completedAt, out _)) return true;
        return Key(status) switch { "fait" or "faite" or "terminee" or "termine" or "done" or "completed" or "2" => true,
            "a faire" or "en cours" or "todo" or "to do" or "not done" or "1" => false, _ => null };
    }
    private static string Role(string value) => Key(value) switch { "requester" or "demandeur" => "Demandeur", "technician" or "technicien" => "Technicien", _ => "Non déterminée" };
    private static string Key(string value) => TicketIntelligenceService.Normalize(value);
}
