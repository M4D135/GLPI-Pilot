using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

string temporary = Path.Combine(Path.GetTempPath(), "GlpiPilot-Configuration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var checks = new List<(string Name, Action Run)>
{
    ("Copie indépendante : sections de ticket et correspondances dashboard", CheckCopy),
    ("Villes : variantes, coordonnées et identifiants partagés avec copie indépendante", CheckCitiesRoundTrip),
    ("Villes : alias, coordonnées et identifiants invalides refusés avant réparation", CheckCitiesStrictImport),
    ("Villes : champs inconnus et dupliqués refusés à chaque niveau", CheckCitiesUnknownFields),
    ("Requêtes : filtres, groupes, favoris et références de widgets partagés", CheckQueriesRoundTrip),
    ("Requêtes : validation stricte des définitions avant normalisation", CheckQueriesStrictImport),
    ("Requêtes : champs inconnus et dupliqués refusés à chaque niveau", CheckQueriesUnknownFields),
    ("Anciennes configurations : villes et requêtes récupérées sans données synthétiques", CheckLocalAnalyticsMigration),
    ("Anciens réglages : objets, chaînes et collections null normalisés", CheckOldNulls),
    ("Export portable : filtres conservés, secrets et dossier local exclus", CheckPortableExport),
    ("Import portable : aucune mutation et dossier du poste conservé", CheckPortableImport),
    ("Ancien settings.json : migration sans reprendre le dossier distant", CheckLegacy),
    ("Schéma et version : formats inconnus et doublons refusés", CheckEnvelope),
    ("Champs inconnus ou non partageables : refus à chaque niveau", CheckUnknownFields),
    ("Valeurs bornées : délais, lignes, sections et sélecteurs", CheckBounds),
    ("Dashboard : clés, cultures et seuils cohérents", CheckDashboardValidation),
    ("Dispositions : styles, tailles, ordre et notes conservés dans le partage", CheckLayoutsRoundTrip),
    ("Dashboards global et personnel : partage, migration et copies indépendantes", CheckIndependentLayouts),
    ("Widgets : jauges, anneaux, objectifs et seuils partagés sans perte", CheckProbePresentationRoundTrip),
    ("Widgets : seuils non finis, incohérents ou hors limites refusés", CheckProbeThresholdValidation),
    ("Espace personnel : identité et ouverture automatique locales, exclues du partage", CheckPersonalTechnician),
    ("Dispositions importées : dimensions et nombres bornés avant normalisation", CheckLayoutBounds),
    ("Dispositions importées : identifiants uniques et tableau actif existant", CheckLayoutIdentity),
    ("Dispositions importées : types, styles et champs inconnus refusés", CheckLayoutTypes),
    ("Traitement du ticket : lecture auto et sections chronologiques partageables", CheckTreatmentConfiguration),
    ("Diagnostic : intitulés et correspondances sans cellules ni identité personnelle", CheckDiagnosticPrivacy),
    ("Fichier limité : JSON malformé, trop profond, vide et plus de 1 Mio", CheckFileLimits),
    ("Export atomique : échec sans remplacer un fichier existant", CheckAtomic),
    ("Identifiant : zéros initiaux et groupements français normalisés", CheckId),
    ("Colonne configurée : nom exact et unique requis", CheckConfiguredColumn),
    ("Identifiant ambigu : aucune déduction depuis un titre ou une autre colonne", CheckAmbiguousIds),
    ("Identifiant invalide : valeurs non numériques et zéro refusés", CheckInvalidIds),
    ("Provenance : même recherche et même origine GLPI obligatoires", CheckSource),
    ("Route standard : préfixe GLPI conservé et filtres non transmis à la fiche", CheckDefaultUrl),
    ("Modèle explicite : fiche GLPI et paramètres de lecture uniquement", CheckTemplate),
    ("Modèles dangereux ou ambigus : refus avant navigation", CheckUnsafeTemplates),
    ("Adresses portables : paramètres d’action refusés et authentification retirée", CheckPortableUrls)
};
int failures = 0;
try
{
    foreach (var check in checks)
    {
        try { check.Run(); Console.WriteLine("PASS " + check.Name); }
        catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + check.Name + "\n" + ex); }
    }
    Console.WriteLine($"{checks.Count - failures}/{checks.Count} vérifications de configuration réussies.");
}
finally { Directory.Delete(temporary, true); }
return failures == 0 ? 0 : 1;

AppSettings Sample() => new()
{
    PortalUrl = "https://glpi.example.test/glpi/front/ticket.php?criteria%5B0%5D%5Bfield%5D=12&criteria%5B0%5D%5Bvalue%5D=notold&start=20",
    ExportFolder = "C:\\PRIVATE-LOCAL-FOLDER\\Exports",
    TableSelector = "#search-results", TableHeaderFingerprint = "ID|Titre", TableFrameName = "tickets",
    TableFrameOrigin = "https://glpi.example.test", TableTargetUrl = "https://glpi.example.test/glpi/front/ticket.php?start=20",
    SelectorOrigin = "https://glpi.example.test", ReadySelector = "table.search-results", NextSelector = "a[rel=next]",
    NativeSelectorOrigin = "https://glpi.example.test", NativeMenuSelector = "#export", NativeFormatSelector = "select[name=display_type]",
    NativeFormatValue = "-3", NativeSubmitSelector = "input[type=image]",
    TicketDetails = new()
    {
        IdColumn = "ID", UrlTemplate = "https://glpi.example.test/glpi/front/ticket.form.php?id={id}",
        Sections = new() { new() { Name = "Historique", Selector = "#history", Kind = "table", TabSelector = "a[data-tab='Log$1']" } }
    },
    Dashboard = new()
    {
        ColumnMappings = new(StringComparer.OrdinalIgnoreCase) { ["id"] = "ID", ["title"] = "Titre", ["updated"] = "Dernière modification" },
        StaleDays = 7, OldDays = 30, DueSoonHours = 24, DateCulture = "fr-FR"
    }
};

