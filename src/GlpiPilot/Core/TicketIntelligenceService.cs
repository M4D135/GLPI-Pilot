using System.Globalization;
using System.Text;

namespace GlpiPilot.Core;

public sealed record RelatedTicket(TicketReference Ticket, int SourceIndex, string Title, string Status, string Category, string City,
    int Score, string Relationship, string Evidence, string ResolvedDate, string Solution, string SolutionSource, DateTimeOffset ObservedAt)
{
    public string Id => Ticket.Id;
    public bool HasSolution => Solution.Length > 0;
}
public sealed record TicketIntelligenceResult(bool Available, string Message, IReadOnlyList<RelatedTicket> Related,
    int ComparedRows, DateTimeOffset ExtractedAt, string SourceUrl);

/// <summary>Deterministic comparisons within the current local receipt. Never sends ticket data outside the application.</summary>
public static class TicketIntelligenceService
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    { "le", "la", "les", "de", "des", "du", "un", "une", "et", "pour", "avec", "sur", "a", "au", "aux", "en", "est", "incident", "ticket", "demande" };
    private static readonly string[] SolutionHeaders = { "solution", "texte de la solution", "description de la solution", "contenu de la solution", "solution content" };

    public static TicketIntelligenceResult Analyze(TicketDetailResult target, ExtractionResult source, DashboardSettings settings,
        IEnumerable<TicketDetailResult>? readDetails = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        TicketIntelligenceResult Missing(string message) => new(false, message, Array.Empty<RelatedTicket>(), 0, source.ExtractedAt, source.SourceUrl);
        if (!SameInstallation(source.SourceUrl, target.Ticket.Url))
            return Missing("Le relevé courant ne permet pas de confirmer la même installation GLPI que cette fiche. Chargez le relevé correspondant.");
        var columns = DashboardService.ResolveColumns(source.Headers, settings);
        int Column(string role) => columns.FirstOrDefault(column => column.Role == role)?.Index ?? -1;
        var idColumn = Column("id"); var titleColumn = Column("title");
        if (idColumn < 0 || titleColumn < 0) return Missing("Associez une colonne d’identifiant et une colonne de titre uniques pour rapprocher les tickets.");
        string Cell(string[] row, int column) => column >= 0 && column < row.Length ? row[column] ?? "" : "";
        var identities = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < source.Rows.Count; index++)
        {
            if ((index & 127) == 0) ct.ThrowIfCancellationRequested();
            var identity = Id(Cell(source.Rows[index], idColumn)); if (identity.Length == 0) continue;
            if (!identities.TryGetValue(identity, out var rows)) identities[identity] = rows = new(); rows.Add(index);
        }
        if (!identities.TryGetValue(target.Ticket.Id, out var targetIndices) || targetIndices.Count != 1)
            return Missing("La fiche n’a pas d’identifiant unique dans le relevé courant. Aucun rapprochement n’est déduit d’un autre ticket.");
        var targetRow = source.Rows[targetIndices[0]]; var title = Normalize(Cell(targetRow, titleColumn));
        var tokens = Tokens(title); if (tokens.Count == 0) return Missing("Le titre ne contient pas assez de termes distinctifs pour un rapprochement local.");
        var categoryColumn = Column("category"); var cityColumn = Column("city"); var statusColumn = Column("status");
        var category = Normalize(Cell(targetRow, categoryColumn)); var city = Normalize(Cell(targetRow, cityColumn));
        var solutionColumns = source.Headers.Select((header, index) => (Header: Normalize(header), Index: index))
            .Where(pair => SolutionHeaders.Contains(pair.Header, StringComparer.Ordinal)).Select(pair => pair.Index).ToArray();
        var cached = (readDetails ?? Array.Empty<TicketDetailResult>()).Where(detail => SameInstallation(source.SourceUrl, detail.Ticket.Url))
            .GroupBy(detail => detail.Ticket.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.OrderByDescending(detail => detail.ReadAt).First(), StringComparer.Ordinal);
        var related = new List<RelatedTicket>();
        foreach (var pair in identities)
        {
            ct.ThrowIfCancellationRequested();
            if (pair.Key == target.Ticket.Id || pair.Value.Count != 1) continue;
            var index = pair.Value[0]; var row = source.Rows[index]; var candidateTitle = Cell(row, titleColumn);
            var normalized = Normalize(candidateTitle); var candidateTokens = Tokens(normalized);
            var shared = tokens.Intersect(candidateTokens, StringComparer.Ordinal).Count();
            var union = tokens.Union(candidateTokens, StringComparer.Ordinal).Count();
            var similarity = union == 0 ? 0 : (double)shared / union;
            var exact = title == normalized;
            if (!exact && (shared < 2 || similarity < .5)) continue;
            var sameCategory = category.Length > 0 && category == Normalize(Cell(row, categoryColumn));
            var sameCity = city.Length > 0 && city == Normalize(Cell(row, cityColumn));
            var score = (exact ? 55 : (int)Math.Round(similarity * 45)) + (sameCategory ? 20 : 0) + (sameCity ? 20 : 0);
            var relationship = exact && (sameCategory || sameCity) ? "Récurrence possible" : exact ? "Titre identique" : "Titre proche";
            var evidence = (exact ? "Titre normalisé identique" : $"{shared} termes distinctifs communs ; similarité lexicale {similarity:P0}")
                + (sameCategory ? " · même catégorie" : categoryColumn < 0 ? " · catégorie indisponible" : " · catégorie différente ou vide")
                + (sameCity ? " · même ville" : cityColumn < 0 ? " · ville indisponible" : " · ville différente ou vide")
                + ". Proposition de rapprochement, pas une preuve de doublon ni de cause commune.";
            var status = Cell(row, statusColumn); var resolved = DashboardService.Status(status) is 5 or 6;
            var solution = ""; var solutionSource = "Texte de solution non collecté"; var observedAt = source.ExtractedAt;
            if (resolved && cached.TryGetValue(pair.Key, out var detail))
            {
                var solutions = detail.Sections.Where(section => section.Kind == "timeline").SelectMany(section => section.Data.Rows)
                    .Where(entry => entry.Length > 3 && string.Equals(entry[0], "Solution", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry[3]))
                    .Select(entry => (Row: entry, HasDate: TicketReadingService.TryDate(entry[1], out var date), Date: date))
                    .OrderByDescending(entry => entry.HasDate).ThenByDescending(entry => entry.Date).ToArray();
                if (solutions.Length > 0)
                {
                    solution = solutions[0].Row[3]; observedAt = detail.ReadAt;
                    solutionSource = "Chronologie lue · date affichée : " + (solutions[0].Row[1].Length > 0 ? solutions[0].Row[1] : "absente");
                }
            }
            if (resolved && solution.Length == 0 && solutionColumns.Length == 1)
            {
                solution = Cell(row, solutionColumns[0]);
                if (solution.Length > 0) solutionSource = "Colonne « " + source.Headers[solutionColumns[0]] + " » du relevé";
            }
            if (!resolved) solutionSource = "Le relevé ne confirme pas un ticket résolu ou clos";
            var reference = new TicketReference(pair.Key, new UriBuilder(target.Ticket.Url) { Query = "id=" + pair.Key, Fragment = "" }.Uri.AbsoluteUri);
            related.Add(new(reference, index, candidateTitle, status, Cell(row, categoryColumn), Cell(row, cityColumn), score,
                relationship, evidence, Cell(row, Column("resolved")), solution, solutionSource, observedAt));
        }
        var result = related.OrderByDescending(item => item.Score).ThenByDescending(item => item.HasSolution).ThenBy(item => item.SourceIndex).Take(50).ToArray();
        var note = $"{related.Count:N0} rapprochements possibles dans {source.Rows.Count:N0} lignes · {result.Length:N0} affichés, limite 50. "
            + "Le titre conserve ses nombres ; seuls accents, casse et ponctuation sont normalisés. Les solutions proviennent de textes effectivement collectés sur des tickets résolus ou clos dans ce relevé."
            + (source.Complete ? "" : " Le relevé est partiel ou sa complétude n’est pas confirmée.")
            + (solutionColumns.Length > 1 ? " Plusieurs colonnes Solution : leurs textes ne sont pas utilisés tant que le choix est ambigu." : "");
        return new(true, note, result, source.Rows.Count, source.ExtractedAt, source.SourceUrl);
    }

    public static string Normalize(string? value)
    {
        var text = new StringBuilder();
        foreach (var character in (value ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            text.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return string.Join(" ", text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
    private static HashSet<string> Tokens(string title) => title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token.Length > 1 && !StopWords.Contains(token)).ToHashSet(StringComparer.Ordinal);
    private static string Id(string value)
    {
        var groups = value.Trim().Split(new[] { ' ', '\u00A0', '\u202F', '\u2007' }, StringSplitOptions.RemoveEmptyEntries);
        if (groups.Length > 1 && (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(group => group.Length != 3))) return "";
        return ulong.TryParse(string.Concat(groups), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : "";
    }
    private static bool SameInstallation(string source, string target)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var one) || !Uri.TryCreate(target, UriKind.Absolute, out var two)
            || one.Scheme is not ("https" or "http") || one.Scheme != two.Scheme || one.IdnHost != two.IdnHost || one.Port != two.Port || one.UserInfo.Length > 0 || two.UserInfo.Length > 0) return false;
        var sourceMarker = one.AbsolutePath.LastIndexOf("/front/", StringComparison.Ordinal);
        var targetMarker = two.AbsolutePath.LastIndexOf("/front/", StringComparison.Ordinal);
        return sourceMarker >= 0 && targetMarker >= 0 && one.AbsolutePath[..sourceMarker] == two.AbsolutePath[..targetMarker]
            && two.AbsolutePath.EndsWith("/front/ticket.form.php", StringComparison.Ordinal);
    }
}
