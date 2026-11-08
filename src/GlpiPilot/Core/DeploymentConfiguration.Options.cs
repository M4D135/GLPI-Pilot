using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class DeploymentWorkplaceOptions
{
    public List<string> FavoriteSections { get; set; } = new();
    public string DefaultSection { get; set; } = "treatment";
    public List<string> SectionOrder { get; set; } = new();
    public bool GroupTimelineByDay { get; set; } = true;
    public bool DesktopReminders { get; set; } = true;
    public string ActiveWorkspace { get; set; } = "team";
    public string DaySearch { get; set; } = "";
    public int DayScope { get; set; }
    public Dictionary<string, List<string>> ChecklistTemplates { get; set; } = new(StringComparer.Ordinal);

    public static DeploymentWorkplaceOptions From(WorkplaceDocument value) => new()
    {
        FavoriteSections = value.FavoriteSections.ToList(), DefaultSection = value.DefaultSection,
        SectionOrder = value.SectionOrder.ToList(), GroupTimelineByDay = value.GroupTimelineByDay,
        DesktopReminders = value.DesktopReminders, ActiveWorkspace = value.ActiveWorkspace,
        DaySearch = value.DaySearch, DayScope = value.DayScope,
        ChecklistTemplates = value.ChecklistTemplates.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal)
    };
    public WorkplaceDocument ApplyTo(WorkplaceDocument target)
    {
        if (FavoriteSections is null || SectionOrder is null || ChecklistTemplates is null || DayScope is < 0 or > 6)
            throw new InvalidDataException("Les préférences de lecture et de rappels sont invalides.");
        var copy = target.Copy();
        copy.FavoriteSections = FavoriteSections.ToList(); copy.DefaultSection = DefaultSection;
        copy.SectionOrder = SectionOrder.ToList(); copy.GroupTimelineByDay = GroupTimelineByDay;
        copy.DesktopReminders = DesktopReminders; copy.ActiveWorkspace = ActiveWorkspace;
        copy.DaySearch = DaySearch; copy.DayScope = DayScope;
        if (ChecklistTemplates.Any(pair => pair.Value is null)) throw new InvalidDataException("Un modèle de liste est absent.");
        copy.ChecklistTemplates = ChecklistTemplates.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);
        return WorkplaceStore.Validate(copy);
    }
}

