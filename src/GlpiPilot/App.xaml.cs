using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using GlpiPilot.Core;

namespace GlpiPilot;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _exceptionHandlersRegistered;

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
        var userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16];
        _instance = new Mutex(true, "Local\\GLPIPilot_" + userKey, out var first);
        if (!first)
        {
            StartupDiagnostics.Step("Une autre instance est déjà ouverte");
            MessageBox.Show("GLPI Pilot est déjà ouvert dans cette session Windows.", "GLPI Pilot", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
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
                if (!StartupDiagnostics.IsReady) StartupDiagnostics.MarkReady();
            };
            StartupDiagnostics.Step("Affichage et premier rendu de la fenêtre");
            window.Show();
        }
        catch (Exception error)
        {
            Program.ReportStartupFailure(error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StartupDiagnostics.Step("Arrêt de l’application · code " + e.ApplicationExitCode);
        _instance?.Dispose();
        base.OnExit(e);
    }
}
