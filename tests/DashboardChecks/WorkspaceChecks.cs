using System.Text.Json;
using GlpiPilot.Core;

internal static class WorkspaceChecks
{
    public static void Register(Action<string, Action> test, Action<bool, string> check)
    {
        var observed = new BusinessDashboardValues(new[] { "Support", "Terrain" }, new[] { "Matériel > Poste", "Accès" });
        DashboardSettings Settings() { var settings = new DashboardSettings(); settings.Normalize(); return settings; }
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