string Write(string value)
{
    string path = Path.Combine(temporary, Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(path, value, new UTF8Encoding(false, true));
    return path;
}
ExtractionResult Source(params string[] headers) => new() { Headers = headers.ToList(), SourceUrl = "https://glpi.example.test/glpi/front/ticket.php" };
TicketReference Resolve(string id) => TicketReferenceResolver.Resolve(Source("ID", "Titre"), new[] { id, "Titre sans rôle dans l’identifiant" }, Sample());

void CheckCopy()
{
    var original = Sample();
    var copy = original.Copy();
    copy.TicketDetails.IdColumn = "Autre";
    copy.TicketDetails.Sections[0].Name = "Changé";
    copy.TicketDetails.Sections.Add(new() { Name = "Nouveau", Selector = "#new" });
    copy.Dashboard.ColumnMappings["ID"] = "Autre ID";
    copy.Dashboard.StaleDays = 12;
    Equal("ID", original.TicketDetails.IdColumn); Equal("Historique", original.TicketDetails.Sections[0].Name);
    Equal(1, original.TicketDetails.Sections.Count); Equal("ID", original.Dashboard.ColumnMappings["id"]); Equal(7, original.Dashboard.StaleDays);
    True(!ReferenceEquals(copy.TicketDetails.Sections[0], original.TicketDetails.Sections[0]), "Les définitions ne doivent pas partager leurs objets.");
}

void CheckOldNulls()
{
    var old = JsonSerializer.Deserialize<AppSettings>("{\"PortalUrl\":null,\"TicketDetails\":null,\"Dashboard\":null}")!;
    var copy = old.Copy();
    Equal("", copy.PortalUrl); True(copy.TicketDetails.AutoLoadOnSelection, "Les détails doivent avoir leur défaut de nouvelle version.");
    Equal(5000, copy.TicketDetails.MaxRowsPerSection); Equal(0, copy.TicketDetails.Sections.Count); Equal("fr-FR", copy.Dashboard.DateCulture);
    old.Normalize(); True(old.TicketDetails is not null && old.Dashboard is not null, "Load/Save peuvent normaliser une ancienne configuration.");
    var nested = JsonSerializer.Deserialize<AppSettings>("{\"TicketDetails\":{\"IdColumn\":null,\"Sections\":null},\"Dashboard\":{\"ColumnMappings\":null,\"DateCulture\":null}}")!;
    nested.Normalize(); Equal("", nested.TicketDetails.IdColumn); Equal(0, nested.Dashboard.ColumnMappings.Count);
}

void CheckPortableExport()
{
    var settings = Sample();
    settings.PortalUrl += "&access_token=PRIVATE-ACCESS&code=PRIVATE-CODE&PHPSESSID=PRIVATE-SESSION&password=PRIVATE-PASSWORD&_glpi_csrf_token=PRIVATE-CSRF";
    settings.TableTargetUrl += "&refresh_token=PRIVATE-REFRESH";
    settings.TicketDetails.UrlTemplate += "&id_token=PRIVATE-ID-TOKEN";
    string originalUrl = settings.PortalUrl;
    string path = Path.Combine(temporary, "portable.json");
    ConfigurationTransferService.Export(path, settings);
    string json = File.ReadAllText(path);
    foreach (string forbidden in new[] { "PRIVATE-", "ExportFolder", "Cookie", "BrowserProfile", "password", "access_token", "PHPSESSID", "id_token", "refresh_token" })
        True(!json.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "La configuration exportée ne doit pas contenir les données de session ni le dossier local.");
    using var document = JsonDocument.Parse(json);
    Equal("glpi-pilot.configuration", document.RootElement.GetProperty("Schema").GetString());
    Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32()); Equal("1.4.0", document.RootElement.GetProperty("AppVersion").GetString());
    string exportedUrl = document.RootElement.GetProperty("Settings").GetProperty("PortalUrl").GetString()!;
    True(exportedUrl.Contains("criteria%5B0%5D%5Bvalue%5D=notold", StringComparison.Ordinal) && exportedUrl.Contains("start=20", StringComparison.Ordinal), "Les critères et la position ne doivent pas être perdus.");
    Equal(originalUrl, settings.PortalUrl); True(settings.TicketDetails.UrlTemplate.Contains("PRIVATE-ID-TOKEN", StringComparison.Ordinal), "L’export ne doit pas modifier l’objet courant.");
}

void CheckPortableImport()
{
    string path = Path.Combine(temporary, "roundtrip.json");
    var exported = Sample();
    exported.Dashboard.ColumnMappings["source"] = " Source exacte ";
    exported.Dashboard.ColumnMappings["urgency"] = "Urgence";
    exported.Dashboard.ColumnMappings["impact"] = "Impact";
    exported.Dashboard.ColumnMappings["UPDATEDBY"] = "Dernière modification par";
    exported.Dashboard.ColumnMappings["solution"] = "Solution";
    exported.Dashboard.ColumnMappings["location"] = "Lieu";
    exported.Dashboard.ColumnMappings["city"] = "Ville";
    exported.Dashboard.ColumnMappings["postal"] = "Code postal";
    exported.TicketDetails.Sections.Add(new() { Name = "Résumé", Selector = "#summary", Kind = "fields" });
    ConfigurationTransferService.Export(path, exported);
    var current = new AppSettings { ExportFolder = "D:\\MY-LOCAL-EXPORTS", PortalUrl = "https://local.example.test/front/ticket.php" };
    var imported = ConfigurationTransferService.Import(path, current);
    Equal(current.ExportFolder, imported.ExportFolder); Equal(exported.PortalUrl, imported.PortalUrl);
    Equal(" Source exacte ", imported.Dashboard.ColumnMappings["SOURCE"]); Equal(2, imported.TicketDetails.Sections.Count);
    Equal("Urgence", imported.Dashboard.ColumnMappings["urgency"]); Equal("Impact", imported.Dashboard.ColumnMappings["impact"]);
    Equal("Dernière modification par", imported.Dashboard.ColumnMappings["updatedBy"]); Equal("Solution", imported.Dashboard.ColumnMappings["solution"]);
    Equal("Lieu", imported.Dashboard.ColumnMappings["location"]); Equal("Ville", imported.Dashboard.ColumnMappings["city"]);
    Equal("Code postal", imported.Dashboard.ColumnMappings["postal"]);
    True(imported.Dashboard.ColumnMappings.Keys.Contains("updatedBy", StringComparer.Ordinal), "Les rôles conservent leur casse canonique.");
    Equal("fields", imported.TicketDetails.Sections[1].Kind); Equal("https://local.example.test/front/ticket.php", current.PortalUrl);
    imported.Dashboard.ColumnMappings["id"] = "Changed"; imported.TicketDetails.Sections[0].Name = "Changed";
    Equal("ID", exported.Dashboard.ColumnMappings["id"]); Equal("Historique", exported.TicketDetails.Sections[0].Name);
    Equal(0, current.TicketDetails.Sections.Count);
}

