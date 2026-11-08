using System.Globalization;
using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private async Task<TeamWorkspaceConflict> RequireWorkspaceConflictAsync(JsonElement args, CancellationToken ct)
        => await TeamWorkspace.GetConflictAsync(Text(args, "id", 80), Text(args, "conflictId", 100), ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cette version conservée ne correspond pas à la contribution demandée.");

    private async Task<object?> CompareWorkspaceConflictAsync(JsonElement args, CancellationToken ct)
    {
        var pair = await RequireWorkspaceConflictAsync(args, ct).ConfigureAwait(false);
        var comparison = TeamWorkspaceConflictComparison.Compare(pair.Current, pair.Incoming, ct);
        return new
        {
            pair.Id, pair.ItemId, pair.Reason, pair.RecordedAt, comparison,
            current = new { pair.Current.Title, pair.Current.Kind, pair.Current.Author, pair.Current.UpdatedAt, pair.Current.Deleted, hash = TeamWorkspaceCodec.ContentHash(pair.Current) },
            incoming = new { pair.Incoming.Title, pair.Incoming.Kind, pair.Incoming.Author, pair.Incoming.UpdatedAt, pair.Incoming.Deleted, hash = TeamWorkspaceCodec.ContentHash(pair.Incoming) }
        };
    }

    private async Task<object?> ExportWorkspaceConflictAsync(JsonElement args, CancellationToken ct)
    {
        var pair = await RequireWorkspaceConflictAsync(args, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var path = await Host.SaveFileAsync("Exporter les deux versions conservées", "Versions d’équipe JSON|*.json",
            "GLPI-Pilot-versions-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json").ConfigureAwait(false);
        if (path is null) return new { cancelled = true };
        var full = Path.GetFullPath(path); var database = LibrarySettings.DatabasePath;
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (new[] { database, database + "-wal", database + "-shm", database + "-journal" }.Any(active => string.Equals(full, active, comparer)))
            throw new InvalidDataException("L’export ne peut pas remplacer la base active ni ses journaux.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await JsonSerializer.SerializeAsync(stream, new
                {
                    schema = "glpi-pilot.team-conflict", format = 1, exportedAt = DateTimeOffset.UtcNow,
                    pair.Id, pair.ItemId, pair.Reason, pair.RecordedAt,
                    currentHash = TeamWorkspaceCodec.ContentHash(pair.Current), incomingHash = TeamWorkspaceCodec.ContentHash(pair.Incoming),
                    current = pair.Current, incoming = pair.Incoming
                }, Json, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new { cancelled = false, path = full };
    }
}
