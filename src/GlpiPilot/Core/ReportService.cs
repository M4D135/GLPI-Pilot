using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace GlpiPilot.Core;

/// <summary>One self-contained, printable report. Text is encoded, graphics are inline SVG and tickets retain their raw cells.</summary>
public static class ReportService
{
    public const long MaximumBytes = 256L * 1024 * 1024;
    public static ExportResult WriteHtml(DashboardSnapshot snapshot, ExtractionResult source, string criteria, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(source);
        var rows = snapshot.ScopeRows.Distinct().Order().ToArray();
        if (rows.Any(i => i < 0 || i >= source.Rows.Count) || rows.Length != snapshot.RowCount)
            throw new InvalidDataException("Le périmètre du rapport ne correspond plus au relevé source.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; long written = 0;
        string H(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.CurrentCulture) ?? "");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                void W(string html)
                {
                    ct.ThrowIfCancellationRequested(); written += Encoding.UTF8.GetByteCount(html) + 1;
                    if (written > MaximumBytes) throw new InvalidDataException("Le rapport complet dépasserait 256 Mio ; exportez les tickets en CSV et réduisez le périmètre du rapport.");
                    writer.WriteLine(html);
                }
                W("<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'\"><title>GLPI Pilot — Rapport complet</title><style>body{font:14px system-ui,sans-serif;color:#122238;background:#f3f6fa;margin:0}main{max-width:1300px;margin:auto;padding:36px}h1{font-size:30px}h2{margin-top:36px;border-bottom:2px solid #3164c6;padding-bottom:10px}h3{margin-top:8px}p,pre{line-height:1.55}pre{white-space:pre-wrap;font:inherit;background:white;padding:16px;border:1px solid #d5ddea}table{border-collapse:collapse;width:100%;background:white;margin:10px 0 24px}th,td{text-align:left;vertical-align:top;padding:9px;border:1px solid #d5ddea;overflow-wrap:anywhere}th{background:#e7eef9}thead{display:table-header-group}.scroll{overflow-x:auto}.charts{display:grid;grid-template-columns:repeat(auto-fit,minmax(370px,1fr));gap:18px}.chart{background:white;padding:18px;border:1px solid #d5ddea}.muted{color:#52647b}.warning{border-left:5px solid #bd5b11;padding-left:14px}svg{width:100%;height:auto}nav a{margin-right:18px;color:#2359ac}@media print{body{background:white}main{padding:0}h2{break-after:avoid}tr{break-inside:avoid}.charts{display:block}.chart{break-inside:avoid;margin-bottom:15px}nav{display:none}.scroll{overflow:visible}td,th{font-size:10px;padding:4px}details>*{display:block!important}}</style></head><body><main>");
                W("<h1>Rapport complet du relevé GLPI</h1><p class=\"muted\">Produit localement par GLPI Pilot · " + H(DateTimeOffset.Now.ToString("dd/MM/yyyy HH:mm:ss zzz")) + "</p><nav><a href=\"#criteria\">Périmètre</a><a href=\"#metrics\">Indicateurs</a><a href=\"#charts\">Graphiques</a><a href=\"#quality\">Qualité</a><a href=\"#tickets\">Tickets</a></nav>");
                W("<h2 id=\"criteria\">Périmètre, critères et dates</h2><table><tbody>");
                void Pair(string title, object? value) => W("<tr><th>" + H(title) + "</th><td>" + H(value) + "</td></tr>");
                Pair("Extraction", snapshot.ExtractedAt.ToString("dd/MM/yyyy HH:mm:ss zzz")); Pair("Calcul des indicateurs", snapshot.CalculatedAt.ToString("dd/MM/yyyy HH:mm:ss zzz"));
                Pair("Source", AppPaths.SafeUrl(snapshot.SourceUrl)); Pair("Recherche", snapshot.Scope); Pair("Périmètre", snapshot.ScopeLabel);
                Pair("Lignes incluses", rows.Length); Pair("Lignes source récupérées", snapshot.SourceRowCount); Pair("Total annoncé par la source", snapshot.SourceExpectedTotal?.ToString("N0") ?? "Inconnu");
                Pair("Complétude", snapshot.Complete ? "Relevé source complet confirmé" : "Relevé incomplet ou exhaustivité non confirmée");
                Pair("Contexte et recherche vérifiés", source.Provenance is { SearchVerified: true, ScopeVerified: true } ? "Oui" : "Non / non disponible");
                W("</tbody></table><pre>" + H(criteria) + "</pre><p class=\"warning\">Les chiffres concernent les lignes de ce périmètre. Une absence n’est pas une résolution. Les durées calculaires ne sont pas des temps de travail. Un ticket multi-affecté peut contribuer à plusieurs catégories ; leurs totaux ne sont pas nécessairement additionnables.</p>");
                W("<h2 id=\"metrics\">Indicateurs et dénominateurs</h2><table><thead><tr><th>Indicateur</th><th>Valeur</th><th>Unité</th><th>Échantillon</th><th>Méthode et limites</th></tr></thead><tbody>");
                foreach (var metric in snapshot.Metrics) W("<tr><td>" + H(metric.Label) + "</td><td>" + H(metric.Value?.ToString("0.###", CultureInfo.CurrentCulture) ?? "Indisponible") + "</td><td>" + H(metric.Unit) + "</td><td>" + H(metric.Sample?.ToString("N0") ?? "—") + "</td><td>" + H(metric.Explanation) + "</td></tr>");
                W("</tbody></table><h2 id=\"charts\">Graphiques et répartitions</h2><div class=\"charts\">");
                foreach (var distribution in snapshot.Distributions)
                {
                    W("<section class=\"chart\"><h3>" + H(distribution.Title) + "</h3><p class=\"muted\">" + H(distribution.Note) + "</p>");
                    if (!distribution.Available) { W("<p>Indisponible : données nécessaires absentes.</p></section>"); continue; }
                    var buckets = distribution.Buckets.OrderByDescending(b => b.IsAggregate ? b.Value ?? double.NegativeInfinity : b.Count).Take(10).ToArray();
                    var max = buckets.Length == 0 ? 1 : Math.Max(1, buckets.Max(b => b.IsAggregate ? Math.Abs(b.Value ?? 0) : b.Count));
                    W("<svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"" + H(distribution.Title) + "\" viewBox=\"0 0 640 " + Math.Max(40, buckets.Length * 42) + "\">");
                    for (var i = 0; i < buckets.Length; i++)
                    {
                        var bucket = buckets[i]; var value = bucket.IsAggregate ? bucket.Value : bucket.Count;
                        W("<text x=\"0\" y=\"" + (i * 42 + 15) + "\" font-size=\"12\" fill=\"#25364d\">" + H(bucket.Label.Length > 70 ? bucket.Label[..70] + "…" : bucket.Label) + "</text>");
                        if (value.HasValue) W("<rect x=\"0\" y=\"" + (i * 42 + 22) + "\" width=\"" + (500 * Math.Abs(value.Value) / max).ToString("0.##", CultureInfo.InvariantCulture) + "\" height=\"12\" fill=\"#3164c6\"/>");
                        W("<text x=\"510\" y=\"" + (i * 42 + 33) + "\" font-size=\"12\">" + H(value?.ToString("0.###", CultureInfo.CurrentCulture) ?? "—") + "</text>");
                    }
                    W("</svg><p class=\"muted\">Aperçu des 10 premières catégories ; longueur = valeur absolue, signe conservé dans le chiffre. Toutes les catégories ci-dessous.</p><details><summary>Données complètes de cette répartition (" + distribution.Buckets.Count + ")</summary><table><thead><tr><th>Catégorie</th><th>Résultat</th><th>Lignes</th><th>Échantillon</th></tr></thead><tbody>");
                    foreach (var b in distribution.Buckets) W("<tr><td>" + H(b.Label) + "</td><td>" + H(b.IsAggregate ? b.Value?.ToString("0.###") ?? "Indisponible" : b.Count.ToString("N0")) + " " + H(b.Unit) + "</td><td>" + b.Count + "</td><td>" + H(b.Sample?.ToString() ?? distribution.Denominator.ToString()) + "</td></tr>");
                    W("</tbody></table></details></section>");
                }
                W("</div><h2 id=\"quality\">Qualité, correspondances et couverture</h2><ul>");
                foreach (var warning in snapshot.Warnings) W("<li>" + H(warning) + "</li>");
                if (snapshot.Warnings.Count == 0) W("<li>Aucun avertissement de calcul signalé ; cela ne constitue pas une vérification exhaustive de la source.</li>");
                W("</ul><table><thead><tr><th>Rôle</th><th>Colonne</th><th>État</th></tr></thead><tbody>");
                foreach (var column in snapshot.Columns) W("<tr><td>" + H(column.Label) + "</td><td>" + H(column.Header) + "</td><td>" + H(column.State) + "</td></tr>");
                W("</tbody></table><h2 id=\"tickets\">Tickets du périmètre — cellules source</h2><p>" + rows.Length.ToString("N0") + " lignes complètes, dans leur ordre source. Les valeurs ci-dessous restent brutes ; les règles d’interprétation servent aux calculs sans modifier ces cellules.</p><div class=\"scroll\"><table><thead><tr><th>Ligne source</th>");
                foreach (var header in source.Headers) W("<th>" + H(header) + "</th>"); W("</tr></thead><tbody>");
                foreach (var rowIndex in rows)
                {
                    W("<tr><td>" + (rowIndex + 1) + "</td>"); var row = source.Rows[rowIndex];
                    for (var column = 0; column < source.Headers.Count; column++) W("<td>" + H(column < row.Length ? row[column] : "") + "</td>");
                    W("</tr>");
                }
                W("</tbody></table></div><p class=\"muted\">Fin du rapport · aucune ressource distante, aucun script, aucun suivi de consultation.</p></main></body></html>");
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
            return new ExportResult(path, rows.Length, source.Headers.Count, 0);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
