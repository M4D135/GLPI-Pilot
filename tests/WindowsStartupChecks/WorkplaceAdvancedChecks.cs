using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot.WindowsStartupChecks;

internal static partial class Program
{
    private static async Task ExerciseWorkplaceCompletionAsync(TabControl tabs)
    {
        _stage = "workplace completion: observations and reading positions";
        var readAt = new DateTimeOffset(new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Local));
        var detail = new TicketDetailResult
        {
            Ticket = new("42", "https://glpi.example.invalid/front/ticket.form.php?id=42"), Title = "Dossier synthétique", ReadAt = readAt,
            Sections = new() { new() { Name = "Traitement", Kind = "timeline", Data = new() { Headers = new() { "Type", "Date", "Auteur", "Contenu", "Précision" }, Rows = new()
            {
                new[] { "Suivi", "22/10/2026 09:00", "Alex", "Échange récent", "" },
                new[] { "Suivi", "20/10/2026 09:00", "Sam", "Échange antérieur", "" }
            } } } },
            ObservedItems = new()
            {
                new() { Kind = "task", Identity = "1", Title = "Tâche datée", Owner = "Alex", Status = "À faire", Due = "21/10/2026", ObservedAt = readAt },
                new() { Kind = "validation", Identity = "2", Title = "Accord", Owner = "Sam", Status = "En attente", RequestedAt = "20/10/2026", ObservedAt = readAt },
                new() { Kind = "document", Identity = "3", Title = "Pièce fictive.pdf", Url = "https://glpi.example.invalid/front/document.send.php?docid=3", SourceSection = "Documents" }
            }
        };
        var reader = new TicketReaderView();
        reader.Show(detail.Sections[0], detail.Sections[0].Data.Rows, false, readAt.AddDays(-1), new HashSet<string> { TicketReadingService.RowFingerprint(detail.Sections[0].Data.Rows[0]) });
        Add(tabs, "Lecture · nouveautés", reader, false); SelectView(tabs, reader); await FlushLayoutAsync();
        var cards = ((ListBox)reader.FindName("CardsList")).Items.Cast<object>().ToArray();
        string Title(object card) => card.GetType().GetProperty("Title", BindingFlags.Instance | BindingFlags.Public)!.GetValue(card)?.ToString() ?? "";
        Require(cards.Length == 3 && Title(cards[0]).Contains("Nouveau ou modifié") && Title(cards[1]) == "DERNIÈRE LECTURE" && Title(cards[2]) == "Suivi", "New-event highlighting or last-read boundary is misplaced.");
        Pass("Timeline places the last-read marker between dated events and highlights only a changed row");

        var document = new WorkplaceDocument(); var personal = WorkplaceStore.Remember(document, detail.Ticket, detail.Title);
        personal.ReadingPosition = new() { Type = "Suivi", Author = "Alex", From = new DateTime(2026, 10, 20), To = new DateTime(2026, 10, 22), WholeTicketSearch = "échange", IntelligenceSearch = "réseau", PageIndex = 1, ViewMode = 1 };
        var workspace = new TicketWorkplaceView(new TicketReaderView()); workspace.Show(document, personal, detail);
        Add(tabs, "Tickets · reprise avancée", workspace, false); SelectView(tabs, workspace); await FlushLayoutAsync();
        var restored = workspace.CapturePosition(personal.ReadingPosition);
        Require(restored.Author == "Alex" && restored.Type == "Suivi" && restored.From == personal.ReadingPosition.From && restored.To == personal.ReadingPosition.To && restored.WholeTicketSearch == "échange" && restored.IntelligenceSearch == "réseau", "A ticket-specific reading filter was not restored.");
        Pass("Ticket workspace restores author, type, date and both local search filters with its page position");

        var insights = workspace.Insights; var pages = WorkplaceField<TabControl>(workspace, "_pages");
        pages.SelectedItem = pages.Items.Cast<TabItem>().Single(page => ReferenceEquals(page.Content, insights)); await FlushLayoutAsync();
        Require(WorkplaceField<DataGrid>(insights, "_tasks").Items.Cast<TicketTaskInsight>().Single().Overdue == true
            && WorkplaceField<DataGrid>(insights, "_validations").Items.Cast<TicketValidationInsight>().Single().Pending == true, "Structured task/validation state was not bound to its grid.");
        var downloads = 0; insights.DocumentRequested += _ => downloads++;
        var insightPages = Descendants<TabControl>(insights).Single(); insightPages.SelectedItem = insightPages.Items.Cast<TabItem>().Single(page => page.Header as string == "Documents"); await FlushLayoutAsync();
        Require(downloads == 0, "Opening a document index triggered a download.");
        WorkplaceField<DataGrid>(insights, "_documents").SelectedIndex = 0; Click(insights, "Télécharger le document sélectionné");
        Require(downloads == 1, "An explicit document action did not carry its selected observation.");
        Pass("Structured deadline and validation grids render observed states; document downloads require a selected explicit action");

