using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

var root = Path.Combine(Path.GetTempPath(), "GLPI-Foundation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int count = 0, failed = 0;
void Check(string name, Action run) { count++; try { run(); Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + "\n" + error); } }
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
void Reject<T>(Action run) where T : Exception { try { run(); } catch (T) { return; } throw new Exception(typeof(T).Name + " expected"); }
string FilePath(string suffix = ".json") => Path.Combine(root, Guid.NewGuid().ToString("N") + suffix);
AppSettings Settings()
{
    var value = new AppSettings { PortalUrl = "https://glpi.example.test/front/ticket.php?criteria%5B0%5D%5Bvalue%5D=all", History = new() { MaximumSnapshots = 50 } };
    value.Normalize(); return value;
}
ExtractionResult Data(int minutes = 0, params string[][] rows) => new()
{
    Headers = new() { "ID", "Titre", "Statut", "Date d’ouverture", "Attribué à - Technicien", "Dernière modification" },
    Rows = rows.ToList(), Complete = true, ExtractedAt = DateTimeOffset.Now.AddMinutes(minutes), SourceUrl = "https://glpi.example.test/front/ticket.php", ExpectedTotal = rows.Length,
    Provenance = new() { SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "entity-1", RequestedUrl = Settings().PortalUrl, SourceKind = "browser", Method = "native-csv" }
};
string[] Row(string id, string status = "Nouveau", string tech = "Alex", string title = "Incident") => new[] { id, title, status, "2026-01-02 10:00:00", tech, "2026-01-05 10:00:00" };
SnapshotDocument Document(SnapshotStore store, ExtractionResult data, AppSettings? settings = null) => store.Load(store.Save(data, settings ?? Settings()).Id);
SnapshotStore Store() => new(Path.Combine(root, Guid.NewGuid().ToString("N")));

