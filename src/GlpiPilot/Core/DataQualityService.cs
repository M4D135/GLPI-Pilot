using System.Globalization;

namespace GlpiPilot.Core;

public sealed record ColumnQuality(string Header, string Role, int Present, int Empty, int Invalid, int Distinct,
    bool DistinctLimited, string Example, int Total)
{
    public string Coverage => Total == 0 ? "—" : $"{100.0 * Present / Total:F1} %";
    public string Diversity => DistinctLimited ? $"≥ {Distinct:N0}" : Distinct.ToString("N0");
}
public sealed record QualityIssue(string Kind, string Field, int Row, string TicketId, string Detail);
public sealed class DataQualityReport
{
    public string Completeness { get; set; } = "";
    public string Coverage { get; set; } = "";
    public string Interpretation { get; set; } = "";
    public List<ColumnQuality> Columns { get; } = new();
    public List<QualityIssue> Issues { get; } = new();
    public int IssueCount { get; set; }
    public List<string> UnavailableRoles { get; } = new();
    public string Summary => $"{Completeness}\n{Coverage}\n{Interpretation}";
}

public static class DataQualityService
{
    private static readonly HashSet<string> DateRoles = new(StringComparer.Ordinal) { "created", "updated", "due", "resolved", "closed" };
    public static DataQualityReport Analyze(ExtractionResult data, DashboardSettings settings, CancellationToken ct = default)
    {
        var dashboard = DashboardService.Calculate(data, settings, data.ExtractedAt, ct);
        var report = new DataQualityReport
        {
            Completeness = data.Complete && (!data.ExpectedTotal.HasValue || data.ExpectedTotal == data.Rows.Count) ? (data.ExpectedTotal.HasValue ? $"Complétude : {data.Rows.Count:N0} / {data.ExpectedTotal:N0} lignes, extraction confirmée." : $"Complétude déclarée ; total indépendant indisponible ({data.Rows.Count:N0} lignes).")
                : $"Complétude à vérifier · {data.Rows.Count:N0} lignes chargées, total attendu {(data.ExpectedTotal?.ToString("N0") ?? "inconnu")}.",
            Coverage = $"Couverture : {dashboard.Columns.Count(column => column.Index.HasValue)} / {dashboard.Columns.Count} rôles reconnus."
        };
        report.UnavailableRoles.AddRange(dashboard.Columns.Where(column => !column.Index.HasValue).Select(column => column.Label + " : " + column.State));
        int Index(string role) => dashboard.Columns.FirstOrDefault(column => column.Role == role)?.Index ?? -1;
        string Cell(int row, int column) => column >= 0 && column < data.Rows[row].Length ? data.Rows[row][column] ?? "" : "";
        void Issue(string kind, string field, int row, string detail)
        {
            report.IssueCount++;
            if (report.Issues.Count < 10000) report.Issues.Add(new(kind, field, row, Cell(row, Index("id")), detail));
        }
        var invalidTotal = 0;
        for (int column = 0; column < data.Headers.Count; column++)
        {
            ct.ThrowIfCancellationRequested();
            var role = dashboard.Columns.FirstOrDefault(candidate => candidate.Index == column)?.Role ?? "";
            var distinct = new HashSet<string>(StringComparer.Ordinal); int present = 0, invalid = 0; bool limited = false; string example = "";
            for (int row = 0; row < data.Rows.Count; row++)
            {
                if ((row & 1023) == 0) ct.ThrowIfCancellationRequested();
                var text = Cell(row, column);
                if (string.IsNullOrWhiteSpace(text)) continue;
                present++; if (example.Length == 0) example = text.Length > 100 ? text[..100] + "…" : text;
                if (distinct.Count < 5000) distinct.Add(text); else if (!distinct.Contains(text)) limited = true;
                var bad = DateRoles.Contains(role) ? !DashboardService.TryDate(text, settings.DateCulture, data.ExtractedAt.Offset, out _)
                    : role == "status" ? DashboardService.Status(text) == 0
                    : role == "id" && CanonicalId(text).Length == 0;
                if (bad) { invalid++; Issue("Valeur non interprétable", data.Headers[column], row, "La valeur source est conservée ; elle ne participe pas au calcul correspondant."); }
            }
            invalidTotal += invalid;
            report.Columns.Add(new(data.Headers[column], role, present, data.Rows.Count - present, invalid, distinct.Count, limited, example, data.Rows.Count));
        }
        if (Index("id") >= 0)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int row = 0; row < data.Rows.Count; row++)
            {
                var id = CanonicalId(Cell(row, Index("id"))); if (id.Length == 0) continue;
                if (seen.TryGetValue(id, out var previous)) Issue("Identifiant répété", data.Headers[Index("id")], row,
                    data.Rows[row].SequenceEqual(data.Rows[previous]) ? $"Ligne identique à la ligne {previous + 1:N0}." : $"Même identifiant que la ligne {previous + 1:N0}, avec des valeurs différentes.");
                else seen[id] = row;
            }
        }
        for (int row = 0; row < data.Rows.Count; row++)
        {
            if ((row & 1023) == 0) ct.ThrowIfCancellationRequested();
            bool Date(string role, out DateTime value) => DashboardService.TryDate(Cell(row, Index(role)), settings.DateCulture, data.ExtractedAt.Offset, out value);
            if (Date("created", out var created))
            {
                foreach (var role in new[] { "updated", "resolved", "closed", "due" })
                    if (Date(role, out var date) && date < created) Issue("Dates incohérentes", role, row, "Cette date précède la création du ticket.");
                if (created > data.ExtractedAt.DateTime.AddMinutes(5)) Issue("Date future", "created", row, "La création se situe après la date du relevé.");
            }
            if (Date("resolved", out var resolved) && Date("closed", out var closed) && closed < resolved)
                Issue("Dates incohérentes", "closed", row, "La clôture précède la résolution indiquée.");
        }
        report.Interpretation = $"Interprétation : {invalidTotal:N0} valeurs non interprétables ; {report.IssueCount:N0} points à examiner. Aucune cellule source n’a été corrigée automatiquement.";
        return report;
    }
    private static string CanonicalId(string value)
    {
        try { return TicketReferenceResolver.NormalizeId(value); }
        catch (InvalidOperationException) { return ""; }
    }

}
