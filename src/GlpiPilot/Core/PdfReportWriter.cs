using System.Globalization;
using System.IO;
using System.Text;

namespace GlpiPilot.Core;

public sealed record PdfRasterPage(byte[] Jpeg, int PixelWidth, int PixelHeight);

/// <summary>A bounded PDF 1.4 writer for locally rendered RGB JPEG pages; no printer, COM, browser or extra package.</summary>
public static class PdfReportWriter
{
    public static void Write(string path, int pageCount, Func<int, PdfRasterPage> renderPage, CancellationToken ct = default)
    {
        if (pageCount is < 1 or > 200) throw new InvalidDataException("L’export PDF accepte de 1 à 200 pages.");
        ArgumentNullException.ThrowIfNull(renderPage);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var offsets = new long[3 + pageCount * 3];
                void Text(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
                void Object(int id, string value) { offsets[id] = stream.Position; Text(id + " 0 obj\n" + value + "\nendobj\n"); }
                Text("%PDF-1.4\n%GLPI-Pilot locally rendered report\n");
                Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
                Object(2, "<< /Type /Pages /Count " + pageCount + " /Kids [" + string.Join(" ", Enumerable.Range(0, pageCount).Select(i => (3 + i * 3) + " 0 R")) + "] >>");
                for (var pageNumber = 0; pageNumber < pageCount; pageNumber++)
                {
                    ct.ThrowIfCancellationRequested(); var page = renderPage(pageNumber);
                    if (page.PixelWidth is < 1 or > 12000 || page.PixelHeight is < 1 or > 18000 || page.Jpeg is not { Length: >= 4 and <= 16000000 }
                        || page.Jpeg[0] != 0xff || page.Jpeg[1] != 0xd8 || page.Jpeg[^2] != 0xff || page.Jpeg[^1] != 0xd9)
                        throw new InvalidDataException("Une page PDF doit contenir une image JPEG RGB valide dans les limites autorisées.");
                    if (stream.Position + page.Jpeg.Length > 256L * 1024 * 1024) throw new InvalidDataException("Le rapport PDF dépasserait 256 Mio.");
                    var id = 3 + pageNumber * 3;
                    Object(id, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources << /XObject << /Im0 " + (id + 1) + " 0 R >> >> /Contents " + (id + 2) + " 0 R >>");
                    offsets[id + 1] = stream.Position;
                    Text((id + 1) + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + page.PixelWidth + " /Height " + page.PixelHeight + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + page.Jpeg.Length + " >>\nstream\n");
                    stream.Write(page.Jpeg); Text("\nendstream\nendobj\n");
                    const string commands = "q\n595.28 0 0 841.89 0 0 cm\n/Im0 Do\nQ\n";
                    Object(id + 2, "<< /Length " + Encoding.ASCII.GetByteCount(commands) + " >>\nstream\n" + commands + "endstream");
                }
                var xref = stream.Position; Text("xref\n0 " + offsets.Length + "\n0000000000 65535 f \n");
                foreach (var offset in offsets.Skip(1)) Text(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
                Text("trailer\n<< /Size " + offsets.Length + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
                stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
