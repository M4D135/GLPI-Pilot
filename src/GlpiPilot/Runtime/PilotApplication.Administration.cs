using System.Text.Json;
using GlpiPilot.Core;
using Npgsql;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record DatabaseResetDraft(string Token, string Path, LocalDatabaseResetPreview Preview, DateTimeOffset Expires);
    private DatabaseResetDraft? _databaseResetDraft;
    private void RegisterAdministrationOperations()
    {
        Register("admin.local.preview", async (a, ct) =>
        {
            AdministrationCapabilities.Require();
            var preview = await LocalDatabaseMaintenance.PreviewAsync(LibrarySettings.DatabasePath, Flag(a, "includeTeam"), ct);
            _databaseResetDraft = new(Guid.NewGuid().ToString("N"), LibrarySettings.DatabasePath, preview, DateTimeOffset.UtcNow.AddMinutes(10));
            return new { token = _databaseResetDraft.Token, preview.Tickets, preview.TeamItems, preview.PendingTickets, preview.PendingTeam, preview.IncludeTeam,
                message = "Une sauvegarde SQLite vérifiée précède l’effacement. Les données distantes, les notes personnelles et les fichiers de tournées restent conservés. La synchronisation automatique sera mise en pause." };
        });
        Register("admin.local.reset", async (a, ct) =>
        {
            AdministrationCapabilities.Require();
            var draft = _databaseResetDraft;
            if (draft is null || Text(a, "token", 40) != draft.Token || draft.Path != LibrarySettings.DatabasePath || draft.Expires < DateTimeOffset.UtcNow || !Flag(a, "confirmed"))
                throw new InvalidOperationException("Affichez le bilan de la base locale puis confirmez cette réinitialisation.");
            var settings = LibrarySettings.Copy(); settings.AutoSync = false;
            _libraryPreferencesStore.Save(settings); _libraryPreferences = settings; ResetLibrarySharing();
            var result = await LocalDatabaseMaintenance.ResetAsync(draft.Path, draft.Preview, ct);
            _databaseResetDraft = null; _ticketLibrary = null; _teamWorkspace = null;
            _librarySyncState = "paused"; _librarySyncLabel = "Base locale réinitialisée · synchronisation automatique en pause";
            Publish("library.changed", new { reset = true }); Publish("calendar.changed", new { reset = result.IncludeTeam });
            return new { result.BackupPath, result.BackupSha256, result.IncludeTeam, message = _librarySyncLabel + ". Une synchronisation explicite peut récupérer les données conservées sur le central." };
        });
        Register("admin.postgres.prepare", async (a, ct) =>
        {
            AdministrationCapabilities.Require();
            if (IsTestMode) throw new InvalidOperationException("La démonstration ne peut pas administrer un serveur PostgreSQL réel.");
            var settings = ReadLibrarySettings(a);
            if (settings.SyncMode != "postgres") throw new InvalidOperationException("Sélectionnez PostgreSQL avant de préparer la base distante.");
            var adminUser = Text(a, "adminUsername", 200).Trim(); var adminPassword = Text(a, "adminPassword", 8000);
            var maintenance = Text(a, "maintenanceDatabase", 200).Trim();
            if (adminUser.Length == 0) throw new InvalidDataException("Indiquez le compte administrateur PostgreSQL, distinct du compte technique.");
            // Administrative credentials exist only in this operation. They never
            // enter LibraryPreferences, the password cache or the normal profile.
            var administrative = new NpgsqlConnectionStringBuilder(settings.ConnectionString(adminPassword))
                { Username = adminUser, Database = maintenance.Length == 0 ? "postgres" : maintenance, Pooling = false };
            try
            {
                return await PostgresDatabaseProvisioning.PrepareAsync(administrative.ToString(), settings.Database, settings.Username,
                    LibraryConnectionPassword(settings, Text(a, "password", 8000)), ct);
            }
            catch (PostgresException error)
            {
                var reason = error.SqlState switch
                {
                    PostgresErrorCodes.InsufficientPrivilege => "Le compte administrateur n’a pas les droits nécessaires pour créer la base, préparer les tables ou attribuer les accès.",
                    PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification => "L’identification PostgreSQL a été refusée.",
                    PostgresErrorCodes.InvalidCatalogName => "La base de maintenance est introuvable. Utilisez une base existante, généralement postgres.",
                    _ => "PostgreSQL n’a pas terminé la préparation. Vérifiez les droits et la compatibilité du serveur."
                };
                throw new InvalidOperationException(reason + " Les éléments déjà créés sont conservés ; une nouvelle préparation les réutilisera. Aucun mot de passe administrateur n’est enregistré.");
            }
            catch (Exception error) when (error is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
            { throw new InvalidOperationException("Serveur PostgreSQL indisponible. Vérifiez le réseau, le certificat TLS et la base de maintenance. La préparation peut être relancée ; les éléments existants ne sont pas supprimés."); }
        });
    }
}
