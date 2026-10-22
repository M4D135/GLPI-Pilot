using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class QueryWorkshopView
{
    private readonly TreeView _groupTree = new() { Margin = new Thickness(0, 8, 0, 8) };
    private FrameworkElement DistributionViews()
    {
        VirtualizingPanel.SetIsVirtualizing(_groupTree, true); VirtualizingPanel.SetVirtualizationMode(_groupTree, VirtualizationMode.Recycling);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "Catégories finales", Content = _buckets }); tabs.Items.Add(new TabItem { Header = "Hiérarchie et sous-totaux", Content = _groupTree }); return tabs;
    }
    private void ShowHierarchy(QueryResult result)
    {
        _groupTree.Items.Clear();
        var root = new TreeViewItem { IsExpanded = true, Header = Text($"Ensemble · {result.Metric.Value?.ToString("0.##", CultureInfo.CurrentCulture) ?? "—"} {result.Metric.Unit} · {result.Rows.Length:N0} lignes", 13, "TextBrush", FontWeights.SemiBold) };
        root.ToolTip = "Sous-totaux recalculés sur les lignes uniques de chaque préfixe ; les médianes, distincts et pourcentages ne sont jamais additionnés. Un ticket multi-affecté peut appartenir à plusieurs branches.";
        _groupTree.Items.Add(root); var parents = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in result.Hierarchy.OrderBy(g => g.Path.Length).ThenBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            var label = group.Path[^1] + " · " + (group.Value?.ToString("0.##", CultureInfo.CurrentCulture) ?? "—") + " " + group.Unit + $" · {group.Count:N0} lignes · n={group.Sample:N0}";
            var button = Command(label); button.HorizontalContentAlignment = HorizontalAlignment.Left; button.ToolTip = $"Base du groupe avant conditions : {group.Denominator:N0} lignes. Ouvrir les lignes retenues de ce sous-total.";
            button.Click += (_, _) => { if (PreviewIsCurrent() && _previewResult!.Hierarchy.Contains(group)) RowsRequested?.Invoke(group.Rows, "Sous-total · " + group.Label); };
            var node = new TreeViewItem { Header = button, IsExpanded = group.Path.Length == 1 };
            var key = JsonSerializer.Serialize(group.Path); var parentKey = JsonSerializer.Serialize(group.Path.Take(group.Path.Length - 1));
            if (parents.TryGetValue(parentKey, out var parent)) parent.Items.Add(node); else root.Items.Add(node); parents[key] = node;
        }
    }
}
