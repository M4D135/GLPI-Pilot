using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

/// <summary>Bounded lexical evidence from confirmed local tickets; no generated answer or confidence score.</summary>
public static class TicketLibraryResolutionService
{
    public const int MaximumCandidates = 100;
    public const int MaximumResults = 8;
    private const int MaximumRecheckedDocuments = 16;
    private const int MaximumTerms = 12;
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "le", "la", "les", "de", "des", "du", "un", "une", "et", "ou", "pour", "avec", "sans", "sur", "dans", "par", "a", "au", "aux", "en", "est", "sont",
        "ce", "ces", "cet", "cette", "mon", "ma", "mes", "son", "sa", "ses", "ne", "pas", "plus", "que", "qui", "se", "si", "il", "elle", "vous", "nous",
        "incident", "ticket", "demande", "probleme", "erreur", "bonjour", "merci", "besoin", "nouveau", "nouvelle", "urgent", "suite", "date", "description", "solution",
        "the", "and", "or", "for", "with", "from", "this", "that", "not", "have", "has", "was", "are", "request", "issue", "problem", "error", "please", "new"
    };
    private sealed record Word(string Text, string Key, bool Code);
    private sealed record Ranked(TicketLibrarySearchRow Ticket, List<string> TitleTerms, List<string> ContentTerms, bool SameCategory, int CodeCount)
    { public int TermCount => TitleTerms.Concat(ContentTerms).Distinct(StringComparer.OrdinalIgnoreCase).Count(); }

    internal static bool Eligible(TicketLibraryDocument? document) => TicketLibraryRules.IsContentConfirmed(document)
        && document!.DetailComplete && document.Seed.StatusCode is 5 or 6;

    public static async Task<TicketLibraryResolutionResult> FindAsync(TicketLibraryStore store, string sourceKey, string? text = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (sourceKey.Length is 0 or > 200) throw new InvalidDataException("Choisissez une fiche conservée pour chercher ses précédents.");
        if (text is { Length: > 300 }) throw new InvalidDataException("La recherche de précédents est limitée à 300 caractères.");
        ct.ThrowIfCancellationRequested();
        var source = await store.GetAsync(sourceKey, ct).ConfigureAwait(false) ?? throw new InvalidDataException("Ticket absent de la bibliothèque locale.");
        var manual = text is not null;
        var words = Words(manual ? text! : source.Seed.Title).OrderByDescending(word => word.Code).Take(MaximumTerms).ToList();
        var result = new TicketLibraryResolutionResult
        {
            SourceKey = source.Key, SourceTicket = source.Seed.Ticket, SourceTitle = source.Seed.Title, Installation = source.Installation,
            SourceContentConfirmed = TicketLibraryRules.IsContentConfirmed(source) && source.DetailComplete,
            Manual = manual, Terms = words.Select(word => word.Text).ToList()
        };
        if (words.Count == 0 || !manual && words.Count == 1 && !words[0].Code)
        {
            result.Message = manual ? "Ajoutez un symptôme, un équipement ou un code d’erreur distinctif."
                : "Le titre conservé contient trop peu de termes distinctifs. Précisez la recherche ci-dessous.";
            return result;
        }
        result.Available = true;
        var query = string.Join(' ', words.Select(word => word.Text));
        var found = await store.SearchAsync(new() { Text = query, Installation = source.Installation, ResolvedOnly = true, Limit = MaximumCandidates }, ct).ConfigureAwait(false);
        result.CandidatesFound = found.Total;
        var ranked = new List<Ranked>();
        foreach (var row in found.Rows)
        {
            ct.ThrowIfCancellationRequested(); result.CandidatesExamined++;
            if (row.Key == source.Key || row.Installation != source.Installation || !row.DetailsCurrent || !row.DetailComplete || row.StatusCode is not (5 or 6)) continue;
            var candidate = Rank(row, row.Citations, source.Seed.Category, words);
            if (Accept(candidate, manual, words.Count)) ranked.Add(candidate);
        }
        foreach (var preliminary in Order(ranked).Take(MaximumRecheckedDocuments))
        {
            ct.ThrowIfCancellationRequested(); result.DocumentsRechecked++;
            var snapshot = await store.ReadResolutionEvidenceAsync(preliminary.Ticket.Key, query, ct).ConfigureAwait(false);
            if (snapshot is null || snapshot.Ticket.Installation != source.Installation || snapshot.Ticket.Key == source.Key) continue;
            var candidate = Rank(snapshot.Ticket, snapshot.Citations, source.Seed.Category, words);
            if (!Accept(candidate, manual, words.Count)) continue;
            result.Matches.Add(new()
            {
                Key = candidate.Ticket.Key, Ticket = candidate.Ticket.Ticket, Title = candidate.Ticket.Title,
                Status = candidate.Ticket.Status, Category = candidate.Ticket.Category, ObservedAt = candidate.Ticket.ReadAt,
                TitleTerms = candidate.TitleTerms, ContentTerms = candidate.ContentTerms, SameCategory = candidate.SameCategory,
                Citations = snapshot.Citations.ToList()
            });
            if (result.Matches.Count == MaximumResults) break;
        }
        result.Limited = found.Total > result.CandidatesExamined || ranked.Count > result.Matches.Count;
        result.Message = result.Matches.Count == 0 ? "Aucun précédent confirmé ne partage assez de termes dans la portion examinée. Essayez des mots plus précis ou différents."
            : "Ces tickets partagent les termes indiqués. Vérifiez le contexte et les extraits avant de reprendre une solution.";
        return result;
    }

    private static IOrderedEnumerable<Ranked> Order(IEnumerable<Ranked> candidates) => candidates
        .OrderByDescending(candidate => candidate.CodeCount).ThenByDescending(candidate => candidate.TitleTerms.Count)
        .ThenByDescending(candidate => candidate.TermCount).ThenByDescending(candidate => candidate.SameCategory)
        .ThenBy(candidate => candidate.Ticket.Key, StringComparer.Ordinal);

    private static bool Accept(Ranked candidate, bool manual, int words) => candidate.TermCount >= 2 || candidate.CodeCount > 0
        || manual && words == 1 && candidate.TermCount == 1;

    private static Ranked Rank(TicketLibrarySearchRow row, IEnumerable<TicketLibraryCitation> citations, string category, IReadOnlyList<Word> words)
    {
        var title = Words(row.Title).Select(word => word.Key).ToHashSet(StringComparer.Ordinal);
        var content = citations.Where(citation => citation.SectionKey != "index" && citation.Current)
            .SelectMany(citation => Words(citation.Excerpt)).Select(word => word.Key).ToHashSet(StringComparer.Ordinal);
        var titleTerms = words.Where(word => title.Contains(word.Key)).Select(word => word.Text).ToList();
        var contentTerms = words.Where(word => content.Contains(word.Key) && !title.Contains(word.Key)).Select(word => word.Text).ToList();
        var sameCategory = !string.IsNullOrWhiteSpace(category) && Normalize(category) == Normalize(row.Category);
        return new(row, titleTerms, contentTerms, sameCategory, words.Count(word => word.Code && (title.Contains(word.Key) || content.Contains(word.Key))));
    }

    private static IEnumerable<Word> Words(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, @"[\p{L}\p{N}]+(?:[-_][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant))
        {
            var raw = match.Value;
            if (raw.Length is < 2 or > 64) continue;
            var word = Normalize(raw);
            if (StopWords.Contains(word) || !seen.Add(word)) continue;
            yield return new(raw, word, raw.Any(char.IsLetter) && raw.Any(char.IsDigit));
        }
    }

    private static string Normalize(string text)
    {
        var normalized = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) normalized.Append(char.ToLowerInvariant(character));
        return normalized.ToString().Trim();
    }
}
