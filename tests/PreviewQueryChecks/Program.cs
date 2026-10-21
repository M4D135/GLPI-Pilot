using System.Data;
using System.Globalization;
using System.Text.Json;
using GlpiPilot.Core;

var checks = new List<(string Name, Action Run)>();
void Test(string name, Action run) => checks.Add((name, run));
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
ExtractionResult Source(params string[][] rows) => new() { Headers = new() { "ID", "Description" }, Rows = rows.ToList() };
var large = Source(Enumerable.Range(0, 15037).Select(index => new[] { index.ToString("D5", CultureInfo.InvariantCulture), index == 14000 ? "needle-after-the-limit" : "ordinary" }).ToArray());

Test("Literal search reaches rows beyond the first ten thousand", () =>
{
    var found = PreviewQueryService.Query(large, "needle-after-the-limit");
    Assert(found.MatchCount == 1 && found.SourceIndices.SequenceEqual(new[] { 14000 }) && found.ScopeCount == 15037);
});
Test("Displayed rows are bounded while the total match count remains exact", () =>
{
    var found = PreviewQueryService.Query(large, "");
    Assert(found.MatchCount == 15037 && found.DisplayedCount == 10000 && found.IsLimited && found.SourceIndices[^1] == 9999);
});
Test("Matches beyond the display limit still contribute to the count", () =>
{
    var found = PreviewQueryService.Query(large, "ordinary");
    Assert(found.MatchCount == 15036 && found.DisplayedCount == 10000 && found.SourceIndices[0] == 0);
});
Test("Zero display capacity counts results without materializing them", () =>
{
    var found = PreviewQueryService.Query(large, "ordinary", 0);
    Assert(found.MatchCount == 15036 && found.DisplayedCount == 0 && found.IsLimited);
});
Test("A dashboard scope preserves the original row indices and its explicit order", () =>
{
    var found = PreviewQueryService.Query(large, "", sourceIndices: new[] { 14000, 3, 12000 });
    Assert(found.SourceIndices.SequenceEqual(new[] { 14000, 3, 12000 }) && found.ScopeCount == 3 && found.SourceCount == 15037);
});
Test("Search is intersected with the dashboard scope", () =>
{
    var found = PreviewQueryService.Query(large, "needle", sourceIndices: new[] { 10, 11, 12 });
    Assert(found.MatchCount == 0 && found.ScopeCount == 3 && !found.IsLimited);
});
Test("Duplicate and invalid scope indices cannot inflate row counts", () =>
{
    var found = PreviewQueryService.Query(large, "", sourceIndices: new[] { -1, 14000, 14000, 15037, 3, int.MaxValue, 3 });
    Assert(found.SourceIndices.SequenceEqual(new[] { 14000, 3 }) && found.MatchCount == 2 && found.ScopeCount == 2);
});
Test("An empty dashboard scope never falls back to all rows", () =>
{
    var found = PreviewQueryService.Query(large, null, sourceIndices: Array.Empty<int>());
    Assert(found.MatchCount == 0 && found.ScopeCount == 0 && found.DisplayedCount == 0);
});
foreach (var literal in new[] { "%", "*", "[", "]", "' OR 1=1", "a_b", "<tag>", "[[]]" })
{
    var captured = literal;
    Test("Literal characters are not interpreted: " + captured, () =>
    {
        var source = Source(new[] { "1", "prefix " + captured + " suffix" }, new[] { "2", "ordinary" });
        var found = PreviewQueryService.Query(source, captured);
        Assert(found.SourceIndices.SequenceEqual(new[] { 0 }));
    });
}
Test("Case-insensitive matching trims the query but preserves source text", () =>
{
    var source = Source(new[] { "1", "État\nSuivi" });
    var found = PreviewQueryService.Query(source, "  état  ");
    Assert(found.MatchCount == 1 && source.Rows[0][1] == "État\nSuivi");
});
Test("Newlines inside the searched text are literal", () =>
{
    var source = Source(new[] { "1", "Line1\nLine2" }, new[] { "2", "Line1 Line2" });
    Assert(PreviewQueryService.Query(source, "Line1\nLine2").SourceIndices.SequenceEqual(new[] { 0 }));
});
Test("An empty or whitespace search retains empty and duplicate rows", () =>
{
    var source = Source(Array.Empty<string>(), new[] { "", "" }, new[] { "7", "same" }, new[] { "7", "same" });
    foreach (var term in new string?[] { null, "", " \t\r\n " })
        Assert(PreviewQueryService.Query(source, term).SourceIndices.SequenceEqual(new[] { 0, 1, 2, 3 }));
});
Test("Empty extraction and absent cell values are handled without invented matches", () =>
{
    Assert(PreviewQueryService.Query(new(), "x").MatchCount == 0);
    var source = Source(null!, new[] { "", null! }, Array.Empty<string>());
    Assert(PreviewQueryService.Query(source, "x").MatchCount == 0);
});
Test("Search covers every source cell independently of presentation preferences", () =>
{
    var source = Source(new[] { "1", "visible", "hidden-value" });
    Assert(PreviewQueryService.Query(source, "hidden-value").MatchCount == 1);
});
Test("Invalid limits fail before any source scan", () => Throws<ArgumentOutOfRangeException>(() => PreviewQueryService.Query(large, "", -1)));
Test("A previously cancelled request never produces an obsolete result", () =>
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Throws<OperationCanceledException>(() => PreviewQueryService.Query(large, "", ct: cancellation.Token));
});
Test("Cancellation during a sparse odd-index scope uses the iteration counter", () =>
{
    using var cancellation = new CancellationTokenSource();
    var scope = new CancellingIndices(1000, index => index * 2 + 1, 300, cancellation);
    Throws<OperationCanceledException>(() => PreviewQueryService.Query(large, "", sourceIndices: scope, ct: cancellation.Token));
});
Test("Reaching the display limit does not skip later cancellation", () =>
{
    using var cancellation = new CancellationTokenSource();
    var scope = new CancellingIndices(15000, index => index, 12000, cancellation);
    Throws<OperationCanceledException>(() => PreviewQueryService.Query(large, "ordinary", 1, scope, cancellation.Token));
});
Test("A filtered and sorted DataView resolves the exact original ticket row", () =>
{
    var source = Source(new[] { "10", "skip" }, new[] { "30", "match" }, new[] { "20", "match" }, new[] { "40", "match" });
    using var view = PreviewQueryService.CreateView(source, "match", sourceIndices: new[] { 3, 2, 1 });
    view.Table.DefaultView.Sort = "C0 DESC";
    var indices = view.Table.DefaultView.Cast<DataRowView>().Select(row => view.GetSourceIndex(row.Row, source)).ToArray();
    Assert(indices.SequenceEqual(new int?[] { 3, 1, 2 }));
    foreach (DataRowView row in view.Table.DefaultView)
    {
        var index = view.GetSourceIndex(row.Row, source)!.Value;
        Assert((string)row.Row[0] == source.Rows[index][0]);
    }
});
Test("Equal cell contents do not merge distinct source identities", () =>
{
    var source = Source(new[] { "7", "duplicate" }, new[] { "7", "duplicate" });
    using var view = PreviewQueryService.CreateView(source, "duplicate");
    Assert(view.GetSourceIndex(view.Table.Rows[0], source) == 0 && view.GetSourceIndex(view.Table.Rows[1], source) == 1);
});
Test("Foreign rows and rows from a replaced extraction cannot resolve a ticket", () =>
{
    var source = Source(new[] { "7", "one" });
    using var view = PreviewQueryService.CreateView(source, "");
    using var otherView = PreviewQueryService.CreateView(source, "");
    Assert(view.GetSourceIndex(otherView.Table.Rows[0], source) == null);
    Assert(view.GetSourceIndex(view.Table.Rows[0], Source(new[] { "7", "one" })) == null);
    Assert(view.GetSourceIndex(view.Table.Rows[0], null) == null);
});
Test("Removed and disposed preview rows are rejected", () =>
{
    var source = Source(new[] { "7", "one" }, new[] { "8", "two" });
    var view = PreviewQueryService.CreateView(source, "");
    var removed = view.Table.Rows[0]; view.Table.Rows.Remove(removed);
    Assert(view.GetSourceIndex(removed, source) == null);
    var retained = view.Table.Rows[0]; view.Dispose();
    Assert(view.GetSourceIndex(retained, source) == null);
});
Test("Mutating the source in place cannot silently open a different ticket", () =>
{
    var source = Source(new[] { "7", "one" }, new[] { "8", "two" });
    using var view = PreviewQueryService.CreateView(source, "");
    var first = view.Table.Rows[0];
    source.Rows[0][0] = "999"; Assert(view.GetSourceIndex(first, source) == null);
    source.Rows[0][0] = "7"; Assert(view.GetSourceIndex(first, source) == 0);
    source.Headers[0] = "Unrelated"; Assert(view.GetSourceIndex(first, source) == null);
    source.Headers[0] = "ID";
    source.Rows[0] = new[] { "7", "one" }; Assert(view.GetSourceIndex(first, source) == null);
    source.Rows.RemoveAt(0); Assert(view.GetSourceIndex(view.Table.Rows[1], source) == null);
});
Test("Materialized view preserves duplicate and empty headers by ordinal", () =>
{
    var source = new ExtractionResult { Headers = new() { "", "Title", "Title" }, Rows = new() { new[] { "1", "first", "second" } } };
    using var view = PreviewQueryService.CreateView(source, "");
    Assert(view.Table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).SequenceEqual(new[] { "C0", "C1", "C2" }));
    Assert(view.Table.Columns[1].Caption == "Title" && view.Table.Columns[2].Caption == "Title");
    Assert((string)view.Table.Rows[0][2] == "second" && view.GetSourceIndex(view.Table.Rows[0], source) == 0);
});
Test("View construction preserves source metadata, controls and export values", () =>
{
    var source = Source(new[] { "7", "raw\u0092text" }); source.Complete = false; source.ExpectedTotal = 800; source.Scope = "synthetic scope";
    using var view = PreviewQueryService.CreateView(source, "raw");
    Assert((string)view.Table.Rows[0][1] == "raw\u0092text" && source.Rows[0][1] == "raw\u0092text");
    Assert(!source.Complete && source.ExpectedTotal == 800 && source.Scope == "synthetic scope" && source.Rows.Count == 1);
});

