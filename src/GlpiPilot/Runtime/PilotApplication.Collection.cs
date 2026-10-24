using System.IO;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Playwright;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private List<PageChoice> _pages = new();
    private List<TableChoice> _tables = new();
    private TableChoice? _selectedTable;
    private long _collectionRevision;
    private string _inspectedPageUrl = "";
    partial void SettingsContextChanged()
    {
        _collectionRevision++; _pages.Clear(); _tables.Clear(); _selectedTable = null; _inspectedPageUrl = "";
    }
    private void CheckCollectionRevision(JsonElement args)
    {
        if (!args.TryGetProperty("revision", out var value) || !value.TryGetInt64(out var revision) || revision != _collectionRevision)
            throw new InvalidOperationException("La sélection du navigateur a changé. Actualisez les onglets et inspectez de nouveau le tableau.");
    }
    private void RegisterCollectionOperations()
    {
        Register("collect.connect", async (_, ct) => { await Browser.OpenAsync(Settings.Copy(), ct, background: false).ConfigureAwait(false); return await PagesAsync().ConfigureAwait(false); });
        Register("collect.show", async (_, ct) => { await Browser.ShowBrowserAsync(Browser.ActivePage, ct).ConfigureAwait(false); return new { visible = true }; });
        Register("collect.hide", async (_, ct) => { await Browser.HideBrowserAsync(ct).ConfigureAwait(false); return new { visible = false }; });
        Register("collect.pages", async (_, _) => await PagesAsync().ConfigureAwait(false));
        Register("collect.inspect", async (a, ct) =>
        {
            CheckCollectionRevision(a); if (_pages.Count == 0) throw new InvalidOperationException("Ouvrez d’abord la connexion GLPI.");
            var choice = _pages[Number(a, "page", 0, 0, _pages.Count - 1)];
            if (choice.Page.IsClosed || SnapshotStore.SourceKey(choice.Page.Url) != SnapshotStore.SourceKey(choice.Url)) throw new InvalidOperationException("Cet onglet a changé de recherche. Actualisez la liste des onglets.");
            var tables = await Browser.InspectAsync(choice.Page, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            _tables = tables; _selectedTable = null; _inspectedPageUrl = choice.Page.Url; _collectionRevision++;
            return _tables.Select((t, i) => new { index = i, revision = _collectionRevision, t.Label, t.Headers, t.RowCount, t.ColumnCount }).ToArray();
        });
        Register("collect.table", async (a, ct) =>
        {
            CheckCollectionRevision(a); if (_tables.Count == 0) throw new InvalidOperationException("Inspectez les tableaux de l’onglet.");
            var table = _tables[Number(a, "index", 0, 0, _tables.Count - 1)]; var tables = _tables.ToList(); var url = _inspectedPageUrl;
            if (table.Page is null || table.Page.IsClosed || SnapshotStore.SourceKey(table.Page.Url) != SnapshotStore.SourceKey(url)) throw new InvalidOperationException("Le tableau a changé de recherche ; inspectez de nouveau l’onglet.");
            var next = Settings.Copy(); next.RememberTable(table); next.Sources.SingleOrDefault(s => s.Id == next.ActiveSourceId)?.Capture(next); CommitSettings(next);
            _tables = tables; _selectedTable = table; _inspectedPageUrl = url; _collectionRevision++;
            if (Flag(a, "highlight", true)) await Browser.HighlightAsync(table, ct).ConfigureAwait(false);
            return new { selected = true, revision = _collectionRevision, table.Label };
        });
        Register("collect.pick", async (a, ct) => { var page = Browser.ActivePage ?? throw new InvalidOperationException("Ouvrez d’abord le navigateur."); var kind = Text(a, "kind", 20); if (kind is not ("ready" or "next" or "table")) throw new InvalidDataException("Repère non pris en charge."); await Browser.ShowBrowserAsync(page, ct).ConfigureAwait(false); var picked = await Browser.PickAsync(page, kind, _selectedTable?.Frame, ct).ConfigureAwait(false); if (!picked.Pick.Cancelled) { if (kind == "ready") { Settings.ReadySelector = picked.Pick.Selector; Settings.SelectorOrigin = picked.Origin; } else if (kind == "next") { Settings.NextSelector = picked.Pick.Selector; Settings.SelectorOrigin = picked.Origin; } else { Settings.TableSelector = picked.Pick.Selector; Settings.TableFrameOrigin = picked.Origin; Settings.TableTargetUrl = Settings.PortalUrl; } CaptureCurrentSource(); SaveSettings(); } return picked.Pick; });
        Register("collect.run", async (_, ct) => await RunCollectionAsync(Settings.Copy(), true, ct).ConfigureAwait(false));
        Register("collect.manual", async (a, ct) => { CheckCollectionRevision(a); var table = _selectedTable ?? throw new InvalidOperationException("Inspectez et choisissez un tableau."); if (table.Page is null || table.Page.IsClosed || SnapshotStore.SourceKey(table.Page.Url) != SnapshotStore.SourceKey(_inspectedPageUrl)) throw new InvalidOperationException("Le tableau a changé de recherche ; inspectez de nouveau l’onglet."); var settings = Settings.Copy(); var data = await Browser.ExtractAsync(table, Flag(a, "followingPages", true), settings, Progress(), ct).ConfigureAwait(false); var locations = CityDetectionService.Enrich(data, settings.Cities, settings.Dashboard, ct); SetResult(locations.Data, locations, context: settings); await ArchiveActiveAsync(ct).ConfigureAwait(false); return SessionState(); });
        Register("collect.campaign", async (_, ct) => await RunCampaignAsync(ct).ConfigureAwait(false));
        Register("collect.captureDownload", async (_, ct) => { var page = Browser.ActivePage ?? throw new InvalidOperationException("Ouvrez le navigateur GLPI."); await Browser.ShowBrowserAsync(page, ct).ConfigureAwait(false); var path = await Browser.CaptureDownloadAsync(page, Settings.ExportFolder, ct).ConfigureAwait(false); var data = NativeCsvImportService.Read(path, AppPaths.SafeUrl(page.Url), Settings.ExpectedTotal, Settings.MaxRows, false, ct, diagnostics: Log); SetResult(data); return new { path, session = SessionState() }; });
    }
    private IProgress<ExtractionProgress> Progress() => new InlineProgress<ExtractionProgress>(p => Publish("operation.progress", new { p.Pages, p.Rows, p.Expected, p.Message }));
    private async Task<object> PagesAsync()
    {
        _pages = await Browser.GetPagesAsync().ConfigureAwait(false);
        _tables.Clear(); _selectedTable = null; _inspectedPageUrl = ""; _collectionRevision++;
        return new { pages = _pages.Select((p, i) => new { index = i, revision = _collectionRevision, p.Title, url = AppPaths.SafeUrl(p.Url) }).ToArray(), status = await Browser.SessionAsync(Browser.ActivePage, Settings).ConfigureAwait(false) };
    }
    private async Task<object?> RunCollectionAsync(AppSettings settings, bool archive, CancellationToken ct)
    {
        if (IsSimulationInstance) return await SimulationCollectAsync(archive, ct).ConfigureAwait(false);
        var entry = _journal.Start("Collecte GLPI", settings.PortalUrl); var service = new AutomaticExtractionService(Browser); service.Log += Log; service.PhaseChanged += phase => Publish("operation.phase", new { phase = phase.ToString() });
        try
        {
            var run = await service.RunAsync(settings, Progress(), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); Settings.RememberTable(run.Table); CaptureCurrentSource(); SaveSettings();
            SetResult(run.Data, run.Cities, context: settings); _selectedTable = run.Table; _inspectedPageUrl = run.Table.Page?.Url ?? ""; _collectionRevision++;
            var received = run.Data.Complete || run.Data.Quality is { ReceptionComplete: true, ScopeStable: true, SampleIdentityVerified: not false };
            _health.Record(settings.ActiveSourceId, settings.Sources.FirstOrDefault(s => s.Id == settings.ActiveSourceId)?.Name ?? "Source courante", received, run.Data.Status, DateTimeOffset.Now);
            if (archive) await ArchiveActiveAsync(ct).ConfigureAwait(false);
            if (archive) EvaluateAlerts(ct);
            _journal.Finish(entry, received ? "Terminé" : "À vérifier", "COLLECTED", run.Data.Status);
            return new { session = SessionState(), path = run.PrimaryPath, nativePath = run.NativePath, excelPath = run.ExcelPath };
        }
        catch (Exception error)
        {
            _journal.Finish(entry, error is OperationCanceledException ? "Annulé" : "Échec", OperationJournal.ErrorCode(error), AppPaths.SafeError(error));
            if (error is not OperationCanceledException) _health.Record(settings.ActiveSourceId, settings.Sources.FirstOrDefault(s => s.Id == settings.ActiveSourceId)?.Name ?? "Source courante", false, AppPaths.SafeError(error), DateTimeOffset.Now);
            throw;
        }
    }
    private Task ArchiveActiveAsync(CancellationToken ct)
    {
        if (IsSimulationInstance) return SimulationArchiveAsync(ct);
        if (Result is null || !Settings.History.Enabled || Result.Provenance.SourceKind == "demo" || IsHistorical || IsTestMode) return Task.CompletedTask;
        try
        {
            var populations = new AnalyticsPopulationStore().LoadOrEmpty(out var warning); if (warning.Length > 0) Log(warning);
            var saved = Snapshots.Save(Result, EffectiveSettings, ct, Locations, populations); Publish("history.changed", new { saved.Id, saved.Rows });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { Log("Archivage impossible ; le relevé reste disponible : " + AppPaths.SafeError(error)); }
        return Task.CompletedTask;
    }
    private async Task<object?> RunCampaignAsync(CancellationToken ct)
    {
        var profiles = Settings.Sources.Where(s => s.IncludedInCampaign).Select(s => s.Copy()).ToArray();
        if (profiles.Length == 0) throw new InvalidDataException("Choisissez au moins une source pour la campagne.");
        var began = DateTimeOffset.Now; var rows = profiles.Select(p => new CampaignSourceResult { Id = p.Id, Name = p.Name }).ToList(); string path;
        try
        {
            for (var i = 0; i < profiles.Length; i++)
            {
                ct.ThrowIfCancellationRequested(); var row = rows[i]; row.StartedAt = DateTimeOffset.Now; row.State = "En cours";
                var next = Settings.Copy(); profiles[i].ApplyTo(next); CommitSettings(next); Publish("campaign.progress", rows);
                try { var result = await RunCollectionAsync(Settings.Copy(), true, ct).ConfigureAwait(false); row.State = Result!.Complete ? "Terminé" : "À vérifier"; row.Rows = Result.Rows.Count; row.Detail = Result.Status; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { row.State = "Annulé"; throw; }
                catch (Exception error) { row.State = "Échec"; row.Detail = AppPaths.SafeError(error); }
                finally { row.FinishedAt = DateTimeOffset.Now; Publish("campaign.progress", rows); }
            }
        }
        finally { path = CampaignReport.Save(Settings.ExportFolder, rows, began); Log("Bilan de campagne enregistré : " + Path.GetFileName(path)); }
        return new { sources = rows, path };
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
