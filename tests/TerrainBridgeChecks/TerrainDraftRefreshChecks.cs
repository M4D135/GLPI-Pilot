using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class TerrainDraftRefreshChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        const string url = "https://reference-refresh.example.invalid/front/ticket.php";
        var settings = new AppSettings { PortalUrl = url };
        var host = new TerrainTestHost { OpenPath = Path.Combine(root, "reference-refresh.xlsx") };
        await using var app = new PilotApplication(host, (_, _) => { }, settings);
        app.LoadFixture(new()
        {
            SourceUrl = url, Complete = true, Headers = new() { "ID", "Titre", "Statut" },
            Rows = new() { new[] { "1", "Incident TERME-NORD", "Nouveau" } },
            Provenance = new() { SourceKind = "glpi", RequestedUrl = url, SearchVerified = true, ScopeVerified = true }
        });
        async Task<JsonElement> Call(string operation, object? arguments = null)
            => JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation,
                JsonSerializer.SerializeToElement(arguments ?? new { }, PilotApplication.Json)), PilotApplication.Json);
        static int Version(JsonElement value) => value.GetProperty("draftVersion").GetInt32();
        async Task ApplyProfile()
        {
            var preview = await Call("dashboard.business.profile.preview");
            await Call("dashboard.business.profile.apply", new { token = preview.GetProperty("token").GetString() });
        }

        var initial = await Call("terrain.reference.state");
        var before = await Call("terrain.reference.preview", new { draftVersion = Version(initial), revision = app.Revision });
        WriteWorkbook(host.OpenPath, expanded: false);
        var profile = await Call("dashboard.business.profile.preview");
        check(app.Settings.Cities.Sites.Count == 0 && (await Call("terrain.reference.state")).GetProperty("sites").GetInt32() == 0,
            "workbook preview does not replace the saved reference or its clean draft");
        await Call("dashboard.business.profile.apply", new { token = profile.GetProperty("token").GetString() });
        var saved = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        var refreshed = await Call("terrain.reference.state");
        check(refreshed.GetProperty("sites").GetInt32() == 1 && !refreshed.GetProperty("dirty").GetBoolean() && !refreshed.GetProperty("conflicted").GetBoolean()
            && Version(refreshed) > Version(initial), "a clean terrain draft refreshes immediately after workbook profile application");
        var list = await Call("terrain.reference.list", new { kind = "site" });
        var detection = await Call("terrain.review.detail", new { revision = app.Revision, row = 0 });
        check(list.GetProperty("items")[0].GetProperty("aliases").GetString() == "TERME-NORD"
            && detection.GetProperty("match").GetProperty("siteName").GetString() == "Site synthétique Nord",
            "the refreshed site list and the actual detector use the imported terms from the same saved reference");
        await reject(() => Call("terrain.reference.apply", new { token = before.GetProperty("token").GetString(), confirmed = true }),
            "an earlier terrain preview cannot be applied after a clean draft refresh");
        var stable = await Call("terrain.reference.state");
        check(Version(stable) == Version(refreshed) && (await File.ReadAllBytesAsync(AppPaths.SettingsFile)).SequenceEqual(saved),
            "reading the refreshed reference neither increments its version again nor rewrites saved settings");

        var edited = app.Settings.Cities.Sites.Single().Copy(); edited.Name = "Nom modifié dans le brouillon"; edited.Aliases.Add("EDITION-LOCALE");
        var changed = await Call("terrain.reference.upsert", new { draftVersion = Version(stable), kind = "site", entry = edited });
        var retained = await Call("terrain.reference.detail", new { kind = "site", id = edited.Id });
        WriteWorkbook(host.OpenPath, expanded: true); await ApplyProfile();
        var conflicted = await Call("terrain.reference.state");
        check(app.Settings.Cities.Sites.Count == 2 && conflicted.GetProperty("sites").GetInt32() == 1
            && conflicted.GetProperty("dirty").GetBoolean() && conflicted.GetProperty("conflicted").GetBoolean()
            && Version(conflicted) == Version(changed), "an edited terrain draft survives a later profile import and explicitly reports the saved-reference conflict");
        var current = await Call("terrain.reference.detail", new { kind = "site", id = edited.Id });
        check(current.GetProperty("entry").GetRawText() == retained.GetProperty("entry").GetRawText(),
            "the conflicting draft retains the exact local name and aliases without merging or discarding edits");
        await reject(() => Call("terrain.reference.preview", new { draftVersion = Version(conflicted), revision = app.Revision }),
            "a conflicting draft cannot overwrite the newly imported saved reference");
        var reset = await Call("terrain.reference.reset", new { draftVersion = Version(conflicted) });
        check(reset.GetProperty("sites").GetInt32() == 2 && !reset.GetProperty("dirty").GetBoolean() && !reset.GetProperty("conflicted").GetBoolean()
            && Version(reset) > Version(conflicted), "explicitly abandoning an edited draft reloads all sites from the imported saved reference");
        var reloaded = await Call("terrain.reference.detail", new { kind = "site", id = edited.Id });
        check(reloaded.GetProperty("entry").GetProperty("name").GetString() == "Site synthétique Nord"
            && reloaded.GetProperty("entry").GetProperty("aliases").EnumerateArray().Select(value => value.GetString()).Contains("TERME-AJOUTE"),
            "the explicit reload exposes the imported name and new detection term");
    }

    private static void WriteWorkbook(string path, bool expanded)
    {
        XNamespace sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace document = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace relation = "http://schemas.openxmlformats.org/package/2006/relationships";
        using var file = File.Create(path); using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        void Write(string name, XElement value)
        { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(value.ToString(SaveOptions.DisableFormatting)); }
        XElement Row(int index, string name, string aliases) => new(sheet + "row", new XAttribute("r", index),
            new[] { name, aliases }.Select((value, column) => new XElement(sheet + "c", new XAttribute("r", (column == 0 ? "A" : "B") + index),
                new XAttribute("t", "inlineStr"), new XElement(sheet + "is", new XElement(sheet + "t", value)))));
        Write("xl/workbook.xml", new(sheet + "workbook", new XElement(sheet + "sheets", new XElement(sheet + "sheet",
            new XAttribute("name", "Paramètres"), new XAttribute("sheetId", "1"), new XAttribute(document + "id", "r1")))));
        Write("xl/_rels/workbook.xml.rels", new(relation + "Relationships", new XElement(relation + "Relationship",
            new XAttribute("Id", "r1"), new XAttribute("Type", document.NamespaceName + "/worksheet"), new XAttribute("Target", "worksheets/sheet1.xml"))));
        Write("xl/worksheets/sheet1.xml", new(sheet + "worksheet", new XElement(sheet + "sheetData",
            Row(1, "Site affiché", "Écritures reconnues"), Row(2, "Site synthétique Nord", expanded ? "TERME-NORD;TERME-AJOUTE" : "TERME-NORD"),
            expanded ? Row(3, "Site synthétique Sud", "TERME-SUD") : null)));
    }
}
