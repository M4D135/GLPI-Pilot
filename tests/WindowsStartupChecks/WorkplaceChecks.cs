using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot.WindowsStartupChecks;

internal static partial class Program
{
    private static async Task ExerciseWorkplaceAsync(TabControl tabs)
    {
        _stage = "personal workplace and ticket tabs";
        var detail = new TicketDetailResult
        {
            Ticket = new("42", "https://glpi.example.invalid/front/ticket.form.php?id=42"), Title = "Paris réseau synthétique", ReadAt = Sample().ExtractedAt,
            Sections = new()
            {
                new() { Name = "Description", Kind = "fields", Data = new() { Headers = new() { "Champ", "Valeur" }, Rows = new() { new[] { "Description", "Panne réseau synthétique" } } } },
                new() { Name = "Traitement", Kind = "timeline", TabKey = "treatment", Data = new() { Headers = new() { "Type", "Date", "Auteur", "Contenu", "Informations" }, Rows = new()
                {
                    new[] { "Suivi", "20/10/2026 08:00", "Alex Exemple", "Réseau contrôlé", "Public" },
                    new[] { "Tâche", "21/10/2026 08:00", "Sam Exemple", "Vérifier le réseau", "État fourni par la source" }
                } } }
            }
        };
        var document = new WorkplaceDocument(); var first = WorkplaceStore.Remember(document, detail.Ticket, detail.Title);
        first.Pinned = true; first.QueueOrder = 0; first.Notes = "Note conservée";
        var second = WorkplaceStore.Remember(document, new("43", detail.Ticket.Url.Replace("42", "43")), "Second ticket synthétique");
        var reader = new TicketReaderView(); reader.Show(detail.Sections[1], detail.Sections[1].Data.Rows, true);
        var workplace = new TicketWorkplaceView(reader); workplace.Show(document, first, detail);
        Add(tabs, "Tickets · espace personnel", workplace, false); SelectView(tabs, workplace); await FlushLayoutAsync();
        var strip = WorkplaceField<StackPanel>(workplace, "_ticketTabs");
        Require(strip.Children.Count == 2, "Two internal ticket tabs were not created.");
        PersonalTicket? requested = null; workplace.TicketRequested += value => requested = value;
        ((Button)strip.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(requested?.Key == second.Key, "Switching a ticket tab did not preserve its scoped identity.");
        var list = (ListBox)reader.FindName("CardsList");
        Require(list.Items.Groups?.Count == 2, "The real GLPI timeline order did not produce two day groups.");
        Require(Descendants<Expander>(list).Count() == 2, "Timeline day groups are not collapsible in the rendered template.");
        Pass("Internal ticket tabs preserve portal identity and real GLPI timeline columns create collapsible day groups");

        var pages = WorkplaceField<TabControl>(workplace, "_pages");
        foreach (var page in pages.Items.Cast<TabItem>())
        {
            pages.SelectedItem = page; await FlushLayoutAsync();
            Require(page.Content is FrameworkElement { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }, "Ticket workspace page failed to render: " + page.Header);
        }
        pages.SelectedItem = pages.Items.Cast<TabItem>().Single(page => page.Header as string == "Chercher partout"); await FlushLayoutAsync();
        WorkplaceField<TextBox>(workplace, "_search").Text = "reseau"; Click(workplace, "Rechercher");
        await workplace.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5)); await FlushLayoutAsync();
        var matches = WorkplaceField<DataGrid>(workplace, "_searchResults").Items.Cast<TicketSearchHit>().ToArray();
        Require(matches.Length == 3 && matches.Select(item => item.SectionIndex).Distinct().Count() == 2, "Whole-ticket UI search did not search all collected sections accent-insensitively.");
        Pass("Ticket search executes across every collected section and retains original source coordinates");

        workplace.ShowPersonal(); await FlushLayoutAsync(); PersonalTicket? saved = null;
        workplace.Editor.SaveRequested += value => saved = value;
        WorkplaceField<TextBox>(workplace.Editor, "_notes").Text = "Nouvelle note saisie";
        WorkplaceField<TextBox>(workplace.Editor, "_checklist").Text = "[x] Action terminée\n[ ] Action suivante";
        WorkplaceField<DatePicker>(workplace.Editor, "_reminderDate").SelectedDate = DateTime.Today;
        WorkplaceField<TextBox>(workplace.Editor, "_reminderTime").Text = "11:30";
        Click(workplace.Editor, "Enregistrer mon travail");
        Require(saved?.Notes == "Nouvelle note saisie" && saved.Checklist.Count == 2 && saved.Checklist[0].Done && !saved.Checklist[1].Done && saved.ReminderAt?.LocalDateTime.Hour == 11,
            "Personal editor failed to emit a note/checklist/reminder draft.");
        Require(first.Notes == "Note conservée" && first.Checklist.Count == 0, "The personal editor mutated caller state before an explicit save event was applied.");
        Pass("Personal editor emits a complete notes/checklist/reminder draft without mutating input state or GLPI");

