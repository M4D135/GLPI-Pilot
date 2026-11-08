using GlpiPilot.Runtime;

var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-CollectionJourney-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1");
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root);
var total = 0; var passed = 0;
try
{
    await CollectionJourneyChecks.RunAsync(root, async (name, test) =>
    {
        total++;
        try { await test().WaitAsync(TimeSpan.FromMinutes(3)); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + name + "\n" + error); }
    });
}
finally
{
    Console.WriteLine($"{passed}/{total} resumable collection browser journeys passed.");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
    Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", null);
    Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", null);
}
return passed == total && total > 0 ? 0 : 1;

sealed class NullHost : IPilotHost
{
    public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult<string?>(null);
    public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task SetClipboardAsync(string text) => Task.CompletedTask;
    public Task OpenExternalAsync(string url) => Task.CompletedTask;
    public Task CloseAsync() => Task.CompletedTask;
    public Task<byte[]> CapturePngAsync() => Task.FromResult(Array.Empty<byte>());
    public Task PrintPdfAsync(string path) => Task.CompletedTask;
    public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
}
