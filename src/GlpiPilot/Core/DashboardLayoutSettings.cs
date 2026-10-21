using System.IO;

namespace GlpiPilot.Core;

/// <summary>Portable presentation only. Ticket data and local technician identity are never stored here.</summary>
public sealed class DashboardLayoutSettings
{
    public int Version { get; set; } = 1;
    public string ActiveBoardId { get; set; } = "";
    public List<DashboardBoardSettings> Boards { get; set; } = new();

    public DashboardLayoutSettings Copy() => new()
    {
        Version = Version, ActiveBoardId = ActiveBoardId,
        Boards = Boards?.Where(x => x is not null).Select(x => x.Copy()).ToList() ?? new()
    };

    public void Normalize()
    {
        Version = 1;
        Boards ??= new();
        Boards = Boards.Where(x => x is not null).Take(12).ToList();
        if (Boards.Count == 0) Boards.Add(CreateBoard("Pilotage", "overview"));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var board in Boards)
        {
            board.Normalize();
            if (!ids.Add(board.Id)) { board.Id = NewId(); ids.Add(board.Id); }
        }
        if (!Boards.Any(x => x.Id == ActiveBoardId)) ActiveBoardId = Boards[0].Id;
    }

    public void Validate()
    {
        if (Version != 1 || Boards is null || Boards.Count is < 1 or > 12)
            throw new InvalidDataException("La disposition doit contenir de 1 à 12 tableaux de bord, au format 1.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var board in Boards)
        {
            if (board is null || !ValidText(board.Id, 96) || !ids.Add(board.Id) || !ValidText(board.Name, 120)
                || board.Widgets is null || board.Widgets.Count > 60)
                throw new InvalidDataException("Un tableau de bord possède un nom, un identifiant ou une liste de widgets incorrects.");
            var widgetIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var widget in board.Widgets)
            {
                if (widget is null || !ValidText(widget.Id, 96) || !widgetIds.Add(widget.Id)
                    || !DashboardWidgetSettings.Kinds.Contains(widget.Kind)
                    || !DashboardWidgetSettings.Styles.Contains(widget.Style)
                    || !DashboardWidgetSettings.Accents.Contains(widget.Accent)
                    || !DashboardWidgetSettings.Presentations.Contains(widget.Presentation)
                    || widget.Span is < 3 or > 12 || widget.Height is < 160 or > 960 || widget.TopCount is < 3 or > 30
                    || widget.Title is null || widget.Title.Length > 180 || widget.SourceKey is null || widget.SourceKey.Length > 120
                    || widget.Note is null || widget.Note.Length > 4000)
                    throw new InvalidDataException("Un widget possède une présentation ou une taille incorrecte.");
                widget.ValidateThresholds();
            }
        }
        if (!ids.Contains(ActiveBoardId)) throw new InvalidDataException("Le tableau de bord actif est absent de la disposition.");
    }

    public static string NewId() => Guid.NewGuid().ToString("N");
    private static bool ValidText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    public static DashboardBoardSettings CreateBoard(string name, string preset)
    {
        var board = new DashboardBoardSettings { Name = name };
        void Add(string kind, string key = "", int span = 4, int height = 320, string accent = "blue") => board.Widgets.Add(new()
        { Kind = kind, SourceKey = key, Span = span, Height = height, Accent = accent });
        if (preset == "empty") return board;
        void Metrics(params string[] keys)
        {
            foreach (var key in keys) Add("metric", key, 3, 220,
                key.Contains("overdue", StringComparison.Ordinal) || key is "invalid-dates" or "inconsistent-dates" ? "red"
                    : key is "high" or "high-stale" or "stale" or "old" ? "orange" : "blue");
        }
        void Distribution(string key, int span = 6, string presentation = "bars")
        {
            Add("distribution", key, span, 400);
            board.Widgets[^1].Presentation = presentation;
            board.Widgets[^1].TopCount = 10;
        }
        if (preset == "personal")
        {
            Add("weather", span: 12, height: 280);
            Metrics("active", "new", "pending", "high", "overdue", "due-today", "stale", "old");
            Add("attention", span: 12, height: 440);
            Distribution("active-priority", presentation: "donut"); Distribution("due-horizon");
            Distribution("age"); Distribution("inactivity");
            Distribution("active-type", presentation: "donut"); Distribution("active-category");
            Add("timeline", span: 12, height: 340); Add("quality", span: 12, height: 360, accent: "slate");
            return board;
        }
        if (preset == "team")
        {
            Add("weather", span: 12, height: 280);
            Metrics("total", "active", "unassigned", "without-technician", "multi-technician", "high", "overdue", "stale");
            Distribution("active-assignee", 12); board.Widgets[^1].Height = 440; board.Widgets[^1].TopCount = 15;
            Distribution("active-group"); Distribution("assignment-coverage", presentation: "donut");
            Distribution("active-category"); Distribution("active-priority", presentation: "donut");
            Distribution("active-entity"); Distribution("active-location");
            Add("attention", span: 12, height: 420); Add("quality", span: 12, height: 360, accent: "slate");
            return board;
        }
        if (preset == "deadlines")
        {
            Metrics("overdue", "soon", "due-today", "due-week", "high-overdue", "active-no-due", "age-median", "inactivity-p90",
                "resolution-median", "closure-median", "resolution-closure-median", "due-tomorrow");
            Distribution("due-horizon"); Distribution("overdue-delay");
            Add("attention", span: 12, height: 440); Add("timeline", span: 12, height: 340);
            Add("quality", span: 12, height: 360, accent: "slate"); return board;
        }
        if (preset == "quality")
        {
            Add("quality", span: 12, height: 420, accent: "slate");
            Metrics("total", "unique", "duplicate", "missing-id", "unknown-status", "invalid-dates", "future-dates", "inconsistent-dates",
                "missing-category", "missing-requester", "active-no-due", "completed-no-solution");
            foreach (var key in new[] { "quality-created", "quality-updated", "quality-resolved", "quality-closed", "quality-due", "assignment-coverage" })
                Distribution(key, presentation: "donut");
            return board;
        }
        if (preset == "geography")
        {
            Metrics("total", "active", "high", "overdue", "city-matched", "city-coverage", "city-unmatched", "city-ambiguous", "city-no-title");
            Distribution("active-city", 12); board.Widgets[^1].Height = 440; board.Widgets[^1].TopCount = 20;
            Distribution("city", 6, "donut"); Distribution("city-detection", 6, "donut");
            Distribution("active-location"); Distribution("active-postal");
            Distribution("active-entity"); Distribution("active-assignee");
            Add("note", span: 12, height: 160, accent: "teal");
            board.Widgets[^1].Title = "Préparer les interventions";
            board.Widgets[^1].Note = "L’atelier de requêtes propose des vues d’urgences, de retards et de charge par ville. Ajoutez-les à cette disposition. L’onglet Villes et tournées prépare l’ordre des visites ; un nom de ville ne constitue pas une adresse d’intervention.";
            Add("quality", span: 12, height: 360, accent: "slate");
            return board;
        }
        Add("weather", span: 12, height: 280);
        var metrics = preset == "analysis"
            ? new[] { "total", "active", "age-median", "age-p90", "resolution-median", "closure-median", "created-7", "resolved-7" }
            : preset == "operations"
                ? new[] { "active", "pending", "high", "overdue", "soon", "unassigned", "stale", "old" }
                : new[] { "total", "active", "pending", "high", "overdue", "unassigned", "stale", "old" };
        foreach (var key in metrics) Add("metric", key, 3, 200, key is "overdue" or "high" ? "orange" : "blue");
        if (preset != "analysis") Add("attention", span: 12, height: 400);
        var distributions = preset == "analysis"
            ? new[] { "status", "type", "urgency", "impact", "priority", "age", "inactivity", "category", "entity", "location", "city", "postal" }
            : preset == "operations"
                ? new[] { "active-group", "active-assignee", "active-category", "active-priority", "age", "inactivity" }
                : new[] { "status", "active-priority", "age", "active-group", "active-assignee", "active-category" };
        foreach (var key in distributions) Add("distribution", key, 4, 380);
        Add("timeline", span: 12, height: 340);
        Add("quality", span: 12, height: 360, accent: "slate");
        return board;
    }
}

