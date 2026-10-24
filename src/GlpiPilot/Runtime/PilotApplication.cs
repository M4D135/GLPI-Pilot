using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public interface IPilotHost
{
    Task<string?> OpenFileAsync(string title, string filter);
    Task<string?> SaveFileAsync(string title, string filter, string suggestedName);
    Task<string?> PickFolderAsync(string title);
    Task SetClipboardAsync(string text);
    Task OpenExternalAsync(string url);
    Task OpenGoogleMapsAsync(string url)
        => GoogleMapsService.IsExternalMapUrl(url) ? OpenExternalAsync(url) : throw new InvalidDataException("Lien Google Maps invalide.");
    Task ShowGoogleMapAsync(GoogleMapFrame frame) => throw new NotSupportedException("La carte intégrée est disponible dans l’application Windows. Utilisez le lien Google Maps.");
    Task CloseAsync();
    Task<byte[]> CapturePngAsync();
    Task PrintPdfAsync(string path);
    Task<bool> NotifyAsync(string title, string message);
}

/// <summary>Owns the desktop session. Only registered operations reach the C# engines; the page never executes code here.</summary>
public sealed partial class PilotApplication : IAsyncDisposable
{
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 48,
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _handlers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<string, object?> _publish;
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly LocalResultMappingStore _localMappings = new();
    private readonly OperationJournal _journal;
    private readonly object _executionAdmission = new();
    private readonly Dictionary<Task, CancellationTokenSource> _pendingExecutions = new();
    private AppSettings _savedSettings;
    private AppSettings? _resultContext;
    private sealed record LocalFileTestContext(ExtractionResult? Previous, CityEnrichmentResult? Locations, SnapshotDocument? Historical,
        AppSettings? Interpretation, string InitialQueries, string FileName, long? SourceRows, bool FileReadComplete, int MissingFormulaValues);
    private LocalFileTestContext? _localFileTest = null;
    private readonly string? _simulationRoot;
    private PilotApplication? _simulation;
    private bool IsSimulationInstance => _simulationRoot is not null;
    private AppSettings _settings = null!;
    private ExtractionResult? _result;
    private SnapshotDocument? _historicalDocument;
    private CityEnrichmentResult? _locations;
    private long _revision;
    private Task? _disposeTask;
    private volatile bool _disposed;
    private volatile bool _isBusy;
    public AppSettings Settings { get => _simulation?.Settings ?? _settings; private set => _settings = value; }
    public ExtractionResult? Result { get => _simulation is { } simulation ? simulation.Result : _result; private set => _result = value; }
    public SnapshotDocument? HistoricalDocument { get => _simulation is { } simulation ? simulation.HistoricalDocument : _historicalDocument; private set => _historicalDocument = value; }
    public CityEnrichmentResult? Locations { get => _simulation is { } simulation ? simulation.Locations : _locations; private set => _locations = value; }
    public BrowserService Browser { get; }
    private readonly SnapshotStore _snapshots;
    public SnapshotStore Snapshots => _simulation?.Snapshots ?? _snapshots;
    public BackgroundWorkScheduler Jobs { get; } = new();
    public IPilotHost Host { get; }
    public long Revision { get => _simulation?.Revision ?? _revision; private set => _revision = value; }
    public bool IsHistorical => HistoricalDocument is not null;
    public bool IsTestMode => IsSimulationInstance || _simulation is not null;
    public bool IsBusy => _isBusy;
    public string CurrentOperation { get; private set; } = "";
    public bool SessionLocked { get; set; }
    public bool EditorOpen { get; set; }
    public IReadOnlyCollection<string> Operations => _handlers.Keys;
    public AppSettings EffectiveSettings
    {
        get
        {
            if (_simulation is { } simulation) return simulation.EffectiveSettings;
            var copy = Settings.Copy();
            if (HistoricalDocument is { } history)
            { copy.Dashboard = history.Dashboard.Copy(); copy.Cities = history.Cities.Copy(); copy.Queries = history.Queries.Copy(); }
            else if (Result is not null && _resultContext is { } loadedContext)
            { copy.Dashboard = loadedContext.Dashboard.Copy(); copy.Cities = loadedContext.Cities.Copy(); copy.Queries = loadedContext.Queries.Copy(); }
            if (!IsHistorical && Result is { } result && IsLocalResult(result))
            {
                // A local file remains attached to the interpretation that opened it, even
                // when the user selects another GLPI profile for a future collection.
                if ((_localMappings.Get(result) ?? (Locations is { } locations && ReferenceEquals(locations.Data, result) ? _localMappings.Get(locations.Source) : null)) is { } mapping) copy.Dashboard = mapping;
                if (result.Provenance is { SourceKind: "local", Method: "ticket-library" })
                {
                    // Cached rows keep their own proven installation and canonical ID column.
                    // Online reads/writes still validate against Settings and RequireCurrentResult.
                    copy.PortalUrl = result.SourceUrl;
                    copy.TicketDetails.IdColumn = "ID";
                    copy.TicketDetails.UrlTemplate = "";
                }
            }
            if (!IsHistorical) copy.Dashboard.PersonalTechnician = Settings.Dashboard.PersonalTechnician;
            return copy;
        }
    }

