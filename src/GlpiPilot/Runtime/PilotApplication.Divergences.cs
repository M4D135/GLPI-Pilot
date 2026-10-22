using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private Task<object?> ReadCellDivergencesAsync(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); ct.ThrowIfCancellationRequested();
        var data = RequireResult();
        var name = data.Quality?.DivergenceReportName ?? "";
        if (name.Length == 0)
            return Task.FromResult<object?>(new { available = false, reason = "Aucun diagnostic détaillé enregistré pour ce relevé. Une nouvelle collecte capture les éventuels écarts." });
        try
        {
            var document = NativeCellDivergenceReport.Read(AppPaths.CellDivergenceFolder, name);
            if (document.RunId != data.Provenance.RunId)
                throw new InvalidDataException("Ce diagnostic appartient à une autre collecte.");
            return Task.FromResult<object?>(new { available = true, fileName = name, document });
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<object?>(new { available = false, reason = "Le diagnostic détaillé n’est plus disponible sur ce poste. Une nouvelle collecte capture les éventuels écarts." });
        }
    }

    private async Task<object?> ExportCellDivergencesAsync(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); ct.ThrowIfCancellationRequested();
        var data = RequireResult();
        var name = data.Quality?.DivergenceReportName ?? "";
        if (name.Length == 0) throw new InvalidOperationException("Ce relevé ne possède pas de diagnostic détaillé des écarts.");
        var document = NativeCellDivergenceReport.Read(AppPaths.CellDivergenceFolder, name);
        if (document.RunId != data.Provenance.RunId) throw new InvalidDataException("Ce diagnostic appartient à une autre collecte.");
        var path = await Host.SaveFileAsync("Exporter le diagnostic des écarts", "Diagnostic JSON|*.json", name).ConfigureAwait(false);
        if (path is null) return new { cancelled = true };
        CheckRevision(args); ct.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".glpi-diagnostic-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions(Json) { WriteIndented = true });
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(bytes, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new { cancelled = false, path = destination };
    }
}