public sealed class DashboardBoardSettings
{
    public string Id { get; set; } = DashboardLayoutSettings.NewId();
    public string Name { get; set; } = "Mon tableau de bord";
    public List<DashboardWidgetSettings> Widgets { get; set; } = new();
    public DashboardBoardSettings Copy() => new() { Id = Id, Name = Name, Widgets = Widgets?.Where(x => x is not null).Select(x => x.Copy()).ToList() ?? new() };
    internal void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 96) Id = DashboardLayoutSettings.NewId();
        Name = string.IsNullOrWhiteSpace(Name) ? "Mon tableau de bord" : Name[..Math.Min(Name.Length, 120)];
        Widgets = Widgets?.Where(x => x is not null).Take(60).ToList() ?? new();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var widget in Widgets)
        {
            widget.Normalize();
            if (!ids.Add(widget.Id)) { widget.Id = DashboardLayoutSettings.NewId(); ids.Add(widget.Id); }
        }
    }
}

public sealed class DashboardWidgetSettings
{
    public static readonly string[] Kinds = { "metric", "distribution", "weather", "timeline", "attention", "quality", "note" };
    public static readonly string[] Styles = { "surface", "outline", "accent", "minimal" };
    public static readonly string[] Accents = { "blue", "teal", "green", "orange", "red", "purple", "slate" };
    public static readonly string[] Presentations = { "bars", "list", "compact", "donut", "gauge" };
    public string Id { get; set; } = DashboardLayoutSettings.NewId();
    public string Kind { get; set; } = "metric";
    public string SourceKey { get; set; } = "total";
    public string Title { get; set; } = "";
    public int Span { get; set; } = 4;
    public int Height { get; set; } = 320;
    public bool AutoHeight { get; set; } = true;
    public string Style { get; set; } = "surface";
    public string Accent { get; set; } = "blue";
    public string Presentation { get; set; } = "bars";
    public int TopCount { get; set; } = 8;
    public bool ShowDescription { get; set; } = true;
    public bool Hidden { get; set; }
    public bool Locked { get; set; }
    public string Note { get; set; } = "";
    public double? TargetValue { get; set; }
    public double? WarningValue { get; set; }
    public double? CriticalValue { get; set; }
    public bool LowerIsBetter { get; set; } = true;
    public DashboardWidgetSettings Copy() => (DashboardWidgetSettings)MemberwiseClone();
    public void ValidateThresholds()
    {
        if (!ValidNumber(TargetValue, positive: true) || !ValidNumber(WarningValue) || !ValidNumber(CriticalValue))
            throw new InvalidDataException("Les objectifs et seuils doivent être des nombres finis entre 0 et 1 000 000 000 000 ; l’objectif doit être strictement positif.");
        if (WarningValue.HasValue && CriticalValue.HasValue && (LowerIsBetter
            ? CriticalValue.Value < WarningValue.Value : CriticalValue.Value > WarningValue.Value))
            throw new InvalidDataException("Le seuil critique doit être au-delà du seuil d’avertissement dans le sens défavorable choisi.");
    }
    private static bool ValidNumber(double? value, bool positive = false) => !value.HasValue ||
        double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= 1e12 && (!positive || value.Value > 0);
    internal void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 96) Id = DashboardLayoutSettings.NewId();
        if (!Kinds.Contains(Kind)) Kind = "note";
        SourceKey = (SourceKey ?? "")[..Math.Min(SourceKey?.Length ?? 0, 120)];
        Title = (Title ?? "")[..Math.Min(Title?.Length ?? 0, 180)];
        Note = (Note ?? "")[..Math.Min(Note?.Length ?? 0, 4000)];
        Span = Math.Clamp(Span, 3, 12); Height = Math.Clamp(Height, 160, 960); TopCount = Math.Clamp(TopCount, 3, 30);
        if (!Styles.Contains(Style)) Style = "surface";
        if (!Accents.Contains(Accent)) Accent = "blue";
        if (!Presentations.Contains(Presentation)) Presentation = "bars";
        if (!ValidNumber(TargetValue, positive: true)) TargetValue = null;
        if (!ValidNumber(WarningValue)) WarningValue = null;
        if (!ValidNumber(CriticalValue)) CriticalValue = null;
        if (WarningValue.HasValue && CriticalValue.HasValue && (LowerIsBetter ? CriticalValue < WarningValue : CriticalValue > WarningValue))
            WarningValue = CriticalValue = null;
    }
}

