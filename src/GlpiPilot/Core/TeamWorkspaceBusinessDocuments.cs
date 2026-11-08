using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

public abstract class TeamBusinessDocument
{
    public string Schema { get; set; } = "glpi-pilot.business-document";
    public int Format { get; set; } = 1;
    public string Installation { get; set; } = "";
}
public sealed class SharedPersonalNoteDocument : TeamBusinessDocument
{
    public TicketReference Ticket { get; set; } = new("", "");
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
}
public sealed class SharedTourDocument : TeamBusinessDocument
{
    public ScheduledTour Tour { get; set; } = new();
}
public sealed class SharedDispatchDocument : TeamBusinessDocument
{
    public DispatchScenario Scenario { get; set; } = new();
    public List<TicketReference> References { get; set; } = new();
}
public sealed class SharedTechniciansDocument : TeamBusinessDocument
{
    public Dictionary<string, SharedTechnicianBusiness> Technicians { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Only explicitly shared planning fields. Personal notes, availability notes,
/// favourites and personal targets do not exist in this transport type.</summary>
public sealed class SharedTechnicianBusiness
{
    public List<string> Skills { get; set; } = new();
    public List<string> PrimaryTerritories { get; set; } = new();
    public List<string> BackupTerritories { get; set; } = new();
    public int? DailyCapacityMinutes { get; set; }
    public int? EstimatedTicketMinutes { get; set; }
    public DateTime? UnavailableFrom { get; set; }
    public DateTime? UnavailableUntil { get; set; }
    public List<TechnicianWorkPeriod> WorkPeriods { get; set; } = new();
    public List<TechnicianCapacityDay> CapacityDays { get; set; } = new();
    public List<TechnicianCategoryEstimate> CategoryEstimates { get; set; } = new();

    public static SharedTechnicianBusiness From(TechnicianPreference value) => new()
    {
        Skills = value.Skills.ToList(), PrimaryTerritories = value.PrimaryTerritories.ToList(), BackupTerritories = value.BackupTerritories.ToList(),
        DailyCapacityMinutes = value.DailyCapacityMinutes, EstimatedTicketMinutes = value.EstimatedTicketMinutes,
        UnavailableFrom = value.UnavailableFrom, UnavailableUntil = value.UnavailableUntil,
        WorkPeriods = value.WorkPeriods.Select(p => p.Copy()).ToList(), CapacityDays = value.CapacityDays.Select(d => d.Copy()).ToList(),
        CategoryEstimates = value.CategoryEstimates.Select(e => e.Copy()).ToList()
    };
    internal TechnicianPreference ToPreference() => new()
    {
        Skills = Skills, PrimaryTerritories = PrimaryTerritories, BackupTerritories = BackupTerritories,
        DailyCapacityMinutes = DailyCapacityMinutes, EstimatedTicketMinutes = EstimatedTicketMinutes,
        UnavailableFrom = UnavailableFrom, UnavailableUntil = UnavailableUntil, WorkPeriods = WorkPeriods,
        CapacityDays = CapacityDays, CategoryEstimates = CategoryEstimates
    };
}

/// <summary>Versioned business payloads; their installation and identities are verified
/// independently of the contribution's free text. No credentials or GLPI mutations.</summary>
public static class TeamWorkspaceBusinessDocuments
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 20,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static bool IsBusinessKind(string kind) => kind is "personal-note" or "tour" or "dispatch" or "technicians";
    public static JsonElement Payload(TeamBusinessDocument document)
    {
        Validate(document);
        var payload = JsonSerializer.SerializeToElement(document, document.GetType(), Options);
        CheckSize(payload, document is SharedTourDocument ? 16 * 1024 * 1024 : document is SharedPersonalNoteDocument ? 64 * 1024 : 2 * 1024 * 1024);
        return payload;
    }
    public static TeamBusinessDocument Read(TeamWorkspaceItem item)
    {
        if (!IsBusinessKind(item.Kind) || item.Payload is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out var version) || version != 1
            || !payload.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != "glpi-pilot.business-document")
            throw new InvalidDataException("Le document métier partagé est absent ou sa version n’est pas reconnue.");
        CheckSize(payload, item.Kind == "tour" ? 16 * 1024 * 1024 : item.Kind == "personal-note" ? 64 * 1024 : 2 * 1024 * 1024);
        TeamBusinessDocument? document;
        try
        {
            document = item.Kind switch
            {
                "personal-note" => payload.Deserialize<SharedPersonalNoteDocument>(Options),
                "tour" => payload.Deserialize<SharedTourDocument>(Options),
                "dispatch" => payload.Deserialize<SharedDispatchDocument>(Options),
                "technicians" => payload.Deserialize<SharedTechniciansDocument>(Options),
                _ => null
            };
        }
        catch (JsonException) { throw new InvalidDataException("Le document métier partagé contient des champs inconnus ou illisibles."); }
        if (document is null) throw new InvalidDataException("Le document métier partagé est vide.");
        Validate(document);
        if (document is SharedPersonalNoteDocument note)
        {
            if (item.TicketKey != TicketLibraryRules.Key(note.Ticket) || item.Body != note.Notes)
                throw new InvalidDataException("La note partagée ne correspond pas à son ticket ou à son texte annoncé.");
        }
        else if (item.TicketKey.Length != 0) throw new InvalidDataException("Ce document métier ne doit pas être rattaché à un seul ticket.");
        return document;
    }
    public static string Installation(string url)
    {
        var value = ObjectIdentity.Installation(url); ValidateInstallation(value); return value;
    }
    public static TicketReference PublicReference(TicketReference ticket)
    {
        _ = TicketLibraryRules.Key(ticket);
        var uri = new Uri(ticket.Url);
        return new(ticket.Id, uri.GetLeftPart(UriPartial.Path) + "?id=" + ticket.Id);
    }
    public static TechnicianPreferences Merge(TechnicianPreferences local, SharedTechniciansDocument incoming)
    {
        Validate(incoming); var result = local.Copy();
        foreach (var pair in incoming.Technicians)
        {
            var personal = local.Get(pair.Key); var business = pair.Value.ToPreference();
            business.Favorite = personal.Favorite; business.ActiveTarget = personal.ActiveTarget;
            business.Notes = personal.Notes; business.AvailabilityNote = personal.AvailabilityNote;
            result = result.With(pair.Key, business);
        }
        return result;
    }
    private static void Validate(TeamBusinessDocument document)
    {
        if (document.Schema != "glpi-pilot.business-document" || document.Format != 1)
            throw new InvalidDataException("Version du document métier non reconnue.");
        ValidateInstallation(document.Installation);
        switch (document)
        {
            case SharedPersonalNoteDocument note:
                ValidateReference(note.Ticket, document.Installation);
                if (!ValidText(note.Title, 2000, false) || !ValidText(note.Notes, WorkplaceStore.MaximumNotes, true) || string.IsNullOrWhiteSpace(note.Notes))
                    throw new InvalidDataException("La note partagée est vide, trop longue ou contient des caractères non autorisés.");
                break;
            case SharedTourDocument tour:
                if (tour.Tour is null) throw new InvalidDataException("La tournée partagée est absente.");
                tour.Tour.Validate();
                if (tour.Tour.SourceKey != document.Installation || tour.Tour.Tickets.Any(t => !ValidId(t.TicketId)))
                    throw new InvalidDataException("Les tickets de la tournée ne correspondent pas à l’installation annoncée.");
                break;
            case SharedDispatchDocument dispatch:
                if (dispatch.Scenario is null || dispatch.References is null) throw new InvalidDataException("Le scénario partagé est absent.");
                TechnicianPlanningService.Validate(dispatch.Scenario);
                if (dispatch.Scenario.Date.Year is < 2000 or > 2100 || dispatch.References.Count != dispatch.Scenario.Assignments.Count)
                    throw new InvalidDataException("La date ou les références du scénario sont invalides.");
                var references = new Dictionary<string, TicketReference>(StringComparer.Ordinal);
                foreach (var reference in dispatch.References)
                {
                    ValidateReference(reference, document.Installation);
                    if (!references.TryAdd(WorkplaceStore.TicketKey(reference), reference)) throw new InvalidDataException("Une référence du scénario est dupliquée.");
                }
                foreach (var assignment in dispatch.Scenario.Assignments)
                    if (!references.TryGetValue(assignment.TicketKey, out var reference) || reference.Id != assignment.TicketId)
                        throw new InvalidDataException("Une proposition du scénario ne correspond pas à sa fiche GLPI.");
                break;
            case SharedTechniciansDocument technicians:
                if (technicians.Technicians is null || technicians.Technicians.Count is < 1 or > TechnicianPreferences.MaximumTechnicians)
                    throw new InvalidDataException("La liste des compétences et capacités est vide ou trop longue.");
                foreach (var pair in technicians.Technicians)
                {
                    if (pair.Value is null || pair.Key != TechnicianDashboardService.IdentityKey(pair.Key))
                        throw new InvalidDataException("Une identité de technicien partagée est invalide.");
                    _ = new TechnicianPreferences().With(pair.Key, pair.Value.ToPreference());
                }
                break;
            default: throw new InvalidDataException("Type de document métier non reconnu.");
        }
    }
    private static void ValidateReference(TicketReference? ticket, string installation)
    {
        if (ticket is null) throw new InvalidDataException("Référence de ticket absente.");
        _ = TicketLibraryRules.Key(ticket);
        if (ticket.Url.Length > 8192 || Installation(ticket.Url) != installation || PublicReference(ticket).Url != ticket.Url)
            throw new InvalidDataException("La fiche de ticket ne correspond pas à l’installation annoncée ou contient des paramètres non partagés.");
    }
    private static void ValidateInstallation(string? value)
    {
        if (value is null || value.Length is 0 or > 1000 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || ObjectIdentity.Installation(value + "/front/ticket.php") != value)
            throw new InvalidDataException("L’installation du document métier est invalide.");
    }
    private static bool ValidId(string id) => id.Length is >= 1 and <= 20 && id.All(char.IsAsciiDigit) && id.TrimStart('0').Length != 0;
    private static bool ValidText(string? text, int limit, bool multiline) => text is not null && text.Length <= limit
        && !text.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t'));
    private static void CheckSize(JsonElement payload, int limit)
    {
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > limit) throw new InvalidDataException("Le document métier partagé dépasse la taille autorisée.");
    }
}
