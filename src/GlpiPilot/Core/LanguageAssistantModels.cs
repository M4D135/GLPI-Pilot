using System.IO;
using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

public sealed class LanguageAssistantSettings
{
    public int Version { get; set; } = 1;
    public string Provider { get; set; } = "local";
    public bool ExternalEnabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaximumTickets { get; set; } = 50;
    public int TimeoutSeconds { get; set; } = 90;
    public string ProtectedApiKey { get; set; } = "";
    public string KeyEndpoint { get; set; } = "";
    public LanguageAssistantSettings Copy() => (LanguageAssistantSettings)MemberwiseClone();
    public void Validate()
    {
        if (Version != 1 || Provider is not ("local" or "compatible") || MaximumTickets is < 1 or > 200 || TimeoutSeconds is < 10 or > 180
            || Endpoint is null || Endpoint.Length > 2000 || Model is null || Model.Length > 120 || Model.Any(char.IsControl)
            || ProtectedApiKey is null || ProtectedApiKey.Length > 32768 || KeyEndpoint is null || KeyEndpoint.Length > 2000)
            throw new InvalidDataException("Configuration d’assistance invalide : moteur, limite 1–200 tickets et délai 10–180 s requis.");
        if (Endpoint.Length > 0) ValidateEndpoint(Endpoint);
        if (Provider == "compatible" && (Endpoint.Length == 0 || string.IsNullOrWhiteSpace(Model))) throw new InvalidDataException("Renseignez l’adresse complète /chat/completions et le nom du modèle du fournisseur choisi.");
    }
    public static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0
            || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidDataException("L’endpoint doit utiliser HTTPS, ou HTTP sur la boucle locale, sans identifiants, paramètres ni fragment dans l’URL.");
        return uri;
    }
}
public sealed record LanguageTicket(string Id, IReadOnlyDictionary<string, string> Fields, int SourceRow);
public sealed class LanguageAssistantContext
{
    public DateTimeOffset ExtractedAt { get; init; }
    public string Scope { get; init; } = "";
    public bool Complete { get; init; }
    public int ScopeRows { get; init; }
    public IReadOnlyList<QueryChoice> Fields { get; init; } = Array.Empty<QueryChoice>();
    public IReadOnlyList<LanguageTicket> Tickets { get; init; } = Array.Empty<LanguageTicket>();
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();
}
public sealed class LanguageCitation
{
    [JsonRequired] public string TicketId { get; set; } = "";
    [JsonRequired] public string Field { get; set; } = "";
    [JsonRequired] public string Quote { get; set; } = "";
}
public sealed class LanguageClaim
{
    [JsonRequired] public string Text { get; set; } = "";
    [JsonRequired] public List<LanguageCitation> Citations { get; set; } = new();
}
public sealed class LanguageSummary
{
    [JsonRequired] public string Title { get; set; } = "";
    [JsonRequired] public List<LanguageClaim> Claims { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
}
public sealed record LanguageQueryDraft(SavedQuery Query, string Explanation, IReadOnlyList<string> Assumptions);
public sealed record LanguageAssistantRequest(string Operation, string Prompt, LanguageAssistantSettings Settings, LanguageAssistantContext Context, string SystemMessage, string UserMessage, string Preview);
