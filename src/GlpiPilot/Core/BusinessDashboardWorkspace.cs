using System.Text.Json;

namespace GlpiPilot.Core;

public static partial class BusinessDashboardTemplates
{
    /// <summary>One analytical scope, composed by use rather than by former page.</summary>
    private static DashboardBoardSettings Workspace(DashboardSettings settings, BusinessDashboardValues observed)
    {
        if (settings.Business.BoardOverrides.FirstOrDefault(board => board.Id == "business-workspace") is { } saved)
            return saved.Copy();

        var profile = Profile(settings, observed);
        var result = new DashboardBoardSettings
        {
            Id = "business-workspace", Name = "Pilotage du service", HideUnavailableWidgets = false,
            Description = "Météo, activité, répartition, charge et priorités sur un même périmètre.",
            FreshnessWarningHours = profile.FreshnessWarningHours,
            Sections = new()
            {
                new() { Key = "weather", Title = "La météo du service", Description = "Tous les volumes et seuils du profil, avec leurs équipes et catégories.", Accent = "blue" },
                new() { Key = "flow", Title = "Comprendre le stock", Description = "Incidents, demandes et étapes de traitement.", Accent = "teal" },
                new() { Key = "attention", Title = "Ce qui demande une action", Description = "Affectations à compléter, ancienneté, inactivité et tickets prioritaires.", Accent = "orange" },
                new() { Key = "teams", Title = "Répartir le travail", Description = "Les affectations observées par groupe et par statut.", Accent = "teal" },
                new() { Key = "analysis", Title = "Comprendre les demandes", Description = "Catégories et priorités, du volume au détail des tickets.", Accent = "purple" },
                new() { Key = "people", Title = "Suivre la contribution", Description = "Rédaction, attribution, suivis et tâches. Ces nombres ne mesurent pas la présence.", Accent = "blue" },
                new() { Key = "actions", Title = "Décisions et contexte", Description = "Faits marquants et actions en cours.", Accent = "teal" }
            }
        };
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        void Add(DashboardBoardSettings source, DashboardWidgetSettings original, string section, bool preserve = false)
        {
            var widget = original.Copy();
            widget.Filters = DashboardScopeFilter.CopyList(source.Filters.Concat(widget.Filters));
            widget.Hidden |= source.Sections.Any(item => item.Key == original.Section && item.Hidden);
            var signature = JsonSerializer.Serialize(new { widget.Kind, widget.SourceKey, widget.Presentation, widget.Filters, widget.ParticipationRoster, widget.Note });
            if (!preserve && !signatures.Add(signature)) return;
            if (preserve) signatures.Add(signature);
            widget.Section = section;
            if (result.Widgets.Any(existing => existing.Id == widget.Id))
                widget.Id = "workspace-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.Id + ":" + widget.Id)))[..32];
            if (widget.Kind == "metric" && widget.Presentation != "weather-volume") widget.Span = 3;
            result.Widgets.Add(widget);
        }

        // Every configured weather card survives the redesign, including its
        // thresholds, first-team precedence, note and deliberately hidden state.
        var weather = Create("weather", settings, observed);
        foreach (var widget in weather.Widgets)
            Add(weather, widget, widget.Section == "context" ? "attention" : widget.Section == "actions" ? "actions" : "weather", true);

        var native = settings.Copy(); native.Business.BoardOverrides.Clear();
        foreach (var key in new[] { "pilotage", "groups", "categories", "oldest", "followup", "participants" })
        {
            var source = Create(key, native, observed);
            foreach (var widget in source.Widgets)
            {
                // Keep the highlighted top-group and category charts alongside
                // the complete matrices; drilldowns always reach all values.
                if (key == "oldest" && widget.Section == "categories") continue;
                var section = key switch
                {
                    "groups" => "teams",
                    "categories" => "analysis",
                    "oldest" => "attention",
                    "followup" or "participants" => "people",
                    _ => widget.Section switch { "quality" => "attention", "distribution" => "flow", _ => "flow" }
                };
                Add(source, widget, section);
            }
        }
        result.Widgets.Add(new() { Id = "workspace-attention", Kind = "attention", SourceKey = "", Title = "Tickets à prendre en charge", Section = "attention", Span = 12, Height = 400, AutoHeight = true, Accent = "orange" });
        result.Widgets.Add(new() { Id = "workspace-stock-history", Kind = "history", SourceKey = "active", Title = "Évolution observée du stock", Section = "flow", Span = 12, Height = 280, AutoHeight = true, Accent = "teal", ShowMiniHistory = false });
        var layout = new DashboardLayoutSettings { ActiveBoardId = result.Id, Boards = new() { result } };
        // Validate before normalization: never silently drop a configured card.
        layout.Validate();
        return result;
    }
}
