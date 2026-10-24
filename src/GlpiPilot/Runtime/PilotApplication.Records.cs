using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record LocalTestImportDraft(string Path, string Token, string Fingerprint, long Revision, DateTimeOffset ExpiresAt, WorkbookPilotInspection Inspection);
    private LocalTestImportDraft? _testImportDraft;
    private readonly HashSet<int> _testRecordBasket = new();
    private int[] RecordBasketRows(ExtractionResult data, DashboardSettings settings) => IsTestMode ? _testRecordBasket.Order().ToArray() : _basket.Find(data, settings);
    private object TestRecordBasket(JsonElement args)
    {
        var data = RequireResult();
        if (args.TryGetProperty("rows", out var chosen))
        {
            CheckRevision(args); var rows = Read<int[]>(chosen);
            if (rows.Length > 10000 || rows.Any(row => row < 0 || row >= data.Rows.Count) || _testRecordBasket.Union(rows).Count() > 10000)
                throw new InvalidDataException("Le panier de simulation accepte 10 000 lignes présentes dans le fichier.");
            _testRecordBasket.UnionWith(rows);
        }
        if (Flag(args, "clear")) _testRecordBasket.Clear();
        return new { count = _testRecordBasket.Count, rows = _testRecordBasket.Order().ToArray() };
    }
    private async Task<object?> ImportTestRecordsAsync(JsonElement args, CancellationToken ct)
    {
        string? path; string? sheetName = null; var headerRow = 0;
        LocalTestImportDraft? draft = null;
        if (args.TryGetProperty("token", out _))
        {
            var token = Text(args, "token", 96);
            draft = _testImportDraft;
            if (draft is null || draft.Token != token || draft.Revision != Revision || draft.ExpiresAt < DateTimeOffset.UtcNow)
                throw new InvalidOperationException("La sélection de feuille a expiré. Ouvrez à nouveau le fichier de simulation.");
            if (Flag(args, "cancelled")) { _testImportDraft = null; return new { cancelled = true }; }
            path = draft.Path; sheetName = Text(args, "sheetName", 128);
            if (sheetName.Length == 0) throw new InvalidDataException("Choisissez la feuille contenant la liste des incidents.");
            headerRow = Number(args, "headerRow", 0, 0, 1048576);
            if (await TestImportFingerprintAsync(path, ct).ConfigureAwait(false) != draft.Fingerprint)
                throw new InvalidOperationException("Le fichier a changé depuis sa sélection. Ouvrez à nouveau le fichier de simulation.");
        }
        else
        {
            _testImportDraft = null;
            path = await Host.OpenFileAsync("Importer un fichier de simulation", "Fichier de simulation CSV ou Excel|*.csv;*.xlsx;*.xlsm;*.tsv;*.txt").ConfigureAwait(false);
            if (path is null) return new { cancelled = true };
        }
        ct.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var isWorkbook = extension is ".xlsx" or ".xlsm";
        if (extension is not (".csv" or ".xlsx" or ".xlsm" or ".tsv" or ".txt"))
            throw new InvalidDataException("Choisissez un fichier CSV, XLSX ou XLSM (TSV/TXT également acceptés). Pour un ancien classeur XLS, enregistrez une copie XLSX ou CSV dans Excel.");
        if (isWorkbook && draft is null)
        {
            var fingerprint = await TestImportFingerprintAsync(path, ct).ConfigureAwait(false);
            var inspection = WorkbookPilotImportService.Inspect(path, ct);
            if (await TestImportFingerprintAsync(path, ct).ConfigureAwait(false) != fingerprint)
                throw new InvalidOperationException("Le classeur a changé pendant son inspection. Ouvrez à nouveau le fichier de simulation.");
            var selection = new LocalTestImportDraft(path, Guid.NewGuid().ToString("N"), fingerprint, Revision, DateTimeOffset.UtcNow.AddMinutes(20), inspection);
            _testImportDraft = selection;
            return new { selectionRequired = true, token = selection.Token, fileName = Path.GetFileName(path), sheets = inspection.Sheets,
                suggestedSheet = inspection.SuggestedSheet, suggestedHeaderRow = inspection.SuggestedHeaderRow,
                workbookProfile = WorkbookProfileSummary(inspection.Profile), warnings = inspection.Warnings };
        }
        var data = isWorkbook ? XlsxImportService.Read(path, Settings.MaxRows, ct, sheetName, headerRow)
            : NativeCsvImportService.Read(path, "", null, Settings.MaxRows, false, ct);
        if (draft is not null && await TestImportFingerprintAsync(path, ct).ConfigureAwait(false) != draft.Fingerprint)
            throw new InvalidOperationException("Le fichier a changé pendant sa lecture. Aucun relevé n’a été remplacé.");
        var fileName = Path.GetFileName(path); var sourceRows = data.Quality?.SourceRows;
        var missingFormulaValues = data.Quality?.UncachedFormulaCount ?? 0;
        var fileReadComplete = sourceRows.HasValue && sourceRows.Value == data.Rows.Count && missingFormulaValues == 0;
        data.Title = fileName; data.SourceUrl = ""; data.Complete = false; data.ExpectedTotal = null;
        data.Scope = "Simulation sur fichier local · couverture GLPI non vérifiée";
        data.Provenance.SourceKind = "file"; data.Provenance.Method = isWorkbook ? extension[1..] : "csv";
        data.Provenance.OriginalFileName = fileName; data.Provenance.SearchVerified = false; data.Provenance.ScopeVerified = false;
        data.Provenance.FinishedAt = DateTimeOffset.Now;
        var receipt = fileReadComplete ? "fichier entièrement lu" : missingFormulaValues > 0 ? $"{missingFormulaValues:N0} valeurs calculées indisponibles" : "lecture limitée";
        data.Status = $"Simulation · fichier local · {data.Rows.Count:N0} lignes · {data.Headers.Count:N0} colonnes · {receipt}";
        // Validate and initialize a complete isolated session before replacing any
        // visible state. The real application and its stores remain untouched.
        var interpretation = draft?.Inspection.Profile is { } profile && Flag(args, "useWorkbookProfile", true) ? profile.ApplyTo(Settings) : Settings.Copy();
        var result = await EnterSimulationAsync(data, interpretation, fileName, sourceRows, fileReadComplete, missingFormulaValues, ct, path).ConfigureAwait(false);
        _testImportDraft = null; return result;
    }
    private static async Task<string> TestImportFingerprintAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > 512L * 1024 * 1024) throw new InvalidDataException("Le fichier dépasse la limite de taille autorisée.");
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }
    private async Task<object?> ExitTestRecordsAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_simulation is not { } simulation) return SessionState();
        var nextRevision = simulation.Revision + 1;
        try { await simulation.DisposeAsync().ConfigureAwait(false); }
        finally { _simulation = null; _revision = nextRevision; _testImportDraft = null; InvalidatePreparedActions(); }
        Publish("settings.changed", new { settings = Settings, revision = Revision });
        Publish("session.changed", SessionState());
        return SessionState();
    }
    private Task<object?> RecordListAsync(JsonElement args, CancellationToken ct)
    {
        if (args.TryGetProperty("indices", out _) || args.TryGetProperty("transform", out _)) CheckRevision(args);
        var data = RequireResult(); var settings = EffectiveSettings.Dashboard;
        var offset = Number(args, "offset", 0, 0, data.Rows.Count); var limit = Number(args, "limit", 100, 1, 500);
        var transform = args.TryGetProperty("transform", out var element) ? Read<PreviewTransform>(element) : new();
        var indices = args.TryGetProperty("indices", out var chosen) ? Read<int[]>(chosen) : null;
        if (indices is not null && (indices.Length > 1000000 || indices.Any(i => i < 0 || i >= data.Rows.Count))) throw new InvalidDataException("Sélection de lignes invalide.");
        if (Flag(args, "basketOnly")) indices = RecordBasketRows(data, settings);
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
            if (Flag(args, "basketOnly")) indices = RecordBasketRows(data, EffectiveSettings.Dashboard);
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
        var suppliedParts = ConfigurationParts.All;
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
            var incoming = ConfigurationTransferService.Import(path, Settings, out suppliedParts);
            if (suppliedParts == ConfigurationParts.None) throw new InvalidDataException("Le fichier ne contient aucune section de configuration à importer.");
            _configurationDraft = ConfigurationReview.Apply(Settings, incoming, suppliedParts, false);
        }
        ct.ThrowIfCancellationRequested(); _configurationToken = Guid.NewGuid().ToString("N"); _configurationBaseline = JsonSerializer.Serialize(Settings, Json);
        return new { token = _configurationToken, name = Path.GetFileName(path), fields = ConfigurationReview.DescribeFields(Settings, _configurationDraft), conflicts = ConfigurationReview.Conflicts(Settings, _configurationDraft), sections = ConfigurationReview.Describe(Settings, _configurationDraft).Where(section => suppliedParts.HasFlag(section.Part)) };
    }
}
