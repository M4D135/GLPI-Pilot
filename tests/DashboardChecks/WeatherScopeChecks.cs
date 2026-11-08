using System.Text.Json;
using GlpiPilot.Core;

internal static class WeatherScopeChecks
{
    public static void Register(Action<string, Action> test, Action<bool, string> check)
    {
        static DashboardSettings Settings()
        {
            var settings = new DashboardSettings(); settings.Normalize();
            settings.Business.WeatherRules = new()
            {
                new() { Id = "stock", Label = "Stock du service", OrangeAbove = 100, RedAbove = 200 },
                new() { Id = "support", Label = "Équipe Support", Kind = "team", Values = new() { "Support" }, OrangeAbove = 10, RedAbove = 20 },
                new() { Id = "network", Label = "Équipe Réseau", Kind = "team", Values = new() { "Réseau" }, OrangeAbove = 5, RedAbove = 10 },
                new() { Id = "other", Label = "Équipe Autre", Kind = "team", Values = new() { "Autre" }, OrangeAbove = 3, RedAbove = 6 },
                new() { Id = "empty", Label = "Équipe Vide", Kind = "team", Values = new() { "Vide" }, OrangeAbove = 3, RedAbove = 6 }
            };
            return settings;
        }
        var source = new ExtractionResult { Headers = new() { "ID", "Titre", "Statut", "Attribué à - Groupe de techniciens", "Attribué à - Technicien" },
            Complete = true, ExpectedTotal = 104, ExtractedAt = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), SourceUrl = "https://glpi.example.test/front/ticket.php" };
        for (var index = 0; index < 104; index++) source.Rows.Add(new[] { (index + 1).ToString(), "Ticket fictif " + (index + 1), index >= 100 ? "Clos" : "Nouveau",
            index < 6 ? "Support;Réseau" : index < 84 || index >= 100 ? "Support" : index < 96 ? "Réseau" : "Autre", index < 42 ? "Agent A" : "Agent B" });
        var heroId = BusinessDashboardTemplates.WeatherWidgetId("stock");
        string TeamId(string id) => BusinessDashboardTemplates.WeatherWidgetId(id);
        (BusinessWeatherProjection Projection, Dictionary<string, DashboardSnapshot> Widgets, DashboardBoardSettings Board) Project(
            DashboardSettings settings, string[] keys, string[] groups, bool other = false, Action<DashboardBoardSettings>? edit = null, int[]? scopeRows = null)
        {
            var observed = BusinessDashboardTemplates.Observe(source, settings);
            var ruleBoard = BusinessDashboardTemplates.Create("weather", settings, observed); var board = ruleBoard.Copy(); edit?.Invoke(board);
            var baseline = DashboardService.Calculate(source, settings, source.ExtractedAt);
            var filters = groups.Length == 0 ? new List<DashboardScopeFilter>() : new() { new() { Kind = "matrix-row", SourceKey = "group-status", Values = groups.ToList() } };
            var rows = scopeRows ?? DashboardService.ResolveScope(baseline, filters);
            if (other) rows = rows.Where(index => index < 42).ToArray();
            var current = DashboardService.CalculateSubset(source, settings, rows, "Périmètre de test", source.ExtractedAt);
            var widgets = board.Widgets.Where(widget => !widget.Hidden).ToDictionary(widget => widget.Id, widget => widget.Filters.Count == 0 ? current
                : DashboardService.CalculateSubset(source, settings, DashboardService.ResolveScope(baseline, widget.Filters).Intersect(rows).ToArray(), "Carte de test", source.ExtractedAt));
            return (BusinessWeatherProjection.Create(board, settings.Business.WeatherRules, ruleBoard, baseline, current, widgets, keys, groups, other), widgets, board);
        }
        DashboardVolumeWeather Weather(BusinessWeatherProjection projection, Dictionary<string, DashboardSnapshot> widgets, string id)
        {
            var card = projection.Board.Widgets.Single(widget => widget.Id == id);
            return projection.Overrides.GetValueOrDefault(id) ?? card.EvaluateVolumeWeather(widgets[id].Metric(card.SourceKey)?.Value, true);
        }
        test("One exact team projects its own weather thresholds without changing the stored layout or original row scope", () =>
        {
            var settings = Settings(); var before = JsonSerializer.Serialize(settings);
            var (projection, widgets, original) = Project(settings, new[] { "team:support" }, new[] { "Support" }, edit: board => { board.Widgets[0].Height = 420; board.Widgets[0].Title = "Mon stock"; });
            var hero = projection.Board.Widgets.Single(widget => widget.Id == heroId);
            check(projection.Scope.Mode == "team" && projection.Scope.PrimaryRuleId == "support" && hero.WarningValue == 10 && hero.CriticalValue == 20
                && hero.Height == 420 && Weather(projection, widgets, heroId).State == "rain" && widgets[heroId].Metric("active")!.Value == 84, "The 84-ticket hero uses the selected team's 10/20 thresholds.");
            check(projection.Contexts[TeamId("support")].RepresentedByHero && !projection.Contexts[TeamId("network")].Applicable
                && Weather(projection, widgets, TeamId("network")).State == "unknown", "The identical team is identified once and an outside team is not a healthy zero.");
            check(JsonSerializer.Serialize(settings) == before && original.Widgets[0].WarningValue == 100 && original.Widgets[0].Title == "Mon stock", "Projection does not mutate profile, original title, geometry or service thresholds.");
        });
        test("Equivalent group aliases and redundant team-plus-group choices resolve to one exact weather scope", () =>
        {
            foreach (var keys in new[] { new[] { "group:Support" }, new[] { "team:support", "group:Support" } })
            {
                var (projection, widgets, _) = Project(Settings(), keys, new[] { "Support" });
                check(projection.Scope.Mode == "team" && projection.Scope.PrimaryRuleId == "support" && Weather(projection, widgets, heroId).State == "rain", "Aliases preserve the team's exact threshold source.");
            }
        });
        test("Cumulative weather keeps a neutral union total and evaluates only the complete selected teams", () =>
        {
            var (projection, widgets, _) = Project(Settings(), new[] { "team:support", "team:network" }, new[] { "Support", "Réseau" });
            var hero = projection.Board.Widgets.Single(widget => widget.Id == heroId);
            check(projection.Scope.Mode == "multiple" && hero.WarningValue is null && hero.CriticalValue is null && Weather(projection, widgets, heroId).State == "unknown"
                && widgets[heroId].Metric("active")!.Value == 96, "No service or summed threshold is applied to the 96 unique rows.");
            check(Weather(projection, widgets, TeamId("support")).State == "rain" && widgets[TeamId("support")].Metric("active")!.Value == 84
                && Weather(projection, widgets, TeamId("network")).State == "rain" && widgets[TeamId("network")].Metric("active")!.Value == 12
                && !projection.Contexts[TeamId("other")].Applicable && !projection.Contexts[TeamId("support")].RepresentedByHero, "Priority is retained per team and no team represents the union hero.");
        });
        test("Hidden earlier team priority never transfers its excluded rows to the selected team's hero", () =>
        {
            var settings = Settings(); settings.Business.WeatherRules[1].Visible = false;
            var (projection, widgets, _) = Project(settings, new[] { "team:network" }, new[] { "Réseau" });
            check(projection.Scope.Mode == "filtered" && Weather(projection, widgets, heroId).State == "unknown" && projection.Scope.Reason.Contains("priorité")
                && widgets[heroId].Metric("active")!.Rows.Length == 18 && widgets[TeamId("network")].Metric("active")!.Rows.Length == 12
                && !projection.Contexts[TeamId("network")].RepresentedByHero, "The 18-row selected group and the 12-row exclusive team remain distinct and explained.");
        });
        test("Technician and cross-filter subsets keep volumes without reusing entire-team or service thresholds", () =>
        {
            var (projection, widgets, _) = Project(Settings(), new[] { "team:support" }, new[] { "Support" }, other: true);
            check(projection.Scope.Mode == "filtered" && widgets[heroId].Metric("active")!.Value == 42 && Weather(projection, widgets, heroId).State == "unknown"
                && Weather(projection, widgets, TeamId("support")).State == "unknown" && projection.Board.Widgets.Single(widget => widget.Id == TeamId("support")).WarningValue is null,
                "A 42-ticket subset does not inherit thresholds defined for all 84 team tickets.");
        });
        test("Blank zero disabled and customized team thresholds survive scope projection exactly", () =>
        {
            foreach (var pair in new[] { (Orange: (double?)null, Red: (double?)null, Enabled: true), (Orange: (double?)0, Red: (double?)0, Enabled: true),
                (Orange: (double?)82, Red: (double?)84, Enabled: true), (Orange: (double?)1, Red: (double?)2, Enabled: false) })
            {
                var settings = Settings(); var team = settings.Business.WeatherRules[1]; team.OrangeAbove = pair.Orange; team.RedAbove = pair.Red; team.WeatherEnabled = pair.Enabled;
                var (projection, widgets, _) = Project(settings, new[] { "team:support" }, new[] { "Support" }); var hero = projection.Board.Widgets.Single(widget => widget.Id == heroId);
                check(hero.Presentation == "weather-volume" && hero.WarningValue == (pair.Enabled ? pair.Orange : null) && hero.CriticalValue == (pair.Enabled ? pair.Red : null)
                    && Weather(projection, widgets, heroId).State == (!pair.Enabled || pair.Orange is null ? "unknown" : pair.Red == 0 ? "rain" : "watch"), "The effective pair, strict equality boundary and disabled state are preserved.");
            }
        });
        test("A missing hidden or compact hero never suppresses the only visible team weather", () =>
        {
            foreach (var edit in new Action<DashboardBoardSettings>[] { board => board.Widgets.RemoveAll(widget => widget.Id == heroId),
                board => board.Widgets.Single(widget => widget.Id == heroId).Hidden = true,
                board => board.Sections.Single(section => section.Key == "stock").Hidden = true,
                board => board.Widgets.Single(widget => widget.Id == heroId).Presentation = "compact" })
            {
                var (projection, _, _) = Project(Settings(), new[] { "team:support" }, new[] { "Support" }, edit: edit);
                check(!projection.Contexts[TeamId("support")].RepresentedByHero, "Only an actually displayed volume-weather hero may represent the team card.");
            }
        });
        test("Equal volume with different original rows never qualifies as the same team weather", () =>
        {
            var (projection, widgets, _) = Project(Settings(), new[] { "team:support" }, new[] { "Support" }, scopeRows: Enumerable.Range(1, 84).ToArray());
            check(widgets[heroId].Metric("active")!.Value == 84 && projection.Scope.Mode == "filtered" && Weather(projection, widgets, heroId).State == "unknown"
                && !projection.Contexts[TeamId("support")].RepresentedByHero, "Matching 84-ticket counts do not replace original-row equality.");
        });
        test("Team targets and independent mini histories are preserved without reusing a global target on a subset", () =>
        {
            void Customize(DashboardBoardSettings board)
            {
                board.Widgets.Single(widget => widget.Id == heroId).TargetValue = 300;
                var team = board.Widgets.Single(widget => widget.Id == TeamId("support")); team.TargetValue = 24; team.ShowMiniHistory = true; team.HistoryPeriodDays = 30;
            }
            var (teamScope, _, original) = Project(Settings(), new[] { "team:support" }, new[] { "Support" }, edit: Customize);
            check(teamScope.Board.Widgets.Single(widget => widget.Id == heroId).TargetValue == 24
                && !teamScope.Contexts[TeamId("support")].RepresentedByHero
                && teamScope.Contexts[TeamId("support")].Reason.Contains("historique")
                && teamScope.Board.Widgets.Single(widget => widget.Id == TeamId("support")).HistoryPeriodDays == 30
                && original.Widgets.Single(widget => widget.Id == heroId).TargetValue == 300,
                "The exact hero adopts the team target while the team's own historical scope remains available and stored settings stay intact.");
            var (filtered, _, _) = Project(Settings(), new[] { "team:support", "team:network" }, new[] { "Support", "Réseau" }, edit: Customize);
            check(filtered.Board.Widgets.Single(widget => widget.Id == heroId).TargetValue is null,
                "A neutral union total carries no service target or capacity comparison.");
            var disabledSettings = Settings(); disabledSettings.Business.WeatherRules[1].WeatherEnabled = false;
            var (disabled, disabledWidgets, _) = Project(disabledSettings, new[] { "team:support" }, new[] { "Support" }, edit: board =>
            {
                board.Widgets.Single(widget => widget.Id == heroId).TargetValue = 300;
                board.Widgets.Single(widget => widget.Id == TeamId("support")).TargetValue = 50;
            });
            check(Weather(disabled, disabledWidgets, heroId).State == "unknown"
                && disabled.Board.Widgets.Single(widget => widget.Id == heroId).TargetValue == 50
                && disabled.Contexts[TeamId("support")].RepresentedByHero,
                "Disabling exact-team weather preserves the team's explicit target on the hero when its compact card is represented there.");
        });
        test("Returning to all teams restores service thresholds and preserves a genuinely empty selected team", () =>
        {
            var settings = Settings(); var (all, widgets, _) = Project(settings, Array.Empty<string>(), Array.Empty<string>());
            check(all.Scope.Mode == "service" && all.Board.Widgets.Single(widget => widget.Id == heroId).WarningValue == 100
                && Weather(all, widgets, heroId).State == "sun" && all.Contexts[TeamId("empty")].Applicable, "A complete service retains its configured thresholds and known empty teams.");
            var (empty, emptyWidgets, _) = Project(settings, new[] { "team:empty" }, new[] { "Vide" });
            check(empty.Scope.Mode == "team" && emptyWidgets[heroId].Metric("active")!.Value == 0 && Weather(empty, emptyWidgets, heroId).State == "sun", "Explicitly selecting a known empty team remains different from displaying an outside team as zero.");
        });
    }
}
