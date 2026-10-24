using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private const string SimulationPortal = "https://simulation.glpi-pilot.invalid/front/ticket.php";
    private ExtractionResult? _simulationLiveData;
    private AppSettings? _simulationLiveSettings;
    private readonly Dictionary<int, TicketReference> _simulationReferences = new();
    private readonly Dictionary<string, int> _simulationRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TicketDetailResult> _simulationDetails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Kind, string Value)> _simulationActions = new(StringComparer.Ordinal);
    private bool _simulationBrowserVisible;

    private static object SimulationReply(object value, string? message = null)
    {
        var result = JsonSerializer.SerializeToNode(value, Json) as JsonObject ?? new JsonObject { ["value"] = JsonSerializer.SerializeToNode(value, Json) };
        result["simulated"] = true;
        if (message is not null) result["message"] = message;
        return result;
    }

    private async Task<object?> EnterDemoSimulationAsync(CancellationToken ct)
    {
        var data = CreateDemo();
        return await EnterSimulationAsync(data, SimulationDemoSettings(), "Démonstration · données fictives", data.Rows.Count, true, 0, ct).ConfigureAwait(false);
    }

    private async Task<object?> EnterSimulationAsync(ExtractionResult data, AppSettings interpretation, string fileName,
        long? sourceRows, bool fileReadComplete, int missingFormulaValues, CancellationToken ct, string? sourcePath = null)
    {
        if (IsSimulationInstance) throw new InvalidOperationException("La session de simulation est créée par le contrôleur principal.");
        var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Simulation-" + Guid.NewGuid().ToString("N"));
        PilotApplication? next = null; var previous = _simulation; var revision = Revision + 1;
        var realDataRoot = AppPaths.DataRoot;
        try
        {
            using (AppPaths.EnterSimulation(root))
            {
                Directory.CreateDirectory(root);
                interpretation.ExportFolder = Path.Combine(root, "Exports");
                Directory.CreateDirectory(interpretation.ExportFolder);
                interpretation.History.RestoreLastOnStartup = false;
                next = new PilotApplication(new SimulationHost(Host, realDataRoot, sourcePath), (name, value) =>
                { if (ReferenceEquals(_simulation, next)) Publish(name, value); }, interpretation, null, null, root);
                next._revision = revision;
                next._localFileTest = new(_result, _locations, _historicalDocument, _resultContext?.Copy(), "", fileName,
                    sourceRows, fileReadComplete, missingFormulaValues);
                next.RebaseSimulationSettings(next.Settings);
                next._savedSettings = next.Settings.Copy();
                next.Settings.Save();
                next._networkAvailable = () => true;
                await next.InitializeSimulationDataAsync(data, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            _simulation = next; _revision = next.Revision;
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
            Publish("settings.changed", new { settings = Settings, revision = Revision });
            Publish("session.changed", SessionState());
            Publish("library.changed", new { simulated = true });
            return SessionState();
        }
        catch
        {
            if (!ReferenceEquals(_simulation, next))
            {
                if (next is not null) await next.DisposeAsync().ConfigureAwait(false);
                else DeleteSimulationFolder(root);
            }
            throw;
        }
    }

    private void RebaseSimulationSettings(AppSettings settings)
    {
        if (!IsSimulationInstance) return;
        settings.ExportFolder = Path.Combine(_simulationRoot!, "Exports");
        settings.History.RestoreLastOnStartup = false;
    }

    private async Task InitializeSimulationDataAsync(ExtractionResult data, CancellationToken ct)
    {
        // Metadata identifies the isolated installation; source columns and rows are
        // left intact. Duplicate or absent IDs receive separate internal references.
        data.SourceUrl = SimulationPortal;
        data.Scope = "Simulation locale · " + data.Scope;
        data.Provenance.RequestedUrl = SimulationPortal;
        data.Provenance.ScopeFingerprint = "simulation:" + Path.GetFileName(_simulationRoot);
        data.Warnings.Add("Simulation isolée : les actions modifient uniquement cette copie locale. Aucun échange avec GLPI ou un serveur partagé.");
        _simulationLiveData = data;
        var columns = DashboardService.ResolveColumns(data.Headers, Settings.Dashboard);
        var idColumn = columns.FirstOrDefault(column => column.Role == "id")?.Index;
        var normalizedIds = data.Rows.Select(row =>
        {
            try { return idColumn is int index && index < row.Length ? TicketReferenceResolver.NormalizeId(row[index]) : ""; }
            catch (InvalidOperationException) { return ""; }
        }).ToArray();
        var repeated = normalizedIds.Where(id => id.Length > 0).GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var used = normalizedIds.Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        ulong generated = 10_000_000_000_000_000_000UL;
        for (var index = 0; index < data.Rows.Count; index++)
        {
            ct.ThrowIfCancellationRequested(); var id = normalizedIds[index];
            if (id.Length == 0 || repeated.Contains(id))
            { do { id = (generated++).ToString(CultureInfo.InvariantCulture); } while (!used.Add(id)); }
            var reference = new TicketReference(id, "https://simulation.glpi-pilot.invalid/front/ticket.form.php?id=" + id);
            _simulationReferences[index] = reference; _simulationRows[WorkplaceStore.TicketKey(reference)] = index;
        }
        var locations = CityDetectionService.Enrich(data, Settings.Cities, Settings.Dashboard, ct);
        SetResult(locations.Data, locations, context: Settings);
        InitializeSimulationLibrarySettings();
        await TicketLibrary.SeedSimulationAsync(Enumerable.Range(0, data.Rows.Count).Select(SimulationSeed), ct).ConfigureAwait(false);
        await TeamWorkspace.InitializeAsync(ct).ConfigureAwait(false);
        // Real local provider preferences, with clearly synthetic endpoints. The
        // registered adapters never open these addresses.
        new TourServiceSettings { RoutingEndpoint = "https://routing.simulation.invalid", WeatherEndpoint = "https://weather.simulation.invalid" }
            .Save(DataPath("terrain-services.json"));
        await SimulationArchiveAsync(ct).ConfigureAwait(false);
    }

    private static void DeleteSimulationFolder(string root)
    {
        try { using var scope = AppPaths.EnterSimulation(root); if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private sealed class SimulationHost(IPilotHost actual, string realDataRoot, string? sourcePath) : IPilotHost
    {
        public Task<string?> OpenFileAsync(string title, string filter) => actual.OpenFileAsync(title, filter);
        public async Task<string?> SaveFileAsync(string title, string filter, string suggestedName)
        {
            var path = await actual.SaveFileAsync("Simulation · " + title, filter, suggestedName).ConfigureAwait(false);
            if (path is null) return null;
            var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var root = Path.GetFullPath(realDataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, comparison) || sourcePath is not null && string.Equals(full, Path.GetFullPath(sourcePath), comparison))
                throw new InvalidDataException("Choisissez un export séparé du profil habituel et du fichier importé. La simulation ne peut pas les remplacer.");
            return path;
        }
        public Task<string?> PickFolderAsync(string title) => actual.PickFolderAsync("Simulation · " + title);
        public Task SetClipboardAsync(string text) => actual.SetClipboardAsync(text);
        public Task OpenExternalAsync(string url) => Task.CompletedTask;
        // Maps are a separate, explicit opt-in. Other simulated external actions remain inert.
        public Task OpenGoogleMapsAsync(string url) => GoogleMapsService.IsExternalMapUrl(url)
            ? actual.OpenGoogleMapsAsync(url) : throw new InvalidDataException("Lien Google Maps invalide.");
        public Task ShowGoogleMapAsync(GoogleMapFrame frame) => GoogleMapsService.IsEmbedUrl(frame.EmbedUrl)
            ? actual.ShowGoogleMapAsync(frame) : throw new InvalidDataException("Carte Google Maps invalide.");
        public Task CloseAsync() => actual.CloseAsync();
        public Task<byte[]> CapturePngAsync() => actual.CapturePngAsync();
        public Task PrintPdfAsync(string path) => actual.PrintPdfAsync(path);
        public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
    }

    private void RegisterSimulationOperations()
    {
        ReplaceSimulationOperation("records.schema", (a, ct) =>
        {
            var data = RequireResult(); if (IsHistorical) return Task.FromResult<object?>(new { available = false });
            var key = SnapshotStore.SourceKey(SimulationObservation());
            if (Flag(a, "accept")) { CheckRevision(a); _schemas.Accept(key, data.Headers); }
            return Task.FromResult<object?>(new { available = true, change = _schemas.Inspect(key, data.Headers), revision = Revision, simulated = true });
        });
        ReplaceSimulationOperation("collect.connect", (a, ct) => Task.FromResult<object?>(SimulationPages()));
        ReplaceSimulationOperation("collect.pages", (a, ct) => Task.FromResult<object?>(SimulationPages()));
        ReplaceSimulationOperation("collect.show", (a, ct) => { _simulationBrowserVisible = true; return Task.FromResult<object?>(new { visible = true, simulated = true, message = "Session GLPI simulée disponible à partir du fichier local." }); });
        ReplaceSimulationOperation("collect.hide", (a, ct) => { _simulationBrowserVisible = false; return Task.FromResult<object?>(new { visible = false, simulated = true }); });
        ReplaceSimulationOperation("collect.inspect", (a, ct) =>
        {
            CheckCollectionRevision(a); var data = RequireResult(); _collectionRevision++;
            return Task.FromResult<object?>(new[] { new { index = 0, revision = _collectionRevision, label = "Liste locale simulée", data.Headers,
                rowCount = data.Rows.Count, columnCount = data.Headers.Count, simulated = true } });
        });
        ReplaceSimulationOperation("collect.table", (a, ct) =>
        { CheckCollectionRevision(a); _collectionRevision++; return Task.FromResult<object?>(new { selected = true, revision = _collectionRevision, label = "Liste locale simulée", simulated = true }); });
        ReplaceSimulationOperation("collect.pick", (a, ct) =>
        {
            var kind = Text(a, "kind", 20); if (kind is not ("ready" or "next" or "table")) throw new InvalidDataException("Repère non pris en charge.");
            var selector = "[data-simulation='" + kind + "']";
            if (kind == "ready") Settings.ReadySelector = selector;
            else if (kind == "next") Settings.NextSelector = selector;
            else Settings.TableSelector = selector;
            CaptureCurrentSource(); SaveSettings();
            return Task.FromResult<object?>(new { cancelled = false, selector, simulated = true, message = "Repère mémorisé pour cette simulation uniquement." });
        });
        ReplaceSimulationOperation("collect.manual", async (a, ct) => { CheckCollectionRevision(a); await SimulationCollectAsync(true, ct).ConfigureAwait(false); return SessionState(); });
        ReplaceSimulationOperation("collect.captureDownload", async (a, ct) =>
        {
            await SimulationCollectAsync(false, ct).ConfigureAwait(false);
            var path = Path.Combine(Settings.ExportFolder, "simulation-collecte.csv");
            ExportService.WriteCsv(RequireResult(), path, ct); return new { path, session = SessionState(), simulated = true };
        });
        ReplaceSimulationOperation("collect.campaign", async (a, ct) =>
        {
            await SimulationCollectAsync(true, ct).ConfigureAwait(false);
            return new { sources = new[] { new { name = "Relevé local", state = "Simulation terminée", rows = RequireResult().Rows.Count, detail = "Aucun portail contacté" } }, path = "", simulated = true };
        });
        ReplaceSimulationOperation("history.archive", async (a, ct) => { CheckRevision(a); return await SimulationArchiveAsync(ct).ConfigureAwait(false); });
        ReplaceSimulationOperation("library.capture", SimulationLibraryCaptureAsync);
        ReplaceSimulationOperation("library.sync", (a, ct) => SimulationLibrarySyncAsync(ct));
        ReplaceSimulationOperation("library.tick", (a, ct) => SessionLocked || EditorOpen || !LibrarySettings.PostgresEnabled || !LibrarySettings.AutoSync
            || _nextLibrarySync is { } next && next > DateTimeOffset.UtcNow ? Task.FromResult<object?>(new { skipped = true, simulated = true }) : SimulationLibrarySyncAsync(ct));
        ReplaceSimulationOperation("library.connection.test", (a, ct) => Task.FromResult<object?>(new { success = true, simulated = true, message = "Connexion simulée : la réplique d’équipe reste dans le dossier de simulation. Aucun serveur contacté." }));
        ReplaceSimulationOperation("library.settings.save", SimulationLibrarySettingsAsync);
        ReplaceSimulationOperation("library.folder.pick", (a, ct) => Task.FromResult<object?>(new { folder = LibrarySettings.LocalFolder, cancelled = false, simulated = true }));
        ReplaceSimulationOperation("library.loadRecords", (a, ct) =>
        {
            var interpretation = _simulationLiveSettings?.Copy() ?? Settings.Copy();
            var locations = SimulationEnrich(_simulationLiveData!, interpretation, ct);
            SetResult(locations.Data, locations, context: interpretation); return Task.FromResult<object?>(SessionState());
        });
        ReplaceSimulationOperation("library.open", async (a, ct) =>
        {
            var document = await RequireLibraryTicketAsync(a, ct).ConfigureAwait(false);
            var detail = SimulationDetail(document.Seed.Ticket); WorkAcceptDetail(detail, open: true);
            return new { opened = true, simulated = true, ticketKey = WorkplaceStore.TicketKey(detail.Ticket), message = "Fiche ouverte dans l’espace de travail simulé." };
        });
        RegisterSimulationTicketOperations();
        ReplaceSimulationOperation("terrain.tours.route", (a, ct) => Task.FromResult<object?>(SimulationTourProvider(a, true, ct)));
        ReplaceSimulationOperation("terrain.tours.weather", (a, ct) => Task.FromResult<object?>(SimulationTourProvider(a, false, ct)));
    }

    private object SimulationPages()
    {
        _collectionRevision++; var data = RequireResult();
        return new { pages = new[] { new { index = 0, revision = _collectionRevision, title = data.Title,
                url = SimulationPortal, simulated = true } }, status = new SessionStatus("Session locale simulée prête", true, true),
            visible = _simulationBrowserVisible, simulated = true };
    }

    private async Task<object?> SimulationCollectAsync(bool archive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var data = _simulationLiveData ?? RequireResult();
        var entry = _journal.Start("Collecte simulée du fichier local", SimulationPortal);
        try
        {
            var copied = JsonSerializer.Deserialize<ExtractionResult>(JsonSerializer.Serialize(data, Json), Json)!;
            copied.ExtractedAt = DateTimeOffset.Now; copied.Provenance.RunId = Guid.NewGuid().ToString("N");
            copied.Status = "Simulation · collecte locale · " + copied.Rows.Count.ToString("N0", CultureInfo.CurrentCulture) + " lignes";
            var interpretation = _simulationLiveSettings?.Copy() ?? Settings.Copy();
            var locations = SimulationEnrich(copied, interpretation, ct);
            SetResult(locations.Data, locations, context: interpretation);
            if (archive) await SimulationArchiveAsync(ct).ConfigureAwait(false);
            EvaluateAlerts(ct);
            _health.Record("simulation", "Corpus local de simulation", _localFileTest?.FileReadComplete == true, copied.Status, DateTimeOffset.Now);
            _journal.Finish(entry, "Simulation terminée", "SIMULATED", copied.Status + " ; aucun portail contacté.");
            return new { session = SessionState(), simulated = true, path = "", nativePath = "", excelPath = "" };
        }
        catch (Exception error)
        {
            _journal.Finish(entry, error is OperationCanceledException ? "Simulation annulée" : "Simulation en échec", OperationJournal.ErrorCode(error), AppPaths.SafeError(error));
            throw;
        }
    }

    private ExtractionResult SimulationObservation()
    {
        var data = JsonSerializer.Deserialize<ExtractionResult>(JsonSerializer.Serialize(RequireResult(), Json), Json)!;
        // Keep the identity and date of the visible observation: alerts and their
        // contributing rows must remain attached to that exact local revision.
        data.Provenance.SourceKind = "simulation"; data.Provenance.Method = "simulation-locale";
        // Completeness concerns the imported local corpus only. These observations
        // never leave the simulation's history or acquire a real GLPI source key.
        data.Complete = _localFileTest?.FileReadComplete == true;
        data.ExpectedTotal = data.Rows.Count; data.Provenance.SearchVerified = true; data.Provenance.ScopeVerified = true;
        data.Title = "Simulation · " + data.Title; data.Scope = "Corpus local de simulation · aucun état GLPI confirmé";
        return data;
    }

    private Task<object?> SimulationArchiveAsync(CancellationToken ct)
    {
        var data = SimulationObservation();
        var evidence = HistoricalDocument?.LocationEvidence ?? (Locations is { } locations && ReferenceEquals(locations.Data, Result) ? SnapshotLocationEvidence.Capture(Result!, locations) : null);
        var metadata = Snapshots.Save(data, EffectiveSettings, ct, evidence?.ToReport(data),
            new AnalyticsPopulationStore(DataPath("site-populations.json")).LoadOrEmpty(out _));
        Publish("history.changed", new { metadata.Id, simulated = true });
        return Task.FromResult<object?>(SimulationReply(AnalyticsMetadata(metadata)));
    }

    private CityEnrichmentResult SimulationEnrich(ExtractionResult data, AppSettings interpretation, CancellationToken ct)
        => CityDetectionService.Enrich(data, interpretation.Cities, interpretation.Dashboard, ct,
            CityOverrideStore.Load(DataPath("city-overrides.json")).Overrides);
}
