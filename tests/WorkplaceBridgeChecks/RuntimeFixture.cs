using GlpiPilot.Core;
namespace GlpiPilot.Runtime;

// Fixture data enters the complete production runtime, including simulation isolation.
public sealed partial class PilotApplication
{
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
