using GlpiPilot.Core;

// Browser checks run sequentially, but every fixture owns its durable recovery
// state. A cancelled or deliberately interrupted collection must not affect the
// next synthetic installation/user in the suite.
internal sealed class ColumnTestStorageScope : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_ROOT");
    private readonly string? _previousMode = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_MODE");
    private bool _disposed;

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Columns-" + Guid.NewGuid().ToString("N"));

    public ColumnTestStorageScope()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", Root);
        if (AppPaths.DataRoot != Root) throw new InvalidOperationException("The column fixture must use its isolated durable journal.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", _previousRoot);
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", _previousMode);
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
