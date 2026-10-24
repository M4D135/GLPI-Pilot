using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public sealed class SessionFixture : IAsyncDisposable
{
    public const string SourceA = "http://session-glpi.test/glpi/front/ticket.php?query=A";
    public const string SourceB = "http://session-glpi.test/glpi/front/ticket.php?query=B";
    public const string ProfileA = "11111111111111111111111111111111";
    public const string ProfileB = "22222222222222222222222222222222";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Session-" + Guid.NewGuid().ToString("N"));
    public SessionHost Host { get; } = new();
    public ConcurrentQueue<(string Name, object? Data)> Events { get; } = new();
    public PilotApplication App { get; }
    public SessionFixture(BrowserService? browser = null)
    {
        Directory.CreateDirectory(Root); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1"); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", Root);
        var settings = new AppSettings { PortalUrl = SourceA, ActiveSourceId = ProfileA, ExportFolder = Path.Combine(Root, "exports"), Dashboard = MappingA() };
        var a = new SourceProfile { Id = ProfileA, Name = "Source A" }; a.Capture(settings);
        var b = new SourceProfile { Id = ProfileB, Name = "Source B", Url = SourceB, Interpretation = MappingB() };
        settings.Sources.AddRange(new[] { a, b });
        App = new(Host, (name, data) => Events.Enqueue((name, data)), settings, browser, new SnapshotStore(Path.Combine(Root, "history")));
        App.LoadSessionFixture(Data());
    }
    public static DashboardSettings MappingA() => new() { ColumnMappings = new() { ["id"] = "ID", ["title"] = "Titre", ["status"] = "Statut" } };
    public static DashboardSettings MappingB() => new() { ColumnMappings = new() { ["id"] = "Autre ID", ["title"] = "Autre titre", ["status"] = "Statut" } };
    public static ExtractionResult Data() => new()
    {
        Headers = new() { "ID", "Titre", "Statut", "Autre ID", "Autre titre" }, Rows = new() { new[] { "42", "Observation A", "Nouveau", "84", "Autre interprétation" } },
        SourceUrl = SourceA, ExtractedAt = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero), Complete = true,
        Provenance = new() { RequestedUrl = SourceA, SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "entity-synthetic", SourceKind = "glpi" }
    };
    public async Task<JsonElement> Call(string operation, object? args = null, CancellationToken ct = default)
    {
        var result = await App.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args ?? new { }, PilotApplication.Json), ct);
        return JsonSerializer.SerializeToElement(result, PilotApplication.Json);
    }
    public Task<JsonElement> SelectB() => Call("sources.select", new { id = ProfileB, revision = App.Revision });
    public async ValueTask DisposeAsync() { await App.DisposeAsync(); try { Directory.Delete(Root, true); } catch (IOException) { } }
}

public sealed class SessionHost : IPilotHost
{
    public List<string> GoogleMapUrls { get; } = new();
    public List<GoogleMapFrame> GoogleMapFrames { get; } = new();
    public Task OpenGoogleMapsAsync(string url) { if (!GoogleMapsService.IsExternalMapUrl(url)) throw new InvalidDataException(); GoogleMapUrls.Add(url); return Task.CompletedTask; }
    public Task ShowGoogleMapAsync(GoogleMapFrame frame) { if (!GoogleMapsService.IsEmbedUrl(frame.EmbedUrl)) throw new InvalidDataException(); GoogleMapFrames.Add(frame); return Task.CompletedTask; }
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public string Clipboard { get; private set; } = "";
    public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult(OpenPath);
    public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult(SavePath);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task SetClipboardAsync(string text) { Clipboard = text; return Task.CompletedTask; }
    public Task OpenExternalAsync(string url) => throw new InvalidOperationException("No external navigation in the session fixture.");
    public Task CloseAsync() => Task.CompletedTask;
    public Task<byte[]> CapturePngAsync() => throw new NotSupportedException();
    public Task PrintPdfAsync(string path) => throw new NotSupportedException();
    public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
}

namespace GlpiPilot.Runtime
{
    public sealed partial class PilotApplication
    {
        public void LoadSessionFixture(ExtractionResult data, SnapshotDocument? historical = null) => SetResult(data, historical: historical);
        public void LoadEnrichedSessionFixture(CityEnrichmentResult locations, AppSettings? context = null) => SetResult(locations.Data, locations, context: context);
        public void RegisterSessionFixture(string name, Func<JsonElement, CancellationToken, Task<object?>> handler) => Register(name, handler);
        public void ForceScheduledFixture(Func<JsonElement, CancellationToken, Task<object?>> collect)
        {
            Settings.Automation.ScheduledExtraction = true; Settings.Automation.QuietHours = false; Settings.Automation.UseRecipe = false; Settings.Automation.RunCampaign = false;
            _networkAvailable = () => true; // Synthetic platform availability; the collection handler below never uses a network.
            _automaticPolicyStamp = JsonSerializer.Serialize(Settings.Automation, Json); _nextAutomatic = DateTimeOffset.Now.AddMinutes(-1); _handlers["collect.run"] = collect;
        }
    }
}
