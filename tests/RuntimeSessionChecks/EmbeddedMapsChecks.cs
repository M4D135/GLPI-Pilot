using System.Globalization;
using System.Text.Json;
using GlpiPilot.Core;

internal static class EmbeddedMapsChecks
{
    private const string Tile = "https://tile.openstreetmap.org/12/2107/1501.png";
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Local(string name, Action action) => tests.Add(("Embedded maps: " + name, () => { action(); return Task.CompletedTask; }));
        void Runtime(string name, Func<SessionFixture, Task> action) => tests.Add(("Embedded maps: " + name, async () => { await using var fixture = new SessionFixture(); await action(fixture); }));
        Local("site coordinates remain exact and missing positions never borrow city centres", Places);
        Local("the network allowlist admits only canonical HTTPS OSM image tile addresses", Tiles);
        Runtime("all sites, selected sites and an empty selection preserve the exact requested scope", Selection);
        Runtime("tiles require a live map grant, which closing and changed settings revoke", Grants);
        Runtime("reference draft edits revoke active tiles without changing saved references", Drafts);
        Runtime("CSV simulation retains every stop and isolates map access, days and assignments", Simulation);
        Runtime("old Google keys are ignored and no external map operation is registered", Legacy);
        return tests;
    }
    private static void Check(bool value, string message = "Embedded map contract failed") { if (!value) throw new InvalidOperationException(message); }
    private static async Task Refuse(Func<Task> action, string reason)
    {
        try { await action(); }
        catch (Exception error) when ((error is InvalidOperationException or InvalidDataException) && error.Message.Contains(reason, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException("Expected rejection: " + reason);
    }
    private static string Token(JsonElement value) => value.GetProperty("token").GetString()!;
    private static JsonElement Places(JsonElement map) => map.GetProperty("routes")[0].GetProperty("places");
    private static void Places()
    {
        var city = new CityDefinition { Name = "Commune fictive", PostalCode = "00000", Latitude = 45, Longitude = 5 };
        var site = new SiteDefinition { Name = "Atelier fictif", Address = "12 rue de l’Essai, Commune fictive", Latitude = 46, Longitude = 6 };
        var place = EmbeddedMapsService.ForSite(site, city);
        Check(place.Latitude == 46 && place.Longitude == 6 && place.Address == site.Address);
        site.Latitude = null; site.Longitude = null; place = EmbeddedMapsService.ForSite(site, city);
        Check(place.Latitude is null && place.Longitude is null && place.Address == site.Address && place.Precision.Contains("manquantes"));
        Check(EmbeddedMapsService.ForCity(city).Latitude == 45 && EmbeddedMapsService.ForCity(city).Precision.Contains("commune"));
        Check(EmbeddedMapsService.ForPoint(new(), "start", "Départ") is null);
        Check(EmbeddedMapsService.ForPoint(new() { Label = "Lieu à compléter" }, "start", "Départ") is { Latitude: null });
        Check(!EmbeddedMapsService.ValidCoordinates(double.NaN, 5) && !EmbeddedMapsService.ValidCoordinates(45, double.PositiveInfinity));
    }
    private static void Tiles()
    {
        Check(EmbeddedMapsService.IsTileUrl(Tile) && EmbeddedMapsService.IsTileUrl("https://tile.openstreetmap.org/0/0/0.png")
            && EmbeddedMapsService.IsTileUrl("https://tile.openstreetmap.org/19/524287/524287.png"));
        foreach (var url in new[] { "http://tile.openstreetmap.org/0/0/0.png", "https://tile.openstreetmap.org.evil.invalid/0/0/0.png",
            "https://user@tile.openstreetmap.org/0/0/0.png", "https://tile.openstreetmap.org:8443/0/0/0.png", Tile + "?address=private", Tile + "#fragment",
            "https://tile.openstreetmap.org/20/0/0.png", "https://tile.openstreetmap.org/0/1/0.png", "https://tile.openstreetmap.org/1/-1/0.png",
            "https://tile.openstreetmap.org/01/0/0.png", "https://tile.openstreetmap.org/1/0/0.svg", "https://tile.openstreetmap.org/index.html" })
            Check(!EmbeddedMapsService.IsTileUrl(url), "Refused map resource: " + url);
    }
    private static async Task<SiteDefinition[]> AddSites(SessionFixture f, int count)
    {
        var settings = f.App.Settings.Copy(); var city = new CityDefinition { Name = "Commune fictive", Latitude = 44, Longitude = 5 }; settings.Cities.Cities.Add(city);
        var sites = Enumerable.Range(1, count).Select(index => new SiteDefinition { Name = "Site fictif " + index.ToString("00", CultureInfo.InvariantCulture), CityId = city.Id,
            Aliases = new() { "SF" + index.ToString("00", CultureInfo.InvariantCulture) }, Address = index + " rue Fictive, Commune fictive",
            Latitude = index == 2 ? null : 44 + index / 100d, Longitude = index == 2 ? null : 5 + index / 100d }).ToArray();
        settings.Cities.Sites.AddRange(sites); await f.Call("settings.save", new { revision = f.App.Revision, settings }); return sites;
    }
    private static async Task Selection(SessionFixture f)
    {
        var sites = await AddSites(f, 26);
        var all = await f.Call("maps.preview", new { kind = "sites" });
        Check(Places(all).GetArrayLength() == 26 && !all.GetProperty("routes")[0].GetProperty("ordered").GetBoolean() && !f.App.AllowsMapTileRequest(Tile));
        var selected = await f.Call("maps.preview", new { kind = "sites", ids = new[] { sites[0].Id, "site:" + sites[2].Id, sites[0].Id } });
        Check(Places(selected).GetArrayLength() == 2 && Places(selected)[1].GetProperty("id").GetString() == "site:" + sites[2].Id);
        Check(Places(await f.Call("maps.preview", new { kind = "sites", ids = Array.Empty<string>() })).GetArrayLength() == 0);
        await Refuse(() => f.Call("maps.preview", new { kind = "sites", ids = new[] { "unknown" } }), "n’existe plus");
        await Refuse(() => f.Call("maps.preview", new { kind = "sites", ids = Enumerable.Repeat(sites[0].Id, 2001).ToArray() }), "volumineuse");
        Check(Places(all).EnumerateArray().Count(place => place.GetProperty("latitude").ValueKind == JsonValueKind.Null) == 1);
    }
    private static async Task Grants(SessionFixture f)
    {
        var sites = await AddSites(f, 1); var preview = await f.Call("maps.preview", new { kind = "site", id = sites[0].Id }); var token = Token(preview);
        Check(!f.App.AllowsMapTileRequest(Tile));
        await Refuse(() => f.Call("maps.tiles", new { token = "unknown", enabled = true }), "expiré");
        var enabled = await f.Call("maps.tiles", new { token, enabled = true });
        Check(enabled.GetProperty("tileUrl").GetString() == EmbeddedMapsService.TileTemplate && f.App.AllowsMapTileRequest(Tile));
        await f.Call("maps.tiles", new { token = "different-map", enabled = false }); Check(f.App.AllowsMapTileRequest(Tile));
        await f.Call("maps.tiles", new { token, enabled = false }); Check(!f.App.AllowsMapTileRequest(Tile));
        await f.Call("maps.tiles", new { token, enabled = true });
        var settings = f.App.Settings.Copy(); settings.Cities.Sites[0].Latitude = 43;
        await f.Call("settings.save", new { revision = f.App.Revision, settings });
        Check(!f.App.AllowsMapTileRequest(Tile));
        await Refuse(() => f.Call("maps.tiles", new { token, enabled = true }), "changé");
        await f.Call("maps.tiles", new { token, enabled = false });
    }
    private static async Task Drafts(SessionFixture f)
    {
        var sites = await AddSites(f, 1); var state = await f.Call("terrain.reference.state");
        var map = await f.Call("maps.preview", new { kind = "site", id = sites[0].Id, referenceDraft = true, draftVersion = state.GetProperty("draftVersion").GetInt32() });
        await f.Call("maps.tiles", new { token = Token(map), enabled = true }); Check(f.App.AllowsMapTileRequest(Tile));
        var changed = sites[0].Copy(); changed.Latitude = 43;
        await f.Call("terrain.reference.upsert", new { kind = "site", entry = changed, draftVersion = state.GetProperty("draftVersion").GetInt32() });
        Check(!f.App.AllowsMapTileRequest(Tile) && f.App.Settings.Cities.Sites[0].Latitude == sites[0].Latitude);
        await Refuse(() => f.Call("maps.tiles", new { token = Token(map), enabled = true }), "changé");
    }
    private static async Task Simulation(SessionFixture f)
    {
        var sites = await AddSites(f, 13); var ordinary = JsonSerializer.Serialize(f.App.Settings, GlpiPilot.Runtime.PilotApplication.Json);
        var normalMap = await f.Call("maps.preview", new { kind = "sites" });
        await f.Call("maps.tiles", new { token = Token(normalMap), enabled = true }); Check(f.App.AllowsMapTileRequest(Tile));
        f.Host.OpenPath = Path.Combine(f.Root, "maps-simulation.csv");
        await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre;Statut;Attribué à - Technicien\n" + string.Join('\n', sites.Select((site, index) =>
            (index + 1) + ";" + site.Aliases[0] + " CONTENU_TICKET_CONFIDENTIEL;Nouveau;AGENT_CONFIDENTIEL")));
        await f.Call("records.test.import"); Check(!f.App.AllowsMapTileRequest(Tile));
        await Refuse(() => f.Call("maps.tiles", new { token = Token(normalMap), enabled = true }), "expiré");
        await f.Call("terrain.tours.projection", new { revision = f.App.Revision });
        var projection = await f.Call("maps.preview", new { kind = "projection", revision = f.App.Revision });
        Check(Places(projection)[0].GetProperty("latitude").GetDouble() == 44 && Places(projection)[0].GetProperty("precision").GetString()!.Contains("commune"));
        await f.Call("terrain.tours.select", new { revision = f.App.Revision, rows = Enumerable.Range(0, 13).ToArray(), selected = true });
        var day = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var tour = await f.Call("terrain.tours.create", new { revision = f.App.Revision, name = "Tournée fictive", date = day });
        var version = tour.GetProperty("draftVersion").GetInt32();
        var map = await f.Call("maps.preview", new { kind = "tour", revision = f.App.Revision, draftVersion = version });
        Check(map.GetProperty("routes").GetArrayLength() == 1 && Places(map).GetArrayLength() == 13
            && Places(map).EnumerateArray().Select(place => place.GetProperty("sequence").GetInt32()).SequenceEqual(Enumerable.Range(1, 13)));
        Check(!map.GetRawText().Contains("CONTENU_TICKET_CONFIDENTIEL") && !map.GetRawText().Contains("AGENT_CONFIDENTIEL"));
        Check(Places(map).EnumerateArray().Single(place => place.GetProperty("id").GetString() == "site:" + sites[1].Id).GetProperty("latitude").ValueKind == JsonValueKind.Null);
        await f.Call("maps.tiles", new { token = Token(map), enabled = true }); Check(f.App.AllowsMapTileRequest(Tile));
        var stops = await f.Call("terrain.tours.stops", new { revision = f.App.Revision, draftVersion = version, limit = 100 });
        var stopId = stops.GetProperty("items")[0].GetProperty("stop").GetProperty("id").GetString();
        var edited = await f.Call("terrain.tours.stopEdit", new { revision = f.App.Revision, draftVersion = version, stopId,
            fields = new { date = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), technicianKey = "AFFECTATION_CONFIDENTIELLE", requiredSkills = Array.Empty<string>() } });
        Check(!f.App.AllowsMapTileRequest(Tile));
        var separated = await f.Call("maps.preview", new { kind = "tour", revision = f.App.Revision, draftVersion = edited.GetProperty("draftVersion").GetInt32() });
        Check(separated.GetProperty("routes").GetArrayLength() == 2 && separated.GetProperty("routes").EnumerateArray().Sum(route => route.GetProperty("places").GetArrayLength()) == 13
            && !separated.GetRawText().Contains("AFFECTATION_CONFIDENTIELLE"));
        await f.Call("maps.tiles", new { token = Token(separated), enabled = true }); Check(f.App.AllowsMapTileRequest(Tile));
        await f.Call("records.test.exit");
        Check(!f.App.AllowsMapTileRequest(Tile) && JsonSerializer.Serialize(f.App.Settings, GlpiPilot.Runtime.PilotApplication.Json) == ordinary);
    }
    private static async Task Legacy(SessionFixture f)
    {
        await AddSites(f, 1); var path = Path.Combine(f.Root, "google-maps.local.json"); await File.WriteAllTextAsync(path, "{legacy-unreadable-key");
        var preview = await f.Call("maps.preview", new { kind = "sites" });
        Check(Places(preview).GetArrayLength() == 1 && !preview.GetRawText().Contains("key") && await File.ReadAllTextAsync(path) == "{legacy-unreadable-key");
        Check(!f.App.Operations.Contains("maps.open") && !f.App.Operations.Contains("maps.settings.save") && !f.App.Operations.Contains("maps.settings.get"));
        await f.Call("maps.tiles", new { token = Token(preview), enabled = true }); Check(f.App.AllowsMapTileRequest(Tile));
    }
}
