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
Test("Every preset is valid and bounded", () => { foreach (var preset in new[] { "overview", "operations", "analysis", "personal", "team", "deadlines", "quality", "geography", "empty" }) Layout(preset).Validate(); });
Test("Deep copy isolates nested boards and widgets", () => { var a = Layout(); var b = a.Copy(); b.Boards[0].Widgets[0].Title = "B"; b.Boards[0].Name = "B"; Assert(a.Boards[0].Name == "Test" && a.Boards[0].Widgets[0].Title == ""); });
Test("Settings deep copy preserves local identity without sharing layout", () => { var a = new DashboardSettings { Layout = Layout(), PersonalTechnician = "A" }; var b = a.Copy(); b.Layout.Boards.Clear(); Assert(a.Layout.Boards.Count == 1 && b.PersonalTechnician == "A"); });
Test("Portable layout JSON round-trips all presentation settings", () => { var a = Layout("empty"); a.Boards[0].Widgets.Add(new() { Kind = "note", Note = "Ligne 1\nLigne 2", Span = 7, Height = 440, Locked = true, Hidden = true, Style = "accent", Accent = "teal", Presentation = "compact", Title = "Note" }); var b = JsonSerializer.Deserialize<DashboardLayoutSettings>(JsonSerializer.Serialize(a))!; b.Validate(); Assert(b.Boards[0].Widgets[0].Note == "Ligne 1\nLigne 2" && b.Boards[0].Widgets[0].Locked && b.Boards[0].Widgets[0].Hidden && b.Boards[0].Widgets[0].Span == 7); });
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
var failures = 0;
foreach (var (name, run) in checks) { try { run(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
Console.WriteLine($"{checks.Count - failures}/{checks.Count} checks passed.");
return failures == 0 ? 0 : 1;
