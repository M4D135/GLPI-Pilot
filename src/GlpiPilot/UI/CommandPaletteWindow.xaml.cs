using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed record WorkspaceCommand(string Title, string Description, string Shortcut, Action Execute, bool Available = true);

public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<WorkspaceCommand> _commands;
    public WorkspaceCommand? SelectedCommand { get; private set; }

    public CommandPaletteWindow(IEnumerable<WorkspaceCommand> commands)
    {
        InitializeComponent();
        _commands = commands.ToArray();
        ThemeService.Attach(this);
        QueryBox.TextChanged += (_, _) => Filter();
        ActionsList.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source ||
                ItemsControl.ContainerFromElement(ActionsList, source) is not ListBoxItem { Content: WorkspaceCommand { Available: true } command }) return;
            ActionsList.SelectedItem = command;
            Choose(); e.Handled = true;
        };
        PreviewKeyDown += OnKeyDown;
        Loaded += (_, _) => { QueryBox.Focus(); Keyboard.Focus(QueryBox); };
        Filter();
    }

    private void Filter()
    {
        var query = QueryBox.Text.Trim();
        var comparison = CultureInfo.CurrentCulture.CompareInfo;
        var matches = _commands.Where(command => query.Length == 0 || comparison.IndexOf(
            command.Title + " " + command.Description + " " + command.Shortcut, query,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0).ToArray();
        ActionsList.ItemsSource = matches;
        ActionsList.SelectedItem = matches.FirstOrDefault(command => command.Available);
        NoActionText.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = $"{matches.Length} actions · ↑ ↓ pour choisir · Entrée pour ouvrir · Échap pour fermer";
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!QueryBox.IsKeyboardFocusWithin && !ActionsList.IsKeyboardFocusWithin) return;
        if (e.Key == Key.Enter) { Choose(); e.Handled = true; return; }
        if (e.Key is not (Key.Up or Key.Down)) return;
        var items = ActionsList.Items.Cast<WorkspaceCommand>().ToArray();
        if (items.Length == 0) return;
        var direction = e.Key == Key.Down ? 1 : -1;
        var index = ActionsList.SelectedIndex;
        for (var step = 0; step < items.Length; step++)
        {
            index = (index + direction + items.Length) % items.Length;
            if (!items[index].Available) continue;
            ActionsList.SelectedIndex = index;
            ActionsList.ScrollIntoView(items[index]);
            break;
        }
        e.Handled = true;
    }

    private void Choose()
    {
        if (ActionsList.SelectedItem is not WorkspaceCommand { Available: true } command) return;
        SelectedCommand = command;
        DialogResult = true;
    }
}
