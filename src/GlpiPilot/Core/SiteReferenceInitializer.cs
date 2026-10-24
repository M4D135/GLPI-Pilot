using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record SiteReferenceInitialization(string State, int AddedSites = 0, int AddedAliases = 0, string Message = "");

/// <summary>One optional, local CSV import per profile; no reference data is compiled into the application.</summary>
public static class SiteReferenceInitializer
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string Key(string text) => string.Join(' ', CityText.Tokens(text));

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
                // The hash is a receipt, not a revision to synchronize. A different
                // future CSV must not undo edits or recreate sites the user deleted.
                if (current.SiteReferenceImportHash.Length > 0) return new("already-imported");

                var bytes = ReadBounded(csvPath);
                var entries = Parse(bytes);
                var merged = current.Copy();
                int addedSites = 0, addedAliases = 0;
                foreach (var entry in entries)
                {
                    var matches = merged.Cities.Sites.Where(site => Key(site.Name) == Key(entry.Name)).Take(2).ToArray();
                    if (matches.Length > 1) throw new InvalidDataException("Un nom du CSV correspond à plusieurs sites existants.");
                    var site = matches.SingleOrDefault();
                    if (site is null)
                    {
                        if (merged.Cities.Sites.Count >= CitySettings.MaximumSites)
                            throw new InvalidDataException("Le référentiel dépasserait 2 000 sites.");
                        site = new SiteDefinition { Name = entry.Name };
                        merged.Cities.Sites.Add(site); addedSites++;
                    }
                    // Retain existing spelling, IDs, relationships, geometry and
                    // options. Only genuinely new detection terms are appended.
                    var known = new HashSet<string>(site.Aliases.Select(Key), StringComparer.Ordinal) { Key(site.Name) };
                    foreach (var alias in entry.Aliases)
                        if (known.Add(Key(alias))) { site.Aliases.Add(alias); addedAliases++; }
                    if (site.Aliases.Count > CitySettings.MaximumAliases)
                        throw new InvalidDataException("La fusion dépasserait 12 termes de détection pour un site.");
                }
                merged.Cities.Validate();
                merged.SiteReferenceImportHash = Convert.ToHexString(SHA256.HashData(bytes));
                // The primary-instance mutex already excludes another app. Also
                // refuse an external settings edit observed during this import.
                var unchanged = original is null ? !File.Exists(settingsPath)
                    : File.Exists(settingsPath) && original.AsSpan().SequenceEqual(ReadBounded(settingsPath));
                if (!unchanged) throw new IOException("Les réglages ont changé pendant l’import du référentiel.");
                // Save backs up the complete original and atomically persists the
                // sites and receipt together. No earlier write marks a failed import.
                SettingsRecovery.Save(settingsPath, merged);
                return new("imported", addedSites, addedAliases,
                    $"Référentiel de sites importé depuis le fichier facultatif : {addedSites} site(s) et {addedAliases} terme(s) ajoutés.");
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

    private static List<SiteDefinition> Parse(byte[] bytes)
    {
        int offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var text = Utf8.GetString(bytes, offset, bytes.Length - offset);
        using var reader = new StringReader(text);
        var entries = new List<SiteDefinition>(); var names = new HashSet<string>(StringComparer.Ordinal);
        int nameColumn = -1, termsColumn = -1;
        foreach (var row in NativeCsvImportService.ReadRecords(reader, ';', 1, CancellationToken.None))
        {
            if (row.PhysicallyBlank) continue;
            if (row.Fields.Length != 2) throw new InvalidDataException("Le CSV doit contenir exactement deux colonnes.");
            if (nameColumn < 0)
            {
                var headers = row.Fields.Select(Key).ToArray();
                nameColumn = Array.FindIndex(headers, value => value is "SITE" or "NOM DU SITE" or "SITE AFFICHE");
                termsColumn = Array.FindIndex(headers, value => value is "TERMES DE DETECTION" or "TERMES" or "ALIAS" or "ECRITURES RECONNUES");
                if (nameColumn < 0 || termsColumn < 0 || nameColumn == termsColumn)
                    throw new InvalidDataException("Les colonnes Site et Termes de détection sont requises.");
                continue;
            }
            var name = row.Fields[nameColumn].Trim();
            if (!ValidTerm(name)) throw new InvalidDataException("Un nom de site est vide, invalide ou dépasse 120 caractères.");
            if (!names.Add(Key(name))) throw new InvalidDataException("Le CSV contient plusieurs occurrences du même nom de site.");
            var aliases = new List<string>(); var known = new HashSet<string>(StringComparer.Ordinal) { Key(name) };
            foreach (var alias in row.Fields[termsColumn].Split(new[] { ';', '|', '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!ValidTerm(alias)) throw new InvalidDataException("Un terme de détection est invalide ou dépasse 120 caractères.");
                if (known.Add(Key(alias))) aliases.Add(alias);
            }
            if (aliases.Count > CitySettings.MaximumAliases || entries.Count >= CitySettings.MaximumSites)
                throw new InvalidDataException("Le CSV dépasse 2 000 sites ou 12 termes de détection par site.");
            entries.Add(new SiteDefinition { Name = name, Aliases = aliases });
        }
        if (entries.Count == 0) throw new InvalidDataException("Le CSV ne contient aucun site.");
        return entries;
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
