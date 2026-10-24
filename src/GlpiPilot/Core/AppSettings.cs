using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class AppSettings
{
    public const int MinimumInterfaceZoomPercent = 75;
    public const int MaximumInterfaceZoomPercent = 150;
    public const int InterfaceZoomStep = 5;
    public string PortalUrl { get; set; } = "";
    public string BrowserChannel { get; set; } = "msedge";
    public string Theme { get; set; } = "dark";
    public int InterfaceZoomPercent { get; set; } = 100;
    public string AutoMode { get; set; } = "native";
    public bool AutoSaveXlsx { get; set; } = true;
    public string ReadySelector { get; set; } = "";
    public string NextSelector { get; set; } = "";
    public string SelectorOrigin { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 180;
    public int DownloadTimeoutSeconds { get; set; } = 600;
    public int ListTimeoutSeconds { get; set; } = 45;
    public int DetailTimeoutSeconds { get; set; } = 30;
    public int ReconnectAttempts { get; set; } = 2;
    public bool ResumePagination { get; set; } = true;
    public int CheckpointMaximumAgeMinutes { get; set; } = 120;
    public int MinimumFreeSpaceMb { get; set; } = 256;
    public int MaxPages { get; set; } = 200;
    public int MaxRows { get; set; } = 100000;
    public string ExportFolder { get; set; } = AppPaths.DefaultExports;
    public int? ExpectedTotal { get; set; }
    public string TableSelector { get; set; } = "";
    public string TableHeaderFingerprint { get; set; } = "";
    public string TableFrameOrigin { get; set; } = "";
    public string TableFrameName { get; set; } = "";
    public string TableTargetUrl { get; set; } = "";
    public string NativeSelectorOrigin { get; set; } = "";
    public string NativeMenuSelector { get; set; } = "";
    public string NativeFormatSelector { get; set; } = "";
    public string NativeFormatValue { get; set; } = "";
    public string NativeScopeSelector { get; set; } = "";
    public string NativeScopeValue { get; set; } = "";
    public string NativeSubmitSelector { get; set; } = "";
    public TicketDetailSettings TicketDetails { get; set; } = new();
    public DashboardSettings Dashboard { get; set; } = new();
    public CitySettings Cities { get; set; } = new();
    // Local receipt for the optional pack CSV. It is not a portable preference.
    public string SiteReferenceImportHash { get; set; } = "";
    public QueryLibrarySettings Queries { get; set; } = QueryLibrarySettings.CreateDefault();
    public List<SourceProfile> Sources { get; set; } = new();
    public string ActiveSourceId { get; set; } = "";
    public HistorySettings History { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();
    public InterfacePreferences Interface { get; set; } = new();

    public static AppSettings Load()
    {
        return SettingsRecovery.Load(AppPaths.SettingsFile);
    }

    public void Save()
    {
        Normalize();
        // Search filters are part of the recipe. Log redaction must never alter navigation.
        PortalUrl = TargetUrlPolicy.PersistedTarget(PortalUrl);
        TableTargetUrl = TargetUrlPolicy.PersistedTarget(TableTargetUrl);
        SettingsRecovery.Save(AppPaths.SettingsFile, this);
    }

    public void RememberTable(TableChoice table)
    {
        TableSelector = table.Selector;
        TableHeaderFingerprint = table.HeaderFingerprint;
        TableFrameOrigin = table.FrameOrigin;
        TableFrameName = table.FrameName;
        TableTargetUrl = TargetUrlPolicy.PersistedTarget(PortalUrl);
    }

    public AppSettings Copy()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.TicketDetails = TicketDetails?.Copy() ?? new();
        copy.Dashboard = Dashboard?.Copy() ?? new();
        copy.Cities = Cities?.Copy() ?? new();
        copy.Queries = Queries?.Copy() ?? QueryLibrarySettings.CreateDefault();
        copy.Sources = Sources?.Select(source => source.Copy()).ToList() ?? new();
        copy.History = History?.Copy() ?? new();
        copy.Automation = Automation?.Copy() ?? new();
        copy.Interface = Interface?.Copy() ?? new();
        copy.Normalize();
        return copy;
    }

    public void Normalize()
    {
        InterfaceZoomPercent = NormalizeInterfaceZoom(InterfaceZoomPercent);
        ListTimeoutSeconds = Math.Clamp(ListTimeoutSeconds, 5, 600);
        DetailTimeoutSeconds = Math.Clamp(DetailTimeoutSeconds, 5, 600);
        ReconnectAttempts = Math.Clamp(ReconnectAttempts, 0, 5);
        CheckpointMaximumAgeMinutes = Math.Clamp(CheckpointMaximumAgeMinutes, 5, 1440);
        MinimumFreeSpaceMb = Math.Clamp(MinimumFreeSpaceMb, 32, 32768);
        PortalUrl ??= "";
        BrowserChannel ??= "msedge";
        Theme ??= "dark";
        AutoMode ??= "native";
        ReadySelector ??= "";
        NextSelector ??= "";
        SelectorOrigin ??= "";
        ExportFolder ??= AppPaths.DefaultExports;
        TableSelector ??= "";
        TableHeaderFingerprint ??= "";
        TableFrameOrigin ??= "";
        TableFrameName ??= "";
        TableTargetUrl ??= "";
        NativeSelectorOrigin ??= "";
        NativeMenuSelector ??= "";
        NativeFormatSelector ??= "";
        NativeFormatValue ??= "";
        NativeScopeSelector ??= "";
        NativeScopeValue ??= "";
        NativeSubmitSelector ??= "";
        TicketDetails ??= new();
        TicketDetails.Normalize();
        Dashboard ??= new();
        Dashboard.Normalize();
        Cities ??= new();
        Cities.Normalize();
        SiteReferenceImportHash ??= "";
        Queries ??= QueryLibrarySettings.CreateDefault();
        Queries.Normalize();
        Sources = (Sources ?? new()).Where(source => source != null).Take(100).ToList();
        foreach (var source in Sources) source.Normalize();
        ActiveSourceId ??= "";
        History ??= new(); History.Normalize();
        Automation ??= new(); Automation.Normalize();
        Interface ??= new(); Interface.Normalize();
    }

    public static int NormalizeInterfaceZoom(int value) =>
        (Math.Clamp(value, MinimumInterfaceZoomPercent, MaximumInterfaceZoomPercent) + 2) / InterfaceZoomStep * InterfaceZoomStep;
}

public sealed class SourceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nouvelle source";
    public string Url { get; set; } = "";
    public string Browser { get; set; } = "msedge";
    public string Mode { get; set; } = "native";
    public bool IncludedInCampaign { get; set; } = true;
    public string ExportFolder { get; set; } = "";
    public Dictionary<string, string> Acquisition { get; set; } = new(StringComparer.Ordinal);
    public DashboardSettings? Interpretation { get; set; }
    public bool RestoreWorkspace { get; set; }
    public QueryLibrarySettings? WorkspaceQueries { get; set; }
    public SourceProfile Copy()
    {
        var copy = (SourceProfile)MemberwiseClone();
        copy.Acquisition = new(Acquisition ?? new(), StringComparer.Ordinal);
        copy.Interpretation = Interpretation?.Copy(); copy.WorkspaceQueries = WorkspaceQueries?.Copy();
        return copy;
    }
    private static readonly string[] AcquisitionFields = { "ReadySelector", "NextSelector", "SelectorOrigin", "TableSelector", "TableHeaderFingerprint", "TableFrameOrigin", "TableFrameName", "TableTargetUrl", "NativeSelectorOrigin", "NativeMenuSelector", "NativeFormatSelector", "NativeFormatValue", "NativeScopeSelector", "NativeScopeValue", "NativeSubmitSelector", "TimeoutSeconds", "DownloadTimeoutSeconds", "ListTimeoutSeconds", "DetailTimeoutSeconds", "MaxPages", "MaxRows", "ExpectedTotal", "ReconnectAttempts", "ResumePagination", "AutoSaveXlsx" };
    public void Capture(AppSettings settings)
    {
        Url = settings.PortalUrl; Browser = settings.BrowserChannel; Mode = settings.AutoMode;
        CaptureAcquisition(settings);
        Interpretation = settings.Dashboard.Copy(); WorkspaceQueries = settings.Queries.Copy();
    }
    internal void CaptureAcquisition(AppSettings settings)
    {
        ExportFolder = settings.ExportFolder;
        Acquisition = AcquisitionFields.ToDictionary(name => name, name => Convert.ToString(typeof(AppSettings).GetProperty(name)!.GetValue(settings), System.Globalization.CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal);
    }
    public void ApplyTo(AppSettings settings)
    {
        settings.PortalUrl = TargetUrlPolicy.PersistedTarget(Url); settings.BrowserChannel = Browser; settings.AutoMode = Mode; settings.ActiveSourceId = Id;
        if (ExportFolder.Length > 0) settings.ExportFolder = ExportFolder;
        foreach (var name in AcquisitionFields)
        {
            var property = typeof(AppSettings).GetProperty(name)!;
            if (Acquisition.TryGetValue(name, out var text))
            {
                if (property.PropertyType == typeof(string)) property.SetValue(settings, text);
                else if (property.PropertyType == typeof(bool) && bool.TryParse(text, out var flag)) property.SetValue(settings, flag);
                else if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)) property.SetValue(settings, number);
                else if (property.PropertyType == typeof(int?)) property.SetValue(settings, null);
            }
            else if (property.PropertyType == typeof(string)) property.SetValue(settings, "");
        }
        if (Interpretation != null)
        {
            var global = settings.Dashboard; settings.Dashboard = Interpretation.Copy();
            if (!RestoreWorkspace) { settings.Dashboard.Layout = global.Layout.Copy(); settings.Dashboard.PersonalLayout = global.PersonalLayout.Copy(); settings.Dashboard.Business = global.Business.Copy(); }
            settings.Dashboard.PersonalTechnician = global.PersonalTechnician;
            settings.Dashboard.OpenPersonalAfterExtraction = global.OpenPersonalAfterExtraction;
        }
        if (RestoreWorkspace && WorkspaceQueries != null) settings.Queries = WorkspaceQueries.Copy();
        settings.Normalize();
    }
    public void Normalize()
    {
        if (!Guid.TryParseExact(Id, "N", out _)) Id = Guid.NewGuid().ToString("N");
        Name = (Name ?? "").Trim(); if (Name.Length > 120) Name = Name[..120];
        try { Url = TargetUrlPolicy.PersistedTarget(Url ?? ""); }
        catch (InvalidOperationException) { Url = ""; }
        Browser = Browser == "chrome" ? "chrome" : "msedge";
        Mode = Mode == "pagination" ? "pagination" : "native";
        ExportFolder ??= "";
        Acquisition = (Acquisition ?? new()).Where(pair => AcquisitionFields.Contains(pair.Key, StringComparer.Ordinal)).ToDictionary(pair => pair.Key, pair => (pair.Value ?? "")[..Math.Min(pair.Value?.Length ?? 0, 8192)], StringComparer.Ordinal);
        Interpretation?.Normalize(); WorkspaceQueries?.Normalize();
    }
}

