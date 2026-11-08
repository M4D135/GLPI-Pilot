using System.Text.Json;
using GlpiPilot.Core;

var checks = new List<(string Name, Action Run)>();
void Test(string name, Action run) => checks.Add((name, run));
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
DashboardLayoutSettings Layout(string preset = "overview")
{
    var board = DashboardLayoutSettings.CreateBoard("Test", preset);
    return new() { Boards = new() { board }, ActiveBoardId = board.Id };
}
void Invalid(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid layout accepted"); }
Test("New local settings produce a usable default board", () => { var settings = new DashboardSettings(); settings.Normalize(); settings.Layout.Validate(); Assert(settings.Layout.Boards[0].Widgets.Count > 10); });
Test("Every preset is valid and bounded", () => { foreach (var preset in new[] { "overview", "service", "operations", "analysis", "personal", "team", "deadlines", "quality", "geography", "empty" }) Layout(preset).Validate(); });
Test("Appending boards preserves existing identities and resolves imported identity and name collisions", () =>
{
    var current = Layout("overview"); current.CatalogFavorites.Add("metric:active"); var imported = current.Copy();
    var extra = DashboardLayoutSettings.CreateBoard("test", "empty"); imported.Boards.Add(extra);
    var before = JsonSerializer.Serialize(current); var originalImport = JsonSerializer.Serialize(imported);
    var appended = DashboardLayoutSettings.AppendBoards(current, imported); appended.Layout.Validate();
    Assert(appended.Layout.Boards.Count == 3 && appended.AddedBoards.Count == 2);
    Assert(JsonSerializer.Serialize(appended.Layout.Boards[0]) == JsonSerializer.Serialize(current.Boards[0]));
    Assert(appended.Layout.Boards[1].Id != current.Boards[0].Id && appended.Layout.Boards[1].Widgets[0].Id == imported.Boards[0].Widgets[0].Id);
    Assert(appended.Layout.Boards[1].Name == "Test (importé)" && appended.Layout.Boards[2].Name == "test (importé 2)");
    Assert(appended.Layout.ActiveBoardId == appended.Layout.Boards[1].Id && appended.Layout.CatalogFavorites.SequenceEqual(current.CatalogFavorites));
    appended.Layout.Boards[1].Widgets[0].Note = "Changed copy";
    Assert(JsonSerializer.Serialize(current) == before && JsonSerializer.Serialize(imported) == originalImport);
});
Test("Append capacity rejects before mutation and accepts the exact twelve-board boundary", () =>
{
    var current = Layout("empty"); current.Boards.AddRange(Enumerable.Range(1, 11).Select(index => DashboardLayoutSettings.CreateBoard("Board " + index, "empty")));
    var imported = Layout("empty"); var before = JsonSerializer.Serialize(current); Invalid(() => DashboardLayoutSettings.AppendBoards(current, imported));
    Assert(JsonSerializer.Serialize(current) == before);
    current.Boards.RemoveAt(current.Boards.Count - 1); var append = DashboardLayoutSettings.AppendBoards(current, imported);
    Assert(append.Layout.Boards.Count == 12 && current.Boards.Count == 11); append.Layout.Validate();
    imported.Version = 9; Invalid(() => DashboardLayoutSettings.AppendBoards(current, imported));
});
Test("Service preset combines stock weather real analytical sources and editable action sections", () =>
{
    var layout = Layout("service"); layout.Validate(); var board = layout.Boards[0];
    Assert(board.Sections.Count == 5 && board.Widgets.Count == 14 && board.Widgets.All(widget => board.Sections.Any(section => section.Key == widget.Section)));
    var stock = board.Widgets.Single(widget => widget.SourceKey == "active");
    Assert(stock.Presentation == "weather-volume" && stock.WarningValue is null && stock.CriticalValue is null && stock.EvaluateVolumeWeather(5, true).State == "unknown");
    foreach (var key in new[] { "active-incidents", "active-requests", "pending", "without-group", "active-multiple-groups", "active-no-author", "group-status", "type-status", "active-category", "age", "inactivity" })
        Assert(board.Widgets.Any(widget => widget.SourceKey == key), key);
    Assert(board.Widgets.Any(widget => widget.Kind == "note" && widget.Section == "actions") && board.Widgets.Any(widget => widget.Kind == "attention"));
});
Test("Deep copy isolates nested boards and widgets", () => { var a = Layout(); var b = a.Copy(); b.Boards[0].Widgets[0].Title = "B"; b.Boards[0].Name = "B"; Assert(a.Boards[0].Name == "Test" && a.Boards[0].Widgets[0].Title == ""); });
Test("Freshness reminders and participant rosters remain configurable through copy and JSON", () =>
{
    var layout = Layout("empty"); var board = layout.Boards[0]; board.FreshnessWarningHours = 48;
    board.Widgets.Add(new() { Kind = "participation", SourceKey = "comparison", ParticipationRoster = new() { new() { Identity = "ID:123", Name = "Alex", Aliases = new() { "Alias explicite" } } } });
    var copy = JsonSerializer.Deserialize<DashboardLayoutSettings>(JsonSerializer.Serialize(layout.Copy()))!; copy.Validate();
    Assert(copy.Boards[0].FreshnessWarningHours == 48 && copy.Boards[0].Widgets[0].ParticipationRoster[0].Aliases.Single() == "Alias explicite");
    copy.Boards[0].Widgets[0].ParticipationRoster[0].Aliases.Clear(); Assert(board.Widgets[0].ParticipationRoster[0].Aliases.Count == 1);
    copy.Boards[0].FreshnessWarningHours = 0; Invalid(copy.Validate); copy.Normalize(); copy.Validate(); Assert(copy.Boards[0].FreshnessWarningHours is null);
    board.Widgets[0].ParticipationRoster[0].Name = ""; board.Widgets[0].ParticipationRoster[0].Identity = ""; Invalid(layout.Validate);
});
Test("Settings deep copy preserves local identity without sharing layout", () => { var a = new DashboardSettings { Layout = Layout(), PersonalTechnician = "A" }; var b = a.Copy(); b.Layout.Boards.Clear(); Assert(a.Layout.Boards.Count == 1 && b.PersonalTechnician == "A"); });
Test("Portable layout JSON round-trips all presentation settings", () => { var a = Layout("empty"); a.Boards[0].Widgets.Add(new() { Kind = "note", Note = "Ligne 1\nLigne 2", Span = 7, Height = 440, Locked = true, Hidden = true, Style = "accent", Accent = "teal", Presentation = "compact", Title = "Note" }); var b = JsonSerializer.Deserialize<DashboardLayoutSettings>(JsonSerializer.Serialize(a))!; b.Validate(); Assert(b.Boards[0].Widgets[0].Note == "Ligne 1\nLigne 2" && b.Boards[0].Widgets[0].Locked && b.Boards[0].Widgets[0].Hidden && b.Boards[0].Widgets[0].Span == 7); });
Test("Volume weather JSON preserves independent card thresholds and strict boundaries", () =>
{
    var layout = Layout("empty"); layout.Boards[0].Widgets.Add(new() { Kind = "metric", SourceKey = "active", Presentation = "weather-volume", WarningValue = 10, CriticalValue = 20 });
    var copy = JsonSerializer.Deserialize<DashboardLayoutSettings>(JsonSerializer.Serialize(layout.Copy()))!; copy.Normalize(); copy.Validate(); var card = copy.Boards[0].Widgets[0];
    Assert(card.Presentation == "weather-volume" && card.WarningValue == 10 && card.CriticalValue == 20);
    Assert(card.EvaluateVolumeWeather(10, true).State == "sun" && card.EvaluateVolumeWeather(11, true).State == "watch");
    Assert(card.EvaluateVolumeWeather(20, true).State == "watch" && card.EvaluateVolumeWeather(21, true).State == "rain");
    card.WarningValue = 12; Assert(layout.Boards[0].Widgets[0].WarningValue == 10);
});
Test("Volume weather does not infer health from missing thresholds values or partial counts", () =>
{
    var card = new DashboardWidgetSettings { Presentation = "weather-volume", WarningValue = 10, CriticalValue = 20 };
    Assert(card.EvaluateVolumeWeather(0, true).Title == "Maîtrisé" && card.EvaluateVolumeWeather(null, true).State == "unknown");
    Assert(card.EvaluateVolumeWeather(double.NaN, true).State == "unknown" && card.EvaluateVolumeWeather(0, false).State == "partial");
    card.CriticalValue = null; Assert(card.EvaluateVolumeWeather(8, true).Title == "Seuils à définir");
    card.CriticalValue = 20; card.WarningValue = null; Assert(card.EvaluateVolumeWeather(25, true).Title == "Seuils à définir");
});
Test("Volume weather reverses strict boundaries only when higher values are preferred", () =>
{
    var card = new DashboardWidgetSettings { Presentation = "weather-volume", WarningValue = 20, CriticalValue = 10, LowerIsBetter = false }; card.ValidateThresholds();
    Assert(card.EvaluateVolumeWeather(20, true).State == "sun" && card.EvaluateVolumeWeather(19, true).State == "watch");
    Assert(card.EvaluateVolumeWeather(10, true).State == "watch" && card.EvaluateVolumeWeather(9, true).State == "rain");
    card.Kind = "distribution"; Invalid(card.ValidateThresholds); card.Normalize(); Assert(card.Presentation == "bars");
});
Test("Automatic height preserves the manual minimum across copy and JSON", () =>
{
    var layout = Layout("empty"); layout.Boards[0].Widgets.Add(new() { Height = 640, AutoHeight = false });
    var copy = layout.Copy(); var roundTrip = JsonSerializer.Deserialize<DashboardLayoutSettings>(JsonSerializer.Serialize(copy))!;
    roundTrip.Validate(); var widget = roundTrip.Boards[0].Widgets[0];
    Assert(!widget.AutoHeight && widget.Height == 640, "Manual height was not preserved");
    widget.AutoHeight = true; roundTrip.Normalize(); roundTrip.Validate();
    Assert(widget.Height == 640 && !layout.Boards[0].Widgets[0].AutoHeight, "Automatic mode changed the remembered minimum or its source");
    Assert(JsonSerializer.Deserialize<DashboardWidgetSettings>("{\"Height\":640}")!.AutoHeight, "Legacy widgets should use their content height by default");
});
Test("New and legacy boards hide unavailable widgets without deleting saved widgets", () =>
{
    var layout = Layout(); var count = layout.Boards[0].Widgets.Count;
    Assert(layout.Boards[0].HideUnavailableWidgets);
    var document = JsonSerializer.SerializeToNode(layout)!;
    document["Boards"]![0]!.AsObject().Remove("HideUnavailableWidgets");
    var legacy = document.Deserialize<DashboardLayoutSettings>()!; legacy.Normalize(); legacy.Validate();
    Assert(legacy.Boards[0].HideUnavailableWidgets && legacy.Boards[0].Widgets.Count == count);
});
Test("An explicit show-unavailable preference survives copy normalization and JSON", () =>
{
    var layout = Layout(); layout.Boards[0].HideUnavailableWidgets = false;
    var copy = layout.Copy(); copy.Normalize(); copy.Validate();
    var roundTrip = JsonSerializer.Deserialize<DashboardLayoutSettings>(JsonSerializer.Serialize(copy))!; roundTrip.Normalize(); roundTrip.Validate();
    Assert(!copy.Boards[0].HideUnavailableWidgets && !roundTrip.Boards[0].HideUnavailableWidgets);
    copy.Boards[0].HideUnavailableWidgets = true;
    Assert(!layout.Boards[0].HideUnavailableWidgets && !roundTrip.Boards[0].HideUnavailableWidgets);
});
Test("Malformed imported dimensions rejected before local normalization", () => { var a = Layout(); a.Boards[0].Widgets[0].Span = 99; Invalid(a.Validate); a.Normalize(); a.Validate(); Assert(a.Boards[0].Widgets[0].Span == 12); });
Test("Invalid version and active board never pass validation", () => { var a = Layout(); a.Version = 2; Invalid(a.Validate); a.Version = 1; a.ActiveBoardId = "missing"; Invalid(a.Validate); });
Test("Duplicate widget or board IDs are invalid", () => { var a = Layout(); a.Boards[0].Widgets.Add(a.Boards[0].Widgets[0].Copy()); Invalid(a.Validate); a = Layout(); a.Boards.Add(a.Boards[0].Copy()); Invalid(a.Validate); });
Test("Malformed local duplicate IDs recover without merging widgets", () => { var a = Layout(); var count = a.Boards[0].Widgets.Count; a.Boards[0].Widgets.Add(a.Boards[0].Widgets[0].Copy()); a.Normalize(); a.Validate(); Assert(a.Boards[0].Widgets.Count == count + 1); });
Test("Nonexistent source drop leaves original order unchanged", () => { var a = Layout().Boards[0]; var ids = a.Widgets.Select(x => x.Id).ToArray(); Assert(!DashboardLayoutEngine.Move(a, "missing", null)); Assert(ids.SequenceEqual(a.Widgets.Select(x => x.Id))); });
Test("Drag ordering moves exact widget before target without loss", () => { var a = Layout().Boards[0]; var source = a.Widgets[^1]; var target = a.Widgets[0]; var count = a.Widgets.Count; Assert(DashboardLayoutEngine.Move(a, source.Id, target.Id)); Assert(a.Widgets[0] == source && a.Widgets[1] == target && a.Widgets.Count == count); });
Test("Locked widget refuses direct movement", () => { var a = Layout().Boards[0]; var source = a.Widgets[0]; source.Locked = true; Assert(!DashboardLayoutEngine.Move(a, source.Id, null)); Assert(a.Widgets[0] == source); });
Test("Hidden widgets persist but are excluded from packing", () => { var a = Layout().Boards[0]; a.Widgets[0].Hidden = true; var packed = DashboardLayoutEngine.Arrange(a.Widgets, 1400); Assert(packed.Count == a.Widgets.Count - 1 && packed.All(x => x.Id != a.Widgets[0].Id)); });
Test("Narrow layout is one column without mutating stored spans", () => { var a = Layout().Boards[0]; var before = a.Widgets.Select(x => x.Span).ToArray(); var narrow = DashboardLayoutEngine.Arrange(a.Widgets, 500); Assert(narrow.All(x => x.Span == 12 && x.Column == 0)); Assert(before.SequenceEqual(a.Widgets.Select(x => x.Span))); });
Test("Responsive packing never overlaps and stays inside twelve columns", () =>
{
    var random = new Random(83); var widgets = Enumerable.Range(0, 60).Select(_ => new DashboardWidgetSettings { Span = random.Next(3, 13), Height = random.Next(8, 40) * 20 }).ToArray();
    foreach (var width in new[] { 380d, 700, 1100, 1800 })
    {
        var packed = DashboardLayoutEngine.Arrange(widgets, width);
        foreach (var item in packed) Assert(item.Column >= 0 && item.Column + item.Span <= 12);
        for (var i = 0; i < packed.Count; i++) for (var j = i + 1; j < packed.Count; j++)
        { var a = packed[i]; var b = packed[j]; Assert(a.Column + a.Span <= b.Column || b.Column + b.Span <= a.Column || a.Top + a.Height <= b.Top || b.Top + b.Height <= a.Top, "Cards overlap"); }
    }
});
Test("Masonry fills a short column before extending taller neighbors", () => { var widgets = new[] { new DashboardWidgetSettings { Span = 6, Height = 600 }, new DashboardWidgetSettings { Span = 6, Height = 200 }, new DashboardWidgetSettings { Span = 6, Height = 200 } }; var p = DashboardLayoutEngine.Arrange(widgets, 1500); Assert(p[2].Column == 6 && p[2].Top == 214); });
Test("Expanded measured content can exceed manual limits without overlap", () =>
{
    var widgets = new[] { new DashboardWidgetSettings { Span = 6, Height = 320 }, new DashboardWidgetSettings { Span = 6, Height = 320 }, new DashboardWidgetSettings { Span = 6, Height = 320 }, new DashboardWidgetSettings { Span = 12, Height = 320 } };
    var heights = new[] { 1450d, 225, 780, 1830 };
    var measurements = widgets.Select((widget, index) => (widget.Id, Height: heights[index])).ToDictionary(item => item.Id, item => item.Height);
    foreach (var width in new[] { 380d, 700, 1100, 1800 })
    {
        var packed = DashboardLayoutEngine.Arrange(widgets, width, measuredHeights: measurements);
        Assert(packed.Count == widgets.Length && packed.All(item => item.Height == measurements[item.Id]), "Expanded content height was clamped");
        for (var i = 0; i < packed.Count; i++) for (var j = i + 1; j < packed.Count; j++)
        { var a = packed[i]; var b = packed[j]; Assert(a.Column + a.Span <= b.Column || b.Column + b.Span <= a.Column || a.Top + a.Height <= b.Top || b.Top + b.Height <= a.Top, "Expanded cards overlap"); }
    }
    Assert(widgets.All(widget => widget.Height == 320 && widget.AutoHeight), "Measurement modified the saved layout");
});
Test("Layouts reject unknown presentation and excess capacity", () => { var a = Layout(); a.Boards[0].Widgets[0].Style = "script"; Invalid(a.Validate); a = Layout("empty"); a.Boards[0].Widgets = Enumerable.Range(0, 61).Select(_ => new DashboardWidgetSettings()).ToList(); Invalid(a.Validate); });
Test("Packing deterministic across repeat renders", () => { var a = Layout().Boards[0]; Assert(DashboardLayoutEngine.Arrange(a.Widgets, 1420).SequenceEqual(DashboardLayoutEngine.Arrange(a.Widgets, 1420))); });
Test("Legacy board migrates to an independent personal board", () =>
{
    var settings = new DashboardSettings { Layout = Layout("operations") }; settings.Normalize();
    Assert(settings.PersonalLayout.Boards[0].Widgets.Count == settings.Layout.Boards[0].Widgets.Count);
    settings.PersonalLayout.Boards[0].Widgets[0].Title = "Personnel";
    Assert(settings.Layout.Boards[0].Widgets[0].Title != "Personnel");
    var copy = settings.Copy(); copy.PersonalLayout.Boards.Clear(); Assert(settings.PersonalLayout.Boards.Count == 1);
});
Test("Already separate personal layout survives normalization", () =>
{
    var settings = new DashboardSettings { Layout = Layout(), PersonalLayout = Layout("empty") }; settings.Normalize();
    Assert(settings.PersonalLayout.Boards[0].Widgets.Count == 0 && settings.Layout.Boards[0].Widgets.Count > 0);
});
Test("Gauge ranges and threshold direction validate before repair", () =>
{
    var a = Layout("empty"); var widget = new DashboardWidgetSettings { Presentation = "gauge", TargetValue = 50, WarningValue = 40, CriticalValue = 60 };
    a.Boards[0].Widgets.Add(widget); a.Validate();
    widget.TargetValue = double.PositiveInfinity; Invalid(a.Validate); widget.TargetValue = 0; Invalid(a.Validate);
    widget.TargetValue = 50; widget.CriticalValue = 20; Invalid(a.Validate);
    widget.LowerIsBetter = false; a.Validate(); widget.WarningValue = -1; Invalid(a.Validate);
});
Test("Malformed local thresholds recover without inventing a goal", () =>
{
    var a = Layout("empty"); a.Boards[0].Widgets.Add(new() { Presentation = "gauge", TargetValue = double.NaN, WarningValue = 50, CriticalValue = 10 });
    a.Normalize(); a.Validate(); var widget = a.Boards[0].Widgets[0];
    Assert(widget.TargetValue == null && widget.WarningValue == null && widget.CriticalValue == null);
});
Test("New chart options and thresholds round trip without aliasing", () =>
{
    var a = Layout("empty"); a.Boards[0].Widgets.Add(new() { Presentation = "gauge", TargetValue = 42.75, WarningValue = 20, CriticalValue = 10, LowerIsBetter = false });
    a.Boards[0].Widgets.Add(new() { Kind = "distribution", Presentation = "donut", SourceKey = "status" });
    var b = JsonSerializer.Deserialize<DashboardLayoutSettings>(JsonSerializer.Serialize(a))!; b.Validate();
    Assert(b.Boards[0].Widgets[0].TargetValue == 42.75 && !b.Boards[0].Widgets[0].LowerIsBetter && b.Boards[0].Widgets[1].Presentation == "donut");
});
Test("Dashboard scopes sections favourites and presets round trip without aliasing", () =>
{
    var layout = Layout("empty"); var board = layout.Boards[0];
    board.Filters.Add(new() { SourceKey = "city", Value = "Marseille" });
    board.ComparisonFilters.Add(new() { Kind = "metric", SourceKey = "active", Exclude = true });
    board.ComparisonEnabled = board.CrossFilterOnClick = true; board.CollapsedSections.Add("Délais");
    board.Widgets.Add(new() { Kind = "histogram", SourceKey = "age", Section = "Délais", HistogramBreaks = new() { 2, 8, 15 }, Filters = new() { new() { Kind = "metric", SourceKey = "high" } } });
    layout.CatalogFavorites.Add("metric\nactive"); layout.FilterPresets.Add(new() { Name = "Marseille", Filters = DashboardScopeFilter.CopyList(board.Filters) });
    var copy = layout.Copy(); var imported = JsonSerializer.Deserialize<DashboardLayoutSettings>(JsonSerializer.Serialize(copy))!; imported.Validate();
    Assert(imported.Boards[0].ComparisonEnabled && imported.Boards[0].CrossFilterOnClick && imported.Boards[0].CollapsedSections.Single() == "Délais");
    Assert(imported.CatalogFavorites.Single() == "metric\nactive" && imported.FilterPresets.Single().Filters.Single().Value == "Marseille");
    copy.Boards[0].Widgets[0].Filters[0].SourceKey = "old"; copy.Boards[0].Widgets[0].HistogramBreaks[0] = 4;
    copy.Boards[0].Filters[0].Value = "Paris"; copy.FilterPresets[0].Filters[0].Value = "Paris"; copy.CatalogFavorites.Clear();
    Assert(board.Filters[0].Value == "Marseille" && board.Widgets[0].Filters[0].SourceKey == "high" && board.Widgets[0].HistogramBreaks[0] == 2);
    Assert(layout.FilterPresets[0].Filters[0].Value == "Marseille" && layout.CatalogFavorites.Count == 1);
});
Test("Imported filters reject invalid kinds dates and excess conditions", () =>
{
    var layout = Layout(); var board = layout.Boards[0];
    board.Filters.Add(new() { Kind = "execute", SourceKey = "active" }); Invalid(layout.Validate);
    board.Filters[0] = new() { Kind = "date", SourceKey = "created", Value = "2026-02-30", Value2 = "2026-03-01" }; Invalid(layout.Validate);
    board.Filters[0] = new() { Kind = "date", SourceKey = "created", Value = "2026-03-02", Value2 = "2026-03-01" }; Invalid(layout.Validate);
    board.Filters[0].Value = "2026-03-01"; layout.Validate();
    board.Filters = Enumerable.Range(0, 13).Select(_ => new DashboardScopeFilter { Kind = "metric", SourceKey = "active" }).ToList(); Invalid(layout.Validate);
});
Test("Analytical chart kinds persist and nonfinite histogram boundaries are rejected", () =>
{
    var layout = Layout("empty");
    foreach (var kind in new[] { "matrix", "histogram", "boxplot", "scatter", "history" }) layout.Boards[0].Widgets.Add(new() { Kind = kind, SourceKey = "source" });
    layout.Validate(); layout.Boards[0].Widgets[0].HistogramBreaks.Add(double.NaN); Invalid(layout.Validate);
    layout.Boards[0].Widgets[0].HistogramBreaks = Enumerable.Range(0, 31).Select(x => (double)x).ToList(); Invalid(layout.Validate);
});
Test("Weather formula and priority matrix copy independently from presentation", () =>
{
    var settings = new DashboardSettings { Weather = new() { PriorityWeight = 90 }, PriorityRules = new() { ["4:4"] = 4 }, AssignmentCountingMode = "fractional" };
    var copy = settings.Copy(); copy.Weather.PriorityWeight = 10; copy.PriorityRules["4:4"] = 6;
    Assert(settings.Weather.PriorityWeight == 90 && settings.PriorityRules["4:4"] == 4 && copy.AssignmentCountingMode == "fractional");
    var invalid = new DashboardWeatherSettings { WatchThreshold = 60, RainThreshold = 40 }; Invalid(invalid.Validate);
    invalid.Normalize(); invalid.Validate(); Assert(invalid.WatchThreshold == 10);
});
Test("Legacy section labels migrate without losing order hidden widgets or collapse state", () =>
{
    var layout = Layout("empty"); var board = layout.Boards[0];
    board.Widgets.Add(new() { Id = "one", Section = "Urgences" });
    board.Widgets.Add(new() { Id = "two", Section = "Terrain", Hidden = true }); board.CollapsedSections.Add("Terrain");
    layout.Validate(); layout.Normalize(); layout.Validate();
    Assert(board.Sections.Select(s => s.Key).SequenceEqual(new[] { "Urgences", "Terrain" }));
    board.Sections[0].Title = "À traiter aujourd’hui"; layout.Normalize();
    Assert(board.Widgets[0].Section == "Urgences" && board.Sections[0].Title == "À traiter aujourd’hui" && board.CollapsedSections.Single() == "Terrain");
});
Test("Empty sections descriptions colours visibility and density persist independently", () =>
{
    var layout = Layout("empty"); var board = layout.Boards[0]; board.Description = "Organisation locale"; board.Density = "compact";
    board.Sections.Add(new() { Key = "empty", Title = "À organiser", Description = "Aucun widget encore", Accent = "teal", Hidden = true });
    var copy = layout.Copy(); copy.Boards[0].Sections[0].Title = "Copie";
    Assert(board.Sections[0].Title == "À organiser");
    var roundTrip = JsonSerializer.Deserialize<DashboardLayoutSettings>(JsonSerializer.Serialize(layout))!; roundTrip.Normalize(); roundTrip.Validate();
    Assert(roundTrip.Boards[0].Sections.Single().Hidden && roundTrip.Boards[0].Sections[0].Description == "Aucun widget encore" && roundTrip.Boards[0].Density == "compact");
});
Test("Imported section metadata rejects duplicate identities unsafe styles and oversize text", () =>
{
    var layout = Layout("empty"); var board = layout.Boards[0]; board.Sections.Add(new() { Key = "stable", Title = "Section" });
    board.Sections.Add(board.Sections[0].Copy()); Invalid(layout.Validate); board.Sections.RemoveAt(1);
    board.Sections[0].Accent = "url(external)"; Invalid(layout.Validate); board.Sections[0].Accent = "blue";
    board.Sections[0].Description = new('x', 601); Invalid(layout.Validate); board.Sections[0].Description = "";
    board.Density = "arbitrary"; Invalid(layout.Validate);
});
Test("Section capacity includes legacy widget references and local repair retains occupied sections", () =>
{
    var layout = Layout("empty"); var board = layout.Boards[0];
    board.Sections = Enumerable.Range(0, 60).Select(i => new DashboardSectionSettings { Key = "empty-" + i }).ToList();
    board.Widgets.Add(new() { Section = "occupied" }); Invalid(layout.Validate);
    layout.Normalize(); layout.Validate(); Assert(board.Sections.Count == 60 && board.Sections.Any(section => section.Key == "occupied"));
});
Test("Default section identity is stable through repeated normalization and JSON", () =>
{
    var layout = Layout("overview"); layout.Normalize(); var before = JsonSerializer.Serialize(layout);
    layout.Normalize(); Assert(JsonSerializer.Serialize(layout) == before);
    var roundTrip = JsonSerializer.Deserialize<DashboardLayoutSettings>(before)!; roundTrip.Normalize(); roundTrip.Validate();
    Assert(roundTrip.Boards[0].Sections.Single().Key == "" && JsonSerializer.Serialize(roundTrip) == before);
});
Test("Native pilot configuration has independent rules geometry and no custom-board capacity cost", () =>
{
    var settings = new DashboardSettings(); settings.Normalize();
    settings.Business.WeatherRules.Add(new() { Id = "team", Label = "Équipe fictive", Kind = "team", Values = new() { "Groupe fictif" }, Visible = false, WeatherEnabled = false, OrangeAbove = 3, RedAbove = 6 });
    var board = DashboardLayoutSettings.CreateBoard("Groupes", "empty"); board.Id = "business-groups";
    settings.Business.BoardOverrides.Add(board); settings.Business.Validate();
    var copied = settings.Copy(); copied.Business.WeatherRules[0].Values.Add("Autre groupe fictif"); copied.Business.BoardOverrides[0].Name = "Copie";
    Assert(settings.Business.WeatherRules[0].Values.Count == 1 && settings.Business.BoardOverrides[0].Name == "Groupes" && settings.Layout.Boards.Count == 1);
    var roundTrip = JsonSerializer.Deserialize<DashboardSettings>(JsonSerializer.Serialize(settings))!; roundTrip.Business.Validate();
    Assert(!roundTrip.Business.WeatherRules[0].Visible && !roundTrip.Business.WeatherRules[0].WeatherEnabled && roundTrip.Business.FreshnessWarningHours == 48);
});
Test("Native pilot settings reject duplicate rules foreign overrides and contradictory thresholds", () =>
{
    var settings = new BusinessDashboardSettings { WeatherRules = new() { new() { Id = "r", Label = "Volume", OrangeAbove = 10, RedAbove = 5 } } };
    Invalid(settings.Validate); settings.WeatherRules[0].RedAbove = 20; settings.WeatherRules.Add(settings.WeatherRules[0].Copy()); Invalid(settings.Validate);
    settings.WeatherRules.RemoveAt(1); settings.BoardOverrides.Add(DashboardLayoutSettings.CreateBoard("Foreign", "empty")); Invalid(settings.Validate);
    settings.BoardOverrides.Clear(); settings.WeatherRules[0].Kind = "team"; settings.WeatherRules[0].Values = Enumerable.Range(1, 33).Select(index => "Group " + index).ToList(); Invalid(settings.Validate);
});
Test("Integrated composition accepts its 120 cards while ordinary boards retain a sixty-card bound", () =>
{
    var layout = Layout("empty"); var board = layout.Boards[0];
    board.Id = "business-workspace"; layout.ActiveBoardId = board.Id;
    board.Widgets = Enumerable.Range(0, 120).Select(index => new DashboardWidgetSettings { Id = "w" + index, Kind = "note" }).ToList();
    layout.Validate(); layout.Normalize(); Assert(board.Widgets.Count == 120);
    board.Widgets.Add(new() { Id = "too-many", Kind = "note" }); Invalid(layout.Validate);
    board.Widgets.RemoveAt(120); board.Id = "ordinary"; layout.ActiveBoardId = board.Id; Invalid(layout.Validate);
    board.Widgets.RemoveRange(60, 60); layout.Validate();
});
var failures = 0;
foreach (var (name, run) in checks) { try { run(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
Console.WriteLine($"{checks.Count - failures}/{checks.Count} checks passed.");
return failures == 0 ? 0 : 1;
