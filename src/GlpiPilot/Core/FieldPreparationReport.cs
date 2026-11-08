using System.Globalization;
using System.Net;
using System.Text;

namespace GlpiPilot.Core;

public sealed record FieldTicketDetail(string Key, string Id, string Title, string Description = "",
    string Status = "", string Type = "", string Requester = "", DateTimeOffset? ObservedAt = null);
public sealed record FieldSheetContext(string PlanId, IReadOnlyList<FieldTicketDetail> Tickets,
    IReadOnlyList<string> Participants);

/// <summary>Printable HTML generated from a deliberate selection, with all dynamic values encoded.</summary>
public static class FieldPreparationReport
{
    public const int MaximumSheets = 200;
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string N(decimal value) => value.ToString("0.###", CultureInfo.GetCultureInfo("fr-FR"));
    private static string Date(DateTimeOffset? value, TimeZoneInfo zone) => value.HasValue
        ? TimeZoneInfo.ConvertTime(value.Value, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("fr-FR")) : "Non planifié";

    public static string Render(IEnumerable<FieldPreparationPlan> selected, IEnumerable<FieldSheetContext> contexts,
        string title, DateTimeOffset generatedAt, TimeZoneInfo zone, IReadOnlyCollection<string>? excludedDepartureIds = null)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (string.IsNullOrWhiteSpace(title) || title.Length > 300) throw new InvalidDataException("Titre des fiches invalide.");
        var plans = FieldPreparationService.DistinctPlans(selected, includeClosed: true);
        if (plans.Count is < 1 or > MaximumSheets) throw new InvalidDataException("Choisissez de 1 à 200 fiches à imprimer.");
        var contextRows = contexts.ToArray();
        if (contextRows.Length > MaximumSheets || contextRows.Any(value => value is null)
            || contextRows.Select(value => value.PlanId).Distinct(StringComparer.Ordinal).Count() != contextRows.Length)
            throw new InvalidDataException("Les détails des fiches sont répétés ou trop volumineux.");
        var selectedIds = plans.Select(plan => plan.Id).ToHashSet(StringComparer.Ordinal);
        if (contextRows.Any(value => !selectedIds.Contains(value.PlanId) || value.Tickets is null || value.Participants is null
            || value.Tickets.Count > 200 || value.Participants.Count > 200))
            throw new InvalidDataException("Un détail ne correspond pas à la sélection à imprimer.");
        foreach (var context in contextRows)
            foreach (var ticket in context.Tickets)
                if (ticket is null || ticket.Key is null || ticket.Id is null || ticket.Title is null || ticket.Description is null
                    || ticket.Key.Length > 300 || ticket.Id.Length > 100 || ticket.Title.Length > 2000 || ticket.Description.Length > 200000
                    || ticket.Requester?.Length > 2000 || ticket.Status?.Length > 300 || ticket.Type?.Length > 300)
                    throw new InvalidDataException("Détail de ticket absent ou trop volumineux pour la fiche.");
        var byPlan = contextRows.ToDictionary(value => value.PlanId, StringComparer.Ordinal);
        var summary = FieldPreparationService.Summarize(plans.Where(plan => excludedDepartureIds is null || !excludedDepartureIds.Contains(plan.Id)));
        var b = new StringBuilder();
        b.Append("<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src 'none'; base-uri 'none'; form-action 'none'\">")
            .Append("<title>").Append(E(title)).Append("</title><style>")
            .Append("@page{size:A4;margin:14mm}*{box-sizing:border-box}body{font:12px system-ui,sans-serif;color:#17202a;background:#fff;margin:20px auto;max-width:1000px;padding:0 16px}h1{font-size:24px}h2{font-size:19px}h3{font-size:14px}h1,h2,h3{break-after:avoid}p{line-height:1.5}.muted{color:#4d5966}.badge{display:inline-block;padding:5px 9px;border:1px solid #637587;border-radius:5px}table{width:100%;border-collapse:collapse;margin:12px 0;table-layout:fixed}th,td{border:1px solid #c8d0d9;padding:8px;text-align:left;vertical-align:top;overflow-wrap:anywhere}thead{display:table-header-group}tr{break-inside:avoid}pre,.text{font:inherit;white-space:pre-wrap;overflow-wrap:anywhere}.sheet{break-before:page;padding-top:16px}.ticket{margin:14px 0;padding:10px;border:1px solid #c8d0d9}.meta{display:grid;grid-template-columns:1fr 1fr;gap:8px}.notes{min-height:70px;border-bottom:1px solid #b4bfcb;margin-top:14px}.check{font-size:16px}.num{width:13%}.check-col{width:8%}@media print{body{margin:0;padding:0;max-width:none}.sheet{padding-top:0}.meta{display:block}.meta>div{margin-bottom:6px}}")
            .Append("</style></head><body><header><h1>").Append(E(title)).Append("</h1><p class=\"muted\">GLPI Pilot · préparé le ")
            .Append(E(Date(generatedAt, zone))).Append(" · ").Append(E(zone.Id)).Append("</p></header>")
            .Append("<p>Ces fiches décrivent les interventions sélectionnées et le matériel prévu. Elles ne certifient ni une disponibilité en stock ni la résolution des tickets GLPI.</p>")
            .Append("<h2>Liste de départ</h2><p>").Append(plans.Count).Append(" fiche(s) · ").Append(summary.PlanCount)
            .Append(" intervention(s) prévues à préparer. Les interventions terminées ou annulées restent imprimables mais sont exclues de cette liste de départ.</p>");
        if (summary.UnspecifiedCount > 0) b.Append("<p class=\"badge\">").Append(summary.UnspecifiedCount).Append(" intervention(s) : matériel encore à préciser.</p>");
        Materials(b, summary.Materials);
        foreach (var warning in summary.Warnings) b.Append("<p>").Append(E(warning)).Append("</p>");
        int page = 0;
        foreach (var plan in plans)
        {
            byPlan.TryGetValue(plan.Id, out var context);
            b.Append("<article class=\"sheet\"><header><p class=\"muted\">Fiche ").Append(++page).Append(" / ").Append(plans.Count)
                .Append(" · ").Append(plan.Owner.Kind == "ticket" ? "Ticket" : "Intervention manuelle")
                .Append("</p><h2>").Append(E(plan.Title)).Append("</h2><p class=\"badge\">")
                .Append(E(FieldPreparationService.ReadinessLabel(FieldPreparationService.Readiness(plan)))).Append("</p></header>")
                .Append("<div class=\"meta\"><div><strong>Site :</strong> ").Append(E(plan.SiteName.Length > 0 ? plan.SiteName : "À préciser"))
                .Append("<br><strong>Adresse :</strong> ").Append(E(plan.Address.Length > 0 ? plan.Address : "À préciser"))
                .Append("<br><strong>Contact :</strong> ").Append(E(plan.Contact.Length > 0 ? plan.Contact : "Non renseigné"))
                .Append("</div><div><strong>Début :</strong> ").Append(E(Date(plan.PlannedStart, zone))).Append("<br><strong>Fin :</strong> ")
                .Append(E(Date(plan.PlannedEnd, zone))).Append("<br><strong>Techniciens :</strong> ")
                .Append(E(context?.Participants.Count > 0 ? string.Join(", ", context.Participants) : plan.ParticipantKeys.Count > 0 ? "Libellés non disponibles" : "Non affecté"))
                .Append("</div></div>");
            if (context?.Tickets.Count > 0)
            {
                foreach (var ticket in context.Tickets)
                {
                    b.Append("<section class=\"ticket\"><h3>Ticket ").Append(E(ticket.Id)).Append(" · ").Append(E(ticket.Title)).Append("</h3>")
                        .Append("<p>Type : ").Append(E(string.IsNullOrEmpty(ticket.Type) ? "Non connu" : ticket.Type)).Append(" · Statut observé : ")
                        .Append(E(string.IsNullOrEmpty(ticket.Status) ? "Non connu" : ticket.Status)).Append("<br>Demandeur : ")
                        .Append(E(string.IsNullOrEmpty(ticket.Requester) ? "Non disponible" : ticket.Requester)).Append("<br>Observation : ")
                        .Append(E(ticket.ObservedAt.HasValue ? Date(ticket.ObservedAt, zone) : "Date non fournie")).Append("</p><div class=\"text\">")
                        .Append(E(string.IsNullOrWhiteSpace(ticket.Description) ? "Description détaillée non disponible dans la source fournie." : ticket.Description)).Append("</div></section>");
                }
            }
            else b.Append("<p class=\"muted\">").Append(plan.Owner.Kind == "ticket" ? "Le détail du ticket n’a pas été fourni pour cette fiche." : "Intervention manuelle : aucun ticket lié fourni.").Append("</p>");
            b.Append("<h3>Matériel lié à cette intervention</h3>");
            if (plan.NoMaterialsRequired) b.Append("<p>Aucun matériel nécessaire, indiqué explicitement.</p>");
            else if (plan.Lines.Count == 0) b.Append("<p>Matériel à préciser. L’absence de liste ne signifie pas que rien n’est nécessaire.</p>");
            else
            {
                b.Append("<table><thead><tr><th class=\"check-col\">Pris</th><th>Référence / matériel</th><th class=\"num\">Prévu</th><th class=\"num\">Prêt</th><th>Notes / préparé par</th></tr></thead><tbody>");
                foreach (var line in plan.Lines)
                    b.Append("<tr><td class=\"check\">☐</td><td>").Append(E(line.Label)).Append("<br><span class=\"muted\">").Append(E(line.Code))
                        .Append("</span></td><td>").Append(N(line.Quantity)).Append(' ').Append(E(FieldPreparationService.UnitLabel(line.Unit)))
                        .Append("</td><td>").Append(N(line.PreparedQuantity)).Append("</td><td><div class=\"text\">").Append(E(line.Note))
                        .Append("</div>").Append(E(line.PreparedBy)).Append("</td></tr>");
                b.Append("</tbody></table>");
            }
            if (plan.PreparationNote.Length > 0) b.Append("<h3>Consignes de préparation</h3><div class=\"text\">").Append(E(plan.PreparationNote)).Append("</div>");
            b.Append("<h3>Compte rendu terrain</h3><p>☐ Réalisée &nbsp; ☐ Partielle &nbsp; ☐ À replanifier</p><div class=\"notes\"></div>")
                .Append("<p>Actions réalisées / suite à donner : __________________________________________</p><p>Technicien(s) : ________________________ &nbsp; Date : ______________</p></article>");
        }
        b.Append("</body></html>");
        if (b.Length > 16 * 1024 * 1024) throw new InvalidDataException("Les fiches sont trop volumineuses ; réduisez la sélection.");
        return b.ToString();
    }
    private static void Materials(StringBuilder b, IReadOnlyList<FieldPreparationTotal> rows)
    {
        if (rows.Count == 0) { b.Append("<p>Aucune quantité de matériel renseignée dans les interventions prévues sélectionnées.</p>"); return; }
        b.Append("<table><thead><tr><th class=\"check-col\">Pris</th><th>Matériel / référence</th><th class=\"num\">Prévu</th><th class=\"num\">Prêt</th><th class=\"num\">À préparer</th></tr></thead><tbody>");
        foreach (var row in rows)
            b.Append("<tr><td class=\"check\">☐</td><td>").Append(E(row.Label)).Append("<br><span class=\"muted\">")
                .Append(E(row.Code)).Append(" · ").Append(E(row.Category)).Append(" · ").Append(row.PlanIds.Count).Append(" intervention(s)</span></td><td>")
                .Append(N(row.Quantity)).Append(' ').Append(E(FieldPreparationService.UnitLabel(row.Unit))).Append("</td><td>")
                .Append(N(row.PreparedQuantity)).Append("</td><td>").Append(N(row.RemainingQuantity)).Append("</td></tr>");
        b.Append("</tbody></table>");
    }
}
