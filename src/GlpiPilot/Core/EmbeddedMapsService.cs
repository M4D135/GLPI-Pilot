using System.Globalization;

namespace GlpiPilot.Core;

/// <summary>Local map data: reference locations only, never tickets, agents or planning notes.</summary>
public sealed record EmbeddedMapPlace(string Id, string Label, string Address, string Precision,
    double? Latitude, double? Longitude, int Sequence = 0);
public sealed record EmbeddedMapRoute(string Id, string Title, bool Ordered, IReadOnlyList<EmbeddedMapPlace> Places);

public static class EmbeddedMapsService
{
    public const string TileTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    public const int MaximumZoom = 19;

    public static EmbeddedMapPlace ForSite(SiteDefinition site, CityDefinition? city)
    {
        var address = site.Address.Length > 0 ? site.Address : city is null ? "" : string.Join(' ',
            new[] { city.PostalCode, city.Name }.Where(value => value.Length > 0));
        // An address is not a geocode, and the centre of a city is not a site location.
        return Point("site:" + site.Id, site.Name, address, "Coordonnées du site", site.Latitude, site.Longitude);
    }

    public static EmbeddedMapPlace ForCity(CityDefinition city)
        => Point("city:" + city.Id, city.Name, string.Join(' ', new[] { city.PostalCode, city.Name }.Where(value => value.Length > 0)),
            "Repère de commune · pas une adresse de site", city.Latitude, city.Longitude);

    public static EmbeddedMapPlace? ForPoint(TourPoint point, string id, string fallback)
        => string.IsNullOrWhiteSpace(point.Label) && !point.Latitude.HasValue && !point.Longitude.HasValue ? null
            : Point(id, point.Label.Length > 0 ? point.Label : fallback, point.Label, "Coordonnées déclarées", point.Latitude, point.Longitude);

    private static EmbeddedMapPlace Point(string id, string label, string address, string precision, double? latitude, double? longitude)
        => ValidCoordinates(latitude, longitude)
            ? new(id, label, address, precision, latitude, longitude)
            : new(id, label, address, "Coordonnées manquantes · à compléter dans le référentiel", null, null);

    public static bool ValidCoordinates(double? latitude, double? longitude)
        => latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180
            && double.IsFinite(latitude.Value) && double.IsFinite(longitude.Value);

    /// <summary>Only standard image tiles; no URLs carrying address text, tokens, API keys or other query data.</summary>
    public static bool IsTileUrl(string value)
    {
        if (value.Length > 150 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "tile.openstreetmap.org" || !uri.IsDefaultPort || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        var path = uri.AbsolutePath.Split('/');
        if (path.Length != 4 || path[0].Length != 0 || !path[3].EndsWith(".png", StringComparison.Ordinal)) return false;
        static bool Integer(string text, out int number)
            => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
                && text == number.ToString(CultureInfo.InvariantCulture);
        return Integer(path[1], out var zoom) && zoom is >= 0 and <= MaximumZoom
            && Integer(path[2], out var x) && Integer(path[3][..^4], out var y)
            && x >= 0 && y >= 0 && x < (1 << zoom) && y < (1 << zoom);
    }
}
