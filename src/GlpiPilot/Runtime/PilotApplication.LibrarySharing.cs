using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private string _libraryReplicaBinding = "";
    private ITicketLibraryReplica? _libraryTicketReplica;
    private ITeamWorkspaceReplica? _libraryTeamReplica;
    private string _libraryStatusBinding = "";
    private LibrarySyncRecord _librarySyncRecord = new();

    private sealed class LibrarySyncRecord
    {
        public string Binding { get; set; } = "";
        public DateTimeOffset? LastAttempt { get; set; }
        public DateTimeOffset? LastSuccess { get; set; }
        public int Pushed { get; set; }
        public int Pulled { get; set; }
        public int Conflicts { get; set; }
    }

    private void ResetLibrarySharing()
    {
        _libraryReplicaBinding = ""; _libraryTicketReplica = null; _libraryTeamReplica = null;
        _libraryStatusBinding = ""; _nextLibrarySync = null; _librarySyncState = "idle";
        _librarySyncLabel = LibrarySettings.SharingEnabled ? "Prêt pour la synchronisation" : "Données enregistrées sur ce poste";
        _librarySyncError = "";
    }

    private (ITicketLibraryReplica Tickets, ITeamWorkspaceReplica Team) LibraryReplicas(LibraryPreferences settings)
    {
        if (IsSimulationInstance || AppPaths.SimulationRoot is not null)
            throw new InvalidOperationException("La simulation utilise uniquement ses répliques isolées.");
        settings.Validate(); var binding = settings.SharingBinding();
        if (_libraryReplicaBinding == binding && _libraryTicketReplica is not null && _libraryTeamReplica is not null)
            return (_libraryTicketReplica, _libraryTeamReplica);
        switch (settings.SyncMode)
        {
            case "folder":
                var indexFolder = Path.Combine(settings.LocalFolder, "SharedIndexes");
                _libraryTicketReplica = new FolderTicketLibraryReplica(settings.SharedFolder, indexFolder);
                _libraryTeamReplica = new FolderTeamWorkspaceReplica(settings.SharedFolder, indexFolder);
                break;
            case "postgres":
                var connection = settings.ConnectionString(LibraryConnectionPassword(settings));
                _libraryTicketReplica = new TicketLibraryPostgresStore(connection, provisionSchema: false);
                _libraryTeamReplica = new PostgresTeamWorkspaceReplica(connection, provisionSchema: false);
                break;
            default: throw new InvalidOperationException("Choisissez un dossier partagé ou PostgreSQL pour synchroniser les postes.");
        }
        _libraryReplicaBinding = binding; return (_libraryTicketReplica, _libraryTeamReplica);
    }

    private void LoadLibrarySyncRecord()
    {
        var binding = LibrarySettings.SharingBinding(); if (_libraryStatusBinding == binding) return;
        _libraryStatusBinding = binding; _librarySyncRecord = new() { Binding = binding };
        var path = DataPath("library-sync.local.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return;
            var saved = JsonSerializer.Deserialize<LibrarySyncRecord>(File.ReadAllText(path), Json);
            if (saved is not null && saved.Binding == binding) _librarySyncRecord = saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Log("SYNCHRONISATION · le précédent état d’affichage local n’a pas pu être relu ; les files de données restent disponibles."); }
    }

    private void SaveLibrarySyncRecord()
    {
        var path = DataPath("library-sync.local.json"); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(_librarySyncRecord, Json)); File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Log("SYNCHRONISATION · l’heure du dernier échange n’a pas pu être mémorisée ; les données de synchronisation restent conservées dans le cache."); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private object PublicLibrarySync(long pendingTickets, long pendingContributions)
    {
        LoadLibrarySyncRecord(); var settings = LibrarySettings; var enabled = settings.SharingEnabled;
        return new
        {
            state = enabled ? _librarySyncState : "local", label = enabled ? _librarySyncLabel : "Données enregistrées sur ce poste",
            lastError = enabled ? _librarySyncError : "", mode = settings.SyncMode, enabled,
            simulated = IsSimulationInstance, automatic = enabled && settings.AutoSync,
            _librarySyncRecord.LastAttempt, _librarySyncRecord.LastSuccess,
            nextSync = enabled && settings.AutoSync ? _nextLibrarySync ?? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
            pending = pendingTickets + pendingContributions, pendingTickets, pendingContributions,
            _librarySyncRecord.Pushed, _librarySyncRecord.Pulled, _librarySyncRecord.Conflicts
        };
    }

    private void LibrarySyncStarted(string label)
    {
        LoadLibrarySyncRecord(); _librarySyncRecord.LastAttempt = DateTimeOffset.UtcNow; SaveLibrarySyncRecord();
        _librarySyncState = "running"; _librarySyncLabel = label; _librarySyncError = "";
        Publish("library.changed", new { synchronizing = true, simulated = IsSimulationInstance });
    }

    private void LibrarySyncCompleted(int pushed, int pulled, int conflicts)
    {
        _librarySyncRecord.LastSuccess = DateTimeOffset.UtcNow; _librarySyncRecord.Pushed = pushed;
        _librarySyncRecord.Pulled = pulled; _librarySyncRecord.Conflicts = conflicts; SaveLibrarySyncRecord();
        Publish("library.changed", new { synchronized = true, simulated = IsSimulationInstance });
        if (pulled > 0)
        {
            Publish("calendar.changed", new { synchronized = true, simulated = IsSimulationInstance });
            Publish("session.changed", SessionState());
        }
    }
}
