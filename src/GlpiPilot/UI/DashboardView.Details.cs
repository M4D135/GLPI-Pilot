using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class DashboardView
{
    private Action<Panel>? _detailBuilder;
    private string _detailTitle = "", _detailFilter = "";
    private string? _detailReturnWidget;
    private int _detailPage;
    private FrameworkElement? _detailHeading;

    private void ClearDetailView()
    {
        _detailBuilder = null; _detailHeading = null; _detailReturnWidget = null;
        _detailTitle = _detailFilter = ""; _detailPage = 0;
    }

    private void OpenDetails(DashboardWidgetSettings widget, string title, Action<Panel> builder)
    {
        if (_busy) return;
        _detailTitle = title; _detailBuilder = builder; _detailReturnWidget = widget.Id;
        _detailFilter = ""; _detailPage = 0; Render();
        Dispatcher.BeginInvoke(new Action(() => _detailHeading?.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void BuildDetailView()
    {
        var heading = new StackPanel();
        var back = DescribedCommand("← Retour au dashboard", "Retrouver la disposition et les widgets sans changer les données.", () =>
        {
            var returnId = _detailReturnWidget;
            ClearDetailView(); Render();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var target = _boardPanel?.Children.OfType<FrameworkElement>().FirstOrDefault(child => child.Tag as string == returnId);
                (target ?? _toolbar)?.BringIntoView();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        });
        AutomationProperties.SetAutomationId(back, "Dashboard.Detail.Back");
        back.HorizontalAlignment = HorizontalAlignment.Left; heading.Children.Add(back);
        heading.Children.Add(Text(_detailTitle, 22, weight: FontWeights.SemiBold, margin: new Thickness(0, 3, 0, 7)));
        _detailHeading = heading; _body.Children.Add(heading);
        var content = new StackPanel(); _detailBuilder!(content); _body.Children.Add(content);
    }

    /// <summary>Only a bounded page creates visual elements. The parent dashboard owns all vertical scrolling.</summary>
    private void PagedDetails<T>(Panel body, IReadOnlyList<T> source, Func<T, string> searchable,
        Func<T, FrameworkElement> createItem, string itemLabel, int pageSize = 20)
    {
        var search = Input(body, "Rechercher dans cette liste", _detailFilter, 240);
        AutomationProperties.SetAutomationId(search, "Dashboard.Detail.Search");
        Help(search, "Rechercher dans la liste complète", "La recherche porte sur toutes les entrées, y compris celles des autres pages.");
        var status = Text("", 11, "MutedBrush", margin: new Thickness(0, 8, 0, 5)); body.Children.Add(status);
        var content = new StackPanel(); body.Children.Add(content);
        var generated = new HashSet<Button>();
        void Fill(bool resetPage = false)
        {
            if (resetPage) _detailPage = 0;
            _commands.RemoveAll(pair => generated.Contains(pair.Button)); generated.Clear(); content.Children.Clear();
            var commandStart = _commands.Count;
            _detailFilter = search.Text;
            var query = _detailFilter.Trim();
            var filtered = source.Where(item => query.Length == 0 || CultureInfo.CurrentCulture.CompareInfo.IndexOf(searchable(item), query,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0).ToArray();
            var pages = Math.Max(1, (filtered.Length + pageSize - 1) / pageSize);
            _detailPage = Math.Clamp(_detailPage, 0, pages - 1);
            var first = _detailPage * pageSize;
            var current = filtered.Skip(first).Take(pageSize).ToArray();
            status.Text = filtered.Length == 0 ? $"Aucun résultat · {source.Count:N0} {itemLabel} dans la liste complète."
                : $"{first + 1:N0}–{first + current.Length:N0} sur {filtered.Length:N0} {itemLabel}"
                    + (query.Length > 0 ? $" · liste complète : {source.Count:N0}" : "") + $" · page {_detailPage + 1:N0} / {pages:N0}";
            void Pager(string position)
            {
                if (pages == 1) return;
                var bar = new WrapPanel { Margin = new Thickness(0, 5, 0, 7) };
                var previous = DescribedCommand("← Précédente", "Afficher la page précédente de cette liste.", () => { _detailPage--; Fill(); _detailHeading?.BringIntoView(); }, _detailPage > 0);
                AutomationProperties.SetAutomationId(previous, "Dashboard.Detail.Previous." + position); bar.Children.Add(previous);
                var next = DescribedCommand("Suivante →", "Afficher la page suivante de cette liste.", () => { _detailPage++; Fill(); _detailHeading?.BringIntoView(); }, _detailPage + 1 < pages);
                AutomationProperties.SetAutomationId(next, "Dashboard.Detail.Next." + position); bar.Children.Add(next);
                content.Children.Add(bar);
            }
            Pager("Top");
            foreach (var item in current)
            {
                var card = Card(createItem(item)); card.Padding = new Thickness(13); card.Margin = new Thickness(0, 0, 0, 8);
                content.Children.Add(card);
            }
            Pager("Bottom");
            foreach (var pair in _commands.Skip(commandStart)) generated.Add(pair.Button);
        }
        Fill(); search.TextChanged += (_, _) => Fill(true);
    }

    private static Expander Details(string label, FrameworkElement content)
    {
        var expandedContent = new Border { Child = content, Padding = new Thickness(0, 8, 0, 3) };
        var details = new Expander { Header = Text(label, 12), IsExpanded = false, Content = expandedContent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(details, label);
        return details;
    }

    private static string PreviewText(string value, int limit)
    {
        if (value.Length <= limit) return value;
        var end = limit;
        if (end > 0 && char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end])) end--;
        return value[..end].TrimEnd() + "…";
    }

    private void OpenDistribution(DashboardWidgetSettings widget, DashboardDistribution distribution)
    {
        OpenDetails(widget, distribution.Title, body =>
        {
            body.Children.Add(Text($"{distribution.Buckets.Count:N0} catégories · base : {distribution.Denominator:N0} lignes. Sélectionnez une catégorie pour afficher ses lignes.", 12, "MutedBrush"));
            if (!string.IsNullOrWhiteSpace(distribution.Note)) body.Children.Add(Details("Périmètre et méthode de cette répartition", Text(distribution.Note, 12, "MutedBrush")));
            PagedDetails(body, distribution.Buckets, bucket => bucket.Label,
                bucket => DistributionRow(distribution, bucket, widget, false, 1, fullLabel: true), "catégories");
        });
    }

    private void OpenAttention(DashboardWidgetSettings widget)
    {
        var rows = _data!.Attention.ToArray();
        OpenDetails(widget, "Tickets à regarder en priorité", body =>
        {
            body.Children.Add(Text("Classement par échéance dépassée, priorité puis ancienneté. Cette sélection contient au maximum 50 lignes du périmètre. Cliquez sur un ticket pour retrouver sa ligne dans Extraction.", 12, "MutedBrush"));
            PagedDetails(body, rows, row => string.Join(" ", row.Id, row.Title, row.Status, row.Priority, row.Group, row.Reasons),
                row => AttentionRow(row, full: true), "tickets prioritaires");
        });
    }

    private void OpenTimeline(DashboardWidgetSettings widget, DashboardSnapshot data)
    {
        var days = data.Timeline.ToArray();
        OpenDetails(widget, "Activité quotidienne du relevé", body =>
        {
            body.Children.Add(Text("Les événements concernent uniquement les tickets du relevé. Une date absente reste indisponible ; une journée sans événement connu affiche zéro.", 12, "MutedBrush"));
            PagedDetails(body, days, day => day.Date.ToString("dd/MM/yyyy dddd", CultureInfo.CurrentCulture), day =>
            {
                var row = new StackPanel(); row.Children.Add(Text(day.Date.ToString("dddd dd MMMM yyyy", CultureInfo.CurrentCulture), 14, weight: FontWeights.SemiBold));
                var counts = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
                foreach (var (key, label, indices) in new[] { ("created", "Créations", day.Created), ("resolved", "Résolutions", day.Resolved), ("closed", "Clôtures", day.Closed) })
                {
                    var available = data.Column(key)?.Index.HasValue == true;
                    var button = Command(label + " · " + (available ? indices.Length.ToString("N0", CultureInfo.CurrentCulture) : "indisponible"),
                        () => RowsRequested?.Invoke(indices, label + " · " + day.Date.ToString("dd/MM/yyyy")), available);
                    counts.Children.Add(button);
                }
                row.Children.Add(counts); return row;
            }, "journées", 14);
        });
    }

    private void OpenWarnings(DashboardWidgetSettings widget)
    {
        var warnings = _data!.Warnings.ToArray();
        OpenDetails(widget, "Avertissements du relevé", body =>
        {
            body.Children.Add(Text("Les textes complets restent consultables et copiables. Dépliez le diagnostic souhaité.", 12, "MutedBrush"));
            PagedDetails(body, warnings, warning => warning, warning =>
            {
                var full = new TextBox { Text = warning, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, BorderThickness = new Thickness(0) };
                return Details(PreviewText(warning, 180), full);
            }, "avertissements", 10);
        });
    }

    private void OpenColumns(DashboardWidgetSettings widget)
    {
        var columns = _data!.Columns.ToArray();
        OpenDetails(widget, "Colonnes utilisées pour les calculs", body =>
        {
            body.Children.Add(Command("Régler les correspondances", () => SettingsRequested?.Invoke()));
            PagedDetails(body, columns, column => column.Label + " " + column.Header + " " + column.State, column =>
            {
                var row = new StackPanel(); row.Children.Add(Text(column.Label, 14, weight: FontWeights.SemiBold));
                row.Children.Add(Text("Colonne : " + (string.IsNullOrEmpty(column.Header) ? "non reconnue" : column.Header), 12, margin: new Thickness(0, 6, 0, 0)));
                row.Children.Add(Text(column.State, 11, column.Index.HasValue ? "MutedBrush" : "WarningBrush", margin: new Thickness(0, 5, 0, 0)));
                return row;
            }, "rôles de colonnes");
        });
    }

    private void OpenProbes(DashboardWidgetSettings widget, IReadOnlyList<ProbeAvailability> probes)
    {
        OpenDetails(widget, "Sondes et conditions de calcul", body =>
        {
            body.Children.Add(Text($"{probes.Count(probe => probe.State == "Disponible"):N0} / {probes.Count:N0} sondes disponibles. Une donnée indisponible ne représente pas un zéro.", 12, "MutedBrush"));
            PagedDetails(body, probes, probe => string.Join(" ", probe.Label, probe.Kind, probe.State, probe.Explanation), probe =>
            {
                var row = new StackPanel(); row.Children.Add(Text(probe.Label, 14, weight: FontWeights.SemiBold));
                row.Children.Add(Text($"{probe.Kind} · {probe.State} · échantillon / base : {probe.Sample}", 11,
                    probe.State == "Disponible" ? "MutedBrush" : "WarningBrush", margin: new Thickness(0, 6, 0, 0)));
                row.Children.Add(Text(probe.Explanation, 12, "MutedBrush", margin: new Thickness(0, 7, 0, 0))); return row;
            }, "sondes");
        });
    }
}
