using System.Runtime.CompilerServices;
using System.Windows;
using GlpiPilot.Core;

namespace GlpiPilot;

/// <summary>Records startup before WPF resources or the main window are created.</summary>
internal static class Program
{
    private static int _failureReported;

    [STAThread]
    public static int Main()
    {
        StartupDiagnostics.Start(AppPaths.ApplicationVersion);
        try { return RunApplication(); }
        catch (Exception error)
        {
            ReportStartupFailure(error);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication()
    {
        StartupDiagnostics.Step("Création de l’application WPF");
        var app = new App();
        app.RegisterExceptionHandlers();
        StartupDiagnostics.Step("Chargement des ressources de l’interface");
        app.InitializeComponent();
        StartupDiagnostics.Step("Démarrage de la boucle Windows");
        return app.Run();
    }

    internal static void ReportStartupFailure(Exception error)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) != 0) return;
        StartupDiagnostics.CaptureFailure(StartupDiagnostics.CurrentStage, error);
        try { ShowStartupFailure(error); }
        catch { /* A missing UI dependency must not prevent the file diagnostic. */ }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ShowStartupFailure(Exception error)
    {
        var path = StartupDiagnostics.LogPath;
        var diagnostic = string.IsNullOrWhiteSpace(path)
            ? "Le journal n’a pas pu être écrit. Lancez DIAGNOSTIC_DEMARRAGE.cmd dans wad\\support pour recueillir le résultat du lancement."
            : "Le diagnostic de démarrage est enregistré ici :\n" + path + "\n\nVous pouvez copier le contenu de ce fichier pour identifier le blocage.";
        MessageBox.Show("GLPI Pilot n’a pas pu ouvrir sa fenêtre.\n\nÉtape : "
            + StartupDiagnostics.CurrentStage + "\n" + AppPaths.SafeError(error)
            + "\n\nÀ essayer :\n" + StartupDiagnostics.RecoveryAdvice(error)
            + "\n\n" + diagnostic, "GLPI Pilot — démarrage interrompu", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
