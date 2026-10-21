using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class DashboardView
{
    private void BuildHeader()
    {
        var header = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var titles = new StackPanel { Margin = new Thickness(0, 0, 24, 12), MaxWidth = 680 };
        titles.Children.Add(Text(_data?.IsFiltered == true ? "VUE TECHNICIEN" : "VUE D’ÉQUIPE", 10, "MutedBrush", FontWeights.SemiBold));
        titles.Children.Add(Text(_data?.IsFiltered == true ? _data.ScopeLabel : "Tableau de bord", 28, weight: FontWeights.SemiBold, margin: new Thickness(0, 4, 0, 0)));
        titles.Children.Add(Text(_data is null ? "Votre espace de suivi, composé à votre façon."
            : $"{_data.RowCount:N0} {_data.Grain} · relevé du {_data.ExtractedAt:dd/MM/yyyy à HH:mm} · calcul à {_data.CalculatedAt:HH:mm}",
            12, "MutedBrush", margin: new Thickness(0, 7, 0, 0)));
        header.Children.Add(titles);
        var actions = new WrapPanel { Margin = new Thickness(0, 11, 0, 0) };
        actions.Children.Add(DescribedCommand("Bilan Excel", "Exporter les indicateurs du périmètre actuellement affiché, y compris les avertissements.", () => ExportRequested?.Invoke(), _data != null));
        actions.Children.Add(DescribedCommand("Recalculer", "Recalculer les indicateurs avec les données déjà chargées et les réglages actuels.", () => RefreshRequested?.Invoke(), _data != null));
        actions.Children.Add(DescribedCommand("Colonnes et seuils", "Associer les colonnes du relevé et régler les seuils des indicateurs.", () => SettingsRequested?.Invoke(), quiet: true));
        header.Children.Add(actions); _body.Children.Add(header);
        if (_data is null) return;

        var metadata = new WrapPanel { Margin = new Thickness(0, 0, 0, 15) };
        metadata.Children.Add(Chip(_data.Complete ? "Complet confirmé" : "Périmètre à confirmer", _data.Complete ? "SuccessBrush" : "WarningBrush"));
        if (_data.IsFiltered)
            metadata.Children.Add(Chip($"Vue filtrée · {_data.RowCount:N0} / {_data.SourceRowCount:N0} lignes du relevé"));
        else if (_data.ExpectedTotal.HasValue)
            metadata.Children.Add(Chip($"{_data.RowCount:N0} / {_data.ExpectedTotal:N0} lignes annoncées"));
        if (_data.Warnings.Count > 0)
            metadata.Children.Add(Chip($"{_data.Warnings.Count:N0} avertissement(s) · voir Qualité", "WarningBrush"));
        _body.Children.Add(metadata);
        _body.Children.Add(Text("Les filtres de l’aperçu ne réduisent pas ce dashboard. Les chiffres suivent le périmètre indiqué ci-dessus.", 11, "MutedBrush", margin: new Thickness(0, -4, 0, 15)));
    }

    private void BuildToolbar()
    {
        var content = new StackPanel();
        var heading = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        heading.Children.Add(Text(_editing ? "Personnalisation en cours" : "Votre disposition", 13, _editing ? "AccentBrush" : "TextBrush", FontWeights.SemiBold, new Thickness(0, 0, 14, 0)));
        var hidden = Board.Widgets.Count(widget => widget.Hidden);
        heading.Children.Add(Text($"{Board.Widgets.Count - hidden} widget(s) visible(s)" + (hidden > 0 ? $" · {hidden} masqué(s)" : ""), 11, "MutedBrush"));
        content.Children.Add(heading);
        var bar = new WrapPanel();
        var boards = new ComboBox { Width = 230, Margin = new Thickness(0, 0, 10, 8), ItemsSource = _layout.Boards, DisplayMemberPath = nameof(DashboardBoardSettings.Name), SelectedItem = Board };
        Help(boards, "Choisir une disposition", "Changer de disposition conserve les données et le périmètre affichés. L’organisation globale et l’organisation personnelle sont indépendantes ; les vues des techniciens utilisent le même modèle personnel.");
        boards.SelectionChanged += (_, _) =>
        {
            if (!_busy && boards.SelectedItem is DashboardBoardSettings selected && selected.Id != _layout.ActiveBoardId)
                Change(() => { _layout.ActiveBoardId = selected.Id; _selectedWidget = null; });
        };
        bar.Children.Add(boards);
        var mode = DescribedCommand(_editing ? "Terminer" : "Personnaliser",
            _editing ? "Revenir à la consultation. Les changements appliqués sont déjà enregistrés."
                : "Afficher les poignées de déplacement, les tailles et l’éditeur de widgets.",
            () => { _editing = !_editing; Render(); });
        mode.SetResourceReference(Button.StyleProperty, "PrimaryButton"); bar.Children.Add(mode);
        bar.Children.Add(DescribedCommand("+ Ajouter un widget", "Ouvrir le catalogue pour trouver un indicateur, une répartition, une synthèse ou une note.", () => OpenEditor("library")));
        bar.Children.Add(DescribedCommand("Mes dispositions", "Renommer, dupliquer ou créer une disposition à partir d’un modèle.", () => OpenEditor("boards"), quiet: true));
        content.Children.Add(bar);
        if (_editing)
        {
            var history = new WrapPanel { Margin = new Thickness(0, 3, 0, 2) };
            history.Children.Add(DescribedCommand("↶ Annuler", "Annuler le dernier changement de disposition pendant cette session.", Undo, _undo.Count > 0, quiet: true));
            history.Children.Add(DescribedCommand("↷ Rétablir", "Rétablir le changement qui vient d’être annulé.", Redo, _redo.Count > 0, quiet: true));
            history.Children.Add(DescribedCommand("Enregistrer", "Enregistrer à nouveau la disposition actuelle. Les changements appliqués sont aussi sauvegardés automatiquement.", Persist, quiet: true));
            content.Children.Add(history);
            content.Children.Add(Text("Déplacez les cartes par leur poignée ou leurs flèches. Le coin inférieur droit ajuste leur taille. « Modifier » ouvre leurs options.", 11, "MutedBrush", margin: new Thickness(0, 0, 0, 10)));
        }
        _saveLabel = Text(_saveMessage, 11, _saveSuccess ? "MutedBrush" : "ErrorBrush");
        content.Children.Add(_saveLabel);
        if (!_saveSuccess && !_editing)
            content.Children.Add(DescribedCommand("Enregistrer", "Réessayer l’enregistrement de la disposition après l’erreur précédente.", Persist));
        var frame = Card(content); frame.Padding = new Thickness(16, 13, 16, 13); frame.Margin = new Thickness(0, 0, 0, 18);
        _toolbar = frame; _body.Children.Add(frame);
    }

    private FrameworkElement EmptyDataState()
    {
        var content = new StackPanel { Margin = new Thickness(9, 12, 9, 12), MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(Text("Les indicateurs apparaîtront ici", 23, weight: FontWeights.SemiBold));
        content.Children.Add(Text(_emptyMessage, 13, "MutedBrush", margin: new Thickness(0, 10, 0, 20)));
        var steps = new StackPanel();
        foreach (var (number, title, description) in new[]
        {
            ("1", "Retrouvez votre source", "L’adresse mémorisée et votre session SSO servent aux prochains relevés."),
            ("2", "Lancez Extraire tout", "Le bouton principal récupère le relevé et alimente les indicateurs."),
            ("3", "Consultez votre équipe ou votre vue personnelle", "Les chiffres restent liés aux lignes réellement récupérées.")
        })
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var numberBlock = Chip(number, "AccentBrush"); numberBlock.Margin = new Thickness(0, 0, 13, 0); DockPanel.SetDock(numberBlock, Dock.Left); row.Children.Add(numberBlock);
            var text = new StackPanel(); text.Children.Add(Text(title, 13, weight: FontWeights.SemiBold)); text.Children.Add(Text(description, 12, "MutedBrush", margin: new Thickness(0, 3, 0, 0))); row.Children.Add(text); steps.Children.Add(row);
        }
        content.Children.Add(steps);
        var compose = DescribedCommand("Composer ma disposition", "Personnaliser les widgets et leur apparence avant de charger des données.", () => OpenEditor("library"));
        compose.HorizontalAlignment = HorizontalAlignment.Left; content.Children.Add(compose);
        content.Children.Add(Text("Votre disposition est conservée. Aucun chiffre d’exemple n’est affiché.", 11, "MutedBrush", margin: new Thickness(0, 9, 0, 0)));
        return Card(content);
    }

    private FrameworkElement EmptyBoardState()
    {
        var hidden = Board.Widgets.Count(widget => widget.Hidden);
        var content = new StackPanel { Margin = new Thickness(12, 18, 12, 18) };
        content.Children.Add(Text(hidden > 0 ? "Vos widgets sont masqués" : "Un espace à composer", 22, weight: FontWeights.SemiBold));
        content.Children.Add(Text(hidden > 0 ? "Le catalogue permet de réafficher les widgets conservés dans cette disposition."
            : "Ajoutez les indicateurs, répartitions et notes utiles à votre suivi quotidien.", 13, "MutedBrush", margin: new Thickness(0, 9, 0, 16)));
        var add = DescribedCommand(hidden > 0 ? "Retrouver mes widgets" : "Ajouter un widget", "Ouvrir le catalogue de widgets et les widgets masqués de cette disposition.", () => OpenEditor("library"));
        add.HorizontalAlignment = HorizontalAlignment.Left; content.Children.Add(add); return Card(content);
    }

    private Button DescribedCommand(string label, string help, Action action, bool enabled = true, bool quiet = false)
    {
        var button = Command(label, action, enabled); Help(button, label, help);
        if (quiet) button.SetResourceReference(Button.StyleProperty, "QuietButton");
        return button;
    }

    private void Help(FrameworkElement control, string name, string description)
    {
        control.ToolTip = description;
        AutomationProperties.SetName(control, name); AutomationProperties.SetHelpText(control, description);
        ToolTipService.SetShowOnDisabled(control, true); ToolTipService.SetInitialShowDelay(control, 350);
        if (control.Resources.Contains("GlpiPilot.DashboardHelp")) return;
        control.Resources["GlpiPilot.DashboardHelp"] = true;
        control.MouseEnter += (_, _) => { if (_contextHelp != null) _contextHelp.Text = AutomationProperties.GetHelpText(control); };
        control.GotKeyboardFocus += (sender, args) =>
        {
            if (_contextHelp != null && (sender == args.OriginalSource || control is TextBox or ComboBox)) _contextHelp.Text = AutomationProperties.GetHelpText(control);
        };
    }

    private static Border Chip(string label, string foreground = "MutedBrush")
    {
        var chip = new Border { Child = Text(label, 11, foreground), CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 8, 6) };
        chip.SetResourceReference(Border.BackgroundProperty, "BadgeBrush"); return chip;
    }

    private static string KindLabel(string kind) => kind switch
    {
        "metric" => "Indicateur", "distribution" => "Répartition", "weather" => "Météo",
        "timeline" => "Activité", "attention" => "Priorités", "quality" => "Qualité", "note" => "Note", _ => "Widget"
    };
}
