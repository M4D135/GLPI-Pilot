using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private object SimulationTourProvider(JsonElement args, bool routing, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); var context = TerrainContext(ct); var stamp = DateTimeOffset.Now;
        const string provider = "Simulation locale · valeurs d’exercice, aucun fournisseur contacté";
        if (routing)
        {
            var legs = new List<TourRoadLeg>();
            foreach (var group in draft.Stops.GroupBy(stop => (Day: stop.Date.Length == 0 ? draft.Date : stop.Date,
                Technician: stop.TechnicianKey.Length == 0 ? draft.TechnicianKey : stop.TechnicianKey)))
            {
                var points = new List<(string Id, TourPoint Point)> { ("start", draft.Start) };
                points.AddRange(group.Select(stop => (stop.Id, ScheduledTourService.PointFor(context, stop.PlaceKey))));
                if (draft.End.Latitude.HasValue) points.Add(("end", draft.End));
                for (var index = 1; index < points.Count; index++)
                {
                    ct.ThrowIfCancellationRequested(); var from = points[index - 1]; var to = points[index];
                    if (!from.Point.Latitude.HasValue || !from.Point.Longitude.HasValue || !to.Point.Latitude.HasValue || !to.Point.Longitude.HasValue) continue;
                    // A declared exercise formula, not a claimed road-service result.
                    static double Radians(double value) => value * Math.PI / 180;
                    var latitude = Radians(to.Point.Latitude.Value - from.Point.Latitude.Value);
                    var longitude = Radians(to.Point.Longitude.Value - from.Point.Longitude.Value);
                    var a = Math.Pow(Math.Sin(latitude / 2), 2) + Math.Cos(Radians(from.Point.Latitude.Value)) * Math.Cos(Radians(to.Point.Latitude.Value)) * Math.Pow(Math.Sin(longitude / 2), 2);
                    var km = Math.Round(6371 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(a))) * 1.25, 2);
                    legs.Add(new(group.Key.Day, group.Key.Technician, from.Id, to.Id, ScheduledTourService.CoordinatesFingerprint(from.Point, to.Point),
                        km, Math.Round(km / 40 * 60 + 5, 1), provider, stamp));
                }
            }
            draft.RoadLegs = legs;
        }
        else
        {
            draft.Weather = draft.Stops.Select((stop, index) =>
            {
                ct.ThrowIfCancellationRequested(); var point = ScheduledTourService.PointFor(context, stop.PlaceKey);
                return new TourWeatherObservation(stop.Id, stop.Date.Length == 0 ? draft.Date : stop.Date,
                    ScheduledTourService.PlaceName(context, stop), index % 3 == 0 ? 3 : 1, 12 + index % 4, 19 + index % 4, 15 + index % 4 * 10,
                    provider, stamp, ScheduledTourService.CoordinatesFingerprint(point, point));
            }).ToList();
        }
        ct.ThrowIfCancellationRequested();
        return new
        {
            state = TerrainSetTour(draft, edit: routing), segments = draft.RoadLegs.Count, forecasts = draft.Weather.Count, provider, simulated = true,
            message = routing ? "Trajets d’exercice : distance à vol d’oiseau × 1,25, vitesse 40 km/h et 5 minutes fixes. Les segments sans coordonnées restent sans mesure."
                : "Prévisions fictives créées pour essayer le planning ; elles ne représentent pas la météo réelle."
        };
    }

    private AppSettings SimulationDemoSettings()
    {
        var settings = Settings.Copy(); settings.Dashboard.ColumnMappings.Clear(); settings.Dashboard.ValueDictionaries.Clear();
        settings.Dashboard.PersonalTechnician = "Alex Exemple";
        settings.Cities = new() { Enabled = true, TitleColumn = "Titre", LocationColumn = "Lieu" };
        for (var index = 0; index < 4; index++)
        {
            var label = ((char)('A' + index)).ToString();
            var city = new CityDefinition { Name = "Ville exemple " + label, Latitude = 44 + index * .08, Longitude = 5 + index * .06, TerritoryPath = "Territoire fictif / Secteur " + label };
            settings.Cities.Cities.Add(city);
            settings.Cities.Sites.Add(new() { CityId = city.Id, Name = "Site exemple " + label, Latitude = city.Latitude, Longitude = city.Longitude,
                Address = "Adresse fictive de démonstration", Contact = "Contact exemple", Equipment = "Postes et imprimantes de démonstration" });
        }
        settings.Dashboard.Business = new()
        {
            Notes = "Paramètres fictifs de simulation. Les seuils ne constituent pas des objectifs métier.",
            WeatherRules = new()
            {
                new() { Id = "simulation-active", Label = "Stock actif fictif", SourceKey = "active", OrangeAbove = 80, RedAbove = 130 },
                new() { Id = "simulation-support", Label = "Équipe exemple Support", Kind = "team", SourceKey = "group", Values = new() { "Groupe Support exemple" }, OrangeAbove = 25, RedAbove = 45 },
                new() { Id = "simulation-expertise", Label = "Équipe exemple Expertise", Kind = "team", SourceKey = "group", Values = new() { "Groupe Expertise exemple" }, OrangeAbove = 20, RedAbove = 40 }
            }
        };
        return settings;
    }
}
