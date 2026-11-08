using System.Text.Json;
using GlpiPilot.Core;

internal static class WorkspaceOverviewChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Func<SessionFixture, Task> action) => tests.Add(("Workspace overview: " + name, async () => { await using var f = new SessionFixture(); await action(f); }));
        Add("missing assignment and due fields remain unknown rather than zero", async f =>
        {
            var result = await f.Call("workplace.overview");
            var metrics = result.GetProperty("metrics").EnumerateArray().ToDictionary(item => item.GetProperty("key").GetString()!);
            Check(metrics["active"].GetProperty("value").GetInt32() == 1);
            Check(metrics["unassigned"].GetProperty("value").ValueKind == JsonValueKind.Null && metrics["overdue"].GetProperty("value").ValueKind == JsonValueKind.Null);
        });
        Add("the combined team scope and record list count the same original source rows once", async f =>
        {
            f.App.LoadSessionFixture(Source()); var revision = f.App.Revision;
            var scope = new[] { "group:Support", "group:Réseau" };
            var overview = await f.Call("workplace.overview", new { focusKeys = scope });
            Check(overview.GetProperty("metrics").EnumerateArray().Single(item => item.GetProperty("key").GetString() == "active").GetProperty("value").GetInt32() == 3);
            var list = await f.Call("records.list", new { revision, focusKeys = scope, indices = new[] { 2, 0, 1, 3 }, transform = new { }, limit = 2 });
            Check(list.GetProperty("total").GetInt32() == 3 && list.GetProperty("rows")[0].GetProperty("index").GetInt32() == 2);
            var page = await f.Call("records.list", new { revision, focusKeys = scope, indices = new[] { 2, 0, 1, 3 }, transform = new { }, limit = 2, offset = 2 });
            Check(page.GetProperty("rows").GetArrayLength() == 1 && page.GetProperty("rows")[0].GetProperty("index").GetInt32() == 1);
        });
        Add("personal identity is a filter and unassigned work remains independently selectable", async f =>
        {
            f.App.Settings.Dashboard.PersonalTechnician = "ALEX"; f.App.LoadSessionFixture(Source());
            var mine = await f.Call("records.list", new { revision = f.App.Revision, mine = true });
            Check(mine.GetProperty("total").GetInt32() == 2);
            var unassigned = await f.Call("records.list", new { revision = f.App.Revision, technicianKeys = new[] { "@unassigned" } });
            Check(unassigned.GetProperty("total").GetInt32() == 1 && unassigned.GetProperty("rows")[0].GetProperty("index").GetInt32() == 2);
            var everyone = await f.Call("records.list", new { revision = f.App.Revision }); Check(everyone.GetProperty("total").GetInt32() == 4);
        });
        Add("a drilldown expires on source change and no-source overview remains available", async f =>
        {
            var overview = await f.Call("workplace.overview"); var token = overview.GetProperty("token").GetString();
            var rows = await f.Call("workplace.overview.rows", new { token, key = "active" }); Check(rows.GetProperty("rows").GetArrayLength() == 1);
            f.App.LoadSessionFixture(Source()); var rejected = false;
            try { await f.Call("workplace.overview.rows", new { token, key = "active" }); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected); await f.SelectB(); Check(!(await f.Call("workplace.overview")).GetProperty("hasSource").GetBoolean());
        });
        return tests;
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Workspace scope contract failed."); }
    private static ExtractionResult Source() => new()
    {
        Headers = ["ID", "Titre", "Statut", "Attribué à - Technicien", "Attribué à - Groupe de techniciens"],
        Rows = [["1", "Partagé", "Nouveau", "Alex", "Support\nRéseau"], ["2", "Réseau", "Nouveau", "Alex", "Réseau"],
            ["3", "À attribuer", "Nouveau", "", "Support"], ["4", "Autre", "Nouveau", "Sam", "Autre"]],
        SourceUrl = SessionFixture.SourceA, ExtractedAt = DateTimeOffset.Now, Complete = true,
        Provenance = new() { SourceKind = "glpi", RequestedUrl = SessionFixture.SourceA, SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "synthetic" }
    };
}
