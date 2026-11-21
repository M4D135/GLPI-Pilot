using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private string CachedCollectionInstallation()
    {
        if (!IsCachedSource || IsFileSource || Result is not { Provenance.SourceKind: "glpi" or "browser" } source) return "";
        try
        {
            _ = TicketReferenceResolver.ReadHttpUrl(source.SourceUrl, "La source du relevé conservé");
            return ObjectIdentity.Installation(source.SourceUrl);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { return ""; }
    }

    private async Task<object?> ReadOfflineCollectionAsync(CancellationToken ct)
    {
        var source = Result; var installation = CachedCollectionInstallation();
        var store = installation.Length == 0 ? null : WorkExistingLibrary();
        var saved = store is null ? null : await store.ReadSavedCollectionAsync(installation, ct: ct).ConfigureAwait(false);
        var state = saved?.State ?? new();
        var availability = saved is null ? null : await store!.CaptureAvailabilityAsync(saved.Report.RunId, ct).ConfigureAwait(false);
        var readiness = saved is null ? null : await store!.CaptureKnowledgeReadinessAsync(saved.Report.RunId, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!IsCachedSource || !ReferenceEquals(source, Result))
            throw new InvalidOperationException("Le relevé consulté a changé. Actualisez le bilan des contenus conservés.");
        var message = saved is not null
            ? $"Dernier passage conservé pour l’installation {installation}. Son périmètre peut différer de la liste affichée ; les dates et preuves d’origine restent inchangées."
            : installation.Length > 0 ? $"Aucun passage conservé pour l’installation {installation}. Les contenus existants restent consultables dans la bibliothèque."
            : "L’installation GLPI de ce relevé n’est pas identifiée. Aucun bilan d’une autre source n’est affiché ; consultez la bibliothèque pour retrouver les contenus conservés.";
        return new
        {
            state, report = saved?.Report, availability, readiness, installation, activity = new CollectionActivity(),
            applicationMustRemainOpen = true, offlineSavedRun = true,
            scope = state.Scope, catalog = (LibraryCollectionScopeCatalog?)null, coverage = (LibraryCollectionCoverage?)null,
            inventory = (object?)null, available = false, message, warnings = Array.Empty<string>(), teams = Array.Empty<object>(),
            automaticBlocked = true, automaticBlockReason = "La consultation du cache ne déclenche aucune collecte ni programmation en ligne."
        };
    }

    private async Task<object?> ReadOfflineCaptureLatestAsync(CancellationToken ct)
    {
        var source = Result; var installation = CachedCollectionInstallation();
        var store = installation.Length == 0 ? null : WorkExistingLibrary();
        var saved = store is null ? null : await store.ReadSavedCollectionAsync(installation, ct: ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!IsCachedSource || !ReferenceEquals(source, Result)) throw new InvalidOperationException("Le relevé consulté a changé. Actualisez le journal.");
        return saved?.Report;
    }

    private async Task<object?> ReadOfflineCaptureEntriesAsync(JsonElement args, CancellationToken ct)
    {
        var source = Result; var installation = CachedCollectionInstallation();
        var store = installation.Length == 0 ? null : WorkExistingLibrary();
        if (store is null) throw new InvalidOperationException("Aucun journal associé à l’installation du relevé conservé n’est disponible.");
        var page = await store.ReadSavedCaptureEntriesAsync(installation, Text(args, "runId", 32),
            Number(args, "offset", 0, 0, TicketLibraryStore.CaptureTicketLimit), Number(args, "limit", 50, 1, 100), Flag(args, "retryOnly"), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!IsCachedSource || !ReferenceEquals(source, Result)) throw new InvalidOperationException("Le relevé consulté a changé. Actualisez le journal.");
        return page;
    }
}