void CheckLegacy()
{
    var current = new AppSettings { ExportFolder = "E:\\LOCAL" };
    string json = "{\"PortalUrl\":\"https://glpi.example.test/front/ticket.php?state=OLD-SECRET&start=0\",\"ExportFolder\":\"Z:\\\\OLD-MACHINE\",\"Theme\":\"light\",\"TicketDetails\":null,\"Dashboard\":null}";
    var imported = ConfigurationTransferService.Import(Write(json), current);
    Equal("E:\\LOCAL", imported.ExportFolder); Equal("light", imported.Theme); Equal(180, imported.TimeoutSeconds);
    Equal("https://glpi.example.test/front/ticket.php?start=0", imported.PortalUrl);
    Equal(5000, imported.TicketDetails.MaxRowsPerSection); Equal(30, imported.Dashboard.OldDays);
}

void CheckEnvelope()
{
    foreach (string json in new[]
    {
        "{\"Schema\":\"wrong\",\"SchemaVersion\":1,\"AppVersion\":\"1.2.0\",\"Settings\":{}}",
        "{\"Schema\":\"glpi-pilot.configuration\",\"SchemaVersion\":2,\"AppVersion\":\"1.2.0\",\"Settings\":{}}",
        "{\"Schema\":\"glpi-pilot.configuration\",\"SchemaVersion\":1,\"AppVersion\":\"2.0.0\",\"Settings\":{}}",
        "{\"PortalUrl\":\"https://glpi.test/front/ticket.php\",\"portalurl\":\"https://other.test\"}",
        "{\"Dashboard\":{\"ColumnMappings\":{\"id\":\"ID\",\"ID\":\"Autre\"}}}"
    }) Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(json), Sample()));
}

void CheckUnknownFields()
{
    foreach (string json in new[]
    {
        "{\"PortalUrl\":\"https://glpi.test/front/ticket.php\",\"Cookies\":[\"secret\"]}",
        "{\"PortalUrl\":\"https://glpi.test/front/ticket.php\",\"Profile\":\"private\"}",
        "{\"TicketDetails\":{\"Script\":\"alert(1)\"}}",
        "{\"TicketDetails\":{\"Sections\":[{\"Selector\":\"#x\",\"OnClick\":\"delete\"}]}}",
        "{\"Dashboard\":{\"WeatherUrl\":\"https://external.example.test\"}}",
        "{\"Schema\":\"glpi-pilot.configuration\",\"SchemaVersion\":1,\"AppVersion\":\"1.2.0\",\"Settings\":{\"ExportFolder\":\"Z:/remote\"}}"
    }) Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(json), Sample()));
}

void CheckBounds()
{
    foreach (string json in new[]
    {
        "{\"TimeoutSeconds\":0}", "{\"TimeoutSeconds\":1801}", "{\"DownloadTimeoutSeconds\":7201}",
        "{\"MaxPages\":10001}", "{\"MaxRows\":1000001}", "{\"ExpectedTotal\":-1}",
        "{\"BrowserChannel\":\"firefox\"}", "{\"Theme\":\"unknown\"}", "{\"AutoMode\":\"script\"}",
        "{\"TicketDetails\":{\"MaxRowsPerSection\":100001}}", "{\"TicketDetails\":{\"Sections\":[null]}}",
        "{\"TicketDetails\":{\"Sections\":[{\"Selector\":\"#x\",\"Kind\":\"script\"}]}}",
        JsonSerializer.Serialize(new { ReadySelector = new string('x', 2049) }),
        JsonSerializer.Serialize(new { ReadySelector = "javascript=alert(1)" }),
        JsonSerializer.Serialize(new { TicketDetails = new { Sections = Enumerable.Range(1, 21).Select(i => new { Selector = "#t" + i }).ToArray() } })
    }) Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(json), Sample()));
}

void CheckDashboardValidation()
{
    foreach (string json in new[]
    {
        "{\"Dashboard\":{\"StaleDays\":0}}", "{\"Dashboard\":{\"OldDays\":3651}}",
        "{\"Dashboard\":{\"StaleDays\":30,\"OldDays\":7}}", "{\"Dashboard\":{\"DueSoonHours\":721}}",
        "{\"Dashboard\":{\"DateCulture\":\"de-DE\"}}", "{\"Dashboard\":{\"ColumnMappings\":{\"unknown\":\"Titre\"}}}",
        JsonSerializer.Serialize(new { Dashboard = new { ColumnMappings = new Dictionary<string, string> { ["id"] = new string('x', 513) } } })
    }) Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(json), Sample()));
    foreach (string culture in new[] { "fr-FR", "en-GB", "en-US", "invariant" })
    {
        var r = ConfigurationTransferService.Import(Write(JsonSerializer.Serialize(new { Dashboard = new { DateCulture = culture } })), Sample());
        Equal(culture, r.Dashboard.DateCulture);
    }
}

void CheckFileLimits()
{
    foreach (string text in new[] { "", "{", "[]", "{}", "{\"TimeoutSeconds\":\"180\"}", new string('[', 30) + "0" + new string(']', 30) })
        Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(text), Sample()));
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(new string(' ', ConfigurationTransferService.MaximumFileBytes + 1)), Sample()));
    string bom = Path.Combine(temporary, "bom.json"); File.WriteAllText(bom, "{\"Theme\":\"light\"}", new UTF8Encoding(true));
    Equal("light", ConfigurationTransferService.Import(bom, Sample()).Theme);
}

void CheckAtomic()
{
    string path = Write("FICHIER EXISTANT");
    var invalid = Sample(); invalid.MaxRows = 0;
    Throws<InvalidDataException>(() => ConfigurationTransferService.Export(path, invalid));
    Equal("FICHIER EXISTANT", File.ReadAllText(path));
    string folder = Path.Combine(temporary, "target.json"); Directory.CreateDirectory(folder);
    Throws<IOException>(() => ConfigurationTransferService.Export(folder, Sample()));
    Equal(0, Directory.GetFiles(temporary, ".glpi-configuration-*.tmp").Length);
}

