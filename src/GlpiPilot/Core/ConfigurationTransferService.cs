using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

/// <summary>Imports/exports only a bounded, explicit allowlist of portable settings.</summary>
public static class ConfigurationTransferService
{
    public const string Schema = "glpi-pilot.configuration";
    public const int SchemaVersion = 1;
    public const string ApplicationVersion = "1.5.0";
    public const int MaximumFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, MaxDepth = 24 };
    private static readonly HashSet<string> Shareable = new(StringComparer.OrdinalIgnoreCase)
    {
        "PortalUrl", "BrowserChannel", "Theme", "AutoMode", "AutoSaveXlsx", "ReadySelector", "NextSelector", "SelectorOrigin",
        "TimeoutSeconds", "DownloadTimeoutSeconds", "MaxPages", "MaxRows", "ExpectedTotal", "TableSelector", "TableHeaderFingerprint",
        "TableFrameOrigin", "TableFrameName", "TableTargetUrl", "NativeSelectorOrigin", "NativeMenuSelector", "NativeFormatSelector",
        "NativeFormatValue", "NativeScopeSelector", "NativeScopeValue", "NativeSubmitSelector", "TicketDetails", "Dashboard", "Cities", "Queries"
    };
    private static readonly HashSet<string> TicketProperties = new(StringComparer.OrdinalIgnoreCase)
    { "AutoLoadOnSelection", "AutoReadTreatment", "IdColumn", "UrlTemplate", "IncludeAutoDetected", "MaxRowsPerSection", "Sections" };
    private static readonly HashSet<string> SectionProperties = new(StringComparer.OrdinalIgnoreCase) { "Name", "Selector", "Kind", "TabSelector" };
    private static readonly HashSet<string> DashboardProperties = new(StringComparer.OrdinalIgnoreCase)
    { "ColumnMappings", "StaleDays", "OldDays", "DueSoonHours", "DateCulture", "Layout", "PersonalLayout" };
    private static readonly HashSet<string> LayoutProperties = new(StringComparer.OrdinalIgnoreCase)
    { "Version", "ActiveBoardId", "Boards" };
    private static readonly HashSet<string> BoardProperties = new(StringComparer.OrdinalIgnoreCase)
    { "Id", "Name", "Widgets" };
    private static readonly HashSet<string> WidgetProperties = new(StringComparer.OrdinalIgnoreCase)
    { "Id", "Kind", "SourceKey", "Title", "Span", "Height", "AutoHeight", "Style", "Accent", "Presentation", "TopCount", "ShowDescription", "Hidden", "Locked", "Note", "TargetValue", "WarningValue", "CriticalValue", "LowerIsBetter" };
    private static readonly HashSet<string> MappingKeys = new(StringComparer.OrdinalIgnoreCase)
    { "id", "title", "status", "priority", "urgency", "impact", "type", "created", "updated", "updatedBy", "resolved", "closed", "due", "assignee", "group", "requester", "category", "entity", "location", "city", "postal", "source", "solution" };
    private static readonly HashSet<string> CitySettingsProperties = new(StringComparer.OrdinalIgnoreCase)
    { "Enabled", "TitleColumn", "Cities" };
    private static readonly HashSet<string> CityDefinitionProperties = new(StringComparer.OrdinalIgnoreCase)
    { "Id", "Name", "Aliases", "PostalCode", "Latitude", "Longitude" };
    private static readonly HashSet<string> QueryLibraryProperties = new(StringComparer.OrdinalIgnoreCase) { "Queries" };
    private static readonly HashSet<string> SavedQueryProperties = new(StringComparer.OrdinalIgnoreCase)
    { "Id", "Name", "Description", "Category", "BaseProbe", "Match", "Aggregate", "AggregateField", "GroupBy", "Favorite", "Conditions" };
    private static readonly HashSet<string> QueryConditionProperties = new(StringComparer.OrdinalIgnoreCase)
    { "Field", "Operator", "Value", "Value2" };
    private static readonly HashSet<string> CredentialParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "state", "session_state", "access_token", "id_token", "refresh_token", "SAMLResponse", "SAMLRequest", "RelayState",
        "wresult", "wctx", "client_secret", "password", "passwd", "pwd", "username", "login", "authorization", "cookie", "cookies",
        "token", "auth_token", "api_key", "apikey", "secret", "credential", "credentials", "PHPSESSID", "JSESSIONID", "sessionid", "session_id",
        "csrf", "csrftoken", "csrf_token", "_glpi_csrf_token", "glpi_csrf_token"
    };

    public static void Export(string path, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Cities?.Validate();
        settings.Queries?.Validate();
        var portable = settings.Copy();
        ValidateAndSanitize(portable);
        var element = JsonSerializer.SerializeToElement(portable, JsonOptions);
        using var content = new MemoryStream();
        using (var writer = new Utf8JsonWriter(content, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("Schema", Schema);
            writer.WriteNumber("SchemaVersion", SchemaVersion);
            writer.WriteString("AppVersion", ApplicationVersion);
            writer.WritePropertyName("Settings");
            WriteShareable(writer, element);
            writer.WriteEndObject();
        }
        if (content.Length > MaximumFileBytes) throw Invalid("La configuration portable dépasse la limite de 1 Mio.");
        WriteAtomically(path, content.ToArray());
    }

    public static AppSettings Import(string path, AppSettings current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(current);
        byte[] bytes = ReadBounded(path);
        try
        {
            ReadOnlyMemory<byte> payload = bytes;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) payload = payload[3..];
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 24 });
            JsonElement root = document.RootElement;
            RequireObject(root);
            bool envelope = Find(root, "Schema").HasValue || Find(root, "Settings").HasValue || Find(root, "SchemaVersion").HasValue;
            JsonElement settings;
            if (envelope)
            {
                RequireProperties(root, new(StringComparer.OrdinalIgnoreCase) { "Schema", "SchemaVersion", "AppVersion", "Settings" });
                if (Find(root, "Schema") is not { ValueKind: JsonValueKind.String } schema || schema.GetString() != Schema ||
                    Find(root, "SchemaVersion") is not { ValueKind: JsonValueKind.Number } schemaVersion || !schemaVersion.TryGetInt32(out int version) || version != SchemaVersion)
                    throw Invalid("Le format ou la version du schéma de configuration n’est pas pris en charge.");
                if (Find(root, "AppVersion") is not { ValueKind: JsonValueKind.String } appVersion ||
                    !Version.TryParse(appVersion.GetString(), out var app) || app.Major != 1 || app.Minor is < 0 or > 5)
                    throw Invalid("Cette configuration provient d’une version de l’application non prise en charge.");
                settings = Find(root, "Settings") ?? throw Invalid("La configuration ne contient aucun réglage.");
                RequireProperties(settings, Shareable);
            }
            else
            {
                // Older local settings.json files can be migrated explicitly, but their
                // local folder never replaces the destination machine's folder.
                var legacyProperties = new HashSet<string>(Shareable, StringComparer.OrdinalIgnoreCase) { "ExportFolder", "InterfaceZoomPercent" };
                RequireProperties(root, legacyProperties);
                if (!root.EnumerateObject().Any(property => Shareable.Contains(property.Name)))
                    throw Invalid("Le fichier ne contient pas de configuration GLPI Pilot reconnue.");
                settings = root;
            }
            ValidateNestedProperties(settings, allowLocalPreferences: !envelope);
            using var filtered = new MemoryStream();
            using (var writer = new Utf8JsonWriter(filtered)) WriteShareable(writer, settings);
            var imported = JsonSerializer.Deserialize<AppSettings>(filtered.ToArray(), JsonOptions)
                ?? throw Invalid("Les réglages de configuration sont absents.");
            imported.ExportFolder = current.ExportFolder;
            imported.InterfaceZoomPercent = current.InterfaceZoomPercent;
            imported.Dashboard ??= new();
            imported.Dashboard.PersonalTechnician = current.Dashboard?.PersonalTechnician ?? "";
            imported.Dashboard.OpenPersonalAfterExtraction = current.Dashboard?.OpenPersonalAfterExtraction ?? false;
            imported.Normalize();
            ValidateAndSanitize(imported);
            return imported;
        }
        catch (JsonException)
        {
            // Parser exceptions can contain property names supplied by the file.
            throw Invalid("Le fichier n’est pas un JSON de configuration valide, ou une valeur possède un type incorrect.");
        }
        catch (DecoderFallbackException)
        {
            throw Invalid("La configuration doit être un fichier JSON UTF-8 valide.");
        }
    }

    private static void ValidateAndSanitize(AppSettings settings)
    {
        settings.Normalize();
        settings.BrowserChannel = EnumValue(settings.BrowserChannel, "Navigateur", "msedge", "chrome");
        settings.Theme = EnumValue(settings.Theme, "Thème", "dark", "light");
        settings.AutoMode = EnumValue(settings.AutoMode, "Méthode d’extraction", "native", "pagination");
        Range(settings.TimeoutSeconds, 1, 1800, "Délai SSO");
        Range(settings.DownloadTimeoutSeconds, 1, 7200, "Délai de téléchargement");
        Range(settings.MaxPages, 1, 10000, "Nombre maximal de pages");
        Range(settings.MaxRows, 1, 1_000_000, "Nombre maximal de lignes");
        if (settings.ExpectedTotal.HasValue) Range(settings.ExpectedTotal.Value, 0, 10_000_000, "Total attendu");
        settings.PortalUrl = SharedUrl(settings.PortalUrl);
        settings.TableTargetUrl = SharedUrl(settings.TableTargetUrl);
        settings.SelectorOrigin = SharedOrigin(settings.SelectorOrigin);
        settings.TableFrameOrigin = SharedOrigin(settings.TableFrameOrigin);
        settings.NativeSelectorOrigin = SharedOrigin(settings.NativeSelectorOrigin);
        foreach (string selector in new[] { settings.ReadySelector, settings.NextSelector, settings.TableSelector,
            settings.NativeMenuSelector, settings.NativeFormatSelector, settings.NativeScopeSelector, settings.NativeSubmitSelector }) ValidateCss(selector);
        Text(settings.TableHeaderFingerprint, 8192, "Empreinte du tableau");
        Text(settings.TableFrameName, 512, "Nom du cadre");
        Text(settings.NativeFormatValue, 512, "Valeur du format d’export");
        Text(settings.NativeScopeValue, 512, "Valeur du périmètre d’export");

        var details = settings.TicketDetails;
        Text(details.IdColumn, 512, "Nom de la colonne identifiant");
        Range(details.MaxRowsPerSection, 1, 100000, "Lignes par section de ticket");
        if (details.Sections.Count > 20) throw Invalid("La configuration accepte au maximum 20 sections de ticket.");
        foreach (var section in details.Sections)
        {
            Text(section.Name, 120, "Nom de section");
            ValidateCss(section.Selector);
            ValidateCss(section.TabSelector);
            if (string.IsNullOrWhiteSpace(section.Selector)) throw Invalid("Chaque section de ticket configurée doit posséder un sélecteur CSS.");
            section.Kind = EnumValue(section.Kind, "Type de section", "table", "fields", "text", "timeline");
        }
        if (details.UrlTemplate.Length > 0)
        {
            string marker = "GLPITICKETPLACEHOLDER" + Guid.NewGuid().ToString("N");
            string clean = SharedUrl(details.UrlTemplate.Replace("{id}", marker, StringComparison.Ordinal))
                .Replace(marker, "{id}", StringComparison.Ordinal);
            try { details.UrlTemplate = TicketReferenceResolver.ValidateTemplate(clean, settings.PortalUrl); }
            catch (InvalidOperationException ex) { throw Invalid(ex.Message); }
        }

        var dashboard = settings.Dashboard;
        Range(dashboard.StaleDays, 1, 3650, "Ancienneté sans mise à jour");
        Range(dashboard.OldDays, 1, 3650, "Ancienneté des tickets");
        if (dashboard.OldDays < dashboard.StaleDays) throw Invalid("Le seuil des tickets anciens doit être supérieur ou égal au seuil sans mise à jour.");
        Range(dashboard.DueSoonHours, 1, 720, "Échéance proche en heures");
        dashboard.DateCulture = EnumValue(dashboard.DateCulture, "Culture des dates", "fr-FR", "en-GB", "en-US", "invariant");
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in dashboard.ColumnMappings)
        {
            if (!MappingKeys.Contains(pair.Key)) throw Invalid("Une clé de correspondance du tableau de bord n’est pas reconnue.");
            Text(pair.Value, 512, "Nom de colonne du tableau de bord");
            string canonicalKey = MappingKeys.First(key => key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            mappings.Add(canonicalKey, pair.Value);
        }
        dashboard.ColumnMappings = mappings;
        dashboard.Layout.Validate();
        ValidateLayoutJson(JsonSerializer.SerializeToElement(dashboard.Layout, JsonOptions));
        dashboard.PersonalLayout.Validate();
        ValidateLayoutJson(JsonSerializer.SerializeToElement(dashboard.PersonalLayout, JsonOptions));
        settings.Cities.Validate();
        settings.Queries.Validate();
    }

    private static string SharedUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            _ = TicketReferenceResolver.ReadHttpUrl(value, "L’adresse de configuration");
            string preserved = TargetUrlPolicy.PersistedTarget(value);
            var uri = new Uri(preserved);
            var query = new List<string>();
            foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string key = Decode(part.Split('=', 2)[0]);
                if (IsCredential(key)) continue;
                if (key.Any(char.IsControl) || TicketReferenceResolver.IsBusinessParameter(key))
                    throw Invalid("Une adresse de configuration contient un paramètre d’action métier ou un nom de paramètre invalide.");
                query.Add(part);
            }
            string fragment = uri.Fragment;
            if (fragment.TrimStart('#').Split('&').Any(part => IsCredential(Decode(part.Split('=', 2)[0])))) fragment = "";
            return uri.GetLeftPart(UriPartial.Path) + (query.Count == 0 ? "" : "?" + string.Join("&", query)) + fragment;
        }
        catch (InvalidOperationException ex) { throw Invalid(ex.Message); }
    }

    private static string SharedOrigin(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string clean = SharedUrl(value);
        var uri = new Uri(clean);
        if (uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw Invalid("Une origine de sélecteur doit contenir uniquement le protocole, l’hôte et le port.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static bool IsCredential(string name) => CredentialParameters.Contains(name) ||
        name.Split(new[] { '[', ']', '.' }, StringSplitOptions.RemoveEmptyEntries).Any(part => CredentialParameters.Contains(part));
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static void ValidateNestedProperties(JsonElement settings, bool allowLocalPreferences = false)
    {
        if (Find(settings, "Queries") is { ValueKind: not JsonValueKind.Null } queries)
        {
            RequireProperties(queries, QueryLibraryProperties);
            if (Find(queries, "Queries") is not { ValueKind: JsonValueKind.Array } definitions || definitions.GetArrayLength() > 128)
                throw Invalid("La bibliothèque accepte au maximum 128 requêtes enregistrées.");
            foreach (var query in definitions.EnumerateArray())
            {
                RequireProperties(query, SavedQueryProperties);
                JsonText(query, "Id", 96, required: true);
                JsonText(query, "Name", 120, required: true);
                if (Find(query, "Conditions") is { } conditions)
                {
                    if (conditions.ValueKind != JsonValueKind.Array || conditions.GetArrayLength() > 20)
                        throw Invalid("Une requête accepte au maximum 20 conditions.");
                    foreach (var condition in conditions.EnumerateArray()) RequireProperties(condition, QueryConditionProperties);
                }
            }
            var configuredQueries = JsonSerializer.Deserialize<QueryLibrarySettings>(queries.GetRawText(), JsonOptions)
                ?? throw Invalid("La bibliothèque de requêtes est absente.");
            configuredQueries.Validate();
        }
        if (Find(settings, "Cities") is { ValueKind: not JsonValueKind.Null } cities)
        {
            RequireProperties(cities, CitySettingsProperties);
            if (Find(cities, "Cities") is { } definitions)
            {
                if (definitions.ValueKind != JsonValueKind.Array || definitions.GetArrayLength() > 1000)
                    throw Invalid("Les correspondances acceptent au maximum 1 000 villes.");
                foreach (var city in definitions.EnumerateArray())
                {
                    RequireProperties(city, CityDefinitionProperties);
                    JsonText(city, "Id", 96, required: true);
                    JsonText(city, "Name", 120, required: true);
                }
            }
            // Validate the raw city data before normalization can repair or bound
            // local settings. Invalid shared aliases and coordinates are rejected.
            var configured = JsonSerializer.Deserialize<CitySettings>(cities.GetRawText(), JsonOptions)
                ?? throw Invalid("La liste de correspondances des villes est absente.");
            configured.Validate();
        }
        if (Find(settings, "TicketDetails") is { ValueKind: not JsonValueKind.Null } details)
        {
            RequireProperties(details, TicketProperties);
            if (Find(details, "Sections") is { ValueKind: not JsonValueKind.Null } sections)
            {
                if (sections.ValueKind != JsonValueKind.Array || sections.GetArrayLength() > 20)
                    throw Invalid("La liste des sections de ticket doit contenir au maximum 20 éléments.");
                foreach (var section in sections.EnumerateArray()) RequireProperties(section, SectionProperties);
            }
        }
        if (Find(settings, "Dashboard") is { ValueKind: not JsonValueKind.Null } dashboard)
        {
            var allowedDashboard = new HashSet<string>(DashboardProperties, StringComparer.OrdinalIgnoreCase);
            if (allowLocalPreferences)
            {
                allowedDashboard.Add("PersonalTechnician");
                allowedDashboard.Add("OpenPersonalAfterExtraction");
            }
            RequireProperties(dashboard, allowedDashboard);
            if (Find(dashboard, "ColumnMappings") is { ValueKind: not JsonValueKind.Null } mappings) RequireProperties(mappings, MappingKeys);
            if (Find(dashboard, "Layout") is { ValueKind: not JsonValueKind.Null } layout) ValidateLayoutJson(layout);
            if (Find(dashboard, "PersonalLayout") is { ValueKind: not JsonValueKind.Null } personalLayout) ValidateLayoutJson(personalLayout);
        }
    }

    private static void ValidateLayoutJson(JsonElement layout)
    {
        RequireProperties(layout, LayoutProperties);
        JsonRange(layout, "Version", 1, 1);
        JsonText(layout, "ActiveBoardId", 96);
        if (Find(layout, "Boards") is not { } boards) return;
        if (boards.ValueKind != JsonValueKind.Array || boards.GetArrayLength() is < 1 or > 12)
            throw Invalid("La présentation doit contenir entre 1 et 12 tableaux de bord.");
        var boardIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var board in boards.EnumerateArray())
        {
            RequireProperties(board, BoardProperties);
            JsonText(board, "Id", 96, required: true); JsonText(board, "Name", 120, required: true);
            var boardId = Find(board, "Id")!.Value.GetString()!;
            if (!boardIds.Add(boardId)) throw Invalid("Chaque tableau de bord doit posséder un identifiant unique.");
            if (Find(board, "Widgets") is not { } widgets || widgets.ValueKind != JsonValueKind.Array || widgets.GetArrayLength() > 60)
                throw Invalid("Chaque tableau de bord peut contenir au maximum 60 widgets.");
            var widgetIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var widget in widgets.EnumerateArray())
            {
                RequireProperties(widget, WidgetProperties);
                JsonText(widget, "Id", 96, required: true);
                if (!widgetIds.Add(Find(widget, "Id")!.Value.GetString()!)) throw Invalid("Les widgets d’un même tableau de bord doivent avoir des identifiants uniques.");
                JsonText(widget, "SourceKey", 120); JsonText(widget, "Title", 180); JsonText(widget, "Note", 4000);
                JsonRange(widget, "Span", 3, 12); JsonRange(widget, "Height", 160, 960); JsonRange(widget, "TopCount", 3, 30);
                JsonEnum(widget, "Kind", "metric", "distribution", "weather", "timeline", "attention", "quality", "note");
                JsonEnum(widget, "Style", "surface", "outline", "accent", "minimal");
                JsonEnum(widget, "Accent", "blue", "teal", "green", "orange", "red", "purple", "slate");
                JsonEnum(widget, "Presentation", "bars", "list", "compact", "donut", "gauge");
                var target = JsonThreshold(widget, "TargetValue", positive: true);
                var warning = JsonThreshold(widget, "WarningValue");
                var critical = JsonThreshold(widget, "CriticalValue");
                var lowerIsBetter = Find(widget, "LowerIsBetter") is not { ValueKind: JsonValueKind.False };
                if (warning.HasValue && critical.HasValue && (lowerIsBetter ? critical < warning : critical > warning))
                    throw Invalid("Le seuil critique du widget doit être au-delà du seuil d’avertissement dans le sens défavorable choisi.");
                foreach (string boolean in new[] { "ShowDescription", "Hidden", "Locked", "LowerIsBetter", "AutoHeight" })
                    if (Find(widget, boolean) is { } value && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw Invalid("Une option de widget doit valoir vrai ou faux.");
            }
        }
        if (Find(layout, "ActiveBoardId") is { ValueKind: JsonValueKind.String } active &&
            !string.IsNullOrEmpty(active.GetString()) && !boardIds.Contains(active.GetString()!))
            throw Invalid("Le tableau de bord actif est absent de la présentation importée.");
    }

    private static void JsonText(JsonElement item, string key, int maximum, bool required = false)
    {
        if (Find(item, key) is not { } value)
        {
            if (required) throw Invalid("Un identifiant ou un nom de présentation est absent.");
            return;
        }
        if (value.ValueKind != JsonValueKind.String || (required && string.IsNullOrWhiteSpace(value.GetString())))
            throw Invalid("Une valeur textuelle de la présentation n’est pas valide.");
        Text(value.GetString()!, maximum, "Présentation du dashboard");
    }
    private static double? JsonThreshold(JsonElement item, string key, bool positive = false)
    {
        if (Find(item, key) is not { } value || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)
            || number < 0 || number > 1e12 || positive && number <= 0)
            throw Invalid("Un objectif ou seuil de widget est invalide ; utilisez un nombre fini positif ou laissez le champ vide.");
        return number;
    }
    private static void JsonRange(JsonElement item, string key, int minimum, int maximum)
    {
        if (Find(item, key) is not { } value) return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
            throw Invalid("Une dimension de la présentation doit être un entier.");
        Range(number, minimum, maximum, "Présentation du dashboard");
    }
    private static void JsonEnum(JsonElement item, string key, params string[] allowed)
    {
        if (Find(item, key) is not { } value) return;
        if (value.ValueKind != JsonValueKind.String) throw Invalid("Un style ou un type de widget n’est pas valide.");
        if (!allowed.Contains(value.GetString()!, StringComparer.Ordinal))
            throw Invalid("Présentation du dashboard : valeur non prise en charge.");
    }

    private static void RequireProperties(JsonElement value, HashSet<string> allowed)
    {
        RequireObject(value);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                throw Invalid("La configuration contient un champ inconnu, non partageable ou dupliqué. Aucun réglage n’a été appliqué.");
    }
    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("La configuration attend un objet JSON de réglages.");
    }
    private static JsonElement? Find(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }
    private static void WriteShareable(Utf8JsonWriter writer, JsonElement settings)
    {
        writer.WriteStartObject();
        foreach (var property in settings.EnumerateObject())
        {
            if (!Shareable.Contains(property.Name)) continue;
            if (property.Name.Equals("Dashboard", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName(property.Name); writer.WriteStartObject();
                foreach (var field in property.Value.EnumerateObject()) if (DashboardProperties.Contains(field.Name)) field.WriteTo(writer);
                writer.WriteEndObject();
            }
            else property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
    private static void ValidateCss(string value)
    {
        Text(value, 2048, "Sélecteur CSS");
        if (value.Contains(">>", StringComparison.Ordinal) || Regex.IsMatch(value.TrimStart(), @"\A(?!css\s*=)[A-Za-z_][A-Za-z0-9_-]*\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw Invalid("Les repères importés doivent être des sélecteurs CSS simples, sans moteur de script ni chaîne de sélecteurs.");
    }
    private static void Text(string value, int maximum, string label)
    {
        if (value.Length > maximum || value.Contains('\0')) throw Invalid($"{label} : la valeur dépasse {maximum} caractères ou contient un caractère NUL.");
    }
    private static void Range(int value, int minimum, int maximum, string label)
    {
        if (value < minimum || value > maximum) throw Invalid($"{label} : indique un entier compris entre {minimum} et {maximum}.");
    }
    private static string EnumValue(string value, string label, params string[] allowed) =>
        allowed.FirstOrDefault(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? throw Invalid(label + " : valeur non prise en charge.");

    private static byte[] ReadBounded(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length == 0 || file.Length > MaximumFileBytes) throw Invalid("La configuration doit contenir entre 1 octet et 1 Mio.");
        using var memory = new MemoryStream((int)file.Length);
        var buffer = new byte[16_384];
        int read;
        while ((read = file.Read(buffer)) > 0)
        {
            if (memory.Length + read > MaximumFileBytes) throw Invalid("La configuration dépasse la limite de 1 Mio.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
    private static void WriteAtomically(string path, byte[] data)
    {
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Dossier de configuration invalide.", nameof(path));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Le dossier de destination de la configuration n’existe pas.");
        string temporary = Path.Combine(directory, ".glpi-configuration-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(data);
                file.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
    private static InvalidDataException Invalid(string message) => new(message);
}
