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

    public static AppSettings Load()
    {
        if (!File.Exists(AppPaths.SettingsFile)) return new();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(AppPaths.DataRoot);
        // Search filters are part of the recipe. Log redaction must never alter navigation.
        PortalUrl = TargetUrlPolicy.PersistedTarget(PortalUrl);
        TableTargetUrl = TargetUrlPolicy.PersistedTarget(TableTargetUrl);
        var temp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, AppPaths.SettingsFile, true);
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
    }

    public static int NormalizeInterfaceZoom(int value) =>
        (Math.Clamp(value, MinimumInterfaceZoomPercent, MaximumInterfaceZoomPercent) + 2) / InterfaceZoomStep * InterfaceZoomStep;
}