        var actions = workspace.Actions; pages.SelectedItem = pages.Items.Cast<TabItem>().Single(page => ReferenceEquals(page.Content, actions)); await FlushLayoutAsync();
        var adapter = new TicketActionAdapter { Kind = "assignment", Name = "Affectation fictive", Installation = "https://glpi.example.invalid/", ActionPath = "/front/ticket.form.php", FormSelector = "#assignment", FieldName = "_users_id_assign", SubmitName = "update", SubmitValue = "1", StructureHash = new string('a', 64) };
        actions.ShowCandidates(new[] { new TicketActionCandidate(adapter, new[] { new TicketActionChoice("7", "Alex"), new TicketActionChoice("8", "Sam") }, "7", "Aucun autre champ") });
        WorkplaceField<ComboBox>(actions, "_choice").SelectedIndex = 1;
        TicketActionRequest? requested = null; actions.PreviewRequested += request => requested = request; var confirmations = 0; actions.ConfirmRequested += _ => confirmations++;
        Click(actions, "Prévisualiser l’action");
        Require(requested?.Value == "8" && confirmations == 0 && !WorkplaceField<Button>(actions, "_confirm").IsEnabled, "A preview request also confirmed or carried a guessed actor value.");
        actions.ShowPreview(new("synthetic-preview", detail.Ticket, "Affectation", "Alex", "Sam (8)", "Aucun autre champ", DateTimeOffset.Now.AddMinutes(2)));
        Require(confirmations == 0, "Displaying an action preview caused confirmation.");
        Click(actions, "Confirmer l’aperçu et envoyer une fois…"); Require(confirmations == 1, "Explicit preview confirmation was not emitted exactly once.");
        WorkplaceField<ComboBox>(actions, "_choice").SelectedIndex = 0;
        Require(!WorkplaceField<Button>(actions, "_confirm").IsEnabled, "Changing the intended value did not invalidate its preview.");
        Pass("Assisted-action UI separates preview from explicit confirmation and invalidates it when its selected value changes");

        var drafts = workspace.Drafts; pages.SelectedItem = pages.Items.Cast<TabItem>().Single(page => ReferenceEquals(page.Content, drafts)); await FlushLayoutAsync();
        string changedKey = "", changedText = ""; drafts.DraftChanged += (key, text) => { changedKey = key; changedText = text; };
        WorkplaceField<TextBox>(drafts, "_findings").Text = "Constat en cours"; WorkplaceField<TextBox>(drafts, "_draft").Text = "Suivi non publié";
        var another = WorkplaceStore.Remember(document, new("43", detail.Ticket.Url.Replace("42", "43")), "Autre dossier");
        drafts.Show(another, null); drafts.Show(personal, detail);
        Require(changedKey == personal.Key && changedText == "Suivi non publié" && WorkplaceField<TextBox>(drafts, "_draft").Text == "Suivi non publié" && WorkplaceField<TextBox>(drafts, "_findings").Text == "Constat en cours", "Changing a ticket lost a composed or partially composed local followup draft.");
        Pass("Followup draft emits ticket-scoped persistence changes and preserves unfinished composition across ticket switches");

        var day = new PersonalDayView(); var dayDocument = document.Copy(); dayDocument.DaySearch = "Dossier"; dayDocument.DayScope = 6; dayDocument.DaySelectedKey = personal.Key;
        day.Show(new[] { new PersonalDayTicket(personal, "En cours", "Paris", "", true, true, true), new PersonalDayTicket(another, "État inconnu", "", "", null, false, false) }, dayDocument, "Alex", readAt);
        day.ShowTours(new[] { new ScheduledTourDayItem("tour-1", "Tournée fictive", DateTime.Today.ToString("yyyy-MM-dd"), "ready", "09:00", "17:00", "alex", new[] { "42" }) }, true);
        day.ShowBriefing(new("Repère synthétique", new[] { new PersonalBriefingLine(personal.Key, "42", "État modifié", "Nouveau → En cours") }));
        Add(tabs, "Ma journée · briefing et tournée", day, false); SelectView(tabs, day); await FlushLayoutAsync();
        Require(WorkplaceField<TextBox>(day, "_search").Text == "Dossier" && WorkplaceField<ComboBox>(day, "_scope").SelectedIndex == 6, "Personal day did not restore temporary filters.");
        Require(WorkplaceField<DataGrid>(day, "_tourGrid").Items.Count == 1 && WorkplaceField<DataGrid>(day, "_briefingGrid").Items.Count == 1, "Daily tours or the previous-session briefing were not populated.");
        var dayPages = WorkplaceField<TabControl>(day, "_pages"); foreach (var page in dayPages.Items.Cast<TabItem>()) { dayPages.SelectedItem = page; await FlushLayoutAsync(); }
        Pass("Personal day restores filters and renders scheduled tours beside its session briefing");

        var queue = workspace.Enrichment; pages.SelectedItem = pages.Items.Cast<TabItem>().Single(page => ReferenceEquals(page.Content, queue)); SelectView(tabs, workspace); await FlushLayoutAsync();
        queue.Show(new() { Label = "Absence historique", Limit = 1, Tickets = new() { detail.Ticket, new(another.Id, another.Url) }, Results = new() { new(detail.Ticket, "Lu", readAt, "En attente", 2, "Observation datée distincte") } });
        var requestedLimit = 0; queue.StartRequested += limit => requestedLimit = limit;
        Require(requestedLimit == 0 && WorkplaceField<DataGrid>(queue, "_grid").Items.Count == 2, "Displaying an enrichment queue started it or lost its unread candidate.");
        Click(queue, "Lancer la lecture de cette file"); Require(requestedLimit == 1, "Enrichment did not apply the explicit global read limit.");
        queue.SetBusy(true); Require(WorkplaceField<Button>(queue, "_cancel").IsEnabled && !WorkplaceField<Button>(queue, "_start").IsEnabled, "Running queue cannot be canceled or can be started twice.");
        Pass("Enrichment queue stays reviewable before launch, applies its global limit and exposes cancellation without rewriting source status");
    }
}