    public PilotApplication(IPilotHost host, Action<string, object?> publish, AppSettings? settings = null, BrowserService? browser = null, SnapshotStore? snapshots = null)
        : this(host, publish, settings, browser, snapshots, null) { }
    private PilotApplication(IPilotHost host, Action<string, object?> publish, AppSettings? settings, BrowserService? browser, SnapshotStore? snapshots, string? simulationRoot)
    {
        _simulationRoot = simulationRoot;
        Host = host; _publish = publish; Settings = settings?.Copy() ?? AppSettings.Load();
        _savedSettings = Settings.Copy();
        Browser = browser ?? new(); _snapshots = snapshots ?? new(); _journal = new();
        Browser.Log += Log; Browser.ConnectionProgress += message => Publish("operation.progress", new { message });
        RegisterFoundationOperations(); RegisterAnalyticsOperations(); RegisterWorkplaceOperations(); RegisterTerrainOperations();
        if (IsSimulationInstance) { RegisterSimulationOperations(); RegisterSimulationIntegrations(); }
        else NotificationPolicy.CanNotify = () => !IsTestMode && !SessionLocked && !AutomationPolicy.IsQuiet(Settings.Automation, DateTimeOffset.Now);
    }

    public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct = default)
    {
        lock (_executionAdmission)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_handlers.TryGetValue(operation, out var handler)) throw new InvalidDataException("Opération inconnue ; rien n’a été exécuté.");
            if (arguments.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Les paramètres de l’opération doivent être un objet.");
            ct.ThrowIfCancellationRequested();
            // This registered control operation must bypass the business semaphore so it
            // also reaches a scheduled collection, which has no WebBridge request ID.
            if (operation == "operations.cancel") return handler(arguments, ct);
            var ownedArguments = arguments.Clone();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingExecutions.Add(finished.Task, linked);
            return ExecuteRegisteredAsync(operation, handler, ownedArguments, linked, finished);
        }
    }
    private async Task<object?> ExecuteRegisteredAsync(string operation, Func<JsonElement, CancellationToken, Task<object?>> handler, JsonElement arguments,
        CancellationTokenSource cancellation, TaskCompletionSource finished)
    {
        var entered = false;
        try
        {
            using var storageScope = _simulationRoot is null ? null : AppPaths.EnterSimulation(_simulationRoot);
            await _execution.WaitAsync(cancellation.Token).ConfigureAwait(false); entered = true;
            // Cancellation may release the preceding operation's semaphore while it
            // is still cancelling the rest of the queue. Do not start a queued
            // handler until that admission/cancellation pass has finished.
            lock (_executionAdmission)
            { cancellation.Token.ThrowIfCancellationRequested(); _isBusy = true; CurrentOperation = operation; }
            Publish("operation.state", new { busy = true, operation });
            if (_simulation is { } simulation && operation is not ("records.test.exit" or "records.test.import" or "records.import" or "app.demo"))
            {
                simulation.SessionLocked = SessionLocked; simulation.EditorOpen = EditorOpen;
                return await simulation.ExecuteAsync(operation, arguments, cancellation.Token).ConfigureAwait(false);
            }
            return await handler(arguments, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (entered)
                {
                    _isBusy = false; CurrentOperation = "";
                    try { Publish("operation.state", new { busy = false, operation }); }
                    finally { _execution.Release(); }
                }
            }
            finally
            {
                lock (_executionAdmission)
                {
                    _pendingExecutions.Remove(finished.Task); cancellation.Dispose(); finished.TrySetResult();
                }
            }
        }
    }
    private Task<object?> ExecuteWhenIdleAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        lock (_executionAdmission)
        {
            if (_disposed || _pendingExecutions.Count > 0 || SessionLocked || EditorOpen) return Task.FromResult<object?>(null);
            return ExecuteAsync(operation, arguments, ct);
        }
    }
    private object CancelOperations()
    {
        lock (_executionAdmission)
        {
            var pending = _pendingExecutions.Values.ToArray();
            foreach (var cancellation in pending) cancellation.Cancel();
            return new { cancelled = pending.Length > 0, count = pending.Length };
        }
    }
    private void Register(string operation, Func<JsonElement, CancellationToken, Task<object?>> handler)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(operation, "^[a-z][A-Za-z0-9]*(\\.[A-Za-z][A-Za-z0-9]*)+$") || !_handlers.TryAdd(operation, handler))
            throw new InvalidOperationException("Opération dupliquée ou invalide : " + operation);
    }
    partial void RegisterSimulationIntegrations();
    private void ReplaceSimulationOperation(string operation, Func<JsonElement, CancellationToken, Task<object?>> handler)
    {
        if (!IsSimulationInstance || !_handlers.ContainsKey(operation)) throw new InvalidOperationException("Adaptateur de simulation invalide : " + operation);
        _handlers[operation] = handler;
    }
    private ExtractionResult RequireResult() => Result ?? throw new InvalidOperationException("Chargez un relevé ou la démonstration pour ouvrir cet espace.");
    private void RequireCurrentResult()
    {
        var data = RequireResult();
        if (IsSimulationInstance && !IsHistorical) return;
        if (IsHistorical || IsLocalResult(data) || !data.Provenance.SearchVerified || !data.Provenance.ScopeVerified
            || SourceMappingPolicy.Resolve(data, Settings, false) != SourceMappingTarget.CurrentConfiguration)
            throw new InvalidOperationException("Cette action exige un relevé GLPI actuel dont la recherche et le contexte sont confirmés.");
    }
    private void SetResult(ExtractionResult data, CityEnrichmentResult? locations = null, SnapshotDocument? historical = null, AppSettings? context = null)
    {
        var previous = Result;
        var sameObservation = ReferenceEquals(previous, data) || locations is not null
            && (ReferenceEquals(locations.Source, previous) || Locations is not null && ReferenceEquals(locations.Source, Locations.Source));
        var nextContext = context?.Copy() ?? (sameObservation ? _resultContext?.Copy() : null) ?? Settings.Copy();
        if (IsLocalResult(data) && _localMappings.Get(data) is null)
        {
            var mapping = locations is not null ? _localMappings.Get(locations.Source) ?? (sameObservation ? _localMappings.Get(previous) : null) ?? nextContext.Dashboard : nextContext.Dashboard;
            _localMappings.Set(data, mapping);
            if (locations is not null && IsLocalResult(locations.Source) && _localMappings.Get(locations.Source) is null) _localMappings.Set(locations.Source, mapping);
        }
        Result = data; Locations = locations; HistoricalDocument = historical;
        _resultContext = nextContext;
        Revision++; InvalidatePreparedActions();
        Publish("session.changed", SessionState());
    }
    private void SaveSettings()
    {
        AppSettings next;
        try
        {
            DashboardService.ValidateSettings(Settings.Dashboard); Settings.Queries.Validate(); Settings.Cities.Validate(); Settings.Normalize();
            next = Settings.Copy();
            if (IsSimulationInstance) RebaseSimulationSettings(next);
            var profile = next.Sources.SingleOrDefault(s => s.Id == next.ActiveSourceId);
            if (profile is null || SnapshotStore.SourceKey(profile.Url) != SnapshotStore.SourceKey(next.PortalUrl)) next.ActiveSourceId = "";
            next.Save();
        }
        catch { Settings = _savedSettings.Copy(); throw; }
        var previous = _savedSettings; Settings = next; _savedSettings = next.Copy();
        var sourceChanged = SourceContextSignature(previous) != SourceContextSignature(next);
        if (sourceChanged || SessionConfigurationSignature(previous) != SessionConfigurationSignature(next))
        {
            if (sourceChanged && Result is { } data && !IsHistorical && !IsLocalResult(data))
            { Result = null; Locations = null; _resultContext = null; }
            else if (!IsHistorical && Result is { } current && !IsLocalResult(current))
            {
                if (Locations is { } locations && GeographySignature(previous) != GeographySignature(next)) { Result = locations.Source; Locations = null; }
                _resultContext = next.Copy();
            }
            else if (!sourceChanged && !IsHistorical && _resultContext is { } local)
            {
                if (JsonSerializer.Serialize(previous.Queries, Json) != JsonSerializer.Serialize(next.Queries, Json)) local.Queries = next.Queries.Copy();
                if (JsonSerializer.Serialize(previous.Cities, Json) != JsonSerializer.Serialize(next.Cities, Json))
                {
                    local.Cities = next.Cities.Copy();
                    if (Locations is { } locations)
                    {
                        if (_localMappings.Get(locations.Data) is { } mapping) _localMappings.Set(locations.Source, mapping);
                        Result = locations.Source; Locations = null;
                    }
                }
            }
            Revision++; InvalidatePreparedActions(); SettingsContextChanged();
            Publish("session.changed", SessionState());
        }
        Publish("settings.changed", new { settings = Settings, revision = Revision });
    }
    private static bool IsLocalResult(ExtractionResult data) => data.Provenance.SourceKind is "demo" or "local" or "file" or "simulation";
    private static string SourceContextSignature(AppSettings settings) => JsonSerializer.Serialize(new
    { url = SnapshotStore.SourceKey(settings.PortalUrl), settings.ActiveSourceId, settings.BrowserChannel, settings.AutoMode }, Json);
    private static string GeographySignature(AppSettings settings) => JsonSerializer.Serialize(new { settings.Cities, settings.Dashboard.ColumnMappings, settings.Dashboard.ValueDictionaries }, Json);
    private static string SessionConfigurationSignature(AppSettings settings) => JsonSerializer.Serialize(new
    {
        rules = SnapshotStore.RulesFingerprint(settings), settings.TicketDetails, settings.Dashboard.PersonalTechnician, settings.Sources,
        settings.TimeoutSeconds, settings.DownloadTimeoutSeconds, settings.ListTimeoutSeconds, settings.DetailTimeoutSeconds, settings.ReconnectAttempts,
        settings.MaxPages, settings.MaxRows, settings.ExpectedTotal, settings.ResumePagination, settings.CheckpointMaximumAgeMinutes,
        settings.MinimumFreeSpaceMb, settings.AutoSaveXlsx, settings.ExportFolder, settings.History, settings.Automation,
        settings.ReadySelector, settings.NextSelector, settings.SelectorOrigin, settings.TableSelector, settings.TableHeaderFingerprint, settings.TableFrameOrigin, settings.TableFrameName, settings.TableTargetUrl,
        settings.NativeSelectorOrigin, settings.NativeMenuSelector, settings.NativeFormatSelector, settings.NativeFormatValue, settings.NativeScopeSelector, settings.NativeScopeValue, settings.NativeSubmitSelector
    }, Json);
    partial void InvalidatePreparedActions();
    partial void SettingsContextChanged();
    private static string DataPath(string fixedName)
    {
        if (Path.GetFileName(fixedName) != fixedName || fixedName.Contains(':')) throw new ArgumentException("Nom de donnée locale invalide.", nameof(fixedName));
        return Path.Combine(AppPaths.DataRoot, fixedName);
    }
    private void Publish(string eventName, object? data) { if (!_disposed) _publish(eventName, data); }
    private void Log(string message)
    {
        var safe = AppPaths.SafeLog(message); _logs.Enqueue(safe); while (_logs.Count > 500) _logs.TryDequeue(out _);
        AppPaths.AppendLog(safe); Publish("operation.log", new { at = DateTimeOffset.Now, message = safe });
    }
    private object SessionState() => _simulation is { } simulation ? simulation.SessionState() : new
    {
        revision = Revision, loaded = Result is not null, title = Result?.Title ?? "Aucun relevé chargé",
        rows = Result?.Rows.Count ?? 0, columns = Result?.Headers.Count ?? 0, complete = Result?.Complete ?? false,
        date = Result?.ExtractedAt, historical = IsHistorical, demo = Result?.Provenance.SourceKind == "demo",
        testMode = IsTestMode, testFileName = _localFileTest?.FileName ?? "", testCanRestore = _localFileTest?.Previous is not null,
        simulation = IsTestMode,
        testSourceRows = _localFileTest?.SourceRows, testFileReadComplete = _localFileTest?.FileReadComplete ?? false,
        testMissingFormulaValues = _localFileTest?.MissingFormulaValues ?? 0,
        status = Result?.Status ?? "Configurez une source ou explorez la démonstration.", warnings = Result?.Warnings,
        source = Result is null ? "" : AppPaths.SafeUrl(Result.SourceUrl), provenance = Result?.Provenance,
        quality = Result?.Quality, expectedTotal = Result?.ExpectedTotal
    };
    private static string Text(JsonElement args, string name, int max = 8192, string fallback = "")
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > max) throw new InvalidDataException("Champ texte invalide : " + name);
        return value.GetString()!;
    }
    private static int Number(JsonElement args, string name, int fallback = 0, int min = 0, int max = 1000000)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback;
        if (!value.TryGetInt32(out var number) || number < min || number > max) throw new InvalidDataException("Valeur numérique hors limites : " + name);
        return number;
    }
    private static bool Flag(JsonElement args, string name, bool fallback = false)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Choix invalide : " + name);
        return value.GetBoolean();
    }
    private static T Read<T>(JsonElement args) => args.Deserialize<T>(Json) ?? throw new InvalidDataException("Données absentes ou illisibles.");
    private void CheckRevision(JsonElement args)
    {
        if (!args.TryGetProperty("revision", out var value) || !value.TryGetInt64(out var revision) || revision != Revision)
            throw new InvalidOperationException("Le relevé a changé. Actualisez cet écran avant de poursuivre.");
    }
    public ValueTask DisposeAsync()
    {
        lock (_executionAdmission)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true; _lifetime.Cancel(); Jobs.Dispose();
            _disposeTask = DrainAndDisposeAsync(_pendingExecutions.Keys.ToArray());
            return new(_disposeTask);
        }
    }
    private async Task DrainAndDisposeAsync(Task[] pending)
    {
        // Receipts, partial reads and local saves finish their cancellation paths before
        // the authenticated browser and session resources are disposed.
        await Task.WhenAll(pending).ConfigureAwait(false);
        try
        {
            if (_simulation is { } simulation)
            { try { await simulation.DisposeAsync().ConfigureAwait(false); } finally { _simulation = null; } }
            using var storageScope = _simulationRoot is null ? null : AppPaths.EnterSimulation(_simulationRoot);
            await Browser.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (!IsSimulationInstance) NotificationPolicy.CanNotify = () => true;
            else DeleteSimulationFolder(_simulationRoot!);
            _lifetime.Dispose(); _execution.Dispose();
        }
    }
}
