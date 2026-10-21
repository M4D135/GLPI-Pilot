using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>Measures complete card content at its actual width, then packs the cards without clipping or nested viewports.</summary>
public sealed class DashboardBoardPanel : Panel
{
    public const double Gap = 14;
    public DashboardBoardSettings? Board { get; set; }
    private IReadOnlyList<DashboardPlacement> _placements = Array.Empty<DashboardPlacement>();
    private double _measuredWidth = double.NaN;
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 1000;
        MeasureCards(width);
        return new Size(width, _placements.Count == 0 ? 0 : _placements.Max(x => x.Top + x.Height));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!double.IsFinite(_measuredWidth) || Math.Abs(_measuredWidth - finalSize.Width) > .1)
            MeasureCards(Math.Max(1, finalSize.Width));
        var unit = (finalSize.Width + Gap) / 12;
        var map = _placements.ToDictionary(x => x.Id);
        foreach (FrameworkElement child in InternalChildren)
            if (child.Tag is string id && map.TryGetValue(id, out var place))
                child.Arrange(new Rect(unit * place.Column, place.Top, Math.Max(1, unit * place.Span - Gap), place.Height));
        return finalSize;
    }

    private void MeasureCards(double width)
    {
        _measuredWidth = width;
        var widgets = Board?.Widgets ?? new();
        var byId = widgets.Where(widget => !widget.Hidden).ToDictionary(widget => widget.Id);
        var heights = new Dictionary<string, double>(StringComparer.Ordinal);
        var unit = (width + Gap) / 12;
        foreach (FrameworkElement child in InternalChildren)
        {
            if (child.Tag is not string id || !byId.TryGetValue(id, out var widget)) continue;
            var span = Math.Clamp(DashboardLayoutEngine.EffectiveSpan(widget.Span, width), 3, 12);
            child.Measure(new Size(Math.Max(1, unit * span - Gap), double.PositiveInfinity));
            var naturalHeight = double.IsFinite(child.DesiredSize.Height) ? child.DesiredSize.Height : 160;
            // A manually chosen height is a minimum. Expanded text must remain reachable
            // through the dashboard's single parent scroller, never a clipped card.
            heights[id] = Math.Max(widget.AutoHeight ? 128 : Math.Clamp(widget.Height, 160, 960), naturalHeight);
        }
        _placements = DashboardLayoutEngine.Arrange(widgets, width, Gap, heights);
    }
}
