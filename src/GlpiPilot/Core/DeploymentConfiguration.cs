using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

public sealed class DeploymentConfiguration
{
    public string Schema { get; set; } = "glpi-pilot.deployment";
    public int SchemaVersion { get; set; } = 1;
    public string AppVersion { get; set; } = AppPaths.ApplicationVersion;
    public AppSettings Settings { get; set; } = new();
    /// <summary>Files exported up to 3.14.0 name the GitHub releases page, then an update source. Accepted on read, never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? UpdateRepository { get; set; }
    internal const string LegacyUpdateRepository = "https://github.com/M4D135/GLPI-Pilot/releases";
    public string SharedUpdateFolder { get; set; } = "";
    public UpdatePolicy UpdatePolicy { get; set; } = new();
    public JsonElement Library { get; set; }
    public JsonElement Assistant { get; set; }
    public Dictionary<string, JsonElement> Options { get; set; } = new(StringComparer.Ordinal);
}

public sealed record DeploymentConfigurationRead(DeploymentConfiguration Configuration, string Sha256);

/// <summary>A full configuration document, separate from limited team configuration and from user data.</summary>
public static partial class DeploymentConfigurationService
{
    public const string FileName = UpdateService.DeploymentDefaultFileName;
    public const int MaximumBytes = 8 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 32, PropertyNameCaseInsensitive = true,
        IgnoreReadOnlyProperties = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const string DataToken = "${GLPI_DATA_ROOT}", ExportsToken = "${GLPI_DEFAULT_EXPORTS}";
    private static readonly string[] LibrarySecrets = ["ProtectedPassword", "PasswordBinding"];
    private static readonly string[] AssistantSecrets = ["ProtectedApiKey", "KeyEndpoint"];
    internal static bool StorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException;
    public static DeploymentConfiguration Capture(AppSettings settings, string root)
    {
        RealOnly();
        var library = new LibraryPreferencesStore(Path.Combine(root, "library.local.json")).Load();
        var assistant = new LanguageAssistantStore(Path.Combine(root, "language-assistant.local.json")).Load();
        var folder = Path.Combine(root, "update-share.json");
        var result = new DeploymentConfiguration
        {
            Settings = settings.Copy(), SharedUpdateFolder = File.Exists(folder) ? JsonSerializer.Deserialize<string>(ReadBytes(folder), Json) ?? "" : "",
            UpdatePolicy = UpdatePolicy.Load(Path.Combine(root, "update-policy.json")),
            Library = WithoutSecrets(library, LibrarySecrets), Assistant = WithoutSecrets(assistant, AssistantSecrets)
        };
        result.Settings.Interface.ContrastTheme = false;
        CaptureOptions(result, root);
        result.Settings = ConfigurationTransferService.ValidateDeployment(JsonSerializer.SerializeToElement(result.Settings, Json));
        if (result.Settings.ExportFolder == AppPaths.DefaultExports) result.Settings.ExportFolder = ExportsToken;
        foreach (var source in result.Settings.Sources) if (source.ExportFolder == AppPaths.DefaultExports) source.ExportFolder = ExportsToken;
        var fields = result.Library.Deserialize<Dictionary<string, JsonElement>>(Json)!;
        if (library.LocalFolder == Path.Combine(root, "Library")) fields["LocalFolder"] = JsonSerializer.SerializeToElement(DataToken + "/Library");
        result.Library = JsonSerializer.SerializeToElement(fields, Json);
        return result;
    }
    public static byte[] Serialize(DeploymentConfiguration configuration)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(configuration, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("La configuration complète dépasse 8 Mio.");
        return bytes;
    }
    public static void Export(string path, AppSettings settings, string root)
    {
        var full = Path.GetFullPath(path); var directory = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && !string.Equals(full, Path.Combine(root, FileName), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Enregistrez le fichier de diffusion en dehors des fichiers internes du profil GLPI Pilot.");
        AtomicWrite(path, Serialize(Capture(settings, root)));
    }
    public static DeploymentConfigurationRead Read(string path, string root)
    {
        RealOnly(); var bytes = ReadBytes(path);
        try
        {
            using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); ValidateKeys(parsed.RootElement);
            var legacy = parsed.RootElement.EnumerateObject().Any(p => p.Name.Equals(nameof(DeploymentConfiguration.UpdateRepository), StringComparison.OrdinalIgnoreCase));
            RequireComplete<DeploymentConfiguration>(parsed.RootElement, legacy ? null : [nameof(DeploymentConfiguration.UpdateRepository)]);
            var value = parsed.RootElement.Deserialize<DeploymentConfiguration>(Json) ?? throw new InvalidDataException("Configuration complète absente.");
            if (value.Schema != "glpi-pilot.deployment" || value.SchemaVersion != 1 || !Version.TryParse(value.AppVersion, out _)
                || value.UpdateRepository is not (null or DeploymentConfiguration.LegacyUpdateRepository) || value.Settings is null || value.UpdatePolicy is null || value.Options is null)
                throw new InvalidDataException("Le format de configuration complète n’est pas reconnu.");
            value.UpdateRepository = null;
            var rawSettings = parsed.RootElement.EnumerateObject().SingleOrDefault(p => p.Name.Equals("Settings", StringComparison.OrdinalIgnoreCase)).Value;
            RequireComplete<AppSettings>(rawSettings);
            RequireComplete<UpdatePolicy>(parsed.RootElement.EnumerateObject().Single(p => p.Name.Equals("UpdatePolicy", StringComparison.OrdinalIgnoreCase)).Value);
            value.Settings = ConfigurationTransferService.ValidateDeployment(rawSettings);
            value.Settings.ExportFolder = Expand(value.Settings.ExportFolder, root);
            foreach (var source in value.Settings.Sources) source.ExportFolder = Expand(source.ExportFolder, root);
            AutomationPolicy.Times(value.Settings.Automation.FixedTimes); AutomationPolicy.Days(value.Settings.Automation.WeekDays); AutomationPolicy.ValidateRecipe(value.Settings.Automation.Steps);
            if (value.SharedUpdateFolder is null) throw new InvalidDataException("Dossier de diffusion absent.");
            if (value.SharedUpdateFolder.Length > 0) value.SharedUpdateFolder = SharedUpdateSource.ValidateFolder(value.SharedUpdateFolder);
            value.UpdatePolicy.Validate();
            var library = ReadPublic<LibraryPreferences>(value.Library, LibrarySecrets); library.LocalFolder = Expand(library.LocalFolder, root); library.Validate();
            value.Library = WithoutSecrets(library, LibrarySecrets);
            var assistant = ReadPublic<LanguageAssistantSettings>(value.Assistant, AssistantSecrets); assistant.Validate(); value.Assistant = WithoutSecrets(assistant, AssistantSecrets);
            ValidateOptions(value);
            if (JsonSerializer.SerializeToUtf8Bytes(value.Settings, Json).Length > 4 * 1024 * 1024) throw new InvalidDataException("Les options de l’application dépassent 4 Mio.");
            return new(value, Digest(bytes));
        }
        catch (JsonException) { throw new InvalidDataException("Le fichier de configuration complète est invalide ou contient une option inconnue."); }
    }
    internal static JsonElement WithoutSecrets<T>(T value, string[] secrets)
    {
        var values = JsonSerializer.SerializeToElement(value, Json).Deserialize<Dictionary<string, JsonElement>>(Json)!;
        foreach (var key in secrets) values.Remove(key); return JsonSerializer.SerializeToElement(values, Json);
    }
    private static T ReadPublic<T>(JsonElement element, string[] excluded)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Any(p => excluded.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Le fichier complet contient un secret non transportable ou une section absente.");
        RequireComplete<T>(element, excluded);
        return element.Deserialize<T>(Json) ?? throw new InvalidDataException("Section de configuration absente.");
    }
    private static void RequireComplete<T>(JsonElement element, string[]? excluded = null)
    {
        var expected = typeof(T).GetProperties().Where(p => p.CanWrite && !(excluded?.Contains(p.Name, StringComparer.OrdinalIgnoreCase) ?? false))
            .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object || !expected.SetEquals(element.EnumerateObject().Select(p => p.Name)))
            throw new InvalidDataException("Le fichier complet doit contenir toutes les options de chaque section. Utilisez l’export complet.");
    }
    private static string Expand(string value, string root) => value == ExportsToken ? AppPaths.DefaultExports
        : value == DataToken + "/Library" ? Path.Combine(root, "Library") : value;
    internal static void ValidateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Une option du fichier complet est répétée."); ValidateKeys(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidateKeys(item);
    }
    internal static byte[] ReadBytes(string path, int maximum = MaximumBytes)
    {
        UpdateService.EnsureNoLinks(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 1 || stream.Length > maximum) throw new InvalidDataException("Le fichier de configuration est vide ou dépasse sa limite autorisée.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Le fichier de configuration a changé pendant sa lecture."); return bytes;
    }
    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static void RealOnly() { if (AppPaths.SimulationRoot is not null) throw new InvalidOperationException("La démonstration ne peut pas diffuser ou remplacer la configuration réelle."); }
    internal static void AtomicWrite(string path, byte[] bytes)
    {
        UpdateService.EnsureNoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    static partial void CaptureOptions(DeploymentConfiguration configuration, string root);
    static partial void ValidateOptions(DeploymentConfiguration configuration);
    static partial void AddOptionFiles(DeploymentConfiguration configuration, string root, Dictionary<string, byte[]> files);
}
