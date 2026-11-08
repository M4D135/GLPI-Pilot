using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>Canonical calendar documents use the existing revisioned team outbox, without copying appointments on reception.</summary>
public static class CalendarWorkspaceDocuments
{
    public const string InterventionKind = "calendar-intervention";
    public const string ResourceKind = "calendar-resource";
    public static bool IsKind(string kind) => kind is InterventionKind or ResourceKind;
    public static string ResourceId(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ResourceKind + ":" + key))).ToLowerInvariant()[..32];
    public static void Validate(TeamWorkspaceItem item)
    {
        if (item.Payload is not { ValueKind: JsonValueKind.Object }) throw new InvalidDataException("Document de calendrier absent ou illisible.");
        if (item.Kind == InterventionKind)
        {
            var value = ReadIntervention(item); InterventionCalendarService.Validate(value);
            if (value.Id != item.Id) throw new InvalidDataException("L’identité du rendez-vous ne correspond pas à celle de son document partagé.");
        }
        else if (item.Kind == ResourceKind)
        {
            var value = ReadResource(item); InterventionCalendarService.ValidateResource(value);
            if (ResourceId(value.Key) != item.Id) throw new InvalidDataException("L’identité du participant a changé ; créez une nouvelle ressource pour une autre personne.");
        }
        else throw new InvalidDataException("Type de document de calendrier inconnu.");
    }
    public static CalendarIntervention ReadIntervention(TeamWorkspaceItem item)
    {
        if (item.Kind != InterventionKind || item.Payload is null) throw new InvalidDataException("Intervention introuvable.");
        var value = item.Payload.Value.Deserialize<CalendarIntervention>(TeamWorkspaceCodec.Json) ?? throw new InvalidDataException("Intervention illisible.");
        value.Revision = item.Revision; return value;
    }
    public static CalendarResource ReadResource(TeamWorkspaceItem item)
    {
        if (item.Kind != ResourceKind || item.Payload is null) throw new InvalidDataException("Ressource de calendrier introuvable.");
        return item.Payload.Value.Deserialize<CalendarResource>(TeamWorkspaceCodec.Json) ?? throw new InvalidDataException("Ressource illisible.");
    }
    public static TeamWorkspaceItem ForIntervention(CalendarIntervention value, string author) => new()
    {
        Id = value.Id, Kind = InterventionKind, Title = value.Title, Author = author,
        Status = value.State is "done" or "cancelled" ? "done" : "open", Body = value.Notes,
        Payload = JsonSerializer.SerializeToElement(value, TeamWorkspaceCodec.Json)
    };
    public static TeamWorkspaceItem ForResource(CalendarResource value, string author) => new()
    {
        Id = ResourceId(value.Key), Kind = ResourceKind, Title = value.Label, Author = author,
        Status = value.Active ? "open" : "done", Payload = JsonSerializer.SerializeToElement(value, TeamWorkspaceCodec.Json)
    };
}
