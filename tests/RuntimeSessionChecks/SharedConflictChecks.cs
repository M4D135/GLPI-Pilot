using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class SharedConflictRuntimeChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string Name, Func<Task> Run)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add(("Shared versions: " + name, async () => { await using var f = new SessionFixture(); await run(f); }));
        Add("identical summaries expose the actual changed business payload without changing local state", async f =>
        {
            var (store, current, incoming, id) = await Pair(new { schedule = new[] { new { start = "08:00", end = "12:00" } } }, new { schedule = new[] { new { start = "09:00", end = "12:00" } } });
            var baseline = TeamWorkspaceCodec.Write((await store.GetAsync(current.Id))!); var counts = await store.CountsAsync();
            var review = await f.Call("workspace.conflict.compare", new { id = current.Id, conflictId = id });
            var differences = review.GetProperty("comparison").GetProperty("differences").EnumerateArray().ToArray();
            Require(differences.Length == 1 && differences[0].GetProperty("path").GetString() == "/payload/schedule/0/start");
            Require(differences[0].GetProperty("current").GetString() == "08:00" && differences[0].GetProperty("incoming").GetString() == "09:00");
            Require(review.GetProperty("comparison").GetProperty("scanComplete").GetBoolean() && !review.GetProperty("comparison").GetProperty("truncated").GetBoolean());
            Require(baseline == TeamWorkspaceCodec.Write((await store.GetAsync(current.Id))!) && counts == await store.CountsAsync());
        });
        Add("field identity preserves slash, tilde, null, empty text and literal markup", async f =>
        {
            var before = JsonSerializer.Deserialize<JsonElement>("{\"a/b\":\"<b>littéral</b>\\nDeuxième ligne\",\"a~b\":null,\"empty\":\"\",\"old\":1}");
            var after = JsonSerializer.Deserialize<JsonElement>("{\"a/b\":\"<b>autre</b>\\nDeuxième ligne\",\"a~b\":\"\",\"empty\":null,\"new\":1}");
            var (_, current, _, id) = await Pair(before, after);
            var review = await f.Call("workspace.conflict.compare", new { id = current.Id, conflictId = id });
            var differences = review.GetProperty("comparison").GetProperty("differences").EnumerateArray().ToDictionary(row => row.GetProperty("path").GetString()!);
            Require(differences["/payload/a~1b"].GetProperty("current").GetString() == "<b>littéral</b>\nDeuxième ligne");
            Require(differences["/payload/a~0b"].GetProperty("currentType").GetString() == "null" && differences["/payload/a~0b"].GetProperty("incomingType").GetString() == "text");
            Require(differences["/payload/old"].GetProperty("change").GetString() == "removed" && differences["/payload/new"].GetProperty("change").GetString() == "added");
            Require(differences["/payload/empty"].GetProperty("current").GetString() == "" && differences["/payload/empty"].GetProperty("incomingType").GetString() == "null");
        });
        Add("differences after the display boundary are detected and exports retain both full versions", async f =>
        {
            var prefix = new string('x', TeamWorkspaceConflictComparison.MaximumValueCharacters - 1) + "🧰";
            var (store, current, incoming, id) = await Pair(new { text = prefix + "CURRENT_END" }, new { text = prefix + "INCOMING_END" });
            var review = await f.Call("workspace.conflict.compare", new { id = current.Id, conflictId = id });
            var comparison = review.GetProperty("comparison"); var row = comparison.GetProperty("differences")[0];
            Require(comparison.GetProperty("differencesTotal").GetInt32() == 1 && comparison.GetProperty("truncated").GetBoolean());
            var shown = row.GetProperty("current").GetString()!; Require(shown.Length == prefix.Length - 2 && !char.IsHighSurrogate(shown[^1]));
            f.Host.SavePath = Path.Combine(f.Root, "archived-versions.json");
            await f.Call("workspace.conflict.export", new { id = current.Id, conflictId = id });
            using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(f.Host.SavePath));
            var json = exported.RootElement;
            Require(json.GetProperty("schema").GetString() == "glpi-pilot.team-conflict" && json.GetProperty("itemId").GetString() == current.Id);
            Require(json.GetProperty("current").GetProperty("payload").GetProperty("text").GetString() == prefix + "CURRENT_END");
            Require(json.GetProperty("incoming").GetProperty("payload").GetProperty("text").GetString() == prefix + "INCOMING_END");
            Require(json.GetProperty("currentHash").GetString() == TeamWorkspaceCodec.ContentHash(current) && json.GetProperty("incomingHash").GetString() == TeamWorkspaceCodec.ContentHash(incoming));
            Require((await store.CountsAsync()).Pending == 0);
        });
        Add("a conflict from another item and active database destinations are refused without mutation", async f =>
        {
            var (store, current, _, id) = await Pair(new { value = 1 }, new { value = 2 });
            await Refuse(() => f.Call("workspace.conflict.compare", new { id = Guid.NewGuid().ToString("N"), conflictId = id }));
            await Refuse(() => f.Call("workspace.conflict.export", new { id = current.Id, conflictId = "' OR 1=1 --" }));
            var baseline = TeamWorkspaceCodec.Write((await store.GetAsync(current.Id))!);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                f.Host.SavePath = new LibraryPreferences().DatabasePath + suffix;
                await Refuse(() => f.Call("workspace.conflict.export", new { id = current.Id, conflictId = id }));
            }
            f.Host.SavePath = null;
            Require((await f.Call("workspace.conflict.export", new { id = current.Id, conflictId = id })).GetProperty("cancelled").GetBoolean());
            Require(TeamWorkspaceCodec.Write((await store.GetAsync(current.Id))!) == baseline);
        });
        Add("older archived pairs remain reachable through stable paginated comparisons", async f =>
        {
            var (store, current, _, _) = await Pair(new { value = 0 }, new { value = 1 });
            for (var i = 2; i <= 8; i++)
            {
                current.CentralRevision = i;
                var incoming = current.Copy(); incoming.Payload = JsonSerializer.SerializeToElement(new { value = i });
                await store.ApplyChangeAsync(new(i, current, incoming), default);
            }
            var first = await f.Call("workspace.conflicts", new { id = current.Id, offset = 0, limit = 5 });
            var second = await f.Call("workspace.conflicts", new { id = current.Id, offset = 5, limit = 5 });
            Require(first.GetProperty("total").GetInt64() == 8 && first.GetProperty("items").GetArrayLength() == 5 && second.GetProperty("items").GetArrayLength() == 3);
            var ids = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(item => item.GetProperty("id").GetString()!).ToArray();
            Require(ids.Distinct().Count() == 8);
            var archived = await f.Call("workspace.conflict.compare", new { id = current.Id, conflictId = ids[^1] });
            Require(archived.GetProperty("id").GetString() == ids[^1]);
        });
        Add("large comparisons are explicitly bounded and cancellation is honored", _ =>
        {
            var a = Item(JsonSerializer.SerializeToElement(Enumerable.Range(0, TeamWorkspaceConflictComparison.MaximumNodes + 50).ToArray()));
            var b = a.Copy(); b.Payload = JsonSerializer.SerializeToElement(Enumerable.Range(1, TeamWorkspaceConflictComparison.MaximumNodes + 50).ToArray());
            var result = TeamWorkspaceConflictComparison.Compare(a, b);
            Require(!result.ScanComplete && result.Truncated && result.Differences.Count <= TeamWorkspaceConflictComparison.MaximumDifferences);
            Require(result.DifferencesTotal > result.Differences.Count);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { TeamWorkspaceConflictComparison.Compare(a, b, cancel.Token); throw new Exception("Comparison ignored cancellation."); }
            catch (OperationCanceledException) { }
            return Task.CompletedTask;
        });
        return tests;
    }

    private static TeamWorkspaceItem Item(JsonElement payload) => new()
    { Id = Guid.NewGuid().ToString("N"), Kind = "statistics", Title = "Même résumé", Body = "Contenu métier conservé", Status = "published", Author = "Équipe synthétique", CentralRevision = 1, Payload = payload };
    private static async Task<(TeamWorkspaceStore Store, TeamWorkspaceItem Current, TeamWorkspaceItem Incoming, string ConflictId)> Pair(object before, object after)
    {
        var store = new TeamWorkspaceStore(new LibraryPreferences().DatabasePath);
        var current = Item(JsonSerializer.SerializeToElement(before)); var incoming = current.Copy(); incoming.Payload = JsonSerializer.SerializeToElement(after);
        await store.ApplyChangeAsync(new(1, current, incoming), default);
        return (store, current, incoming, TeamWorkspaceCodec.ConflictKey(current, incoming));
    }
    private static async Task Refuse(Func<Task<JsonElement>> action)
    { try { await action(); throw new Exception("An invalid conflict request was accepted."); } catch (InvalidDataException) { } }
    private static void Require(bool value) { if (!value) throw new Exception("Shared conflict assertion failed."); }
}
