using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using GlpiPilot.Core;

string temporary = Path.Combine(Path.GetTempPath(), "GlpiPilot-NativeImport-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
const string source = "https://glpi.example.invalid/front/ticket.php?token=secret#session";
var checks = new List<(string Name, Action Run)>
{
    ("UTF-8 BOM : champs multilignes, guillemets, zéros et formules littérales", CheckMultiline),
    ("UTF-8 sans BOM : Unicode et emoji préservés", CheckUtf8),
    ("UTF-16 LE avec BOM", () => CheckUtf16(false)),
    ("UTF-16 BE avec BOM", () => CheckUtf16(true)),
    ("Windows-1252 : repli explicite et accents conservés", CheckWindows1252),
    ("Séparateur virgule avec virgules entre guillemets", CheckComma),
    ("Séparateur tabulation avec tabulations entre guillemets", CheckTab),
    ("Directive sep= et séparateur autrement ambigu", CheckDirective),
    ("En-têtes vides et doublons : noms uniques sans perdre de colonnes", CheckHeaders),
    ("Doublons de données légitimes conservés", CheckDuplicateRows),
    ("GLPI 10 : colonne finale entièrement vide retirée", CheckTerminalColumn),
    ("Colonne finale non vide au-delà de la limite conservée", CheckTerminalBeyondCap),
    ("Lignes physiques vides ignorées, cellules vides et paragraphes conservés", CheckBlankRecords),
    ("Total inconnu : exhaustivité jamais certifiée", CheckUnknownTotal),
    ("Périmètre toutes-pages non confirmé : exhaustivité non certifiée", CheckUnconfirmedScope),
    ("Total connu différent : résultat explicitement incomplet", CheckCountMismatch),
    ("Limite d’import : compteur réel, contenu natif intact et état partiel", CheckCap),
    ("CSV malformé : guillemets et largeur incohérente refusés", CheckMalformed),
    ("CSV malformé après les lignes conservées : erreur non masquée", CheckMalformedBeyondCap),
    ("HTML de connexion, JSON et fichiers binaires refusés", CheckNonCsv),
    ("BOM avec séquence invalide : aucun remplacement silencieux", CheckInvalidBomEncoding),
    ("En-têtes seuls et total zéro : export vide valide", CheckZeroRows),
    ("Fichier vide, limites invalides et annulation refusés proprement", CheckInvalidArguments),
    ("CSV natif vers XLSX : toutes les lignes et cellules texte", CheckXlsxRoundtrip),
    ("Contrôles C0/C1 : descriptions et caractères conservés sans modifier le natif", CheckTextControls),
    ("Diagnostics : codes, comptes et offsets UTF-16 sans contenu privé", CheckControlDiagnostics),
    ("Histogramme complet : tous les codes restent visibles sous la limite du journal", CheckDiagnosticHistogramChunks),
    ("Contrôles textuels en UTF-16 et Windows-1252 : encodage explicite", CheckControlEncodings),
    ("Contrôles au-delà de la limite : diagnostic sur tout le fichier", CheckControlsBeyondCap),
    ("NUL et contenu dominé par des contrôles : refus documenté sans données", CheckNonTextDiagnostics),
    ("Contrôles : HTML et CSV malformés restent refusés", CheckControlMarkupAndMalformed),
    ("Diagnostic complet : BOM, séparateur, colonnes et nombre réel de lignes", CheckDiagnosticSummary),
    ("831 descriptions synthétiques : CSV volumineux, contrôles et intégrité du fichier", CheckLargeNativeCsv),
    ("Encodage invalide : diagnostic sans octets d’entrée ni exception interne", CheckInvalidEncodingDiagnostics),
    ("Signes C1 en UTF-8 : hypothèse documentée sans correction silencieuse", CheckC1Typography),
    ("Annulation après inspection : aucun préambule vide ne masque la demande", CheckCancelledPreamble),
    ("Annulation finale : aucun résultat n’est publié après la demande", CheckCancelledPublication),
    ("Préambule sep= : préfixes proches, lignes CR et en-têtes littéraux conservés", CheckPreamblePrefixes),
    ("Qualité : réception, contrôles initiaux/finals et réserves restent indépendants", CheckQualityObservations),
    ("Excel : réception complète qualifiée et contrôle d’identité indisponible explicites", CheckQualityInformation)
};
int failures = 0;
try
{
    foreach (var check in checks)
    {
        try { check.Run(); Console.WriteLine("PASS " + check.Name); }
        catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + check.Name + "\n" + e); }
    }
    Console.WriteLine($"{checks.Count - failures}/{checks.Count} vérifications d’import natif réussies.");
}
finally { Directory.Delete(temporary, recursive: true); }
return failures == 0 ? 0 : 1;