        var notesField = WorkplaceField<TextBox>(workplace.Editor, "_notes");
        var checklistField = WorkplaceField<TextBox>(workplace.Editor, "_checklist");
        workplace.Editor.RejectSave(saved!, "Écriture indisponible pour ce contrôle");
        workplace.Show(document, first, detail);
        Require(notesField.Text == "Nouvelle note saisie" && checklistField.Text.Contains("[x] Action terminée"),
            "Refreshing a ticket after a failed save erased its draft.");
        workplace.Show(document, second, null);
        notesField.Text = "Brouillon du second dossier";
        var refreshedFirst = first.Copy(); refreshedFirst.LastReadAt = DateTimeOffset.Now;
        refreshedFirst.SectionFingerprints["new-section"] = new string('a', 64);
        refreshedFirst.ReminderAcknowledgedAt = DateTimeOffset.Now;
        workplace.Show(document, refreshedFirst, detail);
        Require(notesField.Text == "Nouvelle note saisie", "Switching ticket tabs erased the first ticket's draft.");
        Click(workplace.Editor, "Enregistrer mon travail");
        Require(saved?.SectionFingerprints.ContainsKey("new-section") == true && saved.LastReadAt == refreshedFirst.LastReadAt,
            "Saving a retained draft replaced newer reading metadata with stale editor state.");
        workplace.Editor.AcceptSaved(saved!);
        workplace.Show(document, second, null);
        Require(notesField.Text == "Brouillon du second dossier", "Saving one ticket discarded another ticket's draft.");
        workplace.Show(document, saved!, detail);
        Require(notesField.Text == "Nouvelle note saisie" && !WorkplaceField<TextBlock>(workplace.Editor, "_message").Text.Contains("Brouillon conservé"),
            "A confirmed save did not clear the matching draft.");
        Pass("Ticket drafts survive refresh, tab switches and failed saves; confirmed saves preserve current reading metadata");

        var relatedSource = new ExtractionResult
        {
            SourceUrl = "https://glpi.example.invalid/front/ticket.php", ExtractedAt = detail.ReadAt, Complete = true,
            Headers = new() { "ID", "Titre", "Statut", "Ville", "Catégorie", "Solution" },
            Rows = new() { new[] { "42", "Paris réseau synthétique", "En cours", "Paris", "Réseau", "" }, new[] { "43", "Paris réseau synthétique", "Résolu", "Paris", "Réseau", "Solution source fictive" } }
        };
        workplace.Intelligence.Show(TicketIntelligenceService.Analyze(detail, relatedSource, new()));
        pages.SelectedItem = pages.Items.Cast<TabItem>().Single(page => page.Header as string == "Rapprochements"); await FlushLayoutAsync();
        TicketReference? relatedRequest = null; workplace.Intelligence.TicketRequested += value => relatedRequest = value;
        Click(workplace.Intelligence, "Lire ce ticket dans GLPI");
        Require(relatedRequest?.Id == "43" && relatedRequest.Url == "https://glpi.example.invalid/front/ticket.form.php?id=43", "Related-ticket action did not preserve the proven source identity.");
        var intelligencePages = Descendants<TabControl>(workplace.Intelligence).Single(); intelligencePages.SelectedIndex = 1; await FlushLayoutAsync();
        var solution = WorkplaceField<TextBox>(workplace.Intelligence, "_solution").Text;
        Require(solution.Contains("Solution source fictive") && solution.Contains("Lecture :") && solution.Contains("ticket.form.php?id=43"), "Collected solution did not render its real text, source and observation date.");
        Pass("Local ticket intelligence displays collected solutions with provenance and opens only an explicit selected source ticket");

