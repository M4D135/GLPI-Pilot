using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public static class AdministrationStandardChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create() => new (string, Func<Task>)[]
    {
        ("Administration: Standard rejects direct maintenance commands and cannot elevate through the web bridge or demo", async () =>
        {
            await using var f = new SessionFixture();
            if (AdministrationCapabilities.Enabled || (await f.Call("library.get")).GetProperty("administration").GetBoolean())
                throw new InvalidOperationException("The Standard fixture must have no compiled administrative capability.");
            var before = JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json);
            var entered = false; f.App.RegisterSessionFixture("admin.injected", (_, _) => { entered = true; return Task.FromResult<object?>(null); });
            foreach (var op in new[] { "admin.local.preview", "admin.local.reset", "admin.postgres.prepare", "admin.injected" })
                await Refuse(() => f.Call(op, new { administration = true, admin = true, confirmed = true, token = "spoofed" }));
            await Refuse(() => LocalDatabaseMaintenance.PreviewAsync(Path.Combine(f.Root, "must-not-exist.sqlite"), true));
            await Refuse(() => PostgresDatabaseProvisioning.PrepareAsync("", "irrelevant", "irrelevant", "irrelevant"));
            if (entered || File.Exists(Path.Combine(f.Root, "must-not-exist.sqlite")) || before != JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json))
                throw new InvalidOperationException("An administrative command reached the Standard runtime or changed its data.");
            await f.Call("app.demo");
            await Refuse(() => f.Call("admin.local.preview"));
            await Refuse(() => f.Call("admin.postgres.prepare"));
        })
    };
    private static async Task Refuse(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException error) when (error.Message.Contains("Standard", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("The Standard build accepted an administrative command.");
    }
}
