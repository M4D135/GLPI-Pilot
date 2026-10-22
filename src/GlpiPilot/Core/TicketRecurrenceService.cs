namespace GlpiPilot.Core;

public sealed record TicketRecurrenceSource(ExtractionResult Data, DashboardSettings Settings, string Label);
public sealed record TicketRecurrenceIncident(TicketReference Ticket, string Title, string Equipment, string Category, DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen, int Observations, DateTime? CreatedAt, string Evidence)
{
    public string Id => Ticket.Id;
}
public sealed record TicketRecurrenceReport(string Summary, IReadOnlyList<TicketRecurrenceIncident> Incidents, int Sources, int DistinctTickets, int RepeatedObservations);

/// <summary>Counts distinct ticket identities across dated receipts. Repeated captures of one ticket never become extra incidents.</summary>
public static class TicketRecurrenceService
{
    public const int MaximumSources = 20;
    public const int MaximumRows = 500000;
    public static TicketRecurrenceReport Analyze(TicketReference target, IEnumerable<TicketRecurrenceSource> sources, string equipmentColumn = "", CancellationToken ct = default)
    {
        var incidents = new Dictionary<string, TicketRecurrenceIncident>(StringComparer.Ordinal);
        var totalRows = 0; var acceptedSources = 0; var skippedSources = 0; var ambiguous = 0; var partial = 0; var observations = 0;
        DateTimeOffset? first = null, last = null;
        var installation = new Uri(TicketActionService.Installation(target));
        foreach (var source in sources.Take(MaximumSources))
        {
            ct.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(source.Data.SourceUrl, UriKind.Absolute, out var address) || address.GetLeftPart(UriPartial.Authority) != installation.GetLeftPart(UriPartial.Authority)
                || !address.AbsolutePath.StartsWith(installation.AbsolutePath + "front/", StringComparison.Ordinal)) { skippedSources++; continue; }
            var columns = DashboardService.ResolveColumns(source.Data.Headers, source.Settings);
            int Column(string role) => columns.FirstOrDefault(column => column.Role == role)?.Index ?? -1;
            var idColumn = Column("id"); var titleColumn = Column("title"); var categoryColumn = Column("category"); var createdColumn = Column("created");
            if (idColumn < 0 || titleColumn < 0) { skippedSources++; continue; }
            var equipment = source.Data.Headers.Select((name, index) => (name, index)).Where(item => equipmentColumn.Length > 0 ? item.name == equipmentColumn : TicketIntelligenceService.Normalize(item.name) is "equipement" or "equipment" or "materiel" or "asset" or "ordinateur" or "computer").ToArray();
            var equipmentIndex = equipment.Length == 1 ? equipment[0].index : -1;
            string Cell(string[] row, int column) => column >= 0 && column < row.Length ? row[column] ?? "" : "";
            string Interpreted(string[] row, int column, string role) => column >= 0 && column < source.Data.Headers.Count
                ? DashboardService.NormalizeValue(Cell(row, column), role, source.Data.Headers[column], source.Settings) : "";
            if ((long)totalRows + source.Data.Rows.Count > MaximumRows) { skippedSources++; continue; }
            totalRows += source.Data.Rows.Count; acceptedSources++; if (!source.Data.Complete) partial++;
            first = !first.HasValue || source.Data.ExtractedAt < first ? source.Data.ExtractedAt : first;
            last = !last.HasValue || source.Data.ExtractedAt > last ? source.Data.ExtractedAt : last;
            var indexed = source.Data.Rows.Select(row => (Row: row, Id: NormalizeId(Cell(row, idColumn)))).Where(item => item.Id.Length > 0).GroupBy(item => item.Id, StringComparer.Ordinal);
            foreach (var group in indexed)
            {
                if ((observations & 127) == 0) ct.ThrowIfCancellationRequested();
                if (group.Count() != 1) { ambiguous++; continue; }
                observations++; var row = group.First().Row;
                var created = DashboardService.TrySourceDate(Interpreted(row, createdColumn, "created"), source.Settings, source.Data.ExtractedAt.Offset, out var date)
                    ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), source.Data.ExtractedAt.Offset).LocalDateTime : (DateTime?)null;
                var incident = new TicketRecurrenceIncident(new(group.Key, new UriBuilder(target.Url) { Query = "id=" + group.Key, Fragment = "" }.Uri.AbsoluteUri),
                    Interpreted(row, titleColumn, "title"), Interpreted(row, equipmentIndex, "equipment"), Interpreted(row, categoryColumn, "category"), source.Data.ExtractedAt, source.Data.ExtractedAt, 1, created, source.Label);
                if (incidents.TryGetValue(group.Key, out var previous))
                {
                    var latest = source.Data.ExtractedAt >= previous.LastSeen ? incident : previous;
                    incident = latest with { FirstSeen = source.Data.ExtractedAt < previous.FirstSeen ? source.Data.ExtractedAt : previous.FirstSeen,
                        LastSeen = source.Data.ExtractedAt > previous.LastSeen ? source.Data.ExtractedAt : previous.LastSeen, Observations = previous.Observations + 1,
                        CreatedAt = latest.CreatedAt ?? previous.CreatedAt };
                }
                incidents[group.Key] = incident;
            }
        }
        if (!incidents.TryGetValue(target.Id, out var targetIncident)) return new("Le ticket courant n’a pas d’identité unique dans ces relevés ; aucune fréquence n’est déduite.", Array.Empty<TicketRecurrenceIncident>(), acceptedSources, 0, 0);
        var targetEquipment = TicketIntelligenceService.Normalize(targetIncident.Equipment); var targetTitle = TicketIntelligenceService.Normalize(targetIncident.Title); var targetCategory = TicketIntelligenceService.Normalize(targetIncident.Category);
        var related = incidents.Values.Where(item =>
        {
            if (item.Id == target.Id) return true;
            var sameEquipment = targetEquipment.Length > 0 && TicketIntelligenceService.Normalize(item.Equipment) == targetEquipment;
            var sameTitle = targetTitle.Length > 3 && TicketIntelligenceService.Normalize(item.Title) == targetTitle;
            var category = TicketIntelligenceService.Normalize(item.Category);
            return (sameEquipment || sameTitle) && (targetCategory.Length == 0 || category.Length == 0 || targetCategory == category);
        }).OrderByDescending(item => item.LastSeen).Select(item => item with { Evidence =
            (targetEquipment.Length > 0 && TicketIntelligenceService.Normalize(item.Equipment) == targetEquipment ? "Même équipement explicitement collecté" : "Même titre normalisé")
            + (targetCategory.Length > 0 && TicketIntelligenceService.Normalize(item.Category) == targetCategory ? " · même catégorie" : " · catégorie non concordante ou absente")
            + $" · {item.Observations} observation(s) ; présence répétée d’un même ID comptée une seule fois. Dernière source : {item.Evidence}" }).ToArray();
        var createdInWindow = related.Count(item => item.CreatedAt.HasValue && item.CreatedAt >= first?.LocalDateTime && item.CreatedAt <= last?.LocalDateTime);
        var repeated = related.Sum(item => Math.Max(0, item.Observations - 1));
        var summary = $"{related.Length} dossiers distincts rapprochés (ticket courant inclus) dans {acceptedSources} relevés, du {first:dd/MM/yyyy HH:mm} au {last:dd/MM/yyyy HH:mm}. {repeated} observations répétées ne sont pas comptées comme nouveaux incidents. "
            + $"{createdInWindow} créations explicitement datées dans cette fenêtre ; {related.Count(item => !item.CreatedAt.HasValue)} dates de création inconnues. "
            + (targetEquipment.Length > 0 ? "Équipement du ticket : " + targetIncident.Equipment + ". " : "Équipement du ticket non collecté ou colonne ambiguë ; rapprochement par titre. ")
            + $"{partial} relevés partiels/non confirmés ; {skippedSources} ignorés ; {ambiguous} identités ambiguës ignorées. Limite : 20 relevés, 500 000 lignes et 200 résultats affichés. "
            + "Un rapprochement n’établit ni une panne récurrente ni une cause commune ; une absence ne prouve pas une clôture.";
        return new(summary, related.Take(200).ToArray(), acceptedSources, related.Length, repeated);
    }
    private static string NormalizeId(string value) => ulong.TryParse(value.Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", ""), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0 ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
}
