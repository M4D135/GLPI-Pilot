using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

internal static class RelaisFixtureWriter
{
    internal static void Write(RelaisExportResult result, string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = result.Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); connection.Open();
        var sql = new StringBuilder("-- Generated from the real GLPI-Pilot C# export; synthetic data only.\n");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 ELSE 2 END,name";
            using var reader = command.ExecuteReader(); while (reader.Read()) sql.Append(reader.GetString(0)).AppendLine(";");
        }
        var tables = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var table in new[] { "ticket_library_schema", "relais_manifest", "relais_tickets", "relais_texts" })
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY " + (table == "relais_texts" ? "citation_key" : table == "relais_tickets" ? "ticket_key" : "singleton");
            using var reader = command.ExecuteReader(); var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i)); rows.Add(row);
            }
            tables.Add(table, rows);
        }
        var json = new JsonSerializerOptions { WriteIndented = true };
        var schemaPath = Path.Combine(directory, "schema.sql"); var documentsPath = Path.Combine(directory, "documents.json");
        File.WriteAllText(schemaPath, sql.ToString(), new UTF8Encoding(false)); File.WriteAllText(documentsPath, JsonSerializer.Serialize(tables, json) + "\n", new UTF8Encoding(false));
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var corePath = Path.Combine(project, "src/GlpiPilot/Core/TicketLibraryRelaisExport.cs");
        var fixturePath = Path.Combine(project, "tests/RelaisExportChecks/RelaisFixture.cs");
        var provenance = new
        {
            fixtureVersion = 1, synthetic = true, generator = "GLPI-Pilot/tests/RelaisExportChecks", exportMethod = "TicketLibraryStore.ExportRelaisAsync",
            command = "dotnet run --project tests/RelaisExportChecks/RelaisExportChecks.csproj -c Release -p:Version=" + AppPaths.ApplicationVersion + " -- --fixture <directory>",
            sourceCommit = GitHead(project), sourceWorkingTree = "includes the accompanying Relais export change", harnessApplicationVersion = AppPaths.ApplicationVersion,
            generatedAt = DateTimeOffset.UtcNow, installation = result.Installation, ticketCount = result.TicketCount, textCount = result.TextCount,
            sqliteBytes = result.FileBytes, sqliteSha256 = result.Sha256, schemaSha256 = Digest(schemaPath), documentsSha256 = Digest(documentsPath),
            exporterSourceSha256 = File.Exists(corePath) ? Digest(corePath) : "", fixtureSourceSha256 = File.Exists(fixturePath) ? Digest(fixturePath) : "",
            note = "Created by the real C# exporter from synthetic native library documents. The test harness assembly supplies application_version. Reconstruct the SQLite file using schema.sql and the four table arrays in documents.json. No binary database or user data is versioned."
        };
        File.WriteAllText(Path.Combine(directory, "provenance.json"), JsonSerializer.Serialize(provenance, json) + "\n", new UTF8Encoding(false));
    }
    private static string Digest(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static string GitHead(string directory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git") { ArgumentList = { "rev-parse", "HEAD" }, WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null) return ""; var output = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); return process.ExitCode == 0 ? output : "";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
    }
}
