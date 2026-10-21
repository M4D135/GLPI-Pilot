using System.Globalization;
using System.IO;
using System.Text;

namespace GlpiPilot.Core;

/// <summary>Explicit, portable city names and aliases. No ticket text is stored in these settings.</summary>
public sealed class CitySettings
{
    public const int MaximumCities = 1000;
    public const int MaximumAliases = 12;
    public const int MaximumTextLength = 120;

    public bool Enabled { get; set; } = true;
    public string TitleColumn { get; set; } = "";
    public List<CityDefinition> Cities { get; set; } = new();

    /// <summary>A bounded, independent copy for use at runtime. Validate raw imports before copying.</summary>
    public CitySettings Copy()
    {
        var copy = new CitySettings
        {
            Enabled = Enabled,
            TitleColumn = TitleColumn,
            Cities = Cities?.Where(city => city is not null).Take(MaximumCities)
                .Select(city => city.Copy()).ToList() ?? new()
        };
        copy.Normalize();
        return copy;
    }

    /// <summary>Non-throwing recovery for local settings. Strict save/import validation is separate.</summary>
    public void Normalize()
    {
        // A column name is an exact header, so retain meaningful leading/trailing spaces.
        TitleColumn = CityText.CleanText(TitleColumn, false);
        if (string.IsNullOrWhiteSpace(TitleColumn)) TitleColumn = "";
        var normalized = new List<CityDefinition>();
        var ids = new HashSet<Guid>();
        if (Cities is not null)
            foreach (var entry in Cities.Take(MaximumCities))
            {
                if (entry is null) continue;
                // Shared object references are possible in a locally assembled draft.
                // Repairing one duplicate identifier must not also change the other city.
                var city = entry.Copy();
                if (CityText.Tokens(city.Name).Count == 0) continue;
                var id = Guid.Parse(city.Id);
                if (!ids.Add(id))
                {
                    city.Id = Guid.NewGuid().ToString("N");
                    ids.Add(Guid.Parse(city.Id));
                }
                normalized.Add(city);
            }
        Cities = normalized;
    }

    /// <summary>Checks the original values without repairing or discarding an invalid user entry.</summary>
    public void Validate()
    {
        if (!CityText.ValidText(TitleColumn, allowEmpty: true) || Cities is null || Cities.Count > MaximumCities)
            throw new InvalidDataException($"La liste de villes est invalide : au maximum {MaximumCities} villes et {MaximumTextLength} caractères pour le nom de colonne.");
        var ids = new HashSet<Guid>();
        for (var index = 0; index < Cities.Count; index++)
        {
            var city = Cities[index];
            var number = index + 1;
            if (city is null || !Guid.TryParse(city.Id, out var id) || id == Guid.Empty || !ids.Add(id))
                throw new InvalidDataException($"Ville {number} : un identifiant GUID unique et non vide est nécessaire.");
            if (!CityText.ValidText(city.Name, allowEmpty: false) || CityText.Tokens(city.Name).Count == 0)
                throw new InvalidDataException($"Ville {number} : renseignez un nom contenant des lettres ou chiffres, de {MaximumTextLength} caractères au maximum.");
            if (city.Aliases is null || city.Aliases.Count > MaximumAliases)
                throw new InvalidDataException($"Ville {number} : au maximum {MaximumAliases} variantes sont possibles.");
            if (city.Aliases.Any(alias => !CityText.ValidText(alias, allowEmpty: false) || CityText.Tokens(alias).Count == 0))
                throw new InvalidDataException($"Ville {number} : chaque variante doit contenir un mot et compter au maximum {MaximumTextLength} caractères.");
            if (!CityText.ValidText(city.PostalCode, allowEmpty: true))
                throw new InvalidDataException($"Ville {number} : le code postal doit compter au maximum {MaximumTextLength} caractères.");
            if (!CityDefinition.ValidCoordinates(city.Latitude, city.Longitude))
                throw new InvalidDataException($"Ville {number} : renseignez latitude et longitude ensemble, avec une latitude de −90 à 90 et une longitude de −180 à 180.");
        }
    }
}