public sealed record DashboardPlacement(string Id, int Column, int Span, double Top, double Height);

/// <summary>Deterministic masonry packing, independent of WPF so responsive behavior can be verified.</summary>
public static class DashboardLayoutEngine
{
    public static int EffectiveSpan(int requested, double width) => width < 650 ? 12 : width < 1000 ? Math.Max(6, requested) : requested;
    public static IReadOnlyList<DashboardPlacement> Arrange(IEnumerable<DashboardWidgetSettings> widgets, double width, double gap = 14,
        IReadOnlyDictionary<string, double>? measuredHeights = null)
    {
        var columns = new double[12]; var result = new List<DashboardPlacement>();
        foreach (var widget in widgets.Where(x => !x.Hidden))
        {
            var span = Math.Clamp(EffectiveSpan(widget.Span, width), 3, 12);
            var bestColumn = 0; var bestTop = double.MaxValue;
            for (var col = 0; col <= 12 - span; col++)
            {
                var top = columns.Skip(col).Take(span).Max();
                if (top < bestTop) { bestTop = top; bestColumn = col; }
            }
            var height = measuredHeights != null && measuredHeights.TryGetValue(widget.Id, out var measured)
                && double.IsFinite(measured) && measured > 0 ? measured : Math.Clamp(widget.Height, 160, 960);
            result.Add(new(widget.Id, bestColumn, span, bestTop, height));
            for (var col = bestColumn; col < bestColumn + span; col++) columns[col] = bestTop + height + gap;
        }
        return result;
    }

    public static bool Move(DashboardBoardSettings board, string id, string? beforeId)
    {
        var widget = board.Widgets.FirstOrDefault(x => x.Id == id);
        if (widget is null || widget.Locked || id == beforeId) return false;
        var before = beforeId is null ? null : board.Widgets.FirstOrDefault(x => x.Id == beforeId);
        if (beforeId is not null && before is null) return false;
        board.Widgets.Remove(widget);
        var index = before is null ? board.Widgets.Count : board.Widgets.IndexOf(before);
        board.Widgets.Insert(index, widget); return true;
    }
}