string FileFor(string text, Encoding? encoding = null, string extension = ".csv")
{
    string path = Path.Combine(temporary, Guid.NewGuid().ToString("N") + extension);
    File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false, true));
    return path;
}

ExtractionResult Import(string text, int? expected = null, int maxRows = 1_000_000, bool allPages = true) =>
    NativeCsvImportService.Read(FileFor(text), source, expected, maxRows, allPages);

void CheckMultiline()
{
    string csv = "\"ID\";\"Titre\";\"Description\"\r\n\"00042\";\"=1+1\";\"Une ligne; puis une autre\r\navec \"\"guillemets\"\" et é\"\r\n";
    var r = NativeCsvImportService.Read(FileFor(csv, new UTF8Encoding(true, true)), source, 1);
    Equal(3, r.Headers.Count); Equal(1, r.Rows.Count); True(r.Complete, "Le total connu doit concorder.");
    Equal("00042", r.Rows[0][0]); Equal("=1+1", r.Rows[0][1]);
    Equal("Une ligne; puis une autre\r\navec \"guillemets\" et é", r.Rows[0][2]);
    Equal("https://glpi.example.invalid/front/ticket.php", r.SourceUrl);
}

void CheckUtf8()
{
    var r = Import("ID;Libellé\n01;Écran 😀\n", 1);
    Equal("Écran 😀", r.Rows[0][1]); True(r.Complete, "Le CSV UTF-8 doit être reconnu.");
    True(!r.Warnings.Any(w => w.Contains("Windows-1252", StringComparison.Ordinal)), "Pas de repli pour un UTF-8 valide.");
}

void CheckUtf16(bool bigEndian)
{
    string path = FileFor("ID;Libellé\r\n001;Écran 😀\r\n", new UnicodeEncoding(bigEndian, true, true));
    var r = NativeCsvImportService.Read(path, source, 1);
    Equal("001", r.Rows[0][0]); Equal("Écran 😀", r.Rows[0][1]); True(r.Complete, "Le CSV UTF-16 doit être lu.");
}

void CheckWindows1252()
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var r = NativeCsvImportService.Read(FileFor("ID;État\r\n001;Réparé — 20 €\r\n", Encoding.GetEncoding(1252)), source, 1);
    Equal("Réparé — 20 €", r.Rows[0][1]);
    True(r.Warnings.Any(w => w.Contains("Windows-1252", StringComparison.Ordinal)), "Le choix de repli doit être explicite.");
}

void CheckComma()
{
    var r = Import("\"ID\",\"Personne\",\"Montant\"\n\"01\",\"Durand, Jean\",\"3,50\"\n", 1);
    Equal(3, r.Headers.Count); Equal("Durand, Jean", r.Rows[0][1]); Equal("3,50", r.Rows[0][2]);
}

void CheckTab()
{
    var r = Import("ID\tLibellé\n01\t\"Une\ttabulation\"\n", 1);
    Equal(2, r.Headers.Count); Equal("Une\ttabulation", r.Rows[0][1]);
}

void CheckDirective()
{
    const string ambiguous = "ID;Nom, complet\n1;Durand, Jean\n";
    Throws<InvalidDataException>(() => Import(ambiguous));
    var r = Import("\nsep=;\r\n" + ambiguous, 1);
    Equal("Nom, complet", r.Headers[1]); Equal("Durand, Jean", r.Rows[0][1]); True(r.Complete, "La directive explicite doit lever l’ambiguïté.");
    Throws<InvalidDataException>(() => Import("sep=|\nA|B\n1|2\n"));
}

