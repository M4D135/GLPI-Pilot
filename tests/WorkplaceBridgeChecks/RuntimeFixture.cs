using GlpiPilot.Core;
namespace GlpiPilot.Runtime;

// Only unrelated domains are omitted. The dispatcher, handlers, persistence and engines are production code.
public sealed partial class PilotApplication
{
    private void RegisterFoundationOperations() { }
    private void RegisterAnalyticsOperations() { }
    private void RegisterTerrainOperations() { }
    public void LoadFixture(ExtractionResult data, SnapshotDocument? historical = null) => SetResult(data, historical: historical);
    public void ReadFixture(TicketDetailResult detail) => WorkAcceptDetail(detail);
}

public sealed class WorkplaceTestHost : IPilotHost
{
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public string Clipboard { get; private set; } = "";
    public int Notifications { get; private set; }
    public bool ShowNotification { get; set; }
    public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult(OpenPath);
    public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult(SavePath);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task SetClipboardAsync(string text) { Clipboard = text; return Task.CompletedTask; }
    public Task OpenExternalAsync(string url) => throw new InvalidOperationException("External navigation prohibited by synthetic fixture.");
    public Task CloseAsync() => Task.CompletedTask;
    public Task<bool> NotifyAsync(string title, string message) { Notifications++; return Task.FromResult(ShowNotification); }
    public Task<byte[]> CapturePngAsync() => throw new NotSupportedException();
    public Task PrintPdfAsync(string path) => throw new NotSupportedException();
}
