using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public static class SitePatchChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add(("Site patch: " + name, async () => { await using var f = new SessionFixture(); await run(f); }));
        Add("standard receives a paged exact comparison without changing settings", async f =>
        {
            var patch = await Setup(f); var before = JsonSerializer.Serialize(f.App.Settings);
            var review = await f.Call("update.sites.preview");
            Check(review.GetProperty("modified").GetInt32() == 1 && review.GetProperty("added").GetInt32() == 1);
            var item = review.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("kind").GetString() == "Modification");
            var field = item.GetProperty("fields").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "Adresse");
            Check(field.GetProperty("before").GetString() == "Correction locale" && field.GetProperty("after").GetString() == "Nouvelle adresse");
            var page = await f.Call("update.sites.page", new { token = review.GetProperty("token").GetString(), offset = 1, limit = 1 });
            Check(page.GetProperty("items").GetArrayLength() == 1 && before == JsonSerializer.Serialize(f.App.Settings));
        });
        Add("confirmed merge preserves IDs and absent sites, saves a restorable backup, and repeated import has no additional effect", async f =>
        {
            await Setup(f); var before = f.App.Settings.Cities.Copy(); var review = await f.Call("update.sites.preview");
            await Refuse(() => f.Call("update.sites.apply", new { token = Token(review) }), "Confirmez");
            var applied = await f.Call("update.sites.apply", new { token = Token(review), confirmed = true });
            Check(f.App.Settings.Cities.Sites.Count == 3 && f.App.Settings.Cities.Sites.Single(site => site.Id == Site).Address == "Nouvelle adresse"
                && f.App.Settings.Cities.Sites.Single(site => site.Id == Retained).Name == "Site local conservé");
            var backup = applied.GetProperty("backupPath").GetString()!;
            var restored = ConfigurationTransferService.Import(backup, f.App.Settings, out _);
            Check(JsonSerializer.Serialize(restored.Cities) == JsonSerializer.Serialize(before) && restored.PortalUrl == f.App.Settings.PortalUrl);
            Check(AppSettings.Load().SitePatchAppliedHash == applied.GetProperty("sha256").GetString());
            await Refuse(() => f.Call("update.sites.apply", new { token = Token(review), confirmed = true }), "changé");
            review = await f.Call("update.sites.preview"); Check(review.GetProperty("modified").GetInt32() == 0 && review.GetProperty("added").GetInt32() == 0);
            var after = JsonSerializer.Serialize(f.App.Settings.Cities);
            applied = await f.Call("update.sites.apply", new { token = Token(review), confirmed = true });
            Check(applied.GetProperty("backupPath").GetString() == "" && after == JsonSerializer.Serialize(f.App.Settings.Cities));
        });
        Add("local edits after preview invalidate confirmation without overwriting them", async f =>
        {
            await Setup(f); var review = await f.Call("update.sites.preview");
            f.App.Settings.Cities.Sites.Single(site => site.Id == Site).Address = "Modification après aperçu";
            await Refuse(() => f.Call("update.sites.apply", new { token = Token(review), confirmed = true }), "changé");
            Check(f.App.Settings.Cities.Sites.Single(site => site.Id == Site).Address == "Modification après aperçu");
        });
        Add("changed published bytes are rejected even when the checksum file was not updated", async f =>
        {
            var path = await Setup(f); var review = await f.Call("update.sites.preview");
            var bytes = File.ReadAllBytes(path); bytes[^3] ^= 1; File.WriteAllBytes(path, bytes);
            await Refuse(() => f.Call("update.sites.apply", new { token = Token(review), confirmed = true }), "empreinte");
            Check(f.App.Settings.Cities.Sites.Single(site => site.Id == Site).Address == "Correction locale");
        });
        Add("a draft in the reference editor cannot be silently discarded", async f =>
        {
            await Setup(f); var state = await f.Call("terrain.reference.state");
            await f.Call("terrain.reference.configure", new { draftVersion = state.GetProperty("draftVersion").GetInt32(), enabled = false, titleColumn = "Titre", locationColumn = "", sourcePriority = new[] { "manual", "combined" } });
            await Refuse(() => f.Call("update.sites.preview"), "brouillon");
        });
        Add("unversioned columns or missing stable IDs never produce a partial merge", async f =>
        {
            var path = await Setup(f); File.WriteAllText(path, "Type;Nom\nSite;Sans identité\n"); await Seal(path);
            await Refuse(() => f.Call("update.sites.preview"), "v1"); Check(f.App.Settings.Cities.Sites.Count == 2);
        });
        Add("changing the configured share invalidates both preview and prepared update", async f =>
        {
            await Setup(f); var review = await f.Call("update.sites.preview");
            await f.Call("update.shared.save", new { folder = "" });
            await Refuse(() => f.Call("update.sites.apply", new { token = Token(review), confirmed = true }), "changé");
        });
        Add("demonstration blocks real network and patch operations", async f =>
        {
            await f.Call("app.demo");
            foreach (var op in new[] { "update.shared.save", "update.shared.find", "update.shared.prepare", "update.sites.preview", "update.sites.apply" })
                await Refuse(() => f.Call(op), "démonstration");
        });
        Add("v1 text encoding preserves phone prefixes, literal apostrophes, formula-like values and its own reserved prefix", async f =>
        {
            var values = new[] { "+33 1 23 45 67 89", "-Annexe fictive", "'Apostrophe littérale", "'=payload", "=HYPERLINK(\"https://example.invalid\")", "@Téléphone", "  +33", "\u200E+33", "~glpi-text-v1:already-literal", "Unicode café 🏥" };
            var reference = new CitySettings { Sites = values.Select((value, i) => new SiteDefinition
            { Id = (i + 100).ToString("x32"), Name = "Site texte " + i, Contact = value, Latitude = -12.34, Longitude = -45.67 }).ToList() };
            var path = Path.Combine(f.Root, "patch-texte.csv"); SitePatchFormat.Write(reference, path);
            var original = CityReferenceService.Export(reference); var decoded = SitePatchFormat.Read(path);
            Check(original.Headers.SequenceEqual(decoded.Headers) && original.Rows.Zip(decoded.Rows).All(pair => pair.First.Where((_, i) => i is not (4 or 12 or 18)).SequenceEqual(pair.Second.Where((_, i) => i is not (4 or 12 or 18)))));
            var raw = NativeCsvImportService.Read(path, ""); var contact = raw.Headers.IndexOf("Contact");
            Check(raw.Rows.Take(values.Length - 1).All(row => row[contact].StartsWith(SitePatchFormat.TextPrefix, StringComparison.Ordinal)));
            var merged = SitePatchFormat.Preview(decoded, new CitySettings());
            Check(merged.Settings.Sites[0].Contact == values[0] && merged.Settings.Sites[2].Contact == values[2] && merged.Settings.Sites[8].Contact == values[8]
                && merged.Settings.Sites.All(site => site.Latitude == -12.34 && site.Longitude == -45.67));
            SitePatchFormat.Write(merged.Settings, path);
            Check(SitePatchFormat.Preview(SitePatchFormat.Read(path), merged.Settings).Changes.All(item => item.Kind == "Identique"));
            await Task.CompletedTask;
        });
        Add("malformed v1 encoded text is refused before a reference preview can mutate settings", async f =>
        {
            var path = await Setup(f); var text = File.ReadAllText(path).Replace("Nouvelle adresse", SitePatchFormat.TextPrefix + "not-base64!", StringComparison.Ordinal);
            File.WriteAllText(path, text); await Seal(path);
            await Refuse(() => f.Call("update.sites.preview"), "encodée");
            Check(f.App.Settings.Cities.Sites.Single(site => site.Id == Site).Address == "Correction locale");
        });
        Add("v1 typed lists preserve literal separators, reserved prefixes, Unicode and item boundaries", async f =>
        {
            var aliases = new List<string> { "Annexe | Nord", "Accueil ; bâtiment B", "~glpi-list-v1:literal", "~glpi-text-v1:literal", "Repère 🏥" };
            var city = new CityDefinition { Id = "11111111111111111111111111111111", Name = "Ville fictive", Aliases = aliases.ToList(), ExcludedTerms = aliases.ToList() };
            var site = new SiteDefinition { Id = Site, Name = "Site fictif", CityId = city.Id, Aliases = aliases.ToList(), ClosedDates = ["2030-01-07", "2030-02-09"] };
            var building = new BuildingDefinition { Id = Added, SiteId = site.Id, Name = "Bâtiment fictif", Aliases = aliases.ToList() };
            var reference = new CitySettings { Cities = [city], Sites = [site], Buildings = [building] };
            var path = Path.Combine(f.Root, "patch-listes.csv"); SitePatchFormat.Write(reference, path);
            var merged = SitePatchFormat.Preview(SitePatchFormat.Read(path), new CitySettings()).Settings;
            Check(merged.Cities[0].Aliases.SequenceEqual(aliases) && merged.Cities[0].ExcludedTerms.SequenceEqual(aliases)
                && merged.Sites[0].Aliases.SequenceEqual(aliases) && merged.Buildings[0].Aliases.SequenceEqual(aliases)
                && merged.Sites[0].ClosedDates.SequenceEqual(site.ClosedDates));
            SitePatchFormat.Write(merged, path);
            Check(SitePatchFormat.Preview(SitePatchFormat.Read(path), merged).Changes.All(item => item.Kind == "Identique"));
            await Task.CompletedTask;
        });
        return tests;
    }
    private const string Site = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Retained = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Added = "cccccccccccccccccccccccccccccccc";
    private static async Task<string> Setup(SessionFixture f)
    {
        f.App.Settings.Cities = new() { Sites = new() { new() { Id = Site, Name = "Site de démonstration", Address = "Correction locale" }, new() { Id = Retained, Name = "Site local conservé" } } };
        f.App.Settings.Save();
        var patch = new CitySettings { Sites = new() { new() { Id = Site, Name = "Site de démonstration", Address = "Nouvelle adresse" }, new() { Id = Added, Name = "Annexe fictive" } } };
        var share = Path.Combine(f.Root, "diffusion"); Directory.CreateDirectory(share);
        var path = Path.Combine(share, SharedUpdateSource.SiteFile); SitePatchFormat.Write(patch, path); await Seal(path);
        await f.Call("update.shared.save", new { folder = share }); return path;
    }
    private static Task Seal(string path) => File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, SharedUpdateSource.SiteHashes), UpdateService.HashFile(path) + "  " + SharedUpdateSource.SiteFile + "\n");
    private static string Token(JsonElement value) => value.GetProperty("token").GetString()!;
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Site patch assertion failed."); }
    private static async Task Refuse(Func<Task> action, string text)
    {
        try { await action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException && e.Message.Contains(text, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException("Expected refusal: " + text);
    }
}
