using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

internal static class StartupCollectionConfigurationChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(string temporary)
    {
        yield return ("Collecte au démarrage : défaut activé pour les réglages neufs et anciens sans choix", Defaults);
        yield return ("Collecte au démarrage : désactivation conservée par copie, normalisation et changement de source", CopyAndSource);
        yield return ("Collecte au démarrage : choix local conservé après sauvegarde et réouverture", () => Persistence(temporary));
        yield return ("Collecte au démarrage : préférence locale préservée par import et absente du partage", () => Portable(temporary));
        yield return ("Collecte au démarrage : éligibilité complète en lecture seule, sans créer le dossier d’export", () => EligibleWithoutSideEffects(temporary));
        yield return ("Collecte au démarrage : adresse ou dossier manquant, aucune offre automatique", () => Incomplete(temporary));
        yield return ("Collecte au démarrage : configuration invalide refusée avant le navigateur", () => InvalidConfiguration(temporary));
        yield return ("Collecte au démarrage : sources locales, historique et activité en cours préservés", () => ContextGuards(temporary));
        yield return ("Collecte au démarrage : navigateur et réseau requis, horaires récurrents indépendants", () => Availability(temporary));
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static void Defaults()
    {
        Check(new AutomationSettings().CollectOnStartup && new AppSettings().Automation.CollectOnStartup,
            "A new profile must opt into one collection when startup configuration is complete.");
        foreach (var json in new[] { "{}", "{\"Automation\":{}}", "{\"Automation\":null}", "{\"Automation\":{\"ScheduledExtraction\":false}}" })
        {
            var settings = SettingsRecovery.Read(Encoding.UTF8.GetBytes(json));
            Check(settings.Automation.CollectOnStartup && !settings.Automation.ScheduledExtraction,
                "Older settings without an explicit startup choice must receive the startup default without enabling recurring collection.");
        }
        var disabled = SettingsRecovery.Read(Encoding.UTF8.GetBytes("{\"Automation\":{\"CollectOnStartup\":false}}"));
        Check(!disabled.Automation.CollectOnStartup, "A saved explicit opt-out must never be replaced by the new default.");
    }

    private static void CopyAndSource()
    {
        var settings = new AppSettings { PortalUrl = "https://startup.test/glpi/front/ticket.php" };
        settings.Automation.CollectOnStartup = false;
        settings.Automation.ScheduledExtraction = true;
        var copy = settings.Copy(); copy.Normalize();
        Check(!copy.Automation.CollectOnStartup && copy.Automation.ScheduledExtraction && !ReferenceEquals(copy.Automation, settings.Automation),
            "Deep copying must retain both independent automatic-collection choices.");
        copy.Automation.CollectOnStartup = true;
        Check(!settings.Automation.CollectOnStartup, "Changing a copy must not re-enable the saved startup choice.");
        var source = new SourceProfile { Name = "Recherche synthétique" }; source.Capture(copy);
        source.Copy().ApplyTo(settings);
        Check(!settings.Automation.CollectOnStartup && settings.Automation.ScheduledExtraction,
            "Selecting a search profile must not replace the workstation's startup or schedule preferences.");
    }

    private static void Persistence(string temporary)
    {
        var path = Path.Combine(temporary, "startup-collection-settings.json");
        var settings = new AppSettings { PortalUrl = "https://startup.test/glpi/front/ticket.php" };
        foreach (var enabled in new[] { false, true, false })
        {
            settings.Automation.CollectOnStartup = enabled;
            SettingsRecovery.Save(path, settings);
            var reloaded = SettingsRecovery.Load(path);
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Check(reloaded.Automation.CollectOnStartup == enabled
                && saved.RootElement.GetProperty("Automation").GetProperty("CollectOnStartup").GetBoolean() == enabled,
                "The explicit startup choice must survive the actual atomic settings save and a fresh load.");
        }
    }

    private static void Portable(string temporary)
    {
        var incoming = new AppSettings { PortalUrl = "https://startup.test/glpi/front/ticket.php" };
        incoming.Normalize();
        var path = Path.Combine(temporary, "startup-collection-portable.json");
        ConfigurationTransferService.Export(path, incoming);
        Check(!File.ReadAllText(path).Contains("CollectOnStartup", StringComparison.OrdinalIgnoreCase),
            "A shared team configuration must not carry a workstation's automatic startup choice.");
        foreach (var enabled in new[] { false, true })
        {
            var current = new AppSettings(); current.Automation.CollectOnStartup = enabled;
            incoming.Automation.CollectOnStartup = !enabled;
            var imported = ConfigurationTransferService.Import(path, current);
            var reviewed = ConfigurationReview.Apply(current, incoming, ConfigurationParts.All, mergeCollections: false);
            Check(imported.Automation.CollectOnStartup == enabled && reviewed.Automation.CollectOnStartup == enabled
                && current.Automation.CollectOnStartup == enabled,
                "Portable import and selective configuration application must both preserve the local startup preference.");
            var legacy = Path.Combine(temporary, "startup-collection-legacy.json");
            File.WriteAllText(legacy, JsonSerializer.Serialize(new { incoming.PortalUrl, Automation = new { CollectOnStartup = !enabled } }));
            Check(ConfigurationTransferService.Import(legacy, current).Automation.CollectOnStartup == enabled,
                "Importing another workstation's old settings.json must not silently replace the local startup choice.");
        }
    }

    private static AppSettings Configured(string temporary) => new()
    {
        PortalUrl = "https://startup.test/glpi/front/ticket.php?criteria%5B0%5D%5Bvalue%5D=synthese",
        ExportFolder = Path.Combine(temporary, "startup-uncreated-exports"), BrowserChannel = BrowserChannels.Edge
    };

    private static void EligibleWithoutSideEffects(string temporary)
    {
        var settings = Configured(temporary); var before = JsonSerializer.Serialize(settings);
        Check(!Directory.Exists(settings.ExportFolder), "The fixture export directory must initially be absent.");
        var decision = StartupCollectionPolicy.Evaluate(settings, new());
        Check(decision.Eligible && decision.Reason == "ready" && decision.Message.Length > 0
            && !Directory.Exists(settings.ExportFolder) && JsonSerializer.Serialize(settings) == before,
            "Bootstrap eligibility must not create directories, run a writable-destination probe, or alter configuration.");
    }

    private static void Incomplete(string temporary)
    {
        foreach (var field in new[] { "source", "folder" })
        {
            var settings = Configured(temporary);
            if (field == "source") settings.PortalUrl = " "; else settings.ExportFolder = " ";
            var decision = StartupCollectionPolicy.Evaluate(settings, new());
            Check(!decision.Eligible && decision.Reason == "configuration-incomplete", "A missing " + field + " must keep startup local and explicit.");
        }
    }

    private static void InvalidConfiguration(string temporary)
    {
        Action<AppSettings>[] invalid =
        [
            settings => settings.PortalUrl = "file:///private/tickets.html",
            settings => settings.PortalUrl = "https://user:secret@startup.test/glpi/front/ticket.php",
            settings => settings.BrowserChannel = "unknown",
            settings => settings.AutoMode = "script",
            settings => settings.ColumnPreparationMode = "global",
            settings => settings.MaxPages = 0,
            settings => { settings.MaxRows = 10; settings.ExpectedTotal = 11; },
            settings => settings.ListTimeoutSeconds = 0,
            settings => settings.NativeMenuSelector = "#incomplete-export",
            settings => settings.Cities.SourcePriority.Clear(),
            settings => settings.Queries.Queries.Add(new SavedQuery { Id = "invalid-query-id" }),
            settings => settings.Cities = null!
        ];
        foreach (var change in invalid)
        {
            var settings = Configured(temporary); change(settings); var before = JsonSerializer.Serialize(settings);
            var decision = StartupCollectionPolicy.Evaluate(settings, new());
            Check(!decision.Eligible && decision.Reason == "configuration-invalid" && JsonSerializer.Serialize(settings) == before,
                "Invalid settings must be rejected without silently repairing them or starting the browser.");
        }
    }

    private static void ContextGuards(string temporary)
    {
        var contexts = new (StartupCollectionContext Context, string Reason)[]
        {
            (new() { Simulation = true }, "simulation"), (new() { LocalSource = true }, "local-source"),
            (new() { HistoricalSource = true }, "history-source"), (new() { AlreadyCollected = true }, "already-collected"),
            (new() { Busy = true }, "busy"), (new() { EditorOpen = true }, "editor-open"),
            (new() { SessionLocked = true }, "session-locked")
        };
        foreach (var (context, reason) in contexts)
        {
            var decision = StartupCollectionPolicy.Evaluate(Configured(temporary), context);
            Check(!decision.Eligible && decision.Reason == reason, "The startup policy must preserve the observed session context: " + reason);
        }
        var disabled = Configured(temporary); disabled.Automation.CollectOnStartup = false;
        Check(StartupCollectionPolicy.Evaluate(disabled, new()).Reason == "disabled", "The explicit local opt-out must prevent the startup offer.");
    }

    private static void Availability(string temporary)
    {
        var settings = Configured(temporary);
        Check(StartupCollectionPolicy.Evaluate(settings, new() { BrowserAvailable = false }).Reason == "browser-unavailable"
            && StartupCollectionPolicy.Evaluate(settings, new() { NetworkAvailable = false }).Reason == "offline",
            "An unavailable browser or network must leave the current observation available without attempting collection.");
        settings.Automation.ScheduledExtraction = false; settings.Automation.QuietHours = true;
        Check(StartupCollectionPolicy.Evaluate(settings, new()).Eligible, "Startup collection must not depend on enabling a recurring schedule.");
        settings.Automation.ScheduledExtraction = true; settings.Automation.RunCampaign = true; settings.Automation.UseRecipe = true;
        Check(StartupCollectionPolicy.Evaluate(settings, new()).Eligible, "Recurring campaign and recipe choices must not replace the startup policy for the active search.");
    }
}
