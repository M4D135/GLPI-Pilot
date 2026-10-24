using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static partial class Program
{
    private static async Task CheckLayoutImports()
    {
        var settings = Settings(); var host = new AnalyticsTestHost();
        await using var app = new PilotApplication(host, (_, _) => { }, settings);
        var original = JsonSerializer.Serialize(app.Settings.Dashboard.Layout, PilotApplication.Json);
        var personalBefore = JsonSerializer.Serialize(app.Settings.Dashboard.PersonalLayout, PilotApplication.Json);
        var existingBoard = JsonSerializer.Serialize(app.Settings.Dashboard.Layout.Boards[0], PilotApplication.Json);
        host.SavePath = Path.Combine(_root, "append-layout.json");
        await Call(app, "dashboard.layout.export", new { personal = false }); host.OpenPath = host.SavePath;
        var preview = await Call(app, "dashboard.layout.import", new { personal = false });
        Check(preview.GetProperty("canAppend").GetBoolean() && Int(preview, "existingBoards") == 1
            && Str(preview.GetProperty("addedBoards")[0], "name").EndsWith(" (importé)", StringComparison.Ordinal)
            && JsonSerializer.Serialize(app.Settings.Dashboard.Layout, PilotApplication.Json) == original,
            "Import previews added boards and readable name collisions without changing existing dashboards.");
        var append = new { personal = false, layout = preview.GetProperty("layout"), baseFingerprint = Str(preview, "baseFingerprint") };
        var saved = await Call(app, "dashboard.layout.append", append);
        var merged = JsonSerializer.Serialize(app.Settings.Dashboard.Layout, PilotApplication.Json);
        Check(app.Settings.Dashboard.Layout.Boards.Count == 2
            && JsonSerializer.Serialize(app.Settings.Dashboard.Layout.Boards[0], PilotApplication.Json) == existingBoard
            && app.Settings.Dashboard.Layout.Boards.Select(board => board.Id).Distinct().Count() == 2
            && app.Settings.Dashboard.Layout.ActiveBoardId == Str(saved, "boardId")
            && JsonSerializer.Serialize(app.Settings.Dashboard.PersonalLayout, PilotApplication.Json) == personalBefore,
            "Additive import retains every existing board and identity, remaps imported collisions and leaves personal dashboards intact.");
        await Reject<InvalidOperationException>(async () => { await Call(app, "dashboard.layout.append", append); },
            "A stale additive preview cannot duplicate boards after the layout changes.");
        await Reject<InvalidOperationException>(async () => { await Call(app, "dashboard.layout.save", append); },
            "A stale replacement preview cannot overwrite the layout that has changed since review.");
        await Call(app, "dashboard.layout.undo");
        Check(JsonSerializer.Serialize(app.Settings.Dashboard.Layout, PilotApplication.Json) == original,
            "Undo of an additive import restores the complete prior layout in one operation.");
        await Call(app, "dashboard.layout.redo");
        Check(JsonSerializer.Serialize(app.Settings.Dashboard.Layout, PilotApplication.Json) == merged,
            "Redo restores the same imported board identities instead of creating additional copies.");

        var full = app.Settings.Dashboard.Layout.Copy();
        while (full.Boards.Count < 12) full.Boards.Add(DashboardLayoutSettings.CreateBoard("Tableau " + full.Boards.Count, "empty"));
        await Call(app, "dashboard.layout.save", new { layout = full });
        var fullBefore = JsonSerializer.Serialize(app.Settings.Dashboard.Layout, PilotApplication.Json);
        var blocked = await Call(app, "dashboard.layout.import", new { personal = false });
        Check(!blocked.GetProperty("canAppend").GetBoolean() && Str(blocked, "appendReason").Contains("12", StringComparison.Ordinal)
            && blocked.GetProperty("addedBoards").GetArrayLength() == 0,
            "Import review reports the twelve-board capacity before proposing a mutation.");
        await Reject<InvalidDataException>(async () => { await Call(app, "dashboard.layout.append", new { layout = blocked.GetProperty("layout"), baseFingerprint = Str(blocked, "baseFingerprint") }); },
            "The C# append endpoint enforces board capacity even if a client ignores the disabled action.");
        Check(JsonSerializer.Serialize(app.Settings.Dashboard.Layout, PilotApplication.Json) == fullBefore,
            "A rejected over-capacity import preserves every board and the active selection.");
        var personalPreview = await Call(app, "dashboard.layout.import", new { personal = true });
        await Call(app, "dashboard.layout.append", new { personal = true, layout = personalPreview.GetProperty("layout"), baseFingerprint = Str(personalPreview, "baseFingerprint") });
        Check(Int(personalPreview, "existingBoards") == 1 && personalPreview.GetProperty("canAppend").GetBoolean()
            && app.Settings.Dashboard.PersonalLayout.Boards.Count == 2
            && JsonSerializer.Serialize(app.Settings.Dashboard.Layout, PilotApplication.Json) == fullBefore,
            "Personal additive import reviews and modifies its own layout independently of a full team layout.");
        var service = await Call(app, "dashboard.board.create", new { personal = true, name = "Pilotage de contrôle", preset = "service" });
        var serviceBoard = app.Settings.Dashboard.PersonalLayout.Boards.Single(board => board.Id == Str(service, "boardId"));
        Check(serviceBoard.Widgets.Count == 14 && serviceBoard.Sections.Count == 5
            && serviceBoard.Widgets.Single(widget => widget.Presentation == "weather-volume").WarningValue is null
            && serviceBoard.Widgets.Any(widget => widget.Kind == "matrix" && widget.SourceKey == "group-status")
            && serviceBoard.Widgets.Any(widget => widget.Kind == "matrix" && widget.SourceKey == "type-status"),
            "The runtime creates the service preset with editable sections, neutral weather and active group/type matrices.");

        var largeBoard = DashboardLayoutSettings.CreateBoard("Composition volumineuse enregistrée", "empty");
        largeBoard.Id = "business-workspace";
        for (var index = 0; index < 120; index++) largeBoard.Widgets.Add(new()
        { Id = "large-widget-" + index, Kind = "metric", SourceKey = "total", Title = "Carte synthétique " + index, ShowMiniHistory = false });
        var large = new DashboardLayoutSettings { ActiveBoardId = largeBoard.Id, Boards = new() { largeBoard } };
        large.Normalize(); large.Validate();
        await Call(app, "dashboard.layout.save", new { personal = false, layout = large });
        var importedLarge = large.Copy(); importedLarge.Boards[0].Name = "Composition volumineuse reprise";
        importedLarge.Boards[0].Widgets[119].Title = "Dernière carte conservée sans troncature";
        host.OpenPath = Path.Combine(_root, "large-service-layout.json");
        var expectedLarge = JsonSerializer.Serialize(importedLarge, PilotApplication.Json);
        await File.WriteAllTextAsync(host.OpenPath, expectedLarge);
        var sourceBytes = await File.ReadAllBytesAsync(host.OpenPath);
        var beforeLargePreview = JsonSerializer.Serialize(app.Settings, PilotApplication.Json);
        var settingsBytes = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        var personalBeforeLarge = JsonSerializer.Serialize(app.Settings.Dashboard.PersonalLayout, PilotApplication.Json);
        var largePreview = await Call(app, "dashboard.layout.import", new { personal = false });
        Check(largePreview.GetProperty("preview").GetBoolean() && !largePreview.GetProperty("canAppend").GetBoolean()
            && !string.IsNullOrWhiteSpace(Str(largePreview, "appendReason")) && largePreview.GetProperty("addedBoards").GetArrayLength() == 0
            && largePreview.GetProperty("layout").GetProperty("boards")[0].GetProperty("widgets").GetArrayLength() == 120
            && JsonSerializer.Serialize(largePreview.GetProperty("layout"), PilotApplication.Json) == expectedLarge
            && JsonSerializer.Serialize(app.Settings, PilotApplication.Json) == beforeLargePreview
            && Enumerable.SequenceEqual(settingsBytes, await File.ReadAllBytesAsync(AppPaths.SettingsFile)),
            "A valid 120-card service layout remains fully previewable without mutation when an identity collision makes additive import invalid.");
        var largeCommit = new { personal = false, layout = largePreview.GetProperty("layout"), baseFingerprint = Str(largePreview, "baseFingerprint") };
        await Reject<InvalidDataException>(async () => { await Call(app, "dashboard.layout.append", largeCommit); },
            "The append endpoint still rejects a remapped 120-card custom composition instead of relaxing its limit or truncating it.");
        Check(JsonSerializer.Serialize(app.Settings, PilotApplication.Json) == beforeLargePreview
            && Enumerable.SequenceEqual(settingsBytes, await File.ReadAllBytesAsync(AppPaths.SettingsFile)),
            "The refused additive action leaves both working settings and the persisted profile exactly unchanged.");
        await Call(app, "dashboard.layout.save", largeCommit);
        Check(JsonSerializer.Serialize(app.Settings.Dashboard.Layout, PilotApplication.Json) == expectedLarge
            && JsonSerializer.Serialize(AppSettings.Load().Dashboard.Layout, PilotApplication.Json) == expectedLarge
            && JsonSerializer.Serialize(app.Settings.Dashboard.PersonalLayout, PilotApplication.Json) == personalBeforeLarge
            && Enumerable.SequenceEqual(sourceBytes, await File.ReadAllBytesAsync(host.OpenPath)),
            "Explicit replacement from that preview persists all 120 cards exactly and preserves the personal layout and import file.");
    }

    private static async Task CheckWeatherSourceReliability()
    {
        var settings = Settings(); var board = DashboardLayoutSettings.CreateBoard("Fiabilité météo", "empty");
        board.Widgets.Add(new() { Id = "source-weather", Kind = "metric", SourceKey = "active", Presentation = "weather-volume", WarningValue = 1, CriticalValue = 2,
            ShowMiniHistory = false, Filters = new() { new() { Kind = "metric", SourceKey = "active" } } });
        board.Widgets.Add(new() { Id = "pending-weather", Kind = "metric", SourceKey = "pending", Presentation = "weather-volume", WarningValue = 1, CriticalValue = 2,
            ShowMiniHistory = false });
        settings.Dashboard.Layout = new() { Boards = new() { board }, ActiveBoardId = board.Id };
        settings.Dashboard.PersonalLayout = settings.Dashboard.Layout.Copy();
        await using var app = new PilotApplication(new AnalyticsTestHost(), (_, _) => { }, settings);
        string WeatherState(JsonElement model) => Str(Widget(model, "source-weather").GetProperty("content").GetProperty("volumeWeather"), "state");
        var unknown = Data(1, Row("51", person: "Alex"), Row("52", "État non classé", person: "Bob"));
        app.LoadFixture(unknown);
        var filtered = await Call(app, "dashboard.get");
        Check(Widget(filtered, "source-weather").GetProperty("content").GetProperty("value").GetDouble() == 1 && WeatherState(filtered) == "partial",
            "An active-only filter cannot turn unknown source statuses into a trusted weather count.");
        Check(Str(Widget(filtered, "pending-weather").GetProperty("content").GetProperty("volumeWeather"), "state") == "partial",
            "Pending stock weather also retains status uncertainty instead of treating unknown tickets as a reliable zero.");
        var personal = await Call(app, "dashboard.get", new { technicianKey = TechnicianDashboardService.IdentityKey("Alex") });
        Check(WeatherState(personal) == "partial",
            "A personal weather retains source status uncertainty even when that row belongs to another technician.");
        app.LoadFixture(Data(1, Row("51"), Row("", "Résolu", person: "Bob")));
        Check(WeatherState(await Call(app, "dashboard.get")) == "partial",
            "Filtering out a source row with no usable identifier does not certify the remaining weather.");
        app.LoadFixture(Data(1, Row("51"), Row("52", "Résolu", person: "Bob"), Row("52", "Résolu", person: "Bob")));
        Check(WeatherState(await Call(app, "dashboard.get")) == "partial",
            "Filtering out duplicated source identifiers does not silently change the weather to unique-ticket confidence.");
        app.LoadFixture(Data(1, Row("51"), Row("52", "Résolu", person: "Bob")));
        Check(WeatherState(await Call(app, "dashboard.get")) == "sun",
            "A complete source with recognized statuses and unique identities still gives weather to a filtered active count.");
    }
}
