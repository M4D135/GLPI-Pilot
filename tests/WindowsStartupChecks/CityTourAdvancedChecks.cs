using System.Reflection;
using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot.WindowsStartupChecks;

internal static partial class Program
{
    private static async Task ExerciseCityAndScheduledTourAsync(TabControl tabs, DashboardSettings settings)
    {
        _stage = "city rules, hierarchy, sites, real simulation and local correction event";
        var editor = _cityEditor!; var original = editor.ReadSettings();
        var configured = Cities.Copy();
        configured.Cities[0].Region = "Région synthétique"; configured.Cities[0].Sector = "Secteur test";
        configured.Cities[0].Rules.Add(new() { Term = "PAR", RequiredTerms = new() { "Agence" }, Priority = 15 });
        configured.Sites.Add(new() { CityId = configured.Cities[0].Id, Name = "Bâtiment Nord", Latitude = 48.8, Longitude = 2.4 });
        int? correctionRow = null; string? correctionCity = null;
        void Correct(int row, string city, string site) { correctionRow = row; correctionCity = city; }
        editor.LocalCorrectionRequested += Correct;
        try
        {
            editor.LoadSettings(configured); SelectView(tabs, editor); await FlushLayoutAsync();
            await SelectSectionAsync(editor, "Simuler et expliquer");
            CityTourField<TextBox>(editor, "_testTitle").Text = "PAR agence panne";
            Click(editor, "Tester le titre"); await FlushLayoutAsync();
            var evidence = CityTourField<DataGrid>(editor, "_proofGrid").Items.Cast<CityMatchEvidence>().ToArray();
            Require(evidence.Length == 1 && evidence[0].Decision == "Retenue" && evidence[0].Priority == 15, "The actual city testbench did not use contextual rules.");
            var read = editor.ReadSettings();
            Require(read.Cities[0].Region == "Région synthétique" && read.Sites.Single().Latitude == 48.8 && read.Cities[0].Rules.Count == 1, "The city editor lost advanced hierarchy, rules or site coordinates.");

            var source = new ExtractionResult { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "41", "PAR ordinateur" } }, Complete = true, SourceUrl = "https://glpi.example.invalid/front/ticket.php" };
            editor.ShowSummary(CityDetectionService.Enrich(source, configured, new()));
            await SelectSectionAsync(editor, "Lieux à vérifier"); await FlushLayoutAsync();
            var queue = CityTourField<DataGrid>(editor, "_unlocatedGrid"); Require(queue.Items.Count == 1, "Unmatched ticket did not enter the city review queue."); queue.SelectedIndex = 0;
            CityTourField<ComboBox>(editor, "_correctionCity").SelectedValue = configured.Cities[0].Id;
            Click(editor, "Corriger localement");
            Require(correctionRow == 0 && correctionCity == configured.Cities[0].Id, "A local city correction did not retain the exact original row and canonical city.");
            Click(editor, "Revenir à la détection"); Require(correctionCity == "", "Removing a manual correction did not emit the explicit reset.");
            await SelectSectionAsync(editor, "Sites"); await FlushLayoutAsync();
            Require(CityTourField<DataGrid>(editor, "_sitesGrid").Items.Count == 1, "The real site reference did not render.");
            Pass("City advanced UI retains hierarchy and coordinates, explains contextual rules and emits explicit local corrections");
        }
        finally { editor.LocalCorrectionRequested -= Correct; editor.LoadSettings(original); editor.ShowSummary(_cities); }

        _stage = "dated tour basket, live scheduling, saved editing and scenarios";
        var tours = _tours!; SelectView(tabs, tours); await SelectSectionAsync(tours, "Tournées datées");
        var dated = CityTourField<ScheduledTourView>(tours, "_scheduledView");
        await tours.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
        Require(CityTourField<DataGrid>(dated, "_basket").Items.Count == 2, "The dated tour basket did not use the current active cohort.");
        Click(dated, "Tout sélectionner"); Click(dated, "Préparer les tickets cochés");
        await dated.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
        Require(dated.Draft is { Tickets.Count: 2, Stops.Count: 2 }, "The real dated tour workflow did not build the explicit ticket basket.");
        var originalId = dated.Draft!.Id;
        var duration = Descendants<TextBox>(CityTourField<StackPanel>(dated, "_stopEditor"))
            .Single(box => BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == nameof(ScheduledTourStop.DurationMinutes));
        duration.Text = "75"; Click(dated, "Calculer le planning");
        await dated.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
        Require(CityTourField<TourSchedule>(dated, "_schedule").Technicians.Sum(item => item.InterventionMinutes) == 105, "Edited intervention duration did not reach the calculated schedule.");
        Click(dated, "Enregistrer la tournée");
        var path = Path.Combine(TemporaryRoot, "scheduled-tours.json");
        Require(ScheduledTourStore.Load(path).Tours.Single().Stops[0].DurationMinutes == 75, "The dated tour was not saved in the isolated test directory.");
        duration.Text = "90"; Click(dated, "Calculer le planning");
        await dated.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync(); Click(dated, "Enregistrer la tournée");
        Require(ScheduledTourStore.Load(path).Tours.Single().Stops[0].DurationMinutes == 90, "Editing an already-saved stop changed a detached object instead of the draft.");
        Require(CityTourField<Button>(dated, "_calendar").IsEnabled, "A valid current dated tour did not enable calendar export.");
        Click(dated, "Créer un scénario"); Click(dated, "Enregistrer la tournée");
        var store = ScheduledTourStore.Load(path);
        Require(store.Tours.Count == 2 && store.Tours.Select(tour => tour.Id).Distinct().Count() == 2 && store.Tours.Single(tour => tour.Id == originalId).Stops[0].DurationMinutes == 90,
            "Creating and saving a scenario replaced the original tour.");
        var sections = CityTourField<TabControl>(dated, "_tabs");
        foreach (var section in sections.Items.Cast<TabItem>())
        {
            sections.SelectedItem = section; await FlushLayoutAsync();
            Require(section.Content is FrameworkElement { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }, "A dated-tour section failed to render: " + section.Header);
        }
        sections.SelectedIndex = 1; await FlushLayoutAsync(); CaptureView("v2-tours-dated");
        var removedId = dated.Draft!.Id;
        Click(dated, "Retirer du registre");
        Require(dated.Draft?.Id == removedId && ScheduledTourStore.Load(path).Tours.All(tour => tour.Id != removedId),
            "Removing a tour should preserve its visible draft while deleting the saved copy.");
        Click(dated, "Nouvelle tournée");
        Require(ScheduledTourStore.Load(path).Tours.Count == 1 && ScheduledTourStore.Load(path).Tours.All(tour => tour.Id != removedId),
            "Switching after removal silently saved the deleted tour again.");
        Pass("Dated tour UI builds a ticket basket, persists duration edits, enables ICS and keeps scenarios and explicit removal independent");
    }

    private static T CityTourField<T>(object owner, string name) where T : class =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) as T
        ?? throw new InvalidOperationException("City/tour field not available: " + name);
}