void CheckId()
{
    Equal("42", Resolve("00042").Id); Equal("1234", Resolve("1 234").Id);
    Equal("1234567", Resolve("1\u202F234\u00A0567").Id); Equal("1234", Resolve("001\u2007234").Id);
    var settings = Sample(); settings.TicketDetails.IdColumn = "";
    Equal("7", TicketReferenceResolver.Resolve(Source("Identifiant", "Titre"), new[] { "0007", "Titre" }, settings).Id);
    Equal("8", TicketReferenceResolver.Resolve(Source("Ticket ID", "Titre"), new[] { "8", "Titre" }, settings).Id);
}

void CheckConfiguredColumn()
{
    var settings = Sample(); settings.TicketDetails.IdColumn = "Numéro exact";
    Equal("42", TicketReferenceResolver.Resolve(Source("ID", "Numéro exact"), new[] { "100", "42" }, settings).Id);
    settings.TicketDetails.IdColumn = "id";
    Throws<InvalidOperationException>(() => TicketReferenceResolver.Resolve(Source("ID"), new[] { "42" }, settings));
    settings.TicketDetails.IdColumn = "ID";
    Throws<InvalidOperationException>(() => TicketReferenceResolver.Resolve(Source("ID", "ID"), new[] { "42", "43" }, settings));
}

void CheckAmbiguousIds()
{
    var settings = Sample(); settings.TicketDetails.IdColumn = "";
    Throws<InvalidOperationException>(() => TicketReferenceResolver.Resolve(Source("ID", "Ticket ID"), new[] { "42", "42" }, settings));
    Throws<InvalidOperationException>(() => TicketReferenceResolver.Resolve(Source("Titre"), new[] { "Ticket 42 : panne" }, settings));
    Throws<InvalidOperationException>(() => TicketReferenceResolver.Resolve(Source("ID", "Titre"), new[] { "42" }, settings));
}

void CheckInvalidIds()
{
    foreach (string value in new[] { "", "0", "000", "-1", "+42", "1.0", "1e3", "12 34", "1\t234", "Ticket 42", "#42", "٤٢", "42&delete=1", "18446744073709551616" })
        Throws<InvalidOperationException>(() => Resolve(value));
}

void CheckSource()
{
    foreach (string url in new[] { "", "https://other.example.test/glpi/front/ticket.php", "http://glpi.example.test/glpi/front/ticket.php",
        "https://glpi.example.test:444/glpi/front/ticket.php", "https://glpi.example.test/other/front/ticket.php",
        "https://name:secret@glpi.example.test/glpi/front/ticket.php", "file:///C:/ticket.csv" })
    {
        var data = Source("ID"); data.SourceUrl = url;
        Throws<InvalidOperationException>(() => TicketReferenceResolver.Resolve(data, new[] { "42" }, Sample()));
    }
}

void CheckDefaultUrl()
{
    var settings = Sample(); settings.TicketDetails.UrlTemplate = "";
    var reference = TicketReferenceResolver.Resolve(Source("ID"), new[] { "42" }, settings);
    Equal("https://glpi.example.test/glpi/front/ticket.form.php?id=42", reference.Url);
    var other = Source("ID"); other.SourceUrl = "https://glpi.example.test/glpi/front/computer.php"; settings.PortalUrl = other.SourceUrl;
    Throws<InvalidOperationException>(() => TicketReferenceResolver.Resolve(other, new[] { "42" }, settings));
}

void CheckTemplate()
{
    var settings = Sample(); settings.PortalUrl = "https://glpi.example.test/glpi/dashboard";
    var data = Source("ID"); data.SourceUrl = settings.PortalUrl;
    settings.TicketDetails.UrlTemplate = "https://glpi.example.test/glpi/front/ticket.form.php?id={id}&forcetab=Log$1";
    var reference = TicketReferenceResolver.Resolve(data, new[] { "42" }, settings);
    Equal("https://glpi.example.test/glpi/front/ticket.form.php?id=42&forcetab=Log%241", reference.Url);
}

void CheckUnsafeTemplates()
{
    foreach (string template in new[]
    {
        "https://evil.test/glpi/front/ticket.form.php?id={id}",
        "javascript:alert({id})", "https://user:secret@glpi.example.test/glpi/front/ticket.form.php?id={id}",
        "https://glpi.example.test/glpi/front/ticket.form.php?id={id}&delete=1",
        "https://glpi.example.test/glpi/front/ticket.form.php?id={id}&%70urge=1",
        "https://glpi.example.test/glpi/front/ticket.form.php?id={id}&update=1",
        "https://glpi.example.test/glpi/front/ticket.form.php?id={id}&action=read",
        "https://glpi.example.test/glpi/front/ticket.form.php?id={id}&foo=1",
        "https://glpi.example.test/glpi/front/ticket.form.php?id={id}&id=2",
        "https://glpi.example.test/glpi/front/ticket.form.php?id=1&forcetab={id}",
        "https://glpi.example.test/glpi/front/ticket.form.php?id={id}&forcetab={tab}",
        "https://glpi.example.test/glpi/front/ticket.form.php?id={id}#tab",
        "https://glpi.example.test/other/front/ticket.form.php?id={id}",
        "https://glpi.example.test/glpi/front/delete.php?id={id}"
    })
    {
        var settings = Sample(); settings.TicketDetails.UrlTemplate = template;
        Throws<InvalidOperationException>(() => TicketReferenceResolver.Resolve(Source("ID"), new[] { "42" }, settings));
    }
}

void CheckPortableUrls()
{
    foreach (string url in new[] { "javascript:alert(1)", "https://user:secret@glpi.test/front/ticket.php", "https://glpi.test/front/ticket.php?delete=1", "https://glpi.test/front/ticket.php?%61ction=update" })
        Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(JsonSerializer.Serialize(new { PortalUrl = url })), Sample()));
    string source = "https://glpi.test/front/ticket.php?criteria[0][value][]=1&criteria[0][value][]=2&auth[token]=PRIVATE-TOKEN&state=PRIVATE-STATE#access_token=PRIVATE-HASH";
    var imported = ConfigurationTransferService.Import(Write(JsonSerializer.Serialize(new { PortalUrl = source })), Sample());
    True(imported.PortalUrl.Contains("value%5D%5B%5D=1", StringComparison.Ordinal) || imported.PortalUrl.Contains("value][]=1", StringComparison.Ordinal), "Les valeurs de filtre répétées doivent être conservées.");
    True(imported.PortalUrl.Contains("=2", StringComparison.Ordinal), "La seconde valeur répétée doit rester dans la recette.");
    True(!imported.PortalUrl.Contains("PRIVATE", StringComparison.Ordinal), "Les paramètres d’authentification connus doivent être retirés, y compris les clés imbriquées.");
}

