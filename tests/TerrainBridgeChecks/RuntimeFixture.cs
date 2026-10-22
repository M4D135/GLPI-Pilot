using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

// The dispatcher, terrain handlers, file stores and computation engines below are the production sources.
// Only unrelated domains and native dialogs are replaced for this offline, isolated fixture.
public sealed partial class PilotApplication
{
    private void RegisterFoundationOperations() { }
    private void RegisterAnalyticsOperations() { }
    private void RegisterWorkplaceOperations() { }
    public void LoadFixture(ExtractionResult source, CityEnrichmentResult? locations = null, SnapshotDocument? history = null) => SetResult(source, locations, history);
    public void ChangeFixtureDashboard(DashboardSettings value) => Settings.Dashboard = value;
}

public sealed class TerrainTestHost : IPilotHost
{
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public int Closures { get; private set; }
    public List<string> ExternalUrls { get; } = new();
    public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult(OpenPath);
    public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult(SavePath);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task SetClipboardAsync(string text) => Task.CompletedTask;
    public Task OpenExternalAsync(string url) { ExternalUrls.Add(url); return Task.CompletedTask; }
    public Task CloseAsync() { Closures++; return Task.CompletedTask; }
    public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
    public Task<byte[]> CapturePngAsync() => throw new NotSupportedException();
    public Task PrintPdfAsync(string path) => throw new NotSupportedException();
}
