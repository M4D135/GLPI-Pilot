using GlpiPilot.Core;
using GlpiPilot.Desktop;

namespace GlpiPilot;

internal static class Program
{
    private static int _failureReported;
    [STAThread]
    public static int Main()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (UpdateService.TryRunMaintenance(args, out var maintenanceCode)) return maintenanceCode;
        StartupDiagnostics.Start(AppPaths.ApplicationVersion);
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            using var instance = new SingleInstanceCoordinator();
            if (!instance.IsPrimary)
            {
                StartupDiagnostics.Step("Rappel de l’instance déjà ouverte");
                if (!instance.ActivateExistingAsync().GetAwaiter().GetResult())
                    MessageBox.Show("GLPI Pilot est déjà ouvert. Retrouvez sa fenêtre dans la barre des tâches ; son démarrage peut encore être en cours.", "GLPI Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            var prepared = false;
            try { var backup = StartupBackupService.Prepare(AppPaths.DataRoot, AppPaths.ApplicationVersion); prepared = true; StartupDiagnostics.Step(backup.Message); }
            catch (Exception error) { StartupDiagnostics.Step("Sauvegarde préalable : " + AppPaths.SafeError(error)); }
            var sites = SiteReferenceInitializer.TryInitialize(AppContext.BaseDirectory, AppPaths.SettingsFile);
            if (sites.Message.Length > 0) StartupDiagnostics.Step(sites.Message);
            using var window = new MainForm(args, prepared);
            instance.Listen(window.ActivateExisting, error => AppPaths.AppendLog("Activation : " + AppPaths.SafeError(error)));
            Application.ThreadException += (_, eventArgs) =>
            {
                if (!StartupDiagnostics.IsReady) { ReportStartupFailure(eventArgs.Exception); window.Close(); }
                else window.ReportError(eventArgs.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            { if (eventArgs.ExceptionObject is Exception error) StartupDiagnostics.CaptureFailure(StartupDiagnostics.CurrentStage, error); };
            TaskScheduler.UnobservedTaskException += (_, eventArgs) => { AppPaths.AppendLog("Traitement de fond : " + AppPaths.SafeError(eventArgs.Exception)); eventArgs.SetObserved(); };
            StartupDiagnostics.Step("Ouverture de l’hôte Windows et de l’interface WebView2");
            Application.Run(window);
            StartupDiagnostics.Step("Arrêt de l’application");
            return window.ExitCode;
        }
        catch (Exception error) { ReportStartupFailure(error); return 1; }
    }
    internal static void ReportStartupFailure(Exception error)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) != 0) return;
        StartupDiagnostics.CaptureFailure(StartupDiagnostics.CurrentStage, error);
        try
        {
            MessageBox.Show("GLPI Pilot n’a pas pu ouvrir son interface.\n\n" + AppPaths.SafeError(error) +
                "\n\nRelancez GLPI-Pilot.exe depuis le pack complet, avec son dossier wad.\n\nDiagnostic :\n" + StartupDiagnostics.LogPath,
                "GLPI Pilot — démarrage interrompu", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* Keep startup evidence when the desktop is unavailable. */ }
    }
}
