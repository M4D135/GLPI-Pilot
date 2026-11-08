using System.Globalization;
using System.Net;
using System.Text;

namespace GlpiPilot.Core;

/// <summary>A frozen, explicitly selected report. No source rows or unselected analyses are appended implicitly.</summary>
public sealed record DashboardMeetingReport(string Title, DateTimeOffset GeneratedAt, DateTimeOffset ExtractedAt,
    IReadOnlyList<KeyValuePair<string, string>> Context, IReadOnlyList<DashboardReportSection> Sections)
{
    public DashboardPresentationSettings? Presentation { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
public sealed class DashboardReportSection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Kind { get; init; }
    public required string Scope { get; init; }
    public required string Filters { get; init; }
    public string? MetricValue { get; set; }
    public string Unit { get; set; } = "";
    public List<string> Notes { get; } = new();
    public List<DashboardReportTable> Tables { get; } = new();
    public List<DashboardReportPoint> Chart { get; } = new();
}
public sealed record DashboardReportTable(string Title, IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows);
public sealed record DashboardReportPoint(string Label, double? Value);

public static partial class ReportService
{
    // Wide rows preserve a complete table as a table, including its own headers. The common
    // columns identify each block and its exact scope even after a spreadsheet sort/filter.
    public static ExtractionResult ToExport(DashboardMeetingReport report, CancellationToken ct = default)
    {
        var width = Math.Max(2, report.Sections.SelectMany(section => section.Tables).Select(table => table.Headers.Count).DefaultIfEmpty().Max());
        var data = new ExtractionResult { Title = report.Title, ExtractedAt = report.GeneratedAt,
            Scope = "Sélection de réunion — ordre et filtres indiqués dans chaque bloc", Complete = false,
            Headers = new[] { "Ordre", "Bloc", "Périmètre", "Filtres", "Tableau", "Type" }
                .Concat(Enumerable.Range(1, width).Select(index => "Colonne " + index)).ToList() };
        void Add(int order, string title, string scope, string filters, string table, string type, params string[] values)
        {
            ct.ThrowIfCancellationRequested();
            data.Rows.Add(new[] { order.ToString(CultureInfo.InvariantCulture), title, scope, filters, table, type }.Concat(values).ToArray());
        }
        foreach (var pair in report.Context) Add(0, "Contexte du rapport", "", "", "Provenance", "Contexte", pair.Key, pair.Value);
        for (var index = 0; index < report.Sections.Count; index++)
        {
            var section = report.Sections[index];
            Add(index + 1, section.Title, section.Scope, section.Filters, "", "Bloc", section.Title);
            if (section.MetricValue is not null) Add(index + 1, section.Title, section.Scope, section.Filters, "", "Indicateur", section.MetricValue, section.Unit);
            foreach (var note in section.Notes) Add(index + 1, section.Title, section.Scope, section.Filters, "", "Note", note);
            foreach (var table in section.Tables)
            {
                Add(index + 1, section.Title, section.Scope, section.Filters, table.Title, "En-têtes", table.Headers.ToArray());
                foreach (var row in table.Rows) Add(index + 1, section.Title, section.Scope, section.Filters, table.Title, "Données", row);
                if (table.Rows.Count == 0) Add(index + 1, section.Title, section.Scope, section.Filters, table.Title, "Information", "Aucune ligne dans ce périmètre.");
            }
        }
        return data;
    }

    public static ExportResult WriteHtml(DashboardMeetingReport report, string path, CancellationToken ct = default)
    {
        if (report.Presentation is not null) return WritePresentationHtml(report, path, ct);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var rows = 0; long written = 0;
        string H(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.CurrentCulture) ?? "");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                void W(string value)
                {
                    ct.ThrowIfCancellationRequested(); written += Encoding.UTF8.GetByteCount(value) + 1;
                    if (written > MaximumBytes) throw new InvalidDataException("Le rapport dépasserait 256 Mio. Réduisez la sélection ou exportez ses tableaux en CSV.");
                    writer.WriteLine(value);
                }
                W("<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'\"><title>" + H(report.Title) + "</title><style>"
                    + "*{box-sizing:border-box}body{font:14px system-ui,sans-serif;color:#16283e;background:#edf2f7;margin:0}main{max-width:1200px;margin:auto;padding:32px}h1{font-size:30px}h2{margin:0 0 12px;color:#164d8d}h3{margin:22px 0 8px;font-size:16px}p{line-height:1.55;white-space:pre-wrap;overflow-wrap:anywhere}.context,.report-section{background:white;border:1px solid #cbd7e5;border-radius:10px;padding:24px;margin:20px 0}.scope,.muted{color:#4b5c70}.metric{font-size:42px;font-weight:700;margin:16px 0}.metric small{font-size:18px;font-weight:400}table{width:100%;border-collapse:collapse;table-layout:fixed;margin:12px 0 24px}th,td{border:1px solid #cad5e3;text-align:left;vertical-align:top;padding:8px;overflow-wrap:anywhere;white-space:pre-wrap}th{background:#e9eff8}thead{display:table-header-group}svg{display:block;width:100%;height:auto;max-height:480px}.chart{break-inside:avoid}.context th{width:28%}@page{size:A4 landscape;margin:12mm}@media print{body{background:white;font-size:10pt}main{padding:0;max-width:none}.context,.report-section{border-radius:0;padding:12px;margin:12px 0;break-inside:auto}h1,h2,h3{break-after:avoid}th,td{font-size:8pt;padding:5px}tr{break-inside:avoid}svg{max-height:115mm}.metric{font-size:30pt}.report-table{break-inside:auto}}"
                    + "</style></head><body><main><h1>" + H(report.Title) + "</h1><p class=\"muted\">Sélection de réunion · GLPI Pilot · aucun contenu des blocs non sélectionnés n’est ajouté.</p><section class=\"context\"><h2>Source, périmètre et fraîcheur</h2><table><tbody>");
                foreach (var pair in report.Context) W("<tr><th>" + H(pair.Key) + "</th><td>" + H(pair.Value) + "</td></tr>");
                W("</tbody></table></section>");
                foreach (var section in report.Sections)
                {
                    W("<section class=\"report-section\" data-widget-id=\"" + H(section.Id) + "\"><h2>" + H(section.Title) + "</h2><p class=\"scope\">Périmètre : " + H(section.Scope) + "<br>Filtres : " + H(section.Filters) + "</p>");
                    if (section.MetricValue is not null) W("<p class=\"metric\">" + H(section.MetricValue) + " <small>" + H(section.Unit) + "</small></p>");
                    foreach (var note in section.Notes) W("<p>" + H(note) + "</p>");
                    if (section.Chart.Count > 0)
                    {
                        var points = section.Chart.Take(12).ToArray();
                        var maximum = Math.Max(1, points.Where(point => point.Value.HasValue && double.IsFinite(point.Value.Value)).Select(point => Math.Abs(point.Value!.Value)).DefaultIfEmpty().Max());
                        W("<div class=\"chart\"><svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"" + H(section.Title) + "\" viewBox=\"0 0 1000 " + (points.Length * 34 + 12) + "\">");
                        for (var index = 0; index < points.Length; index++)
                        {
                            var point = points[index]; var label = point.Label.Length > 52 ? point.Label[..52] + "…" : point.Label;
                            W("<text x=\"0\" y=\"" + (index * 34 + 23) + "\" font-size=\"13\">" + H(label) + "</text>");
                            if (point.Value.HasValue && double.IsFinite(point.Value.Value)) W("<rect x=\"370\" y=\"" + (index * 34 + 7) + "\" width=\"" + (500 * Math.Abs(point.Value.Value) / maximum).ToString("0.###", CultureInfo.InvariantCulture) + "\" height=\"21\" fill=\"#2765a8\"/>");
                            W("<text x=\"885\" y=\"" + (index * 34 + 23) + "\" font-size=\"13\">" + H(point.Value?.ToString("0.###", CultureInfo.CurrentCulture) ?? "Indisponible") + "</text>");
                        }
                        W("</svg><p class=\"muted\">Aperçu des " + points.Length + " premières valeurs ; longueur des barres en valeur absolue, signe conservé. Les tableaux ci-dessous contiennent toutes les lignes de ce bloc.</p></div>");
                    }
                    foreach (var table in section.Tables)
                    {
                        W("<div class=\"report-table\"><h3>" + H(table.Title) + " · " + table.Rows.Count + " lignes</h3><table><thead><tr>");
                        foreach (var header in table.Headers) W("<th>" + H(header) + "</th>");
                        W("</tr></thead><tbody>");
                        foreach (var row in table.Rows)
                        {
                            W("<tr>"); foreach (var cell in row) W("<td>" + H(cell) + "</td>"); W("</tr>"); rows++;
                        }
                        W("</tbody></table></div>");
                    }
                    W("</section>");
                }
                W("<p class=\"muted\">Fin du rapport · données figées à l’export · document autonome hors ligne, sans script ni ressource distante.</p></main></body></html>");
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
            return new ExportResult(path, rows, 0, 0);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
