using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private string _simulationInstalledVersion = AppPaths.ApplicationVersion;
    private string? _simulationPreviousVersion;
    private string? _simulationSelectedArchive;
    private SimulationRelease? _simulationRelease;
    private SimulationUpdatePlan? _simulationUpdatePlan;
    private object? _simulationUpdateOutcome;
    private const string SimulationArchiveHash = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string SimulationCommit = "2222222222222222222222222222222222222222";
    private sealed record SimulationRelease(string Token, string Version, string Edition, DateTimeOffset PublishedAt);
    private sealed record SimulationUpdatePlan(string Token, string Version, string Edition, bool IsRollback, string Policy);

    partial void RegisterSimulationIntegrations()
    {
        var assistantGet = _handlers["assistant.config.get"];
        ReplaceSimulationOperation("assistant.config.get", async (args, ct) => SimulationResult(await assistantGet(args, ct).ConfigureAwait(false),
            "Le parcours complet utilise le moteur local dans la simulation. Les paramètres de fournisseur restent modifiables ; aucun fournisseur n’est contacté."));
        ReplaceSimulationOperation("assistant.config.save", (args, ct) =>
        {
            var settings = AnalyticsAssistantOptions(args); settings.ExternalEnabled = false;
            settings.ProtectedApiKey = ""; settings.KeyEndpoint = "";
            ct.ThrowIfCancellationRequested(); AnalyticsAssistantStorage().Save(settings); _analyticsAssistantPlan = null;
            return Task.FromResult<object?>(SimulationResult(AnalyticsAssistantProfile(settings),
                "Profil enregistré dans la simulation. Aucune clé n’est conservée ni utilisée ; le moteur de simulation reste local."));
        });
        ReplaceSimulationOperation("assistant.prepare", async (args, ct) =>
        {
            var local = SimulationArguments(args, new() { ["provider"] = "local", ["externalEnabled"] = false, ["endpoint"] = "", ["model"] = "" });
            var result = await AnalyticsPrepareAssistantAsync(local, ct).ConfigureAwait(false);
            return SimulationResult(result, "Simulation : les données affichées seront traitées par le moteur local. Aucun envoi au fournisseur configuré.");
        });
        ReplaceSimulationOperation("assistant.generate", async (args, ct) =>
        {
            // Every preparation in this instance is local; also check the plan at
            // the network boundary rather than relying solely on the page controls.
            var plan = RequireAnalyticsAssistantPlan(args);
            if (plan.Request.Settings.Provider != "local") throw new InvalidOperationException("Préparez de nouveau cette demande dans la simulation locale.");
            return SimulationResult(await AnalyticsGenerateAssistantAsync(args, ct).ConfigureAwait(false),
                "Simulation : proposition calculée sur votre relevé par le moteur local, sans appel à un modèle externe. Vous pouvez l’essayer et l’enregistrer dans cet espace.");
        });

        ReplaceSimulationOperation("update.state", (args, ct) => Task.FromResult<object?>(SimulationUpdateState()));
        ReplaceSimulationOperation("update.policy", (args, ct) =>
        {
            var policy = TerrainRead<UpdatePolicy>(args, "policy", 2000); policy.Validate(); ct.ThrowIfCancellationRequested();
            policy.Save(DataPath("update-policy.json")); _simulationUpdatePlan = null;
            return Task.FromResult<object?>(SimulationUpdateState());
        });
        ReplaceSimulationOperation("update.findOfficial", (args, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (!Flag(args, "consent")) throw new InvalidDataException("Lancez explicitement le scénario de recherche de publication.");
            var edition = Text(args, "edition", 10, "PROD");
            if (edition is not ("PROD" or "DEV")) throw new InvalidDataException("Choisissez PROD ou DEV.");
            _simulationRelease = new(Guid.NewGuid().ToString("N"), SimulationNextVersion(), edition, DateTimeOffset.Now);
            Publish("update.progress", new { message = "Simulation · une publication d’exemple est disponible localement, sans contact GitHub." });
            return Task.FromResult<object?>(SimulationOfficialReview(_simulationRelease));
        });
        ReplaceSimulationOperation("update.selectArchive", (args, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            _simulationSelectedArchive = "GLPI-Pilot_" + SimulationNextVersion() + "_PROD_SIMULATION.zip"; _simulationUpdatePlan = null;
            return Task.FromResult<object?>(new { name = _simulationSelectedArchive, bytes = 4096, sha256 = SimulationArchiveHash, commit = SimulationCommit, simulation = true });
        });
        ReplaceSimulationOperation("update.prepareLocal", (args, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (_simulationSelectedArchive is null) throw new InvalidOperationException("Choisissez d’abord le pack de démonstration.");
            if (Text(args, "sha256", 64) != SimulationArchiveHash || Text(args, "commit", 40) != SimulationCommit)
                throw new InvalidDataException("Les références saisies ne correspondent pas au pack de simulation. Reprenez les références affichées à sa sélection.");
            return Task.FromResult<object?>(PrepareSimulationUpdate(SimulationNextVersion(), "PROD", false));
        });
        ReplaceSimulationOperation("update.downloadOfficial", (args, ct) =>
        {
            ct.ThrowIfCancellationRequested(); var release = _simulationRelease;
            if (!Flag(args, "consent") || release is null || Text(args, "token", 40) != release.Token)
                throw new InvalidOperationException("Vérifiez puis confirmez la publication de simulation affichée.");
            Publish("update.progress", new { message = "Simulation · préparation du pack d’exemple, sans téléchargement ni modification de fichiers." });
            return Task.FromResult<object?>(PrepareSimulationUpdate(release.Version, release.Edition, false));
        });
        ReplaceSimulationOperation("update.prepareRollback", (args, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (!Flag(args, "confirmed")) throw new InvalidDataException("Confirmez la préparation du retour arrière simulé.");
            var version = _simulationPreviousVersion ?? SimulationOlderVersion();
            return Task.FromResult<object?>(PrepareSimulationUpdate(version, "PROD", true));
        });
        ReplaceSimulationOperation("update.install", (args, ct) =>
        {
            ct.ThrowIfCancellationRequested(); var plan = _simulationUpdatePlan;
            if (plan is null || !Flag(args, "confirmed") || Text(args, "token", 40) != plan.Token || Text(args, "version", 32) != plan.Version)
                throw new InvalidOperationException("Confirmez le pack de simulation préparé et sa version.");
            if (plan.Policy != TerrainDigest(UpdatePolicy.Load(DataPath("update-policy.json"))))
                throw new InvalidOperationException("La politique a changé ; préparez de nouveau le scénario.");
            var previous = _simulationInstalledVersion; _simulationInstalledVersion = plan.Version; _simulationPreviousVersion = previous;
            _simulationUpdateOutcome = new
            {
                at = DateTimeOffset.Now, state = plan.IsRollback ? "simulation-rolled-back" : "simulation-installed", version = plan.Version,
                detail = plan.IsRollback ? "Retour arrière simulé terminé. L’installation réelle est inchangée." : "Mise à jour simulée terminée ; sauvegarde d’exemple disponible pour essayer le retour arrière.",
                backupPath = "Sauvegarde virtuelle de la simulation", simulation = true
            };
            _simulationUpdatePlan = null;
            Publish("update.progress", new { message = "Simulation · scénario terminé. L’application reste ouverte et aucun binaire réel n’a été remplacé." });
            return Task.FromResult<object?>(new { closing = false, version = plan.Version, simulation = true, state = SimulationUpdateState() });
        });
        ReplaceSimulationOperation("update.openReleases", (args, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            _simulationRelease ??= new(Guid.NewGuid().ToString("N"), SimulationNextVersion(), "PROD", DateTimeOffset.Now);
            return Task.FromResult<object?>(new { opened = false, simulation = true, official = SimulationOfficialReview(_simulationRelease), note = "Publication d’exemple chargée dans la simulation, sans ouvrir de navigateur." });
        });
    }

    private object SimulationUpdateState() => new
    {
        version = _simulationInstalledVersion, actualVersion = AppPaths.ApplicationVersion,
        installRoot = "Installation virtuelle · simulation", policy = UpdatePolicy.Load(DataPath("update-policy.json")), windows = true,
        archive = _simulationSelectedArchive is null ? null : new { name = _simulationSelectedArchive },
        official = _simulationRelease is null ? null : SimulationOfficialReview(_simulationRelease),
        prepared = _simulationUpdatePlan is null ? null : SimulationPreparedReview(_simulationUpdatePlan),
        outcome = _simulationUpdateOutcome, automatic = false, simulation = true
    };
    private object SimulationOfficialReview(SimulationRelease release) => new
    {
        release.Token, release.Version, tag = "simulation-v" + release.Version, commit = SimulationCommit,
        release.Edition, archiveName = "GLPI-Pilot_" + release.Version + "_" + release.Edition + "_SIMULATION.zip",
        archiveSha256 = SimulationArchiveHash, bytes = 4096, release.PublishedAt,
        newer = Version.Parse(release.Version) > Version.Parse(_simulationInstalledVersion), simulation = true
    };
    private object PrepareSimulationUpdate(string version, string edition, bool rollback)
    {
        _simulationUpdatePlan = new(Guid.NewGuid().ToString("N"), version, edition, rollback, TerrainDigest(UpdatePolicy.Load(DataPath("update-policy.json"))));
        return SimulationPreparedReview(_simulationUpdatePlan);
    }
    private static object SimulationPreparedReview(SimulationUpdatePlan plan) => new
    {
        plan.Token, plan.Version, plan.Edition, commit = SimulationCommit, archiveSha256 = SimulationArchiveHash,
        files = 0, plan.IsRollback, simulation = true,
        signature = new { status = "simulation", publisher = "Exemple local", certificateSha256 = "", description = "Scénario simulé · aucune signature ni archive réelle contrôlée" },
        backup = "Sauvegarde virtuelle de la simulation"
    };
    private string SimulationNextVersion()
    {
        var current = Version.Parse(_simulationInstalledVersion);
        return new Version(current.Major, current.Minor, Math.Max(0, current.Build) + 1).ToString(3);
    }
    private string SimulationOlderVersion()
    {
        var current = Version.Parse(_simulationInstalledVersion);
        return new Version(current.Major, current.Minor, Math.Max(0, current.Build - 1)).ToString(3);
    }
    private static JsonElement SimulationArguments(JsonElement args, Dictionary<string, object?> changes)
    {
        var document = JsonNode.Parse(args.GetRawText())!.AsObject();
        foreach (var change in changes) document[change.Key] = JsonSerializer.SerializeToNode(change.Value, Json);
        return JsonSerializer.SerializeToElement(document, Json);
    }
    private static JsonElement SimulationResult(object? value, string note)
    {
        var document = JsonSerializer.SerializeToNode(value, Json)?.AsObject() ?? new JsonObject();
        document["simulation"] = true; document["note"] = note;
        return JsonSerializer.SerializeToElement(document, Json);
    }
}
