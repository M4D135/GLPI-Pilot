using System.IO;

namespace GlpiPilot.Core;

public sealed record StartupCollectionContext
{
    public bool Simulation { get; init; }
    public bool LocalSource { get; init; }
    public bool HistoricalSource { get; init; }
    public bool Busy { get; init; }
    public bool EditorOpen { get; init; }
    public bool SessionLocked { get; init; }
    public bool AlreadyCollected { get; init; }
    public bool BrowserAvailable { get; init; } = true;
    public bool NetworkAvailable { get; init; } = true;
}

public sealed record StartupCollectionDecision(bool Eligible, string Reason, string Message);

/// <summary>Read-only startup eligibility; never opens a browser, writes a file or starts a campaign.</summary>
public static class StartupCollectionPolicy
{
    public static StartupCollectionDecision Evaluate(AppSettings settings, StartupCollectionContext context)
    {
        static StartupCollectionDecision Skip(string reason, string message) => new(false, reason, message);
        ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(context);
        if (settings.Automation is null || settings.Dashboard is null || settings.Cities is null || settings.Queries is null)
            return Skip("configuration-invalid", "Une section des réglages est absente ; vérifiez la configuration avant de collecter.");
        if (!settings.Automation.CollectOnStartup) return Skip("disabled", "La collecte à l’ouverture est désactivée sur ce poste.");
        if (context.Simulation) return Skip("simulation", "La démonstration et la simulation conservent leurs données locales.");
        if (context.LocalSource) return Skip("local-source", "Le fichier ou le relevé local reste affiché ; aucune collecte GLPI ne démarre à l’ouverture.");
        if (context.HistoricalSource) return Skip("history-source", "L’historique ouvert reste affiché ; aucune collecte GLPI ne démarre automatiquement.");
        if (context.AlreadyCollected) return Skip("already-collected", "Une collecte a déjà été lancée pendant cette ouverture.");
        if (context.SessionLocked) return Skip("session-locked", "La session est verrouillée ; la collecte à l’ouverture n’est pas lancée.");
        if (context.EditorOpen) return Skip("editor-open", "Un formulaire est ouvert ; la collecte à l’ouverture n’est pas lancée.");
        if (context.Busy) return Skip("busy", "Un traitement est déjà en cours ; la collecte à l’ouverture n’est pas mise en attente.");
        if (string.IsNullOrWhiteSpace(settings.PortalUrl) || string.IsNullOrWhiteSpace(settings.ExportFolder))
            return Skip("configuration-incomplete", "Renseignez la recherche GLPI et le dossier d’export pour collecter à l’ouverture.");
        try
        {
            ExtractionVerificationService.ValidateConfiguration(settings);
            if (settings.ColumnPreparationMode is not ("current" or "supervision" or "all")
                || settings.ListTimeoutSeconds is < 5 or > 600 || settings.DetailTimeoutSeconds is < 5 or > 600
                || settings.ReconnectAttempts is < 0 or > 5)
                return Skip("configuration-invalid", "Les limites ou le mode de collecte doivent être corrigés dans les réglages.");
            DashboardService.ValidateSettings(settings.Dashboard);
            settings.Cities.Validate(); settings.Queries.Validate();
            _ = Path.GetFullPath(settings.ExportFolder);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or ArgumentException or NotSupportedException)
        { return Skip("configuration-invalid", "La configuration doit être vérifiée avant une collecte à l’ouverture. " + AppPaths.SafeError(error)); }
        if (!context.BrowserAvailable) return Skip("browser-unavailable", "Le navigateur configuré est indisponible sur ce poste.");
        if (!context.NetworkAvailable) return Skip("offline", "La connexion réseau n’est pas disponible ; le dernier relevé reste affiché.");
        return new(true, "ready", "La recherche GLPI active sera collectée après l’ouverture de l’interface.");
    }

    public static bool BrowserAvailable(AppSettings settings)
    {
        if (!OperatingSystem.IsWindows() || !BrowserChannels.IsSupported(settings.BrowserChannel)
            || !BrowserChannels.IsAvailable(settings.BrowserChannel)) return false;
        if (settings.BrowserChannel == BrowserChannels.Firefox) return BrowserChannels.HasFirefoxSystemDependencies();
        var relative = settings.BrowserChannel == BrowserChannels.Chrome
            ? Path.Combine("Google", "Chrome", "Application", "chrome.exe")
            : Path.Combine("Microsoft", "Edge", "Application", "msedge.exe");
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData }
            .Select(Environment.GetFolderPath).Where(path => path.Length > 0).Any(path => File.Exists(Path.Combine(path, relative)));
    }
}
