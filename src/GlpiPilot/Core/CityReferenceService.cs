using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

public sealed record CityReferenceChange(string Kind, string Type, string Id, string Name, string Detail);
public sealed record CityReferencePreview(CitySettings Settings, IReadOnlyList<CityReferenceChange> Changes);

/// <summary>Validated, reviewable merges of a bounded CSV reference; never mutates the current settings.</summary>
public static class CityReferenceService
{
    private static readonly string[] Headers = { "Type", "Id", "VilleId", "Nom", "Alias", "CodePostal", "Region", "Departement", "Secteur", "Adresse", "Latitude", "Longitude", "Exclusions", "Regles", "Territoire", "SiteId", "Etage", "Ouvertures", "Fermetures", "Contact", "Materiel" };
    // Fusion is an import instruction, not a reference field. Keep the exported
    // schema unchanged so existing Admin site patches retain their v1 contract.
    private static readonly string[] ImportHeaders = Headers.Append("Fusion").ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static string Key(string text) => string.Concat(CityText.Tokens(text));

    public static CityReferencePreview Preview(ExtractionResult csv, CitySettings current, CancellationToken ct = default, Func<string, List<string>>? termReader = null)
    {
        ArgumentNullException.ThrowIfNull(csv); ArgumentNullException.ThrowIfNull(current); current.Validate(); ct.ThrowIfCancellationRequested();
        if (csv.Rows.Count > CitySettings.MaximumCities + CitySettings.MaximumSites + 5000 || csv.Headers.Count > ImportHeaders.Length)
            throw new InvalidDataException("Le référentiel dépasse 1 000 villes, 2 000 sites, 5 000 bâtiments ou le nombre de colonnes prévu.");
        var headerKeys = csv.Headers.Select(Key).ToArray();
        if (headerKeys.Distinct(StringComparer.Ordinal).Count() != headerKeys.Length || headerKeys.Any(key => !ImportHeaders.Any(header => Key(header) == key)) || !headerKeys.Contains("NOM"))
            throw new InvalidDataException("Utilisez les colonnes du modèle : " + string.Join(", ", ImportHeaders) + ". Nom est obligatoire.");
        if (csv.Rows.Any(row => row == null || row.Length != csv.Headers.Count)) throw new InvalidDataException("Chaque ligne du référentiel doit avoir exactement ses en-têtes.");
        var result = current.Copy(); var changes = new List<CityReferenceChange>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = csv.Rows.Select((row, index) => (Row: row, Number: index + 2)).ToArray();
        List<string> ReadTerms(string value) => termReader is null ? Terms(value) : termReader(value);
        string Value(string[] row, string header) { var column = Array.IndexOf(headerKeys, Key(header)); return column < 0 ? "" : row[column].Trim(); }
        bool Has(string header) => headerKeys.Contains(Key(header));
        bool Complete(string[] row) => Key(Value(row, "Fusion")) == "COMPLETER";
        string TextField(string[] row, string header, string previous, bool complete) => Has(header) && (!complete || string.IsNullOrWhiteSpace(previous)) ? Value(row, header) : previous;
        List<string> TermField(string[] row, string header, List<string> previous, bool complete)
        {
            if (!Has(header)) return previous;
            var incoming = ReadTerms(Value(row, header));
            if (!complete) return incoming;
            var merged = previous.ToList();
            var keys = previous.Select(term => string.Join(' ', CityText.Tokens(term))).ToHashSet(StringComparer.Ordinal);
            foreach (var term in incoming)
                if (keys.Add(string.Join(' ', CityText.Tokens(term)))) merged.Add(term);
            return merged;
        }
        (double? Latitude, double? Longitude) Position(string[] row, int number, double? latitude, double? longitude, bool complete)
        {
            if (!Has("Latitude") && !Has("Longitude")) return (latitude, longitude);
            var importedLatitude = Has("Latitude") ? Coordinate(Value(row, "Latitude"), number) : null;
            var importedLongitude = Has("Longitude") ? Coordinate(Value(row, "Longitude"), number) : null;
            if (!complete) return (Has("Latitude") ? importedLatitude : latitude, Has("Longitude") ? importedLongitude : longitude);
            if (!CityDefinition.ValidCoordinates(importedLatitude, importedLongitude))
                throw new InvalidDataException($"Ligne {number} : Compléter demande une paire latitude/longitude valide ou deux valeurs vides.");
            return latitude.HasValue || longitude.HasValue ? (latitude, longitude) : (importedLatitude, importedLongitude);
        }
        bool IsSite(string[] row) => Key(Value(row, "Type")) == "SITE";
        bool IsBuilding(string[] row) => Key(Value(row, "Type")) == "BATIMENT";
        foreach (var (row, number) in rows)
        {
            if (Key(Value(row, "Type")) is not ("" or "VILLE" or "SITE" or "BATIMENT")) throw new InvalidDataException($"Ligne {number} : Type doit être Ville, Site ou Bâtiment.");
            if (Key(Value(row, "Fusion")) is not ("" or "REMPLACER" or "COMPLETER"))
                throw new InvalidDataException($"Ligne {number} : Fusion doit être vide, Remplacer ou Completer.");
        }
        foreach (var (row, number) in rows.Where(item => !IsSite(item.Row) && !IsBuilding(item.Row)))
        {
            ct.ThrowIfCancellationRequested(); var name = Value(row, "Nom"); var id = Value(row, "Id"); var complete = Complete(row);
            var sameName = result.Cities.Where(city => Key(city.Name) == Key(name)).ToArray();
            if (id.Length == 0 && sameName.Length > 1) throw new InvalidDataException($"Ligne {number} : plusieurs villes portent ce nom ; renseignez Id.");
            var existing = id.Length == 0 ? sameName.SingleOrDefault() : result.Cities.FirstOrDefault(city => city.Id == id);
            if (complete && id.Length > 0 && existing is null && sameName.Length > 0)
                throw new InvalidDataException($"Ligne {number} : cette ville existe avec un autre identifiant ; conservez son Id pour la compléter.");
            id = id.Length > 0 ? id : existing?.Id ?? Guid.NewGuid().ToString("N");
            if (!seen.Add("city:" + id)) throw new InvalidDataException($"Ligne {number} : la même ville apparaît plusieurs fois.");
            var city = existing?.Copy() ?? new CityDefinition { Id = id }; if (!complete || existing is null) city.Name = name;
            city.Aliases = TermField(row, "Alias", city.Aliases, complete);
            city.PostalCode = TextField(row, "CodePostal", city.PostalCode, complete);
            city.Region = TextField(row, "Region", city.Region, complete); city.Department = TextField(row, "Departement", city.Department, complete);
            city.TerritoryPath = TextField(row, "Territoire", city.TerritoryPath, complete);
            city.Sector = TextField(row, "Secteur", city.Sector, complete);
            if (Has("Exclusions") && (!complete || city.ExcludedTerms.Count == 0)) city.ExcludedTerms = ReadTerms(Value(row, "Exclusions"));
            (city.Latitude, city.Longitude) = Position(row, number, city.Latitude, city.Longitude, complete);
            if (Has("Regles") && (!complete || city.Rules.Count == 0))
            {
                var json = Value(row, "Regles"); if (json.Length > 48_000) throw new InvalidDataException($"Ligne {number} : règles trop longues.");
                try { city.Rules = json.Length == 0 ? new() : JsonSerializer.Deserialize<List<CityAliasRule>>(json, JsonOptions) ?? throw new InvalidDataException("Règles nulles."); }
                catch (JsonException error) { throw new InvalidDataException($"Ligne {number} : règles JSON invalides.", error); }
            }
            var changed = existing == null || JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(city);
            if (existing != null) result.Cities.Remove(existing); result.Cities.Add(city);
            changes.Add(new(existing == null ? "Ajout" : changed ? "Modification" : "Identique", "Ville", id, name,
                existing == null ? "Nouvelle ville de référence." : changed ? ChangedCity(existing, city) + (complete ? " Valeurs déjà renseignées conservées." : "")
                    : complete ? "Complément : valeurs déjà renseignées conservées." : "Aucune modification."));
        }
        foreach (var (row, number) in rows.Where(item => IsSite(item.Row)))
        {
            ct.ThrowIfCancellationRequested(); var id = Value(row, "Id"); var name = Value(row, "Nom"); var cityReference = Value(row, "VilleId"); var complete = Complete(row);
            var cities = cityReference.Length == 0 ? Array.Empty<CityDefinition>() : result.Cities.Where(city => city.Id == cityReference || Key(city.Name) == Key(cityReference)).ToArray();
            if (cityReference.Length > 0 && cities.Length != 1) throw new InvalidDataException($"Ligne {number} : VilleId doit être vide ou identifier une ville unique par son Id ou son nom.");
            var cityId = cities.SingleOrDefault()?.Id ?? "";
            // An omitted parent column preserves a known relationship. An explicit empty
            // VilleId detaches the site; neither case invents a municipality for a new site.
            var sameName = result.Sites.Where(site => (!Has("VilleId") || site.CityId == cityId) && Key(site.Name) == Key(name)).ToArray();
            if (id.Length == 0 && sameName.Length > 1) throw new InvalidDataException($"Ligne {number} : site ambigu ; renseignez Id.");
            var existing = id.Length == 0 ? sameName.SingleOrDefault() : result.Sites.FirstOrDefault(site => site.Id == id);
            if (complete && existing is null)
            {
                if (id.Length > 0 && sameName.Length > 0 || result.Sites.Any(site => Key(site.Name) == Key(name) && site.CityId != cityId))
                    throw new InvalidDataException($"Ligne {number} : ce nom de site existe avec un autre identifiant ou rattachement ; renseignez son Id pour le compléter sans doublon.");
                if (id.Length == 0)
                {
                    var aliases = result.Sites.Where(site => (!Has("VilleId") || site.CityId == cityId) && site.Aliases.Any(alias => Key(alias) == Key(name))).ToArray();
                    if (aliases.Length > 1) throw new InvalidDataException($"Ligne {number} : plusieurs sites portent cet alias ; renseignez Id.");
                    existing = aliases.SingleOrDefault();
                }
            }
            id = id.Length > 0 ? id : existing?.Id ?? Guid.NewGuid().ToString("N"); if (!seen.Add("site:" + id)) throw new InvalidDataException($"Ligne {number} : site dupliqué.");
            var site = existing?.Copy() ?? new SiteDefinition { Id = id };
            if ((Has("VilleId") || existing is null) && (!complete || site.CityId.Length == 0)) site.CityId = cityId;
            if (!complete || existing is null) site.Name = name;
            site.Aliases = TermField(row, "Alias", site.Aliases, complete);
            var address = TextField(row, "Adresse", site.Address, complete);
            var position = Position(row, number, site.Latitude, site.Longitude, complete);
            if (complete && string.IsNullOrWhiteSpace(site.Address) && site.Latitude.HasValue && address.Length > 0)
            {
                // A personalized point without an address cannot be paired with a
                // catalogue address unless both sources identify the same point.
                var incoming = Position(row, number, null, null, true);
                if (incoming.Latitude != site.Latitude || incoming.Longitude != site.Longitude) address = site.Address;
            }
            site.Address = address;
            site.Contact = TextField(row, "Contact", site.Contact, complete); site.Equipment = TextField(row, "Materiel", site.Equipment, complete);
            if (Has("Fermetures") && (!complete || site.ClosedDates.Count == 0)) site.ClosedDates = ReadTerms(Value(row, "Fermetures"));
            if (Has("Ouvertures") && (!complete || site.Openings.Count == 0))
                try { site.Openings = Value(row, "Ouvertures").Length == 0 ? new() : JsonSerializer.Deserialize<List<WeeklyOpening>>(Value(row, "Ouvertures"), JsonOptions) ?? new(); }
                catch (JsonException error) { throw new InvalidDataException($"Ligne {number} : ouvertures JSON invalides.", error); }
            // An address customized in the destination must not acquire the
            // coordinates of a different address from the incoming catalogue.
            if (!complete || !Has("Adresse") || site.Address.Length == 0
                || string.Join(' ', CityText.Tokens(site.Address)) == string.Join(' ', CityText.Tokens(Value(row, "Adresse"))))
                (site.Latitude, site.Longitude) = position;
            var changed = existing == null || JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(site);
            if (existing != null) result.Sites.Remove(existing); result.Sites.Add(site);
            changes.Add(new(existing == null ? "Ajout" : changed ? "Modification" : "Identique", "Site", id, name,
                existing == null ? site.CityId.Length == 0 ? "Nouveau site · commune non renseignée." : "Nouveau site rattaché à " + result.Cities.Single(city => city.Id == site.CityId).Name + "."
                    : changed ? complete ? "Complément du site ; valeurs déjà renseignées conservées." : "Nom, commune, alias, adresse ou coordonnées du site modifiés."
                    : complete ? "Complément : valeurs déjà renseignées conservées." : "Aucune modification."));
        }
        foreach (var (row, number) in rows.Where(item => IsBuilding(item.Row)))
        {
            ct.ThrowIfCancellationRequested(); var id = Value(row, "Id"); var name = Value(row, "Nom"); var reference = Value(row, "SiteId"); var complete = Complete(row);
            var sites = result.Sites.Where(site => site.Id == reference || Key(site.Name) == Key(reference)).ToArray();
            if (sites.Length != 1) throw new InvalidDataException($"Ligne {number} : SiteId doit désigner un site unique.");
            var sameName = result.Buildings.Where(building => building.SiteId == sites[0].Id && Key(building.Name) == Key(name)).ToArray();
            if (id.Length == 0 && sameName.Length > 1) throw new InvalidDataException($"Ligne {number} : bâtiment ambigu ; renseignez Id.");
            var existing = id.Length == 0 ? sameName.SingleOrDefault() : result.Buildings.FirstOrDefault(building => building.Id == id);
            id = id.Length > 0 ? id : existing?.Id ?? Guid.NewGuid().ToString("N"); if (!seen.Add("building:" + id)) throw new InvalidDataException($"Ligne {number} : bâtiment dupliqué.");
            var building = existing?.Copy() ?? new BuildingDefinition { Id = id };
            if (!complete || existing is null) { building.SiteId = sites[0].Id; building.Name = name; }
            building.Aliases = TermField(row, "Alias", building.Aliases, complete); building.Floor = TextField(row, "Etage", building.Floor, complete);
            var changed = existing == null || JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(building);
            if (existing != null) result.Buildings.Remove(existing); result.Buildings.Add(building);
            changes.Add(new(existing == null ? "Ajout" : changed ? "Modification" : "Identique", "Bâtiment", id, name, "Rattachement au site " + sites[0].Name + "."));
        }
        result.Validate(); ct.ThrowIfCancellationRequested(); return new(result, changes);
    }

