using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>Selectable, wrapping cards with a compiled template. Values are always bound as plain text.</summary>
public partial class TicketReaderView : UserControl
{
    public TicketReaderView() => InitializeComponent();

    public void Show(TicketDetailSection section, IEnumerable<string[]> rows)
    {
        CardsList.ItemsSource = rows.Select((row, index) =>
        {
            string At(int i) => i < row.Length ? row[i] : "";
            if (section.Kind == "timeline") return new ReaderCard(At(0), string.Join(" · ", new[] { At(1), At(2) }.Where(value => value.Length > 0)), At(3), At(4));
            if (section.Kind == "fields") return new ReaderCard(At(0), "", At(1), "");
            if (section.Kind == "text") return new ReaderCard(section.Name, "", At(0), "");
            return new ReaderCard("Ligne " + (index + 1), "", string.Join("\n\n", section.Data.Headers.Select((header, i) => header + "\n" + At(i))), "");
        }).ToArray();
        CardsList.SelectedIndex = -1;
        if (CardsList.Items.Count > 0) CardsList.ScrollIntoView(CardsList.Items[0]);
    }

    private sealed record ReaderCard(string Title, string Meta, string Content, string Details);
}
