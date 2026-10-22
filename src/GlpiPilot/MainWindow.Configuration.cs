using System.IO;
using System.Windows;
using GlpiPilot.Core;
using GlpiPilot.UI;
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
            ReviewConfiguration(imported, "Importer la configuration");
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ReviewConfiguration(AppSettings imported, string title)
    {
        var review = new ConfigurationReviewView(_settings, imported, title);
        review.ApplyRequested += (parts, merge) =>
        {
            if (_busy || _closing) return;
            try
            {
                var settings = ConfigurationReview.Apply(_settings, imported, parts, merge);
                settings.Save(); _settings = settings;
                CloseInlinePanel(); LoadSettingsFields(); _controlCenter?.Load(_settings);
                ApplyInterfacePreferences(); UpdateThemeButton(); ResetAutomaticSchedule();
                ClearResult("Configuration appliquée. Cliquez sur Extraire tout pour charger le relevé de cette source.", retainPrevious: false);
                StatsText.Text = "Prêt à extraire"; EmptyStateTitleText.Text = "Configuration appliquée"; UpdateControls();
                SetStatus("Configuration appliquée. La version précédente est disponible dans Sources et organisation → Réglages sauvegardés.");
                AddLog("CONFIG · sections choisies appliquées ; réglages précédents archivés, préférences locales conservées.");
            }
            catch (Exception error) { review.ShowError(AppPaths.SafeError(error)); }
        };
        ShowInlinePanel(review);
    }

}