void CheckLayoutsRoundTrip()
{
    var settings = Sample();
    settings.Dashboard.Layout = new()
    {
        Version = 1, ActiveBoardId = "board-analysis",
        Boards = new()
        {
            new() { Id = "board-analysis", Name = "Analyse du relevé", Widgets = new()
            {
                new() { Id = "metric-one", Kind = "metric", SourceKey = "active", Title = "Ma charge", Span = 4, Height = 220, Accent = "teal", Style = "outline", Presentation = "compact", Locked = true },
                new() { Id = "note-one", Kind = "note", Title = "Consignes", Note = "Texte libre\nDeuxième ligne", Span = 8, Height = 320, Accent = "purple", Style = "accent", Hidden = true },
                new() { Id = "status-bars", Kind = "distribution", SourceKey = "status", Span = 6, Height = 480, TopCount = 15, Presentation = "list", ShowDescription = false }
            } },
            new() { Id = "board-empty", Name = "À personnaliser", Widgets = new() }
        }
    };
    string path = Path.Combine(temporary, "layout-roundtrip.json");
    ConfigurationTransferService.Export(path, settings);
    var imported = ConfigurationTransferService.Import(path, Sample());
    var board = imported.Dashboard.Layout.Boards[0];
    Equal(2, imported.Dashboard.Layout.Boards.Count); Equal("board-analysis", imported.Dashboard.Layout.ActiveBoardId);
    Equal("metric-one", board.Widgets[0].Id); Equal("Ma charge", board.Widgets[0].Title); Equal("teal", board.Widgets[0].Accent);
    Equal("outline", board.Widgets[0].Style); Equal(4, board.Widgets[0].Span); Equal(220, board.Widgets[0].Height);
    True(board.Widgets[0].Locked, "Le verrouillage doit être partagé.");
    Equal("Texte libre\nDeuxième ligne", board.Widgets[1].Note); True(board.Widgets[1].Hidden, "Un widget masqué reste dans la disposition.");
    Equal("list", board.Widgets[2].Presentation); Equal(15, board.Widgets[2].TopCount); True(!board.Widgets[2].ShowDescription, "L’option d’explication est conservée.");
    var copied = imported.Copy(); copied.Dashboard.Layout.Boards[0].Widgets[0].Title = "Modifié";
    Equal("Ma charge", imported.Dashboard.Layout.Boards[0].Widgets[0].Title);
    True(!ReferenceEquals(copied.Dashboard.Layout.Boards[0].Widgets, imported.Dashboard.Layout.Boards[0].Widgets), "La disposition doit être copiée profondément.");
    var old = ConfigurationTransferService.Import(Write("{\"Dashboard\":{\"StaleDays\":7}}"), Sample());
    True(old.Dashboard.Layout.Boards.Count > 0, "Un ancien réglage doit recevoir une disposition utilisable.");
}

void CheckTreatmentConfiguration()
{
    var settings = Sample();
    settings.TicketDetails.AutoReadTreatment = false;
    settings.TicketDetails.Sections.Add(new() { Name = "Traitement", Selector = ".timeline_history", Kind = "timeline", TabSelector = "a[data-tab='Ticket$1']" });
    string path = Path.Combine(temporary, "treatment-portable.json");
    ConfigurationTransferService.Export(path, settings);
    var imported = ConfigurationTransferService.Import(path, Sample());
    True(!imported.TicketDetails.AutoReadTreatment, "Le choix de lecture automatique doit rester explicite.");
    Equal("timeline", imported.TicketDetails.Sections[^1].Kind); Equal(".timeline_history", imported.TicketDetails.Sections[^1].Selector);
    var old = ConfigurationTransferService.Import(Write("{\"PortalUrl\":\"https://glpi.test/front/ticket.php\"}"), Sample());
    True(old.TicketDetails.AutoReadTreatment, "Une ancienne configuration doit activer par défaut la lecture du traitement.");
}

void CheckDiagnosticPrivacy()
{
    var settings = Sample(); settings.Dashboard.PersonalTechnician = "PRIVATE-TECHNICIAN"; settings.Dashboard.Normalize();
    settings.Dashboard.Layout.Boards[0].Name = "PRIVATE-BOARD-NAME";
    settings.Cities.Cities.Add(new() { Name = "PRIVATE-CITY-CONFIG", Aliases = new() { "PRIVATE-ALIAS" }, PostalCode = "PRIVATE-POSTAL" });
    settings.Queries.Queries.Add(new() { Name = "PRIVATE-QUERY", Conditions = new() { new() { Field = "role:title", Operator = "contains", Value = "PRIVATE-CRITERION" } } });
    var result = new ExtractionResult
    {
        Headers = new() { "ID", "Titre", "Lieu - Ville", "Test\npassword=PRIVATE-SECRET", "Code\u0085postal" },
        Rows = new() { new[] { "42", "PRIVATE-TICKET-TEXT", "PRIVATE-CITY", "PRIVATE-VALUE", "99999" } },
        Complete = true, ExpectedTotal = 1
    };
    string report = DiagnosticReport.Create(settings, result, "Connexion https://glpi.example.test/front/ticket.php?access_token=PRIVATE-TOKEN\nOK");
    True(report.Contains("COLONNE 3 : Lieu - Ville", StringComparison.Ordinal), "Les vrais intitulés sont nécessaires pour corriger une correspondance.");
    True(report.Contains("CORRESPONDANCE city : auto · colonne=3", StringComparison.Ordinal), "Le diagnostic indique la correspondance et l’index.");
    True(report.Contains("\\u0085", StringComparison.Ordinal), "Les contrôles de l’intitulé sont affichés comme codes.");
    True(!report.Contains("PRIVATE-", StringComparison.Ordinal), "Aucun texte de ticket, identité personnelle, nom de dashboard ou jeton ne doit être copié.");
    True(!report.Contains("Test\npassword", StringComparison.Ordinal), "Un intitulé ne peut pas injecter une ligne de diagnostic.");
}