Check("Archive: exact cells, provenance and interpretation survive compression", () =>
{
    var store = Store(); var data = Data(-1, Row("00042", title: "été\nA;B\t=literal")); var settings = Settings(); settings.Dashboard.StaleDays = 9;
    var saved = Document(store, data, settings); Equal(data.Rows[0][1], saved.Data.Rows[0][1]); Equal(9, saved.Dashboard.StaleDays);
    Assert(saved.Metadata.IdentityReliable && saved.Metadata.SearchVerified && saved.Metadata.ScopeVerified); Equal(1, store.List().Count);
    Equal(saved.Metadata.Id, store.Save(data, settings).Id);
});
Check("Diff: absence never means resolved; observed status and assignment changes counted", () =>
{
    var store = Store(); var before = Document(store, Data(-2, Row("001", "Résolu"), Row("2"), Row("3"), Row("5")));
    var after = Document(store, Data(-1, Row("1", "En cours (attribué)"), Row("2", "Résolu", "Sam"), Row("4"), Row("5")));
    var diff = SnapshotStore.Compare(before, after); Assert(diff.Compatible); Equal(1, diff.Absent); Equal(1, diff.Added);
    Equal(1, diff.ReopeningsObserved); Equal(1, diff.ResolutionsObserved); Equal(1, diff.ReassignmentsObserved); Equal(1, diff.Unchanged);
    Assert(diff.Changes.Any(change => change.Kind == "Absent du relevé" && change.TicketId == "3"));
});
Check("Diff: query, scope, coverage, schema, rule and time mismatches disable comparison", () =>
{
    var store = Store(); var before = Document(store, Data(-3, Row("1"))); var after = Document(store, Data(-1, Row("1")));
    var baseline = JsonSerializer.Serialize(after);
    foreach (var mutate in new Action<SnapshotDocument>[] {
        d => d.Metadata.SourceKey = "different", d => d.Metadata.SearchVerified = false, d => d.Metadata.ScopeVerified = false,
        d => d.Metadata.Complete = false, d => d.Metadata.RulesFingerprint = "different", d => d.Metadata.SchemaFingerprint = "different",
        d => d.Metadata.CapturedAt = before.Metadata.CapturedAt })
    {
        var changed = JsonSerializer.Deserialize<SnapshotDocument>(baseline)!; mutate(changed);
        var diff = SnapshotStore.Compare(before, changed); Assert(!diff.Compatible && diff.Changes.Count == 0);
    }
});
Check("Diff: numeric ID canonicalization rejects malformed groups and duplicate aliases", () =>
{
    var store = Store(); var before = Document(store, Data(-2, Row("1")));
    foreach (var rows in new[] { new[] { Row("+1") }, new[] { Row("1 2") }, new[] { Row("1"), Row("0001") }, new[] { Row("0") } })
    {
        var after = Document(store, Data(-1, rows)); Assert(!after.Metadata.IdentityReliable); Assert(!SnapshotStore.Compare(before, after).Compatible);
    }
    Assert(Document(store, Data(-1, Row("1 234"))).Metadata.IdentityReliable);
});
Check("Archive: tampered payload rejected, protected receipt kept under retention", () =>
{
    var folder = Path.Combine(root, "tamper"); var store = new SnapshotStore(folder); var first = store.Save(Data(-100, Row("1")), Settings()); store.Protect(first.Id, true);
    for (int i = 0; i < 5; i++) store.Save(Data(-20 + i, Row("1")), Settings());
    store.Prune(new() { MaximumSnapshots = 2 }); Assert(store.List().Any(item => item.Id == first.Id)); Assert(store.List().Count <= 3);
    File.AppendAllText(Path.Combine(folder, first.Id + ".json.gz"), "tampered"); Reject<InvalidDataException>(() => store.Load(first.Id));
    Reject<InvalidDataException>(() => store.Load("../settings"));
});
Check("Archive: cancellation publishes no receipt; source key excludes pagination only", () =>
{
    var store = Store(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Reject<OperationCanceledException>(() => store.Save(Data(0, Row("1")), Settings(), cancellation.Token)); Equal(0, store.List().Count);
    Equal(SnapshotStore.SourceKey("https://x.test/front/ticket.php?a=1&start=20&b=2"), SnapshotStore.SourceKey("https://x.test/front/ticket.php?b=2&a=1&start=40"));
    Assert(SnapshotStore.SourceKey("https://x.test/front/ticket.php?a=2") != SnapshotStore.SourceKey("https://x.test/front/ticket.php?a=1"));
    var data = Data(); data.Provenance.RequestedUrl = ""; Equal("", SnapshotStore.SourceKey(data));
});
Check("Archive: partial refreshes cannot evict the last complete comparable receipt", () =>
{
    var store = Store(); var settings = Settings(); settings.History.RetentionDays = 7; settings.History.MaximumSnapshots = 2;
    var complete = Data(-60, Row("1")); complete.ExtractedAt = DateTimeOffset.Now.AddDays(-30);
    var retained = store.Save(complete, settings);
    SnapshotMetadata? latest = null;
    for (int i = 0; i < 4; i++)
    {
        var partial = Data(-10 + i, Row("1")); partial.Complete = false;
        latest = store.Save(partial, settings);
    }
    Assert(store.List().Any(item => item.Id == retained.Id)); Assert(store.List().Any(item => item.Id == latest!.Id));
    Equal("1", store.Load(retained.Id).Data.Rows[0][0]);
    var replacement = store.Save(Data(0, Row("1"), Row("2")), settings);
    Assert(store.List().Any(item => item.Id == replacement.Id)); Assert(!store.List().Any(item => item.Id == retained.Id));
});
Check("Archive: malformed catalogue entries cannot prevent fresh receipts", () =>
{
    var folder = Path.Combine(root, "corrupt-catalogue"); var store = new SnapshotStore(folder);
    var receipt = store.Save(Data(-2, Row("1")), Settings()); var path = Path.Combine(folder, receipt.Id + ".meta.json");
    var original = File.ReadAllText(path);
    foreach (var field in new[] { "RunId", "SourceKey", "Metrics" })
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(original)!; metadata[field] = null;
        File.WriteAllText(path, metadata.ToJsonString()); Equal(0, store.List().Count);
    }
    var next = store.Save(Data(-1, Row("2")), Settings()); Equal(1, store.List().Count); Equal(next.Id, store.List()[0].Id);
});
Check("Archive: rules cover thresholds, weather and mappings but exclude presentation", () =>
{
    var first = Settings(); var second = first.Copy(); second.Dashboard.StaleDays++; Assert(SnapshotStore.RulesFingerprint(first) != SnapshotStore.RulesFingerprint(second));
    second = first.Copy(); second.Dashboard.Weather.StaleWeight++; Assert(SnapshotStore.RulesFingerprint(first) != SnapshotStore.RulesFingerprint(second));
    second = first.Copy(); second.Dashboard.Layout.Boards[0].Name = "Other"; Equal(SnapshotStore.RulesFingerprint(first), SnapshotStore.RulesFingerprint(second));
});
Check("Quality: duplicate IDs, invalid cells, incoherent dates and source preservation", () =>
{
    var data = Data(0, Row("1"), Row("0001"), Row("+2", "Unknown")); data.Rows[1][3] = "not-a-date"; data.Rows[0][5] = "2025-01-01 00:00:00";
    var original = JsonSerializer.Serialize(data); var report = DataQualityService.Analyze(data, Settings().Dashboard);
    Assert(report.Issues.Any(issue => issue.Kind == "Identifiant répété")); Assert(report.Issues.Any(issue => issue.Kind == "Dates incohérentes"));
    Assert(report.Columns.Single(column => column.Role == "id").Invalid == 1); Assert(report.Columns.Single(column => column.Role == "created").Invalid == 1);
    Equal(original, JsonSerializer.Serialize(data));
});
Check("XLSX: own export imports all adjacent inline rows and literal strings", () =>
{
    var data = Data(0, Row("0001", title: "A&B\n_x000D_"), Row("2", title: "=1+1"), Row("3", title: "été <ok>"));
    var path = FilePath(".xlsx"); ExportService.WriteXlsx(data, path); var imported = XlsxImportService.Read(path);
    Equal(data.Rows.Count, imported.Rows.Count); Equal(data.Headers.Count, imported.Headers.Count);
    for (int row = 0; row < 3; row++) for (int col = 0; col < data.Headers.Count; col++) Equal(data.Rows[row][col], imported.Rows[row][col]);
    Assert(!imported.Complete && !imported.Provenance.SearchVerified);
});
Check("XLSX: shared strings, cached formulas, formatted dates and leading zeros", () =>
{
    var sheet = "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row>"
        + "<row r=\"2\"><c r=\"A2\" s=\"2\"><v>7</v></c><c r=\"B2\" s=\"1\"><v>46023</v></c><c r=\"C2\"><f>2+2</f><v>4</v></c></row>"
        + "<row r=\"3\"><c r=\"A3\" t=\"s\"><v>3</v></c></row>";
    var shared = "<si><t>ID</t></si><si><t>Date</t></si><si><t>Valeur</t></si><si><r><t>é</t></r><r><t>té</t></r></si>";
    var styles = "<numFmts><numFmt numFmtId=\"164\" formatCode=\"00000\"/></numFmts><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/><xf numFmtId=\"164\"/></cellXfs>";
    var path = Workbook(sheet, shared, styles); var value = XlsxImportService.Read(path);
    Equal(2, value.Rows.Count); Equal("00007", value.Rows[0][0]); Equal("4", value.Rows[0][2]); Equal("été", value.Rows[1][0]);
    Assert(value.Rows[0][1].StartsWith("2026-01-01")); Assert(value.Warnings.Any(warning => warning.Contains("formule")));
});
Check("XLSX: row limit stays partial and reports actual scanned total", () =>
{
    var path = FilePath(".xlsx"); ExportService.WriteXlsx(Data(0, Row("1"), Row("2"), Row("3")), path); var value = XlsxImportService.Read(path, 1);
    Equal(1, value.Rows.Count); Assert(!value.Complete && value.Warnings.Any(warning => warning.Contains("3 lignes")));
});
Check("XLSX: external sheet, duplicated cells and cells beyond headers fail without loss", () =>
{
    var path = Workbook("<row><c r=\"A1\" t=\"inlineStr\"><is><t>ID</t></is></c></row><row><c r=\"A2\"><v>1</v></c><c r=\"A2\"><v>2</v></c></row>");
    Reject<InvalidDataException>(() => XlsxImportService.Read(path));
    path = Workbook("<row><c r=\"A1\" t=\"inlineStr\"><is><t>ID</t></is></c></row><row><c r=\"B2\"><v>1</v></c></row>");
    Reject<InvalidDataException>(() => XlsxImportService.Read(path));
    path = Workbook("", relationship: "Target=\"https://example.test/a.xml\" TargetMode=\"External\""); Reject<InvalidDataException>(() => XlsxImportService.Read(path));
    path = Workbook("", relationship: "Target=\"../worksheets/sheet1.xml\""); Reject<InvalidDataException>(() => XlsxImportService.Read(path));
});
Check("Export: cancellation preserves the old complete destination", () =>
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    foreach (var extension in new[] { ".csv", ".xlsx" })
    {
        var path = FilePath(extension); File.WriteAllText(path, "previous");
        Reject<OperationCanceledException>(() => { if (extension == ".csv") ExportService.WriteCsv(Data(0, Row("1")), path, cancellation.Token); else ExportService.WriteXlsx(Data(0, Row("1")), path, cancellation.Token); });
        Equal("previous", File.ReadAllText(path));
    }
});
Check("Settings: atomic versions and automatic recovery from corrupted current JSON", () =>
{
    var path = FilePath(); var settings = Settings(); settings.Theme = "dark"; SettingsRecovery.Save(path, settings);
    settings.Theme = "light"; SettingsRecovery.Save(path, settings); Equal(1, SettingsRecovery.List(path).Count);
    SettingsRecovery.Save(path, settings); Equal(1, SettingsRecovery.List(path).Count);
    File.WriteAllText(path, "{broken"); var recovered = SettingsRecovery.Load(path); Equal("dark", recovered.Theme); Assert(SettingsRecovery.LastRecoveryMessage.Contains("récupérée"));
    Equal("{broken", File.ReadAllText(path)); SettingsRecovery.Save(path, recovered); Equal("dark", SettingsRecovery.Read(path).Theme);
});
Check("Settings: version retention bounded and local defaults survive legacy load", () =>
{
    var path = FilePath(); var value = Settings(); for (int i = 0; i < 36; i++) { value.MaxRows = i + 100; SettingsRecovery.Save(path, value); }
    Equal(30, SettingsRecovery.List(path).Count); Equal(135, SettingsRecovery.Load(path).MaxRows);
    path = FilePath(); File.WriteAllText(path, "{\"Theme\":\"light\",\"Dashboard\":null,\"Interface\":null}"); var migrated = SettingsRecovery.Load(path);
    Equal(100, migrated.InterfaceZoomPercent); Equal("balanced", migrated.Interface.Density); Assert(migrated.Dashboard.Layout.Boards.Count > 0);
});
Check("Configuration: new dashboards, parameters and local preferences round-trip", () =>
{
    var current = Settings(); current.Interface.Density = "compact"; current.Automation.IntervalMinutes = 90; current.History.RetentionDays = 120;
    var incoming = Settings(); incoming.Sources.Add(new() { Name = "Tickets", Url = incoming.PortalUrl });
    var board = incoming.Dashboard.Layout.Boards[0]; board.Filters.Add(new() { Kind = "metric", SourceKey = "active" }); board.ComparisonEnabled = true;
    board.Widgets[0].Section = "Matin"; board.Widgets[0].Filters.Add(new() { Kind = "distribution", SourceKey = "status", Value = "Nouveau" });
    incoming.Dashboard.AssignmentCountingMode = "fractional"; incoming.Dashboard.Weather.OldWeight = 12;
    incoming.Queries.Queries.Add(new() { Name = "Paramétrée", Conditions = new() { new() { Field = "role:title", Operator = "contains", ValueSource = "parameter", Value = "lieu" } }, Parameters = new() { new() { Key = "lieu", Label = "Ville", DefaultValue = "Marseille" } }, ConditionExpression = "1" });
    var path = FilePath(); ConfigurationTransferService.Export(path, incoming); var imported = ConfigurationTransferService.Import(path, current);
    Equal("Matin", imported.Dashboard.Layout.Boards[0].Widgets[0].Section); Equal("fractional", imported.Dashboard.AssignmentCountingMode);
    Equal("lieu", imported.Queries.Queries.Last().Parameters[0].Key); Equal("compact", imported.Interface.Density); Equal(90, imported.Automation.IntervalMinutes); Equal(120, imported.History.RetentionDays);
    Assert(!File.ReadAllText(path).Contains("ScheduledExtraction"));
});
Check("Configuration: unknown nested v2 fields and invalid unnormalized weather rejected", () =>
{
    foreach (var json in new[] { "{\"Dashboard\":{\"Weather\":{\"OldWeight\":101}}}", "{\"Dashboard\":{\"Weather\":{\"Token\":\"secret\"}}}",
        "{\"Dashboard\":{\"PriorityRules\":{\"6:2\":4}}}", "{\"Queries\":{\"Queries\":[]},\"Sources\":[{\"Id\":\"bad\",\"Name\":\"x\",\"Url\":\"https://x.test\"}]}" })
    {
        var path = FilePath(); File.WriteAllText(path, json); Reject<InvalidDataException>(() => ConfigurationTransferService.Import(path, Settings()));
    }
});
Check("Configuration: selective merge preserves existing elements and identity without mutating drafts", () =>
{
    var current = Settings(); current.Dashboard.PersonalTechnician = "Alex"; var incoming = current.Copy(); incoming.Queries.Queries[0].Name = "Changed elsewhere";
    incoming.Queries.Queries.Add(new() { Name = "New query" }); incoming.Theme = "light"; incoming.ExportFolder = "remote";
    var before = JsonSerializer.Serialize(current); var merged = ConfigurationReview.Apply(current, incoming, ConfigurationParts.Queries, true);
    Equal(current.Queries.Queries.Count + 1, merged.Queries.Queries.Count); Equal(current.Queries.Queries[0].Name, merged.Queries.Queries[0].Name);
    Equal(current.Theme, merged.Theme); Equal("Alex", merged.Dashboard.PersonalTechnician); Equal(current.ExportFolder, merged.ExportFolder); Equal(before, JsonSerializer.Serialize(current));
    var replaced = ConfigurationReview.Apply(current, incoming, ConfigurationParts.Queries, false); Equal("Changed elsewhere", replaced.Queries.Queries[0].Name);
});


