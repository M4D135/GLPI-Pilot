using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace GlpiPilot.Core;

/// <summary>Reads a downloaded native CSV without executing or converting its cell values.</summary>
public static class NativeCsvImportService
{
    private const long MaximumFileBytes = 512L * 1024 * 1024;
    private const int MaximumFieldCharacters = 16 * 1024 * 1024;
    private const int MaximumColumnsWithTerminalField = 16_385;
    private static readonly char[] Delimiters = { ';', ',', '\t' };

    public static ExtractionResult Read(
        string path,
        string sourceUrl,
        int? expectedTotal = null,
        int maxRows = 1_000_000,
        bool allPagesRequested = true,
        CancellationToken cancellationToken = default,
        Action<string>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxRows is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxRows), "La limite doit être comprise entre 1 et 1 000 000 lignes.");
        if (expectedTotal < 0) throw new ArgumentOutOfRangeException(nameof(expectedTotal));
        cancellationToken.ThrowIfCancellationRequested();
        RejectKnownOtherFormat(path);

        // The same read-only handle is retained for detection and parsing, so a replacement
        // of the path during import cannot cause two different files to be interpreted.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.SequentialScan);
        diagnostics?.Invoke($"CSV natif : taille={file.Length.ToString(CultureInfo.InvariantCulture)} octets.");
        if (file.Length == 0) throw Invalid("Le téléchargement est vide. Aucun tableau CSV n’a été reçu.");
        if (file.Length > MaximumFileBytes) throw Invalid("Le fichier dépasse 512 Mio. Réduis la recherche avant de relancer l’export.");
        var encoding = DetectEncoding(file, cancellationToken);
        diagnostics?.Invoke($"CSV natif : encodage={encoding.Name} ; BOM={(encoding.PreambleBytes == 0 ? "absent" : encoding.Name + " (" + encoding.PreambleBytes.ToString(CultureInfo.InvariantCulture) + " octets)")} ; repli Windows-1252={(encoding.Fallback ? "oui" : "non")}.");
        var text = ValidateText(file, encoding, cancellationToken, diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        RejectMarkupOrOtherText(text.Prefix);
        var preamble = DetectPreamble(file, encoding, cancellationToken);
        char delimiter = preamble.Delimiter ?? DetectDelimiter(file, encoding, preamble.SkipLines, cancellationToken);
        string delimiterName = delimiter == ';' ? "point-virgule" : delimiter == ',' ? "virgule" : "tabulation";
        diagnostics?.Invoke($"CSV natif : séparateur={delimiterName} (U+{(int)delimiter:X4}) ; lignes de préambule={preamble.SkipLines.ToString(CultureInfo.InvariantCulture)}.");

        var result = new ExtractionResult
        {
            SourceUrl = SafeSourceUrl(sourceUrl),
            Title = "Export natif GLPI — " + Path.GetFileName(path),
            ExtractedAt = DateTimeOffset.Now,
            Scope = allPagesRequested ? "Export natif GLPI : toutes les pages" : "Export natif GLPI : périmètre non confirmé",
            ExpectedTotal = expectedTotal,
            Pages = 0
        };
        if (encoding.Fallback)
            result.Warnings.Add("Le fichier n’est pas un UTF-8 valide : lecture de repli en Windows-1252. Vérifie les accents dans l’aperçu.");
        if (text.UnusualControls.Count != 0)
            result.Warnings.Add($"Le CSV contient {text.UnusualControlCount:N0} caractère(s) de contrôle inhabituels. Les valeurs et le fichier natif sont conservés ; certains signes peuvent mal s’afficher. Les codes et leurs positions sont disponibles dans le diagnostic d’import.");

        using var reader = OpenReader(file, encoding);
        SkipLines(reader, preamble.SkipLines, cancellationToken);
        string[]? header = null;
        long sourceRows = 0;
        bool lastColumnAlwaysEmpty = true;
        foreach (var record in ReadRecords(reader, delimiter, preamble.SkipLines + 1, cancellationToken))
        {
            if (record.PhysicallyBlank) continue;
            if (header is null)
            {
                header = record.Fields;
                continue;
            }
            if (record.Fields.Length != header.Length)
                throw Invalid($"CSV incohérent à la ligne physique {record.Line}: {record.Fields.Length} cellule(s), {header.Length} attendue(s). Le fichier natif est conservé ; aucune ligne n’a été corrigée ou ignorée.");
            sourceRows++;
            lastColumnAlwaysEmpty &= record.Fields[^1].Length == 0;
            if (result.Rows.Count < maxRows) result.Rows.Add(record.Fields);
        }
        if (header is null) throw Invalid("Le téléchargement ne contient aucun en-tête CSV.");
        int sourceColumns = header.Length;
        bool terminalColumnRemoved = false;

        // GLPI 10 appends a delimiter after every cell. Remove its terminal column
        // only after checking every record, including the records beyond maxRows.
        if (header.Length > 1 && header[^1].Length == 0 && lastColumnAlwaysEmpty)
        {
            terminalColumnRemoved = true;
            header = header[..^1];
            for (int i = 0; i < result.Rows.Count; i++)
            {
                if ((i & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();
                result.Rows[i] = result.Rows[i][..^1];
            }
        }
        if (header.Length > 16_384) throw Invalid("Le CSV dépasse la limite de 16 384 colonnes du fichier Excel.");
        result.Headers = UniqueHeaders(header, out bool renamed, cancellationToken);
        if (renamed) result.Warnings.Add("Les en-têtes vides ou en double ont reçu un nom unique. Les valeurs et les lignes sont conservées.");

        bool capped = sourceRows > result.Rows.Count;
        if (capped)
            result.Warnings.Add($"Limite d’import atteinte : {result.Rows.Count:N0} lignes chargées sur {sourceRows:N0} présentes dans le CSV. Le fichier natif complet est conservé.");
        if (!allPagesRequested)
            result.Warnings.Add("Le choix « Toutes les pages » n’a pas été confirmé ; cet import ne certifie pas l’exhaustivité de la recherche GLPI.");
        if (!expectedTotal.HasValue)
            result.Warnings.Add("Total GLPI inconnu : le CSV a été lu, mais l’exhaustivité de la recherche ne peut pas être confirmée.");
        else if (sourceRows != expectedTotal.Value)
            result.Warnings.Add($"Le CSV contient {sourceRows:N0} lignes pour {expectedTotal.Value:N0} résultats annoncés par GLPI. Vérifie le périmètre de l’export.");

        result.Complete = allPagesRequested && expectedTotal.HasValue && sourceRows == expectedTotal.Value && !capped;
        result.Status = result.Complete
            ? $"Export natif importé : {result.Rows.Count:N0} lignes, total conforme à GLPI"
            : capped
                ? $"Import natif partiel : {result.Rows.Count:N0} / {sourceRows:N0} lignes du fichier"
                : $"Export natif importé : {result.Rows.Count:N0} lignes — exhaustivité à vérifier";
        diagnostics?.Invoke($"CSV natif : lignes source={sourceRows.ToString(CultureInfo.InvariantCulture)} ; lignes chargées={result.Rows.Count.ToString(CultureInfo.InvariantCulture)} ; colonnes source={sourceColumns.ToString(CultureInfo.InvariantCulture)} ; colonnes finales={result.Headers.Count.ToString(CultureInfo.InvariantCulture)} ; colonne terminale retirée={(terminalColumnRemoved ? "oui" : "non")} ; total attendu={expectedTotal?.ToString(CultureInfo.InvariantCulture) ?? "inconnu"} ; complet={(result.Complete ? "oui" : "non")}.");
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static void RejectKnownOtherFormat(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".xlsx" or ".xls" or ".xlsb" or ".slk" or ".sylk" or ".ods" or ".pdf" or ".zip" or ".gz"
            or ".html" or ".htm" or ".xml" or ".json" or ".exe" or ".dll" or ".png" or ".jpg" or ".jpeg" or ".gif")
            throw Invalid("Le fichier reçu n’est pas un CSV. Choisis l’export CSV de GLPI ; le fichier d’origine est conservé.");
    }

    private static EncodingInfo DetectEncoding(FileStream file, CancellationToken ct)
    {
        Span<byte> start = stackalloc byte[8];
        file.Position = 0;
        int count = file.Read(start);
        var bytes = start[..count];
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) || bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
            throw Invalid("L’encodage UTF-32 n’est pas pris en charge. Enregistre le CSV en UTF-8 ou UTF-16.");
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return new(new UTF8Encoding(false, true), 3, false, "UTF-8");
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return new(new UnicodeEncoding(false, false, true), 2, false, "UTF-16 LE");
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return new(new UnicodeEncoding(true, false, true), 2, false, "UTF-16 BE");
        if (bytes.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }) || bytes.StartsWith(new byte[] { 0x50, 0x4B, 0x05, 0x06 })
            || bytes.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }) || bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })
            || bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) || bytes.StartsWith(new byte[] { 0x1F, 0x8B })
            || bytes.StartsWith("%PDF-"u8) || bytes.StartsWith("GIF8"u8) || bytes.StartsWith("MZ"u8) || bytes.StartsWith(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }))
            throw Invalid("Le téléchargement contient un format binaire ou un document autre qu’un CSV.");

        var utf8 = new EncodingInfo(new UTF8Encoding(false, true), 0, false, "UTF-8");
        try
        {
            // Validate the whole stream: an invalid byte after the preview sample must not
            // be replaced silently by the decoder when the final CSV is read.
            using var reader = OpenReader(file, utf8);
            var buffer = new char[32_768];
            while (reader.Read(buffer, 0, buffer.Length) != 0) ct.ThrowIfCancellationRequested();
            return utf8;
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return new(Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), 0, true, "Windows-1252");
        }
    }

    private static TextInspection ValidateText(FileStream file, EncodingInfo encoding, CancellationToken ct, Action<string>? diagnostics)
    {
        var inspection = new TextInspection();
        try
        {
            using var reader = OpenReader(file, encoding);
            var buffer = new char[32_768];
            var prefix = new StringBuilder(4096);
            bool prefixStarted = false;
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    // Inspect the first meaningful content, even after a long blank
                    // preamble. This does not trim or rewrite the imported fields.
                    if (prefix.Length < 4096 && (prefixStarted || !(char.IsWhiteSpace(c) || char.IsControl(c) || c == '\uFEFF')))
                    {
                        prefix.Append(c);
                        prefixStarted = true;
                    }
                    if (!char.IsControl(c) || c is '\t' or '\r' or '\n') continue;
                    if (!inspection.UnusualControls.TryGetValue(c, out var occurrence))
                    {
                        occurrence = new ControlOccurrence(inspection.CharacterCount + i);
                        inspection.UnusualControls.Add(c, occurrence);
                    }
                    occurrence.Count++;
                    inspection.UnusualControlCount++;
                    // VT, FF and NEL are text-layout controls. They may occur frequently
                    // in copied descriptions without indicating a binary payload.
                    if (c is not ('\u000B' or '\u000C' or '\u0085')) inspection.NonTextControlCount++;
                }
                inspection.CharacterCount += read;
            }
            inspection.Prefix = prefix.ToString();
        }
        catch (DecoderFallbackException)
        {
            // DecoderFallbackException includes offending input bytes. Do not attach it
            // to a user-facing diagnostic or exception that may later be logged.
            diagnostics?.Invoke($"CSV natif : encodage {encoding.Name} déclaré mais séquence invalide ; aucun remplacement effectué.");
            throw Invalid($"Le fichier déclare un encodage {encoding.Name}, mais contient une séquence invalide. Le fichier natif est conservé.");
        }
        diagnostics?.Invoke($"CSV natif : texte={inspection.CharacterCount.ToString(CultureInfo.InvariantCulture)} unités UTF-16 ; contrôles inhabituels={inspection.UnusualControlCount.ToString(CultureInfo.InvariantCulture)} ; codes distincts={inspection.UnusualControls.Count.ToString(CultureInfo.InvariantCulture)}. Offsets UTF-16 depuis le premier caractère après BOM, base 0 ; aucun contenu de cellule journalisé.");
        var compatibleSigns = inspection.UnusualControls.Where(pair => pair.Key is >= '\u0080' and <= '\u009F' and not ('\u0081' or '\u008D' or '\u008F' or '\u0090' or '\u009D')).Sum(pair => pair.Value.Count);
        if (compatibleSigns > 0)
            diagnostics?.Invoke($"CSV natif : {compatibleSigns.ToString(CultureInfo.InvariantCulture)} contrôle(s) C1 correspondent aussi à des positions de signes Windows-1252. Une conversion antérieure est possible, non démontrée ; aucun recodage ou remplacement automatique.");
        if (diagnostics is not null)
        {
            // SafeLog caps each displayed/persisted line at 1,800 characters. Keep
            // every code/count/offset visible even when all control codes occur.
            var groups = inspection.UnusualControls.OrderBy(pair => pair.Key).Chunk(10).ToArray();
            if (groups.Length == 0) diagnostics("CSV natif : histogramme=aucun contrôle inhabituel.");
            for (int i = 0; i < groups.Length; i++)
                diagnostics($"CSV natif : histogramme {(i + 1).ToString(CultureInfo.InvariantCulture)}/{groups.Length.ToString(CultureInfo.InvariantCulture)} : {string.Join(", ", groups[i].Select(FormatControl))}.");
        }
        if (inspection.UnusualControls.TryGetValue('\0', out var nul))
            throw Invalid($"Le téléchargement contient {nul.Count.ToString(CultureInfo.InvariantCulture)} caractère(s) NUL U+0000 (premier offset UTF-16 {nul.FirstOffset.ToString(CultureInfo.InvariantCulture)}, base 0). Le contenu peut être binaire ou son encodage non reconnu ; il n’est pas importé comme CSV. Le fichier natif reste intact.");
        // This is a conservative rejection of control-dominated content, not a blanket
        // prohibition on control characters in otherwise textual CSV descriptions.
        if (inspection.NonTextControlCount >= 32 && inspection.NonTextControlCount > inspection.CharacterCount / 4)
            throw Invalid("Le téléchargement présente une proportion anormalement élevée de caractères de contrôle non textuels (plus de 25 %, au moins 32). Il peut s’agir d’un fichier binaire ; import interrompu et fichier natif conservé.");
        return inspection;
    }

    private static string FormatControl(KeyValuePair<char, ControlOccurrence> pair) =>
        $"U+{(int)pair.Key:X4}×{pair.Value.Count.ToString(CultureInfo.InvariantCulture)} (premier offset {pair.Value.FirstOffset.ToString(CultureInfo.InvariantCulture)})";

    private sealed class TextInspection
    {
        public string Prefix { get; set; } = "";
        public long CharacterCount { get; set; }
        public long UnusualControlCount { get; set; }
        public long NonTextControlCount { get; set; }
        public Dictionary<char, ControlOccurrence> UnusualControls { get; } = new();
    }

    private sealed class ControlOccurrence(long firstOffset)
    {
        public long Count { get; set; }
        public long FirstOffset { get; } = firstOffset;
    }

    private static void RejectMarkupOrOtherText(string prefix)
    {
        int start = 0;
        while (start < prefix.Length && (char.IsWhiteSpace(prefix[start]) || char.IsControl(prefix[start]) || prefix[start] == '\uFEFF')) start++;
        string text = prefix[start..];
        if (text.StartsWith("<", StringComparison.Ordinal))
            throw Invalid("GLPI a renvoyé une page HTML/XML, possiblement une page de connexion, au lieu du CSV attendu. Vérifie la session SSO puis relance l’export.");
        if (text.StartsWith("%PDF-", StringComparison.Ordinal))
            throw Invalid("Le téléchargement correspond à un document PDF, pas à un CSV.");
        if (text.StartsWith("{\"", StringComparison.Ordinal) || text.StartsWith("{\r", StringComparison.Ordinal)
            || text.StartsWith("{\n", StringComparison.Ordinal) || text.StartsWith("[{", StringComparison.Ordinal))
            throw Invalid("GLPI a renvoyé un document JSON au lieu du tableau CSV attendu.");
    }

    private static Preamble DetectPreamble(FileStream file, EncodingInfo encoding, CancellationToken ct)
    {
        using var reader = OpenReader(file, encoding);
        int skip = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int first = reader.Read();
            if (first < 0) throw Invalid("Le téléchargement ne contient que des lignes vides.");
            if (first is '\r' or '\n')
            {
                if (first == '\r' && reader.Peek() == '\n') _ = reader.Read();
                skip++; continue;
            }
            // Only the five-character directive is relevant. Reading a whole
            // first line here could allocate hundreds of MiB before field limits
            // and cancellation in the CSV parser were ever reached.
            if (first is not ('s' or 'S')) return new(skip, null);
            foreach (char expected in "ep=")
            {
                int next = reader.Read();
                if (next != expected && next != char.ToUpperInvariant(expected)) return new(skip, null);
            }
            int delimiter = reader.Read();
            int end = reader.Read();
            if (delimiter < 0 || !Delimiters.Contains((char)delimiter) || end is not (-1 or '\r' or '\n'))
                throw Invalid("La directive CSV sep= indique un séparateur non pris en charge. Séparateurs acceptés : point-virgule, virgule, tabulation.");
            return new(skip + 1, (char)delimiter);
        }
    }

    private static char DetectDelimiter(FileStream file, EncodingInfo encoding, int skipLines, CancellationToken ct)
    {
        var candidates = new List<(char Delimiter, int Columns)>();
        string? firstError = null;
        bool multicolumnHeaderObserved = false;
        foreach (char delimiter in Delimiters)
        {
            try
            {
                using var reader = OpenReader(file, encoding);
                SkipLines(reader, skipLines, ct);
                int columns = 0, records = 0;
                bool consistent = true;
                foreach (var record in ReadRecords(reader, delimiter, skipLines + 1, ct))
                {
                    if (record.PhysicallyBlank) continue;
                    if (columns == 0)
                    {
                        columns = record.Fields.Length;
                        multicolumnHeaderObserved |= columns > 1;
                    }
                    else if (record.Fields.Length != columns) { consistent = false; break; }
                    if (++records >= 64) break;
                }
                if (columns != 0 && consistent) candidates.Add((delimiter, columns));
            }
            catch (InvalidDataException ex) { firstError ??= ex.Message; }
        }
        var multipleColumns = candidates.Where(c => c.Columns > 1).ToList();
        if (multipleColumns.Count == 1) return multipleColumns[0].Delimiter;
        if (multipleColumns.Count > 1)
            throw Invalid("Plusieurs séparateurs produisent un tableau plausible. Le CSV est ambigu ; utilise l’export standard GLPI ou une directive sep= explicite.");
        if (candidates.Count != 0 && !multicolumnHeaderObserved) return candidates[0].Delimiter;
        throw Invalid("Le fichier ne présente pas une structure CSV régulière. " + (firstError ?? "Le nombre de cellules varie entre les lignes."));
    }

    private static StreamReader OpenReader(FileStream file, EncodingInfo encoding)
    {
        file.Position = encoding.PreambleBytes;
        return new StreamReader(file, encoding.Encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 65_536, leaveOpen: true);
    }

    private static void SkipLines(TextReader reader, int count, CancellationToken ct)
    {
        for (int i = 0; i < count; i++)
        {
            if ((i & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            _ = reader.ReadLine();
        }
        ct.ThrowIfCancellationRequested();
    }

    private static IEnumerable<CsvRecord> ReadRecords(TextReader reader, char delimiter, int firstLine, CancellationToken ct)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var state = FieldState.Start;
        int physicalLine = firstLine, recordLine = firstLine, characters = 0;
        bool content = false;
        while (true)
        {
            if ((characters++ & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            int read = reader.Read();
            if (read < 0)
            {
                if (state == FieldState.Quoted) throw Invalid($"CSV malformé : guillemet non fermé dans l’enregistrement commencé à la ligne {recordLine}.");
                if (content || fields.Count != 0 || field.Length != 0)
                {
                    AddField();
                    yield return new(fields.ToArray(), recordLine, false);
                }
                yield break;
            }
            char c = (char)read;
            if (state == FieldState.Quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { _ = reader.Read(); Append('"'); }
                    else state = FieldState.AfterQuote;
                }
                else if (c is '\r' or '\n')
                {
                    Append(c);
                    if (c == '\r' && reader.Peek() == '\n') { _ = reader.Read(); Append('\n'); }
                    physicalLine++;
                }
                else Append(c);
                continue;
            }
            if (c == delimiter)
            {
                content = true; AddField(); state = FieldState.Start;
                continue;
            }
            if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n') _ = reader.Read();
                AddField();
                yield return new(fields.ToArray(), recordLine, !content);
                fields.Clear(); field.Clear(); state = FieldState.Start; content = false;
                physicalLine++; recordLine = physicalLine;
                continue;
            }
            if (state == FieldState.AfterQuote)
                throw Invalid($"CSV malformé à la ligne physique {physicalLine} : caractère inattendu après un guillemet fermant.");
            if (c == '"')
            {
                if (state != FieldState.Start) throw Invalid($"CSV malformé à la ligne physique {physicalLine} : guillemet dans un champ non délimité.");
                state = FieldState.Quoted; content = true;
                continue;
            }
            state = FieldState.Unquoted; content = true; Append(c);
        }

        void Append(char c)
        {
            if (field.Length >= MaximumFieldCharacters) throw Invalid($"Une cellule dépasse 16 Mio de caractères à la ligne {recordLine}. Import interrompu.");
            field.Append(c);
        }

        void AddField()
        {
            if (fields.Count >= MaximumColumnsWithTerminalField) throw Invalid("Le CSV possède trop de colonnes pour un export Excel.");
            fields.Add(field.ToString()); field.Clear();
        }
    }

    private static List<string> UniqueHeaders(string[] header, out bool renamed, CancellationToken ct)
    {
        var bases = header.Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Colonne {i + 1}" : h.Trim()).ToArray();
        var reserved = new HashSet<string>(bases, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(header.Length);
        renamed = false;
        for (int i = 0; i < bases.Length; i++)
        {
            if ((i & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            string name = bases[i];
            if (!used.Add(name))
            {
                int suffix = 2;
                do
                {
                    if ((suffix & 0xFF) == 0) ct.ThrowIfCancellationRequested();
                    name = bases[i] + " (" + (suffix++).ToString(CultureInfo.InvariantCulture) + ")";
                }
                while (reserved.Contains(name) || !used.Add(name));
            }
            if (name != header[i]) renamed = true;
            result.Add(name);
        }
        return result;
    }

    private static string SafeSourceUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "";
        var safe = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        return safe.Uri.GetLeftPart(UriPartial.Path);
    }

    private static InvalidDataException Invalid(string message) => new(message);
    private sealed record EncodingInfo(Encoding Encoding, int PreambleBytes, bool Fallback, string Name);
    private sealed record Preamble(int SkipLines, char? Delimiter);
    private sealed record CsvRecord(string[] Fields, int Line, bool PhysicallyBlank);
    private enum FieldState { Start, Unquoted, Quoted, AfterQuote }
}
