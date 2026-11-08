using System.Globalization;
using System.Net;
using System.Text;

namespace GlpiPilot.Core;

public static partial class ReportService
{
    private static ExportResult WritePresentationHtml(DashboardMeetingReport report, string path, CancellationToken ct)
    {
        var deck = report.Presentation!; deck.Validate(report.Sections.Select(section => section.Id));
        var sections = report.Sections.ToDictionary(section => section.Id, StringComparer.Ordinal);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; var rows = 0; long written = 0;
        static string H(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.CurrentCulture) ?? "");
        static string P(int value) => (value / 10d).ToString("0.#", CultureInfo.InvariantCulture) + "%";
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
                void Chart(DashboardReportSection section)
                {
                    var points = section.Chart.Take(12).ToArray();
                    if (points.Length == 0) return;
                    var maximum = Math.Max(1, points.Where(point => point.Value.HasValue && double.IsFinite(point.Value.Value)).Select(point => Math.Abs(point.Value!.Value)).DefaultIfEmpty().Max());
                    W("<svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"" + H(section.Title) + "\" viewBox=\"0 0 1000 " + (points.Length * 34 + 12) + "\">");
                    for (var index = 0; index < points.Length; index++)
                    {
                        var point = points[index]; var label = point.Label.Length > 52 ? point.Label[..52] + "…" : point.Label;
                        W("<text x=\"0\" y=\"" + (index * 34 + 23) + "\" font-size=\"18\">" + H(label) + "</text>");
                        if (point.Value.HasValue && double.IsFinite(point.Value.Value))
                            W("<rect x=\"470\" y=\"" + (index * 34 + 7) + "\" width=\"" + (380 * Math.Abs(point.Value.Value) / maximum).ToString("0.###", CultureInfo.InvariantCulture) + "\" height=\"21\" rx=\"4\" fill=\"#2765a8\"/>");
                        W("<text x=\"880\" y=\"" + (index * 34 + 23) + "\" font-size=\"18\">" + H(point.Value?.ToString("0.###", CultureInfo.CurrentCulture) ?? "Indisponible") + "</text>");
                    }
                    W("</svg>");
                }
                void Table(DashboardReportTable table, bool preview)
                {
                    W("<h3>" + H(table.Title) + " · " + table.Rows.Count + " lignes</h3><table><thead><tr>");
                    foreach (var header in table.Headers) W("<th>" + H(header) + "</th>");
                    W("</tr></thead><tbody>");
                    foreach (var row in preview ? table.Rows.Take(5) : table.Rows)
                    {
                        W("<tr>"); foreach (var cell in row) W("<td>" + H(cell) + "</td>"); W("</tr>");
                        if (!preview) rows++;
                    }
                    W("</tbody></table>");
                }

