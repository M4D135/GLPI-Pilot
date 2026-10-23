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
    public const int MaximumRules = 16;
    public const int MaximumSites = 2000;

    public bool Enabled { get; set; } = true;
    public string TitleColumn { get; set; } = "";
    public List<CityDefinition> Cities { get; set; } = new();
    public List<SiteDefinition> Sites { get; set; } = new();
    public List<BuildingDefinition> Buildings { get; set; } = new();
    public string LocationColumn { get; set; } = "";
    public List<string> SourcePriority { get; set; } = new() { "manual", "combined" };

    /// <summary>A bounded, independent copy for use at runtime. Validate raw imports before copying.</summary>
    public CitySettings Copy()
    {
        var copy = new CitySettings
        {
            Enabled = Enabled,
            TitleColumn = TitleColumn, LocationColumn = LocationColumn,
            SourcePriority = SourcePriority?.ToList() ?? new() { "manual", "combined" },
            Buildings = Buildings?.Select(building => building.Copy()).ToList() ?? new(),
            Cities = Cities?.Where(city => city is not null).Take(MaximumCities)
                .Select(city => city.Copy()).ToList() ?? new(),
            Sites = Sites?.Where(site => site is not null).Take(MaximumSites).Select(site => site.Copy()).ToList() ?? new()
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
        LocationColumn = CityText.CleanText(LocationColumn, false);
        SourcePriority = (SourcePriority ?? new()).Where(value => value is "manual" or "combined" or "location" or "site" or "title" or "building").Distinct(StringComparer.Ordinal).ToList();
        if (SourcePriority.Count == 0) SourcePriority = new() { "manual", "combined" };
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
                // Discard a damaged contextual definition as a whole. Removing just its rule
                // could turn a restricted abbreviation back into an unconditional alias.
                try { city.ValidateAdvanced(); } catch (InvalidDataException) { continue; }
                var id = Guid.Parse(city.Id);
                if (!ids.Add(id))
                {
                    city.Id = Guid.NewGuid().ToString("N");
                    ids.Add(Guid.Parse(city.Id));
                }
                normalized.Add(city);
            }
        Cities = normalized;
        var siteIds = new HashSet<Guid>();
        Sites = (Sites ?? new()).Where(site => site != null).Take(MaximumSites).Select(site => site.Copy())
            .Where(site => site.IsValid(Cities) && Guid.TryParse(site.Id, out var id) && siteIds.Add(id)).ToList();
        var buildingIds = new HashSet<string>(StringComparer.Ordinal);
        Buildings = (Buildings ?? new()).Where(building => building != null).Take(5000).Select(building => building.Copy())
            .Where(building => building.IsValid(Sites) && buildingIds.Add(building.Id)).ToList();
    }

    /// <summary>Checks the original values without repairing or discarding an invalid user entry.</summary>
    public void Validate()
    {
        if (!CityText.ValidText(TitleColumn, allowEmpty: true) || Cities is null || Cities.Count > MaximumCities
            || Sites is null || Sites.Count > MaximumSites || Buildings is null || Buildings.Count > 5000
            || !CityText.ValidText(LocationColumn, true) || SourcePriority is null || SourcePriority.Count is < 1 or > 6
            || SourcePriority.Distinct(StringComparer.Ordinal).Count() != SourcePriority.Count || SourcePriority.Any(value => value is not ("manual" or "combined" or "location" or "site" or "title" or "building")))
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
            city.ValidateAdvanced();
        }
        var siteIds = new HashSet<Guid>();
        foreach (var site in Sites)
            if (site is null || !site.IsValid(Cities) || !siteIds.Add(Guid.Parse(site.Id)))
                throw new InvalidDataException("Chaque site doit avoir un identifiant unique et un nom ; sa commune est facultative mais doit exister si elle est renseignée. Vérifiez aussi ses horaires et coordonnées.");
        var buildingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var building in Buildings)
            if (building == null || !building.IsValid(Sites) || !buildingIds.Add(building.Id))
                throw new InvalidDataException("Chaque bâtiment doit avoir un identifiant unique, un site existant et un nom valide.");
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
    public string Region { get; set; } = "";
    public string Department { get; set; } = "";
    public string Sector { get; set; } = "";
    public string TerritoryPath { get; set; } = "";
    public List<string> ExcludedTerms { get; set; } = new();
    public List<CityAliasRule> Rules { get; set; } = new();

    public CityDefinition Copy()
    {
        var copy = new CityDefinition
        {
            Id = Id, Name = Name, PostalCode = PostalCode, Latitude = Latitude, Longitude = Longitude,
            Aliases = Aliases?.Take(CitySettings.MaximumAliases).ToList() ?? new(),
            Region = Region, Department = Department, Sector = Sector, TerritoryPath = TerritoryPath,
            ExcludedTerms = ExcludedTerms?.ToList()!, Rules = Rules?.Select(rule => rule?.Copy()!).ToList()!
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

    internal void ValidateAdvanced()
    {
        if (new[] { Region, Department, Sector }.Any(value => !CityText.ValidText(value, true))
            || TerritoryPath == null || TerritoryPath.Length > 1500 || TerritoryPath.Split('/').Length > 12 || TerritoryPath.Split('/').Any(part => !CityText.ValidText(part.Trim(), true))
            || !CityText.ValidTerms(ExcludedTerms) || Rules is null || Rules.Count > CitySettings.MaximumRules)
            throw new InvalidDataException("Région, département, secteur, exclusions ou règles contextuelles de ville invalides.");
        var ids = new HashSet<Guid>();
        foreach (var rule in Rules)
        {
            if (rule is null || !Guid.TryParse(rule.Id, out var id) || id == Guid.Empty || !ids.Add(id)
                || !CityText.ValidText(rule.Term, false) || CityText.Tokens(rule.Term).Count == 0
                || !CityText.ValidTerms(rule.RequiredTerms) || !CityText.ValidTerms(rule.ExcludedTerms)
                || !CityText.ValidText(rule.ContextColumn, true) || rule.Priority is < 0 or > 100
                || rule.Position is not ("any" or "start" or "end" or "token") || rule.TokenPosition is < 0 or > 10000 || rule.Position == "token" && rule.TokenPosition == 0
                || rule.TitlePattern == null || rule.TitlePattern.Length > 500)
                throw new InvalidDataException("Une règle de ville est invalide : terme, contexte, priorité, position ou format à vérifier.");
            if (rule.TitlePattern.Length > 0)
                try { _ = new System.Text.RegularExpressions.Regex(rule.TitlePattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(30)); }
                catch (ArgumentException error) { throw new InvalidDataException("Le format de titre de la règle est invalide.", error); }
        }
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
    TitleUnavailable,
    SiteOnly
}

/// <summary>RowIndex always refers to the unchanged order of Source.Rows.</summary>
public sealed record CityMatch(int RowIndex, string? CityId, string? CityName,
    CityMatchState State, IReadOnlyList<string> CandidateIds)
{
    public string? SiteId { get; init; }
    public string SiteName { get; init; } = "";
    public string? BuildingId { get; init; }
    public string BuildingName { get; init; } = "";
    public string SiteState { get; init; } = "unmatched";
    public string Explanation { get; init; } = "";
    public string Confidence { get; init; } = "none";
    public bool IsOverride { get; init; }
    public IReadOnlyList<CityMatchEvidence> Evidence { get; init; } = Array.Empty<CityMatchEvidence>();
}

public sealed record CityMatchEvidence(string CityId, string CityName, string Term, string RuleId, int Priority,
    string Decision, string Explanation)
{
    public string SiteId { get; init; } = "";
    public string SiteName { get; init; } = "";
}

public sealed class CityAliasRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Term { get; set; } = "";
    public List<string> RequiredTerms { get; set; } = new();
    public List<string> ExcludedTerms { get; set; } = new();
    public string ContextColumn { get; set; } = "";
    public int Priority { get; set; }
    public string Position { get; set; } = "any";
    public int TokenPosition { get; set; }
    public string TitlePattern { get; set; } = "";
    public CityAliasRule Copy() => new()
    { Id = Id, Term = Term, RequiredTerms = RequiredTerms?.ToList()!, ExcludedTerms = ExcludedTerms?.ToList()!, ContextColumn = ContextColumn, Priority = Priority, Position = Position, TokenPosition = TokenPosition, TitlePattern = TitlePattern };
}

public sealed class SiteDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CityId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public string Address { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public List<WeeklyOpening> Openings { get; set; } = new();
    public List<string> ClosedDates { get; set; } = new();
    public string Contact { get; set; } = "";
    public string Equipment { get; set; } = "";
    public SiteDefinition Copy() => new() { Id = Id, CityId = CityId, Name = Name, Aliases = Aliases?.ToList()!, Address = Address, Latitude = Latitude, Longitude = Longitude,
        Openings = Openings?.Select(value => value.Copy()).ToList()!, ClosedDates = ClosedDates?.ToList()!, Contact = Contact, Equipment = Equipment };
    internal bool IsValid(IReadOnlyList<CityDefinition> cities) => Guid.TryParse(Id, out var id) && id != Guid.Empty
        && CityId is not null && (CityId.Length == 0 || cities.Any(city => city.Id == CityId)) && CityText.ValidText(Name, false) && CityText.Tokens(Name).Count > 0
        && CityText.ValidTerms(Aliases) && CityText.ValidText(Address, true) && CityDefinition.ValidCoordinates(Latitude, Longitude)
        && CityText.ValidText(Contact, true) && CityText.ValidText(Equipment, true) && Openings != null && Openings.Count <= 28 && Openings.All(value => value != null && value.IsValid)
        && ClosedDates != null && ClosedDates.Count <= 366 && ClosedDates.All(value => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
}

public sealed class CityEnrichmentResult
{
    public ExtractionResult Source { get; init; } = new();
    public ExtractionResult Data { get; init; } = new();
    public bool Applied { get; init; }
    public IReadOnlyList<CityMatch> Matches { get; init; } = Array.Empty<CityMatch>();
    public string CityColumnName { get; init; } = "";
    public string StateColumnName { get; init; } = "";
    public string TitleColumnName { get; init; } = "";
    public string SiteColumnName { get; init; } = "";
    public string BuildingColumnName { get; init; } = "";
    public string TerritoryColumnName { get; init; } = "";
    public string RegionColumnName { get; init; } = "";
    public string DepartmentColumnName { get; init; } = "";
    public string SectorColumnName { get; init; } = "";
    public int SiteMatchedCount { get; init; }
    public int SiteOnlyCount { get; init; }
    public int ManualCount { get; init; }
    public int MatchedCount { get; init; }
    public int UnmatchedCount { get; init; }
    public int AmbiguousCount { get; init; }
    public int TitleUnavailableCount { get; init; }
    public string Message { get; init; } = "";
}

internal static class CityText
{
    public static bool ValidTerms(IReadOnlyList<string>? terms) => terms != null && terms.Count <= CitySettings.MaximumAliases
        && terms.All(term => ValidText(term, false) && Tokens(term).Count > 0);
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

public sealed class BuildingDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SiteId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public string Floor { get; set; } = "";
    public BuildingDefinition Copy() => new() { Id = Id, SiteId = SiteId, Name = Name, Aliases = Aliases?.ToList()!, Floor = Floor };
    internal bool IsValid(IReadOnlyList<SiteDefinition> sites) => Guid.TryParse(Id, out var id) && id != Guid.Empty && sites.Any(site => site.Id == SiteId)
        && CityText.ValidText(Name, false) && CityText.Tokens(Name).Count > 0 && CityText.ValidTerms(Aliases) && CityText.ValidText(Floor, true);
}

public sealed class WeeklyOpening
{
    public DayOfWeek Day { get; set; }
    public string From { get; set; } = "08:00";
    public string Until { get; set; } = "17:00";
    public WeeklyOpening Copy() => new() { Day = Day, From = From, Until = Until };
    internal bool IsValid => Enum.IsDefined(Day) && TimeOnly.TryParseExact(From, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
        && TimeOnly.TryParseExact(Until, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until) && from < until;
}
