using System.IO;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot;

public partial class MainWindow
{
    private void OpenUpdateAssistant()
    {
        if (_busy || _closing) return;
        var view = new UpdateAssistantView(AppContext.BaseDirectory, AppPaths.ApplicationVersion, Path.Combine(AppPaths.DataRoot, "Updates"), Path.Combine(AppPaths.DataRoot, "update-policy.json"));
        view.InstallRequested += prepared =>
        {
            if (_busy || _closing) return;
            try
            {
                _settings.Save(); UpdateService.LaunchUpdater(prepared); AddLog("MISE À JOUR · pack " + prepared.Package.Version + " vérifié ; fermeture pour remplacement avec sauvegarde et retour arrière."); Close();
            }
            catch (Exception error) { ShowError(error); }
        };
        ShowInlinePanel(view);
    }
}
