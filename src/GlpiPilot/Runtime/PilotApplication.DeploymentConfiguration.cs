using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private string _deploymentDirectory = AppContext.BaseDirectory;
    private string _deploymentVersion = AppPaths.ApplicationVersion;
    private sealed record DeploymentDraft(string Token, string Path, string Sha256, string Baseline, DeploymentConfiguration Configuration, bool Seed);
    private DeploymentDraft? _deploymentDraft;
    private void RequireDeploymentReal()
    { if (IsTestMode) throw new InvalidOperationException("Fermez la démonstration avant d’exporter ou de remplacer la configuration réelle."); }
    private object DeploymentState() => DeploymentConfigurationInitializer.State(_deploymentDirectory, AppPaths.DataRoot, _deploymentVersion);
    private void RegisterDeploymentConfigurationOperations()
    {
        Register("deployment.state", (_, _) => Task.FromResult<object?>(DeploymentState()));
        Register("deployment.default.save", (a, ct) =>
        {
            RequireDeploymentReal();
            if (!Flag(a, "confirmed")) throw new InvalidOperationException("Confirmez le remplacement de la configuration par défaut fournie avec ce pack.");
            if (!a.TryGetProperty("expectedSha256", out var expected) || expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Rouvrez les réglages pour confirmer la version actuelle de la configuration par défaut.");
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<object?>(DeploymentConfigurationInitializer.SaveDefault(_deploymentDirectory, AppPaths.DataRoot, _deploymentVersion, Settings, Text(a, "expectedSha256", 64)));
        });
        Register("deployment.export", async (_, ct) =>
        {
            RequireDeploymentReal(); var path = await Host.SaveFileAsync("Exporter toutes les options de configuration", "Configuration complète JSON|*.json", DeploymentConfigurationService.FileName);
            if (path is null) return new { cancelled = true, path = "", sha256 = "", fileName = DeploymentConfigurationService.FileName };
            ct.ThrowIfCancellationRequested(); DeploymentConfigurationService.Export(path, Settings, AppPaths.DataRoot);
            return new { cancelled = false, path, sha256 = DeploymentConfigurationService.Digest(DeploymentConfigurationService.ReadBytes(path)), fileName = DeploymentConfigurationService.FileName };
        });
        Register("deployment.preview", async (_, ct) =>
        {
            RequireDeploymentReal(); var path = await Host.OpenFileAsync("Examiner une configuration complète", "Configuration complète JSON|*.json");
            if (path is null) return new { cancelled = true };
            return PreviewDeployment(path, false, ct);
        });
        Register("deployment.seed.preview", (_, ct) =>
        {
            RequireDeploymentReal(); return Task.FromResult<object?>(PreviewDeployment(DeploymentConfigurationInitializer.ResolveSeedPath(_deploymentDirectory), true, ct));
        });
        Register("deployment.decline", (a, ct) =>
        {
            RequireDeploymentReal(); var state = DeploymentConfigurationInitializer.State(_deploymentDirectory, AppPaths.DataRoot, _deploymentVersion);
            if (!state.Offer.Available || state.Offer.Sha256 != Text(a, "sha256", 64)) throw new InvalidOperationException("La configuration fournie a changé. Examinez son nouvel aperçu.");
            ct.ThrowIfCancellationRequested(); DeploymentConfigurationInitializer.RememberChoice(AppPaths.DataRoot, state.Offer.Sha256, _deploymentVersion, false);
            _deploymentDraft = null; return Task.FromResult<object?>(DeploymentState());
        });
        Register("deployment.apply", (a, ct) =>
        {
            RequireDeploymentReal();
            if (!Flag(a, "confirmed")) throw new InvalidOperationException("Confirmez le remplacement de toutes les options après avoir examiné les différences.");
            var draft = _deploymentDraft;
            if (draft is null || draft.Token != Text(a, "token", 80)
                || draft.Seed && !string.Equals(draft.Path, DeploymentConfigurationInitializer.ResolveSeedPath(_deploymentDirectory), StringComparison.OrdinalIgnoreCase)
                || draft.Baseline != DeploymentConfigurationService.Fingerprint(Settings, AppPaths.DataRoot)
                || draft.Sha256 != DeploymentConfigurationService.Digest(DeploymentConfigurationService.ReadBytes(draft.Path)))
                throw new InvalidOperationException("L’aperçu a expiré : le fichier ou vos options ont changé. Examinez de nouveau la configuration complète.");
            var applied = DeploymentConfigurationService.Apply(draft.Configuration, Settings, AppPaths.DataRoot, ct, expectedFingerprint: draft.Baseline);
            _deploymentDraft = null; _libraryPreferences = null; _libraryPassword = ""; _libraryPasswordBinding = ""; _ticketLibrary = null; _teamWorkspace = null;
            ResetLibrarySharing();
            _workplaceDocument = null; _workplacePrevious = new(); _alerts = new(); _analyticsAssistantPlan = null; _analyticsAssistantDraft = null;
            _sharedUpdateOffer = null; _sharedUpdateToken = ""; _terrainPreparedUpdate = null; _sitePatchDraft = null;
            _terrainCityDraft = null; _terrainCityPreview = null; _terrainImportPreview = null; _configurationDraft = null; _configurationToken = "";
            InvalidatePreparedActions();
            ApplySavedSettings(applied.Settings);
            if (draft.Seed)
            {
                try { DeploymentConfigurationInitializer.RememberChoice(AppPaths.DataRoot, draft.Sha256, _deploymentVersion, true); }
                catch (Exception error) when (DeploymentConfigurationService.StorageError(error)) { Log("Configuration appliquée ; reçu de proposition non enregistré : " + AppPaths.SafeError(error)); }
            }
            return Task.FromResult<object?>(new { settings = Settings, session = SessionState(), backupPath = applied.BackupPath, sha256 = draft.Sha256 });
        });
    }
    private object PreviewDeployment(string path, bool seed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var read = DeploymentConfigurationService.Read(path, AppPaths.DataRoot);
        var current = DeploymentConfigurationService.Capture(Settings, AppPaths.DataRoot);
        current.Settings = Settings.Copy();
        current.Library = DeploymentConfigurationService.WithoutSecrets(new LibraryPreferencesStore().Load(), ["ProtectedPassword", "PasswordBinding"]);
        var sections = new List<object>();
        void Add(string key, string title, object before, object after)
        {
            var old = JsonSerializer.SerializeToElement(before, DeploymentConfigurationService.Json); var next = JsonSerializer.SerializeToElement(after, DeploymentConfigurationService.Json);
            sections.Add(new { key, title, before = old, after = next, changed = old.GetRawText() != next.GetRawText() });
        }
        Add("Settings", "Options de l’application : sources, sites, interface, automatisation et historique", current.Settings, read.Configuration.Settings);
        Add("Updates", "Mises à jour : partage réseau et confiance",
            new Dictionary<string, object> { ["SharedUpdateFolder"] = current.SharedUpdateFolder, ["UpdatePolicy"] = current.UpdatePolicy },
            new Dictionary<string, object> { ["SharedUpdateFolder"] = read.Configuration.SharedUpdateFolder, ["UpdatePolicy"] = read.Configuration.UpdatePolicy });
        Add("Library", "Bibliothèque et PostgreSQL : options publiques", current.Library, read.Configuration.Library);
        Add("Assistant", "Assistant : fournisseur et options publiques", current.Assistant, read.Configuration.Assistant);
        foreach (var pair in read.Configuration.Options) Add(pair.Key, pair.Key switch
        {
            "TerrainServices" => "Météo et itinéraires", "TicketActions" => "Formulaires GLPI", "Workplace" => "Lecture et rappels",
            "AlertRules" => "Règles d’alerte", "TableViews" => "Vues des tableaux", "TableColumns" => "Colonnes des tableaux",
            "Technicians" => "Préférences des techniciens", "TourOrder" => "Ordre des tournées", _ => pair.Key
        }, current.Options[pair.Key], pair.Value);
        var baseline = DeploymentConfigurationService.Fingerprint(Settings, AppPaths.DataRoot); ct.ThrowIfCancellationRequested();
        _deploymentDraft = new(Guid.NewGuid().ToString("N"), Path.GetFullPath(path), read.Sha256, baseline, read.Configuration, seed);
        return new
        {
            cancelled = false, token = _deploymentDraft.Token, fileName = Path.GetFileName(path), read.Sha256, sections,
            warnings = new[] { "Toutes les options présentées seront remplacées après sauvegarde réimportable. Les chemins personnalisés doivent convenir à ce poste.",
                "Les bases, tickets, notes, interventions, cookies et sessions SSO ne sont pas transportés. Les secrets existants restent locaux et ne sont réutilisés que pour la même destination.",
                "Les droits Administration dépendent de l’exécutable, jamais du fichier de configuration." }
        };
    }
}
