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
    public SourceProfile Copy() => (SourceProfile)MemberwiseClone();
    public void Normalize()
    {
        if (!Guid.TryParseExact(Id, "N", out _)) Id = Guid.NewGuid().ToString("N");
        Name = (Name ?? "").Trim(); if (Name.Length > 120) Name = Name[..120];
        try { Url = TargetUrlPolicy.PersistedTarget(Url ?? ""); }
        catch (InvalidOperationException) { Url = ""; }
        Browser = Browser == "chrome" ? "chrome" : "msedge";
        Mode = Mode == "pagination" ? "pagination" : "native";
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
    public AutomationSettings Copy() => (AutomationSettings)MemberwiseClone();
    public void Normalize()
    {
        IntervalMinutes = Math.Clamp(IntervalMinutes, 5, 10080);
        FreshnessHours = Math.Clamp(FreshnessHours, 1, 720);
        QuietStartHour = Math.Clamp(QuietStartHour, 0, 23);
        QuietEndHour = Math.Clamp(QuietEndHour, 0, 23);
    }
}

public sealed class InterfacePreferences
{
    public string Density { get; set; } = "balanced";
    public bool FollowWindowsTheme { get; set; }
    public string Accent { get; set; } = "blue";
    public bool HighContrast { get; set; }
    public bool ReduceMotion { get; set; }
    public bool FocusMode { get; set; }
    public int FrozenColumns { get; set; } = 2;
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
    public InterfacePreferences Copy() => (InterfacePreferences)MemberwiseClone();
    public void Normalize()
    {
        if (Density is not ("compact" or "balanced" or "comfortable")) Density = "balanced";
        if (Accent is not ("blue" or "violet" or "green" or "orange" or "rose")) Accent = "blue";
        FrozenColumns = Math.Clamp(FrozenColumns, 0, 8);
        if (!double.IsFinite(WindowWidth) || WindowWidth < 0) WindowWidth = 0;
        if (!double.IsFinite(WindowHeight) || WindowHeight < 0) WindowHeight = 0;
        if (WindowLeft.HasValue && !double.IsFinite(WindowLeft.Value)) WindowLeft = null;
        if (WindowTop.HasValue && !double.IsFinite(WindowTop.Value)) WindowTop = null;
    }
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
        var settings = JsonSerializer.Deserialize<AppSettings>(memory.ToArray(), new JsonSerializerOptions { MaxDepth = 32 })
            ?? throw new InvalidDataException("Les réglages sont absents.");
        settings.Normalize(); return settings;
    }
    public static AppSettings Load(string path)
    {
        lock (Gate)
        {
            LastRecoveryMessage = "";
            try { if (File.Exists(path)) return Read(path); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
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
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
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
                    catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException) { prefix = "unreadable-"; }
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