    public static ExtractionResult Export(CitySettings settings)
    {
        settings.Validate(); var data = new ExtractionResult { Headers = Headers.ToList(), Title = "Référentiel villes et sites", Complete = true, Scope = "Dictionnaire uniquement ; aucune ligne de ticket" };
        string N(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        foreach (var city in settings.Cities)
            data.Rows.Add(new[] { "Ville", city.Id, "", city.Name, string.Join(" | ", city.Aliases), city.PostalCode, city.Region, city.Department, city.Sector, "", N(city.Latitude), N(city.Longitude), string.Join(" | ", city.ExcludedTerms), JsonSerializer.Serialize(city.Rules), city.TerritoryPath, "", "", "", "", "", "" });
        foreach (var site in settings.Sites)
            data.Rows.Add(new[] { "Site", site.Id, site.CityId, site.Name, string.Join(" | ", site.Aliases), "", "", "", "", site.Address, N(site.Latitude), N(site.Longitude), "", "", "", "", "", JsonSerializer.Serialize(site.Openings), string.Join(" | ", site.ClosedDates), site.Contact, site.Equipment });
        foreach (var building in settings.Buildings)
            data.Rows.Add(new[] { "Bâtiment", building.Id, "", building.Name, string.Join(" | ", building.Aliases), "", "", "", "", "", "", "", "", "", "", building.SiteId, building.Floor, "", "", "", "" });
        return data;
    }
    private static List<string> Terms(string text) => text.Split(new[] { '|', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    private static double? Coordinate(string text, int row)
    {
        if (text.Length == 0) return null;
        // The shared CSV exporter protects negative spreadsheet cells with an apostrophe.
        // It is safe to remove it here only because this field must parse as a finite number.
        if (text[0] == '\'') text = text[1..];
        if (double.TryParse(text.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) return value;
        throw new InvalidDataException($"Ligne {row} : coordonnée illisible, utilisez un point ou une virgule décimale.");
    }
    private static string ChangedCity(CityDefinition before, CityDefinition after)
    {
        var fields = new List<string>();
        if (before.Name != after.Name) fields.Add("nom"); if (!before.Aliases.SequenceEqual(after.Aliases)) fields.Add("alias");
        if (before.TerritoryPath != after.TerritoryPath) fields.Add("hiérarchie libre");
        if (before.PostalCode != after.PostalCode) fields.Add("code postal"); if (before.Region != after.Region || before.Department != after.Department || before.Sector != after.Sector) fields.Add("hiérarchie");
        if (before.Latitude != after.Latitude || before.Longitude != after.Longitude) fields.Add("coordonnées");
        if (!before.ExcludedTerms.SequenceEqual(after.ExcludedTerms)) fields.Add("exclusions");
        if (JsonSerializer.Serialize(before.Rules) != JsonSerializer.Serialize(after.Rules)) fields.Add("règles contextuelles");
        return "Changements : " + string.Join(", ", fields) + ".";
    }
}
