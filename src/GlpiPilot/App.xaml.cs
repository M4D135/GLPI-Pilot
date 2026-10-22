using System.IO;
using System.Windows;
using GlpiPilot.Core;

namespace GlpiPilot;

public partial class App : Application
{
    private SingleInstanceCoordinator? _instance;
    private bool _exceptionHandlersRegistered;
    private bool _activationRequested;
    private bool _backupPrepared;

    internal void RegisterExceptionHandlers()
    {
        if (_exceptionHandlersRegistered) return;
        _exceptionHandlersRegistered = true;
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (!StartupDiagnostics.IsReady)
            {
                // A constructor/first-render error must never leave a windowless process.
                Program.ReportStartupFailure(args.Exception);
                Shutdown(1);
                return;
            }
            AppPaths.AppendLog("Erreur inattendue : " + AppPaths.SafeError(args.Exception));
            try
            {
                if (MainWindow is MainWindow window) window.ShowError(args.Exception);
                else MessageBox.Show(AppPaths.SafeError(args.Exception), "GLPI Pilot", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception reportingError)
            {
                AppPaths.AppendLog("Affichage de l’erreur impossible : " + AppPaths.SafeError(reportingError));
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is not Exception error) return;
            if (!StartupDiagnostics.IsReady) StartupDiagnostics.CaptureFailure(StartupDiagnostics.CurrentStage, error);
            else AppPaths.AppendLog("Arrêt inattendu : " + AppPaths.SafeError(error));
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppPaths.AppendLog("Erreur de fond : " + AppPaths.SafeError(args.Exception));
            args.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterExceptionHandlers();
        StartupDiagnostics.Step("Vérification de l’instance Windows");
        _instance = new SingleInstanceCoordinator();
        if (!_instance.IsPrimary)
        {
            StartupDiagnostics.Step("Une autre instance est déjà ouverte");
            var signaled = _instance.ActivateExistingAsync().GetAwaiter().GetResult();
            StartupDiagnostics.Step(signaled ? "Demande de retour à la fenêtre existante transmise" : "La fenêtre existante ne répond pas au signal d’activation");
            if (!signaled)
                MessageBox.Show("GLPI Pilot est déjà ouvert dans cette session Windows.\n\nOuvrez sa fenêtre depuis la barre des tâches. Si son démarrage est encore en cours, patientez quelques secondes.\n\nLe diagnostic de ce lancement est disponible ici :\n" + StartupDiagnostics.LogPath,
                    "GLPI Pilot", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        _instance.Listen(() =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke((Action)ActivateExistingWindow);
        }, error => StartupDiagnostics.Step("Signal d’activation indisponible : " + AppPaths.SafeError(error)));
        StartupDiagnostics.Step("Sauvegarde préalable des préférences en cas de nouvelle version");
        try
        {
            var backup = StartupBackupService.Prepare(AppPaths.DataRoot, AppPaths.ApplicationVersion);
            _backupPrepared = true;
            StartupDiagnostics.Step(backup.Message + (backup.Files > 0 ? " Fichiers : " + backup.Files + "." : ""));
        }
        catch (Exception error)
        {
            StartupDiagnostics.Step("Sauvegarde préalable non réalisée : " + AppPaths.SafeError(error));
        }
        base.OnStartup(e);
        try
        {
            StartupDiagnostics.Step("Construction de la fenêtre principale");
            var window = new MainWindow();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.ContentRendered += (_, _) =>
            {
                if (!StartupDiagnostics.IsReady)
                {
                    StartupDiagnostics.MarkReady();
                    if (_backupPrepared)
                    {
                        try { StartupBackupService.MarkReady(AppPaths.DataRoot, AppPaths.ApplicationVersion); }
                        catch (Exception error) { StartupDiagnostics.Step("Repère de version non enregistré : " + AppPaths.SafeError(error)); }
                    }
                }
            };
            StartupDiagnostics.Step("Affichage et premier rendu de la fenêtre");
            window.Show();
            if (_activationRequested) ActivateExistingWindow();
        }
        catch (Exception error)
        {
            Program.ReportStartupFailure(error);
            Shutdown(1);
        }
    }

    private void ActivateExistingWindow()
    {
        _activationRequested = true;
        if (MainWindow is not Window window || Dispatcher.HasShutdownStarted) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        _ = window.Activate();
        _activationRequested = false;
        StartupDiagnostics.Step("Fenêtre existante rappelée à la demande d’un nouveau lancement");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StartupDiagnostics.Step("Arrêt de l’application · code " + e.ApplicationExitCode);
        _instance?.Dispose();
        base.OnExit(e);
    }
}
