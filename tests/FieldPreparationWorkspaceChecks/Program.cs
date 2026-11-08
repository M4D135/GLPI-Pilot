using System.Text.Json;
using GlpiPilot.Core;

var root = Path.Combine(Path.GetTempPath(), "glpi-field-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = new List<(string Name, Func<Task> Run)>();
void Add(string name, Func<Task> run) => checks.Add((name, run));
TeamWorkspaceStore Store(string name) => new(Path.Combine(root, name + ".sqlite"));
FieldPreparationDocument Document(string key = "ticket-a")
{
    var plan = FieldPreparationService.NewPlan(new("ticket", key), "Intervention de test");
    return new() { Plan = plan, ObservedAtUtc = DateTimeOffset.UtcNow,
        Tickets = new() { new(key, "42", "Ticket de test", "Description fictive") } };
}
void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
async Task Refuse(Func<Task> run)
{
    try { await run(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
    throw new Exception("Expected a refusal");
}
FieldPreparationDocument MaterialDocument(string key)
{
    var d = Document(key); var catalogue = FieldMaterialDefaults.Create();
    d.Plan = FieldPreparationService.AddReference(d.Plan, catalogue, catalogue.References[0].Id, 2); return d;
}
Add("A preparation round-trips through the strict shared-document codec", () =>
{
    var d = Document(); var item = FieldPreparationDocuments.ForPlan(d, "Test");
    var copy = TeamWorkspaceCodec.Read(TeamWorkspaceCodec.Write(item));
    Check(FieldPreparationDocuments.ReadPlan(copy).Tickets[0].Description == "Description fictive"); return Task.CompletedTask;
});
Add("A mismatched identity or ticket detail is rejected", async () =>
{
    var item = FieldPreparationDocuments.ForPlan(Document(), "Test"); item.Id = Guid.NewGuid().ToString("N");
    await Refuse(() => { TeamWorkspaceCodec.Validate(item); return Task.CompletedTask; });
    var d = Document(); d.Tickets[0] = d.Tickets[0] with { Key = "another-ticket" };
    await Refuse(() => { FieldPreparationDocuments.ForPlan(d, "Test"); return Task.CompletedTask; });
});
Add("Saved preparations survive a new store instance with pending metadata", async () =>
{
    var first = Store("reopen"); var d = MaterialDocument("persist"); var saved = await first.SaveFieldPreparationAsync(d, 0, "Test");
    Check(saved.Revision == 1 && saved.Pending);
    var second = Store("reopen"); var read = (await second.GetAsync(saved.Id))!;
    Check(read.Revision == 1 && read.Pending && FieldPreparationDocuments.ReadPlan(read).Plan.Lines[0].Quantity == 2);
});
Add("A stale writer cannot replace another workstation's locally saved version", async () =>
{
    var a = Store("cas"); var b = Store("cas"); var d = Document("cas");
    var first = await a.SaveFieldPreparationAsync(d, 0, "A");
    var recent = FieldPreparationService.Copy(d); recent.Plan.PreparationNote = "Version récente";
    await b.SaveFieldPreparationAsync(recent, first.Revision, "B");
    d.Plan.PreparationNote = "Version périmée";
    await Refuse(() => a.SaveFieldPreparationAsync(d, first.Revision, "A"));
    Check(FieldPreparationDocuments.ReadPlan((await a.GetAsync(first.Id))!).Plan.PreparationNote == "Version récente");
});
Add("The complete preparation list keeps payloads and never uses truncated generic rows", async () =>
{
    var store = Store("list"); await store.SaveFieldPreparationAsync(Document("one"), 0, "Test");
    await store.SaveFieldPreparationAsync(Document("two"), 0, "Test");
    var rows = await store.FieldPreparationsAsync(); Check(rows.Count == 2 && rows.All(row => row.Payload is not null && row.Pending));
});
Add("An unchanged save is idempotent and does not increase the local revision", async () =>
{
    var store = Store("same"); var d = Document("same"); var one = await store.SaveFieldPreparationAsync(d, 0, "Test");
    var two = await store.SaveFieldPreparationAsync(d, one.Revision, "Test"); Check(one.Revision == two.Revision);
});
Add("The catalogue is versioned and refuses a stale edit", async () =>
{
    var store = Store("catalogue"); var c = FieldMaterialDefaults.Create(); var first = await store.SaveFieldCatalogueAsync(c, 0, "A");
    c.Categories[0] = c.Categories[0] with { Name = "Nouveau libellé" }; await store.SaveFieldCatalogueAsync(c, first.Revision, "B");
    await Refuse(() => store.SaveFieldCatalogueAsync(c, first.Revision, "A"));
});
Add("Catalogue identifiers must be archived instead of silently deleted", async () =>
{
    var store = Store("deletion"); var c = FieldMaterialDefaults.Create(); c.References.RemoveAt(0);
    await Refuse(() => store.SaveFieldCatalogueAsync(c, 0, "Test"));
    Check(await store.GetAsync(FieldPreparationDocuments.CatalogueId) is null);
});
Add("Renaming a reference never rewrites a saved line snapshot", async () =>
{
    var store = Store("rename"); var d = MaterialDocument("rename"); var saved = await store.SaveFieldPreparationAsync(d, 0, "Test");
    var c = FieldMaterialDefaults.Create(); var oldLabel = c.References[0].Label;
    c.References[0] = c.References[0] with { Label = "Référence renommée" }; await store.SaveFieldCatalogueAsync(c, 0, "Test");
    d.Plan.Lines[0].PreparedQuantity = 1; var changed = await store.SaveFieldPreparationAsync(d, saved.Revision, "Test");
    Check(FieldPreparationDocuments.ReadPlan(changed).Plan.Lines[0].Label == oldLabel);
});
Add("Archived references remain readable but cannot create a new material line", async () =>
{
    var store = Store("archive"); var c = FieldMaterialDefaults.Create(); c.References[0] = c.References[0] with { Archived = true };
    await store.SaveFieldCatalogueAsync(c, 0, "Test");
    await Refuse(() => store.SaveFieldPreparationAsync(MaterialDocument("archive"), 0, "Test"));
    Check((await store.FieldPreparationsAsync()).Count == 0);
});
Add("Forged material snapshots are rejected without a partial write", async () =>
{
    var store = Store("forged"); var d = MaterialDocument("forged"); d.Plan.Lines[0].Label = "Not in the catalogue";
    await Refuse(() => store.SaveFieldPreparationAsync(d, 0, "Test")); Check((await store.CountsAsync()).Items == 0);
});
Add("Changing an existing line's unit or reference is rejected", async () =>
{
    var store = Store("unit"); var d = MaterialDocument("unit"); var saved = await store.SaveFieldPreparationAsync(d, 0, "Test");
    d.Plan.Lines[0].Unit = "metre"; await Refuse(() => store.SaveFieldPreparationAsync(d, saved.Revision, "Test"));
});
Add("Explicitly no material is persistent and distinct from an empty unspecified list", async () =>
{
    var store = Store("none"); var d = Document("none"); d.Plan.NoMaterialsRequired = true;
    var saved = await store.SaveFieldPreparationAsync(d, 0, "Test");
    Check(FieldPreparationService.Readiness(FieldPreparationDocuments.ReadPlan(saved).Plan) == "none");
    Check(FieldPreparationService.Readiness(Document("unspecified").Plan) == "unspecified");
});
Add("A cancelled write leaves neither a preparation nor an outbox entry", async () =>
{
    var store = Store("cancel"); using var cancel = new CancellationTokenSource(); cancel.Cancel();
    try { await store.SaveFieldPreparationAsync(Document("cancel"), 0, "Test", cancel.Token); throw new Exception("Cancellation expected"); }
    catch (OperationCanceledException) { }
    Check((await store.CountsAsync()).Items == 0 && (await store.CountsAsync()).Pending == 0);
});
Add("Unknown payload fields do not silently survive transport", async () =>
{
    var item = FieldPreparationDocuments.ForPlan(Document("unknown"), "Test");
    var json = item.Payload!.Value.GetRawText();
    item.Payload = JsonDocument.Parse(json[..^1] + ",\"privateDeployment\":{}}").RootElement.Clone();
    await Refuse(() => { TeamWorkspaceCodec.Validate(item); return Task.CompletedTask; });
});
var failed = 0;
try
{
    foreach (var check in checks)
    {
        try { await check.Run(); Console.WriteLine("PASS: " + check.Name); }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL: " + check.Name + "\n" + error); }
    }
    Console.WriteLine($"{checks.Count - failed}/{checks.Count} field workspace checks passed.");
}
finally { try { Directory.Delete(root, true); } catch (IOException) { } }
return failed == 0 ? 0 : 1;
