using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class CampaignSourceResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "En attente";
    public int Rows { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Detail { get; set; } = "";
    public string Path { get; set; } = "";
}

public static class CampaignReport
{
    public static string Save(string folder, IReadOnlyList<CampaignSourceResult> sources, DateTimeOffset started)
    {
        Directory.CreateDirectory(folder);
        var stem = System.IO.Path.Combine(folder, "Campagne_" + started.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        var temp = stem + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(new { StartedAt = started, FinishedAt = DateTimeOffset.Now, Sources = sources }, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, stem + ".json"); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        var data = new ExtractionResult { Headers = new() { "Source", "État", "Lignes", "Début", "Fin", "Détail", "Fichier" }, Title = "Bilan de campagne", Complete = false };
        data.Rows = sources.Select(s => new[] { s.Name, s.State, s.Rows.ToString(), s.StartedAt?.ToString("O") ?? "", s.FinishedAt?.ToString("O") ?? "", s.Detail, s.Path }).ToList();
        ExportService.WriteCsv(data, stem + ".csv"); return stem + ".json";
    }
}
