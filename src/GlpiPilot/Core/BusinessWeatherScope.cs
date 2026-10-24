namespace GlpiPilot.Core;

public sealed record BusinessWeatherScope(string Mode, string Label, IReadOnlyList<string> SelectedKeys,
    string PrimaryRuleId, string HeroWidgetId, string Reason);
public sealed record BusinessWeatherContext(string Role, string RuleId, bool Applicable, bool RepresentedByHero, string Reason);

/// <summary>A transient presentation of stock weather. Stored rules and source row scopes stay unchanged.</summary>
public sealed class BusinessWeatherProjection
{
    public required DashboardBoardSettings Board { get; init; }
    public required BusinessWeatherScope Scope { get; init; }
    public Dictionary<string, BusinessWeatherContext> Contexts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DashboardVolumeWeather> Overrides { get; } = new(StringComparer.Ordinal);

    public static BusinessWeatherProjection Create(DashboardBoardSettings board, IReadOnlyList<BusinessWeatherRule> rules,
        DashboardBoardSettings ruleBoard, DashboardSnapshot baseline, DashboardSnapshot current,
        IReadOnlyDictionary<string, DashboardSnapshot> snapshots, IReadOnlyList<string> selectedKeys,
        IReadOnlyList<string> selectedGroups, bool otherScope, CancellationToken ct = default)
    {
        var copy = board.Copy();
        var selected = selectedKeys.Distinct(StringComparer.Ordinal).ToArray();
        var groups = selectedGroups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedTeams = selected.Where(key => key.StartsWith("team:", StringComparison.Ordinal)).Select(key => key[5..]).ToHashSet(StringComparer.Ordinal);
        var scoped = selected.Length > 0 || otherScope;
        DashboardWidgetSettings? Find(DashboardBoardSettings source, BusinessWeatherRule rule) => source.Widgets.FirstOrDefault(widget =>
            widget.Id == BusinessDashboardTemplates.WeatherWidgetId(rule.Id) || widget.Id == rule.Id);
        var ruleCards = rules.Select(rule => (Rule: rule, Card: Find(board, rule) ?? Find(ruleBoard, rule)))
            .Where(pair => pair.Card is not null).Select(pair => (pair.Rule, Card: pair.Card!)).ToArray();
        var identities = ruleCards.GroupBy(pair => pair.Card.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Rule, StringComparer.Ordinal);
        var hero = copy.Widgets.FirstOrDefault(widget => !widget.Hidden && !copy.Sections.Any(section => section.Key == widget.Section && section.Hidden)
            && widget.Kind == "metric" && widget.SourceKey == "active" && widget.Filters.Count == 0
            && (widget.Presentation == "weather-volume" || identities.GetValueOrDefault(widget.Id)?.Kind == "metric"));
        var matchingTeams = ruleCards.Where(pair => pair.Rule.Kind == "team" && groups.Count > 0 && groups.SetEquals(pair.Rule.Values)).ToArray();
        var primary = selectedTeams.Count == 1 ? matchingTeams.FirstOrDefault(pair => selectedTeams.Contains(pair.Rule.Id)) : default;
        if (primary.Rule is null) primary = matchingTeams.FirstOrDefault();

        int[]? FullRows(DashboardWidgetSettings widget)
        {
            ct.ThrowIfCancellationRequested();
            var metric = baseline.Metric(widget.SourceKey);
            if (metric?.Value is null) return null;
            try { return DashboardService.ResolveScope(baseline, widget.Filters, ct).Intersect(metric.Rows).Order().ToArray(); }
            catch (System.IO.InvalidDataException) { return null; } // An unavailable source column remains unknown.
        }
        var primaryRows = primary.Card is null ? null : FullRows(primary.Card);
        var heroRows = current.Metric("active");
        // First-team precedence is already encoded in each rule's exclusions.
        // Equal counts alone are not enough: every contributing original row must match.
        var exactTeam = scoped && !otherScope && primary.Rule?.SourceKey == "active" && primary.Card?.SourceKey == "active" && primaryRows is not null
            && heroRows?.Value is not null && primaryRows.ToHashSet().SetEquals(heroRows.Rows);
        var mode = !scoped ? "service" : exactTeam ? "team" : selected.Length > 1 ? "multiple" : "filtered";
        var label = exactTeam ? primary.Rule!.Label : !scoped ? "Service" : "Périmètre filtré";
        var reason = !scoped ? "" : exactTeam ? "Seuils de l’équipe « " + primary.Rule!.Label + " » ; les mêmes tickets actifs composent le périmètre et cette règle."
            : otherScope ? "Aucun seuil global n’est défini pour ce périmètre filtré. Les seuils du service ou d’une équipe entière ne sont pas appliqués à un sous-ensemble."
            : primary.Rule is not null ? "Le périmètre contient " + (heroRows?.Value?.ToString("G", System.Globalization.CultureInfo.CurrentCulture) ?? "un nombre inconnu de")
                + " tickets actifs ; la règle d’équipe en retient " + (primaryRows?.Length.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "un nombre inconnu")
                + " selon la priorité configurée. Ces ensembles de tickets diffèrent : la météo du total n’est pas évaluée avec les seuils de cette règle."
            : "Aucun seuil global n’est défini pour cette sélection. Les seuils des équipes ne sont ni additionnés ni appliqués au total cumulé.";
        var result = new BusinessWeatherProjection { Board = copy,
            Scope = new(mode, label, selected, primary.Rule?.Id ?? "", hero?.Id ?? "", reason) };
        void Neutral(DashboardWidgetSettings widget, string title, string explanation)
        {
            widget.WarningValue = null; widget.CriticalValue = null; widget.TargetValue = null;
            result.Overrides[widget.Id] = new("unknown", title, explanation);
        }
        foreach (var widget in copy.Widgets)
        {
            ct.ThrowIfCancellationRequested();
            var rule = identities.GetValueOrDefault(widget.Id);
            if (widget.Kind != "metric" || widget.Presentation != "weather-volume" && rule is null) continue;
            var role = widget.Id == hero?.Id ? "hero" : rule?.Kind == "team" ? "team" : "other";
            var applies = true; var represented = false; var explanation = "";
            if (role == "hero" && scoped)
            {
                explanation = reason;
                if (exactTeam)
                {
                    widget.Title = primary.Rule!.Label + " · tickets en cours";
                    widget.WarningValue = primary.Card!.WarningValue; widget.CriticalValue = primary.Card.CriticalValue;
                    widget.LowerIsBetter = primary.Card.LowerIsBetter; widget.Note = primary.Card.Note;
                    if (primary.Card.Presentation != "weather-volume")
                        Neutral(widget, "Météo désactivée", "La météo est désactivée pour cette équipe. Son volume reste disponible.");
                    widget.TargetValue = primary.Card.TargetValue;
                }
                else Neutral(widget, "Périmètre filtré · seuil global non défini", reason);
            }
            else if (role == "team")
            {
                var rows = snapshots.GetValueOrDefault(widget.Id)?.Metric(widget.SourceKey)?.Rows ?? Array.Empty<int>();
                var full = FullRows(board.Widgets.First(original => original.Id == widget.Id));
                applies = !scoped || exactTeam ? !scoped || rule!.Id == primary.Rule!.Id
                    : groups.Count == 0 || rule!.Values.Any(groups.Contains) || selectedTeams.Contains(rule.Id);
                if (scoped && !exactTeam && rows.Length == 0 && !selectedTeams.Contains(rule!.Id)) applies = false;
                if (!applies)
                {
                    explanation = "Cette équipe est hors du périmètre sélectionné ; son absence n’est pas évaluée comme un stock nul maîtrisé.";
                    Neutral(widget, "Hors périmètre", explanation);
                }
                else if (exactTeam && hero is { Presentation: "weather-volume" } && widget.Id != hero.Id)
                {
                    represented = !widget.ShowMiniHistory;
                    explanation = represented ? "Cette même équipe et ses seuils sont présentés dans la météo principale du périmètre."
                        : "Volume repris en tête ; historique de la règle conservé ici.";
                }
                else if (scoped && (otherScope || full is null || !full.ToHashSet().SetEquals(rows)))
                {
                    explanation = "Cette carte présente une partie de l’équipe selon les filtres et la priorité configurée. Les seuils de l’équipe entière ne sont pas appliqués à cette partie.";
                    Neutral(widget, "Équipe · périmètre partiel", explanation);
                }
                else if (scoped) explanation = "Météo de cette équipe entière selon sa priorité et ses propres seuils ; elle ne qualifie pas le total cumulé.";
            }
            else if (scoped && widget.Presentation == "weather-volume")
            {
                explanation = "Le volume est calculé dans le périmètre sélectionné. Les seuils configurés pour l’indicateur complet ne sont pas réutilisés pour ce sous-ensemble.";
                Neutral(widget, "Périmètre filtré · seuil spécifique non défini", explanation);
            }
            result.Contexts[widget.Id] = new(role, role == "hero" && exactTeam ? primary.Rule!.Id : rule?.Id ?? "", applies, represented, explanation);
        }
        return result;
    }
}
