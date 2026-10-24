using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

internal static class SiteReferenceInitializerChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(string temporary)
    {
        yield return Case("Référentiel facultatif : absence sans mutation, puis ajout tardif", AbsentThenAdded);
        yield return Case("Référentiel facultatif : UTF-8, termes multiligne et normalisation du moteur", Terms);
        yield return Case("Référentiel facultatif : fusion conservant noms, ID, communes, bâtiments et options", Merge);
        yield return Case("Référentiel facultatif : sauvegarde exacte et CSV intact", Backup);
        yield return Case("Référentiel facultatif : second lancement sans écriture ni sauvegarde supplémentaire", Repeated);
        yield return Case("Référentiel facultatif : modifications et suppressions respectées même si le CSV change", Edits);
        yield return Case("Référentiel facultatif : noms dupliqués dans le CSV refusés sans écriture", DuplicateCsv);
        yield return Case("Référentiel facultatif : homonymes existants ambigus refusés sans écriture", AmbiguousExisting);
        yield return Case("Référentiel facultatif : CSV malformés, textes et encodages invalides refusés", InvalidCsv);
        yield return Case("Référentiel facultatif : limites des sites et des termes après fusion", Limits);
        yield return Case("Référentiel facultatif : réglages corrompus préservés sans remplacement", CorruptSettings);
        yield return Case("Référentiel facultatif : échec de sauvegarde sans marqueur, réessai possible", SaveFailure);
        yield return Case("Référentiel facultatif : repère local conservé lors des imports et exclu du partage", LocalReceipt);
        yield return Case("Référentiel facultatif : simulation sans amorçage du profil habituel", Simulation);

        (string Name, Action Run) Case(string name, Action<Fixture> check) => (name, () =>
        {
            var fixture = new Fixture(Path.Combine(temporary, Guid.NewGuid().ToString("N")));
            check(fixture);
        });
    }

    private static void AbsentThenAdded(Fixture f)
    {
        Require(f.Run().State == "absent" && !Directory.Exists(f.Profile), "Un fichier absent ne crée ni profil ni repère.");
        f.Csv("Site;Termes de détection\r\nSite Alpha;Annexe Alpha\r\n");
        var result = f.Run(); var saved = f.Read();
        Require(result.State == "imported" && result.AddedSites == 1 && result.AddedAliases == 1, "Un fichier ajouté plus tard doit être importé.");
        Require(saved.Cities.Sites.Count == 1 && saved.Cities.Sites[0].CityId == "" && Guid.TryParse(saved.Cities.Sites[0].Id, out var id) && id != Guid.Empty, "Un nouveau site possède un ID et aucune commune inventée.");
        Require(saved.SiteReferenceImportHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.CsvPath))), "Le repère identifie les octets effectivement lus.");
    }

    private static void Terms(Fixture f)
    {
        f.Csv("Écritures reconnues;Site affiché\r\n\"ecole nord|Annexe-Nord;ANNEXE NORD\r\nCour Ouest|Cour Ouest\";École-Nord\r\n\"\";Site Vide\r\n");
        var result = f.Run(); var sites = f.Read().Cities.Sites;
        Require(result.State == "imported" && result.AddedAliases == 2 && sites[0].Aliases.SequenceEqual(new[] { "Annexe-Nord", "Cour Ouest" }), "Les variantes canoniques et les doublons sont éliminés selon les mots du moteur.");
        Require(sites[1].Aliases.Count == 0, "Un site peut être reconnu uniquement par son nom.");
    }

    private static AppSettings Existing()
    {
        var city = new CityDefinition { Name = "Commune Exemple", PostalCode = "00000", Latitude = 0, Longitude = 0 };
        var site = new SiteDefinition { Name = "École-Nord", CityId = city.Id, Aliases = new() { "Ancienne annexe" },
            Latitude = 1.25, Longitude = 2.5, Address = "Adresse de test", Contact = "Accueil de test", Equipment = "Équipement de test",
            Openings = new() { new() { Day = DayOfWeek.Monday, From = "08:00", Until = "17:00" } }, ClosedDates = new() { "2026-12-25" } };
        return new AppSettings { Theme = "light", ExportFolder = "Exports-synthetiques", InterfaceZoomPercent = 115,
            PortalUrl = "https://glpi.example.test/front/ticket.php?criteria%5B0%5D%5Bvalue%5D=5", TimeoutSeconds = 321,
            Cities = new() { Enabled = false, TitleColumn = "Titre exact", LocationColumn = "Lieu exact", SourcePriority = new() { "site", "title" },
                Cities = new() { city }, Sites = new() { site }, Buildings = new() { new() { SiteId = site.Id, Name = "Bâtiment Exemple", Floor = "1" } } },
            Interface = new() { Accent = "violet" } };
    }

    private static void Merge(Fixture f)
    {
        var original = Existing(); f.Settings(original);
        f.Csv("Site;Termes de détection\nECOLE NORD;Ancienne-annexe|Nouvelle annexe|École Nord\nSite Beta;Variante Beta\n");
        var result = f.Run(); var saved = f.Read();
        Require(result.State == "imported" && result.AddedSites == 1 && result.AddedAliases == 2, "La fusion ajoute seulement les nouvelles entrées.");
        var expectedSite = original.Cities.Sites[0].Copy(); expectedSite.Aliases.Add("Nouvelle annexe");
        Require(JsonSerializer.Serialize(expectedSite) == JsonSerializer.Serialize(saved.Cities.Sites[0]), "Nom, ID, commune, horaires, coordonnées et propriétés du site sont conservés.");
        Require(JsonSerializer.Serialize(original.Cities.Cities) == JsonSerializer.Serialize(saved.Cities.Cities) && JsonSerializer.Serialize(original.Cities.Buildings) == JsonSerializer.Serialize(saved.Cities.Buildings), "Communes et bâtiments sont conservés.");
        Require(!saved.Cities.Enabled && saved.Cities.TitleColumn == original.Cities.TitleColumn && saved.Cities.LocationColumn == original.Cities.LocationColumn
            && saved.Cities.SourcePriority.SequenceEqual(original.Cities.SourcePriority) && saved.Theme == original.Theme
            && saved.ExportFolder == original.ExportFolder && saved.PortalUrl == original.PortalUrl && saved.TimeoutSeconds == original.TimeoutSeconds
            && saved.InterfaceZoomPercent == original.InterfaceZoomPercent && saved.Interface.Accent == original.Interface.Accent, "Les options et autres réglages locaux restent inchangés.");
    }

    private static void Backup(Fixture f)
    {
        f.Settings(Existing()); var before = File.ReadAllBytes(f.SettingsPath);
        f.Csv("Site;Termes de détection\nSite Beta;Annexe Beta\n"); var csv = File.ReadAllBytes(f.CsvPath);
        Require(f.Run().State == "imported", "La fusion doit réussir.");
        var backup = SettingsRecovery.List(f.SettingsPath).Single();
        Require(before.SequenceEqual(File.ReadAllBytes(backup.Path)), "La sauvegarde contient exactement tous les octets du profil initial.");
        Require(csv.SequenceEqual(File.ReadAllBytes(f.CsvPath)), "Le fichier livré ne doit jamais être modifié.");
    }

    private static void Repeated(Fixture f)
    {
        f.Settings(Existing()); f.Csv("Site;Alias\nSite Beta;Annexe Beta\n");
        Require(f.Run().State == "imported", "Le premier passage doit réussir.");
        var bytes = File.ReadAllBytes(f.SettingsPath); var timestamp = File.GetLastWriteTimeUtc(f.SettingsPath); var backups = SettingsRecovery.List(f.SettingsPath).Count;
        Require(f.Run().State == "already-imported" && bytes.SequenceEqual(File.ReadAllBytes(f.SettingsPath)) && timestamp == File.GetLastWriteTimeUtc(f.SettingsPath)
            && backups == SettingsRecovery.List(f.SettingsPath).Count, "Le second passage ne réécrit rien.");
    }

    private static void Edits(Fixture f)
    {
        f.Csv("Site;Alias\nSite Alpha;Alias Alpha\nSite Beta;Alias Beta\n"); Require(f.Run().State == "imported", "Le premier passage doit réussir.");
        var edited = f.Read(); edited.Cities.Sites.RemoveAt(1); edited.Cities.Sites[0].Name = "Nom modifié"; edited.Cities.Sites[0].Aliases.Clear(); f.Settings(edited);
        var before = File.ReadAllBytes(f.SettingsPath);
        f.Csv("Site;Alias\nSite Alpha;Nouvel alias\nSite Beta;Alias Beta\nSite Gamma;Alias Gamma\n");
        Require(f.Run().State == "already-imported" && before.SequenceEqual(File.ReadAllBytes(f.SettingsPath)), "Un CSV changé ne remplace pas les modifications et ne recrée aucun site supprimé.");
    }

    private static void DuplicateCsv(Fixture f)
    {
        f.Csv("Site;Alias\nÉcole-Nord;Alpha\nECOLE NORD;Beta\n");
        Require(f.Run().State == "failed" && !Directory.Exists(f.Profile), "Deux noms équivalents dans le CSV doivent refuser l'ensemble.");
    }

    private static void AmbiguousExisting(Fixture f)
    {
        var settings = Existing(); settings.Cities.Sites.Add(new() { Name = "Ecole Nord" }); f.Settings(settings);
        f.Csv("Site;Alias\nEcole Nord;Nouveau terme\n"); RefusedUnchanged(f);
    }

    private static void InvalidCsv(Fixture f)
    {
        f.Settings(Existing());
        foreach (var csv in new[] { "", "Site;Alias\n", "Autre;Colonne\nAlpha;Beta\n", "Site;Alias\nAlpha;Beta;Gamma\n", "Site;Alias\n\"Alpha;Beta\n",
            "Site;Alias\n;Beta\n", "Site;Alias\nAlpha;***\n", "Site;Alias\nAlpha;Inva\0lide\n", "Site;Alias\n" + new string('A', 121) + ";Beta\n",
            "Site;Alias\nAlpha;" + new string('B', 121) + "\n" })
        { f.Csv(csv); RefusedUnchanged(f); }
        File.WriteAllBytes(f.CsvPath, new byte[] { 0xff, 0xfe, 0x41, 0x00 }); RefusedUnchanged(f);
        File.WriteAllBytes(f.CsvPath, new byte[4 * 1024 * 1024 + 1]); RefusedUnchanged(f);
    }

    private static void Limits(Fixture f)
    {
        f.Settings(Existing()); f.Csv("Site;Alias\nÉcole-Nord;" + string.Join('|', Enumerable.Range(1, 12).Select(i => "Terme " + i)) + "\n");
        RefusedUnchanged(f); // The existing term also counts toward the merged limit.
        f.Csv("Site;Alias\nSite Alpha;" + string.Join('|', Enumerable.Range(1, 13).Select(i => "Terme " + i)) + "\n"); RefusedUnchanged(f);
        f.Csv("Site;Alias\n" + string.Join('\n', Enumerable.Range(1, 2001).Select(i => "Site " + i + ";"))); RefusedUnchanged(f);
        var settings = new AppSettings(); settings.Cities.Sites = Enumerable.Range(1, 2000).Select(i => new SiteDefinition { Name = "Site " + i }).ToList(); f.Settings(settings);
        f.Csv("Site;Alias\nSite supplémentaire;\n"); RefusedUnchanged(f);
    }

    private static void CorruptSettings(Fixture f)
    {
        f.Csv("Site;Alias\nSite Alpha;Alias Alpha\n"); Directory.CreateDirectory(f.Profile);
        foreach (var json in new[] { "", "{broken", "null", "[]", "{\"Cities\":{},\"Cities\":{}}", "{\"Cities\":{\"Sites\":null}}", "{\"TimeoutSeconds\":\"not-a-number\"}" })
        { File.WriteAllText(f.SettingsPath, json, new UTF8Encoding(false)); RefusedUnchanged(f); }
        File.WriteAllText(f.SettingsPath, "");
        Require(SettingsRecovery.Load(f.SettingsPath).SiteReferenceImportHash.Length == 0 && File.ReadAllBytes(f.SettingsPath).Length == 0,
            "Après refus, le chargement normal doit aussi démarrer avec un profil vide endommagé sans l'écraser.");
    }

    private static void SaveFailure(Fixture f)
    {
        f.Settings(Existing()); f.Csv("Site;Alias\nSite Beta;Alias Beta\n");
        var before = File.ReadAllBytes(f.SettingsPath); var blocker = Path.Combine(f.Profile, "SettingsVersions"); File.WriteAllText(blocker, "blocked");
        Require(f.Run().State == "failed" && before.SequenceEqual(File.ReadAllBytes(f.SettingsPath)) && f.Read().SiteReferenceImportHash.Length == 0, "Un échec de sauvegarde ne consomme pas le fichier ni ne modifie le profil.");
        File.Delete(blocker);
        Require(f.Run().State == "imported" && f.Read().Cities.Sites.Count == 2 && f.Read().SiteReferenceImportHash.Length == 64, "Le même import doit réussir une fois la sauvegarde possible.");
    }

    private static void LocalReceipt(Fixture f)
    {
        var current = Existing(); current.SiteReferenceImportHash = new string('A', 64); Directory.CreateDirectory(f.Profile);
        var portable = Path.Combine(f.Profile, "portable.json"); ConfigurationTransferService.Export(portable, current);
        Require(!File.ReadAllText(portable).Contains(nameof(AppSettings.SiteReferenceImportHash), StringComparison.Ordinal), "Le repère ne doit pas être partagé.");
        Require(ConfigurationTransferService.Import(portable, current).SiteReferenceImportHash == current.SiteReferenceImportHash && current.Copy().SiteReferenceImportHash == current.SiteReferenceImportHash, "Import et copie gardent le repère du poste cible.");
        var foreign = Existing(); foreign.SiteReferenceImportHash = new string('B', 64); foreign.Normalize(); f.Settings(foreign);
        Require(ConfigurationTransferService.Import(f.SettingsPath, current).SiteReferenceImportHash == current.SiteReferenceImportHash, "Un ancien settings.json ne remplace pas le repère du poste cible.");
    }

    private static void Simulation(Fixture f)
    {
        f.Csv("Site;Alias\nSite Alpha;Alias Alpha\n");
        var simulationRoot = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Simulation-" + Guid.NewGuid().ToString("N"));
        using (AppPaths.EnterSimulation(simulationRoot))
            Require(f.Run().State == "simulation" && !Directory.Exists(f.Profile), "Le contexte de simulation ne doit pas importer vers un profil habituel.");
    }

    private static void RefusedUnchanged(Fixture f)
    {
        var before = File.ReadAllBytes(f.SettingsPath); var csv = File.ReadAllBytes(f.CsvPath);
        Require(f.Run().State == "failed" && before.SequenceEqual(File.ReadAllBytes(f.SettingsPath)) && csv.SequenceEqual(File.ReadAllBytes(f.CsvPath))
            && !Directory.Exists(Path.Combine(f.Profile, "SettingsVersions")), "Un refus ne modifie aucun fichier et ne crée aucune sauvegarde.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture(string root)
    {
        public string Application => Path.Combine(root, "application");
        public string Profile => Path.Combine(root, "profile");
        public string SettingsPath => Path.Combine(Profile, "settings.json");
        public string CsvPath => Path.Combine(Application, "wad", "sites.csv");
        public void Csv(string value) { Directory.CreateDirectory(Path.GetDirectoryName(CsvPath)!); File.WriteAllText(CsvPath, value, new UTF8Encoding(true)); }
        public void Settings(AppSettings settings) { Directory.CreateDirectory(Profile); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings), new UTF8Encoding(false)); }
        public AppSettings Read() => SettingsRecovery.Read(SettingsPath);
        public SiteReferenceInitialization Run() => SiteReferenceInitializer.TryInitialize(Application, SettingsPath);
    }
}