Test("Header fingerprints distinguish ordering, separators, empty and duplicate headers", () =>
{
    var candidates = new[] { new[] { "a", "b" }, new[] { "b", "a" }, new[] { "a|b" }, new[] { "a", "a" }, new[] { "a", "" }, new[] { "a" }, Array.Empty<string>() };
    var fingerprints = candidates.Select(PreviewViewPreferences.Fingerprint).ToArray();
    Assert(fingerprints.Distinct(StringComparer.Ordinal).Count() == candidates.Length);
    Assert(fingerprints.All(value => value.Length == 64) && fingerprints[0] == PreviewViewPreferences.Fingerprint(new[] { "a", "b" }));
});
Test("Preference normalization keeps at least one visible column", () =>
{
    var columns = PreviewViewPreferences.Defaults(3); foreach (var column in columns) column.Visible = false;
    var normalized = PreviewViewPreferences.Normalize(columns, 3);
    Assert(normalized.Count(column => column.Visible) == 1 && normalized[0].Visible);
    Assert(columns.All(column => !column.Visible), "Normalization mutated the input");
    Assert(PreviewViewPreferences.Normalize(null, 0).Length == 0);
});
Test("Invalid widths are finite and bounded before reaching WPF", () =>
{
    var columns = PreviewViewPreferences.Defaults(5);
    var widths = new[] { double.NaN, double.PositiveInfinity, -8, 900000, 240.5 };
    for (var index = 0; index < columns.Length; index++) columns[index].Width = widths[index];
    var normalized = PreviewViewPreferences.Normalize(columns, columns.Length);
    Assert(normalized.Select(column => column.Width).SequenceEqual(new[] { 165d, 165d, 70d, 1200d, 240.5 }));
});
Test("Duplicate or invalid positions are normalized to one complete permutation", () =>
{
    var columns = new[]
    {
        new PreviewColumnPreference { SourceIndex = 2, DisplayIndex = 0, Width = 280 },
        new PreviewColumnPreference { SourceIndex = 0, DisplayIndex = 0 },
        new PreviewColumnPreference { SourceIndex = 2, DisplayIndex = 1, Width = 999 },
        new PreviewColumnPreference { SourceIndex = -1 }, new PreviewColumnPreference { SourceIndex = 999 }
    };
    var normalized = PreviewViewPreferences.Normalize(columns, 4);
    Assert(normalized.Select(column => column.SourceIndex).SequenceEqual(new[] { 0, 1, 2, 3 }));
    Assert(normalized.Select(column => column.DisplayIndex).Order().SequenceEqual(new[] { 0, 1, 2, 3 }));
    Assert(normalized[2].Width == 280 && normalized.All(column => column.Visible));
});
Test("Preference copies isolate a cancelled dialog's working edits", () =>
{
    var original = PreviewViewPreferences.Defaults(2);
    var working = PreviewViewPreferences.Normalize(original, 2); working[0].Visible = false; working[1].Width = 777;
    Assert(original[0].Visible && original[1].Width == 165);
});

