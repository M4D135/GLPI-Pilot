using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record DeploymentConfigurationApplied(AppSettings Settings, string BackupPath);

public static partial class DeploymentConfigurationService
{
    private static readonly object Gate = new();
    private sealed record Original(string Name, bool Present, string Sha256);
    private sealed record Pending(int Version, string Id, Original[] Files);
    private const string PendingName = "deployment-pending.json";
    private static readonly HashSet<string> TargetNames = new(StringComparer.Ordinal)
    { "settings.json", "update-policy.json", "update-share.json", "library.local.json", "language-assistant.local.json", "terrain-services.json",
        "ticket-action-adapters.json", "workplace.json", "alerts.json", "table-views.json", "preview-view.json", "technician-preferences.json", "tour-order.json", "deployment-state.json" };
    public static string Fingerprint(AppSettings current, string root)
    {
        var configuration = Capture(current, root); var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        { ["settings.json"] = [], ["update-policy.json"] = [], ["update-share.json"] = [], ["library.local.json"] = [], ["language-assistant.local.json"] = [], ["deployment-state.json"] = [] };
        AddOptionFiles(configuration, root, files);
        var receipt = files.Keys.Order(StringComparer.Ordinal).Select(name =>
        {
            var path = Path.Combine(root, name); return new { name, hash = File.Exists(path) ? Digest(ReadBytes(path, 32 * 1024 * 1024)) : "absent" };
        }).ToArray();
        return Digest(JsonSerializer.SerializeToUtf8Bytes(new { settings = current, receipt }, new JsonSerializerOptions(Json) { IgnoreReadOnlyProperties = false }));
    }
    public static DeploymentConfigurationApplied Apply(DeploymentConfiguration configuration, AppSettings current, string root, CancellationToken ct = default, string? expectedFingerprint = null)
    {
        RealOnly(); lock (Gate)
        {
            Recover(root); ct.ThrowIfCancellationRequested();
            var baseline = Fingerprint(current, root);
            if (expectedFingerprint is not null && expectedFingerprint != baseline) throw new InvalidOperationException("Vos options ont changé depuis l’aperçu. Examinez de nouveau le fichier complet.");
            var next = configuration.Settings.Copy(); next.Interface.ContrastTheme = current.Interface.ContrastTheme; next.SitePatchAppliedHash = current.SitePatchAppliedHash; next.SiteReferenceImportHash = current.SiteReferenceImportHash;
            var library = configuration.Library.Deserialize<LibraryPreferences>(Json)!;
            var existingLibrary = new LibraryPreferencesStore(Path.Combine(root, "library.local.json")).Load();
            if (library.ConnectionBinding() == existingLibrary.ConnectionBinding())
            { library.ProtectedPassword = existingLibrary.ProtectedPassword; library.PasswordBinding = existingLibrary.PasswordBinding; }
            var assistant = configuration.Assistant.Deserialize<LanguageAssistantSettings>(Json)!;
            var existingAssistant = new LanguageAssistantStore(Path.Combine(root, "language-assistant.local.json")).Load();
            if (assistant.Endpoint == existingAssistant.Endpoint)
            { assistant.ProtectedApiKey = existingAssistant.ProtectedApiKey; assistant.KeyEndpoint = existingAssistant.KeyEndpoint; }
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(next, Json),
                ["update-policy.json"] = JsonSerializer.SerializeToUtf8Bytes(configuration.UpdatePolicy, Json),
                ["update-share.json"] = JsonSerializer.SerializeToUtf8Bytes(configuration.SharedUpdateFolder, Json),
                ["library.local.json"] = JsonSerializer.SerializeToUtf8Bytes(library, Json),
                ["language-assistant.local.json"] = JsonSerializer.SerializeToUtf8Bytes(assistant, Json),
                ["deployment-state.json"] = DeploymentConfigurationInitializer.AppliedOptionsReceipt(root)
            };
            AddOptionFiles(configuration, root, files);
            if (files.Count > 24 || files.Any(pair => !ValidFileName(pair.Key) || pair.Value.Length > 32 * 1024 * 1024)
                || files.Sum(pair => (long)pair.Value.Length) > 64 * 1024 * 1024) throw new InvalidDataException("Configuration complète trop volumineuse ou cible invalide.");
            var id = Guid.NewGuid().ToString("N"); var backup = Path.Combine(root, "DeploymentBackups", id); Directory.CreateDirectory(backup);
            var portable = Path.Combine(backup, FileName); AtomicWrite(portable, Serialize(Capture(current, root)));
            var originals = new List<Original>();
            foreach (var name in files.Keys)
            {
                var path = Path.Combine(root, name); var present = File.Exists(path); var content = present ? ReadBytes(path, 32 * 1024 * 1024) : Array.Empty<byte>();
                if (present) AtomicWrite(Path.Combine(backup, name), content);
                originals.Add(new(name, present, present ? Digest(content) : ""));
            }
            var pending = new Pending(1, id, originals.ToArray()); var journal = Path.Combine(root, PendingName);
            ct.ThrowIfCancellationRequested();
            if (baseline != Fingerprint(current, root)) throw new InvalidOperationException("Les options locales ont changé pendant la sauvegarde. Aucun remplacement n’a été effectué.");
            AtomicWrite(journal, JsonSerializer.SerializeToUtf8Bytes(pending, Json));
            try
            {
                foreach (var pair in files) { ct.ThrowIfCancellationRequested(); AtomicWrite(Path.Combine(root, pair.Key), pair.Value); }
                ct.ThrowIfCancellationRequested(); File.Delete(journal);
                return new(next, portable);
            }
            catch
            {
                Recover(root); throw;
            }
        }
    }
    private static bool ValidFileName(string name) => TargetNames.Contains(name);
    public static void Recover(string root)
    {
        RealOnly(); lock (Gate)
        {
            var path = Path.Combine(root, PendingName); if (!File.Exists(path)) return;
            var pending = JsonSerializer.Deserialize<Pending>(ReadBytes(path), Json) ?? throw new InvalidDataException("Reprise de configuration absente.");
            if (pending.Version != 1 || !Guid.TryParseExact(pending.Id, "N", out _) || pending.Files is null || pending.Files.Length > 24
                || pending.Files.Any(item => !ValidFileName(item.Name) || item.Present && !UpdateService.IsHash(item.Sha256))
                || pending.Files.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pending.Files.Length)
                throw new InvalidDataException("Reprise de configuration invalide ; les sauvegardes sont conservées.");
            var folder = Path.Combine(root, "DeploymentBackups", pending.Id); var originals = new Dictionary<string, byte[]?>(); long totalBytes = 0;
            foreach (var item in pending.Files)
            {
                var bytes = item.Present ? ReadBytes(Path.Combine(folder, item.Name), 32 * 1024 * 1024) : null;
                if (bytes is not null && Digest(bytes) != item.Sha256) throw new InvalidDataException("Une sauvegarde de configuration ne correspond plus à son empreinte.");
                totalBytes += bytes?.Length ?? 0; if (totalBytes > 64 * 1024 * 1024) throw new InvalidDataException("La reprise de configuration dépasse 64 Mio.");
                originals[item.Name] = bytes;
            }
            foreach (var item in originals)
            {
                var target = Path.Combine(root, item.Key); UpdateService.EnsureNoLinks(target);
                if (item.Value is null) { if (File.Exists(target)) File.Delete(target); }
                else AtomicWrite(target, item.Value);
            }
            File.Delete(path);
        }
    }
}
