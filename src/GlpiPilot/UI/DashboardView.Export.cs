using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlpiPilot.Core;
using Microsoft.Win32;

namespace GlpiPilot.UI;

public sealed partial class DashboardView
{
    public string ExportCriteria()
    {
        var lines = new List<string> { "Disposition : " + Board.Name, "Périmètre : " + (_data?.ScopeLabel ?? "indisponible"),
            "Filtres communs : " + (Board.Filters.Count + _crossFilters.Count == 0 ? "aucun" : DescribeFilters(Board.Filters.Concat(_crossFilters))) };
        if (Board.ComparisonEnabled) lines.Add("Comparaison B : " + (Board.ComparisonFilters.Count == 0 ? "périmètre de base" : DescribeFilters(Board.ComparisonFilters)));
        foreach (var widget in Board.Widgets.Where(w => !w.Hidden && w.Filters.Count > 0)) lines.Add("Widget « " + WidgetTitle(widget) + " » : " + DescribeFilters(widget.Filters));
        return string.Join("\n", lines);
    }
    public string ExportCaption()
    {
        if (_data is null) throw new InvalidOperationException("Aucun dashboard calculé à exporter.");
        return "GLPI Pilot · " + Board.Name + "\nExtraction : " + _data.ExtractedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz")
            + " · calcul : " + _data.CalculatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz")
            + "\n" + _data.RowCount.ToString("N0") + " lignes du périmètre / " + _data.SourceRowCount.ToString("N0") + " récupérées · total annoncé : " + (_data.SourceExpectedTotal?.ToString("N0") ?? "inconnu")
            + " · complétude : " + (_data.Complete ? "source complète confirmée" : "partielle ou non confirmée")
            + "\nRecherche : " + _data.Scope + "\n" + ExportCriteria();
    }
    private void ExportDashboardVisual()
    {
        try
        {
            if (_data == null || _busy || _scopeCalculating) return;
            var dialog = new SaveFileDialog { Filter = "Image PNG (*.png)|*.png|Rapport visuel PDF (*.pdf)|*.pdf", DefaultExt = ".png", AddExtension = true, FileName = "GLPI-Dashboard-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            ExportVisualTo(dialog.FileName, dialog.FilterIndex == 2 || Path.GetExtension(dialog.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) { _analysisMessage = "Export visuel : " + error.Message; Render(); }
    }
    /// <summary>Exports the currently rendered board after the caller has explicitly chosen its target.</summary>
    public void ExportVisualTo(string fileName, bool pdf)
    {
            if (_data is null || _busy || _scopeCalculating) throw new InvalidOperationException("Attendez un dashboard calculé avant de l’exporter.");
            var editing = _editing; _editing = false; Render(); UpdateLayout();
            try
            {
                var width = Math.Clamp(_body.ActualWidth, 480, 3000); var bodyHeight = Math.Max(1, _body.ActualHeight);
                var caption = ExportCaption();
                var title = new FormattedText(caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.Black, 1) { MaxTextWidth = width - 40 };
                var headerHeight = Math.Ceiling(title.Height) + 40; var totalHeight = headerHeight + bodyHeight + 24;
                RenderTargetBitmap RenderPart(double offset, double virtualHeight, double scale, int number, int count)
                {
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(virtualHeight * scale), 96, 96, PixelFormats.Pbgra32);
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                    {
                        drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width * scale, virtualHeight * scale));
                        drawing.PushTransform(new ScaleTransform(scale, scale)); drawing.PushClip(new RectangleGeometry(new Rect(0, 0, width, virtualHeight - (count > 1 ? 28 : 0))));
                        drawing.PushTransform(new TranslateTransform(0, -offset)); drawing.DrawText(title, new Point(20, 18));
                        drawing.DrawRectangle(new VisualBrush(_body) { Stretch = Stretch.Fill }, null, new Rect(0, headerHeight, width, bodyHeight)); drawing.Pop(); drawing.Pop();
                        if (count > 1)
                        {
                            var footer = new FormattedText($"GLPI Pilot · page {number + 1}/{count} · extraction {_data.ExtractedAt:dd/MM/yyyy HH:mm} · " + (_data.Complete ? "source complète" : "source partielle"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.Black, 1);
                            drawing.DrawText(footer, new Point(20, virtualHeight - 21));
                        }
                        drawing.Pop();
                    }
                    bitmap.Render(visual); return bitmap;
                }
                if (pdf)
                {
                    var pageHeight = width * 841.89 / 595.28; var contentHeight = pageHeight - 28; var count = (int)Math.Ceiling(totalHeight / contentHeight);
                    PdfReportWriter.Write(fileName, count, page =>
                    {
                        var bitmap = RenderPart(page * contentHeight, pageHeight, Math.Min(1.5, 1800 / width), page, count);
                        var encoder = new JpegBitmapEncoder { QualityLevel = 90 }; encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Rgb24, null, 0)));
                        using var memory = new MemoryStream(); encoder.Save(memory); return new(memory.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
                    });
                    _analysisMessage = $"PDF natif enregistré : {count} page(s), avec cartouche et images du dashboard. " + fileName;
                }
                else
                {
                    var scale = Math.Min(1.5, 8192 / Math.Max(width, totalHeight)); var bitmap = RenderPart(0, totalHeight, scale, 0, 1);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var temporary = fileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) encoder.Save(stream); File.Move(temporary, fileName, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    _analysisMessage = "PNG avec cartouche enregistré : " + fileName + (scale < 1 ? " · dimensions adaptées à 8 192 pixels" : "");
                }
            }
            finally { _editing = editing; Render(); }
    }
}
