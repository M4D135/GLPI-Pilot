using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private readonly Dictionary<string, AnalyticsLayoutJournal> _businessLayoutJournals = new(StringComparer.Ordinal);

    private void RegisterAnalyticsBusinessOperations()
    {
        Register("dashboard.business.bootstrap", (a, ct) => Task.FromResult<object?>(AnalyticsBusinessBootstrap(ct)));
        Register("dashboard.business.settings.get", (a, ct) => Task.FromResult<object?>(AnalyticsBusinessBootstrap(ct)));
        Register("dashboard.business.settings.save", AnalyticsBusinessSaveSettingsAsync);
        Register("dashboard.business.reset", AnalyticsBusinessResetAsync);
        Register("dashboard.business.copy", AnalyticsBusinessCopyAsync);
        RegisterBusinessProfileOperations();
    }

    private DashboardSettings AnalyticsBusinessSettings()
    {
        // Local-file interpretation is attached to its source; the native views
        // deliberately use the latest workspace presentation and pilot profile.
        var settings = EffectiveSettings.Dashboard.Copy();
        settings.Business = Settings.Dashboard.Business.Copy();
        settings.Layout = Settings.Dashboard.Layout.Copy();
        return settings;
    }
    private static string AnalyticsBusinessKey(JsonElement args)
    {
        var key = Text(args, "businessView", 40);
        if (key.Length > 0 && !BusinessDashboardSettings.ViewKeys.Contains(key)) throw new InvalidDataException("Rubrique de pilotage inconnue.");
        return key;
    }
    private DashboardLayoutSettings AnalyticsBusinessLayout(string key, CancellationToken ct = default)
    {
        var settings = AnalyticsBusinessSettings();
        var observed = BusinessDashboardTemplates.Observe(Result, settings, ct);
        var board = BusinessDashboardTemplates.Create(key, settings, observed);
        return new() { ActiveBoardId = board.Id, Boards = new() { board }, CatalogFavorites = new(Settings.Dashboard.Layout.CatalogFavorites),
            CatalogRecent = new(Settings.Dashboard.Layout.CatalogRecent), FilterPresets = Settings.Dashboard.Layout.FilterPresets.Select(preset => preset.Copy()).ToList() };
    }
    private object AnalyticsBusinessBootstrap(CancellationToken ct)
    {
        var settings = AnalyticsBusinessSettings(); var observed = BusinessDashboardTemplates.Observe(Result, settings, ct);
        var business = BusinessDashboardTemplates.Profile(settings, observed);
        var focus = business.WeatherRules.Where(rule => rule.Kind == "team").Select(rule => new { key = "team:" + rule.Id, label = rule.Label, groups = rule.Values }).ToArray();
        return new { revision = Revision, hasSource = Result is not null, business, groups = observed.Groups, categories = observed.Categories,
            focus, sites = Settings.Cities.Sites.Select(site => site.Copy()).ToArray(), cities = Settings.Cities.Cities.Select(city => new { city.Id, city.Name }).ToArray(),
            fingerprint = AnalyticsBusinessSettingsFingerprint(), configured = Settings.Dashboard.Business.WeatherRules.Count > 0,
            views = BusinessDashboardTemplates.Views, emptyWeather = Result is null ? BusinessDashboardTemplates.Create("weather", settings, observed) : null };
    }
    private string AnalyticsBusinessSettingsFingerprint() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Settings.Dashboard.Business, Settings.Cities }, Json))));
    private Task<object?> AnalyticsBusinessSaveSettingsAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Text(args, "baseFingerprint", 64) != AnalyticsBusinessSettingsFingerprint())
            throw new InvalidOperationException("Les paramètres ont changé depuis l’ouverture. Rechargez leur aperçu avant d’enregistrer.");
        var business = AnalyticsArgument<BusinessDashboardSettings>(args, "business"); business.Validate();
        var next = Settings.Copy();
        // Settings changes replace rule semantics while retaining user card geometry.
        AnalyticsBusinessRefreshWeatherOverride(business);
        foreach (var board in business.BoardOverrides.Where(board => board.Id == "business-sites"))
            foreach (var widget in board.Widgets.Where(widget => widget.Kind == "sites")) widget.WarningValue = business.SiteInterventionThreshold;
        next.Dashboard.Business = business.Copy();
        if (args.TryGetProperty("sites", out var sites))
        {
            next.Cities.Sites = Read<List<SiteDefinition>>(sites); if (next.Cities.Sites.Count > 0) next.Cities.Enabled = true;
            next.Cities.Validate();
        }
        CommitSettings(next); _analyticsRun = null; _businessLayoutJournals.Clear();
        return Task.FromResult<object?>(new { saved = true, settings = Settings, revision = Revision });
    }
    private void AnalyticsBusinessRefreshWeatherOverride(BusinessDashboardSettings business)
    {
        var saved = business.BoardOverrides.FirstOrDefault(board => board.Id == "business-weather");
        if (saved is null) return;
        var settings = AnalyticsBusinessSettings(); var observed = BusinessDashboardTemplates.Observe(Result, settings);
        var previous = BusinessDashboardTemplates.WeatherRules(settings, observed);
        settings.Business = business.Copy(); settings.Business.BoardOverrides.RemoveAll(board => board.Id == "business-weather");
        var generated = BusinessDashboardTemplates.Create("weather", settings, observed);
        var oldWeather = saved.Widgets.Where(widget => widget.Id.StartsWith("weather-rule-", StringComparison.Ordinal)
            || previous.Any(rule => rule.Id == widget.Id || rule.Label == widget.Title && widget.Kind == "metric")).ToArray();
        var freshWeather = generated.Widgets.Where(widget => widget.Id.StartsWith("weather-rule-", StringComparison.Ordinal)).ToArray();
        foreach (var fresh in freshWeather)
        {
            var old = oldWeather.FirstOrDefault(widget => widget.Id == fresh.Id || widget.Title == fresh.Title);
            if (old is null) continue;
            fresh.Span = old.Span; fresh.Height = old.Height; fresh.AutoHeight = old.AutoHeight; fresh.StartNewRow = old.StartNewRow;
            fresh.Style = old.Style; fresh.Accent = old.Accent; fresh.Locked = old.Locked;
            if (saved.Sections.Any(section => section.Key == old.Section)) fresh.Section = old.Section;
        }
        var insertion = saved.Widgets.FindIndex(widget => oldWeather.Contains(widget));
        saved.Widgets.RemoveAll(widget => oldWeather.Contains(widget));
        saved.Widgets.InsertRange(insertion < 0 ? 0 : Math.Min(insertion, saved.Widgets.Count), freshWeather);
        foreach (var section in generated.Sections.Where(section => freshWeather.Any(widget => widget.Section == section.Key) && !saved.Sections.Any(existing => existing.Key == section.Key))) saved.Sections.Add(section.Copy());
        saved.FreshnessWarningHours = business.FreshnessWarningHours;
        var notes = saved.Widgets.FirstOrDefault(widget => widget.Kind == "note" && widget.Section == "actions");
        if (notes is not null) notes.Note = business.Notes;
        business.Validate();
    }
    private AnalyticsLayoutJournal AnalyticsBusinessJournal(string key, DashboardLayoutSettings? layout = null)
    {
        var signature = JsonSerializer.Serialize(layout ?? AnalyticsBusinessLayout(key), Json);
        if (!_businessLayoutJournals.TryGetValue(key, out var journal)) _businessLayoutJournals[key] = journal = new();
        if (signature != journal.Expected) { journal.Undo.Clear(); journal.Redo.Clear(); journal.Expected = signature; }
        return journal;
    }
    private void AnalyticsBusinessPersistLayout(string key, DashboardLayoutSettings layout, bool remember = true)
    {
        layout.Validate();
        if (layout.Boards.Count != 1 || layout.Boards[0].Id != "business-" + key)
            throw new InvalidDataException("Une rubrique native conserve son tableau. Créez les tableaux supplémentaires dans Mes tableaux.");
        var before = AnalyticsBusinessLayout(key); var journal = AnalyticsBusinessJournal(key, before);
        var signature = JsonSerializer.Serialize(layout, Json); if (signature == journal.Expected) return;
        var next = Settings.Copy(); next.Dashboard.Business.BoardOverrides.RemoveAll(board => board.Id == "business-" + key);
        next.Dashboard.Business.BoardOverrides.Add(layout.Boards[0].Copy());
        next.Dashboard.Layout.CatalogFavorites = new(layout.CatalogFavorites); next.Dashboard.Layout.CatalogRecent = new(layout.CatalogRecent);
        next.Dashboard.Layout.FilterPresets = layout.FilterPresets.Select(preset => preset.Copy()).ToList();
        next.Dashboard.Business.Validate(); CommitSettings(next); _analyticsRun = null;
        if (remember) { journal.Undo.Add(before); if (journal.Undo.Count > 20) journal.Undo.RemoveAt(0); journal.Redo.Clear(); }
        journal.Expected = JsonSerializer.Serialize(AnalyticsBusinessLayout(key), Json);
    }
    private Task<object?> AnalyticsBusinessMoveLayoutAsync(string key, bool redo, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var journal = AnalyticsBusinessJournal(key); var from = redo ? journal.Redo : journal.Undo; var to = redo ? journal.Undo : journal.Redo;
        if (from.Count == 0) throw new InvalidOperationException(redo ? "Aucune modification à rétablir." : "Aucune modification à annuler.");
        var before = AnalyticsBusinessLayout(key); var next = from[^1].Copy(); AnalyticsBusinessPersistLayout(key, next, false); from.RemoveAt(from.Count - 1); to.Add(before);
        return Task.FromResult<object?>(new { saved = true, boardId = next.ActiveBoardId, undo = journal.Undo.Count, redo = journal.Redo.Count });
    }
    private Task<object?> AnalyticsBusinessResetAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var key = AnalyticsBusinessKey(args); if (key.Length == 0) throw new InvalidDataException("Choisissez une rubrique.");
        var next = Settings.Copy(); next.Dashboard.Business.BoardOverrides.RemoveAll(board => board.Id == "business-" + key);
        CommitSettings(next); _businessLayoutJournals.Remove(key); _analyticsRun = null;
        return Task.FromResult<object?>(new { saved = true, boardId = "business-" + key });
    }
    private Task<object?> AnalyticsBusinessCopyAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var key = AnalyticsBusinessKey(args); if (key.Length == 0) throw new InvalidDataException("Choisissez une rubrique.");
        var board = AnalyticsBusinessLayout(key, ct).Boards[0].Copy(); board.Id = DashboardLayoutSettings.NewId(); board.Name = (board.Name.Length > 110 ? board.Name[..110] : board.Name) + " · copie";
        foreach (var widget in board.Widgets) widget.Id = DashboardLayoutSettings.NewId();
        var result = DashboardLayoutSettings.AppendBoards(Settings.Dashboard.Layout, new() { ActiveBoardId = board.Id, Boards = new() { board } });
        AnalyticsPersistLayout(false, result.Layout); return Task.FromResult<object?>(new { saved = true, boardId = result.Layout.ActiveBoardId });
    }
    private void AnalyticsBusinessFocus(JsonElement args, List<DashboardScopeFilter> filters, CancellationToken ct)
    {
        string[] selected;
        if (args.TryGetProperty("focusKeys", out var choices))
        {
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() > 64)
                throw new InvalidDataException("Choisissez au maximum 64 équipes ou groupes.");
            selected = choices.EnumerateArray().Select(choice => choice.ValueKind == JsonValueKind.String ? choice.GetString()! : "").ToArray();
            if (selected.Any(choice => string.IsNullOrWhiteSpace(choice) || choice.Length > 1200))
                throw new InvalidDataException("Chaque sélection doit désigner une équipe ou un groupe du catalogue.");
        }
        else
        {
            var focus = Text(args, "focusKey", 1200);
            selected = focus.Length == 0 ? Array.Empty<string>() : new[] { focus };
        }
        if (selected.Length == 0) return;
        var settings = AnalyticsBusinessSettings(); var observed = BusinessDashboardTemplates.Observe(Result, settings, ct);
        var teams = BusinessDashboardTemplates.WeatherRules(settings, observed).Where(rule => rule.Kind == "team").ToDictionary(rule => rule.Id, StringComparer.Ordinal);
        var values = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var focus in selected.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<string> groups;
            if (focus.StartsWith("team:", StringComparison.Ordinal))
                groups = teams.TryGetValue(focus[5..], out var team) ? team.Values
                    : throw new InvalidDataException("Une équipe sélectionnée n’est plus dans les paramètres. Actualisez le choix du périmètre.");
            else if (focus.StartsWith("group:", StringComparison.Ordinal) && observed.Groups.Contains(focus[6..], StringComparer.Ordinal)) groups = new[] { focus[6..] };
            else throw new InvalidDataException("Un groupe sélectionné n’est plus dans le relevé. Actualisez le choix du périmètre.");
            foreach (var group in groups) if (seen.Add(group)) values.Add(group);
        }
        // One OR condition retains each original row once, including tickets
        // assigned to overlapping teams or to both a team and a selected group.
        if (values.Count > DashboardScopeFilter.MaximumGroupSelections)
            throw new InvalidDataException("Les équipes et groupes sélectionnés couvrent trop de groupes. Réduisez la sélection.");
        filters.Add(new() { Kind = "matrix-row", SourceKey = "group-status", Values = values });
    }
}
