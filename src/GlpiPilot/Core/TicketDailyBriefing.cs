namespace GlpiPilot.Core;

public sealed record PersonalDayObservation(string Key, string Id, string Title, string Status, string Due, bool Mine, int? Priority);
public sealed record PersonalBriefingLine(string TicketKey, string TicketId, string Kind, string Message);
public sealed record PersonalBriefingReport(string Summary, IReadOnlyList<PersonalBriefingLine> Lines);

public static class TicketDailyBriefing
{
    public static PersonalBriefingReport Build(IReadOnlyList<PersonalDayObservation> previous, string previousScope, DateTimeOffset? previousReadAt,
        IReadOnlyList<PersonalDayObservation> current, string currentScope, DateTimeOffset? previousSession, DateTimeOffset? currentReadAt, DateTimeOffset now, bool complete)
    {
        var rows = new List<PersonalBriefingLine>();
        var comparable = previousScope.Length > 0 && previousScope == currentScope && previousReadAt.HasValue && currentReadAt >= previousReadAt;
        var old = comparable ? previous.DistinctBy(item => item.Key).ToDictionary(item => item.Key, StringComparer.Ordinal) : new();
        foreach (var item in current.Take(5000))
        {
            if (item.Mine && item.Priority >= 4 && DashboardService.Status(item.Status) is not (5 or 6))
                rows.Add(new(item.Key, item.Id, "Priorité", "Priorité élevée lue · " + item.Title));
            if (TicketReadingService.TryDate(item.Due, out var due) && due < now.LocalDateTime && DashboardService.Status(item.Status) is not (5 or 6))
                rows.Add(new(item.Key, item.Id, "Échéance", "Échéance affichée dépassée : " + due.ToString("dd/MM/yyyy HH:mm") + " · état lu : " + (item.Status.Length > 0 ? item.Status : "inconnu")));
            if (!comparable) continue;
            if (!old.TryGetValue(item.Key, out var before))
            { if (item.Mine) rows.Add(new(item.Key, item.Id, "Apparition", "Nouvellement visible dans mon périmètre · " + item.Title + ". La date réelle d’attribution n’est pas connue.")); continue; }
            if (item.Mine && !before.Mine) rows.Add(new(item.Key, item.Id, "Affectation", "Présent désormais dans mon périmètre ; absent de mon périmètre lors du repère précédent."));
            if (before.Status != item.Status) rows.Add(new(item.Key, item.Id, "État modifié", before.Status + " → " + item.Status));
            if (before.Due != item.Due) rows.Add(new(item.Key, item.Id, "Échéance modifiée", (before.Due.Length > 0 ? before.Due : "non renseignée") + " → " + (item.Due.Length > 0 ? item.Due : "non renseignée")));
        }
        var summary = "Briefing de cette session" + (previousSession.HasValue ? " · session précédente ouverte le " + previousSession.Value.LocalDateTime.ToString("dd/MM/yyyy HH:mm") : " · premier repère")
            + ". " + (comparable ? $"Comparaison du repère observé le {previousReadAt!.Value.LocalDateTime:dd/MM/yyyy HH:mm} avec le relevé du {currentReadAt!.Value.LocalDateTime:dd/MM/yyyy HH:mm}." : "Aucun repère comparable dans ce périmètre ; les changements ne sont pas déduits.")
            + (!complete ? " Couverture partielle ou non confirmée." : "")
            + " Les absences ne prouvent aucune clôture. Les échéances utilisent les valeurs collectées ; relire les tickets anciens avant intervention."
            + (current.Count >= 5000 ? " Repère limité aux 5 000 premiers dossiers." : "");
        return new(summary, rows.Take(1000).ToArray());
    }
}
