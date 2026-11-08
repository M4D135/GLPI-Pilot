using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

/// <summary>Operational data, separate from deployment configuration and generic catalogue defaults.</summary>
public sealed class FieldPreparationDocument
{
    public string Schema { get; set; } = "glpi-pilot.field-preparation";
    public int Format { get; set; } = 1;
    public List<string> ResolvedConflictKeys { get; set; } = new();
    public FieldPreparationPlan Plan { get; set; } = new();
    public List<FieldTicketDetail> Tickets { get; set; } = new();
    public List<string> ParticipantLabels { get; set; } = new();
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string SourceStamp { get; set; } = "";
}

public sealed class FieldCatalogueDocument
{
    public string Schema { get; set; } = "glpi-pilot.field-catalogue";
    public int Format { get; set; } = 1;
    public List<string> ResolvedConflictKeys { get; set; } = new();
    public FieldMaterialCatalogue Catalogue { get; set; } = new();
}

public static class FieldPreparationDocuments
{
    public const string PlanKind = "field-preparation", CatalogueKind = "field-catalogue";
    public const int MaximumPayloadBytes = 2 * 1024 * 1024;
    public static string CatalogueId { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("glpi-pilot.field-catalogue.v1")))[..32].ToLowerInvariant();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 24,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static bool IsKind(string kind) => kind is PlanKind or CatalogueKind;

    public static TeamWorkspaceItem ForPlan(FieldPreparationDocument document, string author)
    {
        Validate(document);
        return new TeamWorkspaceItem { Id = document.Plan.Id, Kind = PlanKind, Title = document.Plan.Title,
            Author = author, Status = document.Plan.State == "planned" ? "open" : "done", Payload = Payload(document) };
    }
    public static TeamWorkspaceItem ForCatalogue(FieldMaterialCatalogue catalogue, string author)
    {
        FieldPreparationService.Validate(catalogue);
        return new TeamWorkspaceItem { Id = CatalogueId, Kind = CatalogueKind, Title = "Catalogue de préparation terrain",
            Author = author, Payload = Payload(new FieldCatalogueDocument { Catalogue = catalogue }) };
    }
    private static JsonElement Payload<T>(T document)
    {
        var result = JsonSerializer.SerializeToElement(document, Json); CheckSize(result); return result;
    }
    private static T Read<T>(TeamWorkspaceItem item, string kind)
    {
        if (item.Kind != kind || item.Payload is not { ValueKind: JsonValueKind.Object } payload)
            throw new InvalidDataException("Document de préparation absent ou de type incorrect.");
        CheckSize(payload);
        try { return payload.Deserialize<T>(Json) ?? throw new InvalidDataException("Document de préparation vide."); }
        catch (JsonException) { throw new InvalidDataException("Le document de préparation contient une version ou des champs illisibles."); }
    }
    public static FieldPreparationDocument ReadPlan(TeamWorkspaceItem item)
    {
        var value = Read<FieldPreparationDocument>(item, PlanKind); Validate(value);
        if (item.Id != value.Plan.Id || item.Title != value.Plan.Title || item.Body.Length != 0 || item.TicketKey.Length != 0
            || item.Assignee.Length != 0 || item.Status != (value.Plan.State == "planned" ? "open" : "done"))
            throw new InvalidDataException("La préparation ne correspond pas à son identité ou à son état annoncé.");
        return value;
    }
    public static FieldMaterialCatalogue ReadCatalogue(TeamWorkspaceItem item)
    {
        var value = Read<FieldCatalogueDocument>(item, CatalogueKind);
        if (value.Schema != "glpi-pilot.field-catalogue" || value.Format != 1 || item.Id != CatalogueId
            || item.Body.Length != 0 || item.TicketKey.Length != 0 || item.Assignee.Length != 0 || item.Status != "open")
            throw new InvalidDataException("Identité ou version du catalogue matériel invalide.");
        FieldPreparationService.Validate(value.Catalogue); return value.Catalogue;
    }
    public static void Validate(TeamWorkspaceItem item)
    {
        _ = FieldResolutionKeys.Read(item);
        if (item.Kind == PlanKind) _ = ReadPlan(item);
        else if (item.Kind == CatalogueKind) _ = ReadCatalogue(item);
        else throw new InvalidDataException("Type de préparation inconnu.");
    }
    public static void Validate(FieldPreparationDocument value)
    {
        if (value is null || value.Schema != "glpi-pilot.field-preparation" || value.Format != 1)
            throw new InvalidDataException("Version de préparation inconnue.");
        FieldPreparationService.Validate(value.Plan);
        if (value.Tickets is null || value.Tickets.Count > 200 || value.ParticipantLabels is null || value.ParticipantLabels.Count > 200
            || value.SourceStamp is null || value.SourceStamp.Length > 128 || value.SourceStamp.Any(c => !char.IsAsciiHexDigit(c))
            || value.ObservedAtUtc == default)
            throw new InvalidDataException("Contexte de préparation absent ou trop volumineux.");
        foreach (var label in value.ParticipantLabels) Text(label, 400);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ticket in value.Tickets)
        {
            if (ticket is null) throw new InvalidDataException("Détail de ticket absent.");
            Text(ticket.Key, 300, true); Text(ticket.Id, 100, true); Text(ticket.Title, 2000);
            Text(ticket.Description, 200000); Text(ticket.Status, 300); Text(ticket.Type, 300); Text(ticket.Requester, 2000);
            if (!keys.Add(ticket.Key)) throw new InvalidDataException("Un ticket est répété dans la fiche.");
        }
        if (value.Plan.Owner.Kind == "ticket" && (value.Tickets.Count != 1 || value.Tickets[0].Key != value.Plan.Owner.Key))
            throw new InvalidDataException("Le détail ne correspond pas au ticket de cette préparation.");
    }
    private static void Text(string? text, int maximum, bool required = false)
    {
        if (text is null || text.Length > maximum || text.Contains('\0') || required && string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Un texte de la fiche est absent ou trop long.");
    }
    private static void CheckSize(JsonElement value)
    {
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumPayloadBytes)
            throw new InvalidDataException("La fiche de préparation dépasse 2 Mio ; réduisez les détails conservés.");
    }
    public static void ValidateCatalogueChange(FieldMaterialCatalogue previous, FieldMaterialCatalogue next)
    {
        FieldPreparationService.Validate(previous); FieldPreparationService.Validate(next);
        if (previous.Categories.Any(old => next.Categories.All(value => value.Id != old.Id))
            || previous.References.Any(old => next.References.All(value => value.Id != old.Id)))
            throw new InvalidDataException("Archivez une catégorie ou une référence au lieu de la supprimer : les préparations conservent leur historique.");
    }
    /// <summary>Snapshots of old lines are immutable. New lines must be selected from the current, active catalogue.</summary>
    public static void ValidateMaterialChange(FieldPreparationPlan? previous, FieldPreparationPlan next, FieldMaterialCatalogue catalogue)
    {
        FieldPreparationService.Validate(next); FieldPreparationService.Validate(catalogue);
        if (previous is not null && (previous.Id != next.Id || previous.Owner != next.Owner))
            throw new InvalidDataException("Une préparation ne peut pas changer de ticket ou de visite.");
        var oldLines = previous?.Lines.ToDictionary(line => line.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, FieldMaterialLine>(StringComparer.Ordinal);
        foreach (var line in next.Lines)
        {
            if (oldLines.TryGetValue(line.Id, out var old))
            {
                if (line.ReferenceId != old.ReferenceId || line.Code != old.Code || line.Label != old.Label || line.Category != old.Category || line.Unit != old.Unit)
                    throw new InvalidDataException("L’identité et les libellés historiques d’une ligne ne peuvent pas être remplacés.");
                continue;
            }
            var reference = catalogue.References.SingleOrDefault(value => value.Id == line.ReferenceId);
            var category = reference is null ? null : catalogue.Categories.Single(value => value.Id == reference.CategoryId);
            if (reference is null || reference.Archived || category!.Archived || line.Code != reference.Code || line.Label != reference.Label
                || line.Category != category.Name || line.Unit != reference.Unit)
                throw new InvalidDataException("Le catalogue a changé. Relisez la référence avant d’ajouter cette ligne ; votre saisie reste ouverte.");
        }
    }
}
