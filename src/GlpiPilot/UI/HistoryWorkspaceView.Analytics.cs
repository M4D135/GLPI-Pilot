using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class HistoryWorkspaceView
{
    private readonly ComboBox _analysisSource = new() { DisplayMemberPath = "Label", MinWidth = 240, MaxWidth = 430, Margin = new Thickness(0, 0, 8, 8) };
    private readonly TextBox _analysisCount = PilotControls.Input("60", 60), _cadence = PilotControls.Input("24", 60), _ticketHistoryId = PilotControls.Input("", 110);
    private readonly TextBlock _analysisSummary = PilotControls.Text("Choisissez une source et une cadence attendue, puis calculez les analyses.", 12, true);
    private readonly TabControl _analysisTabs = new();
    private readonly DataGrid _ticketObservations = PilotControls.Table();
    public event Action<string, int, int>? AnalysisRequested;
    public event Action<string, string>? TicketHistoryRequested;
    public event Action<string[]>? AnalysisTicketsRequested;

    private FrameworkElement BuildAnalyticsWorkspace()
    {
        var actions = new WrapPanel(); actions.Children.Add(_analysisSource);
        actions.Children.Add(PilotControls.Text("Relevés (2–120)", 11)); actions.Children.Add(_analysisCount);
        actions.Children.Add(PilotControls.Text("Cadence en heures", 11)); actions.Children.Add(_cadence);
        actions.Children.Add(PilotControls.Button("Analyser", () =>
        {
            if (_analysisSource.SelectedItem is not SnapshotMetadata selected) return;
            if (!int.TryParse(_analysisCount.Text, out var count) || count is < 2 or > 120 || !int.TryParse(_cadence.Text, out var hours) || hours is < 1 or > 720)
            { _analysisSummary.Text = "Choisissez 2 à 120 relevés et une cadence de 1 à 720 heures."; return; }
            AnalysisRequested?.Invoke(selected.Id, count, hours);
        }, "Comparer les relevés de la même source. Les collectes absentes et incompatibles interrompent les calculs."));
        var panel = new DockPanel(); DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions);
        DockPanel.SetDock(_analysisSummary, Dock.Top); panel.Children.Add(_analysisSummary); panel.Children.Add(_analysisTabs);
        return panel;
    }

    private FrameworkElement BuildIndexedTicketHistory()
    {
        var actions = new WrapPanel(); actions.Children.Add(PilotControls.Text("Identifiant du ticket", 12)); actions.Children.Add(_ticketHistoryId);
        actions.Children.Add(PilotControls.Button("Rechercher dans les archives", () =>
        {
            if (_analysisSource.SelectedItem is SnapshotMetadata source) TicketHistoryRequested?.Invoke(source.SourceKey, _ticketHistoryId.Text.Trim());
        }, "Utilise les index persistants par archive ; les index manquants ou endommagés sont reconstruits depuis les relevés vérifiés."));
        var panel = new DockPanel(); DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions); panel.Children.Add(_ticketObservations);
        PilotControls.Column(_ticketObservations, "Observation", "CapturedAt", 170, "{0:dd/MM/yyyy HH:mm}");
        PilotControls.Column(_ticketObservations, "Ticket", "TicketId", 90);
        PilotControls.Column(_ticketObservations, "Statut constaté", "Status", 180);
        PilotControls.Column(_ticketObservations, "Affectation constatée", "Assignee", -1);
        PilotControls.Column(_ticketObservations, "Comparable", "Comparable", 100);
        _ticketObservations.MouseDoubleClick += (_, _) => { if (_ticketObservations.SelectedItem is SnapshotStore.TicketObservation observation) OpenRequested?.Invoke(observation.SnapshotId); };
        return panel;
    }

    public void ShowTicketHistory(IReadOnlyList<SnapshotStore.TicketObservation> observations)
    { _ticketObservations.ItemsSource = observations; _tabs.SelectedIndex = 3; }
    public void ShowAnalysisStatus(string text) => _analysisSummary.Text = text;

    public void ShowAnalytics(SnapshotAnalyticsReport report)
    {
        _analysisSummary.Text = $"{report.SnapshotCount} relevés · {report.ComparablePairs} intervalles comparables · {report.BrokenPairs} ruptures · couverture temporelle {report.CoveragePercent:0.#} %\nPériode observée : {report.FirstObservation:g} → {report.LastObservation:g} · âge du dernier relevé : {report.FreshnessHours:0.#} h\n"
            + string.Join("\n", report.Warnings);
        _analysisTabs.Items.Clear();
        AddAnalysisTable("Mouvements", report.Movements,
            ("Du", "From", 160), ("Au", "To", 160), ("Apparus", "Appeared", 90), ("Absents", "Absent", 90), ("Solde", "Balance", 90),
            ("Résolutions observées", "Resolved", 140), ("Réouvertures", "Reopened", 120), ("Comparable", "Comparable", 90), ("Méthode / rupture", "Note", -1));
        _analysisTabs.Items.Add(new TabItem { Header = "Flux d’affectation", Content = TransferDiagram(report.Transfers) });
        _analysisTabs.Items.Add(new TabItem { Header = "Calendrier observé", Content = ActivityCalendar(report.Calendar) });
        AddAnalysisTable("Cohortes J+1 / 7 / 30", report.Cohorts,
            ("Première observation", "Date", 160), ("Effectif initial", "Initial", 100), ("J +", "HorizonDays", 60), ("Actifs", "Active", 80),
            ("Résolus / clos", "ResolvedOrClosed", 100), ("Absents", "Absent", 80), ("Statut inconnu", "Unknown", 100), ("Observation cible", "Observation", 170), ("Interprétation", "Note", -1));
        AddAnalysisTable("Durées conditionnelles par statut", report.StatusDurations,
            ("Ticket", "Ticket", 90), ("Statut", "Status", 180), ("Borne basse (h)", "MinimumHours", 120), ("Borne haute (h)", "MaximumHours", 120), ("Intervalles", "Intervals", 100), ("Hypothèse nécessaire", "Assumption", -1));
        AddAnalysisTable("Intervalles de statut", report.StatusIntervals,
            ("Ticket", "Ticket", 85), ("Statut avant", "Before", 140), ("Statut après", "After", 140), ("Du", "From", 160), ("Au", "To", 160),
            ("Borne maximale (h)", "MaximumHours", 140), ("Incertitude : borne minimale 0 h", "Note", -1));
        AddAnalysisTable("Variations expliquées", report.Variations,
            ("Indicateur", "Label", 200), ("Avant", "Before", 85), ("Après", "After", 85), ("Entrées", "EnteredContributors", 85),
            ("Sorties", "LeftContributors", 85), ("Changements internes", "ChangedContributors", 140), ("Écart expliqué", "ExplainedDelta", 110), ("Décomposition", "Note", -1));
        AddAnalysisTable("Scénarios du stock", report.Scenarios,
            ("Horizon (jours)", "HorizonDays", 110), ("Scénario central", "Central", 120), ("Borne basse", "Lower", 100),
            ("Borne haute", "Upper", 100), ("Échantillon", "Sample", 100), ("Hypothèses", "Note", -1));
        AddAnalysisTable("Variations inhabituelles", report.Anomalies,
            ("Observation", "At", 160), ("Indicateur", "Metric", 120), ("Valeur", "Value", 90), ("Médiane de référence", "ReferenceMedian", 150),
            ("Écart robuste", "RobustDeviation", 110), ("Signal", "Unusual", 80), ("Référence", "Note", -1));
        _tabs.SelectedIndex = 2;
    }
    private void AddAnalysisTable(string title, IEnumerable items, params (string Label, string Path, int Width)[] columns)
    {
        var grid = PilotControls.Table();
        foreach (var column in columns) PilotControls.Column(grid, column.Label, column.Path, column.Width);
        grid.ItemsSource = items; _analysisTabs.Items.Add(new TabItem { Header = title, Content = grid });
    }

    private FrameworkElement TransferDiagram(IReadOnlyList<ObservedTransfer> transfers)
    {
        var root = new DockPanel();
        var content = new StackPanel(); DockPanel.SetDock(content, Dock.Top); root.Children.Add(content);
        content.Children.Add(PilotControls.Text("Transferts constatés entre relevés comparables. Cliquez un flux pour retrouver ses tickets dans le relevé courant.", 12, true));
        var dimension = new ComboBox { ItemsSource = new[] { "Techniciens", "Groupes" }, SelectedIndex = 0, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(dimension); var host = new ContentControl(); content.Children.Add(host);
        var grid = PilotControls.Table(); PilotControls.Column(grid, "Dimension", "Dimension", 100); PilotControls.Column(grid, "Avant", "Before", -1);
        PilotControls.Column(grid, "Après", "After", -1); PilotControls.Column(grid, "Tickets distincts", "Count", 120); root.Children.Add(grid);
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is ObservedTransfer flow) AnalysisTicketsRequested?.Invoke(flow.Tickets); };
        void Render()
        {
            var filtered = transfers.Where(x => x.Dimension == dimension.SelectedItem?.ToString()).ToArray(); grid.ItemsSource = filtered;
            var shown = filtered.Take(12).ToArray(); var canvas = new Canvas { Width = 900, Height = Math.Max(80, shown.Length * 40 + 30) };
            var max = Math.Max(1, shown.Select(x => x.Count).DefaultIfEmpty(1).Max());
            for (var i = 0; i < shown.Length; i++)
            {
                var flow = shown[i]; var y = 20 + i * 40;
                var left = PilotControls.Text(flow.Before, 11); left.Width = 200; Canvas.SetLeft(left, 0); Canvas.SetTop(left, y - 8); canvas.Children.Add(left);
                var right = PilotControls.Text(flow.After, 11); right.Width = 200; Canvas.SetLeft(right, 690); Canvas.SetTop(right, y - 8); canvas.Children.Add(right);
                var line = new System.Windows.Shapes.Path { StrokeThickness = 2 + 15d * flow.Count / max, Opacity = .75,
                    Data = Geometry.Parse($"M 205,{y} C 350,{y - 14} 550,{y + 14} 680,{y}") };
                line.SetResourceReference(Shape.StrokeProperty, "AccentBrush"); line.ToolTip = $"{flow.Before} → {flow.After} · {flow.Count} tickets";
                line.MouseLeftButtonUp += (_, _) => AnalysisTicketsRequested?.Invoke(flow.Tickets); canvas.Children.Add(line);
                var count = PilotControls.Text(flow.Count.ToString(CultureInfo.CurrentCulture), 12); Canvas.SetLeft(count, 430); Canvas.SetTop(count, y - 23); canvas.Children.Add(count);
            }
            host.Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, MaxHeight = 300, StretchDirection = StretchDirection.DownOnly };
        }
        dimension.SelectionChanged += (_, _) => Render(); Render(); return root;
    }

    private static FrameworkElement ActivityCalendar(IReadOnlyList<ObservedActivity> calendar)
    {
        var root = new DockPanel(); var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(PilotControls.Text("Les jours sans collecte restent inconnus. La couleur décrit les changements constatés à la collecte et leur couverture.", 12, true));
        var months = calendar.Select(x => new DateTime(x.Date.Year, x.Date.Month, 1)).Distinct().OrderDescending().ToArray();
        var choice = new ComboBox { ItemsSource = months, ItemStringFormat = "MMMM yyyy", Width = 220, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Left }; heading.Children.Add(choice);
        var panel = new WrapPanel { Margin = new Thickness(8) }; root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        void Render()
        {
            panel.Children.Clear(); if (choice.SelectedItem is not DateTime month) return;
            var days = calendar.Where(x => x.Date.Year == month.Year && x.Date.Month == month.Month).ToArray();
            var max = Math.Max(1, days.Select(x => x.ChangesObserved).DefaultIfEmpty(1).Max());
            foreach (var day in days)
            {
                var text = PilotControls.Text($"{day.Date:ddd dd}\n{(day.Gap ? "— inconnu" : day.ChangesObserved + " évolutions")}\n{day.Observations} collecte(s)", 12);
                var box = new Border { Child = text, Width = 150, Padding = new Thickness(12), Margin = new Thickness(4), CornerRadius = new CornerRadius(8), ToolTip = day.Note };
                var brush = ((Brush)root.FindResource(day.Gap ? "WarningBrush" : "AccentBrush")).Clone(); brush.Opacity = day.Gap ? .15 : .12 + .5 * day.ChangesObserved / max; box.Background = brush;
                box.SetResourceReference(Border.BorderBrushProperty, day.Gap ? "WarningBrush" : "LineBrush"); box.BorderThickness = new Thickness(1); panel.Children.Add(box);
            }
        }
        choice.SelectionChanged += (_, _) => Render(); Render(); return root;
    }
}
