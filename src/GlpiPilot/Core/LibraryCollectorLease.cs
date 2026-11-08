using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace GlpiPilot.Core;

/// <summary>One collector per GLPI installation and shared workspace, without locking normal replica exchanges.</summary>
public sealed class LibraryCollectorLease : IAsyncDisposable
{
    private FileStream? _file;
    private NpgsqlConnection? _connection;
    private int _backend;
    private bool _disposed;
    private LibraryCollectorLease() { }
    public static async Task<LibraryCollectorLease> AcquireAsync(LibraryPreferences preferences, string installation, string password, CancellationToken ct)
    {
        var lease = new LibraryCollectorLease();
        try
        {
            ct.ThrowIfCancellationRequested();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("glpi-pilot-collector-v1\n" + installation));
            if (preferences.SyncMode == "folder")
            {
                var workspace = new SharedFolderWorkspace(preferences.SharedFolder);
                if (!Directory.Exists(workspace.Folder)) throw new IOException("Partage indisponible.");
                Directory.CreateDirectory(workspace.DataFolder); SharedFolderWorkspace.RejectLink(workspace.DataFolder);
                var path = Path.Combine(workspace.DataFolder, "collection-" + Convert.ToHexString(hash).ToLowerInvariant() + ".lock");
                SharedFolderWorkspace.RejectLink(path);
                lease._file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            }
            else if (preferences.SyncMode == "postgres")
            {
                var builder = new NpgsqlConnectionStringBuilder(preferences.ConnectionString(password)) { Pooling = false, KeepAlive = 30 };
                lease._connection = new NpgsqlConnection(builder.ToString()); await lease._connection.OpenAsync(ct).ConfigureAwait(false);
                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key),pg_backend_pid()", lease._connection);
                command.Parameters.AddWithValue("key", BinaryPrimitives.ReadInt64BigEndian(hash));
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false); await reader.ReadAsync(ct).ConfigureAwait(false);
                if (!reader.GetBoolean(0)) throw new InvalidOperationException("Un autre poste détient le droit de collecte.");
                lease._backend = reader.GetInt32(1);
            }
            await lease.VerifyAsync(ct).ConfigureAwait(false); return lease;
        }
        catch (OperationCanceledException) { await lease.DisposeAsync(); throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NpgsqlException or InvalidOperationException)
        {
            await lease.DisposeAsync();
            throw new InvalidOperationException("La collecte partagée est déjà occupée ou son verrou ne peut pas être vérifié. Aucun mode local de secours n’a été activé.");
        }
    }
    public async Task VerifyAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ct.ThrowIfCancellationRequested();
        try
        {
            if (_file is not null)
            { _file.Position = 0; _file.WriteByte(1); _file.Flush(flushToDisk: true); }
            if (_connection is not null)
            {
                await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", _connection) { CommandTimeout = 8 };
                if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != _backend)
                    throw new InvalidOperationException("La session de verrouillage a changé.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NpgsqlException or InvalidOperationException)
        { throw new InvalidOperationException("Le verrou du collecteur partagé n’est plus vérifiable. Reprise manuelle nécessaire ; aucune nouvelle lecture GLPI autorisée."); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        if (_file is not null) await _file.DisposeAsync().ConfigureAwait(false);
    }
}
