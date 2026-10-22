using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace GlpiPilot.UI;

internal static class PilotControls
{
    public static TextBlock Text(string value, double size = 13, bool muted = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush"); return text;
    }
    public static Button Button(string label, Action action, string tooltip = "")
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 7, 12, 7), ToolTip = tooltip.Length == 0 ? label : tooltip };
        button.Click += (_, _) => action(); return button;
    }
    public static DataGrid Table(bool editable = false)
    {
        var table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = !editable, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader, RowHeight = double.NaN, MinRowHeight = 32 };
        table.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        table.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        return table;
    }
    public static void Column(DataGrid grid, string label, string property, double width = 140, string? format = null)
    {
        var binding = new Binding(property) { Mode = grid.IsReadOnly ? BindingMode.OneWay : BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, StringFormat = format };
        grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = binding, Width = width < 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width), MinWidth = 70 });
    }
    public static Border Card(UIElement content)
    {
        var border = new Border { Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border;
    }
    public static Grid Page(string title, string subtitle, UIElement body, UIElement? toolbar = null)
    {
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var heading = new StackPanel(); heading.Children.Add(Text(title, 23)); heading.Children.Add(Text(subtitle, 12, true)); root.Children.Add(heading);
        if (toolbar != null) { Grid.SetRow(toolbar, 1); root.Children.Add(toolbar); }
        Grid.SetRow(body, 2); root.Children.Add(body); return root;
    }
    public static TextBox Input(string value = "", double width = 180)
    {
        return new TextBox { Text = value, Width = width, Margin = new Thickness(0, 0, 8, 8), MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
    }
}