void CheckHeaders()
{
    var r = Import("\"Nom\";\"Nom\";\"Nom (2)\";\"\"\n\"a\";\"b\";\"c\";\"d\"\n", 1);
    Equal(4, r.Headers.Count); Equal(4, r.Headers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    Equal("Nom", r.Headers[0]); Equal("Nom (3)", r.Headers[1]); Equal("Nom (2)", r.Headers[2]); Equal("Colonne 4", r.Headers[3]);
    Equal("a,b,c,d", string.Join(',', r.Rows[0]));
}

void CheckDuplicateRows()
{
    var r = Import("ID;Texte\n1;identique\n1;identique\n", 2);
    Equal(2, r.Rows.Count); True(r.Complete, "Les doublons légitimes doivent compter deux lignes.");
}

void CheckTerminalColumn()
{
    var r = Import("\"ID\";\"Nom\";\n\"001\";\"P1\";\n\"002\";\"P2\";\n", 2);
    Equal(2, r.Headers.Count); True(r.Rows.All(row => row.Length == 2), "Seule la colonne terminale vide doit disparaître.");
    Equal("002", r.Rows[1][0]); True(r.Complete, "Le séparateur final GLPI10 doit être accepté.");
}

void CheckTerminalBeyondCap()
{
    var r = Import("\"ID\";\"\"\n\"001\";\"\"\n\"002\";\"valeur\"\n", 2, 1);
    Equal(2, r.Headers.Count); Equal(2, r.Rows[0].Length); True(!r.Complete, "La limite doit rendre l’aperçu incomplet.");
}

void CheckBlankRecords()
{
    var r = Import("\n\r\nID;Description\n\n1;\"paragraphe1\n\nparagraphe2\"\n\n\"\";\"\"\n\n", 2);
    Equal(2, r.Rows.Count); Equal("paragraphe1\n\nparagraphe2", r.Rows[0][1]);
    Equal("", r.Rows[1][0]); Equal("", r.Rows[1][1]); True(r.Complete, "Une ligne de cellules vides explicites reste une ligne de données.");
    Throws<InvalidDataException>(() => Import("ID;Description\n \n"));
}

void CheckUnknownTotal()
{
    var r = Import("ID;Nom\n1;P1\n");
    True(!r.Complete, "Le nombre de lignes du fichier ne doit pas être utilisé comme total GLPI.");
    Equal<int?>(null, r.ExpectedTotal);
    True(r.Warnings.Any(w => w.Contains("Total GLPI inconnu", StringComparison.Ordinal)), "L’absence de vérification doit être expliquée.");
    True(r.Quality is { SourceRows: 1, ExpectedRows: null, RowCountVerified: null, ReceptionComplete: false }, "Le comptage du CSV ne crée pas un total GLPI observé.");
}

void CheckUnconfirmedScope()
{
    var r = Import("ID;Nom\n1;P1\n", 1, allPages: false);
    True(!r.Complete, "Un export de périmètre inconnu ne certifie pas Toutes les pages.");
    True(r.Scope.Contains("non confirmé", StringComparison.Ordinal), "Le périmètre doit être honnête.");
    True(r.Quality is { RowCountVerified: true, AllPagesRequested: false, ReceptionComplete: false }, "Un nombre égal sans toutes les pages ne certifie pas la réception complète.");
}

void CheckCountMismatch()
{
    var r = Import("ID;Nom\n1;P1\n2;P2\n", 3);
    Equal(2, r.Rows.Count); True(!r.Complete, "Le total différent doit produire un résultat incomplet.");
    True(r.Warnings.Any(w => w.Contains("2 lignes pour 3", StringComparison.Ordinal)), "Le nombre du CSV et celui de GLPI doivent apparaître.");
    True(r.Quality is { SourceRows: 2, ExpectedRows: 3, RowCountVerified: false, ReceptionComplete: false }, "Le déficit de lignes doit rester distinct d’un écart d’affichage.");
}

void CheckCap()
{
    string path = FileFor("ID;Nom\n1;P1\n2;P2\n3;P3\n");
    byte[] before = File.ReadAllBytes(path);
    var r = NativeCsvImportService.Read(path, source, 1, maxRows: 1);
    Equal(1, r.Rows.Count); True(!r.Complete, "Un total égal à l’aperçu tronqué ne suffit pas.");
    True(r.Status.Contains("1 / 3", StringComparison.Ordinal), "Le nombre réel du fichier doit rester connu.");
    True(before.SequenceEqual(File.ReadAllBytes(path)), "Le CSV téléchargé doit rester intact.");
    True(r.Quality is { SourceRows: 3, ExpectedRows: 1, RowCountVerified: false, ReceptionComplete: false }, "Le compte source entier, pas l’aperçu plafonné, doit figurer dans la qualité.");
    var exact = NativeCsvImportService.Read(path, source, 3, maxRows: 3);
    True(exact.Complete, "Atteindre exactement la limite sans ligne supplémentaire reste complet.");
}

void CheckMalformed()
{
    foreach (string text in new[]
    {
        "ID;Nom\n1;\"non fermé\n",
        "ID;Nom\n1;a\"b\n",
        "ID;Nom\n1;\"a\"reste\n",
        "ID;Nom\n1;P1;extra\n",
        "ID;Nom\n1\n"
    }) Throws<InvalidDataException>(() => Import(text));
}

void CheckMalformedBeyondCap()
{
    string text = "ID;Nom\n" + string.Concat(Enumerable.Range(1, 70).Select(i => $"{i};P{i}\n")) + "71;P71;extra\n";
    Throws<InvalidDataException>(() => Import(text, maxRows: 1));
}

void CheckNonCsv()
{
    foreach (string text in new[]
    {
        "<!doctype html><html><body><form><input type=password></form></body></html>",
        " \r\n<!--proxy--><html>Connexion</html>",
        "{\"error\":\"unauthorized\"}",
        "ID;Nom\n1;P\0C\n"
    }) Throws<InvalidDataException>(() => Import(text));
    string binary = Path.Combine(temporary, "binary.csv"); File.WriteAllBytes(binary, new byte[] { 0x50, 0x4B, 3, 4, 0, 0 });
    Throws<InvalidDataException>(() => NativeCsvImportService.Read(binary, source));
    Throws<InvalidDataException>(() => NativeCsvImportService.Read(FileFor("ID;Nom\n1;P1\n", extension: ".xlsx"), source));
}

void CheckInvalidBomEncoding()
{
    string path = Path.Combine(temporary, "invalid-utf8.csv");
    File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF, 0x49, 0x44, 0x3B, 0xFF, 0x0A });
    Throws<InvalidDataException>(() => NativeCsvImportService.Read(path, source));
    string utf16 = Path.Combine(temporary, "invalid-utf16.csv");
    File.WriteAllBytes(utf16, new byte[] { 0xFF, 0xFE, 0x49 });
    Throws<InvalidDataException>(() => NativeCsvImportService.Read(utf16, source));
}

