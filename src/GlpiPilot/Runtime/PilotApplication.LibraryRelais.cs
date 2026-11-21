using System.Globalization;
using System.IO;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record LibraryRelaisContext(bool Available, string Installation, string Message);

    private LibraryRelaisContext RelaisLibraryContext()
    {
        if (IsTestMode) return new(false, "", "L’export pour Relais est disponible dans la bibliothèque GLPI réelle.");
        if (IsFileSource) return new(false, "", "Ouvrez une bibliothèque GLPI pour préparer son fichier Relais.");
        try
        {
            // The viewed library/cache retains its installation even if another
            // profile has been selected for the next online collection.
            var source = Result;
            var provenSource = source?.Provenance is { SourceKind: "glpi" or "browser" }
                or { SourceKind: "local", Method: "ticket-library" };
            if ((IsCachedSource || IsHistorical) && !provenSource)
                return new(false, "", "L’installation du relevé conservé n’est pas identifiée. Ouvrez la bibliothèque de l’installation à exporter.");
            var url = provenSource ? source!.SourceUrl : Settings.PortalUrl;
            _ = TicketReferenceResolver.ReadHttpUrl(url, "L’installation à exporter");
            var installation = ObjectIdentity.Installation(url);
            LibraryBootstrapFormat.ValidateInstallation(installation);
            return new(true, installation, "Toute la bibliothèque locale admissible de cette installation, indépendamment des filtres affichés.");
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException)
        { return new(false, "", "Choisissez une installation GLPI reconnue avant de préparer le fichier Relais."); }
    }

    private void RegisterLibraryRelaisOperations()
    {
        Register("library.relais.export", async (_, ct) =>
        {
            var context = RelaisLibraryContext();
            if (!context.Available) throw new InvalidOperationException(context.Message);
            var local = TicketLibrary;
            var entry = _journal.Start("Export pour Relais", context.Installation);
            try
            {
                LibraryProgress("Vérification des contenus disponibles pour Relais…");
                var preview = await local.PreviewRelaisExportAsync(context.Installation, ct).ConfigureAwait(false);
                if (!preview.CanExport)
                {
                    var message = "Aucun contenu admissible pour Relais. " + preview.Message
                        + " Consultez le bilan de collecte pour reprendre les fiches concernées. Aucun fichier créé.";
                    _journal.Finish(entry, "À vérifier", "RELAIS_NO_CONTENT", message);
                    return new { kind = "relais-export", cancelled = false, exported = false, preview, message };
                }
                ct.ThrowIfCancellationRequested();
                var path = await Host.SaveFileAsync("Enregistrer le fichier pour Relais", "Fichier Relais|*.relais",
                    "GLPI-Pilot-Relais-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".relais").ConfigureAwait(false);
                if (path is null)
                {
                    _journal.Finish(entry, "Annulé", "CANCELLED", "Enregistrement annulé. Aucun fichier créé.");
                    return new { cancelled = true };
                }
                ct.ThrowIfCancellationRequested();
                if (RelaisLibraryContext() != context)
                    throw new InvalidOperationException("L’installation à exporter a changé. Relancez l’export depuis sa bibliothèque.");
                var exported = await local.ExportRelaisAsync(path, context.Installation,
                    new InlineProgress<RelaisExportProgress>(progress => LibraryProgress(progress.Message)), ct).ConfigureAwait(false);
                var messageDone = $"Fichier Relais prêt : {exported.TicketCount:N0} tickets et {exported.TextCount:N0} fragments."
                    + (exported.ExcludedTicketCount > 0 ? $" {exported.ExcludedTicketCount:N0} fiches non admissibles n’ont pas été incluses." : "");
                _journal.Finish(entry, "Terminé", "RELAIS_EXPORTED", messageDone);
                return new { kind = "relais-export", cancelled = false, exported = true, result = exported, message = messageDone };
            }
            catch (OperationCanceledException)
            {
                _journal.Finish(entry, "Annulé", "CANCELLED", "Export interrompu. Aucun fichier partiel publié.");
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException or InvalidDataException)
            {
                // Provider and filesystem exceptions can contain paths or SQL.
                // Only the exporter's own bounded validation message reaches UI.
                var message = error is InvalidDataException or InvalidOperationException
                    ? AppPaths.SafeError(error)
                    : "Le fichier Relais n’a pas pu être créé. Vérifiez le dossier de destination, son espace disponible et vos droits d’écriture. Les fichiers existants sont conservés.";
                _journal.Finish(entry, "Échec", OperationJournal.ErrorCode(error), message);
                throw new InvalidOperationException(message);
            }
        });
    }
}
