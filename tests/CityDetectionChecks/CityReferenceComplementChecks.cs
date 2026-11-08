using System.Text.Json;
using GlpiPilot.Core;

internal static class CityReferenceComplementChecks
{
    private static void Check(bool condition, string message = "Reference complement assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Expected InvalidDataException"); }
    private static ExtractionResult Csv(string[] headers, params string[][] rows) => new() { Headers = headers.ToList(), Rows = rows.ToList() };
    private static (CitySettings Settings, CityDefinition City, SiteDefinition Site) Fixture()
    {
        var city = new CityDefinition { Name = "Marseille", Aliases = ["MA"], PostalCode = "13001", Region = "Région personnalisée" };
        var site = new SiteDefinition { Name = "Agence", CityId = city.Id, Aliases = ["Agence perso"], Address = "1 rue personnalisée",
            Latitude = 43.3, Longitude = 5.4, Contact = "Contact privé", Equipment = "Matériel local", ClosedDates = ["2026-12-25"] };
        return (new CitySettings { Cities = [city], Sites = [site] }, city, site);
    }
    public static void Register(Action<string, Action> test)
    {
        test("Completing a reference adds aliases without erasing customized values or changing identities", () =>
        {
            var (settings, city, site) = Fixture(); var before = JsonSerializer.Serialize(settings);
            var csv = Csv(["Type", "VilleId", "Nom", "Alias", "CodePostal", "Adresse", "Latitude", "Longitude", "Contact", "Materiel", "Fermetures", "Fusion"],
                ["Ville", "", "MARSEILLE", "ma | MARS", "13999", "", "43.2", "5.2", "", "", "", "Compléter"],
                ["Site", "Marseille", "Agence", "agence PERSO | Nouveau", "", "2 rue du catalogue", "44", "6", "", "", "", "Completer"]);
            var preview = CityReferenceService.Preview(csv, settings); var c = preview.Settings.Cities.Single(); var s = preview.Settings.Sites.Single();
            Check(JsonSerializer.Serialize(settings) == before && c.Id == city.Id && s.Id == site.Id && s.CityId == city.Id);
            Check(c.Name == city.Name && c.PostalCode == "13001" && c.Region == city.Region && c.Aliases.SequenceEqual(new[] { "MA", "MARS" }));
            Check(s.Address == site.Address && s.Latitude == site.Latitude && s.Longitude == site.Longitude && s.Contact == site.Contact
                && s.Equipment == site.Equipment && s.ClosedDates.SequenceEqual(site.ClosedDates) && s.Aliases.SequenceEqual(new[] { "Agence perso", "Nouveau" }));
            Check(preview.Changes.All(change => change.Detail.Contains("conservées")));
        });
        test("A complete-only catalogue initializes an empty profile and repeated imports are idempotent", () =>
        {
            var csv = Csv(["Type", "VilleId", "Nom", "Alias", "CodePostal", "Adresse", "Latitude", "Longitude", "Fusion"],
                ["Site", "Lyon", "Atelier", "ATL", "", "3 rue de test", "45.75", "4.85", "Completer"],
                ["Ville", "", "Lyon", "LY", "69001", "", "45.76", "4.84", "Completer"]);
            var first = CityReferenceService.Preview(csv, new()).Settings;
            Check(first.Cities.Count == 1 && first.Sites.Count == 1 && first.Sites[0].CityId == first.Cities[0].Id);
            Check(first.Sites[0].Latitude == 45.75 && first.Cities[0].Latitude == 45.76);
            var second = CityReferenceService.Preview(csv, first);
            Check(second.Changes.All(change => change.Kind == "Identique") && JsonSerializer.Serialize(second.Settings) == JsonSerializer.Serialize(first));
        });
        test("Complete-only coordinates are atomic and cannot relocate a customized address", () =>
        {
            var (settings, _, site) = Fixture(); site.Latitude = site.Longitude = null;
            var csv = Csv(["Type", "Id", "Nom", "Adresse", "Latitude", "Longitude", "Fusion"],
                ["Site", site.Id, "Agence", "Autre adresse", "43.2", "5.2", "Completer"]);
            var kept = CityReferenceService.Preview(csv, settings).Settings.Sites.Single();
            Check(kept.Address == site.Address && kept.Latitude is null && kept.Longitude is null);
            csv.Rows[0][3] = site.Address;
            var filled = CityReferenceService.Preview(csv, settings).Settings.Sites.Single();
            Check(filled.Latitude == 43.2 && filled.Longitude == 5.2);
            csv.Rows[0][5] = ""; Reject(() => CityReferenceService.Preview(csv, settings));
            Check(site.Latitude is null && site.Longitude is null);
            site.Address = ""; site.Latitude = 43.3; site.Longitude = 5.4;
            csv.Rows[0][3] = "Autre adresse"; csv.Rows[0][5] = "5.2";
            var pointKept = CityReferenceService.Preview(csv, settings).Settings.Sites.Single();
            Check(pointKept.Address.Length == 0 && pointKept.Latitude == 43.3 && pointKept.Longitude == 5.4);
            csv.Rows[0][4] = "43.3"; csv.Rows[0][5] = "5.4";
            var samePoint = CityReferenceService.Preview(csv, settings).Settings.Sites.Single();
            Check(samePoint.Address == "Autre adresse" && samePoint.Latitude == 43.3 && samePoint.Longitude == 5.4);
            csv.Rows[0][4] = csv.Rows[0][5] = "";
            Check(CityReferenceService.Preview(csv, settings).Settings.Sites.Single().Address.Length == 0);
            site.Address = "12 rue A B"; site.Latitude = site.Longitude = null;
            csv.Rows[0][3] = "12 rue AB"; csv.Rows[0][4] = "43.2"; csv.Rows[0][5] = "5.2";
            Check(CityReferenceService.Preview(csv, settings).Settings.Sites.Single().Latitude is null);
        });
        test("Complete-only import reuses an unambiguous historical alias and preserves explicit relationships", () =>
        {
            var (settings, city, site) = Fixture(); site.Name = "Agence renommée"; site.Aliases.Add("Agence");
            var csv = Csv(["Type", "VilleId", "Nom", "Alias", "Fusion"], ["Site", city.Name, "Agence", "Nouveau", "Completer"]);
            var imported = CityReferenceService.Preview(csv, settings).Settings;
            Check(imported.Sites.Count == 1 && imported.Sites[0].Id == site.Id && imported.Sites[0].Name == "Agence renommée");
            var other = new CityDefinition { Name = "Lyon" }; settings.Cities.Add(other);
            csv = Csv(["Type", "Id", "VilleId", "Nom", "Fusion"], ["Site", site.Id, other.Name, "Nom du catalogue", "Completer"]);
            var retained = CityReferenceService.Preview(csv, settings).Settings.Sites.Single();
            Check(retained.Id == site.Id && retained.CityId == city.Id && retained.Name == site.Name);
        });
        test("Ambiguous or conflicting complete-only site identities fail before creating duplicates", () =>
        {
            var (settings, _, site) = Fixture(); settings.Cities.Add(new() { Name = "Lyon" });
            var before = JsonSerializer.Serialize(settings);
            Reject(() => CityReferenceService.Preview(Csv(["Type", "VilleId", "Nom", "Fusion"], ["Site", "Lyon", "Agence", "Completer"]), settings));
            Reject(() => CityReferenceService.Preview(Csv(["Type", "Id", "VilleId", "Nom", "Fusion"],
                ["Site", Guid.NewGuid().ToString("N"), "Marseille", site.Name, "Completer"]), settings));
            Check(JsonSerializer.Serialize(settings) == before);
        });
        test("Unknown complete modes and alias overflow fail without losing the current reference", () =>
        {
            var (settings, _, site) = Fixture(); site.Aliases = Enumerable.Range(1, CitySettings.MaximumAliases).Select(i => "Alias " + i).ToList();
            var before = JsonSerializer.Serialize(settings);
            Reject(() => CityReferenceService.Preview(Csv(["Nom", "Fusion"], ["Marseille", "Effacer"]), settings));
            Reject(() => CityReferenceService.Preview(Csv(["Type", "VilleId", "Nom", "Alias", "Fusion"],
                ["Site", "Marseille", "Agence", "Autre alias", "Completer"]), settings));
            Check(JsonSerializer.Serialize(settings) == before);
        });
        test("Legacy replacement and full reference exports keep the existing site-patch schema", () =>
        {
            var (settings, _, site) = Fixture();
            var replaced = CityReferenceService.Preview(Csv(["Type", "Id", "Nom", "Alias", "Adresse"],
                ["Site", site.Id, "Agence", "", "Adresse choisie"]), settings).Settings.Sites.Single();
            Check(replaced.Aliases.Count == 0 && replaced.Address == "Adresse choisie" && replaced.Contact == site.Contact);
            var exported = CityReferenceService.Export(settings);
            Check(exported.Headers.Count == 21 && !exported.Headers.Contains("Fusion") && exported.Rows.All(row => row.Length == exported.Headers.Count));
            var roundtrip = CityReferenceService.Preview(exported, new()).Settings;
            Check(JsonSerializer.Serialize(roundtrip) == JsonSerializer.Serialize(settings));
        });
    }
}