var temporary = Path.Combine(Path.GetTempPath(), "glpi-preview-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
Test("Missing local preferences yield a usable default layout", () =>
{
    var columns = PreviewViewPreferences.Load(Path.Combine(temporary, "missing.json"), new[] { "a", "b" });
    Assert(columns.Length == 2 && columns.All(column => column.Visible && column.Width == 165));
});
Test("Preferences round-trip visibility, order, fixed width and automatic width", () =>
{
    var path = Path.Combine(temporary, "roundtrip.json"); var headers = new[] { "a", "b", "c" };
    var columns = PreviewViewPreferences.Defaults(3);
    columns[0].Visible = false; columns[1].AutoWidth = true; columns[2].Width = 333; columns[2].DisplayIndex = 0; columns[0].DisplayIndex = 2;
    PreviewViewPreferences.Save(path, headers, columns);
    var loaded = PreviewViewPreferences.Load(path, headers);
    Assert(!loaded[0].Visible && loaded[1].AutoWidth && loaded[2].Width == 333 && loaded[2].DisplayIndex == 0);
});
Test("A different header structure never inherits another table's layout", () =>
{
    var path = Path.Combine(temporary, "identity.json");
    var columns = PreviewViewPreferences.Defaults(2); columns[0].Visible = false;
    PreviewViewPreferences.Save(path, new[] { "a", "b" }, columns);
    Assert(PreviewViewPreferences.Load(path, new[] { "b", "a" }).All(column => column.Visible));
});
Test("The preferences file contains hashes and presentation fields without header or cell content", () =>
{
    var path = Path.Combine(temporary, "privacy.json");
    var headers = new[] { "SYNTHETIC-PRIVATE-HEADER-ALPHA", "SYNTHETIC-PRIVATE-HEADER-BETA" };
    PreviewViewPreferences.Save(path, headers, PreviewViewPreferences.Defaults(2));
    var text = File.ReadAllText(path);
    Assert(headers.All(header => !text.Contains(header, StringComparison.Ordinal)));
    using var document = JsonDocument.Parse(text);
    Assert(document.RootElement.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "Profiles", "Version" }));
    var profile = document.RootElement.GetProperty("Profiles").EnumerateObject().Single();
    Assert(profile.Name == PreviewViewPreferences.Fingerprint(headers));
});
Test("A malformed optional file is reported then recoverable on the next explicit save", () =>
{
    var path = Path.Combine(temporary, "broken.json"); File.WriteAllText(path, "{broken");
    Throws<JsonException>(() => PreviewViewPreferences.Load(path, new[] { "a" }));
    PreviewViewPreferences.Save(path, new[] { "a" }, PreviewViewPreferences.Defaults(1));
    Assert(PreviewViewPreferences.Load(path, new[] { "a" }).Length == 1);
});
Test("Unsupported schemas and oversized files are not trusted during loading", () =>
{
    var path = Path.Combine(temporary, "unsupported.json"); File.WriteAllText(path, "{\"Version\":99,\"Profiles\":{}}");
    Throws<NotSupportedException>(() => PreviewViewPreferences.Load(path, new[] { "a" }));
    var future = File.ReadAllText(path);
    Throws<NotSupportedException>(() => PreviewViewPreferences.Save(path, new[] { "a" }, PreviewViewPreferences.Defaults(1)));
    Assert(File.ReadAllText(path) == future, "Saving overwrote a future schema");
    File.WriteAllBytes(path, new byte[PreviewViewPreferences.MaximumFileBytes + 1]);
    Throws<InvalidDataException>(() => PreviewViewPreferences.Load(path, new[] { "a" }));
    Throws<InvalidDataException>(() => PreviewViewPreferences.Save(path, new[] { "a" }, PreviewViewPreferences.Defaults(1)));
    Assert(new FileInfo(path).Length == PreviewViewPreferences.MaximumFileBytes + 1);
});
Test("Stored profiles are bounded and the current structure remains available", () =>
{
    var path = Path.Combine(temporary, "bounded.json");
    for (var index = 0; index < PreviewViewPreferences.MaximumProfiles + 3; index++)
        PreviewViewPreferences.Save(path, new[] { "column-" + index }, new[] { new PreviewColumnPreference { Width = 200 + index } });
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    Assert(document.RootElement.GetProperty("Profiles").EnumerateObject().Count() == PreviewViewPreferences.MaximumProfiles);
    Assert(PreviewViewPreferences.Load(path, new[] { "column-26" })[0].Width == 226);
    Assert(!Directory.EnumerateFiles(temporary, "*.tmp").Any(), "An atomic-save temporary file was left behind");
});

var failures = 0;
try
{
    foreach (var (name, run) in checks)
    {
        try { run(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
}
finally { Directory.Delete(temporary, recursive: true); }
Console.WriteLine($"{checks.Count - failures}/{checks.Count} checks passed.");
return failures == 0 ? 0 : 1;

sealed class CancellingIndices : IReadOnlyList<int>
{
    private readonly Func<int, int> _get;
    private readonly int _cancelAt;
    private readonly CancellationTokenSource _cancellation;
    public int Count { get; }
    public int this[int index]
    {
        get { if (index >= _cancelAt) _cancellation.Cancel(); return _get(index); }
    }
    public CancellingIndices(int count, Func<int, int> get, int cancelAt, CancellationTokenSource cancellation)
    { Count = count; _get = get; _cancelAt = cancelAt; _cancellation = cancellation; }
    public IEnumerator<int> GetEnumerator() { for (var index = 0; index < Count; index++) yield return this[index]; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
