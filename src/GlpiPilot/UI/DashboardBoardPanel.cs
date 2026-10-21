using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>Uses all available width; unequal card heights do not create entire empty wrap rows.</summary>
public sealed class DashboardBoardPanel : Panel
{
    public const double Gap = 14;
    public DashboardBoardSettings? Board { get; set; }
    private IReadOnlyList<DashboardPlacement> _placements = Array.Empty<DashboardPlacement>();
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 1000;
        _placements = DashboardLayoutEngine.Arrange(Board?.Widgets ?? new(), width, Gap);
        var unit = (width + Gap) / 12;
        var map = _placements.ToDictionary(x => x.Id);
        foreach (FrameworkElement child in InternalChildren)
            if (child.Tag is string id && map.TryGetValue(id, out var place))
                child.Measure(new Size(Math.Max(1, unit * place.Span - Gap), place.Height));
        return new Size(width, _placements.Count == 0 ? 0 : _placements.Max(x => x.Top + x.Height));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        _placements = DashboardLayoutEngine.Arrange(Board?.Widgets ?? new(), finalSize.Width, Gap);
        var unit = (finalSize.Width + Gap) / 12;
        var map = _placements.ToDictionary(x => x.Id);
        foreach (FrameworkElement child in InternalChildren)
            if (child.Tag is string id && map.TryGetValue(id, out var place))
                child.Arrange(new Rect(unit * place.Column, place.Top, Math.Max(1, unit * place.Span - Gap), place.Height));
        return finalSize;
    }
}
