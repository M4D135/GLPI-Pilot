using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class TourServiceSettings
{
    public string RoutingEndpoint { get; set; } = "";
    public string RoutingProfile { get; set; } = "driving";
    public string WeatherEndpoint { get; set; } = "";
    public void Validate()
    {
        if (RoutingEndpoint == null || WeatherEndpoint == null || RoutingProfile == null) throw new InvalidDataException("Réglages de fournisseurs terrain invalides.");
        if (RoutingEndpoint.Length > 0) _ = TourExternalServices.Endpoint(RoutingEndpoint);
        if (WeatherEndpoint.Length > 0) _ = TourExternalServices.Endpoint(WeatherEndpoint);
        if (RoutingProfile.Length is < 1 or > 40 || RoutingProfile.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_'))) throw new InvalidDataException("Profil routier invalide.");
    }
    public static TourServiceSettings Load(string path)
    {
        if (!File.Exists(path)) return new(); using var stream = File.OpenRead(path); if (stream.Length > 16_384) throw new InvalidDataException("Les réglages terrain dépassent la taille autorisée.");
        var settings = JsonSerializer.Deserialize<TourServiceSettings>(stream) ?? throw new InvalidDataException("Réglages terrain vides."); settings.Validate(); return settings;
    }
    public void Save(string path)
    {
        Validate(); var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Opt-in providers; request payloads contain only coordinates, dates and route profile. No browser/session data.</summary>
public sealed class TourExternalServices : IDisposable
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient _client;
    public TourExternalServices(HttpMessageHandler? handler = null)
    {
        if (AppPaths.SimulationRoot is not null) throw new InvalidOperationException("La simulation utilise ses trajets et sa météo fictifs, sans fournisseur réseau.");
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("GLPI-Pilot-Terrain/1.0");
    }
    public static Uri Endpoint(string value)
    {
        if (value == null || value.Length > 1000 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("L’adresse du fournisseur doit être HTTP(S), sans identifiant, paramètre ni fragment. Utilisez votre service autorisé.");
        return uri;
    }
    public async Task<ScheduledTour> RouteAsync(ScheduledTour source, TourPlanningContext context, TourServiceSettings settings, bool consent, CancellationToken ct = default)
    {
        if (!consent) throw new InvalidOperationException("Autorisez explicitement l’envoi des coordonnées avant le calcul routier.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromMinutes(2)); ct = budget.Token;
        source.Validate(); settings.Validate(); var endpoint = Endpoint(settings.RoutingEndpoint); var copy = source.Copy(); var legs = new List<TourRoadLeg>();
        foreach (var group in source.Stops.GroupBy(stop => (Day: stop.Date.Length == 0 ? source.Date : stop.Date, Technician: stop.TechnicianKey.Length == 0 ? source.TechnicianKey : stop.TechnicianKey)))
        {
            ct.ThrowIfCancellationRequested(); var points = new List<(string Id, TourPoint Point)> { ("start", source.Start) };
            points.AddRange(group.Select(stop => (stop.Id, ScheduledTourService.PointFor(context, stop.PlaceKey))));
            if (source.End.Latitude.HasValue) points.Add(("end", source.End));
            if (points.Any(item => !item.Point.Latitude.HasValue || !item.Point.Longitude.HasValue || !item.Point.Valid)) throw new InvalidDataException("Renseignez les coordonnées du départ et de chaque étape avant le calcul routier.");
            for (var start = 0; start < points.Count - 1; start += 99)
            {
                var batch = points.Skip(start).Take(100).ToArray();
                // OSRM v5 route protocol: https://project-osrm.org/docs/v5.24.0/api/#route-service
                var coordinates = string.Join(';', batch.Select(item => item.Point.Longitude!.Value.ToString("R", CultureInfo.InvariantCulture) + "," + item.Point.Latitude!.Value.ToString("R", CultureInfo.InvariantCulture)));
                var uri = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/route/v1/" + settings.RoutingProfile + "/" + coordinates + "?overview=false&steps=false&alternatives=false");
                using var response = await GetAsync(uri, ct); var root = response.RootElement;
                if (!root.TryGetProperty("code", out var code) || code.GetString() != "Ok" || !root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() == 0)
                    throw new InvalidDataException("Le fournisseur routier n’a pas trouvé de route valide pour ces coordonnées.");
                var received = routes[0].GetProperty("legs"); if (received.ValueKind != JsonValueKind.Array || received.GetArrayLength() != batch.Length - 1) throw new InvalidDataException("Le nombre de segments routiers reçus ne correspond pas au parcours.");
                var stamp = DateTimeOffset.UtcNow;
                for (var index = 0; index < batch.Length - 1; index++)
                {
                    var distance = Number(received[index], "distance", 0, 50_000_000); var duration = Number(received[index], "duration", 0, 2_592_000);
                    if (!distance.HasValue || !duration.HasValue) throw new InvalidDataException("Une distance ou durée routière est absente ou invalide.");
                    legs.Add(new(group.Key.Day, group.Key.Technician, batch[index].Id, batch[index + 1].Id, ScheduledTourService.CoordinatesFingerprint(batch[index].Point, batch[index + 1].Point), distance.Value / 1000, duration.Value / 60, endpoint.GetLeftPart(UriPartial.Path), stamp));
                }
            }
        }
        ct.ThrowIfCancellationRequested(); copy.RoadLegs = legs; copy.Validate(); return copy;
    }
    public async Task<ScheduledTour> WeatherAsync(ScheduledTour source, TourPlanningContext context, TourServiceSettings settings, bool consent, CancellationToken ct = default)
    {
        if (!consent) throw new InvalidOperationException("Autorisez explicitement l’envoi des coordonnées et dates avant la météo terrain.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromMinutes(2)); ct = budget.Token;
        source.Validate(); settings.Validate(); var endpoint = Endpoint(settings.WeatherEndpoint); var copy = source.Copy(); var observations = new List<TourWeatherObservation>();
        var cache = new Dictionary<(double Latitude, double Longitude, string Day), (int? Code, double? Min, double? Max, double? Rain, DateTimeOffset At)>();
        foreach (var stop in source.Stops)
        {
            ct.ThrowIfCancellationRequested(); var point = ScheduledTourService.PointFor(context, stop.PlaceKey); var day = stop.Date.Length == 0 ? source.Date : stop.Date;
            if (!point.Latitude.HasValue || !point.Longitude.HasValue || !point.Valid) continue;
            var key = (point.Latitude.Value, point.Longitude.Value, day);
            if (!cache.TryGetValue(key, out var forecast))
            {
                // Open-Meteo compatible daily endpoint: https://open-meteo.com/en/docs
                Uri uri;
                var query = "?latitude=" + key.Item1.ToString("R", CultureInfo.InvariantCulture) + "&longitude=" + key.Item2.ToString("R", CultureInfo.InvariantCulture)
                    + "&start_date=" + day + "&end_date=" + day + "&timezone=auto&daily=weather_code,temperature_2m_min,temperature_2m_max,precipitation_probability_max";
                uri = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + query);
                using var response = await GetAsync(uri, ct); var daily = response.RootElement.GetProperty("daily");
                var times = daily.GetProperty("time"); if (times.ValueKind != JsonValueKind.Array || times.GetArrayLength() != 1 || times[0].GetString() != day) throw new InvalidDataException("La date des prévisions ne correspond pas à la visite.");
                double? Read(string name, double min, double max)
                {
                    if (!daily.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 1) return null;
                    return values[0].ValueKind == JsonValueKind.Number && values[0].TryGetDouble(out var value) && double.IsFinite(value) && value >= min && value <= max ? value : null;
                }
                var code = Read("weather_code", 0, 99); forecast = (code.HasValue && code.Value == Math.Truncate(code.Value) ? (int)code.Value : null, Read("temperature_2m_min", -100, 70), Read("temperature_2m_max", -100, 70), Read("precipitation_probability_max", 0, 100), DateTimeOffset.UtcNow);
                if (!forecast.Item1.HasValue && !forecast.Item2.HasValue && !forecast.Item3.HasValue && !forecast.Item4.HasValue) throw new InvalidDataException("Le fournisseur ne contient aucune prévision exploitable pour cette date.");
                cache.Add(key, forecast);
            }
            observations.Add(new(stop.Id, day, ScheduledTourService.PlaceName(context, stop), forecast.Code, forecast.Min, forecast.Max, forecast.Rain, endpoint.GetLeftPart(UriPartial.Path), forecast.At, ScheduledTourService.CoordinatesFingerprint(point, point)));
        }
        if (observations.Count == 0) throw new InvalidDataException("Aucune étape ne possède les coordonnées nécessaires à la météo.");
        ct.ThrowIfCancellationRequested(); copy.Weather = observations; copy.Validate(); return copy;
    }
    private async Task<JsonDocument> GetAsync(Uri uri, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(30)); ct = budget.Token;
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("Le fournisseur redirige vers une autre adresse ; vérifiez son adresse sans transmettre automatiquement les coordonnées.");
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("Le fournisseur terrain a refusé la demande (HTTP " + (int)response.StatusCode + ").");
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes) throw new InvalidDataException("La réponse du fournisseur terrain est trop volumineuse.");
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[16384];
        int read; while ((read = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("La réponse terrain dépasse 2 Mio."); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
        try { return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException error) { throw new InvalidDataException("Le fournisseur terrain a renvoyé un document invalide.", error); }
    }
    private static double? Number(JsonElement element, string name, double min, double max) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var value) && double.IsFinite(value) && value >= min && value <= max ? value : null;
    public void Dispose() => _client.Dispose();
}
