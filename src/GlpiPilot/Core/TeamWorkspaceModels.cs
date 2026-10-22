using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

/// <summary>Public, versioned business document; local UI preferences and credentials are never shared here.</summary>
public sealed class TeamWorkspaceItem
{
    public int Format { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "note";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string TicketKey { get; set; } = "";
    public string Assignee { get; set; } = "";
    public string Status { get; set; } = "open";
    public string Author { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Deleted { get; set; }
    public JsonElement? Payload { get; set; }
    public long Revision { get; set; }
    public long CentralRevision { get; set; }
    public bool Pending { get; set; }
    public int Conflicts { get; set; }
    public TeamWorkspaceItem Copy() => TeamWorkspaceCodec.Read(TeamWorkspaceCodec.Write(this));
}
public sealed record TeamWorkspaceList(IReadOnlyList<TeamWorkspaceItem> Items, long Total);
public sealed record TeamWorkspaceConflict(string Id, string ItemId, string Reason, DateTimeOffset RecordedAt, TeamWorkspaceItem Current, TeamWorkspaceItem Incoming);
public sealed record TeamWorkspaceMutation(string MutationId, long BaseRevision, TeamWorkspaceItem Item);
public sealed record TeamWorkspaceReceipt(string Outcome, TeamWorkspaceItem Current);
public sealed record TeamWorkspaceChange(long Sequence, TeamWorkspaceItem Item, TeamWorkspaceItem? Incoming = null, string Reason = "");
public sealed record TeamWorkspaceSyncResult(int Pushed, int Pulled, int Conflicts, long Pending);
public sealed record TeamWorkspaceCounts(long Items, long Pending, long Conflicts);

public static class TeamWorkspaceCodec
{
    public const int MaximumBytes = 18 * 1024 * 1024;
    public const int MaximumBatchBytes = 32 * 1024 * 1024;
    public static JsonSerializerOptions Json { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, MaxDepth = 32 };
    public static string Write(TeamWorkspaceItem item) { Validate(item); var text = JsonSerializer.Serialize(item, Json); if (System.Text.Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new InvalidDataException("La contribution d’équipe dépasse 18 Mio."); return text; }
    public static TeamWorkspaceItem Read(string json)
    {
        if (json.Length > MaximumBytes || System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Contribution d’équipe trop volumineuse.");
        TeamWorkspaceItem item;
        try { item = JsonSerializer.Deserialize<TeamWorkspaceItem>(json, Json) ?? throw new InvalidDataException("Contribution d’équipe vide."); }
        catch (JsonException) { throw new InvalidDataException("Contribution d’équipe illisible."); }
        Validate(item); return item;
    }
    public static void Validate(TeamWorkspaceItem item)
    {
        if (item.Format != 1 || !Regex.IsMatch(item.Id ?? "", "^[a-f0-9]{32}$", RegexOptions.CultureInvariant)
            || item.Kind is not ("comment" or "knowledge" or "assignment" or "note" or "configuration" or "statistics") && !TeamWorkspaceBusinessDocuments.IsBusinessKind(item.Kind)
            || item.Status is not ("open" or "done" or "draft" or "published") || item.Revision < 0 || item.CentralRevision < 0)
            throw new InvalidDataException("Identité, type ou version de contribution invalide.");
        foreach (var (value, maximum) in new[] { (item.Title, 300), (item.Body, 200000), (item.TicketKey, 200), (item.Assignee, 200), (item.Author, 120) })
            if (value is null || value.Length > maximum || value.Contains('\0')) throw new InvalidDataException("Texte de contribution invalide ou trop long.");
        if (string.IsNullOrWhiteSpace(item.Title)) throw new InvalidDataException("Le titre de la contribution est obligatoire.");
        if (TeamWorkspaceBusinessDocuments.IsBusinessKind(item.Kind)) _ = TeamWorkspaceBusinessDocuments.Read(item);
        else if (item.Kind is not ("configuration" or "statistics") && item.Payload is not null) throw new InvalidDataException("Ce type de contribution n’accepte pas de données de configuration.");
    }
    // Editing time is descriptive. Concurrency uses server revisions, never clocks on different workstations.
    public static string ContentHash(TeamWorkspaceItem item) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    { item.Format, item.Id, item.Kind, item.Title, item.Body, item.TicketKey, item.Assignee, item.Status, item.Author, item.Deleted, item.Payload }, Json))).ToLowerInvariant();
    public static string ConflictKey(TeamWorkspaceItem current, TeamWorkspaceItem incoming)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(current.Id + ":" + ContentHash(current) + ":" + ContentHash(incoming)))).ToLowerInvariant();
}
