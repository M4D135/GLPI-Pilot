using System.IO;
using System.Windows;
using GlpiPilot.Core;
using Microsoft.Win32;

namespace GlpiPilot;

public partial class MainWindow
{
    private void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        try
        {
            InvalidateTicketRequest();
            var settings = ReadSettings();
            var dialog = new SaveFileDialog { Title = "Exporter les réglages pour un collègue", Filter = "Configuration GLPI Pilot (*.json)|*.json", DefaultExt = ".json", AddExtension = true,
                FileName = "GLPI-Pilot-Configuration.json" };
            if (dialog.ShowDialog(this) != true) return;
            ConfigurationTransferService.Export(dialog.FileName, settings);
            SetStatus("Configuration et widgets exportés. Chaque collègue conserve son SSO et choisit son identité dans Techniciens.");
            AddLog("CONFIG · export des réglages, sections, correspondances et dispositions du dashboard ; identité du technicien et session conservées sur le poste.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        try
        {
            InvalidateTicketRequest();
            var dialog = new OpenFileDialog { Title = "Importer une configuration GLPI Pilot", Filter = "Configuration GLPI Pilot (*.json)|*.json", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            var imported = ConfigurationTransferService.Import(dialog.FileName, _settings);
            Directory.CreateDirectory(AppPaths.DataRoot);
            if (File.Exists(AppPaths.SettingsFile))
            {
                var backupFolder = Path.Combine(AppPaths.DataRoot, "Backups"); Directory.CreateDirectory(backupFolder);
                File.Copy(AppPaths.SettingsFile, Path.Combine(backupFolder, $"settings-before-import-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"), overwrite: false);
            }
            imported.Save();
            _settings = imported;
            ThemeService.Apply(_settings.Theme); UpdateThemeButton();
            LoadSettingsFields();
            ClearResult("Configuration importée. Cliquez sur Extraire tout pour charger le relevé de cette source.", retainPrevious: false);
            StatsText.Text = "Prêt à extraire";
            EmptyStateTitleText.Text = "Configuration importée";
            UpdateControls();
            SetStatus("Configuration et widgets importés. Réglages précédents sauvegardés ; identité personnelle, session SSO et dossier d’export conservés sur ce poste.");
            AddLog("CONFIG · import validé ; anciennes données retirées de l’aperçu ; réglages précédents sauvegardés.");
        }
        catch (Exception ex) { ShowError(ex); }
    }
}
