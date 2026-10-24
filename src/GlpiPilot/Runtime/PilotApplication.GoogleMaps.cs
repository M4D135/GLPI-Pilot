using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private string _googleMapsSessionKey = "";
    private sealed record GoogleMapPreview(string Token, string Title, GoogleMapsSegment Segment, long Revision,
        string ReferenceHash, bool ReferenceDraft, int? TourVersion, DateTimeOffset Created);
    private readonly Dictionary<string, GoogleMapPreview> _googleMapPreviews = new(StringComparer.Ordinal);
    private GoogleMapsSettingsStore GoogleMapsKeys() => new(DataPath("google-maps.local.json"));

    private void RegisterGoogleMapsOperations()
    {
        TerrainRegister("maps.settings.get", (a, ct) => GoogleMapsSettings());
        TerrainRegister("maps.settings.save", GoogleMapsSaveSettings);
        TerrainRegister("maps.sites", (a, ct) =>
        {
            var settings = EffectiveSettings.Cities; var term = Text(a, "search", 120); var (offset, limit) = TerrainPage(a);
            var sites = settings.Sites.Where(site => TerrainContains(site.Name, term) || TerrainContains(site.Address, term))
                .OrderBy(site => site.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            return new { total = sites.Length, offset, limit, rows = sites.Skip(offset).Take(limit).Select(site => new
                { site.Id, site.Name, location = GoogleMapsService.ForSite(site, settings.Cities.SingleOrDefault(city => city.Id == site.CityId)) }).ToArray() };
        });
        TerrainRegister("maps.preview", GoogleMapsPreview);
        Register("maps.open", GoogleMapsOpenAsync);
    }
    private object GoogleMapsSettings()
    {
        var configured = _googleMapsSessionKey.Length > 0; var remembered = false; var warning = "";
        try
        {
            remembered = GoogleMapsKeys().HasKey;
            if (remembered && !configured) configured = GoogleMapsKeys().ReadKey().Length > 0;
        }
        catch (Exception error) when (RecoverableMapKey(error))
        {
            warning = "La clé Google Maps enregistrée est illisible sur ce poste. Oubliez-la ou enregistrez une nouvelle clé. Les liens Google Maps sans clé restent disponibles.";
        }
        return new { configured, remembered, warning, canRemember = OperatingSystem.IsWindows() && !IsSimulationInstance, simulation = IsSimulationInstance,
            referrer = "https://" + GoogleMapsService.MapHost + "/*", documentation = GoogleMapsService.Documentation };
    }
    private static bool RecoverableMapKey(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or FormatException or PlatformNotSupportedException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException;
    private object GoogleMapsSaveSettings(JsonElement args, CancellationToken ct)
    {
        var key = Text(args, "key", GoogleMapsService.MaximumKeyLength); var forget = Flag(args, "forget");
        ct.ThrowIfCancellationRequested();
        if (forget) { GoogleMapsKeys().Forget(); _googleMapsSessionKey = ""; }
        else if (key.Length > 0)
        {
            GoogleMapsService.ValidateKey(key);
            if (Flag(args, "remember"))
            {
                if (IsSimulationInstance) throw new InvalidDataException("La clé d’essai reste seulement dans cette session de simulation.");
                GoogleMapsKeys().SaveKey(key);
            }
            _googleMapsSessionKey = key;
        }
        return GoogleMapsSettings();
    }
    private object GoogleMapsPreview(JsonElement args, CancellationToken ct)
    {
        var kind = Text(args, "kind", 30, "site"); var draftReference = Flag(args, "referenceDraft");
        var reference = draftReference ? TerrainDraft() : EffectiveSettings.Cities;
        if (draftReference) TerrainCheckDraft(args);
        var routes = new List<(string Title, IReadOnlyList<GoogleMapsPlace> Places)>(); var notes = new List<string>(); int? tourVersion = null;
        GoogleMapsPlace Resolve(string place)
        {
            if (place.StartsWith("site:", StringComparison.Ordinal))
            {
                var site = reference.Sites.SingleOrDefault(site => site.Id == place[5..]) ?? throw new InvalidDataException("Le site n’existe plus dans ce référentiel. Actualisez la page.");
                return GoogleMapsService.ForSite(site, reference.Cities.SingleOrDefault(city => city.Id == site.CityId));
            }
            if (place.StartsWith("city:", StringComparison.Ordinal))
                return GoogleMapsService.ForCity(reference.Cities.SingleOrDefault(city => city.Id == place[5..]) ?? throw new InvalidDataException("La commune n’existe plus dans ce référentiel."));
            throw new InvalidDataException("Cette étape n’a pas de lieu reconnu. Corrigez sa localisation avant de demander une carte ; aucune étape ne sera ignorée.");
        }
        if (kind is "site" or "city")
        {
            var id = Text(args, "id", 50); if (!id.StartsWith(kind + ":", StringComparison.Ordinal)) id = kind + ":" + id;
            var place = Resolve(id); routes.Add((place.Label, new[] { place }));
        }
        else if (kind == "projection")
        {
            CheckRevision(args);
            if (_terrainProjection is not { } plan || _terrainProjectionRevision != Revision) throw new InvalidOperationException("Actualisez le regroupement des villes avant d’ouvrir sa carte.");
            routes.Add(("Ordre des communes", plan.Stops.Select(stop => Resolve("city:" + stop.CityId)).ToArray()));
            notes.Add("Ce parcours suit l’ordre affiché des communes. Il ne représente pas les adresses de chaque site.");
        }
        else if (kind == "tour")
        {
            var tour = TerrainCheckedTour(args); tourVersion = _terrainTourDraftVersion;
            var groups = tour.Stops.GroupBy(stop => (Day: stop.Date.Length > 0 ? stop.Date : tour.Date,
                Technician: stop.TechnicianKey.Length > 0 ? stop.TechnicianKey : tour.TechnicianKey)).ToArray();
            var number = 0;
            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested(); var places = new List<GoogleMapsPlace>();
                if (GoogleMapsService.ForPoint(tour.Start, "Départ") is { } start) places.Add(start);
                else notes.Add("Départ non renseigné : le premier lieu de visite est le départ du parcours.");
                places.AddRange(group.Select(stop => Resolve(stop.PlaceKey)));
                if (GoogleMapsService.ForPoint(tour.End, "Retour") is { } end) places.Add(end);
                routes.Add((groups.Length == 1 ? "Tournée · " + group.Key.Day : "Parcours " + (++number) + " · " + group.Key.Day, places));
            }
            notes.Add("L’ordre des visites est conservé, séparément pour chaque journée et affectation. Google Maps ne modifie pas le planning ni ses durées.");
        }
        else throw new InvalidDataException("Carte attendue : site, commune, ordre des communes ou tournée.");
        if (routes.Count == 0) throw new InvalidDataException("Aucun lieu à afficher dans ce parcours.");
        var result = new List<object>(); var fingerprint = TerrainDigest(reference);
        // Replace stale previews without keeping location data indefinitely.
        foreach (var value in _googleMapPreviews.Values.Where(value => value.Created < DateTimeOffset.UtcNow.AddMinutes(-10)).ToArray()) _googleMapPreviews.Remove(value.Token);
        if (_googleMapPreviews.Count > 2000) _googleMapPreviews.Clear();
        foreach (var route in routes)
        {
            var segments = GoogleMapsService.BuildSegments(route.Places);
            if (segments.Count > 1) notes.Add("Parcours découpé en " + segments.Count + " parties pour conserver tous les lieux dans les limites de Google Maps. La fin d’une partie est le départ de la suivante.");
            foreach (var segment in segments)
            {
                var token = Guid.NewGuid().ToString("N"); var title = route.Title + (segments.Count > 1 ? " · partie " + segment.Number + "/" + segments.Count : "");
                _googleMapPreviews[token] = new(token, title, segment, Revision, fingerprint, draftReference, tourVersion, DateTimeOffset.UtcNow);
                result.Add(new { token, title, segment.From, segment.Until, places = segment.Places, externalUrl = segment.Url });
            }
        }
        return new { segments = result, notes = notes.Distinct().ToArray(), settings = GoogleMapsSettings(), simulation = IsSimulationInstance,
            notice = "Google Maps nécessite Internet. Aucune connexion n’est lancée avant votre clic. Seuls les lieux ou coordonnées affichés seront transmis, jamais les tickets ni les agents." };
    }
    private async Task<object?> GoogleMapsOpenAsync(JsonElement args, CancellationToken ct)
    {
        if (!Flag(args, "consent")) throw new InvalidOperationException("Utilisez le bouton d’ouverture pour autoriser l’affichage de ces lieux dans Google Maps.");
        if (!_googleMapPreviews.TryGetValue(Text(args, "token", 40), out var preview) || preview.Created < DateTimeOffset.UtcNow.AddMinutes(-10))
            throw new InvalidOperationException("L’aperçu cartographique a expiré. Rouvrez la carte depuis les sites ou la tournée.");
        var reference = preview.ReferenceDraft ? TerrainDraft() : EffectiveSettings.Cities;
        if (preview.Revision != Revision || preview.ReferenceHash != TerrainDigest(reference)
            || preview.TourVersion.HasValue && preview.TourVersion != _terrainTourDraftVersion)
            throw new InvalidOperationException("Les lieux ou le parcours ont changé. Rouvrez l’aperçu avant de transmettre les nouvelles positions.");
        ct.ThrowIfCancellationRequested();
        if (Flag(args, "embedded"))
        {
            string key;
            try { key = _googleMapsSessionKey.Length > 0 ? _googleMapsSessionKey : GoogleMapsKeys().ReadKey(); }
            catch (Exception error) when (RecoverableMapKey(error)) { throw new InvalidOperationException("La clé locale est illisible. Utilisez le lien Google Maps sans clé, ou remplacez la clé dans les réglages."); }
            if (key.Length == 0) throw new InvalidOperationException("La carte intégrée nécessite votre clé Maps Embed API. Le bouton Ouvrir Google Maps fonctionne sans clé.");
            var url = GoogleMapsService.Embed(preview.Segment.Places, key);
            if (!GoogleMapsService.IsEmbedUrl(url)) throw new InvalidDataException("URL de carte intégrée invalide.");
            var theme = Text(args, "theme", 10, Settings.Theme); if (theme is not ("dark" or "light")) theme = Settings.Theme;
            await Host.ShowGoogleMapAsync(new(preview.Title, url, preview.Segment.Url, theme, Settings.Interface.Accent, IsSimulationInstance));
        }
        else await Host.OpenGoogleMapsAsync(preview.Segment.Url);
        return new { opened = true, embedded = Flag(args, "embedded"), external = true, simulation = IsSimulationInstance };
    }
}
