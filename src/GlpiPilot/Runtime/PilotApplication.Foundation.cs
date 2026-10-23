using System.Globalization;
using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private readonly SelectionBasket _basket = new();
    private readonly NamedPreviewStore _views = new();
    private readonly SourceSchemaStore _schemas = new();
    private readonly CollectionHealthStore _health = new();
    private readonly AlertService _alerts = new();
    private AppSettings? _configurationDraft;
    private string _configurationToken = "";
    private string _configurationBaseline = "";
    private void RegisterFoundationOperations()
    {
        Register("app.bootstrap", async (_, ct) => { await RestoreLastHistoryOnStartupAsync(ct).ConfigureAwait(false); return new { version = AppPaths.ApplicationVersion, settings = Settings, session = SessionState(), capabilities = Operations.Order().ToArray(), runtime = "C# · WebView2", operations = _journal.Entries.Take(30) }; });
        Register("app.demo", (_, _) => { SetResult(CreateDemo()); return Task.FromResult<object?>(SessionState()); });
        Register("app.diagnostic", (_, _) => Task.FromResult<object?>(new { text = DiagnosticReport.Create(Settings, Result, string.Join(Environment.NewLine, _logs)), operations = _journal.Entries }));
        Register("app.copy", async (a, ct) => { await Host.SetClipboardAsync(Text(a, "text", 2_000_000)); return new { copied = true }; });
        Register("app.close", async (_, _) => { await Host.CloseAsync(); return new { closing = true }; });
        Register("app.appearance", (a, ct) => { if (a.TryGetProperty("theme", out _)) Settings.Theme = Text(a, "theme", 10); if (a.TryGetProperty("zoom", out _)) Settings.InterfaceZoomPercent = Number(a, "zoom", 100, 75, 150); if (a.TryGetProperty("preferences", out var preferences)) Settings.Interface = Read<InterfacePreferences>(preferences); SaveSettings(); return Task.FromResult<object?>(Settings); });
        Register("settings.get", (_, _) => Task.FromResult<object?>(Settings));
        Register("settings.save", (a, ct) => { CheckRevision(a); var next = Read<AppSettings>(a.GetProperty("settings")); ValidateSettingsInput(next); var profile = next.Sources.SingleOrDefault(s => s.Id == next.ActiveSourceId); if (profile is null || SnapshotStore.SourceKey(profile.Url) != SnapshotStore.SourceKey(next.PortalUrl)) next.ActiveSourceId = ""; CommitSettings(next); return Task.FromResult<object?>(new { settings = Settings, revision = Revision, session = SessionState() }); });
        Register("settings.folder", async (_, _) => { var path = await Host.PickFolderAsync("Choisir le dossier des exports"); if (path is not null) { Settings.ExportFolder = path; SaveSettings(); } return new { path }; });
        Register("settings.backups", (_, _) => Task.FromResult<object?>(SettingsRecovery.List(AppPaths.SettingsFile)));
        Register("sources.save", (a, ct) => { CheckRevision(a); var source = Read<SourceProfile>(a.GetProperty("source")); source.Normalize(); if (source.Name.Length == 0 || source.Url.Length == 0) throw new InvalidDataException("Renseignez le nom et l’adresse de la source."); TargetUrlPolicy.PersistedTarget(source.Url); var next = Settings.Copy(); var current = next.Sources.FindIndex(s => s.Id == source.Id); if (current >= 0) next.Sources[current] = source; else { if (next.Sources.Count >= 100) throw new InvalidDataException("Cent sources au maximum."); next.Sources.Add(source); } if (next.ActiveSourceId == source.Id) source.ApplyTo(next); CommitSettings(next); return Task.FromResult<object?>(source); });
        Register("sources.capture", (a, ct) => Task.FromResult<object?>(CaptureSource(a)));
        Register("sources.select", (a, ct) => { CheckRevision(a); var next = Settings.Copy(); var source = next.Sources.SingleOrDefault(s => s.Id == Text(a, "id", 80)) ?? throw new InvalidDataException("Source introuvable."); source.ApplyTo(next); next.ActiveSourceId = source.Id; CommitSettings(next); return Task.FromResult<object?>(Settings); });
        Register("sources.remove", (a, ct) => { CheckRevision(a); var id = Text(a, "id", 80); var next = Settings.Copy(); next.Sources.RemoveAll(s => s.Id == id); if (next.ActiveSourceId == id) next.ActiveSourceId = ""; CommitSettings(next); return Task.FromResult<object?>(Settings.Sources); });
        Register("records.schema", (a, ct) => { var data = RequireResult(); if (IsHistorical || IsLocalResult(data)) return Task.FromResult<object?>(new { available = false }); var key = SnapshotStore.SourceKey(data); if (key.Length == 0) return Task.FromResult<object?>(new { available = false }); if (Flag(a, "accept")) { CheckRevision(a); _schemas.Accept(key, data.Headers); } return Task.FromResult<object?>(new { available = true, change = _schemas.Inspect(key, data.Headers), revision = Revision }); });
        Register("records.import", ImportRecordsAsync);
        Register("records.test.import", ImportTestRecordsAsync);
        Register("records.test.exit", ExitTestRecordsAsync);
        Register("records.list", RecordListAsync);
        Register("records.export", ExportRecordsAsync);
        Register("records.cell", CellAsync);
        Register("records.columns", (a, ct) => { var data = RequireResult(); if (a.TryGetProperty("columns", out var input)) { CheckRevision(a); PreviewViewPreferences.Save(DataPath("preview-view.json"), data.Headers, Read<PreviewColumnPreference[]>(input)); } return Task.FromResult<object?>(new { revision = Revision, columns = PreviewViewPreferences.Load(DataPath("preview-view.json"), data.Headers), families = data.Headers.Select((h, i) => new { index = i, family = DataObjectIdentity.Family(h) }) }); });
        Register("records.views", (_, _) => Task.FromResult<object?>(_views.List(PreviewViewPreferences.Fingerprint(RequireResult().Headers))));
        Register("records.saveView", (a, ct) => { CheckRevision(a); var view = Read<NamedPreview>(a); var data = RequireResult(); view.HeaderFingerprint = PreviewViewPreferences.Fingerprint(data.Headers); view.ColumnsCount = data.Headers.Count; _views.Save(view); return Task.FromResult<object?>(view); });
        Register("records.removeView", (a, ct) => { _views.Remove(Text(a, "id", 80)); return Task.FromResult<object?>(new { removed = true }); });
        Register("records.basket", (a, ct) => { if (IsTestMode) return Task.FromResult<object?>(TestRecordBasket(a)); var data = RequireResult(); if (a.TryGetProperty("rows", out var rows)) { CheckRevision(a); _basket.Add(data, Read<int[]>(rows), EffectiveSettings.Dashboard); } if (Flag(a, "clear")) _basket.Clear(); return Task.FromResult<object?>(new { count = _basket.Keys.Count, rows = _basket.Find(data, EffectiveSettings.Dashboard) }); });
        Register("mapping.get", (_, _) => Task.FromResult<object?>(MappingState()));
        Register("mapping.preview", (a, ct) => { CheckRevision(a); var settings = Read<DashboardSettings>(a.GetProperty("settings")); DashboardService.ValidateSettings(settings); return Task.FromResult<object?>(MappingPreview(settings)); });
        Register("mapping.save", (a, ct) => Task.FromResult<object?>(SaveMapping(a)));
        Register("records.quality", (_, ct) => Task.FromResult<object?>(DataQualityService.Analyze(RequireResult(), EffectiveSettings.Dashboard, ct)));
        Register("records.divergences", ReadCellDivergencesAsync);
        Register("records.divergences.export", ExportCellDivergencesAsync);
        Register("configuration.export", async (_, _) => { var path = await Host.SaveFileAsync("Exporter la configuration d’équipe", "Configuration JSON|*.json", "GLPI-Pilot-equipe.json"); if (path is not null) ConfigurationTransferService.Export(path, Settings); return new { path }; });
        Register("configuration.preview", ConfigurationPreviewAsync);
        Register("configuration.apply", (a, ct) => { if (_configurationDraft is null || Text(a, "token", 100) != _configurationToken || _configurationBaseline != JsonSerializer.Serialize(Settings, Json)) throw new InvalidOperationException("L’aperçu d’import a expiré."); var parts = (ConfigurationParts)Number(a, "parts", 255, 0, 255); var choices = a.TryGetProperty("choices", out var c) ? Read<Dictionary<string, string>>(c) : new(); var next = ConfigurationReview.Apply(Settings, _configurationDraft, parts, Flag(a, "merge", true), choices); CommitSettings(next); _configurationDraft = null; _configurationToken = ""; return Task.FromResult<object?>(Settings); });
        Register("operations.list", (_, _) => Task.FromResult<object?>(new { entries = _journal.Entries, log = _logs.ToArray(), health = _health.Entries }));
        Register("operations.acknowledge", (a, ct) => { _journal.Acknowledge(Text(a, "id", 80)); return Task.FromResult<object?>(new { saved = true }); });
        Register("operations.cancel", (_, _) => Task.FromResult<object?>(CancelOperations()));
        RegisterCollectionOperations(); RegisterAutomationOperations(); RegisterLibraryOperations();
    }
    private SourceProfile CaptureSource(JsonElement args)
    {
        CheckRevision(args); _ = TargetUrlPolicy.Validate(Settings.PortalUrl);
        var next = Settings.Copy(); var id = Text(args, "id", 80); SourceProfile source;
        if (id.Length > 0)
            source = next.Sources.SingleOrDefault(value => value.Id == id) ?? throw new InvalidDataException("Source introuvable ; aucun profil n’a été remplacé.");
        else
        {
            if (next.Sources.Count >= 100) throw new InvalidDataException("Cent sources au maximum.");
            source = new SourceProfile { Name = "Ma source" }; next.Sources.Add(source);
        }
        if (args.TryGetProperty("name", out _)) source.Name = Text(args, "name", 120).Trim();
        if (source.Name.Length == 0) throw new InvalidDataException("Renseignez le nom de la source.");
        // Only this explicit capture replaces the named recipe with the current
        // settings. Its identity, campaign choice and workspace choice survive.
        source.Capture(Settings); next.ActiveSourceId = source.Id; CommitSettings(next); return source;
    }
    private object SaveMapping(JsonElement args)
    {
        CheckRevision(args); var mapping = Read<DashboardSettings>(args.GetProperty("settings")); DashboardService.ValidateSettings(mapping);
        var target = SourceMappingPolicy.Resolve(Result, Settings, IsHistorical);
        if (target == SourceMappingTarget.LocalResult)
        {
            var data = RequireResult(); var source = Locations is { } locations && ReferenceEquals(locations.Data, data) ? locations.Source : data;
            _localMappings.Set(data, mapping); if (!ReferenceEquals(source, data)) _localMappings.Set(source, mapping);
            // Derived locations were calculated with the former interpretation.
            // Keep the untouched imported cells and let the engines derive them anew.
            SetResult(source, context: _resultContext);
        }
        else if (target == SourceMappingTarget.CurrentConfiguration)
        {
            var next = Settings.Copy(); next.Dashboard = mapping; next.Sources.SingleOrDefault(s => s.Id == next.ActiveSourceId)?.Capture(next); CommitSettings(next);
        }
        else throw new InvalidOperationException("Ces correspondances ne peuvent pas modifier la source actuelle.");
        return MappingState();
    }
    private void CommitSettings(AppSettings next)
    {
        ValidateSettingsInput(next); var previous = Settings;
        try { Settings = next; SaveSettings(); } catch { Settings = previous; throw; }
    }
    private static void ValidateSettingsInput(AppSettings settings)
    {
        if (settings.ListTimeoutSeconds is < 5 or > 600 || settings.DetailTimeoutSeconds is < 5 or > 600 || settings.ReconnectAttempts is < 0 or > 5 || settings.MaxRows is < 1 or > 1_000_000 || settings.MaxPages is < 1 or > 10000) throw new InvalidDataException("Les limites de collecte ne sont pas valides.");
        DashboardService.ValidateSettings(settings.Dashboard); settings.Cities.Validate(); settings.Queries.Validate();
        if (settings.PortalUrl.Length > 0) _ = TargetUrlPolicy.PersistedTarget(settings.PortalUrl);
        AutomationPolicy.Times(settings.Automation.FixedTimes); AutomationPolicy.Days(settings.Automation.WeekDays);
        if (settings.Automation.Steps.Count > 0) AutomationPolicy.ValidateRecipe(settings.Automation.Steps);
    }
    private void CaptureCurrentSource() => Settings.Sources.SingleOrDefault(s => s.Id == Settings.ActiveSourceId)?.Capture(Settings);
    private static ExtractionResult CreateDemo()
    {
        var data = new ExtractionResult { Headers = new() { "ID", "Titre", "Statut", "Priorité", "Date d’ouverture", "Dernière modification", "Attribué à - Technicien", "Catégorie", "Lieu", "Date de résolution", "Échéance" }, Title = "Démonstration fictive", Scope = "Données fictives de démonstration", Complete = false, Provenance = new() { SourceKind = "demo", Method = "synthetic" } };
        var cities = new[] { "Marseille", "Aix-en-Provence", "Aubagne", "Arles" }; var technicians = new[] { "Alex Martin", "Camille Durand", "Sam Bernard", "" };
        for (var i = 1; i <= 180; i++)
        {
            var status = new[] { "Nouveau", "En cours (attribué)", "En attente", "Résolu", "Clos" }[i % 5];
            data.Rows.Add(new[] { i.ToString(CultureInfo.InvariantCulture), $"{cities[i % 4]} · Incident fictif {i}", status, new[] { "Basse", "Moyenne", "Haute", "Très haute" }[i % 4], DateTime.Today.AddDays(-i % 60).ToString("dd/MM/yyyy HH:mm"), DateTime.Today.AddDays(-i % 20).ToString("dd/MM/yyyy HH:mm"), technicians[i % 4], new[] { "Poste de travail", "Impression", "Réseau" }[i % 3], cities[i % 4], status is "Résolu" or "Clos" ? DateTime.Today.AddDays(-i % 7).ToString("dd/MM/yyyy HH:mm") : "", DateTime.Today.AddDays(i % 9 - 4).ToString("dd/MM/yyyy HH:mm") });
        }
        data.Status = "180 tickets fictifs · démonstration"; data.Warnings.Add("Démonstration : aucun relevé métier ni action GLPI."); return data;
    }
}