void CheckZeroRows()
{
    var r = Import("ID;Nom\n", 0);
    Equal(0, r.Rows.Count); Equal(2, r.Headers.Count); True(r.Complete, "Un export sans données peut être valide si GLPI annonce zéro.");
}

void CheckInvalidArguments()
{
    Throws<InvalidDataException>(() => Import(""));
    Throws<InvalidDataException>(() => Import("\n\r\n"));
    Throws<ArgumentOutOfRangeException>(() => Import("ID;Nom\n", maxRows: 0));
    Throws<ArgumentOutOfRangeException>(() => Import("ID;Nom\n", maxRows: 1_000_001));
    Throws<ArgumentOutOfRangeException>(() => Import("ID;Nom\n", expected: -1));
    using var cts = new CancellationTokenSource(); cts.Cancel();
    Throws<OperationCanceledException>(() => NativeCsvImportService.Read(FileFor("ID;Nom\n"), source, cancellationToken: cts.Token));
}

void CheckCancelledPreamble()
{
    string path = FileFor(new string('\n', 8192));
    var original = File.ReadAllBytes(path);
    using var cancellation = new CancellationTokenSource();
    Throws<OperationCanceledException>(() => NativeCsvImportService.Read(path, source, cancellationToken: cancellation.Token,
        diagnostics: message => { if (message.StartsWith("CSV natif : texte=", StringComparison.Ordinal)) cancellation.Cancel(); }));
    True(original.SequenceEqual(File.ReadAllBytes(path)), "Une annulation ne doit modifier aucun octet du reçu natif.");
}

void CheckCancelledPublication()
{
    string path = FileFor("ID;Titre;\n0001;Valeur littérale;\n");
    using var cancellation = new CancellationTokenSource();
    Throws<OperationCanceledException>(() => NativeCsvImportService.Read(path, source, 1, cancellationToken: cancellation.Token,
        diagnostics: message => { if (message.StartsWith("CSV natif : lignes source=", StringComparison.Ordinal)) cancellation.Cancel(); }));
}

void CheckPreamblePrefixes()
{
    foreach (var name in new[] { "s", "se", "sep", "SEParation", "sep=;" })
    {
        var data = Import("\"" + name + "\";ID\rValeur;0001\r", 1);
        Equal(name, data.Headers[0]); Equal("0001", data.Rows[0][1]);
    }
    var literal = Import("sep;ID\nValeur;0001\n", 1);
    Equal("sep", literal.Headers[0]);
    var directed = Import("\r\r\nSeP=;\rID;Titre\r0001;Littéral\r", 1);
    Equal(2, directed.Headers.Count); Equal("0001", directed.Rows[0][0]); True(directed.Complete, "Le préambule accepte CR, CRLF et casse du mot sep.");
    Throws<InvalidDataException>(() => Import("sep=;"));
    Throws<InvalidDataException>(() => Import("sep=;;\nID;Titre\n1;Texte\n"));
}

void CheckXlsxRoundtrip()
{
    var r = Import("ID;Valeur\n001;=1+1\n002;@texte\n003;écran\n", 3);
    string path = Path.Combine(temporary, "converted.xlsx");
    ExportService.WriteXlsx(r, path);
    using var zip = ZipFile.OpenRead(path);
    using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    var xml = XDocument.Load(stream);
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    Equal(4, xml.Descendants(ns + "row").Count());
    Equal(0, xml.Descendants(ns + "f").Count());
    var values = xml.Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!, c => string.Concat(c.Descendants(ns + "t").Select(t => t.Value)));
    Equal("001", values["A2"]); Equal("=1+1", values["B2"]); Equal("003", values["A4"]);
}