public sealed class HistorySettings
{
    public bool Enabled { get; set; } = true;
    public bool RestoreLastOnStartup { get; set; }
    public int RetentionDays { get; set; } = 180;
    public int MaximumSnapshots { get; set; } = 500;
    public int StorageBudgetMb { get; set; } = 1024;
    public HistorySettings Copy() => (HistorySettings)MemberwiseClone();
    public void Normalize()
    {
        RetentionDays = Math.Clamp(RetentionDays, 7, 3650);
        MaximumSnapshots = Math.Clamp(MaximumSnapshots, 2, 5000);
        StorageBudgetMb = Math.Clamp(StorageBudgetMb, 32, 32768);
    }
}

public sealed class AutomationSettings
{
    public bool ScheduledExtraction { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public bool RunCampaign { get; set; }
    public int FreshnessHours { get; set; } = 24;
    public int QuietStartHour { get; set; } = 20;
    public int QuietEndHour { get; set; } = 8;
    public bool QuietHours { get; set; } = true;
    public bool AutomaticPaginationFallback { get; set; } = true;
    public string FixedTimes { get; set; } = "";
    public string WeekDays { get; set; } = "1,2,3,4,5";
    public bool ResumeAfterReconnect { get; set; } = true;
    public string Recipe { get; set; } = "extract,quality,enrich,archive,alerts,xlsx";
    public bool RecipeRequiresComplete { get; set; }
    public int FailureAlertThreshold { get; set; } = 3;
    public bool UseRecipe { get; set; }
    public List<AutomationRecipeStep> Steps { get; set; } = new[] { "extract", "quality", "enrich", "archive", "xlsx", "alerts" }.Select(kind => new AutomationRecipeStep { Kind = kind }).ToList();
    public AutomationSettings Copy() { var copy = (AutomationSettings)MemberwiseClone(); copy.Steps = (Steps ?? new()).Select(step => step.Copy()).ToList(); return copy; }
    public void Normalize()
    {
        IntervalMinutes = Math.Clamp(IntervalMinutes, 5, 10080);
        FreshnessHours = Math.Clamp(FreshnessHours, 1, 720);
        QuietStartHour = Math.Clamp(QuietStartHour, 0, 23);
        QuietEndHour = Math.Clamp(QuietEndHour, 0, 23);
        FixedTimes ??= ""; WeekDays ??= "1,2,3,4,5"; Recipe ??= "extract,quality,enrich,archive,alerts,xlsx";
        FailureAlertThreshold = Math.Clamp(FailureAlertThreshold, 1, 20);
        Steps = (Steps ?? new()).Where(step => step != null).Take(20).ToList();
    }
}

public sealed class AutomationRecipeStep
{
    public string Kind { get; set; } = "quality";
    public string Condition { get; set; } = "always";
    public int MinimumRows { get; set; }
    public bool Enabled { get; set; } = true;
    public AutomationRecipeStep Copy() => (AutomationRecipeStep)MemberwiseClone();
}

public sealed class InterfacePreferences
{
    public const int CurrentNavigationDefaultsVersion = 1;
    // Stable route/group keys only. Labels and group organization belong to the UI.
    private static readonly HashSet<string> NavigationIds = new(StringComparer.Ordinal)
    {
        "home", "collect", "sources", "operations", "library", "records", "quality", "mapping",
        "dashboard", "queries", "history", "populations", "assistant", "day", "tickets", "enrichment",
        "technicians", "planning", "terrain-reference", "terrain-review", "tours", "updates",
        "automation", "alerts", "settings", "help", "pilot-weather", "pilot-summary", "pilot-groups", "pilot-categories",
        "pilot-oldest", "pilot-followup", "pilot-participants", "pilot-sites", "pilot-settings"
    };
    private static readonly HashSet<string> NavigationGroupIds = new(StringComparer.Ordinal)
        { "pinned", "workspace", "collect", "pilot", "analyze", "work", "terrain", "admin" };
    public string Density { get; set; } = "balanced";
    public bool FollowWindowsTheme { get; set; }
    public string Accent { get; set; } = "blue";
    public bool HighContrast { get; set; }
    public bool ReduceMotion { get; set; }
    public bool FocusMode { get; set; }
    public string ColorVision { get; set; } = "standard";
    public bool FollowWindowsMotion { get; set; } = true;
    public string PreferredMonitor { get; set; } = "";
    public double[] WindowPixelBounds { get; set; } = Array.Empty<double>();
    public int NavigationDefaultsVersion { get; set; }
    public List<string> NavigationOrder { get; set; } = new();
    public List<string> HiddenNavigation { get; set; } = new();
    public List<string> PinnedNavigation { get; set; } = new();
    public List<string> NavigationGroupOrder { get; set; } = new();
    public Dictionary<string, string> NavigationGroups { get; set; } = new(StringComparer.Ordinal);
    public bool OnboardingCompleted { get; set; }
    public int FrozenColumns { get; set; } = 2;
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
    public InterfacePreferences Copy()
    {
        var copy = (InterfacePreferences)MemberwiseClone();
        copy.NavigationOrder = new(NavigationOrder ?? new()); copy.HiddenNavigation = new(HiddenNavigation ?? new());
        copy.PinnedNavigation = new(PinnedNavigation ?? new()); copy.NavigationGroupOrder = new(NavigationGroupOrder ?? new());
        copy.NavigationGroups = new(NavigationGroups ?? new(), StringComparer.Ordinal);
        copy.WindowPixelBounds = (WindowPixelBounds ?? Array.Empty<double>()).ToArray(); return copy;
    }
    public void Normalize()
    {
        if (Density is not ("compact" or "balanced" or "comfortable")) Density = "balanced";
        if (Accent is not ("blue" or "violet" or "green" or "orange" or "rose")) Accent = "blue";
        if (ColorVision is not ("standard" or "deuteranopia" or "protanopia" or "tritanopia")) ColorVision = "standard";
        PreferredMonitor ??= "";
        if (WindowPixelBounds == null || WindowPixelBounds.Length != 4 || WindowPixelBounds.Any(v => !double.IsFinite(v)) || WindowPixelBounds[2] <= 0 || WindowPixelBounds[3] <= 0) WindowPixelBounds = Array.Empty<double>();
        NavigationOrder = NavigationList(NavigationOrder);
        HiddenNavigation = NavigationList(HiddenNavigation).Where(value => value is not ("home" or "settings")).ToList();
        // Apply new navigation defaults once to both fresh and existing profiles.
        // A later explicit reveal must survive normalization, copies and restarts.
        if (NavigationDefaultsVersion < CurrentNavigationDefaultsVersion)
        {
            foreach (var route in new[] { "assistant", "enrichment", "populations" })
                if (!HiddenNavigation.Contains(route, StringComparer.Ordinal)) HiddenNavigation.Add(route);
            NavigationDefaultsVersion = CurrentNavigationDefaultsVersion;
        }
        PinnedNavigation = NavigationList(PinnedNavigation);
        NavigationGroupOrder = (NavigationGroupOrder ?? new()).Where(value => value != null && NavigationGroupIds.Contains(value)).Distinct(StringComparer.Ordinal).Take(NavigationGroupIds.Count).ToList();
        // Pinned is a view of PinnedNavigation, not a second assignment for the same route.
        NavigationGroups = (NavigationGroups ?? new()).Where(pair => NavigationIds.Contains(pair.Key)
            && pair.Value != null && pair.Value != "pinned" && NavigationGroupIds.Contains(pair.Value))
            .Take(NavigationIds.Count).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        FrozenColumns = Math.Clamp(FrozenColumns, 0, 8);
        if (!double.IsFinite(WindowWidth) || WindowWidth < 0) WindowWidth = 0;
        if (!double.IsFinite(WindowHeight) || WindowHeight < 0) WindowHeight = 0;
        if (WindowLeft.HasValue && !double.IsFinite(WindowLeft.Value)) WindowLeft = null;
        if (WindowTop.HasValue && !double.IsFinite(WindowTop.Value)) WindowTop = null;
    }
    private static List<string> NavigationList(List<string>? values) => (values ?? new())
        .Where(value => value != null && NavigationIds.Contains(value)).Distinct(StringComparer.Ordinal).Take(NavigationIds.Count).ToList();
}

/// <summary>Atomic settings with bounded local versions. Browser sessions and ticket data are never included.</summary>
public static class SettingsRecovery
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly object Gate = new();
    public static string LastRecoveryMessage { get; private set; } = "";
    public static AppSettings Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length == 0 || file.Length > MaximumBytes) throw new InvalidDataException("Le fichier de réglages est vide ou trop volumineux.");
        using var memory = new MemoryStream(); var buffer = new byte[16_384]; int read;
        while ((read = file.Read(buffer)) > 0)
        {
            if (memory.Length + read > MaximumBytes) throw new InvalidDataException("Le fichier de réglages dépasse 4 Mio.");
            memory.Write(buffer, 0, read);
        }
        return Read(memory.ToArray());
    }
    internal static AppSettings Read(ReadOnlyMemory<byte> content, bool validateReference = false)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
        var settings = document.RootElement.Deserialize<AppSettings>(new JsonSerializerOptions { MaxDepth = 32 })
            ?? throw new InvalidDataException("Les réglages sont absents.");
        if (validateReference) settings.Cities?.Validate();
        settings.Normalize();
        RestoreLegacyActiveAcquisition(document.RootElement, settings);
        return settings;
    }
    private static void RestoreLegacyActiveAcquisition(JsonElement document, AppSettings settings)
    {
        // v2 stored one global acquisition and source identities only. Do not infer legacy
        // from an empty dictionary: an explicitly empty modern recipe must stay empty.
        if (settings.ActiveSourceId.Length == 0 || !document.TryGetProperty(nameof(AppSettings.Sources), out var sources)
            || sources.ValueKind != JsonValueKind.Array) return;
        var active = settings.Sources.Where(source => source.Id == settings.ActiveSourceId).Take(2).ToArray();
        if (active.Length != 1) return;
        var originals = sources.EnumerateArray().Where(source => source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty(nameof(SourceProfile.Id), out var id) && id.ValueKind == JsonValueKind.String
            && id.GetString() == settings.ActiveSourceId).Take(2).ToArray();
        if (originals.Length != 1 || originals[0].TryGetProperty(nameof(SourceProfile.Acquisition), out _)) return;

        string currentUrl;
        try { currentUrl = TargetUrlPolicy.PersistedTarget(settings.PortalUrl); }
        catch (InvalidOperationException) { return; }
        // SamePage deliberately ignores search filters; that is not enough to establish
        // which historical source owns the global recipe.
        if (currentUrl.Length == 0 || !string.Equals(active[0].Url, currentUrl, StringComparison.Ordinal)) return;
        active[0].CaptureAcquisition(settings);
    }
    public static AppSettings Load(string path)
    {
        lock (Gate)
        {
            LastRecoveryMessage = "";
            try { if (File.Exists(path)) return Read(path); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { LastRecoveryMessage = "Le fichier de réglages est illisible. "; }
            IReadOnlyList<SettingsBackup> backups;
            try { backups = List(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { backups = Array.Empty<SettingsBackup>(); }
            foreach (var backup in backups)
            {
                try
                {
                    var recovered = Read(backup.Path);
                    LastRecoveryMessage += "Une version locale du " + backup.SavedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm") + " a été récupérée.";
                    return recovered;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
            }
            if (LastRecoveryMessage.Length > 0) LastRecoveryMessage += "Aucune sauvegarde valide : les valeurs par défaut sont chargées ; le fichier original reste présent jusqu’à un enregistrement.";
            return new();
        }
    }
    public static IReadOnlyList<SettingsBackup> List(string path)
    {
        var folder = BackupFolder(path);
        if (!Directory.Exists(folder)) return Array.Empty<SettingsBackup>();
        return Directory.EnumerateFiles(folder, "settings-*.json").Take(200)
            .Select(file => new FileInfo(file)).Where(file => file.Length is > 0 and <= MaximumBytes)
            .OrderByDescending(file => file.LastWriteTimeUtc).Take(30)
            .Select(file => new SettingsBackup(file.FullName, new DateTimeOffset(file.LastWriteTimeUtc), file.Length)).ToArray();
    }
    public static void Save(string path, AppSettings settings)
    {
        lock (Gate)
        {
            settings.Normalize();
            var content = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true, MaxDepth = 32 });
            if (content.Length > MaximumBytes) throw new InvalidDataException("Les réglages dépassent 4 Mio.");
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!; Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length == content.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
                    var folder = BackupFolder(path); Directory.CreateDirectory(folder);
                    // Keep an unreadable original as evidence, separately from valid restorations.
                    var prefix = "settings-";
                    try { _ = Read(path); }
                    catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException) { prefix = "unreadable-"; }
                    if (info.Length <= MaximumBytes)
                        File.Copy(path, Path.Combine(folder, prefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json"));
                }
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(content); stream.Flush(true); }
                File.Move(temporary, path, true);
                var backups = BackupFolder(path);
                if (Directory.Exists(backups))
                    foreach (var old in Directory.EnumerateFiles(backups, "settings-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(30))
                        try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private static string BackupFolder(string path) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "SettingsVersions");
}
public sealed record SettingsBackup(string Path, DateTimeOffset SavedAt, long Bytes)
{
    public string Label => SavedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") + $" · {Bytes / 1024.0:N1} Kio";
}
