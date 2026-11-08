using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public sealed class CalendarFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Calendar-" + Guid.NewGuid().ToString("N"));
    public CalendarHost Host { get; } = new();
    public ConcurrentQueue<(string Name, object? Data)> Events { get; } = new();
    public PilotApplication App { get; }
    public CalendarFixture()
    {
        Directory.CreateDirectory(Root); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1"); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", Root);
        var settings = new AppSettings { PortalUrl = "https://calendar-glpi.test/front/ticket.php", ExportFolder = Path.Combine(Root, "exports"), Dashboard = new()
        { ColumnMappings = new() { ["id"] = "ID", ["title"] = "Titre", ["status"] = "Statut", ["assignee"] = "Technicien", ["group"] = "Groupe" } } };
        App = new(Host, (name, data) => Events.Enqueue((name, data)), settings, snapshots: new SnapshotStore(Path.Combine(Root, "history")));
    }
    public async Task<JsonElement> Call(string operation, object? arguments = null)
        => JsonSerializer.SerializeToElement(await App.ExecuteAsync(operation, JsonSerializer.SerializeToElement(arguments ?? new { }, PilotApplication.Json)), PilotApplication.Json);
    public object Range(string scope = "all") => new { from = "2026-11-02", to = "2026-11-09", timeZoneId = "Europe/Paris", scope };
    public CalendarIntervention Event(string title = "Intervention libre", string start = "2026-11-02T09:00", string end = "2026-11-02T10:00", params string[] participants)
        => new() { Title = title, StartLocal = start, EndLocal = end, TimeZoneId = "Europe/Paris", ParticipantKeys = participants.ToList() };
    public async Task<JsonElement> Save(CalendarIntervention value, long? revision = null, bool force = false)
    {
        string? fingerprint = null;
        if (force) fingerprint = (await Call("calendar.preview", new { intervention = value, expectedRevision = revision })).GetProperty("conflictFingerprint").GetString();
        return await Call("calendar.save", new { intervention = value, expectedRevision = revision, allowConflicts = force, conflictFingerprint = fingerprint });
    }
    public static CalendarIntervention Entry(JsonElement reply) => reply.GetProperty("item").GetProperty("intervention").Deserialize<CalendarIntervention>(PilotApplication.Json)!;
    public static ExtractionResult Data(params string[][] rows) => new()
    {
        Headers = new() { "ID", "Titre", "Statut", "Technicien", "Groupe" }, Rows = rows.ToList(), SourceUrl = "https://calendar-glpi.test/front/ticket.php", Complete = true,
        Provenance = new() { SourceKind = "glpi", SearchVerified = true, ScopeVerified = true }
    };
    public async ValueTask DisposeAsync() { await App.DisposeAsync(); try { Directory.Delete(Root, true); } catch (IOException) { } }
}
public sealed class CalendarHost : IPilotHost
{
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public string? FolderPath { get; set; }
    public int FolderPicks { get; private set; }
    public int Notifications { get; private set; }
    public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult(OpenPath);
    public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult(SavePath);
    public Task<string?> PickFolderAsync(string title) { FolderPicks++; return Task.FromResult(FolderPath); }
    public Task SetClipboardAsync(string text) => Task.CompletedTask;
    public Task OpenExternalAsync(string url) => throw new InvalidOperationException("No network navigation in calendar checks.");
    public Task CloseAsync() => Task.CompletedTask;
    public Task<byte[]> CapturePngAsync() => throw new NotSupportedException();
    public Task PrintPdfAsync(string path) => throw new NotSupportedException();
    public Task<bool> NotifyAsync(string title, string message) { Notifications++; return Task.FromResult(true); }
}
namespace GlpiPilot.Runtime
{
    public sealed partial class PilotApplication
    {
        public void CalendarLoadFixture(ExtractionResult data) => SetResult(data);
        public TeamWorkspaceStore CalendarStoreFixture => TeamWorkspace;
    }
}
