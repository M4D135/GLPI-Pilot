using System.Text.Json;
using GlpiPilot.Core;

internal static class WorkspaceChecks
{
    public static void Register(Action<string, Action> test, Action<bool, string> check)
    {
        var observed = new BusinessDashboardValues(new[] { "Support", "Terrain" }, new[] { "Matériel > Poste", "Accès" });
        DashboardSettings Settings() { var settings = new DashboardSettings(); settings.Normalize(); return settings; }
        test("Supervision omits the generated empty-team note without removing configured notes or group analyses", () =>
        {
            var settings = Settings(); var withoutTeams = new BusinessDashboardValues(Array.Empty<string>(), observed.Categories);
            var before = JsonSerializer.Serialize(settings);
            var weather = BusinessDashboardTemplates.Create("weather", settings, withoutTeams);
            var placeholder = weather.Widgets.Single(widget => widget.Kind == "note" && widget.Section == "teams");
            var workspace = BusinessDashboardTemplates.Create("workspace", settings, withoutTeams);
            check(!workspace.Widgets.Any(widget => widget.Id == placeholder.Id), "An unconfigured team must not occupy a supervision card with setup instructions.");
            check(workspace.Widgets.Count(widget => widget.Kind == "matrix" && widget.SourceKey == "group-status") == 2,
                "The top-group chart and complete group matrix stay visible for diagnosis and recovery.");
            var pending = weather.Widgets.Single(widget => widget.Kind == "metric" && widget.SourceKey == "pending");
            check(workspace.Widgets.Any(widget => widget.Id == pending.Id), "Removing the placeholder must not shift the identities of the remaining cards.");
            check(JsonSerializer.Serialize(settings) == before, "Rendering the defaults leaves persisted settings untouched.");

            settings.Business.BoardOverrides.Add(weather); var savedWeather = JsonSerializer.Serialize(settings);
            check(!BusinessDashboardTemplates.Create("workspace", settings, withoutTeams).Widgets.Any(widget => widget.Id == placeholder.Id)
                && JsonSerializer.Serialize(settings) == savedWeather, "An unchanged placeholder in a saved weather board is removed only from the displayed copy.");
            var legacyWorkspace = workspace.Copy(); legacyWorkspace.Widgets.Add(placeholder.Copy()); legacyWorkspace.Widgets[^1].Section = "weather";
            settings.Business.BoardOverrides.Clear(); settings.Business.BoardOverrides.Add(legacyWorkspace); var savedWorkspace = JsonSerializer.Serialize(settings);
            check(!BusinessDashboardTemplates.Create("workspace", settings, withoutTeams).Widgets.Any(widget => widget.Id == placeholder.Id)
                && JsonSerializer.Serialize(settings) == savedWorkspace, "An unchanged placeholder in an existing workspace is removed without rewriting stored settings.");

            legacyWorkspace.Presentation = new() { Slides = new() { new() { Title = "Note de préparation", Items = new() { new() { WidgetId = placeholder.Id } } } } };
            var beforePresentation = JsonSerializer.Serialize(settings);
            var presented = BusinessDashboardTemplates.Create("workspace", settings, withoutTeams);
            presented.Presentation!.Validate(presented.Widgets.Select(widget => widget.Id));
            new DashboardLayoutSettings { ActiveBoardId = presented.Id, Boards = new() { presented } }.Validate();
            check(presented.Widgets.Any(widget => widget.Id == placeholder.Id)
                && JsonSerializer.Serialize(presented.Presentation) == JsonSerializer.Serialize(legacyWorkspace.Presentation)
                && JsonSerializer.Serialize(settings) == beforePresentation,
                "An unchanged note explicitly placed in a saved presentation remains available without orphaning slide content or mutating settings.");
            weather.Presentation = legacyWorkspace.Presentation.Copy();
            settings.Business.BoardOverrides.Clear(); settings.Business.BoardOverrides.Add(weather);
            check(BusinessDashboardTemplates.Create("workspace", settings, withoutTeams).Widgets.Any(widget => widget.Id == placeholder.Id),
                "A note used in the saved weather presentation remains a deliberate choice when composing supervision.");
            legacyWorkspace.Presentation = null; weather.Presentation = null;

            placeholder.Note = "Consigne locale de l’équipe"; placeholder.Title = "Équipes du relevé";
            settings.Business.BoardOverrides.Clear(); settings.Business.BoardOverrides.Add(weather);
            var configured = BusinessDashboardTemplates.Create("workspace", settings, withoutTeams);
            check(configured.Widgets.Any(widget => widget.Id == placeholder.Id && widget.Note == "Consigne locale de l’équipe"),
                "A saved weather note survives even when it uses the former placeholder title.");
            settings.Business.BoardOverrides.Clear(); workspace.Widgets.Add(placeholder.Copy());
            workspace.Widgets[^1].Section = "weather"; settings.Business.BoardOverrides.Add(workspace);
            check(BusinessDashboardTemplates.Create("workspace", settings, withoutTeams).Widgets.Any(widget => widget.Id == placeholder.Id),
                "A customized workspace note remains intact.");
            var renamed = legacyWorkspace.Widgets.Single(widget => widget.Id == placeholder.Id); renamed.Title = "Consigne équipe personnalisée";
            settings.Business.BoardOverrides.Clear(); settings.Business.BoardOverrides.Add(legacyWorkspace);
            check(BusinessDashboardTemplates.Create("workspace", settings, withoutTeams).Widgets.Any(widget => widget.Id == placeholder.Id),
                "A deliberately renamed note keeps its content even if it started as the placeholder.");
            renamed.Title = "Équipes du relevé"; renamed.Filters.Add(new() { Kind = "metric", SourceKey = "active" });
            check(BusinessDashboardTemplates.Create("workspace", settings, withoutTeams).Widgets.Any(widget => widget.Id == placeholder.Id),
                "A note with a custom scope is not an untouched generated placeholder.");
        });
        test("Default service and compatibility boards align metric dimensions without migrating saved choices", () =>
        {
            var settings = Settings();
            foreach (var view in BusinessDashboardTemplates.Views)
            {
                var board = BusinessDashboardTemplates.Create(view.Key, settings, observed);
                check(board.Widgets.All(widget => !widget.AutoHeight), "Generated cards have explicit independent heights.");
                check(board.Widgets.Where(widget => widget.Kind == "metric" && widget.Presentation != "weather-volume")
                    .All(widget => widget.Span == 4 && widget.Height == 320), "Default indicators share their width and height.");
                var chosen = board.Widgets[0]; chosen.Span = 7; chosen.Height = 420; chosen.AutoHeight = true; chosen.GridX = 2; chosen.GridY = 17;
                settings.Business.BoardOverrides.Add(board);
                var restored = BusinessDashboardTemplates.Create(view.Key, settings, observed).Widgets[0];
                check(restored.Span == 7 && restored.Height == 420 && restored.AutoHeight && restored.GridX == 2 && restored.GridY == 17,
                    "A saved custom size, automatic-height choice and gap remain untouched.");
                settings.Business.BoardOverrides.Clear();
            }
        });
        test("Integrated workspace keeps all 48 weather rules without truncating its operational analyses", () =>
        {
            var settings = Settings();
            settings.Business.WeatherRules = Enumerable.Range(0, 48).Select(index => new BusinessWeatherRule
            {
                Id = "r" + index, Label = "Volume " + index, SourceKey = index == 0 ? "active" : "pending",
                OrangeAbove = index + 1, RedAbove = index + 100, Visible = index != 47, Note = "Repère " + index
            }).ToList();
            var before = JsonSerializer.Serialize(settings); var board = BusinessDashboardTemplates.Create("workspace", settings, observed);
            var layout = new DashboardLayoutSettings { ActiveBoardId = board.Id, Boards = new() { board } }; layout.Normalize(); layout.Validate();
            check(board.Widgets.Count > 60 && board.Widgets.Count <= 120, "The combined scope keeps its complete bounded composition.");
            var weather = board.Widgets.Where(item => item.Presentation == "weather-volume").ToArray();
            check(weather.Length == 48 && weather.Single(item => item.Title == "Volume 47").Hidden, "Every rule and explicit hidden choice remains present.");
            check(weather.Single(item => item.Title == "Volume 3") is { WarningValue: 4, CriticalValue: 103, Note: "Repère 3" }, "Thresholds and notes remain exact.");
            check(board.Widgets.Any(item => item.Kind == "oldest" && item.SourceKey == "groups") && board.Widgets.Any(item => item.Kind == "oldest" && item.SourceKey == "tickets")
                && board.Widgets.Any(item => item.Kind == "participation" && item.SourceKey == "authors") && board.Widgets.Any(item => item.Kind == "participation" && item.SourceKey == "comparison"), "Oldest and participation capabilities coexist in one scope.");
            check(JsonSerializer.Serialize(settings) == before, "Building the workspace never mutates the user's settings.");
        });
        test("Integrated weather preserves exact first-team scope and custom widget geometry", () =>
        {
            var settings = Settings(); settings.Business.WeatherRules = new()
            {
                new() { Id = "a", Label = "Équipe A", Kind = "team", Values = new() { "Support" }, Visible = false, OrangeAbove = 4, RedAbove = 8 },
                new() { Id = "b", Label = "Équipe B", Kind = "team", Values = new() { "Support", "Terrain" }, OrangeAbove = 12, RedAbove = 24 }
            };
            var weather = BusinessDashboardTemplates.Create("weather", settings, observed);
            weather.Widgets[1].Span = 7; weather.Widgets[1].Height = 440; weather.Widgets[1].AutoHeight = false;
            settings.Business.BoardOverrides.Add(weather);
            var workspace = BusinessDashboardTemplates.Create("workspace", settings, observed);
            var result = workspace.Widgets.Single(item => item.Title == "Équipe B");
            check(result.Span == 7 && result.Height == 440 && !result.AutoHeight, "Explicit geometry is preserved.");
            check(JsonSerializer.Serialize(result.Filters) == JsonSerializer.Serialize(weather.Widgets[1].Filters)
                && result.Filters.Any(filter => filter.Exclude && filter.Values.Contains("Support")), "A hidden prior team still keeps its precedence.");
        });
        test("Integrated workspace identity and saved composition round-trip independently of compatibility boards", () =>
        {
            var settings = Settings(); var board = BusinessDashboardTemplates.Create("workspace", settings, observed);
            board.Name = "Revue de service"; board.Sections[0].Title = "Lecture du service"; settings.Business.BoardOverrides.Add(board);
            var roundtrip = JsonSerializer.Deserialize<DashboardSettings>(JsonSerializer.Serialize(settings))!; roundtrip.Normalize(); roundtrip.Business.Validate();
            var restored = BusinessDashboardTemplates.Create("workspace", roundtrip, observed); restored.Widgets[0].Title = "Copie";
            check(restored.Name == "Revue de service" && restored.Sections[0].Title == "Lecture du service" && roundtrip.Business.BoardOverrides[0].Widgets[0].Title != "Copie", "The new composition is persistent and deep copied.");
        });
    }
}
