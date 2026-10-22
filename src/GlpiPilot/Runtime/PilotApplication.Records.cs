using System.Globalization;
using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private async Task<object?> ImportRecordsAsync(JsonElement args, CancellationToken ct)
    {
        var path = await Host.OpenFileAsync("Importer un relevé local", "Relevé Excel ou CSV|*.xlsx;*.csv;*.tsv;*.txt").ConfigureAwait(false);
        if (path is null) return new { cancelled = true };
        var data = Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? XlsxImportService.Read(path, Settings.MaxRows, ct)
            : NativeCsvImportService.Read(path, "", null, Settings.MaxRows, false, ct, diagnostics: Log);
        data.Provenance.SourceKind = "file"; data.Provenance.SearchVerified = false; data.Provenance.ScopeVerified = false;
        SetResult(data); return SessionState();
    }
    private Task<object?> RecordListAsync(JsonElement args, CancellationToken ct)
    {
        if (args.TryGetProperty("indices", out _) || args.TryGetProperty("transform", out _)) CheckRevision(args);
        var data = RequireResult(); var settings = EffectiveSettings.Dashboard;
        var offset = Number(args, "offset", 0, 0, data.Rows.Count); var limit = Number(args, "limit", 100, 1, 500);
        var transform = args.TryGetProperty("transform", out var element) ? Read<PreviewTransform>(element) : new();
        var indices = args.TryGetProperty("indices", out var chosen) ? Read<int[]>(chosen) : null;
        if (indices is not null && (indices.Length > 1000000 || indices.Any(i => i < 0 || i >= data.Rows.Count))) throw new InvalidDataException("Sélection de lignes invalide.");
        if (Flag(args, "basketOnly")) indices = _basket.Find(data, settings);
        var query = PreviewQueryService.Query(data, Text(args, "search", 4000), data.Rows.Count, indices, ct, transform, CultureInfo.GetCultureInfo(settings.DateCulture));
        var keys = DataObjectIdentity.KeyResolver(data, settings);
        return Task.FromResult<object?>(new { revision = Revision, headers = data.Headers, rows = query.SourceIndices.Skip(offset).Take(limit).Select(i => new { index = i, key = keys(i), cells = data.Rows[i].Select(cell => cell.Length > 2000 ? cell[..2000] + "…" : cell).ToArray() }), total = query.MatchCount, sourceTotal = data.Rows.Count, offset, limit, complete = data.Complete, columns = PreviewViewPreferences.Load(DataPath("preview-view.json"), data.Headers), session = SessionState() });
    }
    private async Task<object?> ExportRecordsAsync(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); var data = RequireResult(); var export = data;
        if (Flag(args, "view"))
        {
            var transform = args.TryGetProperty("transform", out var input) ? Read<PreviewTransform>(input) : new();
            var indices = args.TryGetProperty("indices", out var chosen) ? Read<int[]>(chosen) : null;
            if (indices is not null && (indices.Length > 1000000 || indices.Any(i => i < 0 || i >= data.Rows.Count))) throw new InvalidDataException("Sélection de lignes invalide.");
            if (Flag(args, "basketOnly")) indices = _basket.Find(data, EffectiveSettings.Dashboard);
            var query = PreviewQueryService.Query(data, Text(args, "search", 4000), data.Rows.Count, sourceIndices: indices, ct: ct, transform: transform, culture: CultureInfo.GetCultureInfo(EffectiveSettings.Dashboard.DateCulture));
            var columns = PreviewViewPreferences.Load(DataPath("preview-view.json"), data.Headers).Where(c => c.Visible).OrderBy(c => c.DisplayIndex).Select(c => c.SourceIndex).ToArray();
            export = new() { Title = data.Title + " · vue filtrée", SourceUrl = data.SourceUrl, ExtractedAt = data.ExtractedAt, Provenance = data.Provenance, Complete = false, Scope = "Vue locale filtrée", Headers = columns.Select(i => data.Headers[i]).ToList(), Rows = query.SourceIndices.Select(i => columns.Select(c => c < data.Rows[i].Length ? data.Rows[i][c] : "").ToArray()).ToList(), Warnings = new(data.Warnings) };
        }
        var csv = Text(args, "format", 10, "xlsx") == "csv";
        var path = await Host.SaveFileAsync("Exporter le relevé", csv ? "CSV|*.csv" : "Classeur Excel|*.xlsx", "GLPI-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + (csv ? ".csv" : ".xlsx")).ConfigureAwait(false);
        if (path is null) return new { cancelled = true };
        return csv ? ExportService.WriteCsv(export, path, ct) : ExportService.WriteXlsx(export, path, ct);
    }
    private Task<object?> CellAsync(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); var data = RequireResult(); var row = Number(args, "row", 0, 0, data.Rows.Count - 1); var column = Number(args, "column", 0, 0, data.Headers.Count - 1);
        var raw = column < data.Rows[row].Length ? data.Rows[row][column] : "";
        var settings = EffectiveSettings.Dashboard; var role = DashboardService.ResolveColumns(data.Headers, settings).FirstOrDefault(c => c.Index == column)?.Role ?? "text";
        return Task.FromResult<object?>(new { row, column, header = data.Headers[column], role, raw, interpreted = InterpretCell(raw, role, settings, data.ExtractedAt.Offset), date = data.ExtractedAt, historical = IsHistorical });
    }
    private object MappingState()
    {
        var data = Result; var settings = EffectiveSettings.Dashboard;
        return new { revision = Revision, target = SourceMappingPolicy.Resolve(data, Settings, IsHistorical).ToString(), settings, headers = data?.Headers ?? new(), roles = DashboardService.ResolveColumns(data?.Headers ?? new(), settings), preview = MappingPreview(settings) };
    }
    private object MappingPreview(DashboardSettings settings)
    {
        if (Result is null) return Array.Empty<object>();
        var columns = DashboardService.ResolveColumns(Result.Headers, settings).Where(c => c.Index.HasValue).ToArray();
        return columns.SelectMany(column => Result.Rows.Take(6).Select((row, index) => new { row = index, role = column.Role, label = column.Label, header = column.Header, raw = column.Index < row.Length ? row[column.Index!.Value] : "", interpreted = InterpretCell(column.Index < row.Length ? row[column.Index!.Value] : "", column.Role, settings, Result.ExtractedAt.Offset) })).ToArray();
    }
    private static string InterpretCell(string value, string role, DashboardSettings settings, TimeSpan offset)
    {
        if (value.Length == 0) return "Non renseigné";
        if (role is "created" or "updated" or "resolved" or "closed" or "due") return DashboardService.TrySourceDate(value, settings, offset, out var date) ? date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "Date non interprétable";
        var normalized = settings.ValueDictionaries.TryGetValue(role, out var dictionary) ? dictionary.GetValueOrDefault(value, value) : value;
        if (role is "assignee" or "group") return string.Join(" | ", DashboardService.SplitTechnicianNames(normalized, settings.MultiValueSeparators));
        if (role == "satisfaction") return DashboardService.TrySourceNumber(normalized, settings, out var number) ? number.ToString(CultureInfo.InvariantCulture) : "Nombre non interprétable";
        return normalized;
    }
    private async Task<object?> ConfigurationPreviewAsync(JsonElement args, CancellationToken ct)
    {
        string? path;
        if (args.TryGetProperty("backup", out _))
        {
            var name = Text(args, "backup", 500);
            path = SettingsRecovery.List(AppPaths.SettingsFile).SingleOrDefault(b => Path.GetFileName(b.Path) == name)?.Path ?? throw new InvalidDataException("Version de configuration introuvable.");
            _configurationDraft = SettingsRecovery.Read(path);
        }
        else
        {
            path = await Host.OpenFileAsync("Examiner une configuration d’équipe", "Configuration JSON|*.json").ConfigureAwait(false);
            if (path is null) return new { cancelled = true };
            _configurationDraft = ConfigurationTransferService.Import(path, Settings);
        }
        ct.ThrowIfCancellationRequested(); _configurationToken = Guid.NewGuid().ToString("N"); _configurationBaseline = JsonSerializer.Serialize(Settings, Json);
        return new { token = _configurationToken, name = Path.GetFileName(path), fields = ConfigurationReview.DescribeFields(Settings, _configurationDraft), conflicts = ConfigurationReview.Conflicts(Settings, _configurationDraft), sections = ConfigurationReview.Describe(Settings, _configurationDraft) };
    }
}
