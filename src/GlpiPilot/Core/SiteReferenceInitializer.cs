using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record SiteReferenceInitialization(string State, int AddedSites = 0, int AddedAliases = 0, string Message = "")
{
    public int AddedCities { get; init; }
    public int EnrichedSites { get; init; }
}

/// <summary>Optional local site reference. Later geographic editions only fill missing local information.</summary>
public static class SiteReferenceInitializer
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string Key(string text) => string.Join(' ', CityText.Tokens(text));
    private sealed record SiteEntry(SiteDefinition Site, CityDefinition? City);
    private sealed record ReferenceFile(IReadOnlyList<SiteEntry> Sites, bool HasGeography);

    public static SiteReferenceInitialization TryInitialize(string applicationDirectory, string settingsPath)
    {
        // Startup calls this before creating the runtime. A simulated runtime must
        // never consume a pack file or initialize the user's ordinary profile.
        if (AppPaths.SimulationRoot is not null) return new("simulation");
        lock (Gate)
        {
            try
            {
                var csvPath = Path.Combine(applicationDirectory, "wad", "sites.csv");
                if (!File.Exists(csvPath)) return new("absent");
                byte[]? original = File.Exists(settingsPath) ? ReadBounded(settingsPath) : null;
                if (original is not null) ValidateJsonKeys(original);
                var current = original is null ? new AppSettings() : SettingsRecovery.Read(original, validateReference: true);
                var bytes = ReadBounded(csvPath);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                var initialized = current.SiteReferenceImportHash.Length > 0;
                if (current.SiteReferenceImportHash == hash) return new("already-imported");
                var reference = Parse(bytes);
                // Keep the original two-column initialization contract. A later
                // geographic edition may complete existing sites, but never
                // recreates deleted/renamed sites or removed detection terms.
                if (initialized && !reference.HasGeography) return new("already-imported");
                var merged = current.Copy();
                int addedSites = 0, addedAliases = 0, addedCities = 0, enrichedSites = 0, preservedRelations = 0;
                foreach (var entry in reference.Sites)
                {
                    var matches = merged.Cities.Sites.Where(site => Key(site.Name) == Key(entry.Site.Name)).Take(2).ToArray();
                    if (matches.Length > 1) throw new InvalidDataException("Un nom du CSV correspond à plusieurs sites existants.");
                    var site = matches.SingleOrDefault();
                    if (site is null)
                    {
                        if (initialized) continue;
                        if (merged.Cities.Sites.Count >= CitySettings.MaximumSites)
                            throw new InvalidDataException("Le référentiel dépasserait 2 000 sites.");
                        site = new SiteDefinition { Name = entry.Site.Name };
                        merged.Cities.Sites.Add(site); addedSites++;
                    }
                    if (!initialized)
                    {
                        var known = new HashSet<string>(site.Aliases.Select(Key), StringComparer.Ordinal) { Key(site.Name) };
                        foreach (var alias in entry.Site.Aliases)
                            if (known.Add(Key(alias))) { site.Aliases.Add(alias); addedAliases++; }
                    }
                    if (site.Aliases.Count > CitySettings.MaximumAliases)
                        throw new InvalidDataException("La fusion dépasserait 12 termes de détection pour un site.");
                    var enriched = false;
                    if (entry.City is { } suppliedCity)
                    {
                        var city = site.CityId.Length > 0 ? merged.Cities.Cities.Single(value => value.Id == site.CityId) : null;
                        if (city is not null && Key(city.Name) != Key(suppliedCity.Name))
                        {
                            // A manual relationship wins. Applying the other
                            // city's address or coordinates would be misleading.
                            preservedRelations++; continue;
                        }
                        if (city is null)
                        {
                            var candidates = merged.Cities.Cities.Where(value => Key(value.Name) == Key(suppliedCity.Name)).Take(2).ToArray();
                            if (candidates.Length > 1) throw new InvalidDataException("Une commune du CSV correspond à plusieurs villes existantes.");
                            city = candidates.SingleOrDefault();
                            if (city is null)
                            {
                                if (merged.Cities.Cities.Count >= CitySettings.MaximumCities) throw new InvalidDataException("Le référentiel dépasserait 1 000 villes.");
                                city = suppliedCity.Copy(); merged.Cities.Cities.Add(city); addedCities++;
                            }
                            site.CityId = city.Id; enriched = true;
                        }
                        if (string.IsNullOrWhiteSpace(city.PostalCode) && suppliedCity.PostalCode.Length > 0) city.PostalCode = suppliedCity.PostalCode;
                        if (!city.Latitude.HasValue && suppliedCity.Latitude.HasValue)
                        { city.Latitude = suppliedCity.Latitude; city.Longitude = suppliedCity.Longitude; }
                    }
                    if (string.IsNullOrWhiteSpace(site.Address) && entry.Site.Address.Length > 0) { site.Address = entry.Site.Address; enriched = true; }
                    if (!site.Latitude.HasValue && entry.Site.Latitude.HasValue)
                    { site.Latitude = entry.Site.Latitude; site.Longitude = entry.Site.Longitude; enriched = true; }
                    if (enriched) enrichedSites++;
                }
                merged.Cities.Validate();
                merged.SiteReferenceImportHash = hash;
                // The primary-instance mutex already excludes another app. Also
                // refuse an external settings edit observed during this import.
                var unchanged = original is null ? !File.Exists(settingsPath)
                    : File.Exists(settingsPath) && original.AsSpan().SequenceEqual(ReadBounded(settingsPath));
                if (!unchanged) throw new IOException("Les réglages ont changé pendant l’import du référentiel.");
                // Save backs up the complete original and atomically persists the
                // sites and receipt together. No earlier write marks a failed import.
                SettingsRecovery.Save(settingsPath, merged);
                return new(initialized ? "updated" : "imported", addedSites, addedAliases,
                    $"Référentiel facultatif : {addedSites} site(s), {addedAliases} terme(s) et {addedCities} ville(s) ajoutés ; {enrichedSites} site(s) complétés."
                    + (preservedRelations > 0 ? $" {preservedRelations} rattachement(s) local(aux) différent(s) conservé(s)." : ""))
                    { AddedCities = addedCities, EnrichedSites = enrichedSites };
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // Keep startup quiet and never log cell values or private settings.
                return new("failed", Message: "Le référentiel facultatif wad/sites.csv n’a pas été importé. " +
                    "Vérifiez son format, les limites du référentiel et les droits du dossier de réglages. Le démarrage continue.");
            }
        }
    }

    private static byte[] ReadBounded(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Le fichier est vide ou dépasse 4 Mio.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new IOException("Le fichier a changé pendant sa lecture.");
        return bytes;
    }

    private static ReferenceFile Parse(byte[] bytes)
    {
        int offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var text = Utf8.GetString(bytes, offset, bytes.Length - offset);
        using var reader = new StringReader(text);
        var entries = new List<SiteEntry>(); var names = new HashSet<string>(StringComparer.Ordinal);
        var cities = new Dictionary<string, CityDefinition>(StringComparer.Ordinal);
        Dictionary<string, int>? columns = null;
        foreach (var row in NativeCsvImportService.ReadRecords(reader, ';', 1, CancellationToken.None))
        {
            if (row.PhysicallyBlank) continue;
            if (columns is null)
            {
                var headers = row.Fields.Select(value => HeaderRole(Key(value))).ToArray();
                if (headers.Length is < 2 or > 9 || headers.Any(value => value.Length == 0) || headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
                    throw new InvalidDataException("Le CSV contient une colonne inconnue ou dupliquée.");
                columns = headers.Select((value, index) => (value, index)).ToDictionary(item => item.value, item => item.index, StringComparer.Ordinal);
                if (!columns.ContainsKey("site") || !columns.ContainsKey("terms"))
                    throw new InvalidDataException("Les colonnes Site et Termes de détection sont requises.");
                continue;
            }
            if (row.Fields.Length != columns.Count) throw new InvalidDataException("Chaque ligne doit contenir exactement les colonnes annoncées.");
            string Value(string name) => columns.TryGetValue(name, out var column) ? row.Fields[column].Trim() : "";
            var name = Value("site");
            if (!ValidTerm(name)) throw new InvalidDataException("Un nom de site est vide, invalide ou dépasse 120 caractères.");
            if (!names.Add(Key(name))) throw new InvalidDataException("Le CSV contient plusieurs occurrences du même nom de site.");
            var aliases = new List<string>(); var known = new HashSet<string>(StringComparer.Ordinal) { Key(name) };
            foreach (var alias in Value("terms").Split(new[] { ';', '|', '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!ValidTerm(alias)) throw new InvalidDataException("Un terme de détection est invalide ou dépasse 120 caractères.");
                if (known.Add(Key(alias))) aliases.Add(alias);
            }
            if (aliases.Count > CitySettings.MaximumAliases || entries.Count >= CitySettings.MaximumSites)
                throw new InvalidDataException("Le CSV dépasse 2 000 sites ou 12 termes de détection par site.");
            var cityName = Value("city"); var postalCode = Value("postal"); var address = Value("address");
            var cityLatitude = Coordinate(Value("city-latitude")); var cityLongitude = Coordinate(Value("city-longitude"));
            var siteLatitude = Coordinate(Value("site-latitude")); var siteLongitude = Coordinate(Value("site-longitude"));
            if (!CityText.ValidText(address, true) || !CityText.ValidText(postalCode, true)
                || !CityDefinition.ValidCoordinates(cityLatitude, cityLongitude) || !CityDefinition.ValidCoordinates(siteLatitude, siteLongitude))
                throw new InvalidDataException("Adresse, code postal ou paire de coordonnées invalide.");
            CityDefinition? city = null;
            if (cityName.Length > 0)
            {
                if (!ValidTerm(cityName)) throw new InvalidDataException("Un nom de commune est invalide.");
                if (!cities.TryGetValue(Key(cityName), out city))
                { city = new() { Name = cityName }; cities.Add(Key(cityName), city); }
                if (postalCode.Length > 0 && city.PostalCode.Length > 0 && postalCode != city.PostalCode
                    || cityLatitude.HasValue && city.Latitude.HasValue && (cityLatitude != city.Latitude || cityLongitude != city.Longitude))
                    throw new InvalidDataException("Les lignes d’une même commune contiennent des données géographiques contradictoires.");
                if (postalCode.Length > 0) city.PostalCode = postalCode;
                if (cityLatitude.HasValue) { city.Latitude = cityLatitude; city.Longitude = cityLongitude; }
            }
            else if (postalCode.Length > 0 || cityLatitude.HasValue)
                throw new InvalidDataException("Une commune est requise pour son code postal ou ses coordonnées.");
            entries.Add(new(new SiteDefinition { Name = name, Aliases = aliases, Address = address, Latitude = siteLatitude, Longitude = siteLongitude }, city));
        }
        if (entries.Count == 0) throw new InvalidDataException("Le CSV ne contient aucun site.");
        if (cities.Count > CitySettings.MaximumCities) throw new InvalidDataException("Le CSV dépasse 1 000 villes.");
        return new(entries, columns!.Keys.Any(key => key is not ("site" or "terms")));
    }

    private static string HeaderRole(string key) => key switch
    {
        "SITE" or "NOM DU SITE" or "SITE AFFICHE" => "site",
        "TERMES DE DETECTION" or "TERMES" or "ALIAS" or "ECRITURES RECONNUES" => "terms",
        "VILLE" or "COMMUNE" => "city", "CODE POSTAL" or "CODEPOSTAL" => "postal", "ADRESSE" => "address",
        "LATITUDE VILLE" => "city-latitude", "LONGITUDE VILLE" => "city-longitude",
        "LATITUDE SITE" => "site-latitude", "LONGITUDE SITE" => "site-longitude", _ => ""
    };

    private static double? Coordinate(string text)
    {
        if (text.Length == 0) return null;
        if (text[0] == '\'') text = text[1..];
        if (double.TryParse(text.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) return value;
        throw new InvalidDataException("Une coordonnée est illisible.");
    }

    private static bool ValidTerm(string text) => CityText.ValidText(text, allowEmpty: false) && CityText.Tokens(text).Count > 0;

    private static void ValidateJsonKeys(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Les réglages doivent former un objet JSON.");
        Check(document.RootElement);
        static void Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw new InvalidDataException("Les réglages contiennent une propriété dupliquée.");
                    Check(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Check(item);
        }
    }
}
