using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class TechnicianPreference
{
    public bool Favorite { get; set; }
    public int? ActiveTarget { get; set; }
    public string Notes { get; set; } = "";
    public List<string> Skills { get; set; } = new();
    public List<string> PrimaryTerritories { get; set; } = new();
    public List<string> BackupTerritories { get; set; } = new();
    public int? DailyCapacityMinutes { get; set; }
    public int? EstimatedTicketMinutes { get; set; }
    public DateTime? UnavailableFrom { get; set; }
    public DateTime? UnavailableUntil { get; set; }
    public string AvailabilityNote { get; set; } = "";
    public List<TechnicianWorkPeriod> WorkPeriods { get; set; } = new();
    public List<TechnicianCapacityDay> CapacityDays { get; set; } = new();
    public List<TechnicianCategoryEstimate> CategoryEstimates { get; set; } = new();
    public TechnicianPreference Copy() => new()
    {
        Favorite = Favorite, ActiveTarget = ActiveTarget, Notes = Notes,
        Skills = Skills?.ToList() ?? new(), PrimaryTerritories = PrimaryTerritories?.ToList() ?? new(),
        BackupTerritories = BackupTerritories?.ToList() ?? new(), DailyCapacityMinutes = DailyCapacityMinutes,
        EstimatedTicketMinutes = EstimatedTicketMinutes, UnavailableFrom = UnavailableFrom,
        UnavailableUntil = UnavailableUntil, AvailabilityNote = AvailabilityNote,
        WorkPeriods = WorkPeriods.Select(p => p.Copy()).ToList(), CapacityDays = CapacityDays.Select(d => d.Copy()).ToList(), CategoryEstimates = CategoryEstimates.Select(e => e.Copy()).ToList()
    };
}

