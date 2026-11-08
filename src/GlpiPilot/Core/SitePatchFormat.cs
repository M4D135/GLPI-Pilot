using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>Versioned text escaping for shareable references; spreadsheet formulas remain inert and values round-trip exactly.</summary>
public static class SitePatchFormat
{
    public const string TextPrefix = "~glpi-text-v1:", ListPrefix = "~glpi-list-v1:";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static ExportResult Write(CitySettings settings, string path, CancellationToken ct = default)
    {
        var data = CityReferenceService.Export(settings);
        var aliases = data.Headers.IndexOf("Alias"); var exclusions = data.Headers.IndexOf("Exclusions"); var closed = data.Headers.IndexOf("Fermetures");
        foreach (var row in data.Rows)
        {
            ct.ThrowIfCancellationRequested();
            var type = row[0]; var id = row[1];
            var city = type == "Ville" ? settings.Cities.Single(value => value.Id == id) : null;
            var site = type == "Site" ? settings.Sites.Single(value => value.Id == id) : null;
            var building = type == "Bâtiment" ? settings.Buildings.Single(value => value.Id == id) : null;
            for (var column = 0; column < row.Length; column++) row[column] = Encode(row[column]);
            row[aliases] = EncodeList(city?.Aliases ?? site?.Aliases ?? building?.Aliases ?? []);
            row[exclusions] = EncodeList(city?.ExcludedTerms ?? []);
            row[closed] = EncodeList(site?.ClosedDates ?? []);
        }
        return ExportService.WriteCsv(data, path, ct);
    }

    public static ExtractionResult Read(string path, CancellationToken ct = default)
    {
        var data = NativeCsvImportService.Read(path, "", maxRows: 8001, cancellationToken: ct);
        for (var row = 0; row < data.Rows.Count; row++)
        {
            ct.ThrowIfCancellationRequested();
            data.Rows[row] = data.Rows[row].Select(Decode).ToArray();
        }
        return data;
    }

    public static CityReferencePreview Preview(ExtractionResult data, CitySettings current, CancellationToken ct = default)
        => CityReferenceService.Preview(data, current, ct, ReadTerms);

    private static string EncodeList(IReadOnlyList<string> values) => ListPrefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(values));
    private static List<string> ReadTerms(string value)
    {
        if (!value.StartsWith(ListPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("Le patch v1 doit conserver ses listes encodées. Régénérez-le avec le lanceur Admin.");
        try
        {
            var encoded = value[ListPrefix.Length..]; var bytes = Convert.FromBase64String(encoded);
            if (Convert.ToBase64String(bytes) != encoded) throw new FormatException();
            var values = JsonSerializer.Deserialize<List<string>>(Utf8.GetString(bytes), new JsonSerializerOptions { MaxDepth = 4 });
            if (values is null || values.Any(item => item is null)) throw new FormatException();
            return values;
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException or JsonException)
        {
            throw new InvalidDataException("Le patch contient une liste encodée GLPI Pilot v1 invalide. Régénérez-le avec le lanceur Admin.", error);
        }
    }

    private static string Encode(string value)
    {
        if (value.Length == 0) return value;
        var protect = value[0] is '\'' or '\t' or '\r' or '\n' || (value.StartsWith(TextPrefix, StringComparison.Ordinal) || value.StartsWith(ListPrefix, StringComparison.Ordinal));
        var first = 0;
        while (first < value.Length && (char.IsWhiteSpace(value[first]) || char.IsControl(value[first]) || char.GetUnicodeCategory(value[first]) == UnicodeCategory.Format)) first++;
        protect |= first < value.Length && value[first] is '=' or '+' or '-' or '@';
        return protect ? TextPrefix + Convert.ToBase64String(Utf8.GetBytes(value)) : value;
    }

    private static string Decode(string value)
    {
        if (!value.StartsWith(TextPrefix, StringComparison.Ordinal)) return value;
        try
        {
            var encoded = value[TextPrefix.Length..];
            var bytes = Convert.FromBase64String(encoded);
            if (Convert.ToBase64String(bytes) != encoded) throw new FormatException();
            return Utf8.GetString(bytes);
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("Le patch contient une cellule encodée GLPI Pilot v1 invalide. Régénérez-le avec le lanceur Admin.", error);
        }
    }
}
