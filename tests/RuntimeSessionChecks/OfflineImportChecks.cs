using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public static class OfflineImportChecks
{
    public static readonly string[] IncidentHeaders =
    {
        "ID", "Titre", "Statut", "Dernière modification", "Date d'ouverture", "Priorité", "Demandeur - Demandeur",
        "Attribué à - Technicien", "Catégorie", "Suivis - Rédacteur", "Tâches - Rédacteur", "TTR + Progression",
        "Statistiques - Délai de prise en compte", "Type", "Attribué à - Groupe de techniciens", "Âge (jours)",
        "Nombre de groupes", "Nombre de rédacteurs", "Description"
    };
    private static readonly string[] IncidentValues =
    {
        "0042", "Incident à tester", "En attente", "22/10/2026 09:30", "08/10/2026 08:00", "Haute", "Demandeur synthétique",
        "Agent A", "Impression", "Agent A\nAgent B", "", "80 %", "0,5", "Incident", "Groupe A\nGroupe B", "14", "2", "2",
        "Une description\navec ; séparateur et <b>texte littéral</b>"
    };
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add((name, async () => { await using var fixture = new SessionFixture(); await run(fixture); }));
        Add("Offline CSV: all 19 fields retain exact values, local provenance and distinct mappings", CsvFields);
        Add("Offline mode blocks collection, synchronization, operational mutations and unclassified integrations", OfflineBarrier);
        Add("Offline imports and basket edits restore the original observation and operational basket", Restoration);
        Add("Cancelled or invalid test imports never replace the active observation or its restoration point", AtomicFailure);
        Add("An imported test restores an archive with its exact captured interpretation", ArchiveRestoration);
        Add("Saved test queries and their widgets remain calculable after restoring the live observation", QueryCustomizationRestoration);
        Add("Leaving a test started without an observation restores the empty session", EmptyRestoration);
        Add("Capped file reads disclose their source row count and remain incomplete", LimitedReceipt);
        Add("Simulated weather never certifies GLPI scope or hides truncated files, duplicate IDs or unknown statuses", WeatherEvidence);
        Add("XLSX chooser imports only the selected visible sheet and header row", WorksheetChoice);
        Add("XLSX chooser refuses changed files, stale revisions, hidden sheets and expired selection tokens", WorksheetStaleness);
        Add("XLSX dates, durations, duplicate headers, missing cells and formatting tails retain their meaning", WorksheetValues);
        Add("Uncached XLSX formulas remain unknown and cannot masquerade as a complete trustworthy test file", MissingFormulaCaches);
        return tests;
    }
    private static void Check(bool condition, string message = "Offline import assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Refuse(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        { Check(error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase), error.Message); return; }
        throw new InvalidOperationException("Expected refusal: " + expected);
    }
    private static string CsvCell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    public static async Task<string> WriteCsv(SessionFixture f, string name = "incidents.csv", int count = 1)
    {
        var path = Path.Combine(f.Root, name); var output = new StringBuilder(string.Join(';', IncidentHeaders.Select(CsvCell)) + "\r\n");
        for (var i = 0; i < count; i++) output.AppendLine(string.Join(';', IncidentValues.Select((value, column) => CsvCell(column == 0 && i > 0 ? (42 + i).ToString(CultureInfo.InvariantCulture) : value))));
        await File.WriteAllTextAsync(path, output.ToString(), new UTF8Encoding(true)); f.Host.OpenPath = path; return path;
    }
    private static async Task CsvFields(SessionFixture f)
    {
        var before = f.App.Result; var settings = JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json);
        await WriteCsv(f); var session = await f.Call("records.test.import"); var data = f.App.Result!;
        Check(session.GetProperty("testMode").GetBoolean() && session.GetProperty("testFileReadComplete").GetBoolean());
        Check(data.Headers.Take(19).SequenceEqual(IncidentHeaders) && data.Rows.Single().Take(19).SequenceEqual(IncidentValues));
        Check(data.SourceUrl == "" && data.Provenance.SourceKind == "file" && data.Provenance.Method == "csv" && !data.Complete && !data.Provenance.ScopeVerified && !data.Provenance.SearchVerified);
        var roles = DashboardService.ResolveColumns(data.Headers, f.App.EffectiveSettings.Dashboard).Where(role => role.Index.HasValue).ToArray();
        Check(roles.Length >= 19 && roles.Select(role => role.Index).Distinct().Count() >= 19, "All incident fields must resolve independently.");
        Check(DashboardService.ResolveColumns(data.Headers, f.App.EffectiveSettings.Dashboard).Single(role => role.Role == "due").Index is null); // Progression is not an SLA deadline.
        Check(settings == JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json) && before!.Rows.Single()[1] == "Observation A");
    }
    private static async Task OfflineBarrier(SessionFixture f)
    {
        var invoked = false; f.App.RegisterSessionFixture("integration.unclassified", (_, _) => { invoked = true; return Task.FromResult<object?>(null); });
        f.App.ForceScheduledFixture((_, _) => { invoked = true; return Task.FromResult<object?>(null); });
        await WriteCsv(f); await f.Call("records.test.import");
        var before = JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json); var data = f.App.Result;
        foreach (var operation in new[] { "collect.connect", "collect.run", "collect.campaign", "library.capture", "library.sync", "settings.save", "sources.select", "terrain.tours.save", "terrain.review.correct", "alerts.evaluate", "workplace.personal.save", "integration.unclassified" })
            await Refuse(() => f.Call(operation), "Mode test hors ligne");
        foreach (var operation in new[] { "automation.tick", "library.tick", "workplace.reminders.check" })
            Check((await f.Call(operation)).GetProperty("skipped").GetBoolean());
        await f.App.TickAsync();
        Check(!invoked && f.App.Snapshots.List().Count == 0 && ReferenceEquals(data, f.App.Result));
        Check(before == JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json));
        Check(!f.Events.Any(e => e.Name == "workplace.reminders.due"));
        var mapping = f.App.EffectiveSettings.Dashboard; mapping.ColumnMappings["title"] = "Description";
        await f.Call("mapping.save", new { revision = f.App.Revision, settings = mapping });
        Check(f.App.EffectiveSettings.Dashboard.ColumnMappings["title"] == "Description" && f.App.Settings.Dashboard.ColumnMappings["title"] == "Titre");
    }
    private static async Task Restoration(SessionFixture f)
    {
        var original = f.App.Result; var interpretation = JsonSerializer.Serialize(f.App.EffectiveSettings, PilotApplication.Json);
        await f.Call("records.basket", new { rows = new[] { 0 }, revision = f.App.Revision });
        await WriteCsv(f); await f.Call("records.test.import");
        Check((await f.Call("records.basket")).GetProperty("count").GetInt32() == 0);
        await f.Call("records.basket", new { rows = new[] { 0 }, revision = f.App.Revision });
        await WriteCsv(f, "second.csv", 2); await f.Call("records.test.import");
        Check((await f.Call("records.basket")).GetProperty("count").GetInt32() == 0);
        await f.Call("records.basket", new { clear = true });
        var restored = await f.Call("records.test.exit");
        Check(!restored.GetProperty("testMode").GetBoolean() && ReferenceEquals(original, f.App.Result));
        Check(interpretation == JsonSerializer.Serialize(f.App.EffectiveSettings, PilotApplication.Json));
        Check((await f.Call("records.basket")).GetProperty("rows").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(new[] { 0 }));
    }
    private static async Task AtomicFailure(SessionFixture f)
    {
        var original = f.App.Result; var revision = f.App.Revision; f.Host.OpenPath = null;
        Check((await f.Call("records.test.import")).GetProperty("cancelled").GetBoolean() && f.App.Revision == revision && !f.App.IsTestMode);
        await WriteCsv(f); await f.Call("records.test.import"); var valid = f.App.Result; revision = f.App.Revision;
        f.Host.OpenPath = Path.Combine(f.Root, "broken.csv"); await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre\n1;extrait;colonne en trop\n");
        await Refuse(() => f.Call("records.test.import"), "structure CSV");
        Check(ReferenceEquals(valid, f.App.Result) && f.App.IsTestMode && f.App.Revision == revision);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await f.Call("records.test.import", ct: cancelled.Token); throw new Exception("Expected cancellation."); } catch (OperationCanceledException) { }
        Check(ReferenceEquals(valid, f.App.Result));
        await f.Call("records.test.exit"); Check(ReferenceEquals(original, f.App.Result));
    }
    private static async Task ArchiveRestoration(SessionFixture f)
    {
        var original = SessionFixture.Data(); var settings = SessionFixture.MappingB(); settings.NumberCulture = "en-US"; settings.Normalize();
        var archive = new SnapshotDocument { Data = original, Dashboard = settings, Cities = new(), Queries = QueryLibrarySettings.CreateDefault() };
        f.App.LoadSessionFixture(original, archive); var interpretation = JsonSerializer.Serialize(f.App.EffectiveSettings, PilotApplication.Json);
        await WriteCsv(f); await f.Call("records.test.import"); Check(!f.App.IsHistorical);
        await f.Call("queries.save", new { query = new SavedQuery { Name = "Configuration future", BaseProbe = "active" } });
        await f.Call("records.test.exit"); Check(f.App.IsHistorical && ReferenceEquals(f.App.HistoricalDocument, archive) && ReferenceEquals(f.App.Result, original));
        var restored = f.App.EffectiveSettings;
        Check(restored.Dashboard.NumberCulture == "en-US" && restored.Dashboard.ColumnMappings["id"] == "Autre ID");
        Check(!restored.Queries.Queries.Any(q => q.Name == "Configuration future") && f.App.Settings.Queries.Queries.Any(q => q.Name == "Configuration future"));
        using var before = JsonDocument.Parse(interpretation);
        Check(JsonSerializer.Serialize(restored.Queries, PilotApplication.Json) == before.RootElement.GetProperty("queries").GetRawText());
    }
    private static async Task QueryCustomizationRestoration(SessionFixture f)
    {
        var original = f.App.Result; var geography = JsonSerializer.Serialize(f.App.EffectiveSettings.Cities, PilotApplication.Json);
        await WriteCsv(f); await f.Call("records.test.import");
        var query = new SavedQuery { Name = "Créée pendant un test", BaseProbe = "active" };
        await f.Call("queries.save", new { query });
        await f.Call("queries.widget.add", new { queryId = query.Id });
        var mapping = f.App.EffectiveSettings.Dashboard; mapping.ColumnMappings["title"] = "Description";
        await f.Call("mapping.save", new { revision = f.App.Revision, settings = mapping });
        await f.Call("records.test.exit");
        Check(ReferenceEquals(f.App.Result, original) && f.App.EffectiveSettings.Dashboard.ColumnMappings["title"] == "Titre"
            && geography == JsonSerializer.Serialize(f.App.EffectiveSettings.Cities, PilotApplication.Json));
        Check(f.App.EffectiveSettings.Queries.Queries.Any(q => q.Id == query.Id));
        var dashboard = await f.Call("dashboard.get");
        var widget = f.App.Settings.Dashboard.Layout.Boards.SelectMany(b => b.Widgets).Single(w => w.SourceKey == QueryService.MetricKey(query.Id));
        var rendered = dashboard.GetProperty("widgets").EnumerateArray().Single(w => w.GetProperty("id").GetString() == widget.Id);
        Check(rendered.GetProperty("available").GetBoolean() && rendered.GetProperty("content").GetProperty("value").GetDouble() == 1);
    }
    private static async Task EmptyRestoration(SessionFixture f)
    {
        await f.SelectB(); Check(f.App.Result is null); await WriteCsv(f); await f.Call("records.test.import");
        var response = await f.Call("records.test.exit"); Check(f.App.Result is null && !response.GetProperty("loaded").GetBoolean() && !f.App.IsTestMode);
    }
    private static async Task LimitedReceipt(SessionFixture f)
    {
        var settings = f.App.Settings.Copy(); settings.MaxRows = 1; await f.Call("settings.save", new { revision = f.App.Revision, settings });
        await WriteCsv(f, count: 3); var response = await f.Call("records.test.import");
        Check(response.GetProperty("testSourceRows").GetInt64() == 3 && response.GetProperty("rows").GetInt32() == 1 && !response.GetProperty("testFileReadComplete").GetBoolean());
        Check(!f.App.Result!.Complete && f.App.Result.ExpectedTotal is null);
    }
    private static async Task WeatherEvidence(SessionFixture f)
    {
        var settings = f.App.Settings.Copy();
        var board = DashboardLayoutSettings.CreateBoard("Simulation météo", "empty");
        board.Widgets.Add(new() { Id = "offline-weather", Kind = "metric", SourceKey = "active", Presentation = "weather-volume",
            WarningValue = 10, CriticalValue = 20, ShowMiniHistory = true, Filters = new() { new() { Kind = "metric", SourceKey = "active" } } });
        settings.Dashboard.Layout = new() { Boards = new() { board }, ActiveBoardId = board.Id };
        settings.Dashboard.PersonalLayout = settings.Dashboard.Layout.Copy();
        await f.Call("settings.save", new { revision = f.App.Revision, settings });
        async Task<JsonElement> ReadWeather(string rows)
        {
            f.Host.OpenPath = Path.Combine(f.Root, "weather.csv"); await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre;Statut\n" + rows);
            await f.Call("records.test.import"); var model = await f.Call("dashboard.get");
            Check(f.App.Result is { Complete: false } && !f.App.Result.Provenance.SearchVerified && !f.App.Result.Provenance.ScopeVerified);
            var weather = model.GetProperty("widgets").EnumerateArray().Single(w => w.GetProperty("id").GetString() == "offline-weather").GetProperty("content").GetProperty("volumeWeather");
            Check(weather.GetProperty("simulation").GetBoolean() && weather.GetProperty("title").GetString()!.StartsWith("Simulation · ", StringComparison.Ordinal));
            var history = await f.Call("dashboard.history", new { revision = model.GetProperty("revision").GetInt64(), runId = model.GetProperty("runId").GetString() });
            Check(history.GetProperty("widgets").EnumerateArray().All(w => w.GetProperty("history").ValueKind == JsonValueKind.Null));
            return weather;
        }
        Check((await ReadWeather("1;Visible;Nouveau\n2;Terminé;Résolu\n")).GetProperty("state").GetString() == "sun");
        Check((await ReadWeather("1;Visible;Nouveau\n2;Masqué par filtre;État inconnu\n")).GetProperty("state").GetString() == "partial");
        Check((await ReadWeather("1;Visible;Nouveau\n2;Masqué par filtre;Résolu\n2;Doublon masqué;Résolu\n")).GetProperty("state").GetString() == "partial");
        await f.Call("records.test.exit"); settings = f.App.Settings.Copy(); settings.MaxRows = 1;
        await f.Call("settings.save", new { revision = f.App.Revision, settings });
        Check((await ReadWeather("1;Visible;Nouveau\n2;Non chargé;Nouveau\n")).GetProperty("state").GetString() == "partial");
        Check(f.App.Snapshots.List().Count == 0);
    }
    private static async Task WorksheetChoice(SessionFixture f)
    {
        var original = f.App.Result; f.Host.OpenPath = WriteWorkbook(f.Root); var revision = f.App.Revision;
        var choice = await f.Call("records.test.import");
        Check(choice.GetProperty("selectionRequired").GetBoolean() && choice.GetProperty("sheets").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "Dashboard", "Incidents" }));
        Check(!f.App.IsTestMode && ReferenceEquals(original, f.App.Result) && f.App.Revision == revision && !choice.GetRawText().Contains(f.Root, StringComparison.Ordinal));
        var token = choice.GetProperty("token").GetString();
        await Refuse(() => f.Call("records.test.import", new { token, sheetName = "Incidents", headerRow = 1 }), "en-têtes");
        Check(!f.App.IsTestMode && ReferenceEquals(original, f.App.Result));
        var result = await f.Call("records.test.import", new { token, sheetName = "Incidents", headerRow = 4 });
        Check(result.GetProperty("testFileReadComplete").GetBoolean() && f.App.Result!.Headers.Take(19).SequenceEqual(IncidentHeaders));
        Check(f.App.Result!.Rows.Single().Take(19).SequenceEqual(IncidentValues));
        await f.Call("records.test.exit"); Check(ReferenceEquals(original, f.App.Result));
    }
    private static async Task WorksheetStaleness(SessionFixture f)
    {
        f.Host.OpenPath = WriteWorkbook(f.Root); var original = f.App.Result;
        var choice = await f.Call("records.test.import"); var token = choice.GetProperty("token").GetString();
        await Refuse(() => f.Call("records.test.import", new { token, sheetName = "Masquée", headerRow = 1 }), "masquée");
        await File.AppendAllTextAsync(f.Host.OpenPath, "changed");
        await Refuse(() => f.Call("records.test.import", new { token, sheetName = "Incidents", headerRow = 4 }), "changé");
        Check(ReferenceEquals(original, f.App.Result) && !f.App.IsTestMode);
        f.Host.OpenPath = WriteWorkbook(f.Root); choice = await f.Call("records.test.import"); token = choice.GetProperty("token").GetString();
        await f.Call("records.test.import", new { token, cancelled = true });
        await Refuse(() => f.Call("records.test.import", new { token, sheetName = "Incidents", headerRow = 4 }), "expiré");
        choice = await f.Call("records.test.import"); token = choice.GetProperty("token").GetString();
        await f.Call("app.demo"); await Refuse(() => f.Call("records.test.import", new { token, sheetName = "Incidents", headerRow = 4 }), "expiré");
    }
    private static Task WorksheetValues(SessionFixture f)
    {
        var serial = new DateTime(2026, 10, 22, 9, 30, 0).ToOADate().ToString("R", CultureInfo.InvariantCulture);
        var rows = "<row r='1'>" + TextCell("A1", "ID") + TextCell("B1", "Date d'ouverture") + TextCell("C1", "Délai") + TextCell("D1", "Titre") + TextCell("F1", "Titre") + "<c r='XFD1' s='1'/></row>" +
            "<row r='2'>" + "<c r='A2' s='3'><v>42</v></c><c r='B2' s='1'><v>" + serial + "</v></c><c r='C2' s='2'><v>0.5</v></c>" + TextCell("F2", "Texte") + "<c r='XFD2' s='1'/></row>";
        var path = WriteWorkbook(f.Root, rows);
        var result = XlsxImportService.Read(path, sheetName: "Incidents");
        Check(result.Headers.SequenceEqual(new[] { "ID", "Date d'ouverture", "Délai", "Titre", "Colonne 5", "Titre (2)" }));
        Check(result.Rows.Single().SequenceEqual(new[] { "0042", "2026-10-22 09:30:00", "0.5", "", "", "Texte" }));
        path = WriteWorkbook(f.Root, rows.Replace(serial, (double.Parse(serial, CultureInfo.InvariantCulture) - 1462).ToString("R", CultureInfo.InvariantCulture)), true);
        Check(XlsxImportService.Read(path, sheetName: "Incidents").Rows.Single()[1] == "2026-10-22 09:30:00");
        return Task.CompletedTask;
    }
    private static async Task MissingFormulaCaches(SessionFixture f)
    {
        var settings = f.App.Settings.Copy(); var board = DashboardLayoutSettings.CreateBoard("Valeurs inconnues", "empty");
        board.Widgets.Add(new() { Id = "cache-weather", Kind = "metric", SourceKey = "active", Presentation = "weather-volume", WarningValue = 10, CriticalValue = 20, ShowMiniHistory = false });
        settings.Dashboard.Layout = new() { Boards = new() { board }, ActiveBoardId = board.Id };
        await f.Call("settings.save", new { revision = f.App.Revision, settings });
        var header = "<row r='1'>" + TextCell("A1", "ID") + TextCell("B1", "Titre") + TextCell("C1", "Statut") + TextCell("D1", "Description") + "</row>";
        var known = "<row r='2'>" + TextCell("A2", "42") + TextCell("B2", "Visible") + TextCell("C2", "Nouveau");
        foreach (var (rows, missing, count) in new[]
        {
            (known + "<c r='D2' t='str'><f>\"description\"</f></c></row>", 1, 1),
            (known + "<c r='D2' t='b'><f>TRUE()</f></c></row>", 1, 1),
            ("<row r='2'><c r='A2'><f>42</f></c>" + TextCell("B2", "Identifiant calculé") + TextCell("C2", "Nouveau") + "</row><row r='3'><c r='A3'><f>43</f></c><c r='B3' t='str'><f>\"Titre\"</f></c><c r='C3' t='str'><f>\"En attente\"</f></c></row>", 4, 2)
        })
        {
            f.Host.OpenPath = WriteWorkbook(f.Root, header + rows);
            var draft = await f.Call("records.test.import");
            var result = await f.Call("records.test.import", new { token = draft.GetProperty("token").GetString(), sheetName = "Incidents", headerRow = 1 });
            Check(!result.GetProperty("testFileReadComplete").GetBoolean() && result.GetProperty("testMissingFormulaValues").GetInt32() == missing);
            Check(f.App.Result!.Rows.Count == count && f.App.Result.Quality!.SourceRows == count && f.App.Result.Quality.UncachedFormulaCount == missing);
            Check(f.App.Result.Rows[0][3] == "", "A formula without a cache must never be converted into a known false or zero value.");
            Check(f.App.Result.Warnings.Any(w => w.Contains("valeurs sont inconnues", StringComparison.Ordinal)));
            var model = await f.Call("dashboard.get");
            Check(model.GetProperty("widgets")[0].GetProperty("content").GetProperty("volumeWeather").GetProperty("state").GetString() == "partial");
        }
        f.Host.OpenPath = WriteWorkbook(f.Root, header + known + "<c r='D2' t='str'><f>\"\"</f><v/></c></row>");
        var cached = await f.Call("records.test.import");
        var complete = await f.Call("records.test.import", new { token = cached.GetProperty("token").GetString(), sheetName = "Incidents", headerRow = 1 });
        Check(complete.GetProperty("testFileReadComplete").GetBoolean() && f.App.Result!.Quality!.UncachedFormulaCount == 0 && f.App.Result.Rows.Single()[3] == "");
    }
    private static string TextCell(string reference, string value) => "<c r='" + reference + "' t='inlineStr'><is><t xml:space='preserve'>" + SecurityElement.Escape(value) + "</t></is></c>";
    public static string WriteWorkbook(string root, string? incidentRows = null, bool date1904 = false)
    {
        var path = Path.Combine(root, "incidents.xlsx");
        incidentRows ??= "<row r='1'>" + TextCell("A1", "Rapport quotidien") + "</row><row r='4'>" +
            string.Concat(IncidentHeaders.Select((header, i) => TextCell(((char)('A' + i)) + "4", header))) + "</row><row r='5'>" +
            string.Concat(IncidentValues.Select((value, i) => TextCell(((char)('A' + i)) + "5", value))) + "</row>";
        using var output = File.Create(path); using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        void Add(string name, string text) { using var stream = archive.CreateEntry(name).Open(); using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(text); }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Add("xl/workbook.xml", "<workbook xmlns='" + ns + "' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><workbookPr date1904='" + (date1904 ? "1" : "0") + "'/><sheets><sheet name='Dashboard' sheetId='1' r:id='r1'/><sheet name='Incidents' sheetId='2' r:id='r2'/><sheet name='Masquée' state='hidden' sheetId='3' r:id='r3'/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Target='worksheets/sheet1.xml'/><Relationship Id='r2' Target='worksheets/sheet2.xml'/><Relationship Id='r3' Target='worksheets/sheet3.xml'/></Relationships>");
        Add("xl/worksheets/sheet1.xml", "<worksheet xmlns='" + ns + "'><sheetData><row r='1'>" + TextCell("A1", "Indicateur") + TextCell("B1", "Valeur") + "</row><row r='2'>" + TextCell("A2", "Stock") + TextCell("B2", "1") + "</row></sheetData></worksheet>");
        Add("xl/worksheets/sheet2.xml", "<worksheet xmlns='" + ns + "'><sheetData>" + incidentRows + "</sheetData></worksheet>");
        Add("xl/styles.xml", "<styleSheet xmlns='" + ns + "'><numFmts count='1'><numFmt numFmtId='164' formatCode='0000'/></numFmts><cellXfs count='4'><xf numFmtId='0'/><xf numFmtId='22'/><xf numFmtId='46'/><xf numFmtId='164'/></cellXfs></styleSheet>");
        return path;
    }
}