/// <summary>Local planning aids only; neither GLPI state nor portable application configuration.</summary>
public sealed class TechnicianPreferences
{
    public const int MaximumTechnicians = 2000;
    public const int MaximumIdentityLength = 400;
    public const int MaximumNotesLength = 1200;
    public const int MaximumActiveTarget = 100000;
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 8 };
    public int Version { get; set; } = 1;
    public Dictionary<string, TechnicianPreference> Technicians { get; set; } = new(StringComparer.Ordinal);

    public TechnicianPreference Get(string key) => Technicians.TryGetValue(TechnicianDashboardService.IdentityKey(key), out var value)
        ? value.Copy() : new();

    public TechnicianPreferences Copy() => new()
    {
        Version = Version,
        Technicians = Technicians.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.Ordinal)
    };

    public TechnicianPreferences With(string key, TechnicianPreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        var normalizedKey = ValidIdentity(key);
        var normalizedPreference = ValidPreference(preference);
        var copy = Copy();
        if (!normalizedPreference.Favorite && !normalizedPreference.ActiveTarget.HasValue && normalizedPreference.Notes.Length == 0
            && normalizedPreference.Skills.Count == 0 && normalizedPreference.PrimaryTerritories.Count == 0 && normalizedPreference.BackupTerritories.Count == 0
            && !normalizedPreference.DailyCapacityMinutes.HasValue && !normalizedPreference.EstimatedTicketMinutes.HasValue
            && normalizedPreference.WorkPeriods.Count == 0 && normalizedPreference.CapacityDays.Count == 0 && normalizedPreference.CategoryEstimates.Count == 0
            && !normalizedPreference.UnavailableFrom.HasValue && !normalizedPreference.UnavailableUntil.HasValue && normalizedPreference.AvailabilityNote.Length == 0)
            copy.Technicians.Remove(normalizedKey);
        else copy.Technicians[normalizedKey] = normalizedPreference;
        if (copy.Technicians.Count > MaximumTechnicians)
            throw new InvalidDataException($"Les préférences locales sont limitées à {MaximumTechnicians:N0} techniciens.");
        return copy;
    }

    public static TechnicianPreferences Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        using var ownedStream = stream;
        if (stream.Length > MaximumFileBytes)
            throw new InvalidDataException("Le fichier des préférences des techniciens est trop volumineux.");
        var document = JsonSerializer.Deserialize<TechnicianPreferences>(stream, JsonOptions)
            ?? throw new InvalidDataException("Le fichier des préférences des techniciens est vide.");
        return Validate(document);
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var document = Validate(this);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("Les préférences des techniciens dépassent la taille autorisée.");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static TechnicianPreferences Validate(TechnicianPreferences source)
    {
        if (source.Version != 1 || source.Technicians == null)
            throw new InvalidDataException("La version des préférences des techniciens n’est pas reconnue.");
        if (source.Technicians.Count > MaximumTechnicians)
            throw new InvalidDataException("Le nombre de préférences des techniciens dépasse la limite autorisée.");
        var result = new TechnicianPreferences();
        foreach (var pair in source.Technicians)
        {
            var key = ValidIdentity(pair.Key);
            if (!result.Technicians.TryAdd(key, ValidPreference(pair.Value)))
                throw new InvalidDataException("Plusieurs préférences locales désignent la même identité.");
        }
        return result;
    }

    private static string ValidIdentity(string? key)
    {
        if (key == null || key.Length > MaximumIdentityLength || key.Any(char.IsControl))
            throw new InvalidDataException("Une identité de technicien est invalide dans les préférences locales.");
        var normalized = TechnicianDashboardService.IdentityKey(key);
        if (normalized.Length == 0 || normalized.Length > MaximumIdentityLength)
            throw new InvalidDataException("Une identité de technicien est absente ou trop longue.");
        return normalized;
    }

    private static TechnicianPreference ValidPreference(TechnicianPreference? value)
    {
        if (value == null || value.ActiveTarget is < 1 or > MaximumActiveTarget)
            throw new InvalidDataException($"L’objectif local doit être vide ou compris entre 1 et {MaximumActiveTarget:N0} actifs.");
        var notes = (value.Notes ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (notes.Length > MaximumNotesLength || notes.Any(character => char.IsControl(character) && character is not ('\n' or '\t')))
            throw new InvalidDataException($"La note locale doit contenir au maximum {MaximumNotesLength:N0} caractères de texte.");
        if (value.DailyCapacityMinutes is < 0 or > 1440 || value.EstimatedTicketMinutes is < 1 or > 10080
            || value.UnavailableFrom.HasValue != value.UnavailableUntil.HasValue
            || value.UnavailableFrom > value.UnavailableUntil)
            throw new InvalidDataException("La capacité doit être de 0 à 1 440 minutes, l’estimation de 1 à 10 080 minutes, et une indisponibilité doit avoir deux dates ordonnées.");
        static List<string> Labels(List<string>? items)
        {
            if (items == null || items.Count > 50 || items.Any(text => text == null || text.Length > 160 || text.Any(char.IsControl)))
                throw new InvalidDataException("Une liste de compétences ou de secteurs est invalide (50 valeurs de 160 caractères maximum).");
            return items.Select(text => text.Trim()).Where(text => text.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        TechnicianCalendar.Validate(value);
        var availability = (value.AvailabilityNote ?? "").Trim();
        if (availability.Length > 1200 || availability.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new InvalidDataException("La note de disponibilité dépasse 1 200 caractères.");
        return new()
        {
            Favorite = value.Favorite, ActiveTarget = value.ActiveTarget, Notes = notes,
            Skills = Labels(value.Skills), PrimaryTerritories = Labels(value.PrimaryTerritories), BackupTerritories = Labels(value.BackupTerritories),
            DailyCapacityMinutes = value.DailyCapacityMinutes, EstimatedTicketMinutes = value.EstimatedTicketMinutes,
            UnavailableFrom = value.UnavailableFrom?.Date, UnavailableUntil = value.UnavailableUntil?.Date, AvailabilityNote = availability,
            WorkPeriods = value.WorkPeriods.Select(p => p.Copy()).ToList(), CapacityDays = value.CapacityDays.Select(d => d.Copy()).ToList(), CategoryEstimates = value.CategoryEstimates.Select(e => e.Copy()).ToList()
        };
    }
}
