using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>Selectable, wrapping cards with a compiled template. Values are always bound as plain text.</summary>
public partial class TicketReaderView : UserControl
{
    public TicketReaderView() => InitializeComponent();

    public void Show(TicketDetailSection section, IEnumerable<string[]> rows, bool groupByDay = false, DateTimeOffset? previousRead = null, IReadOnlySet<string>? changedRows = null, double scrollOffset = 0)
    {
        var ordered = section.Kind == "timeline" ? rows.OrderByDescending(row => row.Length > 1 && TicketReadingService.TryDate(row[1], out var parsed) ? parsed : DateTime.MinValue) : rows;
        var cards = ordered.Select((row, index) =>
        {
            string At(int i) => i < row.Length ? row[i] : "";
            var changed = changedRows?.Contains(TicketReadingService.RowFingerprint(row)) == true;
            var prefix = changed ? "● Nouveau ou modifié · " : "";
            if (section.Kind == "timeline") return new ReaderCard(prefix + At(0), string.Join(" · ", new[] { At(1), At(2) }.Where(value => value.Length > 0)), At(3), At(4),
                TicketReadingService.TryDate(At(1), out var date) ? date.ToString("dddd dd MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")) : "Date absente ou non interprétée", changed, TicketReadingService.TryDate(At(1), out var stamp) ? stamp : null);
            if (section.Kind == "fields") return new ReaderCard(prefix + At(0), "", At(1), "", Changed: changed);
            if (section.Kind == "text") return new ReaderCard(prefix + section.Name, "", At(0), "", Changed: changed);
            return new ReaderCard("Ligne " + (index + 1), "", string.Join("\n\n", section.Data.Headers.Select((header, i) => header + "\n" + At(i))), "");
        }).ToList();
        if (section.Kind == "timeline" && previousRead.HasValue)
        {
            var boundary = previousRead.Value.LocalDateTime;
            var index = cards.FindIndex(card => !card.Timestamp.HasValue || card.Timestamp.Value <= boundary);
            if (index < 0) index = cards.Count;
            cards.Insert(index, new ReaderCard("DERNIÈRE LECTURE", boundary.ToString("dd/MM/yyyy HH:mm:ss"), "Les événements datés après ce repère sont postérieurs à la précédente lecture. Le surlignage compare aussi les contenus effectivement relus.", "", boundary.ToString("dddd dd MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))));
        }
        var view = new ListCollectionView(cards);
        if (section.Kind == "timeline" && groupByDay) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ReaderCard.Day)));
        CardsList.ItemsSource = view;
        CardsList.SelectedIndex = -1;
        if (CardsList.Items.Count > 0) CardsList.ScrollIntoView(CardsList.Items[0]);
        Dispatcher.BeginInvoke(() => FindScroll(CardsList)?.ScrollToVerticalOffset(scrollOffset), DispatcherPriority.Loaded);
    }

    public double CaptureOffset() => FindScroll(CardsList)?.VerticalOffset ?? 0;
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
    private sealed record ReaderCard(string Title, string Meta, string Content, string Details, string Day = "", bool Changed = false, DateTime? Timestamp = null);
}