void CheckPersonalTechnician()
{
    True(!new DashboardSettings().OpenPersonalAfterExtraction, "L’ouverture automatique doit rester désactivée par défaut.");
    var sender = Sample(); sender.Dashboard.PersonalTechnician = "PRIVATE-TECHNICIAN-SENDER";
    sender.Dashboard.OpenPersonalAfterExtraction = true;
    var recipient = Sample(); recipient.Dashboard.PersonalTechnician = "Local Technician";
    string path = Path.Combine(temporary, "personal-excluded.json");
    ConfigurationTransferService.Export(path, sender);
    var json = File.ReadAllText(path);
    True(!json.Contains("PRIVATE-TECHNICIAN-SENDER", StringComparison.Ordinal) && !json.Contains("PersonalTechnician", StringComparison.Ordinal), "L’identité personnelle ne doit pas accompagner les widgets partagés.");
    True(!json.Contains("OpenPersonalAfterExtraction", StringComparison.Ordinal), "Le choix d’ouverture automatique doit rester sur son poste.");
    var imported = ConfigurationTransferService.Import(path, recipient);
    Equal("Local Technician", imported.Dashboard.PersonalTechnician);
    True(!imported.Dashboard.OpenPersonalAfterExtraction, "L’import ne doit pas activer le choix du destinataire.");
    recipient.Dashboard.OpenPersonalAfterExtraction = true;
    True(ConfigurationTransferService.Import(path, recipient).Dashboard.OpenPersonalAfterExtraction, "L’import doit conserver le choix local activé.");
    var legacy = ConfigurationTransferService.Import(Write("{\"Dashboard\":{\"PersonalTechnician\":\"Other Technician\",\"OpenPersonalAfterExtraction\":false}}"), recipient);
    Equal("Local Technician", legacy.Dashboard.PersonalTechnician);
    True(legacy.Dashboard.OpenPersonalAfterExtraction, "L’ancien settings.json ne doit pas imposer sa préférence au destinataire.");
    recipient.Dashboard.OpenPersonalAfterExtraction = false;
    True(!ConfigurationTransferService.Import(Write("{\"Dashboard\":{\"OpenPersonalAfterExtraction\":true}}"), recipient).Dashboard.OpenPersonalAfterExtraction,
        "Une ancienne configuration activée ne doit pas activer le poste destinataire.");
    True(!ConfigurationTransferService.Import(Write("{\"Dashboard\":{}}"), recipient).Dashboard.OpenPersonalAfterExtraction,
        "Une ancienne configuration dépourvue du champ conserve le choix local désactivé.");
    var localRoundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(sender))!;
    True(localRoundTrip.Dashboard.OpenPersonalAfterExtraction, "La sauvegarde locale doit conserver l’option.");
    var copy = sender.Copy();
    True(copy.Dashboard.OpenPersonalAfterExtraction, "La copie de travail doit reprendre l’option locale.");
    copy.Dashboard.OpenPersonalAfterExtraction = false;
    True(sender.Dashboard.OpenPersonalAfterExtraction, "Modifier une copie ne doit pas changer le réglage courant.");
    True(!recipient.Dashboard.OpenPersonalAfterExtraction, "L’import ne doit pas modifier l’objet de configuration du destinataire.");
    Equal("PRIVATE-TECHNICIAN-SENDER", sender.Dashboard.PersonalTechnician);
    var portableIdentity = "{\"Schema\":\"glpi-pilot.configuration\",\"SchemaVersion\":1,\"AppVersion\":\"1.3.0\",\"Settings\":{\"Dashboard\":{\"PersonalTechnician\":\"Other\"}}}";
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(portableIdentity), recipient));
    var portablePreference = "{\"Schema\":\"glpi-pilot.configuration\",\"SchemaVersion\":1,\"AppVersion\":\"1.4.0\",\"Settings\":{\"Dashboard\":{\"OpenPersonalAfterExtraction\":true}}}";
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(portablePreference), recipient));
    var oldLocal = JsonSerializer.Deserialize<AppSettings>("{\"Dashboard\":{}}")!;
    True(!oldLocal.Dashboard.OpenPersonalAfterExtraction, "Lire d’anciens réglages locaux ne doit pas activer la navigation automatique.");
}

void CheckIndependentLayouts()
{
    var old = ConfigurationTransferService.Import(Write(LayoutJson()), Sample());
    Equal(old.Dashboard.Layout.ActiveBoardId, old.Dashboard.PersonalLayout.ActiveBoardId);
    old.Dashboard.PersonalLayout.Boards[0].Name = "Mon quotidien";
    old.Dashboard.PersonalLayout.Boards[0].Widgets[0].Title = "Personnel";
    Equal("Board", old.Dashboard.Layout.Boards[0].Name);
    True(old.Dashboard.Layout.Boards[0].Widgets[0].Title != "Personnel", "La migration ne partage aucune instance de widget.");
    var path = Path.Combine(temporary, "independent-layouts.json");
    ConfigurationTransferService.Export(path, old);
    var imported = ConfigurationTransferService.Import(path, Sample());
    Equal("Board", imported.Dashboard.Layout.Boards[0].Name);
    Equal("Mon quotidien", imported.Dashboard.PersonalLayout.Boards[0].Name);
    var copied = imported.Copy();
    copied.Dashboard.PersonalLayout.Boards[0].Widgets.Clear();
    True(imported.Dashboard.PersonalLayout.Boards[0].Widgets.Count > 0, "Copier les réglages préserve les widgets personnels d’origine.");
    var invalidPersonal = LayoutJson(new object[] { new { Id = "w", Span = 13 } }).Replace("\"Layout\"", "\"PersonalLayout\"", StringComparison.Ordinal);
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(invalidPersonal), Sample()));
}

void CheckProbePresentationRoundTrip()
{
    var imported = ConfigurationTransferService.Import(Write(LayoutJson(new object[]
    {
        new { Id = "goal", Kind = "metric", Presentation = "gauge", SourceKey = "active", TargetValue = 50.5, WarningValue = 40.25, CriticalValue = 60.75, LowerIsBetter = true },
        new { Id = "ring", Kind = "distribution", Presentation = "donut", SourceKey = "status" },
        new { Id = "coverage", Kind = "metric", Presentation = "gauge", SourceKey = "completed-share", TargetValue = 100, WarningValue = 80, CriticalValue = 50, LowerIsBetter = false }
    })), Sample());
    var path = Path.Combine(temporary, "probe-presentation.json");
    ConfigurationTransferService.Export(path, imported);
    var restored = ConfigurationTransferService.Import(path, Sample());
    var widgets = restored.Dashboard.Layout.Boards[0].Widgets;
    Equal("gauge", widgets[0].Presentation); Equal(50.5, widgets[0].TargetValue!.Value);
    Equal(40.25, widgets[0].WarningValue!.Value); Equal(60.75, widgets[0].CriticalValue!.Value);
    Equal("donut", widgets[1].Presentation); True(!widgets[2].LowerIsBetter, "Le sens de comparaison doit être conservé.");
    True(widgets[1].TargetValue == null, "Un objectif absent reste absent.");
}

