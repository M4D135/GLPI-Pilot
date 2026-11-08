using System.IO;
using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

/// <summary>A portable dashboard slide composition. Geometry is relative to the usable slide surface, never to ticket data.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DashboardPresentationSettings
{
    public const int MaximumSlides = 40;
    public const int MaximumItemsPerSlide = 20;
    public const int MaximumItems = 300;
    public const int CoordinateScale = 1000;
    public int Version { get; set; } = 1;
    public List<DashboardPresentationSlide> Slides { get; set; } = new();

    public DashboardPresentationSettings Copy() => new() { Version = Version, Slides = Slides.Select(slide => slide.Copy()).ToList() };

    public void Validate(IEnumerable<string>? widgetIds = null)
    {
        var widgets = widgetIds?.ToHashSet(StringComparer.Ordinal);
        if (Version != 1 || Slides is null || Slides.Count > MaximumSlides)
            throw new InvalidDataException("La présentation accepte au maximum 40 diapositives, au format 1.");
        var slides = new HashSet<string>(StringComparer.Ordinal); var items = new HashSet<string>(StringComparer.Ordinal); var count = 0;
        foreach (var slide in Slides)
        {
            if (slide is null || !Identifier(slide.Id) || !slides.Add(slide.Id) || slide.Title is null || slide.Title.Length > 180 || slide.Title.Any(char.IsControl)
                || slide.Items is null || slide.Items.Count > MaximumItemsPerSlide)
                throw new InvalidDataException("Chaque diapositive doit avoir un identifiant unique, un titre valide et au maximum 20 blocs.");
            var shown = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in slide.Items)
            {
                if (item is null || !Identifier(item.Id) || !items.Add(item.Id) || !Identifier(item.WidgetId)
                    || widgets is not null && !widgets.Contains(item.WidgetId) || !shown.Add(item.WidgetId))
                    throw new InvalidDataException("Un bloc de présentation désigne un widget absent ou répété sur la même diapositive, ou possède un identifiant dupliqué.");
                if (item.X is < 0 or > CoordinateScale || item.Y is < 0 or > CoordinateScale
                    || item.Width is < 80 or > CoordinateScale || item.Height is < 80 or > CoordinateScale
                    || item.X + item.Width > CoordinateScale || item.Y + item.Height > CoordinateScale)
                    throw new InvalidDataException("Les blocs doivent rester dans la diapositive ; leur largeur et hauteur sont comprises entre 80 et 1 000.");
                if (++count > MaximumItems) throw new InvalidDataException("La présentation accepte au maximum 300 placements de widgets.");
            }
        }
    }

    public string[] WidgetIds() => Slides.SelectMany(slide => slide.Items).Select(item => item.WidgetId).Distinct(StringComparer.Ordinal).ToArray();
    public void RemapWidgets(IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var item in Slides.SelectMany(slide => slide.Items))
            if (replacements.TryGetValue(item.WidgetId, out var replacement)) item.WidgetId = replacement;
    }
    public void RetainWidgets(IEnumerable<string> widgetIds)
    {
        var ids = widgetIds.ToHashSet(StringComparer.Ordinal);
        foreach (var slide in Slides) slide.Items.RemoveAll(item => !ids.Contains(item.WidgetId));
    }
    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 96 && !value.Any(char.IsControl);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DashboardPresentationSlide
{
    public string Id { get; set; } = DashboardLayoutSettings.NewId();
    public string Title { get; set; } = "";
    public List<DashboardPresentationItem> Items { get; set; } = new();
    public DashboardPresentationSlide Copy() => new() { Id = Id, Title = Title, Items = Items.Select(item => item.Copy()).ToList() };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DashboardPresentationItem
{
    public string Id { get; set; } = DashboardLayoutSettings.NewId();
    public string WidgetId { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 1000;
    public int Height { get; set; } = 1000;
    public DashboardPresentationItem Copy() => (DashboardPresentationItem)MemberwiseClone();
}