void CheckTextControls()
{
    string controls = "\u0001\u0007\u0008\u000B\u000C\u001A\u001B\u001F\u007F" +
        new string(Enumerable.Range(0x80, 0x20).Select(code => (char)code).ToArray());
    string description = "Description copiée avec du texte avant les contrôles : " + controls +
        "; suite après les contrôles.\r\nUne seconde ligne avec \"guillemets\" et des accents.";
    string csv = "ID;De\u000Bscription\r\n00042;\"" + description.Replace("\"", "\"\"") + "\"\r\n";
    string path = FileFor(csv, new UTF8Encoding(true, true));
    byte[] before = File.ReadAllBytes(path);
    var logs = new List<string>();
    var r = NativeCsvImportService.Read(path, source, 1, diagnostics: logs.Add);
    True(r.Complete, "Les contrôles textuels ne doivent pas retirer de lignes ni invalider un total concordant.");
    Equal("00042", r.Rows[0][0]); Equal(description, r.Rows[0][1]); Equal("De\u000Bscription", r.Headers[1]);
    True(r.Quality is { UnusualControlCount: 42, C1ControlCount: 32, ReceptionComplete: true }, "Les contrôles du texte sont comptés sans invalider une réception de lignes exacte ni réencoder le CSV.");
    True(logs.Any(w => w.Contains("U+000B×2", StringComparison.Ordinal)) && logs.Any(w => w.Contains("U+009F×1", StringComparison.Ordinal)), "Tous les codes et nombres doivent rester dans le diagnostic.");
    True(r.Warnings.Any(w => w.Contains("caractère(s) de contrôle inhabituels", StringComparison.Ordinal) && !w.Contains("offset", StringComparison.Ordinal)), "L’avertissement utilisateur doit rester lisible, sans dump technique.");
    True(before.SequenceEqual(File.ReadAllBytes(path)), "Le CSV original doit rester strictement identique octet par octet.");
}

void CheckQualityObservations()
{
    var r = Import("ID;Nom\n1;Valeur synthétique\n", 1);
    True(r.Quality is { SourceRows: 1, ExpectedRows: 1, AllPagesRequested: true, RowCountVerified: true,
        FinalTotalVerified: null, InitialSample: null, FinalSample: null, ReceptionComplete: true }, "L’import isolé connaît la réception, pas des observations navigateur inventées.");
    r.Complete = false;
    r.Quality!.InitialSample = new() { IdentityVerified = true, RepresentationVerified = false, SampleSize = 1, ComparedCells = 1, DifferentCells = 1 };
    r.Quality.FinalSample = new() { IdentityVerified = true, RepresentationVerified = true, SampleSize = 1, ComparedCells = 1 };
    r.Quality.FinalTotalVerified = true;
    var serialized = System.Text.Json.JsonSerializer.Serialize(r);
    var restored = System.Text.Json.JsonSerializer.Deserialize<ExtractionResult>(serialized)!;
    True(!restored.Complete && restored.Quality is { ReceptionComplete: true, SampleIdentityVerified: true, RepresentationVerified: false }, "Une concordance finale ne doit pas effacer l’écart initial ni promouvoir Complete à true après stockage.");
    True(!System.Text.Json.JsonSerializer.Serialize(restored.Quality).Contains("synthétique", StringComparison.Ordinal), "Les observations ne stockent que des compteurs et états, jamais des cellules.");
    restored.Quality!.FinalTotalVerified = false;
    True(!restored.Quality.ReceptionComplete, "Un total final différent invalide la réception complète par rapport au total courant.");
    var legacy = System.Text.Json.JsonSerializer.Deserialize<ExtractionResult>("{\"Complete\":false}")!;
    True(legacy.Quality is null && !legacy.Complete, "Les anciennes archives ne reçoivent pas de preuves fabriquées.");
}

