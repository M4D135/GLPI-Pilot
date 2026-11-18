namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    // A small OR group stays below the native form and URL bounds. The field
    // number and equality operator are observed on GLPI before each query.
    private const int LibrarySubsetBatchSize = 25;

    /// <summary>
    /// Revalidates only identities selected from the current inventory. Complete
    /// describes this finite selection, never all tickets of the installation.
    /// </summary>
    public async Task<TicketLibraryIndex> ReadTicketLibrarySubsetIndexAsync(AppSettings settings,
        IReadOnlyCollection<TicketReference> targets, IProgress<ExtractionProgress>? progress,
        CancellationToken ct, string sourceDateOrder = "", Action<LibraryIndexProgress>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(targets);
        ct.ThrowIfCancellationRequested();
        if (sourceDateOrder is not ("" or "dmy" or "mdy" or "ymd"))
            throw new ArgumentException("L’ordre des dates GLPI n’est pas reconnu.", nameof(sourceDateOrder));
        var source = TicketReferenceResolver.ReadHttpUrl(settings.PortalUrl, "L’adresse de la liste des tickets");
        if (!source.AbsolutePath.EndsWith("/front/ticket.php", StringComparison.Ordinal))
            throw new GlpiLibraryIndexException("session", "index-page", "La collecte ciblée requiert la liste native /front/ticket.php de cette installation GLPI.");
        if (targets.Count > 1000000)
            throw new ArgumentException("La sélection dépasse la limite de tickets prise en charge.", nameof(targets));

        // Validate the entire request before the first navigation: a later
        // malformed/cross-installation reference must not permit a partial run.
        var requested = new List<string>(targets.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(target);
            var reference = ValidateTicketReference(target, settings);
            if (seen.Add(reference.Id)) requested.Add(reference.Id);
        }
        var result = new TicketLibraryIndex
        {
            SourceUrl = source.GetLeftPart(UriPartial.Path), IsTargeted = true, RequestedCount = requested.Count,
            ScopeDescription = "Tickets sélectionnés, tous statuts hors corbeille, dans le périmètre d’entités actif de la session GLPI",
            ExpectedTotal = 0, AllStatuses = true, Complete = true, SearchRestored = true
        };
        // No browser/session claim is made for an empty selection: the scope
        // fingerprint remains empty and the caller can finish without GLPI.
        if (requested.Count == 0) return result;

        var maxPages = Math.Clamp(settings.MaxPages, 1, 10000);
        var maxRows = Math.Clamp(settings.MaxRows, 1, 1000000);
        for (var offset = 0; offset < requested.Count;)
        {
            ct.ThrowIfCancellationRequested();
            if (result.PagesRead >= maxPages || result.Tickets.Count >= maxRows)
            {
                result.Complete = false;
                result.Limitations.Add("La limite de pages ou de tickets configurée a été atteinte pour la sélection. Les autres cibles restent à vérifier.");
                break;
            }
            var batch = requested.Skip(offset).Take(Math.Min(LibrarySubsetBatchSize, maxRows - result.Tickets.Count)).ToArray();
            var pageOffset = result.PagesRead;
            var rowOffset = result.Tickets.Count;
            var batchProgress = progress is null ? null : new LibrarySubsetProgress(progress, pageOffset, rowOffset, requested.Count);
            var part = await ReadTicketLibraryIndexCoreAsync(settings, batchProgress, ct, sourceDateOrder, diagnostic,
                batch, maxPages - pageOffset, maxRows - rowOffset).ConfigureAwait(false);
            result.PagesRead += part.PagesRead;
            result.SearchRestored &= part.SearchRestored;
            result.AllStatuses &= part.AllStatuses;
            result.Complete &= part.Complete;
            result.Limitations.AddRange(part.Limitations);
            if (result.ScopeFingerprint.Length == 0) result.ScopeFingerprint = part.ScopeFingerprint;
            else if (result.ScopeFingerprint != part.ScopeFingerprint)
            {
                // Earlier metadata was observed under another authorization
                // scope. Returning it as a partial index would let the caller
                // open those tickets under the new session's permissions.
                throw new GlpiLibraryIndexException("scope", "index-scope",
                    "Le profil ou le périmètre d’entités a changé entre les lots sélectionnés. La collecte a été arrêtée avant toute lecture de contenu.");
            }
            result.ExpectedTotal += part.ExpectedTotal ?? 0;
            result.Tickets.AddRange(part.Tickets);
            offset += batch.Length;
            if (!part.Complete || !part.AllStatuses) break;
        }
        if (result.Complete && result.Tickets.Count < requested.Count)
            result.Limitations.Add($"{requested.Count - result.Tickets.Count} ticket(s) sélectionné(s) ne figurent plus dans la recherche accessible ; leurs contenus restent conservés sans confirmation de fraîcheur.");
        result.Limitations = result.Limitations.Distinct(StringComparer.Ordinal).ToList();
        diagnostic?.Invoke(new("targeted", "index-complete", $"Sélection vérifiée : {result.Tickets.Count} ticket(s) accessibles sur {requested.Count} demandé(s), {result.PagesRead} page(s) ; aucun index global parcouru."));
        return result;
    }

    private sealed class LibrarySubsetProgress(IProgress<ExtractionProgress> progress, int pages, int rows, int total) : IProgress<ExtractionProgress>
    {
        public void Report(ExtractionProgress value) => progress.Report(new(pages + value.Pages, rows + value.Rows, total,
            "Vérification des tickets sélectionnés…"));
    }
}
