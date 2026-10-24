using System.Globalization;
using System.Text;

namespace GlpiPilot.Core;

public sealed record BusinessDashboardView(string Key, string Title, string Description);
public sealed record BusinessDashboardValues(IReadOnlyList<string> Groups, IReadOnlyList<string> Categories);
public sealed record BusinessWeatherThresholdPreset(string Kind, string SourceKey, double OrangeAbove, double RedAbove);

/// <summary>Generic native views. Local profiles take precedence over editable starter thresholds.</summary>
public static partial class BusinessDashboardTemplates
{
    // These are starting points for stock counts, not capacity or SLA targets.
    // Apply them only while constructing an unconfigured native profile. A saved
    // rule with null or zero thresholds is an explicit choice and stays intact.
    public static IReadOnlyList<BusinessWeatherThresholdPreset> WeatherThresholdPresets { get; } = new[]
    {
        new BusinessWeatherThresholdPreset("metric", "active", 100, 200),
        new("metric", "active-incidents", 50, 100), new("metric", "active-requests", 50, 100),
        new("type", "active", 50, 100), new("category", "active", 10, 20), new("team", "active", 10, 20),
        new("metric", "pending", 10, 20), new("metric", "without-group", 10, 20), new("metric", "without-technician", 10, 20),
        new("metric", "active-multiple-groups", 10, 20), new("metric", "active-no-author", 10, 20),
        new("metric", "high", 10, 20), new("metric", "old", 10, 20), new("metric", "stale", 10, 20)
    };

    public static bool UsesDefaultWeatherThresholds(DashboardSettings settings) => settings.Business.WeatherRules.Count == 0
        && !settings.Business.BoardOverrides.Any(board => board.Id == "business-weather") && ExistingWeather(settings.Layout) is null;

    public static readonly BusinessDashboardView[] Views =
    {
        new("workspace", "Pilotage", "La météo, les risques et l’activité du service dans un périmètre commun."),
        new("weather", "Météo", "Stock, types, catégories et équipes : les volumes du relevé, leurs seuils et les actions du service."),
        new("pilotage", "Pilotage", "Chiffres clés des incidents et demandes, statut, affectation et ancienneté."),
        new("groups", "Groupes", "Stock actif de chaque groupe, avec les quatre statuts et leurs zéros connus."),
        new("categories", "Catégories", "Répartition et statuts par catégorie, avec accès aux sous-catégories."),
        new("oldest", "Plus anciens", "Cinq plus anciens par groupe et dix dans le périmètre, avec les dates à vérifier."),
        new("followup", "Suivi agents", "Comparaison des tickets rédigés et attribués, pour toutes les personnes ou une équipe."),
        new("participants", "Agents actifs", "Suivis, tâches, intersection et union des participations visibles dans les tickets actifs."),
        new("sites", "Sites", "Stock par site, identifiants et préconisations de préparation des interventions.")
    };

