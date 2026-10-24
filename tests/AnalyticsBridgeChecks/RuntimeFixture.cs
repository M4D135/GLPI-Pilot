using GlpiPilot.Core;
using Microsoft.Playwright;
namespace GlpiPilot.Runtime;

// A fixture entry point loads a known observation into the complete runtime.
// Analytics, simulation, stores and their responses are production implementations.
public sealed partial class PilotApplication
{
    public void LoadFixture(ExtractionResult source, CityEnrichmentResult? locations = null) => SetResult(source, locations);
}

public sealed class AnalyticsTestHost : IPilotHost
{
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public int Captures { get; private set; }
    public int Prints { get; private set; }
    public IPage? Page { get; set; }
    public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult(OpenPath);
    public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult(SavePath);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task SetClipboardAsync(string text) => Task.CompletedTask;
    public Task OpenExternalAsync(string url) => throw new InvalidOperationException("No external navigation permitted in analytics tests.");
    public Task CloseAsync() => Task.CompletedTask;
    public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
    public Task<byte[]> CapturePngAsync() { Captures++; return Page?.ScreenshotAsync() ?? throw new NotSupportedException("Visual capture requires the actual WebView host."); }
    public async Task PrintPdfAsync(string path) { Prints++; if (Page is null) throw new NotSupportedException("PDF printing requires the actual WebView host."); await Page.PdfAsync(new() { Path = path, PrintBackground = true }); }
}
