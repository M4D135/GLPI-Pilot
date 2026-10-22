using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private const ConfigurationParts SharedConfigurationParts = ConfigurationParts.Indicators | ConfigurationParts.Dashboards | ConfigurationParts.Queries | ConfigurationParts.Cities;

    private void RegisterSharedWorkspaceOperations()
    {
        Register("workspace.list", async (a, ct) => await TeamWorkspace.ListAsync(Text(a, "kind", 30), Text(a, "ticketKey", 200), Text(a, "text", 2000),
            Number(a, "offset", 0, 0, 1_000_000), Number(a, "limit", 50, 1, 100), ct).ConfigureAwait(false));
        Register("workspace.get", async (a, ct) => await RequireWorkspaceItemAsync(a, ct).ConfigureAwait(false));
        Register("workspace.save", SaveWorkspaceItemAsync);
        Register("workspace.remove", async (a, ct) =>
        {
            var item = await TeamWorkspace.RemoveAsync(Text(a, "id", 80), WorkspaceExpectedRevision(a, required: true)!.Value, ct).ConfigureAwait(false);
            Publish("library.changed", new { team = true }); return item;
        });
        Register("workspace.conflicts", async (a, ct) =>
        {
            var id = Text(a, "id", 80); var offset = Number(a, "offset", 0, 0, 1_000_000); var limit = Number(a, "limit", 5, 1, 20);
            var versions = await TeamWorkspace.ConflictsAsync(id, ct, offset, limit).ConfigureAwait(false);
            // Lists stay small. A dedicated comparison reads the actual fields of one archived pair.
            var items = versions.Select(version => new
            {
                version.Id, version.Reason, version.RecordedAt,
                current = new { version.Current.Kind, version.Current.Author, version.Current.UpdatedAt, version.Current.Title, version.Current.Body, version.Current.Deleted },
                incoming = new { version.Incoming.Kind, version.Incoming.Author, version.Incoming.UpdatedAt, version.Incoming.Title, version.Incoming.Body, version.Incoming.Deleted }
            }).ToArray();
            return new { items, total = await TeamWorkspace.ConflictCountAsync(id, ct).ConfigureAwait(false), offset, limit };
        });
        Register("workspace.conflict.compare", CompareWorkspaceConflictAsync);
        Register("workspace.conflict.export", ExportWorkspaceConflictAsync);
        Register("workspace.export", ExportWorkspaceAsync);
        Register("workspace.publishConfiguration", PublishWorkspaceConfigurationAsync);
        Register("workspace.configuration.preview", PreviewWorkspaceConfigurationAsync);
        Register("workspace.publishStatistics", PublishWorkspaceStatisticsAsync);
        RegisterSharedOperationalOperations();
    }
    private async Task<TeamWorkspaceItem> RequireWorkspaceItemAsync(JsonElement args, CancellationToken ct)
    {
        var item = await TeamWorkspace.GetAsync(Text(args, "id", 80), ct).ConfigureAwait(false);
        return item is { Deleted: false } ? item : throw new InvalidDataException("Contribution absente de l’espace d’équipe.");
    }
    private static long? WorkspaceExpectedRevision(JsonElement args, bool required)
    {
        if (args.TryGetProperty("expectedRevision", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var revision) && revision >= 0) return revision;
        if (required) throw new InvalidDataException("Relisez la contribution avant de la modifier : sa version locale est nécessaire.");
        return null;
    }
    private async Task<object?> SaveWorkspaceItemAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("item", out var input) || input.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Contribution manquante.");
        var id = Text(input, "id", 80); var existing = id.Length > 0;
        var item = new TeamWorkspaceItem
        {
            Id = existing ? id : Guid.NewGuid().ToString("N"), Kind = Text(input, "kind", 30, "note"),
            Title = Text(input, "title", 300).Trim(), Body = Text(input, "body", 200000),
            TicketKey = Text(input, "ticketKey", 200), Assignee = Text(input, "assignee", 200),
            Status = Text(input, "status", 30, "draft"), Author = LibrarySettings.DisplayName
        };
        if (item.Kind is not ("comment" or "knowledge" or "assignment" or "note"))
            throw new InvalidDataException("Utilisez la publication dédiée pour partager une configuration ou des statistiques.");
        if (item.TicketKey.Length > 0 && await TicketLibrary.GetAsync(item.TicketKey, ct).ConfigureAwait(false) is null)
            throw new InvalidDataException("Le ticket lié doit être présent dans la bibliothèque locale.");
        var saved = await TeamWorkspace.SaveAsync(item, WorkspaceExpectedRevision(args, existing), ct).ConfigureAwait(false);
        Publish("library.changed", new { team = true }); return saved;
    }
    private async Task<object?> ExportWorkspaceAsync(JsonElement _, CancellationToken ct)
    {
        var path = await Host.SaveFileAsync("Exporter l’espace d’équipe", "Contributions JSONL|*.jsonl", "GLPI-Pilot-equipe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl").ConfigureAwait(false);
        if (path is null) return new { cancelled = true };
        var full = Path.GetFullPath(path);
        var database = LibrarySettings.DatabasePath;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (new[] { database, database + "-wal", database + "-shm", database + "-journal" }.Any(active => string.Equals(full, active, comparison)))
            throw new InvalidDataException("L’export ne peut pas remplacer la base active ni ses journaux.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!); var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { protocol = "glpi-pilot-team-workspace", format = 1, exportedAt = DateTimeOffset.UtcNow }, Json) + "\n");
                await stream.WriteAsync(header, ct).ConfigureAwait(false);
                await TeamWorkspace.ExportAsync(stream, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new { path = full, cancelled = false };
    }
    private async Task<object?> PublishWorkspaceConfigurationAsync(JsonElement args, CancellationToken ct)
    {
        var path = DataPath("shared-configuration-" + Guid.NewGuid().ToString("N") + ".tmp");
        JsonElement payload;
        try
        {
            // Build from business sections only. Machine folders, connection settings, SSO and browser profiles never enter this document.
            var portable = ConfigurationReview.Apply(new AppSettings(), Settings, SharedConfigurationParts, false);
            ConfigurationTransferService.Export(path, portable);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)); payload = document.RootElement.Clone();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        var body = $"Configuration partagée\n\n{Settings.Dashboard.Layout.Boards.Count} tableaux de bord, {Settings.Dashboard.ColumnMappings.Count} correspondances de colonnes, {Settings.Queries.Queries.Count} requêtes, {Settings.Cities.Cities.Count} villes.\n\nSections : indicateurs, tableaux de bord, requêtes et villes. Examinez les différences avant de les appliquer sur ce poste.";
        var item = new TeamWorkspaceItem { Id = Guid.NewGuid().ToString("N"), Kind = "configuration", Title = Text(args, "title", 300, "Configuration de l’équipe").Trim(), Body = body, Author = LibrarySettings.DisplayName, Status = "published", Payload = payload };
        var saved = await TeamWorkspace.SaveAsync(item, null, ct).ConfigureAwait(false); Publish("library.changed", new { team = true }); return saved;
    }
    private async Task<object?> PreviewWorkspaceConfigurationAsync(JsonElement args, CancellationToken ct)
    {
        var item = await RequireWorkspaceItemAsync(args, ct).ConfigureAwait(false);
        if (item.Kind != "configuration" || item.Payload is not { } payload) throw new InvalidDataException("Cette contribution ne contient pas de configuration d’équipe.");
        var path = DataPath("shared-configuration-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(path, payload.GetRawText(), ct).ConfigureAwait(false);
            var incoming = ConfigurationTransferService.Import(path, Settings);
            // Even a document produced by another client may only propose the four shared business sections.
            _configurationDraft = ConfigurationReview.Apply(Settings, incoming, SharedConfigurationParts, false);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        ct.ThrowIfCancellationRequested(); _configurationToken = Guid.NewGuid().ToString("N"); _configurationBaseline = JsonSerializer.Serialize(Settings, Json);
        return new
        {
            name = item.Title, token = _configurationToken,
            sections = ConfigurationReview.Describe(Settings, _configurationDraft).Where(section => SharedConfigurationParts.HasFlag(section.Part)),
            fields = ConfigurationReview.DescribeFields(Settings, _configurationDraft), conflicts = ConfigurationReview.Conflicts(Settings, _configurationDraft)
        };
    }
    private async Task<object?> PublishWorkspaceStatisticsAsync(JsonElement args, CancellationToken ct)
    {
        var data = RequireResult(); var settings = EffectiveSettings;
        var snapshot = DashboardService.Calculate(data, settings.Dashboard, ct: ct);
        QueryService.AppendDashboardQueries(snapshot, data, settings.Dashboard, settings.Queries, ct);
        var culture = CultureInfo.GetCultureInfo("fr-FR");
        var body = new StringBuilder().AppendLine("Statistiques du relevé").AppendLine()
            .AppendLine($"Observation : {data.ExtractedAt:O}").AppendLine($"Périmètre : {data.Scope}")
            .AppendLine($"Lignes : {data.Rows.Count:N0} · état complet confirmé : {(data.Complete ? "oui" : "non")}").AppendLine();
        foreach (var metric in snapshot.Metrics)
            body.AppendLine(metric.Label + " : " + (metric.Value?.ToString("0.##", culture) ?? "Non disponible") + " " + metric.Unit);
        var payload = JsonSerializer.SerializeToElement(new
        {
            schema = "glpi-pilot.team-statistics", format = 1, snapshot.CalculatedAt, snapshot.ExtractedAt,
            source = AppPaths.SafeUrl(data.SourceUrl), data.Scope, snapshot.RowCount, snapshot.ExpectedTotal, snapshot.Complete,
            metrics = snapshot.Metrics.Select(metric => new { metric.Key, metric.Label, metric.Value, metric.Unit, metric.Explanation, metric.Sample, metric.Tone }),
            distributions = snapshot.Distributions.Select(distribution => new
            {
                distribution.Key, distribution.Title, distribution.Available, distribution.Denominator, distribution.Note,
                buckets = distribution.Buckets.Select(bucket => new { bucket.Label, bucket.Count, bucket.Value, bucket.Unit, bucket.Sample, bucket.IsAggregate })
            })
        }, Json);
        var item = new TeamWorkspaceItem { Id = Guid.NewGuid().ToString("N"), Kind = "statistics", Title = Text(args, "title", 300, "Statistiques du relevé").Trim(), Body = body.ToString(), Author = LibrarySettings.DisplayName, Status = "published", Payload = payload };
        var saved = await TeamWorkspace.SaveAsync(item, null, ct).ConfigureAwait(false); Publish("library.changed", new { team = true }); return saved;
    }
}