                W("<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'\"><title>" + H(report.Title) + "</title><style>"
                    + "*{box-sizing:border-box}body{font:14px system-ui,sans-serif;color:#16283e;background:#edf2f7;margin:0}main{padding:24px}h1{font-size:28px}h2,h3{margin:0 0 8px}p{line-height:1.4;overflow-wrap:anywhere}a{color:#164d8d}.intro,.context,.report-section{max-width:1232px;margin:20px auto;background:white;padding:24px;border:1px solid #cad5e3;border-radius:12px}.deck{overflow-x:auto}.presentation-slide{position:relative;width:1280px;height:720px;margin:24px auto;background:#f7f9fd;border:1px solid #cbd7e5;break-inside:avoid;overflow:hidden}.slide-heading{position:absolute;left:24px;top:20px;right:24px;height:56px}.slide-heading h2{font-size:25px;margin:0 0 6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.slide-heading p{margin:0;font-size:12px;color:#52647d}.slide-surface{position:absolute;left:1.875%;top:12.222222%;width:96.25%;height:81.111111%}.slide-widget{position:absolute;padding:14px;border:1px solid #cbd7e5;border-radius:12px;background:white;overflow:auto}.slide-widget h3{font-size:17px;line-height:1.25}.slide-widget .scope{font-size:11px;margin:6px 0;color:#52647d}.slide-widget .metric{font-size:38px;font-weight:700;margin:10px 0}.metric small{font-size:16px;font-weight:400}.slide-widget svg{width:100%;max-height:260px;display:block}.slide-widget details{font-size:11px;margin:8px 0}.slide-widget table{font-size:10px}.slide-widget .details-link{display:block;font-size:11px}.slide-footer{position:absolute;left:24px;right:24px;bottom:14px;display:flex;justify-content:space-between;font-size:11px;color:#52647d}.report-section svg{width:100%;max-height:480px}.scope,.muted{color:#52647d}.report-section .metric{font-size:36px;font-weight:700}table{width:100%;border-collapse:collapse;table-layout:fixed;margin:10px 0 20px}th,td{border:1px solid #cad5e3;text-align:left;vertical-align:top;padding:7px;overflow-wrap:anywhere;white-space:pre-wrap}th{background:#edf2f8}thead{display:table-header-group}.context th{width:28%}@page{size:A4 landscape;margin:12mm}@page slides{size:320mm 180mm;margin:0}@media print{body{background:white}main{padding:0}.intro{display:none}.deck{overflow:visible}.presentation-slide{page:slides;width:320mm;height:180mm;margin:0;border:0;break-after:page}.slide-heading{left:6mm;right:6mm;top:5mm}.slide-footer{left:6mm;right:6mm;bottom:3.5mm}.slide-widget{overflow:hidden;border-radius:2mm}.slide-widget details,.slide-widget .details-link{display:none}.context,.report-section{max-width:none;border:0;border-radius:0;padding:0;margin:12px 0}tr{break-inside:avoid}h1,h2,h3{break-after:avoid}.report-section svg{max-height:100mm}}"
                    + "</style></head><body><main><header class=\"intro\"><h1>" + H(report.Title) + "</h1><p>" + deck.Slides.Count + " diapositives composées · données figées à l’export. Les <a href=\"#details\">tableaux complets, filtres et réserves</a> suivent la présentation.</p></header><div class=\"deck\">");
                for (var index = 0; index < deck.Slides.Count; index++)
                {
                    var slide = deck.Slides[index];
                    W("<section class=\"presentation-slide\" data-slide-id=\"" + H(slide.Id) + "\"><header class=\"slide-heading\"><h2>" + H(slide.Title.Length == 0 ? report.Title : slide.Title) + "</h2><p>" + H(report.Title) + " · Relevé du " + H(report.ExtractedAt.ToString("dd/MM/yyyy HH:mm zzz", CultureInfo.InvariantCulture)) + "</p></header><div class=\"slide-surface\">");
                    foreach (var item in slide.Items)
                    {
                        var section = sections[item.WidgetId];
                        W("<article class=\"slide-widget\" data-widget-id=\"" + H(item.WidgetId) + "\" data-placement-id=\"" + H(item.Id) + "\" style=\"left:" + P(item.X) + ";top:" + P(item.Y) + ";width:" + P(item.Width) + ";height:" + P(item.Height) + "\"><h3>" + H(section.Title) + "</h3><p class=\"scope\">" + H(section.Scope) + "</p>");
                        if (section.MetricValue is not null) W("<p class=\"metric\">" + H(section.MetricValue) + " <small>" + H(section.Unit) + "</small></p>");
                        Chart(section);
                        if (section.Chart.Count == 0 && section.MetricValue is null && section.Tables.FirstOrDefault() is { } firstTable) Table(firstTable, true);
                        if (section.Notes.Count > 0)
                        {
                            W("<details><summary>Contexte et réserves · " + section.Notes.Count + "</summary>");
                            foreach (var note in section.Notes) W("<p>" + H(note) + "</p>");
                            W("</details>");
                        }
                        W("<a class=\"details-link\" href=\"#widget-" + H(item.WidgetId) + "\">Filtres, réserves et données complètes</a></article>");
                    }
                    W("</div><footer class=\"slide-footer\"><span>GLPI Pilot · " + H(report.Context.FirstOrDefault(pair => pair.Key == "Source de données").Value)
                        + (report.Warnings.Count > 0 ? " · " + report.Warnings.Count + " réserve(s) — détails dans l’annexe" : "")
                        + "</span><span>" + (index + 1) + " / " + deck.Slides.Count + "</span></footer></section>");
                }
                W("</div><section class=\"context\" id=\"details\"><h2>Source, périmètre et fraîcheur</h2><table><tbody>");
                foreach (var pair in report.Context) W("<tr><th>" + H(pair.Key) + "</th><td>" + H(pair.Value) + "</td></tr>");
                W("</tbody></table></section>");
                foreach (var section in report.Sections)
                {
                    W("<section class=\"report-section\" id=\"widget-" + H(section.Id) + "\" data-widget-details=\"" + H(section.Id) + "\"><h2>" + H(section.Title) + "</h2><p class=\"scope\">Périmètre : " + H(section.Scope) + "<br>Filtres : " + H(section.Filters) + "</p>");
                    if (section.MetricValue is not null) W("<p class=\"metric\">" + H(section.MetricValue) + " <small>" + H(section.Unit) + "</small></p>");
                    foreach (var note in section.Notes) W("<p>" + H(note) + "</p>");
                    Chart(section); foreach (var table in section.Tables) Table(table, false);
                    W("</section>");
                }
                W("<p class=\"muted\">Fin du rapport · document autonome hors ligne, sans script ni ressource distante.</p></main></body></html>");
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
            return new(path, rows, 0, 0);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