public sealed class CityDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public string PostalCode { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public CityDefinition Copy()
    {
        var copy = new CityDefinition
        {
            Id = Id, Name = Name, PostalCode = PostalCode, Latitude = Latitude, Longitude = Longitude,
            Aliases = Aliases?.Take(CitySettings.MaximumAliases).ToList() ?? new()
        };
        copy.Normalize();
        return copy;
    }

    internal void Normalize()
    {
        Id = Guid.TryParse(Id, out var id) && id != Guid.Empty ? Id.Trim() : Guid.NewGuid().ToString("N");
        Name = CityText.CleanText(Name);
        PostalCode = CityText.CleanText(PostalCode);
        var aliases = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (Aliases is not null)
            foreach (var alias in Aliases.Take(CitySettings.MaximumAliases))
            {
                var text = CityText.CleanText(alias);
                var key = string.Join(' ', CityText.Tokens(text));
                if (key.Length > 0 && known.Add(key)) aliases.Add(text);
            }
        Aliases = aliases;
        if (!ValidCoordinates(Latitude, Longitude)) Latitude = Longitude = null;
    }

    internal static bool ValidCoordinates(double? latitude, double? longitude)
        => !latitude.HasValue && !longitude.HasValue
            || latitude.HasValue && longitude.HasValue && double.IsFinite(latitude.Value)
                && double.IsFinite(longitude.Value) && latitude.Value is >= -90 and <= 90
                && longitude.Value is >= -180 and <= 180;
}

public enum CityMatchState
{
    Matched,
    Unmatched,
    Ambiguous,
    TitleUnavailable
}

/// <summary>RowIndex always refers to the unchanged order of Source.Rows.</summary>
public sealed record CityMatch(int RowIndex, string? CityId, string? CityName,
    CityMatchState State, IReadOnlyList<string> CandidateIds);

public sealed class CityEnrichmentResult
{
    public ExtractionResult Source { get; init; } = new();
    public ExtractionResult Data { get; init; } = new();
    public bool Applied { get; init; }
    public IReadOnlyList<CityMatch> Matches { get; init; } = Array.Empty<CityMatch>();
    public string CityColumnName { get; init; } = "";
    public string StateColumnName { get; init; } = "";
    public string TitleColumnName { get; init; } = "";
    public int MatchedCount { get; init; }
    public int UnmatchedCount { get; init; }
    public int AmbiguousCount { get; init; }
    public int TitleUnavailableCount { get; init; }
    public string Message { get; init; } = "";
}

internal static class CityText
{
    public static bool ValidText(string? value, bool allowEmpty)
    {
        if (value is null || value.Length > CitySettings.MaximumTextLength
            || !allowEmpty && string.IsNullOrWhiteSpace(value)) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsControl(ch)) return false;
            if (char.IsHighSurrogate(ch))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            else if (char.IsLowSurrogate(ch)) return false;
        }
        return true;
    }

    public static string CleanText(string? value, bool trim = true)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var buffer = new StringBuilder(Math.Min(value.Length, CitySettings.MaximumTextLength));
        foreach (var rune in value.EnumerateRunes())
        {
            var text = Rune.IsControl(rune) || rune == Rune.ReplacementChar ? " " : rune.ToString();
            if (buffer.Length + text.Length > CitySettings.MaximumTextLength) break;
            buffer.Append(text);
        }
        return trim ? buffer.ToString().Trim() : buffer.ToString();
    }

    /// <summary>Accent/case insensitive words; punctuation, apostrophes and hyphens are boundaries.</summary>
    public static List<string> Tokens(string? value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(value)) return new();
        string decomposed;
        try { decomposed = value.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException)
        {
            // Broken UTF-16 is a separator, never grounds for dropping a whole ticket row.
            var repaired = new StringBuilder(value.Length);
            var repairPosition = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if ((repairPosition++ & 1023) == 0) ct.ThrowIfCancellationRequested();
                repaired.Append(rune == Rune.ReplacementChar ? " " : rune.ToString());
            }
            decomposed = repaired.ToString().Normalize(NormalizationForm.FormD);
        }
        ct.ThrowIfCancellationRequested();
        var words = new List<string>();
        var word = new StringBuilder();
        var position = 0;
        foreach (var rune in decomposed.EnumerateRunes())
        {
            if ((position++ & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (Rune.IsLetterOrDigit(rune)) word.Append(Rune.ToUpperInvariant(rune).ToString());
            else if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }
}
