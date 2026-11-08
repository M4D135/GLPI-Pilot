using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record MapPreview(string Token, long Revision, string ReferenceHash, bool ReferenceDraft,
        int? ReferenceVersion, int? TourVersion, DateTimeOffset Created);
    private readonly Dictionary<string, MapPreview> _mapPreviews = new(StringComparer.Ordinal);
    // Immutable and published atomically: the native resource gate runs outside the command queue.
    // The 30-minute expiry applies before activation. An open map stays usable until its
    // dialog closes or its session/reference changes; no surprise timeout while panning.
    private MapPreview? _mapTileGrant;

    private void RegisterMapsOperations()
    {
        TerrainRegister("maps.preview", MapPreviewData);
        TerrainRegister("maps.tiles", MapTiles);
        RegisterMapWorkspaceOperations();
    }

    private object MapPreviewData(JsonElement args, CancellationToken ct)
    {
        var kind = Text(args, "kind", 30, "site"); var draftReference = Flag(args, "referenceDraft");
        if (draftReference) TerrainCheckDraft(args);
        var reference = draftReference ? TerrainDraft() : EffectiveSettings.Cities;
        var routes = new List<EmbeddedMapRoute>(); var notes = new List<string>(); int? tourVersion = null;
        EmbeddedMapPlace Resolve(string key)
        {
            if (key.StartsWith("site:", StringComparison.Ordinal))
            {
                var site = reference.Sites.SingleOrDefault(site => site.Id == key[5..])
                    ?? throw new InvalidDataException("Le site n’existe plus dans ce référentiel. Actualisez la page.");
                return EmbeddedMapsService.ForSite(site, reference.Cities.SingleOrDefault(city => city.Id == site.CityId));
            }
            if (key.StartsWith("city:", StringComparison.Ordinal))
                return EmbeddedMapsService.ForCity(reference.Cities.SingleOrDefault(city => city.Id == key[5..])
                    ?? throw new InvalidDataException("La commune n’existe plus dans ce référentiel. Actualisez la page."));
            throw new InvalidDataException("Une étape n’a pas de lieu reconnu. Corrigez sa localisation dans le planning.");
        }
        void Add(string title, bool ordered, IEnumerable<EmbeddedMapPlace> places)
            => routes.Add(new("route-" + (routes.Count + 1), title, ordered,
                places.Select((place, index) => place with { Sequence = index + 1 }).ToArray()));
        if (kind is "site" or "city")
        {
            var id = Text(args, "id", 50); if (!id.StartsWith(kind + ":", StringComparison.Ordinal)) id = kind + ":" + id;
            var place = Resolve(id); Add(place.Label, false, new[] { place });
        }
        else if (kind == "sites")
        {
            HashSet<string>? ids = null;
            if (args.TryGetProperty("ids", out var input))
            {
                if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() > CitySettings.MaximumSites)
                    throw new InvalidDataException("La sélection de sites est invalide ou trop volumineuse.");
                ids = new(StringComparer.Ordinal);
                foreach (var value in input.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 50 } id)
                        throw new InvalidDataException("Un identifiant de site est invalide.");
                    ids.Add(id.StartsWith("site:", StringComparison.Ordinal) ? id[5..] : id);
                }
                if (ids.Except(reference.Sites.Select(site => site.Id), StringComparer.Ordinal).Any())
                    throw new InvalidDataException("Un site sélectionné n’existe plus. Actualisez la liste.");
            }
            Add(ids is null ? "Tous les sites" : "Sites sélectionnés", false, reference.Sites
                .Where(site => ids is null || ids.Contains(site.Id)).OrderBy(site => site.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(site => EmbeddedMapsService.ForSite(site, reference.Cities.SingleOrDefault(city => city.Id == site.CityId))));
        }
        else if (kind == "projection")
        {
            CheckRevision(args);
            if (_terrainProjection is not { } plan || _terrainProjectionRevision != Revision)
                throw new InvalidOperationException("Actualisez le regroupement des villes avant d’afficher sa carte.");
            Add("Ordre des communes", true, plan.Stops.Select(stop => Resolve("city:" + stop.CityId)));
            notes.Add("Les repères correspondent aux communes, pas aux adresses des sites.");
        }
        else if (kind == "tour")
        {
            var tour = TerrainCheckedTour(args); tourVersion = _terrainTourDraftVersion;
            var groups = tour.Stops.GroupBy(stop => (Day: stop.Date.Length > 0 ? stop.Date : tour.Date,
                Technician: stop.TechnicianKey.Length > 0 ? stop.TechnicianKey : tour.TechnicianKey)).ToArray();
            var number = 0;
            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested(); var places = new List<EmbeddedMapPlace>();
                if (EmbeddedMapsService.ForPoint(tour.Start, "start", "Départ") is { } start) places.Add(start);
                places.AddRange(group.Select(stop => Resolve(stop.PlaceKey)));
                if (EmbeddedMapsService.ForPoint(tour.End, "end", "Retour") is { } end) places.Add(end);
                Add((groups.Length == 1 ? "Tournée" : "Parcours " + (++number)) + " · " + group.Key.Day, true, places);
            }
            notes.Add("Chaque journée et affectation garde son propre parcours et l’ordre prévu des visites.");
        }
        else throw new InvalidDataException("Carte attendue : site, sites, commune, ordre des communes ou tournée.");
        if (routes.Count == 0) Add("Tournée sans étape", true, Array.Empty<EmbeddedMapPlace>());
        if (routes.Any(route => route.Ordered)) notes.Add("Les pointillés indiquent l’ordre des visites à vol d’oiseau, pas un trajet routier. Les durées du planning restent inchangées.");
        ct.ThrowIfCancellationRequested();
        var token = MapCreatePreview(reference, draftReference, tourVersion);
        return new { token, title = routes.Count == 1 ? routes[0].Title : "Carte des tournées", routes, notes,
            simulation = IsSimulationInstance,
            notice = "Les repères et étapes sont consultables hors ligne. Le fond de rues OpenStreetMap utilise Internet, sans clé ni compte." };
    }

    private string MapCreatePreview(CitySettings reference, bool draftReference = false, int? tourVersion = null)
    {
        foreach (var value in _mapPreviews.Values.Where(value => value.Created < DateTimeOffset.UtcNow.AddMinutes(-30)).ToArray())
            _mapPreviews.Remove(value.Token);
        if (_mapPreviews.Count >= 50) _mapPreviews.Remove(_mapPreviews.Values.MinBy(value => value.Created)!.Token);
        var token = Guid.NewGuid().ToString("N");
        _mapPreviews[token] = new(token, Revision, TerrainDigest(reference), draftReference,
            draftReference ? _terrainCityDraftVersion : null, tourVersion, DateTimeOffset.UtcNow);
        return token;
    }

    private object MapTiles(JsonElement args, CancellationToken ct)
    {
        var token = Text(args, "token", 40); var enabled = Flag(args, "enabled");
        if (!enabled)
        {
            var current = Volatile.Read(ref _mapTileGrant);
            if (current?.Token == token) Interlocked.CompareExchange(ref _mapTileGrant, null, current);
            return new { enabled = false };
        }
        if (!_mapPreviews.TryGetValue(token, out var preview) || preview.Created < DateTimeOffset.UtcNow.AddMinutes(-30))
            throw new InvalidOperationException("La carte a expiré. Fermez-la puis rouvrez-la pour charger les rues.");
        var reference = preview.ReferenceDraft ? TerrainDraft() : EffectiveSettings.Cities;
        if (preview.Revision != Revision || preview.ReferenceHash != TerrainDigest(reference)
            || preview.ReferenceVersion.HasValue && preview.ReferenceVersion != _terrainCityDraftVersion
            || preview.TourVersion.HasValue && preview.TourVersion != _terrainTourDraftVersion)
            throw new InvalidOperationException("Les lieux ou le parcours ont changé. Rouvrez la carte pour afficher les positions actualisées.");
        ct.ThrowIfCancellationRequested(); Volatile.Write(ref _mapTileGrant, preview);
        return new { enabled = true, tileUrl = EmbeddedMapsService.TileTemplate, maxZoom = EmbeddedMapsService.MaximumZoom };
    }

    /// <summary>Called only for native image requests. Active simulation and ordinary session never share grants.</summary>
    public bool AllowsMapTileRequest(string url)
    {
        if (_disposed || !EmbeddedMapsService.IsTileUrl(url)) return false;
        if (_simulation is { } simulation) return simulation.AllowsMapTileRequest(url);
        var grant = Volatile.Read(ref _mapTileGrant);
        return grant is not null && grant.Revision == Interlocked.Read(ref _revision)
            && (!grant.ReferenceVersion.HasValue || grant.ReferenceVersion == Volatile.Read(ref _terrainCityDraftVersion))
            && (!grant.TourVersion.HasValue || grant.TourVersion == Volatile.Read(ref _terrainTourDraftVersion));
    }
}
