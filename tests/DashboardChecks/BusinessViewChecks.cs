using System.Text.Json;
using GlpiPilot.Core;

internal static class BusinessViewChecks
{
    public static void Register(Action<string, Action> test, Action<bool, string> check)
    {
        var source = new ExtractionResult
        {
            Headers = new() { "ID", "Titre", "Statut", "Type", "Date d’ouverture", "Attribué à - Groupe de techniciens", "Catégorie", "Attribué à - Technicien", "Suivis - Rédacteur", "Tâches - Rédacteur" },
            Rows = new()
            {
                new[] { "1", "Ticket fictif 1", "Nouveau", "Incident", "01/10/2026", "Groupe Alpha;Groupe Bêta", "Services > Accès", "Agent fictif 1", "Agent fictif 1", "Agent fictif 2" },
                new[] { "2", "Ticket fictif 2", "En attente", "Demande", "02/10/2026", "Groupe Bêta", "Services voisins", "Agent fictif 2", "", "Agent fictif 2" },
                new[] { "3", "Ticket fictif 3", "En cours (planifié)", "Incident", "03/10/2026", "Groupe Gamma", "Équipement > Poste", "", "Agent fictif 3", "" }
            }, Complete = true, ExpectedTotal = 3, ExtractedAt = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), SourceUrl = "https://glpi.example.test/front/ticket.php"
        };
        DashboardSettings Settings() { var settings = new DashboardSettings(); settings.Normalize(); return settings; }
        test("Native pilot views are present without imports and do not consume the twelve custom boards", () =>
        {
            var settings = Settings(); settings.Layout.Boards.AddRange(Enumerable.Range(1, 11).Select(index => DashboardLayoutSettings.CreateBoard("Custom " + index, "empty")));
            var before = JsonSerializer.Serialize(settings); var observed = BusinessDashboardTemplates.Observe(source, settings);
            foreach (var view in BusinessDashboardTemplates.Views)
            {
                var board = BusinessDashboardTemplates.Create(view.Key, settings, observed);
                new DashboardLayoutSettings { ActiveBoardId = board.Id, Boards = new() { board } }.Validate();
                check(board.Widgets.Count > 0 && board.Id == "business-" + view.Key, "Each native route has a stable ready-to-use board.");
            }
            check(BusinessDashboardTemplates.Views.Length == 8 && JsonSerializer.Serialize(settings) == before, "All native views leave custom settings intact.");
        });
        test("Unconfigured native weather uses editable starter thresholds for all stock cards", () =>
        {
            var settings = Settings(); var observed = BusinessDashboardTemplates.Observe(source, settings);
            check(observed.Groups.Count == 3 && observed.Groups.Contains("Groupe Bêta"), "Groups are split using the source interpretation.");
            var before = JsonSerializer.Serialize(settings);
            var board = BusinessDashboardTemplates.Create("weather", settings, observed);
            var cards = board.Widgets.Where(widget => widget.Presentation == "weather-volume").ToArray();
            check(cards.Length >= 8 && cards.All(widget => widget.WarningValue.HasValue && widget.CriticalValue.HasValue), "Every native stock, type, category and team card has a usable starter pair.");
            check(cards.Single(widget => widget.Filters.Count == 0 && widget.SourceKey == "active") is { WarningValue: 100, CriticalValue: 200 }
                && cards.Where(widget => widget.SourceKey is "active-incidents" or "active-requests").All(widget => widget.WarningValue == 50 && widget.CriticalValue == 100)
                && cards.Where(widget => widget.Filters.Count > 0).All(widget => widget.WarningValue == 10 && widget.CriticalValue == 20), "Starter pairs distinguish the global stock, types, and categories or teams.");
            check(cards.Any(widget => widget.SourceKey == "active" && widget.Filters.Count == 0 && widget.Span == 12), "The active stock is a full-width hero card.");
            check(board.FreshnessWarningHours == 48 && board.Sections.Select(section => section.Key).SequenceEqual(new[] { "stock", "categories", "teams", "context", "actions" }), "The report hierarchy and freshness reminder are native.");
            var legacyEmpty = JsonSerializer.Deserialize<DashboardSettings>("{\"Business\":{\"WeatherRules\":[]}}")!; legacyEmpty.Normalize();
            check(BusinessDashboardTemplates.UsesDefaultWeatherThresholds(settings) && BusinessDashboardTemplates.UsesDefaultWeatherThresholds(legacyEmpty)
                && BusinessDashboardTemplates.WeatherRules(legacyEmpty, observed).All(rule => rule.OrangeAbove.HasValue && rule.RedAbove.HasValue)
                && JsonSerializer.Serialize(settings) == before, "New and old unconfigured profiles receive the same virtual defaults without modifying their saved settings.");
        });
        test("Native starter thresholds use strict Excel boundaries without certifying missing or incomplete counts", () =>
        {
            var settings = Settings(); var board = BusinessDashboardTemplates.Create("weather", settings, BusinessDashboardTemplates.Observe(source, settings));
            foreach (var widget in board.Widgets.Where(widget => widget.Presentation == "weather-volume"))
            {
                var orange = widget.WarningValue!.Value; var red = widget.CriticalValue!.Value;
                check(widget.EvaluateVolumeWeather(orange, true).State == "sun" && widget.EvaluateVolumeWeather(orange + 1, true).State == "watch"
                    && widget.EvaluateVolumeWeather(red, true).State == "watch" && widget.EvaluateVolumeWeather(red + 1, true).State == "rain", "Only strictly exceeding each starter value changes its weather state.");
                check(widget.EvaluateVolumeWeather(0, true).State == "sun" && widget.EvaluateVolumeWeather(null, true).State == "unknown"
                    && widget.EvaluateVolumeWeather(0, false).State == "partial", "A known empty stock, a missing measure and an incomplete reading remain distinct.");
            }
        });
        test("Explicit blank zero and custom weather thresholds are never replaced by starter defaults", () =>
        {
            var settings = Settings(); settings.Business.WeatherRules = new()
            {
                new() { Id = "native-stock", Label = "Stock conservé" },
                new() { Id = "native-incidents", Label = "Seuil zéro", SourceKey = "active-incidents", OrangeAbove = 0, RedAbove = 0 },
                new() { Id = "partial", Label = "Seuil partiel", OrangeAbove = null, RedAbove = 5 },
                new() { Id = "disabled", Label = "Météo désactivée", OrangeAbove = 12, RedAbove = 24, WeatherEnabled = false }
            };
            settings = JsonSerializer.Deserialize<DashboardSettings>(JsonSerializer.Serialize(settings))!; settings.Normalize(); settings.Business.Validate();
            var before = JsonSerializer.Serialize(settings); var observed = BusinessDashboardTemplates.Observe(source, settings);
            var rules = BusinessDashboardTemplates.Profile(settings, observed).WeatherRules;
            check(rules[0].OrangeAbove is null && rules[0].RedAbove is null && rules[1].OrangeAbove == 0 && rules[1].RedAbove == 0
                && rules[2].OrangeAbove is null && rules[2].RedAbove == 5 && !rules[3].WeatherEnabled && rules[3].OrangeAbove == 12,
                "Saved values retain intentional blanks, valid zeroes, partial pairs and disabled weather, including former native IDs.");
            check(!BusinessDashboardTemplates.UsesDefaultWeatherThresholds(settings) && JsonSerializer.Serialize(settings) == before, "Configured profiles are never reported or mutated as starter profiles.");
            var custom = Settings(); var board = BusinessDashboardTemplates.Create("weather", custom, observed);
            foreach (var widget in board.Widgets.Where(widget => widget.Presentation == "weather-volume")) widget.WarningValue = widget.CriticalValue = null;
            custom.Business.BoardOverrides.Add(board);
            check(!BusinessDashboardTemplates.UsesDefaultWeatherThresholds(custom)
                && BusinessDashboardTemplates.Create("weather", custom, observed).Widgets.Where(widget => widget.Presentation == "weather-volume").All(widget => widget.WarningValue is null && widget.CriticalValue is null),
                "An explicitly customized native board is preserved even when its thresholds are empty.");
        });
        test("Native team priority includes hidden teams and keeps exact membership for the next rule", () =>
        {
            var settings = Settings(); settings.Business.WeatherRules = new()
            {
                new() { Id = "first", Label = "Équipe fictive A", Kind = "team", Values = new() { "Groupe Alpha" }, Visible = false, OrangeAbove = 1, RedAbove = 3 },
                new() { Id = "second", Label = "Équipe fictive B", Kind = "team", Values = new() { "Groupe Bêta" }, OrangeAbove = 2, RedAbove = 4 }
            };
            var board = BusinessDashboardTemplates.Create("weather", settings, BusinessDashboardTemplates.Observe(source, settings));
            var snapshot = DashboardService.Calculate(source, settings, source.ExtractedAt);
            var first = board.Widgets.Single(widget => widget.Title == "Équipe fictive A"); var second = board.Widgets.Single(widget => widget.Title == "Équipe fictive B");
            check(first.Hidden && DashboardService.ResolveScope(snapshot, first.Filters).SequenceEqual(new[] { 0 }), "A hidden first team retains its population.");
            check(DashboardService.ResolveScope(snapshot, second.Filters).SequenceEqual(new[] { 1 }), "The visible next team excludes the hidden team's shared occurrence.");
            check(settings.Business.WeatherRules[0].Visible == false, "Building a view does not rewrite profile visibility.");
        });
        test("Disabled weather leaves its volume visible without threshold coloring", () =>
        {
            var settings = Settings(); settings.Business.WeatherRules.Add(new() { Id = "volume", Label = "Volume fictif", WeatherEnabled = false, OrangeAbove = 1, RedAbove = 2 });
            var card = BusinessDashboardTemplates.Create("weather", settings, BusinessDashboardTemplates.Observe(source, settings)).Widgets.Single(widget => widget.Title == "Volume fictif");
            check(!card.Hidden && card.SourceKey == "active" && card.Presentation != "weather-volume" && card.WarningValue is null && card.CriticalValue is null, "WeatherEnabled and Visible have separate behavior.");
            check(settings.Business.WeatherRules.Single().OrangeAbove == 1, "The source threshold is retained in the editable profile.");
        });
        test("Native category weather includes descendants without matching word prefixes", () =>
        {
            var settings = Settings(); settings.Business.WeatherRules.Add(new() { Id = "branch", Label = "Branche fictive", Kind = "category", Values = new() { "Services" }, IncludeDescendants = true });
            var card = BusinessDashboardTemplates.Create("weather", settings, BusinessDashboardTemplates.Observe(source, settings)).Widgets.Single(widget => widget.Title == "Branche fictive");
            check(DashboardService.ResolveScope(DashboardService.Calculate(source, settings, source.ExtractedAt), card.Filters).SequenceEqual(new[] { 0 }), "Category branch boundaries are preserved.");
        });
        test("Legacy configuration supplies native weather rules while complete native templates replace its old presentation", () =>
        {
            var settings = Settings(); var board = DashboardLayoutSettings.CreateBoard("Météo locale fictive", "empty");
            board.Widgets.Add(new() { Id = "legacy-first", Kind = "metric", SourceKey = "active", Title = "Stock fictif", Presentation = "weather-volume", WarningValue = 8, CriticalValue = 16 });
            board.Widgets.Add(new() { Id = "legacy-second", Kind = "metric", SourceKey = "pending", Title = "Attente fictive", Presentation = "weather-volume", WarningValue = 3, CriticalValue = 6, Hidden = true });
            board.FreshnessWarningHours = 72;
            board.Widgets.Add(new() { Kind = "note", Note = "Action fictive conservée", Title = "Analyse" });
            settings.Layout.Boards.Add(board); var before = JsonSerializer.Serialize(board);
            check(!BusinessDashboardTemplates.UsesDefaultWeatherThresholds(settings), "Legacy imported weather is not labelled with generic starter thresholds.");
            var copy = BusinessDashboardTemplates.Create("weather", settings, BusinessDashboardTemplates.Observe(source, settings));
            check(copy.Widgets.Count > board.Widgets.Count && copy.Widgets[0].WarningValue == 8 && copy.Widgets[1].Hidden && copy.Name == "Météo", "Existing rules and visibility migrate into the complete native hierarchy without requiring another JSON import.");
            check(copy.FreshnessWarningHours == 72 && copy.Widgets.Any(widget => widget.Kind == "note" && widget.Note == "Action fictive conservée"), "Legacy notes and freshness remain available in the native summary and settings.");
            check(JsonSerializer.Serialize(board) == before && copy.Id == "business-weather", "The native alias does not mutate the imported original.");
            var oldPilotage = DashboardLayoutSettings.CreateBoard("Pilotage", "empty");
            oldPilotage.Widgets.Add(new() { Kind = "matrix", SourceKey = "type-status" }); settings.Layout.Boards.Add(oldPilotage);
            check(BusinessDashboardTemplates.Create("pilotage", settings, BusinessDashboardTemplates.Observe(source, settings)).Widgets.Any(widget => widget.SourceKey == "group-status" && widget.TopCount == 10), "An old Pilotage board cannot hide the new native top-ten groups chart.");
        });
        test("Native overrides are independent copies while other views still use their templates", () =>
        {
            var settings = Settings(); var observed = BusinessDashboardTemplates.Observe(source, settings);
            var saved = BusinessDashboardTemplates.Create("groups", settings, observed); saved.Name = "Mes groupes personnalisés";
            settings.Business.BoardOverrides.Add(saved); settings.Business.Validate();
            var result = BusinessDashboardTemplates.Create("groups", settings, observed); result.Widgets[0].Title = "Edited copy";
            check(result.Name == saved.Name && saved.Widgets[0].Title != "Edited copy", "Native customization is preserved and copied deeply.");
            check(BusinessDashboardTemplates.Create("oldest", settings, observed).Widgets.Count(widget => widget.Kind == "oldest") == 2, "The oldest view is available independently.");
        });
        test("Native Pilotage retains the highlighted charts and separate assigned and planned stocks", () =>
        {
            var settings = Settings(); var observed = BusinessDashboardTemplates.Observe(source, settings);
            var board = BusinessDashboardTemplates.Create("pilotage", settings, observed);
            check(board.Widgets.Any(widget => widget.Kind == "matrix" && widget.SourceKey == "group-status" && widget.Presentation == "stacked" && widget.StackedMode == "volume" && widget.TopCount == 10), "Top ten groups is present on Pilotage itself, on a shared volume scale.");
            check(board.Widgets.Any(widget => widget.Kind == "distribution" && widget.SourceKey == "active-category" && widget.TopCount == 8), "The top eight categories remains separate from the weather selection.");
            check(board.Widgets.Count(widget => widget.Kind == "matrix" && widget.SourceKey == "type-status") == 2, "Type/status has a chart and a readable numeric matrix.");
            var snapshot = DashboardService.Calculate(source, settings, source.ExtractedAt);
            var planned = board.Widgets.Single(widget => widget.Filters.Any(filter => filter.SourceKey == "status" && filter.Value == DashboardService.StatusLabel(3)));
            var assigned = board.Widgets.Single(widget => widget.Filters.Any(filter => filter.SourceKey == "status" && filter.Value == DashboardService.StatusLabel(2)));
            check(DashboardService.ResolveScope(snapshot, planned.Filters).SequenceEqual(new[] { 2 }) && DashboardService.ResolveScope(snapshot, assigned.Filters).Length == 0, "Planned and assigned are distinct, including known zero.");
        });
        test("Native focused stock views retain category detail and the ten oldest occurrences", () =>
        {
            var settings = Settings(); var observed = BusinessDashboardTemplates.Observe(source, settings);
            foreach (var key in new[] { "oldest", "sites" })
            {
                var board = BusinessDashboardTemplates.Create(key, settings, observed);
                check(board.Widgets.Any(widget => widget.Kind == "distribution" && widget.SourceKey == "active-category" && widget.TopCount == 12), "Focus categories are ready to use.");
                check(board.Widgets.Any(widget => widget.Kind == "oldest" && widget.SourceKey == "tickets"), "The focus includes ten oldest occurrences from the same scope.");
                if (key == "sites") check(board.Filters.Any(filter => filter.Kind == "metric" && filter.SourceKey == "active"), "Site charts and localization totals share the active stock of the site table.");
            }
        });
    }
}