void CheckProbeThresholdValidation()
{
    foreach (object widget in new object[]
    {
        new { Id = "w", TargetValue = 0 }, new { Id = "w", TargetValue = -1 }, new { Id = "w", TargetValue = 1e13 },
        new { Id = "w", WarningValue = -1 }, new { Id = "w", CriticalValue = 1e13 }, new { Id = "w", WarningValue = "12" },
        new { Id = "w", WarningValue = 20, CriticalValue = 10, LowerIsBetter = true },
        new { Id = "w", WarningValue = 20, CriticalValue = 30, LowerIsBetter = false }, new { Id = "w", LowerIsBetter = "false" }
    }) Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(LayoutJson(new[] { widget })), Sample()));
    var overflow = LayoutJson(new object[] { new { Id = "w", TargetValue = 7 } }).Replace("\"TargetValue\":7", "\"TargetValue\":1e309", StringComparison.Ordinal);
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(overflow), Sample()));
    var noTarget = ConfigurationTransferService.Import(Write(LayoutJson(new object[] { new { Id = "w", Presentation = "gauge" } })), Sample());
    True(noTarget.Dashboard.Layout.Boards[0].Widgets[0].TargetValue == null, "Une jauge en cours de configuration peut ne pas avoir d’objectif.");
}

string LayoutJson(object[]? widgets = null, object[]? boards = null, string active = "b") => JsonSerializer.Serialize(new
{
    Dashboard = new { Layout = new { Version = 1, ActiveBoardId = active, Boards = boards ?? new object[]
    {
        new { Id = "b", Name = "Board", Widgets = widgets ?? new object[] { new { Id = "w", Kind = "weather", Span = 6, Height = 320, TopCount = 8 } } }
    } } }
});

void CheckLayoutBounds()
{
    foreach (object widget in new object[]
    {
        new { Id = "w", Span = 2 }, new { Id = "w", Span = 13 }, new { Id = "w", Span = 6.5 },
        new { Id = "w", Height = 159 }, new { Id = "w", Height = 961 }, new { Id = "w", TopCount = 2 }, new { Id = "w", TopCount = 31 },
        new { Id = "w", Title = new string('x', 181) }, new { Id = "w", Note = new string('x', 4001) },
        new { Id = "w", Note = "nul\0in-note" }, new { Id = "w", SourceKey = new string('x', 121) }
    }) Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(LayoutJson(new[] { widget })), Sample()));
    object[] tooManyWidgets = Enumerable.Range(0, 61).Select(i => (object)new { Id = "w" + i, Kind = "metric" }).ToArray();
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(LayoutJson(tooManyWidgets)), Sample()));
    object[] tooManyBoards = Enumerable.Range(0, 13).Select(i => (object)new { Id = "b" + i, Name = "Board", Widgets = Array.Empty<object>() }).ToArray();
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(LayoutJson(boards: tooManyBoards, active: "b0")), Sample()));
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(LayoutJson(boards: Array.Empty<object>())), Sample()));
}

void CheckLayoutIdentity()
{
    object[] duplicateWidgets = { new { Id = "w", Kind = "metric" }, new { Id = "w", Kind = "weather" } };
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(LayoutJson(duplicateWidgets)), Sample()));
    object[] duplicateBoards = { new { Id = "b", Name = "One", Widgets = Array.Empty<object>() }, new { Id = "b", Name = "Two", Widgets = Array.Empty<object>() } };
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(LayoutJson(boards: duplicateBoards)), Sample()));
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(LayoutJson(active: "absent")), Sample()));
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(LayoutJson(new object[] { new { Id = " " } })), Sample()));
    Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(LayoutJson(new object[] { new { Kind = "weather" } })), Sample()));
}

void CheckLayoutTypes()
{
    foreach (object widget in new object[]
    {
        new { Id = "w", Kind = "script" }, new { Id = "w", Style = "unknown" }, new { Id = "w", Accent = "#bad" },
        new { Id = "w", Presentation = "unknown" }, new { Id = "w", Hidden = "true" }, new { Id = "w", ShowDescription = 1 },
        new { Id = "w", OnClick = "delete" }, new { Id = "w", Html = "<script>test</script>" }
    }) Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(LayoutJson(new[] { widget })), Sample()));
    foreach (string json in new[] { "{\"Dashboard\":{\"Layout\":{\"Version\":2}}}", "{\"Dashboard\":{\"Layout\":{\"Boards\":null}}}", "{\"Dashboard\":{\"Layout\":{\"Script\":\"x\"}}}" })
        Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(json), Sample()));
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Attendu : {expected}\nObtenu : {actual}");
}
static void True(bool condition, string reason) { if (!condition) throw new Exception(reason); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Exception attendue : " + typeof(T).Name);
}

void CheckCitiesRoundTrip()
{
    var settings = Sample();
    var city = new CityDefinition { Name = "Marseille", Aliases = new() { "MA", "Mars", "Marseille" }, PostalCode = "13000", Latitude = 43.3, Longitude = 5.4 };
    settings.Cities = new() { TitleColumn = " Titre exact ", Cities = new() { city } };
    var copy = settings.Copy(); copy.Cities.Cities[0].Aliases.Add("MRS");
    Equal(3, settings.Cities.Cities[0].Aliases.Count);
    var path = Path.Combine(temporary, "cities.json");
    ConfigurationTransferService.Export(path, settings);
    var imported = ConfigurationTransferService.Import(path, Sample());
    Equal(" Titre exact ", imported.Cities.TitleColumn); Equal(city.Id, imported.Cities.Cities[0].Id);
    Equal("Marseille", imported.Cities.Cities[0].Name); Equal("MA", imported.Cities.Cities[0].Aliases[0]);
    Equal("13000", imported.Cities.Cities[0].PostalCode); Equal(43.3, imported.Cities.Cities[0].Latitude!.Value);
    Equal(5.4, imported.Cities.Cities[0].Longitude!.Value);
}