public static partial class DeploymentConfigurationService
{
    private static readonly string[] OptionNames = ["TerrainServices", "TicketActions", "Workplace", "AlertRules", "TableViews", "TableColumns", "Technicians", "TourOrder"];
    private static readonly JsonSerializerOptions LocalOptionsJson = new(Json) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip };
    private static T Option<T>(DeploymentConfiguration configuration, string key) => configuration.Options[key].Deserialize<T>(Json)
        ?? throw new InvalidDataException("Section d’options absente : " + key);
    private static T ReadOptionFile<T>(string root, string name, T fallback, int maximum = 2 * 1024 * 1024)
    {
        var path = Path.Combine(root, name); UpdateService.EnsureNoLinks(path);
        if (!File.Exists(path)) return fallback;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("Le fichier de préférences dépasse la taille autorisée : " + name);
        return JsonSerializer.Deserialize<T>(stream, LocalOptionsJson) ?? throw new InvalidDataException("Fichier de préférences vide : " + name);
    }
    static partial void CaptureOptions(DeploymentConfiguration configuration, string root)
    {
        UpdateService.EnsureNoLinks(root);
        var workplace = WorkplaceStore.Validate(ReadOptionFile(root, "workplace.json", new WorkplaceDocument(), WorkplaceStore.MaximumBytes));
        var alerts = ReadOptionFile(root, "alerts.json", new AlertDocument(), 32 * 1024 * 1024);
        if (alerts.Version != 1 || alerts.Rules is null) throw new InvalidDataException("Le format des règles d’alerte n’est pas reconnu.");
        configuration.Options = new(StringComparer.Ordinal)
        {
            ["TerrainServices"] = JsonSerializer.SerializeToElement(ReadOptionFile(root, "terrain-services.json", new TourServiceSettings(), 16_384), Json),
            ["TicketActions"] = JsonSerializer.SerializeToElement(ReadOptionFile(root, "ticket-action-adapters.json", new List<TicketActionAdapter>(), 512 * 1024), Json),
            ["Workplace"] = JsonSerializer.SerializeToElement(DeploymentWorkplaceOptions.From(workplace), Json),
            ["AlertRules"] = JsonSerializer.SerializeToElement(alerts.Rules, Json),
            ["TableViews"] = JsonSerializer.SerializeToElement(ReadOptionFile(root, "table-views.json", new List<NamedPreview>()), Json),
            ["TableColumns"] = JsonSerializer.SerializeToElement(ReadOptionFile(root, "preview-view.json", new PreviewViewPreferences.PreferenceDocument()), Json),
            ["Technicians"] = JsonSerializer.SerializeToElement(ReadOptionFile(root, "technician-preferences.json", new TechnicianPreferences()), Json),
            ["TourOrder"] = JsonSerializer.SerializeToElement(ReadOptionFile(root, "tour-order.json", new TourOrderPreferences()), Json)
        };
        ValidateOptions(configuration);
    }
    static partial void ValidateOptions(DeploymentConfiguration configuration)
    {
        if (configuration.Options is null || !OptionNames.ToHashSet(StringComparer.Ordinal).SetEquals(configuration.Options.Keys))
            throw new InvalidDataException("L’export complet doit contenir toutes les sections d’options reconnues.");
        foreach (var key in OptionNames)
            if (JsonSerializer.SerializeToUtf8Bytes(configuration.Options[key], Json).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("La section d’options dépasse 2 Mio : " + key);
        Option<TourServiceSettings>(configuration, "TerrainServices").Validate();
        var adapters = Option<List<TicketActionAdapter>>(configuration, "TicketActions");
        if (adapters.Count > 100 || adapters.Any(value => value is null)
            || adapters.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != adapters.Count)
            throw new InvalidDataException("Les adaptateurs de formulaires sont trop nombreux ou répétés.");
        foreach (var adapter in adapters) TicketActionService.Validate(adapter);
        _ = Option<DeploymentWorkplaceOptions>(configuration, "Workplace").ApplyTo(new WorkplaceDocument());
        var rules = Option<List<AlertRule>>(configuration, "AlertRules");
        if (rules.Count > 50 || rules.Any(value => value is null) || rules.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != rules.Count)
            throw new InvalidDataException("Les règles d’alerte sont trop nombreuses ou répétées.");
        foreach (var rule in rules) rule.Validate();
        var views = Option<List<NamedPreview>>(configuration, "TableViews");
        if (views.Count > 64 || views.Any(value => value is null) || views.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != views.Count)
            throw new InvalidDataException("Les vues nommées sont trop nombreuses ou répétées.");
        foreach (var view in views) NamedPreviewStore.Validate(view);
        ValidateColumns(Option<PreviewViewPreferences.PreferenceDocument>(configuration, "TableColumns"));
        _ = TechnicianPreferences.Validate(Option<TechnicianPreferences>(configuration, "Technicians"));
        _ = TourOrderPreferences.Validate(Option<TourOrderPreferences>(configuration, "TourOrder"));
    }
    private static void ValidateColumns(PreviewViewPreferences.PreferenceDocument document)
    {
        if (document.Version != 1 || document.Profiles is null || document.Profiles.Count > PreviewViewPreferences.MaximumProfiles)
            throw new InvalidDataException("Les préférences de colonnes sont invalides.");
        foreach (var pair in document.Profiles)
        {
            if (pair.Key.Length != 64 || !pair.Key.All(Uri.IsHexDigit) || pair.Value?.Columns is not { Length: <= 16384 } columns)
                throw new InvalidDataException("Un profil de colonnes est invalide.");
            if (columns.Any(column => column is null || column.SourceIndex < 0 || column.SourceIndex >= columns.Length
                || column.DisplayIndex < 0 || column.DisplayIndex >= columns.Length || !double.IsFinite(column.Width)
                || column.Width < PreviewViewPreferences.MinimumWidth || column.Width > PreviewViewPreferences.MaximumWidth)
                || columns.Select(column => column.SourceIndex).Distinct().Count() != columns.Length
                || columns.Select(column => column.DisplayIndex).Distinct().Count() != columns.Length
                || columns.Length > 0 && columns.All(column => !column.Visible))
                throw new InvalidDataException("Un ordre, une largeur ou une visibilité de colonnes est invalide.");
        }
    }
    static partial void AddOptionFiles(DeploymentConfiguration configuration, string root, Dictionary<string, byte[]> files)
    {
        ValidateOptions(configuration);
        void Add(string name, object value, int maximum = 2 * 1024 * 1024)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (bytes.Length > maximum) throw new InvalidDataException("Le fichier d’options dépasse sa taille autorisée : " + name);
            files.Add(name, bytes);
        }
        Add("terrain-services.json", Option<TourServiceSettings>(configuration, "TerrainServices"), 16_384);
        Add("ticket-action-adapters.json", Option<List<TicketActionAdapter>>(configuration, "TicketActions"), 512 * 1024);
        // Replacing options must not replace the recipient's notes, tickets, alert history or receipts.
        var workplace = WorkplaceStore.Validate(ReadOptionFile(root, "workplace.json", new WorkplaceDocument(), WorkplaceStore.MaximumBytes));
        Add("workplace.json", Option<DeploymentWorkplaceOptions>(configuration, "Workplace").ApplyTo(workplace), WorkplaceStore.MaximumBytes);
        var alerts = ReadOptionFile(root, "alerts.json", new AlertDocument(), 32 * 1024 * 1024);
        if (alerts.Version != 1 || alerts.Incidents is null || alerts.Observations is null) throw new InvalidDataException("Le fichier local des alertes est invalide.");
        alerts.Rules = Option<List<AlertRule>>(configuration, "AlertRules");
        foreach (var incident in alerts.Incidents.Where(value => value is not null && value.Active))
        {
            var rule = alerts.Rules.FirstOrDefault(value => value.Id == incident.RuleId);
            if (rule is null || !rule.Enabled || rule.Fingerprint() != incident.RuleFingerprint)
            { incident.Active = false; incident.Evaluation = "Règle retirée ou modifiée"; }
        }
        Add("alerts.json", alerts, 32 * 1024 * 1024);
        Add("table-views.json", Option<List<NamedPreview>>(configuration, "TableViews"));
        Add("preview-view.json", Option<PreviewViewPreferences.PreferenceDocument>(configuration, "TableColumns"));
        Add("technician-preferences.json", TechnicianPreferences.Validate(Option<TechnicianPreferences>(configuration, "Technicians")));
        Add("tour-order.json", TourOrderPreferences.Validate(Option<TourOrderPreferences>(configuration, "TourOrder")));
    }
}
