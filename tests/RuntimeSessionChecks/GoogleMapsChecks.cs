using System.Globalization;
using System.Text.Json;
using GlpiPilot.Core;

internal static class GoogleMapsChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Local(string name, Action action) => tests.Add(("Google Maps: " + name, () => { action(); return Task.CompletedTask; }));
        void Runtime(string name, Func<SessionFixture, Task> action) => tests.Add(("Google Maps: " + name, async () => { await using var fixture = new SessionFixture(); await action(fixture); }));
        Local("site address wins, site coordinates are exact, and city centroids never impersonate a site", Places);
        Local("location text is encoded as data and maps are restricted to their dedicated hosts", UrlSafety);
        Local("long routes retain every leg across nine-waypoint overlapping segments", Waypoints);
        Local("encoded URL limits split routes without dropping places and refuse an impossible leg", UrlLengths);
        Runtime("preview and configuration stay local until explicit consent, and stale references are refused", Consent);
        Runtime("a damaged local key never blocks no-key maps or the repair settings", DamagedKey);
        Runtime("CSV simulation preserves location-only routes, isolates keys and delegates only explicit map clicks", Simulation);
        return tests;
    }
    private static void Check(bool value, string message = "Google Maps contract failed") { if (!value) throw new InvalidOperationException(message); }
    private static async Task Refuse(Func<Task> action, string reason)
    {
        try { await action(); } catch (Exception error) when (error is InvalidOperationException or InvalidDataException && error.Message.Contains(reason, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException("Expected rejection: " + reason);
    }
    private static string Param(string url, string name) => new Uri(url).Query.TrimStart('?').Split('&').Select(value => value.Split('=', 2))
        .Where(pair => pair.Length == 2 && pair[0] == name).Select(pair => Uri.UnescapeDataString(pair[1])).SingleOrDefault() ?? "";
    private static void Places()
    {
        var city = new CityDefinition { Name = "Commune fictive", PostalCode = "00000", Latitude = 45, Longitude = 5 };
        var site = new SiteDefinition { Name = "Atelier fictif", Address = "12 rue de l’Essai, Commune fictive", Latitude = 46, Longitude = 6 };
        Check(GoogleMapsService.ForSite(site, city).Query == site.Address);
        site.Address = ""; Check(GoogleMapsService.ForSite(site, city).Query == "46,6");
        site.Latitude = null; site.Longitude = null;
        var text = GoogleMapsService.ForSite(site, city); Check(text.Query.Contains(site.Name) && text.Query.Contains(city.Name) && !text.Query.Contains("45,5") && text.Precision.Contains("confirmer"));
        Check(GoogleMapsService.ForCity(city).Query == "45,5" && GoogleMapsService.ForCity(city).Precision.Contains("commune"));
    }
    private static void UrlSafety()
    {
        var place = GoogleMapsService.Place("Lieu fictif", "Allée d’essai & entrée #2, A/B", "Adresse");
        var segment = GoogleMapsService.BuildSegments(new[] { place }).Single();
        Check(Param(segment.Url, "query") == place.Query && new Uri(segment.Url).Fragment == "" && GoogleMapsService.IsExternalMapUrl(segment.Url));
        Check(!GoogleMapsService.IsExternalMapUrl("https://www.google.com/maps/search/?api=1&api=1&query=x")
            && !GoogleMapsService.IsExternalMapUrl("https://www.google.com.evil.invalid/maps/search/?api=1&query=x")
            && !GoogleMapsService.IsExternalMapUrl("https://user@www.google.com/maps/search/?api=1&query=x")
            && !GoogleMapsService.IsEmbedUrl("https://www.google.com/maps/embed?pb=undocumented")
            && !GoogleMapsService.IsMapResource("https://unrelated.invalid/resource")
            && !GoogleMapsService.IsMapResource("http://maps.gstatic.com/resource")
            && GoogleMapsService.IsMapResource("https://maps.gstatic.com/resource"));
        var embed = GoogleMapsService.Embed(segment.Places, "SYNTHETIC_KEY"); Check(GoogleMapsService.IsEmbedUrl(embed) && Param(embed, "q") == place.Query);
        try { GoogleMapsService.BuildSegments(new[] { place, new GoogleMapsPlace("Lieu fictif", "12 rue Exemple | entrée B", "Adresse"), place }); throw new Exception("Expected explicit waypoint-delimiter refusal"); }
        catch (InvalidDataException error) { Check(error.Message.Contains("séparateur d’étapes", StringComparison.Ordinal)); }
    }
    private static GoogleMapsPlace[] Locations(int count, Func<int, string>? query = null) => Enumerable.Range(1, count)
        .Select(index => GoogleMapsService.Place("Lieu fictif " + index, query?.Invoke(index) ?? $"{40 + index / 100d:R},5", "Coordonnées")).ToArray();
    private static GoogleMapsPlace[] Flatten(IReadOnlyList<GoogleMapsSegment> segments) => segments.SelectMany((segment, index) => index == 0 ? segment.Places : segment.Places.Skip(1)).ToArray();
    private static void Waypoints()
    {
        var source = Locations(27); var segments = GoogleMapsService.BuildSegments(source);
        Check(segments.Count == 3 && Flatten(segments).SequenceEqual(source));
        Check(segments.All(segment => segment.Places.Count <= 11 && segment.Url.Length <= 2048));
        for (var index = 1; index < segments.Count; index++) Check(segments[index - 1].Places[^1] == segments[index].Places[0] && segments[index - 1].Until == segments[index].From);
        foreach (var segment in segments) Check(Param(segment.Url, "origin") == segment.Places[0].Query && Param(segment.Url, "destination") == segment.Places[^1].Query
            && Param(segment.Url, "waypoints").Split('|', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(segment.Places.Skip(1).SkipLast(1).Select(place => place.Query)));
    }
    private static void UrlLengths()
    {
        var source = Locations(8, index => index + new string('é', 100)); var segments = GoogleMapsService.BuildSegments(source);
        Check(segments.Count > 1 && Flatten(segments).SequenceEqual(source) && segments.All(segment => segment.Url.Length <= 2048
            && GoogleMapsService.Embed(segment.Places, new string('K', 200)).Length <= 2048));
        try { GoogleMapsService.BuildSegments(Locations(2, index => index + new string('é', 500))); throw new Exception("Expected overlength refusal"); }
        catch (InvalidDataException error) { Check(error.Message.Contains("aucune étape", StringComparison.OrdinalIgnoreCase)); }
    }
    private static async Task Consent(SessionFixture f)
    {
        var settings = f.App.Settings.Copy(); var site = new SiteDefinition { Name = "Site de contrôle", Address = "12 rue Fictive, Commune test" }; settings.Cities.Sites.Add(site);
        await f.Call("settings.save", new { revision = f.App.Revision, settings });
        var preview = await f.Call("maps.preview", new { kind = "site", id = site.Id }); var token = preview.GetProperty("segments")[0].GetProperty("token").GetString();
        Check(f.Host.GoogleMapUrls.Count == 0 && f.Host.GoogleMapFrames.Count == 0 && !preview.GetProperty("settings").GetProperty("configured").GetBoolean());
        await Refuse(() => f.Call("maps.open", new { token }), "bouton");
        await Refuse(() => f.Call("maps.open", new { token, consent = true, embedded = true }), "clé");
        await f.Call("maps.open", new { token, consent = true }); Check(f.Host.GoogleMapUrls.Count == 1 && Param(f.Host.GoogleMapUrls[0], "query") == site.Address);
        var keyState = await f.Call("maps.settings.save", new { key = "SYNTHETIC_LOCAL_KEY", remember = false });
        Check(keyState.GetProperty("configured").GetBoolean() && !keyState.GetRawText().Contains("SYNTHETIC_LOCAL_KEY") && !File.Exists(Path.Combine(f.Root, "google-maps.local.json")));
        await f.Call("maps.open", new { token, consent = true, embedded = true }); Check(f.Host.GoogleMapFrames.Count == 1);
        settings = f.App.Settings.Copy(); settings.Cities.Sites[0].Address = "15 rue Fictive, Commune test";
        await f.Call("settings.save", new { revision = f.App.Revision, settings });
        await Refuse(() => f.Call("maps.open", new { token, consent = true }), "changé");
        Check(f.Host.GoogleMapUrls.Count == 1);
    }
    private static async Task Simulation(SessionFixture f)
    {
        var settings = f.App.Settings.Copy(); var city = new CityDefinition { Name = "Commune fictive", Latitude = 44, Longitude = 5 }; settings.Cities.Cities.Add(city);
        var sites = Enumerable.Range(1, 13).Select(index => new SiteDefinition { Name = "Site fictif " + index, CityId = city.Id,
            Aliases = new() { "SF" + index.ToString("00", CultureInfo.InvariantCulture) }, Address = index + " rue Fictive, Commune fictive" }).ToArray();
        settings.Cities.Sites.AddRange(sites); await f.Call("settings.save", new { revision = f.App.Revision, settings });
        await f.Call("maps.settings.save", new { key = "REAL_SESSION_SYNTHETIC_KEY" });
        f.Host.OpenPath = Path.Combine(f.Root, "maps-simulation.csv");
        await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre;Statut;Attribué à - Technicien\n" + string.Join('\n', sites.Select((site, index) =>
            (index + 1) + ";" + site.Aliases[0] + " CONTENU_TICKET_NE_JAMAIS_TRANSMETTRE;Nouveau;AGENT_NE_JAMAIS_TRANSMETTRE")));
        await f.Call("records.test.import");
        Check(!(await f.Call("maps.settings.get")).GetProperty("configured").GetBoolean(), "Normal API keys must not be borrowed by a simulation.");
        await f.Call("terrain.tours.projection", new { revision = f.App.Revision });
        var projection = await f.Call("maps.preview", new { kind = "projection", revision = f.App.Revision });
        Check(projection.GetProperty("segments")[0].GetProperty("places")[0].GetProperty("query").GetString() == "44,5");
        await f.Call("terrain.tours.select", new { revision = f.App.Revision, rows = Enumerable.Range(0, 13).ToArray(), selected = true });
        var tour = await f.Call("terrain.tours.create", new { revision = f.App.Revision, name = "Tournée fictive", date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        var map = await f.Call("maps.preview", new { kind = "tour", revision = f.App.Revision, draftVersion = tour.GetProperty("draftVersion").GetInt32() });
        var segments = map.GetProperty("segments").EnumerateArray().ToArray();
        Check(segments.Length == 2 && segments.SelectMany((segment, index) => segment.GetProperty("places").EnumerateArray().Skip(index == 0 ? 0 : 1)).Count() == 13);
        Check(!map.GetRawText().Contains("CONTENU_TICKET_NE_JAMAIS_TRANSMETTRE") && !map.GetRawText().Contains("AGENT_NE_JAMAIS_TRANSMETTRE") && f.Host.GoogleMapUrls.Count == 0);
        var token = segments[0].GetProperty("token").GetString(); await f.Call("maps.open", new { token, consent = true });
        Check(f.Host.GoogleMapUrls.Count == 1 && GoogleMapsService.IsExternalMapUrl(f.Host.GoogleMapUrls.Single()));
        await f.Call("maps.settings.save", new { key = "SIMULATION_SYNTHETIC_KEY" });
        await f.Call("maps.open", new { token, consent = true, embedded = true }); Check(f.Host.GoogleMapFrames.Single().Simulation);
        await Refuse(() => f.Call("maps.settings.save", new { key = "SIMULATION_SYNTHETIC_KEY", remember = true }), "session");
        await f.Call("maps.settings.save", new { forget = true }); Check(!(await f.Call("maps.settings.get")).GetProperty("configured").GetBoolean());
        await f.Call("records.test.exit"); Check((await f.Call("maps.settings.get")).GetProperty("configured").GetBoolean());
    }
    private static async Task DamagedKey(SessionFixture f)
    {
        var settings = f.App.Settings.Copy(); var site = new SiteDefinition { Name = "Site de contrôle" }; settings.Cities.Sites.Add(site);
        await f.Call("settings.save", new { revision = f.App.Revision, settings });
        var path = Path.Combine(f.Root, "google-maps.local.json");
        foreach (var damaged in new[] { "{", "{\"ProtectedKey\":\"invalid-protected-value\"}" })
        {
            await File.WriteAllTextAsync(path, damaged);
            var state = await f.Call("maps.settings.get"); Check(!state.GetProperty("configured").GetBoolean() && state.GetProperty("warning").GetString()!.Length > 0);
            var preview = await f.Call("maps.preview", new { kind = "site", id = site.Id });
            await f.Call("maps.open", new { token = preview.GetProperty("segments")[0].GetProperty("token").GetString(), consent = true });
        }
        var repaired = await f.Call("maps.settings.save", new { forget = true });
        Check(f.Host.GoogleMapUrls.Count == 2 && !File.Exists(path) && repaired.GetProperty("warning").GetString() == "");
    }
}
