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
    private static readonly string[] Headers = { "Type", "Id", "VilleId", "Nom", "Alias", "CodePostal", "Region", "Departement", "Secteur", "Adresse", "Latitude", "Longitude", "Exclusions", "Regles" };
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static string Key(string text) => string.Concat(CityText.Tokens(text));

    public static CityReferencePreview Preview(ExtractionResult csv, CitySettings current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(csv); ArgumentNullException.ThrowIfNull(current); current.Validate(); ct.ThrowIfCancellationRequested();
        if (csv.Rows.Count > CitySettings.MaximumCities + CitySettings.MaximumSites || csv.Headers.Count > Headers.Length)
            throw new InvalidDataException("Le référentiel dépasse 1 000 villes, 2 000 sites ou le nombre de colonnes prévu.");
        var headerKeys = csv.Headers.Select(Key).ToArray();
        if (headerKeys.Distinct(StringComparer.Ordinal).Count() != headerKeys.Length || headerKeys.Any(key => !Headers.Any(header => Key(header) == key)) || !headerKeys.Contains("NOM"))
            throw new InvalidDataException("Utilisez les colonnes du modèle : Type, Id, VilleId, Nom, Alias, CodePostal, Region, Departement, Secteur, Adresse, Latitude, Longitude, Exclusions, Regles. Nom est obligatoire.");
        if (csv.Rows.Any(row => row == null || row.Length != csv.Headers.Count)) throw new InvalidDataException("Chaque ligne du référentiel doit avoir exactement ses en-têtes.");
        var result = current.Copy(); var changes = new List<CityReferenceChange>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = csv.Rows.Select((row, index) => (Row: row, Number: index + 2)).ToArray();
        string Value(string[] row, string header) { var column = Array.IndexOf(headerKeys, Key(header)); return column < 0 ? "" : row[column].Trim(); }
        bool Has(string header) => headerKeys.Contains(Key(header));
        bool IsSite(string[] row) => Key(Value(row, "Type")) == "SITE";
        foreach (var (row, number) in rows)
            if (Key(Value(row, "Type")) is not ("" or "VILLE" or "SITE")) throw new InvalidDataException($"Ligne {number} : Type doit être Ville ou Site.");
        foreach (var (row, number) in rows.Where(item => !IsSite(item.Row)))
        {
            ct.ThrowIfCancellationRequested(); var name = Value(row, "Nom"); var id = Value(row, "Id");
            var sameName = result.Cities.Where(city => Key(city.Name) == Key(name)).ToArray();
            if (id.Length == 0 && sameName.Length > 1) throw new InvalidDataException($"Ligne {number} : plusieurs villes portent ce nom ; renseignez Id.");
            var existing = id.Length == 0 ? sameName.SingleOrDefault() : result.Cities.FirstOrDefault(city => city.Id == id);
            id = id.Length > 0 ? id : existing?.Id ?? Guid.NewGuid().ToString("N");
            if (!seen.Add("city:" + id)) throw new InvalidDataException($"Ligne {number} : la même ville apparaît plusieurs fois.");
            var city = existing?.Copy() ?? new CityDefinition { Id = id }; city.Name = name;
            if (Has("Alias")) city.Aliases = Terms(Value(row, "Alias"));
            if (Has("CodePostal")) city.PostalCode = Value(row, "CodePostal");
            if (Has("Region")) city.Region = Value(row, "Region"); if (Has("Departement")) city.Department = Value(row, "Departement");
            if (Has("Secteur")) city.Sector = Value(row, "Secteur"); if (Has("Exclusions")) city.ExcludedTerms = Terms(Value(row, "Exclusions"));
            if (Has("Latitude")) city.Latitude = Coordinate(Value(row, "Latitude"), number);
            if (Has("Longitude")) city.Longitude = Coordinate(Value(row, "Longitude"), number);
            if (Has("Regles"))
            {
                var json = Value(row, "Regles"); if (json.Length > 48_000) throw new InvalidDataException($"Ligne {number} : règles trop longues.");
                try { city.Rules = json.Length == 0 ? new() : JsonSerializer.Deserialize<List<CityAliasRule>>(json, JsonOptions) ?? throw new InvalidDataException("Règles nulles."); }
                catch (JsonException error) { throw new InvalidDataException($"Ligne {number} : règles JSON invalides.", error); }
            }
            var changed = existing == null || JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(city);
            if (existing != null) result.Cities.Remove(existing); result.Cities.Add(city);
            changes.Add(new(existing == null ? "Ajout" : changed ? "Modification" : "Identique", "Ville", id, name,
                existing == null ? "Nouvelle ville de référence." : changed ? ChangedCity(existing, city) : "Aucune modification."));
        }
        foreach (var (row, number) in rows.Where(item => IsSite(item.Row)))
        {
            ct.ThrowIfCancellationRequested(); var id = Value(row, "Id"); var name = Value(row, "Nom"); var cityReference = Value(row, "VilleId");
            var cities = result.Cities.Where(city => city.Id == cityReference || Key(city.Name) == Key(cityReference)).ToArray();
            if (cities.Length != 1) throw new InvalidDataException($"Ligne {number} : VilleId doit identifier une ville unique par son Id ou son nom.");
            var sameName = result.Sites.Where(site => site.CityId == cities[0].Id && Key(site.Name) == Key(name)).ToArray();
            if (id.Length == 0 && sameName.Length > 1) throw new InvalidDataException($"Ligne {number} : site ambigu ; renseignez Id.");
            var existing = id.Length == 0 ? sameName.SingleOrDefault() : result.Sites.FirstOrDefault(site => site.Id == id);
            id = id.Length > 0 ? id : existing?.Id ?? Guid.NewGuid().ToString("N"); if (!seen.Add("site:" + id)) throw new InvalidDataException($"Ligne {number} : site dupliqué.");
            var site = existing?.Copy() ?? new SiteDefinition { Id = id }; site.CityId = cities[0].Id; site.Name = name;
            if (Has("Alias")) site.Aliases = Terms(Value(row, "Alias")); if (Has("Adresse")) site.Address = Value(row, "Adresse");
            if (Has("Latitude")) site.Latitude = Coordinate(Value(row, "Latitude"), number); if (Has("Longitude")) site.Longitude = Coordinate(Value(row, "Longitude"), number);
            var changed = existing == null || JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(site);
            if (existing != null) result.Sites.Remove(existing); result.Sites.Add(site);
            changes.Add(new(existing == null ? "Ajout" : changed ? "Modification" : "Identique", "Site", id, name,
                existing == null ? "Nouveau site rattaché à " + cities[0].Name + "." : changed ? "Nom, ville, alias, adresse ou coordonnées du site modifiés." : "Aucune modification."));
        }
        result.Validate(); ct.ThrowIfCancellationRequested(); return new(result, changes);
    }

    public static ExtractionResult Export(CitySettings settings)
    {
        settings.Validate(); var data = new ExtractionResult { Headers = Headers.ToList(), Title = "Référentiel villes et sites", Complete = true, Scope = "Dictionnaire uniquement ; aucune ligne de ticket" };
        string N(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        foreach (var city in settings.Cities)
            data.Rows.Add(new[] { "Ville", city.Id, "", city.Name, string.Join(" | ", city.Aliases), city.PostalCode, city.Region, city.Department, city.Sector, "", N(city.Latitude), N(city.Longitude), string.Join(" | ", city.ExcludedTerms), JsonSerializer.Serialize(city.Rules) });
        foreach (var site in settings.Sites)
            data.Rows.Add(new[] { "Site", site.Id, site.CityId, site.Name, string.Join(" | ", site.Aliases), "", "", "", "", site.Address, N(site.Latitude), N(site.Longitude), "", "" });
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
        if (before.PostalCode != after.PostalCode) fields.Add("code postal"); if (before.Region != after.Region || before.Department != after.Department || before.Sector != after.Sector) fields.Add("hiérarchie");
        if (before.Latitude != after.Latitude || before.Longitude != after.Longitude) fields.Add("coordonnées");
        if (!before.ExcludedTerms.SequenceEqual(after.ExcludedTerms)) fields.Add("exclusions");
        if (JsonSerializer.Serialize(before.Rules) != JsonSerializer.Serialize(after.Rules)) fields.Add("règles contextuelles");
        return "Changements : " + string.Join(", ", fields) + ".";
    }
}
