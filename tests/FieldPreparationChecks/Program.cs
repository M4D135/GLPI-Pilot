using GlpiPilot.Core;
using System.Text.Json;

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch (InvalidDataException) { return; } catch (InvalidOperationException) { return; } throw new Exception("Expected validation failure"); }
void Test(string label, Action action) { action(); passed++; Console.WriteLine("PASS: " + label); }
FieldPreparationPlan Plan(string key = "installation-a:ticket-42") => FieldPreparationService.NewPlan(new("ticket", key), "Intervention exemple");
var catalogue = FieldMaterialDefaults.Create();

Test("generic categories and references form a valid editable catalogue", () => {
    FieldPreparationService.Validate(catalogue);
    Check(catalogue.Categories.Count == 16 && catalogue.References.Count >= 40, "Generic catalogue missing");
    Check(catalogue.References.All(item => item.Code.Length == 0), "Defaults are not organization-specific references");
});
Test("owner identity is stable and separates tickets, manual interventions and repeated visits", () => {
    Check(Plan().Id == Plan().Id, "Unstable identity");
    Check(Plan().Id != Plan("installation-b:ticket-42").Id, "Different portals mixed");
    Check(Plan().Id != FieldPreparationService.NewPlan(new("intervention", "installation-a:ticket-42"), "Manual").Id, "Owner types mixed");
    Check(FieldPreparationService.IdFor(new("intervention", "same", "visit1")) != FieldPreparationService.IdFor(new("intervention", "same", "visit2")), "Occurrences mixed");
});
Test("missing material and explicitly no material are distinct", () => {
    var p = Plan(); Check(FieldPreparationService.Readiness(p) == "unspecified", "Missing list marked ready");
    p.NoMaterialsRequired = true; Check(FieldPreparationService.Readiness(p) == "none", "Explicit absence lost");
});
Test("adding a reference snapshots its name and preserves the original draft", () => {
    var original = Plan(); var before = JsonSerializer.Serialize(original);
    var p = FieldPreparationService.AddReference(original, catalogue, "mouse-usb", 2);
    Check(JsonSerializer.Serialize(original) == before && p.Lines.Count == 1 && p.Lines[0].Quantity == 2, "Draft mutated");
    var updated = FieldPreparationService.Copy(catalogue);
    updated.References = updated.References.Select(item => item.Id == "mouse-usb" ? item with { Label = "Autre souris" } : item).ToList();
    Check(p.Lines[0].Label != updated.References.Single(item => item.Id == "mouse-usb").Label, "Historic label overwritten");
});
Test("negative or fractional piece quantities fail without changing inputs", () => {
    var p = Plan(); var before = JsonSerializer.Serialize(p);
    Throws(() => FieldPreparationService.AddReference(p, catalogue, "mouse-usb", -1));
    Throws(() => FieldPreparationService.AddReference(p, catalogue, "mouse-usb", 1.5m));
    Check(JsonSerializer.Serialize(p) == before, "Rejected request changed draft");
});
Test("metres permit precise planned lengths", () => {
    var p = FieldPreparationService.AddReference(Plan(), catalogue, "cable-roll", 2.5m);
    Check(p.Lines[0].Quantity == 2.5m, "Length lost");
});
Test("archived references cannot create new requests but old lines remain valid", () => {
    var p = FieldPreparationService.AddReference(Plan(), catalogue, "mouse-usb", 1);
    var archived = FieldPreparationService.Copy(catalogue);
    archived.References = archived.References.Select(item => item.Id == "mouse-usb" ? item with { Archived = true } : item).ToList();
    Throws(() => FieldPreparationService.AddReference(Plan(), archived, "mouse-usb", 1));
    FieldPreparationService.Validate(p);
});
Test("categories can be renamed without losing reference identity", () => {
    var edited = FieldPreparationService.Copy(catalogue);
    edited.Categories = edited.Categories.Select(category => category.Id == "peripherals" ? category with { Name = "Accessoires de bureau" } : category).ToList();
    FieldPreparationService.Validate(edited);
    Check(FieldPreparationService.AddReference(Plan(), edited, "mouse-usb", 1).Lines[0].Category == "Accessoires de bureau", "Category rename ignored");
});
Test("prepared quantity drives readiness and never exceeds requested quantity", () => {
    var p = FieldPreparationService.AddReference(Plan(), catalogue, "mouse-usb", 3);
    Check(FieldPreparationService.Readiness(p) == "to-prepare", "Unexpected initial readiness");
    p.Lines[0].PreparedQuantity = 1; Check(FieldPreparationService.Readiness(p) == "partial", "Partial lost");
    p.Lines[0].PreparedQuantity = 3; Check(FieldPreparationService.Readiness(p) == "ready", "Ready lost");
    p.Lines[0].PreparedQuantity = 4; Throws(() => FieldPreparationService.Validate(p));
});
Test("one intervention shared by multiple technicians or tours is counted once", () => {
    var p = FieldPreparationService.AddReference(Plan(), catalogue, "mouse-usb", 2);
    p.ParticipantKeys = new() { "tech-a", "tech-b" }; p.TourIds = new() { "tour-a", "tour-b" };
    var summary = FieldPreparationService.Summarize(new[] { p, FieldPreparationService.Copy(p), p });
    Check(summary.PlanCount == 1 && summary.Materials.Single().Quantity == 2, "Duplicate material total");
});
Test("different interventions aggregate the same reference and keep traceability", () => {
    var a = FieldPreparationService.AddReference(Plan("ticket-a"), catalogue, "mouse-usb", 2);
    var b = FieldPreparationService.AddReference(Plan("ticket-b"), catalogue, "mouse-usb", 3); b.Lines[0].PreparedQuantity = 1;
    var sum = FieldPreparationService.Summarize(new[] { a, b }).Materials.Single();
    Check(sum.Quantity == 5 && sum.PreparedQuantity == 1 && sum.RemainingQuantity == 4 && sum.PlanIds.Count == 2, "Incorrect aggregation");
});
Test("different versions of one plan never silently win", () => {
    var a = Plan(); var b = FieldPreparationService.Copy(a); b.NoMaterialsRequired = true;
    Throws(() => FieldPreparationService.Summarize(new[] { a, b }));
});
Test("cancelled and finished work does not inflate departure quantities", () => {
    var a = FieldPreparationService.AddReference(Plan("cancelled"), catalogue, "mouse-usb", 2); a.State = "cancelled";
    var b = FieldPreparationService.AddReference(Plan("done"), catalogue, "mouse-usb", 4); b.State = "done";
    Check(FieldPreparationService.Summarize(new[] { a, b }).Materials.Count == 0, "Closed work included");
});
Test("no material cannot coexist with a hidden list", () => {
    var p = FieldPreparationService.AddReference(Plan(), catalogue, "mouse-usb", 1); p.NoMaterialsRequired = true;
    Throws(() => FieldPreparationService.Validate(p));
});
Test("distinct units are not silently summed", () => {
    var a = FieldPreparationService.AddReference(Plan("a"), catalogue, "cable-roll", 2);
    var b = FieldPreparationService.AddReference(Plan("b"), catalogue, "cable-roll", 3); b.Lines[0].Unit = "lot";
    var summary = FieldPreparationService.Summarize(new[] { a, b });
    Check(summary.Materials.Count == 2 && summary.Warnings.Count == 1, "Mixed units merged");
});
Test("print output encodes ticket and material text and does not execute it", () => {
    var p = FieldPreparationService.AddReference(Plan(), catalogue, "mouse-usb", 2, "<img src=x onerror=alert(1)>");
    p.Title = "<script>alert(1)</script>";
    var html = FieldPreparationReport.Render(new[] { p, p }, new[] { new FieldSheetContext(p.Id,
        new[] { new FieldTicketDetail(p.Owner.Key, "42", "Ticket exemple", "<script>bad()</script>") }, new[] { "Technicien exemple" }) },
        "Fiches terrain", DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
    Check(!html.Contains("<script>") && html.Contains("&lt;script&gt;") && html.Contains("&lt;img"), "Markup injection");
    Check(html.Split("<article class=\"sheet\">").Length == 2 && html.Contains("Liste de départ"), "Print deduplication failed");
});
Test("printing missing details tells the technician rather than inventing content", () => {
    var html = FieldPreparationReport.Render(new[] { Plan() }, Array.Empty<FieldSheetContext>(), "Fiche", DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
    Check(html.Contains("n’a pas été fourni") && html.Contains("Matériel à préciser"), "Unknown data hidden");
});
Test("print context cannot leak an unselected ticket sheet", () => {
    Throws(() => FieldPreparationReport.Render(new[] { Plan() }, new[] { new FieldSheetContext("not-selected", Array.Empty<FieldTicketDetail>(), Array.Empty<string>()) },
        "Fiche", DateTimeOffset.UtcNow, TimeZoneInfo.Utc));
});
Console.WriteLine($"{passed}/{passed} field preparation checks passed.");