void CheckQualityInformation()
{
    var r = Import("ID;Nom\n1;Valeur synthétique\n", 1);
    r.Complete = false;
    r.Quality!.InitialSample = new() { SampleSize = 1 };
    r.Quality.FinalSample = new() { IdentityVerified = true, RepresentationVerified = false, SampleSize = 1, ComparedCells = 1, DifferentCells = 1 };
    r.Warnings.Add("Réserve synthétique"); r.Warnings.Add("Réserve synthétique");
    var path = Path.Combine(temporary, "qualified.xlsx"); ExportService.WriteXlsx(r, path);
    using var zip = ZipFile.OpenRead(path);
    using var stream = zip.GetEntry("xl/worksheets/sheet2.xml")!.Open();
    var xml = XDocument.Load(stream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    var rows = xml.Descendants(ns + "row").Skip(1).Select(row => row.Elements(ns + "c")
        .Select(cell => string.Concat(cell.Descendants(ns + "t").Select(text => text.Value))).ToArray())
        .ToDictionary(row => row[0], row => row[1]);
    Equal("Réception complète · contrôles à vérifier", rows["État"]);
    Equal("Non disponible", rows["Identité de l’échantillon initial"]);
    True(rows["Comparaison de l’échantillon final"].Contains("1/1 écarts", StringComparison.Ordinal), "Le XLSX conserve la réserve de comparaison.");
    Equal(1, rows.Values.Count(value => value == "Réserve synthétique"));
}

void CheckC1Typography()
{
    const string value = "Ponctuation synthétique : \u0080 \u0085 \u0092 \u0095 \u0096 \u009C ; fin.";
    var logs = new List<string>();
    string path = FileFor("ID;Description\n1;\"" + value + "\"\n", new UTF8Encoding(true, true));
    byte[] before = File.ReadAllBytes(path);
    var data = NativeCsvImportService.Read(path, source, 1, diagnostics: logs.Add);
    Equal(value, data.Rows[0][1]);
    True(before.SequenceEqual(File.ReadAllBytes(path)), "La détection ne doit pas modifier les octets reçus.");
    True(logs.Any(line => line.Contains("6 contrôle(s) C1", StringComparison.Ordinal) && line.Contains("non démontrée", StringComparison.Ordinal)), "L’hypothèse doit être formulée sans affirmer l’encodage d’origine.");
    True(!logs.Any(line => line.Contains(value, StringComparison.Ordinal)), "L’analyse typographique ne doit pas journaliser la cellule.");
    True(data.Warnings.All(warning => !warning.Contains("U+", StringComparison.Ordinal)), "Les dumps de codes appartiennent au diagnostic.");
}

void CheckControlDiagnostics()
{
    const string secret = "PRIVATE-CELL-CONTENT-MUST-NOT-APPEAR";
    const string privateHeader = "PRIVATE-HEADER-MUST-NOT-APPEAR";
    string description = secret + " 😀 " + new string('x', 40_000) + "\u000B\u000B\u009Dfin";
    string csv = "ID;" + privateHeader + "\r\n001;\"" + description + "\"\r\n";
    var logs = new List<string>();
    string path = FileFor(csv, new UTF8Encoding(true, true));
    var r = NativeCsvImportService.Read(path, source, 1, diagnostics: logs.Add);
    Equal(description, r.Rows[0][1]);
    string log = string.Join('\n', logs);
    True(log.Contains("U+000B×2 (premier offset " + csv.IndexOf('\u000B') + ")", StringComparison.Ordinal), "L’offset doit compter les unités UTF-16, même après emoji et bloc de lecture, sans compter le BOM.");
    True(log.Contains("U+009D×1 (premier offset " + csv.IndexOf('\u009D') + ")", StringComparison.Ordinal), "Chaque code doit posséder son premier offset exact.");
    True(log.Contains("contrôles inhabituels=3", StringComparison.Ordinal), "Les contrôles usuels CR/LF doivent être exclus du compte inhabituel.");
    foreach (string forbidden in new[] { secret, privateHeader, Path.GetFileName(path), source, "token=secret", "😀", new string('x', 12) })
        True(!log.Contains(forbidden, StringComparison.Ordinal), "Le diagnostic ne doit contenir ni cellules, ni en-têtes, ni nom de fichier, ni URL.");
    True(log.All(c => !char.IsControl(c) || c == '\n'), "Les codes doivent être écrits sous forme U+XXXX et jamais être injectés comme caractères de contrôle dans le journal.");
}

void CheckControlEncodings()
{
    const string value = "Écran\u000Bcopié\u0085suite\u009Dfin";
    foreach (bool bigEndian in new[] { false, true })
    {
        var logs = new List<string>();
        string path = FileFor("ID;Description\r\n001;\"" + value + "\"\r\n", new UnicodeEncoding(bigEndian, true, true));
        var r = NativeCsvImportService.Read(path, source, 1, diagnostics: logs.Add);
        Equal(value, r.Rows[0][1]); True(r.Complete, "UTF-16 avec BOM et contrôles doit rester importable.");
        True(logs.Any(line => line.Contains("BOM=UTF-16 " + (bigEndian ? "BE" : "LE") + " (2 octets)", StringComparison.Ordinal)), "Le BOM UTF-16 et son ordre doivent être explicites.");
    }
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    const string cpValue = "Réparé\u0081\u008D\u008F\u0090\u009D — 20 €";
    string cpPath = FileFor("ID;Description\r\n001;\"" + cpValue + "\"\r\n", Encoding.GetEncoding(1252));
    var cpLogs = new List<string>();
    var cp = NativeCsvImportService.Read(cpPath, source, 1, diagnostics: cpLogs.Add);
    Equal(cpValue, cp.Rows[0][1]);
    True(cpLogs.Any(line => line.Contains("encodage=Windows-1252 ; BOM=absent", StringComparison.Ordinal)), "Le repli doit être distingué d’un BOM.");
    True(cp.Warnings.Any(w => w.Contains("Windows-1252", StringComparison.Ordinal)), "La lecture de repli doit rester signalée.");
}

void CheckDiagnosticHistogramChunks()
{
    var codes = Enumerable.Range(1, 31).Concat(Enumerable.Range(0x7F, 0x21))
        .Where(code => code is not (9 or 10 or 13)).Select(code => (char)code).ToArray();
    string controls = new(codes);
    const string secret = "PRIVATE-ALL-CONTROLS-DESCRIPTION";
    string value = secret + new string('p', 600) + controls + controls;
    string csv = "ID;Description\n1;\"" + value + "\"\n";
    var logs = new List<string>();
    var result = NativeCsvImportService.Read(FileFor(csv, new UTF8Encoding(true, true)), source, 1, diagnostics: logs.Add);
    True(result.Complete, "Les contrôles ne doivent pas changer le nombre de lignes du fichier.");
    Equal(value, result.Rows[0][1]);
    var histogram = logs.Where(line => line.Contains("histogramme ", StringComparison.Ordinal)).ToList();
    True(histogram.Count > 1, "Un grand histogramme doit être réparti sur plusieurs lignes.");
    foreach (string line in histogram)
    {
        True(line.Length <= 1800, "Aucune ligne d’histogramme ne doit être coupée par la limite SafeLog de 1 800 caractères.");
        True(line.Split("U+", StringSplitOptions.None).Length - 1 <= 10, "Chaque ligne doit rester limitée à dix codes Unicode.");
        True(!line.Contains(secret, StringComparison.Ordinal), "Le découpage ne doit introduire aucun contenu privé.");
    }
    foreach (char code in codes)
    {
        string expected = $"U+{(int)code:X4}×2 (premier offset {csv.IndexOf(code)})";
        Equal(1, histogram.Count(line => line.Contains(expected, StringComparison.Ordinal)));
    }
}

void CheckControlsBeyondCap()
{
    string csv = "ID;Description\n1;première description\n2;texte avec\u000Ccontrôle\n3;autre\u000Ccontrôle\n";
    var logs = new List<string>();
    var r = NativeCsvImportService.Read(FileFor(csv), source, 3, maxRows: 1, diagnostics: logs.Add);
    Equal(1, r.Rows.Count); True(!r.Complete, "La limite doit rester appliquée malgré les contrôles acceptés.");
    True(logs.Any(line => line.Contains("U+000C×2", StringComparison.Ordinal)), "Les caractères après la limite doivent être comptés.");
    True(logs.Any(line => line.Contains("lignes source=3 ; lignes chargées=1", StringComparison.Ordinal)), "Le diagnostic doit distinguer le fichier et l’aperçu limité.");
}

void CheckNonTextDiagnostics()
{
    const string secret = "PRIVATE-NUL-CELL";
    string csv = "ID;Description\n1;" + secret + "\0valeur\0\n";
    string path = FileFor(csv);
    byte[] before = File.ReadAllBytes(path);
    var logs = new List<string>();
    var error = CaptureInvalid(() => NativeCsvImportService.Read(path, source, 1, diagnostics: logs.Add));
    True(error.Message.Contains("NUL U+0000", StringComparison.Ordinal), "Un NUL ne doit pas être considéré comme du simple texte compatible.");
    string diagnostic = string.Join('\n', logs) + error;
    True(diagnostic.Contains("U+0000×2 (premier offset " + csv.IndexOf('\0') + ")", StringComparison.Ordinal), "Le NUL doit avoir un diagnostic structurel précis.");
    True(!diagnostic.Contains(secret, StringComparison.Ordinal), "Le diagnostic de refus doit protéger les données aussi.");
    True(before.SequenceEqual(File.ReadAllBytes(path)), "Un refus doit conserver le fichier original.");
    string binary = Path.Combine(temporary, "non-text.csv");
    File.WriteAllBytes(binary, Enumerable.Repeat((byte)1, 256).ToArray());
    var nonText = CaptureInvalid(() => NativeCsvImportService.Read(binary, source));
    True(nonText.Message.Contains("proportion", StringComparison.Ordinal), "Un flux de contrôles sans texte ne doit pas passer comme un tableau vide à une colonne.");
    foreach (bool bigEndian in new[] { false, true })
    {
        string ambiguous = FileFor("ID;Nom\r\n1;Écran\r\n", new UnicodeEncoding(bigEndian, false, true));
        Throws<InvalidDataException>(() => NativeCsvImportService.Read(ambiguous, source));
    }
}

void CheckControlMarkupAndMalformed()
{
    foreach (string text in new[]
    {
        "\u000B\u000C\u0085<!doctype html><html><body>Connexion</body></html>",
        new string('\u000B', 5000) + "<html>Connexion</html>",
        "\u001B{\"error\":\"session\"}",
        "ID;Description\n1;\"texte\u000Bnon fermé\n",
        "ID;Description\n1;\"texte\"\u000C\n",
        "ID;Description\n1;texte\u0085;extra\n"
    }) Throws<InvalidDataException>(() => Import(text));
}

void CheckDiagnosticSummary()
{
    string csv = "sep=;\r\nID;Description;\r\n1;texte;\r\n2;autre;\r\n";
    var logs = new List<string>();
    string path = FileFor(csv, new UTF8Encoding(true, true));
    var r = NativeCsvImportService.Read(path, source, 2, diagnostics: logs.Add);
    True(r.Complete, "Le total doit rester confirmé sur un CSV sans contrôle inhabituel.");
    True(!r.Warnings.Any(w => w.Contains("contrôle", StringComparison.Ordinal)), "Pas d’avertissement pour les séparateurs CR/LF habituels.");
    string log = string.Join('\n', logs);
    True(log.Contains("taille=" + new FileInfo(path).Length + " octets", StringComparison.Ordinal), "La taille reçue doit être disponible.");
    True(log.Contains("encodage=UTF-8 ; BOM=UTF-8 (3 octets)", StringComparison.Ordinal), "L’encodage et le BOM doivent être identifiables.");
    True(log.Contains("séparateur=point-virgule (U+003B)", StringComparison.Ordinal), "Le séparateur doit être visible sans injecter le caractère de tabulation.");
    True(log.Contains("colonnes source=3 ; colonnes finales=2 ; colonne terminale retirée=oui", StringComparison.Ordinal), "La suppression documentée de la colonne GLPI vide doit être identifiable.");
    True(log.Contains("lignes source=2 ; lignes chargées=2", StringComparison.Ordinal) && log.Contains("total attendu=2 ; complet=oui", StringComparison.Ordinal), "Le bilan doit donner le compte réellement lu.");
}

void CheckLargeNativeCsv()
{
    const int total = 831;
    string prose = string.Concat(Enumerable.Repeat("Description synthétique de ticket. ", 40));
    var csv = new StringBuilder("ID;Titre;Description\r\n");
    for (int i = 1; i <= total; i++)
        csv.Append(i.ToString("D6")).Append(";Titre synthétique;\"").Append(prose).Append(i == 500 ? "\u0085\u009D" : "").Append("\"\r\n");
    string path = FileFor(csv.ToString(), new UTF8Encoding(true, true));
    byte[] before = File.ReadAllBytes(path);
    True(before.Length > 1_000_000, "Cette régression doit franchir les nombreux blocs de lecture d’un export d’environ un mégaoctet.");
    var r = NativeCsvImportService.Read(path, source, total);
    Equal(total, r.Rows.Count); Equal("000831", r.Rows[^1][0]);
    Equal(prose + "\u0085\u009D", r.Rows[499][2]); True(r.Complete, "Les 831 lignes synthétiques doivent être importées sans être remplacées par l’aperçu de la page.");
    True(before.SequenceEqual(File.ReadAllBytes(path)), "L’import volumineux ne doit pas réécrire le CSV natif.");
}

void CheckInvalidEncodingDiagnostics()
{
    string path = Path.Combine(temporary, "encoding-error.csv");
    File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF, 0x49, 0x44, 0x3B, 0xFF, 0x0A });
    var logs = new List<string>();
    var error = CaptureInvalid(() => NativeCsvImportService.Read(path, source, diagnostics: logs.Add));
    True(error.InnerException is null, "L’exception du décodeur peut exposer des octets du fichier et ne doit pas être propagée dans le diagnostic.");
    True(logs.Any(line => line.Contains("UTF-8 déclaré mais séquence invalide", StringComparison.Ordinal)), "L’échec d’encodage doit avoir une cause structurelle visible.");
    True(!string.Join('\n', logs).Contains("ID;", StringComparison.Ordinal), "Le diagnostic d’encodage ne doit pas copier le préfixe reçu.");
}

static InvalidDataException CaptureInvalid(Action action)
{
    try { action(); }
    catch (InvalidDataException exception) { return exception; }
    throw new Exception("Exception attendue : InvalidDataException");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Attendu : {expected}\nObtenu : {actual}");
}

static void True(bool condition, string reason)
{
    if (!condition) throw new Exception(reason);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Exception attendue : " + typeof(T).Name);
}
