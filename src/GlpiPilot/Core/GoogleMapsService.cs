using System.Globalization;
using System.IO;

namespace GlpiPilot.Core;

/// <summary>Only reference locations enter these URLs; no ticket, person or planning notes.</summary>
public sealed record GoogleMapsPlace(string Label, string Query, string Precision);
public sealed record GoogleMapsSegment(int Number, int From, int Until, IReadOnlyList<GoogleMapsPlace> Places, string Url);
public sealed record GoogleMapFrame(string Title, string EmbedUrl, string ExternalUrl, string Theme, string Accent, bool Simulation);

public static class GoogleMapsService
{
    public const int MaximumUrlLength = 2048;
    public const int MaximumWaypoints = 9;
    public const int MaximumKeyLength = 200;
    public const string MapHost = "glpi-pilot-map.local";
    public const string EmbedOrigin = "https://www.google.com/maps/embed/v1/";
    public const string Documentation = "https://developers.google.com/maps/documentation/embed/quickstart";
    private static readonly string[] MapResourceHosts = ["www.google.com", "maps.googleapis.com", "maps.gstatic.com", "www.gstatic.com", "fonts.googleapis.com", "fonts.gstatic.com", "lh3.googleusercontent.com"];

    public static GoogleMapsPlace ForSite(SiteDefinition site, CityDefinition? city)
    {
        // A city centroid is never silently substituted for an unlocated site.
        if (!string.IsNullOrWhiteSpace(site.Address)) return Place(site.Name, site.Address, "Adresse du site");
        if (Coordinates(site.Latitude, site.Longitude) is { } coordinates) return Place(site.Name, coordinates, "Coordonnées du site");
        var locality = city is null ? "" : string.Join(' ', new[] { city.PostalCode, city.Name }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return Place(site.Name, string.Join(", ", new[] { site.Name, locality }.Where(value => value.Length > 0)), "Recherche par nom · position à confirmer");
    }

    public static GoogleMapsPlace ForCity(CityDefinition city)
        => Coordinates(city.Latitude, city.Longitude) is { } coordinates
            ? Place(city.Name, coordinates, "Coordonnées de la commune · pas une adresse de site")
            : Place(city.Name, string.Join(' ', new[] { city.PostalCode, city.Name }.Where(value => !string.IsNullOrWhiteSpace(value))), "Recherche de commune · pas une adresse de site");

    public static GoogleMapsPlace? ForPoint(TourPoint point, string fallback)
    {
        if (Coordinates(point.Latitude, point.Longitude) is { } coordinates) return Place(point.Label.Length > 0 ? point.Label : fallback, coordinates, "Coordonnées déclarées");
        return string.IsNullOrWhiteSpace(point.Label) ? null : Place(point.Label, point.Label, "Lieu déclaré · position à confirmer");
    }

    public static GoogleMapsPlace Place(string label, string query, string precision)
    {
        query = query.Trim();
        if (query.Length is < 1 or > 2000 || query.Any(char.IsControl)) throw new InvalidDataException("Le lieu à afficher est vide, trop long ou contient des caractères de contrôle. Corrigez son adresse dans le référentiel.");
        if (query.Contains('|')) throw new InvalidDataException("Le lieu contient le séparateur d’étapes | de Google Maps. Renseignez une adresse unique sans ce caractère, ou les coordonnées du lieu ; aucune étape ne sera modifiée silencieusement.");
        return new(label.Trim(), query, precision);
    }

    public static IReadOnlyList<GoogleMapsSegment> BuildSegments(IReadOnlyList<GoogleMapsPlace> places)
    {
        if (places.Count is < 1 or > 2002) throw new InvalidDataException("Choisissez entre un et deux mille lieux pour la carte.");
        foreach (var place in places) _ = Place(place.Label, place.Query, place.Precision);
        if (places.Count == 1)
        {
            var url = Search(places[0]);
            if (url.Length > MaximumUrlLength || Embed([places[0]], new string('x', MaximumKeyLength)).Length > MaximumUrlLength)
                throw new InvalidDataException("L’adresse est trop longue pour Google Maps. Renseignez une adresse plus précise ou des coordonnées, sans supprimer une étape.");
            return [new(1, 1, 1, places.ToArray(), url)];
        }
        var result = new List<GoogleMapsSegment>(); var start = 0;
        while (start < places.Count - 1)
        {
            var end = Math.Min(places.Count - 1, start + MaximumWaypoints + 1);
            GoogleMapsPlace[] batch; string url;
            while (true)
            {
                batch = places.Skip(start).Take(end - start + 1).ToArray(); url = Directions(batch);
                if (url.Length <= MaximumUrlLength && Embed(batch, new string('x', MaximumKeyLength)).Length <= MaximumUrlLength) break;
                if (--end <= start) throw new InvalidDataException("Deux adresses successives dépassent la longueur admise par Google Maps. Précisez leurs coordonnées ; aucune étape n’a été retirée.");
            }
            result.Add(new(result.Count + 1, start + 1, end + 1, batch, url));
            start = end; // The previous destination is the next origin: every leg survives the split.
        }
        return result;
    }

    public static string Search(GoogleMapsPlace place) => "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(place.Query);
    public static string Directions(IReadOnlyList<GoogleMapsPlace> places)
    {
        if (places.Count is < 2 or > MaximumWaypoints + 2) throw new InvalidDataException("Un lien routier accepte de deux à onze lieux.");
        return "https://www.google.com/maps/dir/?api=1&origin=" + Uri.EscapeDataString(places[0].Query)
            + "&destination=" + Uri.EscapeDataString(places[^1].Query) + "&travelmode=driving" + Waypoints(places);
    }
    public static string Embed(IReadOnlyList<GoogleMapsPlace> places, string key)
    {
        ValidateKey(key);
        if (places.Count is < 1 or > MaximumWaypoints + 2) throw new InvalidDataException("Segment cartographique invalide.");
        var prefix = EmbedOrigin + (places.Count == 1 ? "place" : "directions") + "?key=" + Uri.EscapeDataString(key);
        return places.Count == 1 ? prefix + "&q=" + Uri.EscapeDataString(places[0].Query)
            : prefix + "&origin=" + Uri.EscapeDataString(places[0].Query) + "&destination=" + Uri.EscapeDataString(places[^1].Query) + "&mode=driving" + Waypoints(places);
    }
    public static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumKeyLength || key.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_')))
            throw new InvalidDataException("Renseignez une clé Maps Embed API valide (lettres, chiffres, tirets et soulignés). Aucune clé n’est fournie par GLPI Pilot.");
    }
    private static string Waypoints(IReadOnlyList<GoogleMapsPlace> places)
        => places.Count <= 2 ? "" : "&waypoints=" + Uri.EscapeDataString(string.Join('|', places.Skip(1).SkipLast(1).Select(place => place.Query)));
    private static string? Coordinates(double? latitude, double? longitude)
        => latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 && double.IsFinite(latitude.Value) && double.IsFinite(longitude.Value)
            ? latitude.Value.ToString("R", CultureInfo.InvariantCulture) + "," + longitude.Value.ToString("R", CultureInfo.InvariantCulture) : null;

    public static bool IsExternalMapUrl(string value) => GoogleUri(value, out var uri) && value.Length <= MaximumUrlLength
        && uri.AbsolutePath is "/maps/search/" or "/maps/dir/" && Query(uri, "api") == "1";
    public static bool IsEmbedUrl(string value) => GoogleUri(value, out var uri) && value.Length <= MaximumUrlLength
        && uri.AbsolutePath is "/maps/embed/v1/place" or "/maps/embed/v1/directions" && Query(uri, "key").Length > 0;
    public static bool IsMapResource(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && MapResourceHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    private static bool GoogleUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme == "https" && parsed.Host == "www.google.com"
            && parsed.IsDefaultPort && parsed.UserInfo.Length == 0 && parsed.Fragment.Length == 0) { uri = parsed; return true; }
        uri = null!; return false;
    }
    private static string Query(Uri uri, string key)
    {
        var values = uri.Query.TrimStart('?').Split('&').Select(value => value.Split('=', 2))
            .Where(value => value.Length == 2 && value[0] == key).ToArray();
        return values.Length == 1 ? Uri.UnescapeDataString(values[0][1]) : "";
    }
}