    public static BusinessDashboardValues Observe(ExtractionResult? source, DashboardSettings settings, CancellationToken ct = default)
    {
        if (source is null) return new(Array.Empty<string>(), Array.Empty<string>());
        var columns = DashboardService.ResolveColumns(source.Headers, settings);
        IReadOnlyList<string> Values(string role, bool split)
        {
            if (columns.FirstOrDefault(column => column.Role == role)?.Index is not int column) return Array.Empty<string>();
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < source.Rows.Count; index++)
            {
                if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
                var row = source.Rows[index]; if (column >= row.Length) continue;
                var raw = DashboardService.NormalizeValue(row[column], role, source.Headers[column], settings);
                foreach (var value in split ? DashboardService.SplitTechnicianNames(raw, settings.MultiValueSeparators) : new[] { raw })
                    if (!string.IsNullOrWhiteSpace(value) && value.Length <= 1000 && Clean(value) is not ("non renseigne" or "aucun" or "aucune" or "null" or "none")) values.Add(value.Trim());
            }
            return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(2000).ToArray();
        }
        return new(Values("group", true), Values("category", false));
    }

    public static List<BusinessWeatherRule> WeatherRules(DashboardSettings settings, BusinessDashboardValues observed)
    {
        if (settings.Business.WeatherRules.Count > 0) return settings.Business.WeatherRules.Select(rule => rule.Copy()).ToList();
        var existing = ExistingWeather(settings.Layout);
        if (existing is not null)
        {
            var result = new List<BusinessWeatherRule>();
            foreach (var widget in existing.Widgets.Where(widget => widget.Kind == "metric" && widget.Presentation == "weather-volume"))
            {
                var filter = widget.Filters.FirstOrDefault(filter => !filter.Exclude && filter.Kind is "matrix-row" or "distribution");
                var kind = filter?.SourceKey switch { "group-status" or "active-group" => "team", "category" or "active-category" => "category", "type" or "active-type" => "type", _ => "metric" };
                result.Add(new() { Id = widget.Id, Label = widget.Title.Length > 0 ? widget.Title : widget.SourceKey, Kind = kind, SourceKey = widget.SourceKey,
                    Values = kind == "metric" ? new() : filter!.Selections().ToList(), OrangeAbove = widget.WarningValue, RedAbove = widget.CriticalValue,
                    Visible = !widget.Hidden, WeatherEnabled = true, IncludeDescendants = filter?.IncludeDescendants == true, Note = widget.Note });
            }
            return result;
        }
        var rules = new List<BusinessWeatherRule>
        {
            new() { Id = "native-stock", Label = "Tickets en cours", SourceKey = "active", Note = "Incidents et demandes · attente et planification incluses." },
            new() { Id = "native-incidents", Label = "Incidents en cours", SourceKey = "active-incidents" },
            new() { Id = "native-requests", Label = "Demandes en cours", SourceKey = "active-requests" }
        };
        foreach (var category in observed.Categories.Select(value => value.Split('>')[0].Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
            rules.Add(new() { Id = StableId("category", category), Label = category, Kind = "category", Values = new() { category }, IncludeDescendants = true });
        foreach (var group in observed.Groups.Take(18))
            rules.Add(new() { Id = StableId("team", group), Label = group, Kind = "team", Values = new() { group } });
        foreach (var rule in rules)
        {
            var preset = WeatherThresholdPresets.Single(preset => preset.Kind == rule.Kind && preset.SourceKey == rule.SourceKey);
            rule.OrangeAbove = preset.OrangeAbove; rule.RedAbove = preset.RedAbove;
        }
        return rules;
    }

    public static BusinessDashboardSettings Profile(DashboardSettings settings, BusinessDashboardValues observed)
    {
        var profile = settings.Business.Copy(); var legacy = profile.WeatherRules.Count == 0 ? ExistingWeather(settings.Layout) : null;
        profile.WeatherRules = WeatherRules(settings, observed);
        if (legacy is not null)
        {
            if (profile.Notes.Length == 0) profile.Notes = string.Join("\n\n", legacy.Widgets.Where(widget => widget.Kind == "note" && !widget.Hidden && widget.Note.Length > 0).Select(widget => widget.Note).Distinct());
            if (profile.Notes.Length > 4000) profile.Notes = profile.Notes[..4000];
            if (profile.FreshnessWarningHours == 48) profile.FreshnessWarningHours = legacy.FreshnessWarningHours;
        }
        return profile;
    }

    public static DashboardBoardSettings Create(string key, DashboardSettings settings, BusinessDashboardValues observed)
    {
        if (key == "workspace") return Workspace(settings, observed);
        var view = Views.FirstOrDefault(view => view.Key == key) ?? throw new System.IO.InvalidDataException("Rubrique de pilotage inconnue.");
        if (settings.Business.BoardOverrides.FirstOrDefault(board => board.Id == "business-" + key) is { } saved)
        {
            var copy = saved.Copy();
            // Upgrade only the two original native summary cards. Keep custom
            // municipality probes, labels, filters and presentation untouched.
            if (key == "sites")
                foreach (var widget in copy.Widgets.Where(widget => widget.Kind == "metric"))
                {
                    if (widget.Id == "business-sites-1" && widget.SourceKey == "city-matched") widget.SourceKey = "location-matched";
                    if (widget.Id == "business-sites-2" && widget.SourceKey == "city-unmatched") widget.SourceKey = "location-review";
                }
            return copy;
        }
        var board = new DashboardBoardSettings { Id = "business-" + key, Name = view.Title, Description = view.Description, HideUnavailableWidgets = false };
        void Section(string id, string title, string description = "", string accent = "blue") => board.Sections.Add(new() { Key = id, Title = title, Description = description, Accent = accent });
        DashboardWidgetSettings Add(string kind, string source, string title, string section, int span = 4, int height = 260, string accent = "blue")
        {
            var widget = new DashboardWidgetSettings { Id = $"business-{key}-{board.Widgets.Count}", Kind = kind, SourceKey = source, Title = title, Section = section,
                Span = span, Height = height, AutoHeight = true, Accent = accent, ShowMiniHistory = false, TopCount = 30 };
            board.Widgets.Add(widget); return widget;
        }
        void Metrics(string section, params (string Key, string Label)[] metrics)
        { foreach (var metric in metrics) Add("metric", metric.Key, metric.Label, section); }
        DashboardWidgetSettings Distribution(string source, string title, string section, int span = 6, string presentation = "bars")
        { var widget = Add("distribution", source, title, section, span, 360); widget.Presentation = presentation; return widget; }
        void Matrix(string source, string title, string section)
        { var widget = Add("matrix", source, title, section, 12, 440); widget.Presentation = "heatmap"; }
        void Note(string title, string text, string section)
        { var widget = Add("note", "", title, section, 12, 160, "teal"); widget.Note = text; }
        if (key == "weather")
        {
            var profile = Profile(settings, observed); board.FreshnessWarningHours = profile.FreshnessWarningHours;
            Section("stock", "Stock du service", "Actifs : nouveau, en cours attribué ou planifié, en attente.");
            Section("categories", "Météo des catégories", "Une catégorie peut inclure ses sous-catégories séparées par >.", "purple");
            Section("teams", "Météo des équipes", "Première équipe correspondante dans l’ordre des paramètres ; une équipe masquée conserve sa priorité.", "teal");
            Section("context", "Points de vigilance", "Les indicateurs complètent les volumes météo.", "orange");
            Section("actions", "Actualité et actions", "Les informations utiles pour expliquer la situation du service.", "teal");
            var previous = new List<string>(); var rules = profile.WeatherRules;
            foreach (var rule in rules)
            {
                var section = rule.Kind switch { "team" => "teams", "category" => "categories", _ => "stock" };
                var widget = Add("metric", rule.SourceKey, rule.Label, section, rule.Kind == "metric" && rule.SourceKey == "active" ? 12 : 3, 260, rule.Kind == "category" ? "purple" : rule.Kind == "team" ? "teal" : "blue");
                widget.Id = "weather-" + StableId("rule", rule.Id); widget.Hidden = !rule.Visible; widget.Presentation = rule.WeatherEnabled ? "weather-volume" : "compact";
                widget.WarningValue = rule.WeatherEnabled ? rule.OrangeAbove : null; widget.CriticalValue = rule.WeatherEnabled ? rule.RedAbove : null; widget.Note = rule.Note;
                if (rule.Kind != "metric") widget.Filters.Add(new() { Kind = rule.Kind == "team" ? "matrix-row" : "distribution",
                    SourceKey = rule.Kind switch { "team" => "group-status", "category" => "active-category", _ => "active-type" },
                    Values = new(rule.Values), IncludeDescendants = rule.Kind == "category" && rule.IncludeDescendants });
                if (rule.Kind == "team")
                {
                    foreach (var chunk in previous.Distinct(StringComparer.Ordinal).Chunk(32)) widget.Filters.Add(new() { Kind = "matrix-row", SourceKey = "group-status", Values = chunk.ToList(), Exclude = true });
                    previous.AddRange(rule.Values);
                }
            }
            if (!rules.Any(rule => rule.Kind == "category")) Note("Catégories du relevé", "Les catégories apparaissent dès qu’elles sont présentes dans le relevé. Vous pouvez aussi définir leurs racines dans Paramètres du pilotage.", "categories");
            if (!rules.Any(rule => rule.Kind == "team")) Note("Équipes du relevé", "Les groupes du relevé alimentent cette section. Paramètres du pilotage permet de les réunir en équipes et de régler leur ordre.", "teams");
            Metrics("context", ("pending", "En attente"), ("without-group", "Sans groupe"), ("active-multiple-groups", "Plusieurs groupes"),
                ("active-no-author", "Sans rédacteur visible"), ("high", "Priorités hautes"), ("old", "Tickets anciens"));
            Note("Analyse et actions en cours", profile.Notes.Length > 0 ? profile.Notes : "Renseignez les faits marquants et les actions prévues dans Paramètres du pilotage.", "actions");
        }
        else if (key == "pilotage")
        {
            Section("stock", "Chiffres clés"); Section("status", "Répartition par statut", accent: "teal");
            Section("distribution", "Groupes, catégories et types", "Les totaux par groupe et par rédacteur peuvent dépasser le stock : une occurrence peut appartenir à plusieurs groupes ou personnes.", "purple");
            Section("quality", "Affectations et qualité", accent: "orange");
            Metrics("stock", ("active", "Tickets en cours"), ("active-incidents", "Incidents"), ("active-requests", "Demandes"), ("without-group", "Sans groupe"));
            foreach (var widget in board.Widgets) widget.Span = 3;
            Add("metric", "new", "Nouveaux", "status", 3);
            foreach (var status in new[] { 2, 3 })
            {
                var widget = Add("metric", "active", status == 2 ? "En cours · attribués" : "En cours · planifiés", "status", 3);
                widget.Filters.Add(new() { Kind = "distribution", SourceKey = "status", Value = DashboardService.StatusLabel(status) });
            }
            Add("metric", "pending", "En attente", "status", 3);
            var groups = Add("matrix", "group-status", "10 premiers groupes par statut", "distribution", 6, 440); groups.Presentation = "stacked"; groups.StackedMode = "volume"; groups.TopCount = 10;
            Distribution("active-category", "8 principales catégories", "distribution").TopCount = 8;
            var types = Add("matrix", "type-status", "Types et statuts", "distribution", 12, 300); types.Presentation = "stacked"; types.StackedMode = "volume";
            Matrix("type-status", "Types × statuts actifs · tableau", "distribution"); Distribution("active-priority", "Priorités actives", "quality", 12);
            Metrics("quality", ("without-technician", "Sans technicien"), ("active-multiple-groups", "Plusieurs groupes"), ("active-no-author", "Sans rédacteur visible"), ("age-median", "Âge médian"), ("age-p90", "Âge au 90e percentile"));
            Add("quality", "", "Champs et qualité du relevé", "quality", 12, 360, "slate");
        }
        else if (key == "groups")
        {
            Section("stock", "Affectations actives"); Section("groups", "Groupes × statuts", "Une ligne multigroupe contribue à chaque groupe ; le stock global reste distinct.", "teal");
            Metrics("stock", ("active", "Stock actif"), ("without-group", "Sans groupe"), ("active-multiple-groups", "Plusieurs groupes"));
            Matrix("group-status", "Stock actif par groupe", "groups");
        }
        else if (key == "categories")
        {
            Section("stock", "Couverture des catégories"); Section("categories", "Catégories et statuts", accent: "purple");
            Metrics("stock", ("active", "Stock actif"), ("active-no-category", "Actifs sans catégorie"), ("missing-category", "Lignes sans catégorie"));
            Distribution("active-category", "Catégories actives", "categories", 12); Matrix("category-status", "Catégories × statuts du relevé", "categories");
        }
        else if (key == "oldest")
        {
            Section("age", "Ancienneté et dates"); Section("oldest", "Tickets à examiner", "Ouverture croissante puis ID numérique ; les dates inconnues ou futures restent dans le stock.", "orange");
            Metrics("age", ("active", "Stock actif"), ("age-median", "Âge médian"), ("age-p90", "Âge au 90e percentile"));
            Distribution("age", "Tranches d’ancienneté", "age"); Distribution("inactivity", "Dernière modification", "age");
            Add("oldest", "groups", "Les cinq plus anciens par groupe", "oldest", 12, 640, "orange");
            Add("oldest", "tickets", "Les dix plus anciens du périmètre", "oldest", 12, 440, "orange");
            Section("categories", "Catégories du périmètre", "Choisissez une équipe ou un groupe pour retrouver son analyse détaillée.", "purple");
            Distribution("active-category", "12 principales catégories du périmètre", "categories", 12).TopCount = 12;
        }
        else if (key is "followup" or "participants")
        {
            Section("stock", "Périmètre actif"); Section("people", key == "followup" ? "Rédaction et attribution" : "Participations des agents",
                "Une personne compte une fois par ticket et par rôle. L’attribution est indépendante de la rédaction.", "teal");
            Metrics("stock", ("active", "Stock actif"), ("active-no-author", "Sans rédacteur visible"), ("without-technician", "Sans technicien"));
            var source = key == "followup" ? "comparison" : "authors";
            Add("participation", source, key == "followup" ? "Tickets rédigés et tickets attribués" : "Suivis, tâches, intersection et union", "people", 12, 520, "teal");
            var configured = settings.Layout.Boards.SelectMany(original => original.Widgets.Where(widget => widget.Kind == "participation" && widget.SourceKey == source)
                .Select(widget => (Board: original, Widget: widget))).Take(12).ToArray();
            foreach (var item in configured)
            {
                var copy = item.Widget.Copy(); copy.Id = "business-configured-" + StableId("widget", item.Board.Id + item.Widget.Id); copy.Section = "people";
                copy.Title = item.Widget.Title.Length > 0 ? item.Widget.Title : item.Board.Name;
                copy.Filters = DashboardScopeFilter.CopyList(item.Board.Filters.Concat(copy.Filters));
                if (copy.Filters.Count <= 12) board.Widgets.Add(copy);
            }
            Note("Lire les comptes", key == "followup" ? "Les tickets rédigés réunissent suivis et tâches. Un ticket peut aussi être attribué à la même personne : les deux colonnes ne s’additionnent pas."
                : "Union = suivis + tâches − intersection. Ces comptes décrivent des participations visibles ; ils ne mesurent ni le nombre d’actions ni la productivité.", "people");
        }
        else if (key == "sites")
        {
            board.Filters.Add(new() { Kind = "metric", SourceKey = "active" });
            Section("stock", "Stock et localisation"); Section("sites", "Sites et interventions", "Les sites à zéro et les tickets sans site reconnu restent visibles.", "teal");
            Metrics("stock", ("active", "Stock actif"), ("location-matched", "Localisation reconnue"), ("location-review", "Localisation à vérifier"));
            var widget = Add("sites", "sites", "Stock et préconisations par site", "sites", 12, 640, "teal"); widget.WarningValue = settings.Business.SiteInterventionThreshold;
            Distribution("site-reference", "Répartition par site", "sites", 12, "donut");
            Section("focus", "Catégories et ancienneté du périmètre", "Le filtre d’équipe ou de groupe s’applique aussi aux sites, aux catégories et à la liste complète des tickets.", "purple");
            Distribution("active-category", "12 principales catégories du périmètre", "focus", 12).TopCount = 12;
            Add("oldest", "tickets", "Les dix plus anciens du périmètre", "focus", 12, 440, "orange");
        }
        board.Normalize(); new DashboardLayoutSettings { ActiveBoardId = board.Id, Boards = new() { board } }.Validate(); return board;
    }

    private static DashboardBoardSettings? ExistingWeather(DashboardLayoutSettings layout) => layout.Boards
        .Where(board => board.Widgets.Count(widget => widget.Kind == "metric" && widget.Presentation == "weather-volume") >= 2)
        .OrderByDescending(board => board.Widgets.Count(widget => widget.Kind == "metric" && widget.Presentation == "weather-volume")).FirstOrDefault();
    private static string StableId(string prefix, string value) => prefix + "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20].ToLowerInvariant();
    private static string Clean(string text) => string.Concat(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant().Trim();
}