Check("Preview: filters and numeric sort cover rows beyond the displayed page", () =>
{
    var data = new ExtractionResult { Headers = new() { "ID", "City" }, Rows = Enumerable.Range(1, 20005).Select(i => new[] { i.ToString(), i % 2 == 0 ? "Marseille" : "Arles" }).ToList() };
    var transform = new PreviewTransform { Filters = new() { new() { Column = 1, Operator = "eq", Value = "marseille" } }, Sorts = new() { new() { Column = 0, Type = "number", Descending = true } } };
    var query = PreviewQueryService.Query(data, "", 2, transform: transform, culture: System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
    Equal(10002, query.MatchCount); Equal(20003, query.SourceIndices[0]); Equal(20001, query.SourceIndices[1]); Equal(20005, query.ScopeCount);
    using var view = PreviewQueryService.CreateView(data, "", 2, transform: transform); Equal(20003, view.GetSourceIndex(view.Table.Rows[0], data));
    Equal("1", data.Rows[0][0]);
});
Check("Preview: multiple sorts remain stable, missing values last and scope deduplicated", () =>
{
    var data = new ExtractionResult { Headers = new() { "Group", "Number" }, Rows = new() { new[] { "A", "2" }, new[] { "A", "10" }, new[] { "A", "" }, new[] { "B", "4" }, new[] { "A", "10" } } };
    var transform = new PreviewTransform { Sorts = new() { new() { Column = 0 }, new() { Column = 1, Type = "number", Descending = true } } };
    var query = PreviewQueryService.Query(data, "", 99, new[] { 0, 1, 2, 3, 4, 0, -1 }, transform: transform);
    Assert(query.SourceIndices.SequenceEqual(new[] { 1, 4, 0, 2, 3 })); Equal(5, query.ScopeCount);
    transform.Filters.Add(new() { Column = 1, Operator = "gt", Value = "5" }); Equal(2, PreviewQueryService.Query(data, "", transform: transform).MatchCount);
    transform.Filters[0].Column = 999; Reject<InvalidDataException>(() => PreviewQueryService.Query(data, "", transform: transform));
});
Check("Named views: saved columns and filters stay bound to the exact schema", () =>
{
    var store = new NamedPreviewStore(FilePath()); var headers = new[] { "ID", "Titre" };
    var view = new NamedPreview { HeaderFingerprint = PreviewViewPreferences.Fingerprint(headers), ColumnsCount = 2, Columns = PreviewViewPreferences.Defaults(2), Transform = new() { Filters = new() { new() { Column = 1, Value = "marseille" } } } };
    store.Save(view); view.Transform.Filters[0].Value = "changed"; Equal("marseille", store.List(view.HeaderFingerprint)[0].Transform.Filters[0].Value);
    Equal(0, store.List(PreviewViewPreferences.Fingerprint(new[] { "Titre", "ID" })).Count); store.Remove(view.Id); Equal(0, store.List().Count);
});
Check("Alerts: repeated extraction deduplicates; unknown never resolves; return to normal does", () =>
{
    var alerts = new AlertService(FilePath()); alerts.SaveRules(new[] { new AlertRule { Name = "Active tickets", MetricKey = "active", Threshold = 0 } });
    var source = Data(0, Row("1")); var snapshot = DashboardService.Calculate(source, Settings().Dashboard);
    Equal(1, alerts.Evaluate(source, snapshot)); Equal(0, alerts.Evaluate(source, snapshot)); Equal(1, alerts.Document.Incidents.Count);
    var id = alerts.Document.Incidents[0].Id; alerts.Acknowledge(id); alerts.Snooze(id, TimeSpan.FromHours(1)); Assert(alerts.Document.Incidents[0].Acknowledged);
    source = Data(1, Row("1")); source.Complete = false; alerts.Evaluate(source, DashboardService.Calculate(source, Settings().Dashboard));
    Assert(alerts.Document.Incidents[0].Active); Equal("Indéterminée", alerts.Document.Incidents[0].Evaluation);
    source = Data(2, Row("1", "Clos")); alerts.Evaluate(source, DashboardService.Calculate(source, Settings().Dashboard)); Assert(!alerts.Document.Incidents[0].Active);
    source = Data(3, Row("1")); Equal(1, alerts.Evaluate(source, DashboardService.Calculate(source, Settings().Dashboard))); Equal(2, alerts.Document.Incidents.Count);
    Assert(!alerts.Document.Incidents.Last().Acknowledged);
});
Check("Alerts: scoped and fictitious dashboards ignored, cancellation atomic", () =>
{
    var alerts = new AlertService(FilePath()); alerts.SaveRules(new[] { new AlertRule { MetricKey = "active" } }); var source = Data(0, Row("1")); source.Provenance.SourceKind = "demo";
    Equal(0, alerts.Evaluate(source, DashboardService.Calculate(source, Settings().Dashboard)));
    source.Provenance.SourceKind = "browser"; using var cancel = new CancellationTokenSource(); cancel.Cancel();
    Reject<OperationCanceledException>(() => alerts.Evaluate(source, DashboardService.Calculate(source, Settings().Dashboard), cancel.Token)); Equal(0, alerts.Document.Incidents.Count);
    var scoped = DashboardService.CalculateSubset(source, Settings().Dashboard, new[] { 0 }, "personal"); Equal(0, alerts.Evaluate(source, scoped));
});
Check("Alerts: an inconsistent expected total cannot resolve an active incident", () =>
{
    var alerts = new AlertService(FilePath()); alerts.SaveRules(new[] { new AlertRule { MetricKey = "active", Threshold = 0 } });
    var source = Data(0, Row("1")); Equal(1, alerts.Evaluate(source, DashboardService.Calculate(source, Settings().Dashboard)));
    source = Data(1, Row("1", "Clos")); source.ExpectedTotal = 2;
    alerts.Evaluate(source, DashboardService.Calculate(source, Settings().Dashboard));
    Assert(alerts.Document.Incidents[0].Active); Equal("Indéterminée", alerts.Document.Incidents[0].Evaluation);
});

try { Console.WriteLine($"{count - failed}/{count} Foundation checks passed."); return failed == 0 ? 0 : 1; }
finally { Directory.Delete(root, true); }

string Workbook(string rows, string? strings = null, string? styles = null, string relationship = "Target=\"worksheets/sheet1.xml\"")
{
    const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    var path = FilePath(".xlsx"); using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    void Add(string name, string xml) { using var output = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)); output.Write(xml); }
    Add("xl/workbook.xml", $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Tickets\" sheetId=\"1\" r:id=\"r1\"/></sheets></workbook>");
    Add("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" {relationship}/></Relationships>");
    Add("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><sheetData>{rows}</sheetData></worksheet>");
    if (strings != null) Add("xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\">{strings}</sst>");
    if (styles != null) Add("xl/styles.xml", $"<styleSheet xmlns=\"{ns}\">{styles}</styleSheet>");
    return path;
}