void CheckCitiesStrictImport()
{
    CitySettings Valid() => new() { Cities = new() { new() { Name = "Marseille", Aliases = new() { "MA", "Mars" } } } };
    var invalid = new List<CitySettings>();
    var aliases = Valid(); aliases.Cities[0].Aliases = Enumerable.Range(0, 13).Select(i => "a" + i).ToList(); invalid.Add(aliases);
    var tooLong = Valid(); tooLong.Cities[0].Aliases[0] = new string('A', 121); invalid.Add(tooLong);
    var noName = Valid(); noName.Cities[0].Name = ""; invalid.Add(noName);
    var coordinates = Valid(); coordinates.Cities[0].Latitude = 91; coordinates.Cities[0].Longitude = 5; invalid.Add(coordinates);
    var pair = Valid(); pair.Cities[0].Latitude = 43; invalid.Add(pair);
    var duplicate = Valid(); duplicate.Cities.Add(duplicate.Cities[0].Copy()); invalid.Add(duplicate);
    var invalidId = Valid(); invalidId.Cities[0].Id = "not-a-guid"; invalid.Add(invalidId);
    foreach (var cities in invalid)
    {
        Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(JsonSerializer.Serialize(new { Cities = cities })), Sample()));
        var settings = Sample(); settings.Cities = cities;
        Throws<InvalidDataException>(() => ConfigurationTransferService.Export(Path.Combine(temporary, "invalid-city-export.json"), settings));
    }
}

void CheckCitiesUnknownFields()
{
    var id = Guid.NewGuid().ToString("N");
    foreach (var json in new[]
    {
        "{\"Cities\":{\"GeocoderUrl\":\"https://example.test\"}}",
        "{\"Cities\":{\"Enabled\":true,\"enabled\":false}}",
        "{\"Cities\":{\"Cities\":[{\"Id\":\"" + id + "\",\"Name\":\"Marseille\",\"OnMatch\":\"script\"}]}}",
        "{\"Cities\":{\"Cities\":[{\"Id\":\"" + id + "\",\"Name\":\"Marseille\",\"name\":\"Lyon\"}]}}"
    }) Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(json), Sample()));
}

SavedQuery ExampleQuery() => new()
{
    Name = "Charge de Marseille", Description = "Tickets actifs de la ville sélectionnée.", Category = "Villes", Favorite = true,
    BaseProbe = "active", GroupBy = "role:assignee", Aggregate = "count", Match = "all",
    Conditions = new() { new() { Field = "role:city", Operator = "equals", Value = "Marseille" } }
};

void CheckQueriesRoundTrip()
{
    var settings = Sample(); var query = ExampleQuery();
    settings.Queries = new() { Queries = new() { query } };
    settings.Dashboard.Layout.Normalize();
    settings.Dashboard.Layout.Boards[0].Widgets.Add(new() { SourceKey = QueryService.MetricKey(query.Id) });
    var copy = settings.Copy(); copy.Queries.Queries[0].Conditions[0].Value = "Lyon";
    Equal("Marseille", settings.Queries.Queries[0].Conditions[0].Value);
    var path = Path.Combine(temporary, "queries.json"); ConfigurationTransferService.Export(path, settings);
    var imported = ConfigurationTransferService.Import(path, Sample());
    Equal(1, imported.Queries.Queries.Count); var restored = imported.Queries.Queries[0];
    Equal(query.Id, restored.Id); Equal("role:assignee", restored.GroupBy); Equal("active", restored.BaseProbe);
    Equal("Marseille", restored.Conditions[0].Value); Equal("equals", restored.Conditions[0].Operator);
    True(restored.Favorite, "Les favoris de requêtes sont partageables.");
    True(imported.Dashboard.Layout.Boards[0].Widgets.Any(w => w.SourceKey == QueryService.MetricKey(query.Id)), "Le widget garde sa référence de requête.");
}

void CheckQueriesStrictImport()
{
    var invalid = new List<QueryLibrarySettings>();
    void Bad(Action<SavedQuery> change) { var query = ExampleQuery(); change(query); invalid.Add(new() { Queries = new() { query } }); }
    Bad(q => q.Id = "bad-id"); Bad(q => q.Name = ""); Bad(q => q.BaseProbe = "query:recursive");
    Bad(q => q.Conditions[0].Operator = "javascript"); Bad(q => q.Conditions[0].Field = "sql:SELECT *");
    Bad(q => q.Conditions[0].Value = new string('x', 501));
    Bad(q => q.Conditions = Enumerable.Range(0, 21).Select(_ => q.Conditions[0].Copy()).ToList());
    Bad(q => q.Conditions = null!);
    var repeated = ExampleQuery(); invalid.Add(new() { Queries = new() { repeated, repeated.Copy() } });
    invalid.Add(new() { Queries = Enumerable.Range(0, 129).Select(_ => ExampleQuery()).ToList() });
    foreach (var queries in invalid)
    {
        Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(JsonSerializer.Serialize(new { Queries = queries })), Sample()));
        var settings = Sample(); settings.Queries = queries;
        Throws<InvalidDataException>(() => ConfigurationTransferService.Export(Path.Combine(temporary, "invalid-query-export.json"), settings));
    }
}

void CheckQueriesUnknownFields()
{
    foreach (var json in new[]
    {
        "{\"Queries\":{\"Queries\":[],\"ConnectionString\":\"secret\"}}",
        "{\"Queries\":{\"Queries\":[],\"queries\":[]}}",
        "{\"Queries\":{\"Queries\":[{\"Id\":\"11111111111111111111111111111111\",\"Name\":\"Q\",\"Script\":\"run\"}]}}",
        "{\"Queries\":{\"Queries\":[{\"Id\":\"11111111111111111111111111111111\",\"Name\":\"Q\",\"Conditions\":[{\"Field\":\"role:city\",\"Operator\":\"equals\",\"Value\":\"MA\",\"Evaluate\":true}]}]}}"
    }) Throws<InvalidDataException>(() => ConfigurationTransferService.Import(Write(json), Sample()));
}

void CheckLocalAnalyticsMigration()
{
    var settings = JsonSerializer.Deserialize<AppSettings>("{\"Cities\":null,\"Queries\":null}")!;
    settings.Normalize(); Equal(0, settings.Cities.Cities.Count);
    True(settings.Queries.Queries.Count > 0, "Les modèles de requêtes sont disponibles dans les anciennes configurations.");
    settings.Queries.Validate();
    var imported = ConfigurationTransferService.Import(Write("{\"Cities\":{\"Cities\":[]},\"Queries\":{\"Queries\":[]}}"), Sample());
    Equal(0, imported.Cities.Cities.Count); Equal(0, imported.Queries.Queries.Count);
}