        _stage = "personal day and local dispatch";
        var technicians = _technicians!; SelectView(tabs, technicians); technicians.ShowPersonalDay();
        var day = technicians.Day;
        var records = new[] { new PersonalDayTicket(first, "En cours", "Paris", "", true, true, true), new PersonalDayTicket(second, "État inconnu", "", "", null, false, false) };
        day.Show(records, document, "Alex Exemple", Sample().ExtractedAt); await FlushLayoutAsync();
        var dayGrid = WorkplaceField<DataGrid>(day, "_grid");
        Require(dayGrid.Items.Count == 1 && dayGrid.EnableRowVirtualization, "Today queue is not filtered and virtualized.");
        Click(day, "Mon travail"); await FlushLayoutAsync();
        var dayEditor = WorkplaceField<PersonalTicketEditorView>(day, "_editor"); var notes = WorkplaceField<TextBox>(dayEditor, "_notes");
        notes.Text = "Brouillon non enregistré"; day.Show(records, document, "Alex Exemple", Sample().ExtractedAt);
        Require(notes.Text == "Brouillon non enregistré", "A reminder refresh erased unsaved personal notes.");
        WorkplaceField<ComboBox>(day, "_templateChoice").SelectedIndex = 0;
        var templateLines = WorkplaceField<TextBox>(day, "_templateLines");
        templateLines.Text = "Étape en cours de rédaction";
        day.Show(records, document, "Alex Exemple", Sample().ExtractedAt);
        Require(templateLines.Text == "Étape en cours de rédaction", "A reminder refresh erased an unsaved checklist-template draft.");
        var dayPages = WorkplaceField<TabControl>(day, "_pages");
        foreach (var page in dayPages.Items.Cast<TabItem>()) { dayPages.SelectedItem = page; await FlushLayoutAsync(); }
        dayPages.SelectedIndex = 0; WorkplaceField<ComboBox>(day, "_scope").SelectedIndex = 6; await FlushLayoutAsync();
        Require(dayGrid.Items.Count == 2, "Saved absent-from-source ticket disappeared from personal retained view.");
        Pass("Personal day keeps absent tickets explicitly unknown, filters today's work and preserves unsaved notes through refresh");

        var effective = Settings.Copy(); effective.ColumnMappings["city"] = _cities!.CityColumnName;
        var workload = TechnicianWorkloadService.Calculate(_cities.Data, effective, referenceTime: Sample().ExtractedAt);
        var alex = TechnicianDashboardService.IdentityKey("Alex Exemple"); var sam = TechnicianDashboardService.IdentityKey("Sam Exemple");
        var preferences = new TechnicianPreferences().With(alex, new() { Skills = new() { "Réseau" }, PrimaryTerritories = new() { "Paris" }, DailyCapacityMinutes = 420, EstimatedTicketMinutes = 30 })
            .With(sam, new() { Skills = new() { "Accès" }, BackupTerritories = new() { "Paris" }, DailyCapacityMinutes = 300, EstimatedTicketMinutes = 45 });
        var planning = technicians.Planning;
        planning.SetScenario(new()); planning.Show(workload, preferences, new[] { new DispatchTicket(first.Key, first.Id, first.Title, "Paris", "Réseau", new[] { alex }, 0) });
        technicians.RestoreWorkspace("planning"); await FlushLayoutAsync();
        WorkplaceField<TextBox>(planning, "_requiredSkills").Text = "Réseau";
        var recommendations = WorkplaceField<DataGrid>(planning, "_recommendations");
        var candidate = recommendations.Items.Cast<DispatchRecommendation>().Single(item => item.TechnicianKey == alex);
        recommendations.SelectedItem = candidate; Click(planning, "Proposer le technicien sélectionné");
        Require(WorkplaceField<DispatchScenario>(planning, "_scenario").Assignments.Single().TechnicianKey == alex,
            "Scenario did not retain the selected eligible technician.");
        Require(WorkplaceField<DataGrid>(planning, "_loads").Items.Count == 2 && candidate.Evidence.Contains("principal"), "Scenario load or explanation was absent.");
        var planningTabs = Descendants<TabControl>(planning).First();
        foreach (var page in planningTabs.Items.Cast<TabItem>()) { planningTabs.SelectedItem = page; await FlushLayoutAsync(); }
        CaptureView("workplace-dispatch");
        Pass("Dispatch workspace renders coverage, before/after estimates and local handover with explained skill/territory recommendations");
        technicians.RestoreWorkspace("team");
        workplace.ShowReader();
        await ExerciseWorkplaceCompletionAsync(tabs);
    }

    private static T WorkplaceField<T>(object owner, string name) where T : class =>
        owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner) as T
        ?? throw new InvalidOperationException("Workplace control field unavailable: " + name);
}
