using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace GlpiPilot.Core;

/// <summary>Canonical team state. Credentials, browser state and personal preferences have no place in this envelope.</summary>
public sealed class SharedBusinessDocument
{
    public string Schema { get; set; } = "glpi-pilot.shared-business";
    public int Version { get; set; } = 1;
    public string Key { get; set; } = "";
    public JsonElement Data { get; set; }
    public List<string> AcknowledgedConflicts { get; set; } = new();
}

public sealed class SharedBusinessConfiguration
{
    public DashboardSettings Dashboard { get; set; } = new();
    public CitySettings Cities { get; set; } = new();
    public QueryLibrarySettings Queries { get; set; } = new();
}

public sealed class SharedBusinessDataset
{
    public string SourceKey { get; set; } = "";
    public string UrlKey { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
    public int Rows { get; set; }
    public int Columns { get; set; }
    public int ExpandedBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string GzipBase64 { get; set; } = "";
}

public sealed class SharedBusinessDatasetContent
{
    public ExtractionResult Table { get; set; } = new();
    public Dictionary<string, string> ColumnMappings { get; set; } = new();
}

public static class SharedBusinessDocuments
{
    public const int MaximumDatasetBytes = 64 * 1024 * 1024;
    public const int MaximumCompressedBytes = 12 * 1024 * 1024;
    public static readonly string[] Kinds = { "team-configuration", "team-tour", "team-technician", "team-dispatch", "team-dataset", "team-location", "team-order" };
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        MaxDepth = 30, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static bool IsKind(string kind) => Kinds.Contains(kind, StringComparer.Ordinal);
    public static string Id(string kind, string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("shared-business-v1\n" + kind + "\n" + key)))[..32].ToLowerInvariant();
    public static string Hash(JsonElement data)
    {
        // Persistence may change JSON whitespace and escape spelling. Those are
        // not edits: compare the same decoded fields after every round trip.
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory)) Write(data, writer);
        return Convert.ToHexString(SHA256.HashData(memory.GetBuffer().AsSpan(0, checked((int)memory.Length)))).ToLowerInvariant();
        static void Write(JsonElement value, Utf8JsonWriter writer)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                    { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
                    writer.WriteEndObject(); break;
                case JsonValueKind.Array:
                    writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, writer); writer.WriteEndArray(); break;
                case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
                case JsonValueKind.Number:
                    if (value.TryGetDecimal(out var number)) writer.WriteRawValue(number.ToString("G29", CultureInfo.InvariantCulture));
                    else writer.WriteNumberValue(value.GetDouble());
                    break;
                case JsonValueKind.True: writer.WriteBooleanValue(true); break;
                case JsonValueKind.False: writer.WriteBooleanValue(false); break;
                case JsonValueKind.Null: writer.WriteNullValue(); break;
                default: throw new InvalidDataException("Valeur commune absente.");
            }
        }
    }
    public static string Label(string kind) => kind switch
    {
        "team-configuration" => "Sites, règles et tableaux de bord", "team-tour" => "Tournée commune",
        "team-technician" => "Compétences et capacités", "team-dispatch" => "Répartition commune",
        "team-dataset" => "Relevé partagé", "team-location" => "Localisation corrigée", "team-order" => "Ordre de tournée", _ => "Données communes"
    };
    public static TeamWorkspaceItem Create(string kind, string key, string title, object data, string author = "")
    {
        var document = new SharedBusinessDocument { Key = key, Data = JsonSerializer.SerializeToElement(data, Json) };
        var item = new TeamWorkspaceItem { Id = Id(kind, key), Kind = kind, Title = title, Status = "published", Author = author,
            Payload = JsonSerializer.SerializeToElement(document, Json) };
        Validate(item); return item;
    }
    public static SharedBusinessDocument Read(TeamWorkspaceItem item)
    {
        if (!IsKind(item.Kind) || item.Payload is not { ValueKind: JsonValueKind.Object } payload)
            throw new InvalidDataException("Le document commun est absent ou son type est inconnu.");
        SharedBusinessDocument value;
        try { value = payload.Deserialize<SharedBusinessDocument>(Json) ?? throw new InvalidDataException("Document commun vide."); }
        catch (JsonException) { throw new InvalidDataException("Document commun illisible ou champs inconnus."); }
        if (value.Schema != "glpi-pilot.shared-business" || value.Version != 1 || value.Key is null || value.Key.Length is < 1 or > 1000
            || value.Key.Any(char.IsControl) || item.Id != Id(item.Kind, value.Key) || value.Data.ValueKind != JsonValueKind.Object
            || value.AcknowledgedConflicts is null || value.AcknowledgedConflicts.Count > 1000
            || value.AcknowledgedConflicts.Any(x => x is null || x.Length != 64 || !x.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Identité ou version du document commun invalide.");
        return value;
    }
    public static T Data<T>(TeamWorkspaceItem item)
    {
        try { return Read(item).Data.Deserialize<T>(Json) ?? throw new InvalidDataException("Données communes vides."); }
        catch (JsonException) { throw new InvalidDataException("Les champs du document commun ne sont pas valides."); }
    }
    public static void Validate(TeamWorkspaceItem item)
    {
        var envelope = Read(item);
        switch (item.Kind)
        {
            case "team-configuration":
                if (envelope.Key != "common") throw new InvalidDataException("Référence de configuration commune invalide.");
                var configuration = Data<SharedBusinessConfiguration>(item);
                if (configuration.Dashboard is null || configuration.Cities is null || configuration.Queries is null
                    || configuration.Dashboard.PersonalTechnician != "" || configuration.Dashboard.OpenPersonalAfterExtraction)
                    throw new InvalidDataException("La configuration commune ne peut pas définir une identité personnelle.");
                DashboardService.ValidateSettings(configuration.Dashboard); configuration.Cities.Validate(); configuration.Queries.Validate();
                break;
            case "team-tour":
                var tour = Data<ScheduledTour>(item); tour.Validate();
                if (envelope.Key != tour.Id) throw new InvalidDataException("Identité de tournée commune incohérente.");
                break;
            case "team-technician":
                if (envelope.Key != TechnicianDashboardService.IdentityKey(envelope.Key)) throw new InvalidDataException("Identité de technicien commune invalide.");
                _ = new TechnicianPreferences().With(envelope.Key, Data<SharedTechnicianBusiness>(item).ToPreference());
                break;
            case "team-dispatch":
                if (envelope.Key != "common") throw new InvalidDataException("Référence de répartition commune invalide.");
                TechnicianPlanningService.Validate(Data<DispatchScenario>(item));
                break;
            case "team-location":
                var location = Data<CityTicketOverride>(item); new CityOverrideStore { Overrides = new() { location } }.Validate();
                if (envelope.Key != LocationKey(location)) throw new InvalidDataException("Référence de localisation commune incohérente.");
                break;
            case "team-order":
                var order = Data<TourOrderProfile>(item);
                if (envelope.Key.Length > 450 || order.CityIds is null || order.CityIds.Length > TourOrderPreferences.MaximumCitiesPerProfile
                    || order.CityIds.Any(id => id is null || id.Length is < 1 or > 128 || id.Any(char.IsControl)))
                    throw new InvalidDataException("Ordre de tournée commun invalide.");
                break;
            case "team-dataset":
                var dataset = Data<SharedBusinessDataset>(item);
                if (dataset.SourceKey is null || dataset.SourceKey.Length != 64 || !dataset.SourceKey.All(Uri.IsHexDigit) || envelope.Key != dataset.SourceKey
                    || dataset.UrlKey is null || dataset.UrlKey.Length != 64 || !dataset.UrlKey.All(Uri.IsHexDigit)
                    || dataset.Title is null || dataset.Title.Length > 300 || dataset.Rows is < 0 or > 1_000_000 || dataset.Columns is < 1 or > 512
                    || dataset.ExpandedBytes is < 1 or > MaximumDatasetBytes || dataset.Sha256 is null || dataset.Sha256.Length != 64 || !dataset.Sha256.All(Uri.IsHexDigit)
                    || dataset.GzipBase64 is null || dataset.GzipBase64.Length is < 4 or > (MaximumCompressedBytes / 3 * 4 + 4))
                    throw new InvalidDataException("Le relevé partagé dépasse les limites ou ses métadonnées sont invalides.");
                break;
        }
    }
    public static SharedBusinessConfiguration Configuration(AppSettings settings)
    {
        var dashboard = settings.Dashboard.Copy();
        dashboard.PersonalTechnician = ""; dashboard.OpenPersonalAfterExtraction = false;
        // The legacy personal layout is not transmitted. Its public placeholder is
        // deterministic, so another person's UI changes never enqueue a team edit.
        dashboard.PersonalLayout = dashboard.Layout.Copy();
        dashboard.Layout.CatalogFavorites.Clear(); dashboard.Layout.CatalogRecent.Clear();
        dashboard.PersonalLayout.CatalogFavorites.Clear(); dashboard.PersonalLayout.CatalogRecent.Clear();
        dashboard.Layout.ActiveBoardId = dashboard.Layout.Boards.FirstOrDefault()?.Id ?? "";
        dashboard.PersonalLayout.ActiveBoardId = dashboard.Layout.ActiveBoardId;
        return new() { Dashboard = dashboard, Cities = settings.Cities.Copy(), Queries = settings.Queries.Copy() };
    }
    public static bool IsDefaultConfiguration(AppSettings settings)
    {
        static string Shape(SharedBusinessConfiguration configuration)
        {
            var node = JsonSerializer.SerializeToNode(configuration, Json)!;
            void Walk(JsonNode? value)
            {
                if (value is JsonObject obj)
                    foreach (var pair in obj.ToArray())
                    { if (pair.Key is "id" or "activeBoardId") obj[pair.Key] = ""; else Walk(pair.Value); }
                else if (value is JsonArray array) foreach (var child in array) Walk(child);
            }
            Walk(node); return node.ToJsonString();
        }
        var defaults = new AppSettings(); defaults.Normalize();
        return Shape(Configuration(settings)) == Shape(Configuration(defaults));
    }
    public static SharedBusinessDataset Dataset(ExtractionResult source, DashboardSettings mapping)
    {
        // Source rows are data, not a browser session. URL query credentials,
        // machine paths, capture logs and browser profiles are never copied.
        var requested = source.Provenance.RequestedUrl;
        var useRequested = source.Provenance.SearchVerified && Uri.TryCreate(requested, UriKind.Absolute, out var requestedUri)
            && requestedUri.Scheme is "http" or "https" && requestedUri.UserInfo.Length == 0
            && ObjectIdentity.Installation(requested) == ObjectIdentity.Installation(source.SourceUrl);
        var url = PublicSourceUrl(useRequested ? requested : source.SourceUrl);
        var table = new ExtractionResult
        {
            Headers = source.Headers, Rows = source.Rows, SourceUrl = url,
            Title = source.Title.Length <= 300 ? source.Title : source.Title[..300], ExtractedAt = source.ExtractedAt,
            Pages = source.Pages, ExpectedTotal = source.ExpectedTotal, Scope = source.Scope, Complete = source.Complete,
            Status = "Relevé partagé · observation conservée", Warnings = source.Warnings.ToList(), Quality = source.Quality,
            Provenance = new() { RunId = source.Provenance.RunId, SourceKind = "local", Method = "shared-records", RequestedUrl = url,
                SearchVerified = source.Provenance.SearchVerified, ScopeVerified = source.Provenance.ScopeVerified,
                ScopeFingerprint = source.Provenance.ScopeFingerprint, StartedAt = source.Provenance.StartedAt, FinishedAt = source.Provenance.FinishedAt }
        };
        var content = new SharedBusinessDatasetContent { Table = table, ColumnMappings = new(mapping.ColumnMappings, StringComparer.OrdinalIgnoreCase) };
        ValidateDatasetContent(content);
        using var bounded = new BoundedDatasetStream(MaximumDatasetBytes, "Le relevé dépasse 64 Mio : il reste local ; partagez un périmètre plus réduit.");
        JsonSerializer.Serialize(bounded, content, Json); var bytes = bounded.ToArray();
        using var memory = new BoundedDatasetStream(MaximumCompressedBytes, "Le relevé dépasse 12 Mio compressés : il reste local ; partagez un périmètre plus réduit.");
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true)) gzip.Write(bytes);
        if (memory.Length > MaximumCompressedBytes) throw new InvalidDataException("Le relevé dépasse 12 Mio compressés : il reste local ; partagez un périmètre plus réduit.");
        return new() { SourceKey = DatasetKey(table), UrlKey = SnapshotStore.SourceKey(url), Title = table.Title, ObservedAt = source.ExtractedAt, Rows = table.Rows.Count,
            Columns = table.Headers.Count, ExpandedBytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), GzipBase64 = Convert.ToBase64String(memory.ToArray()) };
    }
    public static SharedBusinessDatasetContent OpenDataset(TeamWorkspaceItem item)
    {
        Validate(item); var dataset = Data<SharedBusinessDataset>(item); byte[] compressed;
        try { compressed = Convert.FromBase64String(dataset.GzipBase64); }
        catch (FormatException) { throw new InvalidDataException("Le relevé partagé n’est pas un document compressé valide."); }
        using var input = new MemoryStream(compressed); using var gzip = new GZipStream(input, CompressionMode.Decompress); using var output = new MemoryStream();
        var buffer = new byte[65536]; int count;
        while ((count = gzip.Read(buffer)) > 0)
        {
            if (output.Length + count > dataset.ExpandedBytes || output.Length + count > MaximumDatasetBytes) throw new InvalidDataException("Le relevé décompressé dépasse la taille annoncée.");
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        if (bytes.Length != dataset.ExpandedBytes || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), dataset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("L’empreinte du relevé partagé ne correspond pas à son contenu.");
        SharedBusinessDatasetContent content;
        try { content = JsonSerializer.Deserialize<SharedBusinessDatasetContent>(bytes, Json) ?? throw new InvalidDataException("Relevé partagé vide."); }
        catch (JsonException) { throw new InvalidDataException("Le tableau partagé contient des champs invalides."); }
        ValidateDatasetContent(content);
        if (content.Table.Rows.Count != dataset.Rows || content.Table.Headers.Count != dataset.Columns || SnapshotStore.SourceKey(content.Table.SourceUrl) != dataset.UrlKey || DatasetKey(content.Table) != dataset.SourceKey)
            throw new InvalidDataException("Le tableau ne correspond pas aux métadonnées du relevé partagé.");
        return content;
    }
    private static void ValidateDatasetContent(SharedBusinessDatasetContent content)
    {
        var table = content.Table;
        if (table is null || table.Headers is null || table.Headers.Count is < 1 or > 512 || table.Headers.Any(h => h is null || h.Length > 2000)
            || table.Rows is null || table.Rows.Count > 1_000_000 || table.Rows.Any(row => row is null || row.Length != table.Headers.Count || row.Any(cell => cell is null || cell.Length > 4_000_000))
            || table.Title is null || table.Title.Length > 300 || table.Scope is null || table.Scope.Length > 10000 || table.Warnings is null || table.Warnings.Count > 10000 || table.Warnings.Any(x => x is null || x.Length > 10000)
            || table.Provenance is null || table.Provenance.Method != "shared-records" || table.Provenance.SourceKind != "local"
            || table.Provenance.RunId is null || table.Provenance.RunId.Length is < 1 or > 200 || table.Provenance.ScopeFingerprint is null || table.Provenance.ScopeFingerprint.Length > 2000
            || content.ColumnMappings is null || content.ColumnMappings.Count > 100 || content.ColumnMappings.Any(p => p.Key.Length > 100 || p.Value is null || p.Value.Length > 2000)
            || table.SourceUrl != PublicSourceUrl(table.SourceUrl))
            throw new InvalidDataException("Le tableau commun, ses lignes ou ses correspondances sont invalides.");
    }
    public static string PublicSourceUrl(string url)
    {
        var safe = TargetUrlPolicy.Validate(url);
        if (safe.UserInfo.Length > 0) throw new InvalidDataException("L’adresse partagée ne doit pas contenir d’identifiants.");
        var credentials = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "code", "state", "session_state", "access_token", "id_token", "refresh_token", "SAMLResponse", "SAMLRequest", "RelayState", "wresult", "wctx", "client_secret",
          "password", "passwd", "pwd", "username", "login", "authorization", "cookie", "cookies", "token", "auth_token", "api_key", "apikey", "secret", "credential", "credentials", "PHPSESSID", "JSESSIONID", "sessionid", "session_id", "csrf", "csrftoken", "csrf_token", "_glpi_csrf_token", "glpi_csrf_token", "start" };
        var query = safe.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !credentials.Contains(Uri.UnescapeDataString(part.Split('=', 2)[0].Replace('+', ' '))))
            .OrderBy(part => part, StringComparer.Ordinal).ToArray();
        return safe.GetLeftPart(UriPartial.Path) + (query.Length == 0 ? "" : "?" + string.Join('&', query));
    }
    private static string DatasetKey(ExtractionResult table)
    {
        var source = table.Provenance;
        var scope = source.SearchVerified && source.ScopeVerified && source.ScopeFingerprint.Length > 0
            ? "scope:" + source.ScopeFingerprint : "observation:" + source.RunId;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SnapshotStore.SourceKey(table.SourceUrl) + "\n" + scope))).ToLowerInvariant();
    }
    private sealed class BoundedDatasetStream(int maximum, string message) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        { if (Position + count > maximum) throw new InvalidDataException(message); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { if (Position + buffer.Length > maximum) throw new InvalidDataException(message); base.Write(buffer); }
        public override void WriteByte(byte value)
        { if (Position + 1 > maximum) throw new InvalidDataException(message); base.WriteByte(value); }
        public override void SetLength(long value)
        { if (value > maximum) throw new InvalidDataException(message); base.SetLength(value); }
    }
    public static string LocationKey(CityTicketOverride value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.SourceKey + "\n" + value.TicketId))).ToLowerInvariant();
}
