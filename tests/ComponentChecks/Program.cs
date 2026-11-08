using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GlpiPilot.Core;

var temporary = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1");
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", temporary);

var checks = new List<(string Name, Action Run)>
{
    ("ZIP vérifié installé : le composant apparaît et le registre le mentionne", CheckInstallsValidArchive),
    ("Empreinte d’archive attendue qui ne correspond pas : rien n’est installé", CheckRejectsUnexpectedArchiveHash),
    ("Fichier falsifié dans l’archive : l’inventaire le refuse", CheckRejectsTamperedFile),
    ("Chemin évasif dans l’archive : refusé avant toute écriture", CheckRejectsTraversal),
    ("Composant inconnu ou inventaire absent : refusés", CheckRejectsUnknownAndUnlisted),
    ("Réinstallation : l’ancien contenu est remplacé sans résidu", CheckReinstallReplaces),
    ("Composant absent : le message nomme ce qui manque", CheckMissingIsNamed),
    ("Le registre ne mentionne que ce qui est réellement installé", CheckRegistryReflectsReality),
    ("ZIP WebView2 du Socle épinglé : installé comme runtime fixe, sans component.json", CheckInstallsSocleWebView2),
    ("ZIP WebView2 du Socle non épinglé ou hors de runtime/webview2 : refusé", CheckRejectsUnknownSocle)
};

int failures = 0;
try
{
    foreach (var check in checks)
    {
        try { check.Run(); Console.WriteLine("PASS " + check.Name); }
        catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + check.Name + "\n" + ex); }
    }
    Console.WriteLine($"{checks.Count - failures}/{checks.Count} vérifications de composants réussies.");
}
finally { try { Directory.Delete(temporary, true); } catch (IOException) { } }
return failures == 0 ? 0 : 1;

void Clean()
{
    var root = Path.Combine(temporary, "Components");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    var registry = Path.Combine(temporary, "components.json");
    if (File.Exists(registry)) File.Delete(registry);
}

void CheckInstallsValidArchive()
{
    Clean();
    var installed = PackComponents.Install(ValidArchive(PackComponents.Firefox), expectedArchiveSha256: "");
    True(installed.Id == PackComponents.Firefox, "Le composant installé n’est pas celui annoncé.");
    True(installed.Version == "3.13.7", "La version du composant installée est perdue.");
    True(installed.Sha256.Length == 64, "L’empreinte de l’archive installée n’est pas conservée.");
    True(PackComponents.IsInstalled(PackComponents.Firefox), "Le composant installé ne se déclare pas présent.");
    True(PackComponents.Describe(PackComponents.Firefox)?.Version == "3.13.7", "Le manifeste installé n’est pas relisible.");
    True(PackComponents.Installed().Count(c => c.Id == PackComponents.Firefox) == 1, "Le registre compte le composant une seule fois.");
}

void CheckRejectsUnexpectedArchiveHash()
{
    Clean();
    var archive = ValidArchive(PackComponents.Firefox);
    Throws<InvalidDataException>(() => PackComponents.Install(archive, new string('0', 64)));
    True(!PackComponents.IsInstalled(PackComponents.Firefox), "Un ZIP dont l’empreinte ne correspond pas a quand même été installé.");
    True(!Directory.Exists(Path.Combine(temporary, "Components", PackComponents.Firefox)), "Le dossier du composant a été créé malgré le refus.");
}

void CheckRejectsTamperedFile()
{
    Clean();
    var archive = Tamper(ValidArchive(PackComponents.Firefox), "payload/fichier.txt", "contenu modifié après l’inventaire");
    Throws<InvalidDataException>(() => PackComponents.Install(archive, ""));
    True(!PackComponents.IsInstalled(PackComponents.Firefox), "Un fichier falsifié dans le ZIP a passé l’inventaire.");
}

void CheckRejectsTraversal()
{
    Clean();
    var archive = ArchiveWith(
        ("component.json", "{\"id\":\"firefox\",\"version\":\"3.13.7\",\"bytes\":1}"),
        ("SHA256SUMS.txt", ""),
        ("../pion.txt", "ecrit hors du dossier"));
    Throws<InvalidDataException>(() => PackComponents.Install(archive, ""));
    True(!File.Exists(Path.Combine(temporary, "pion.txt")), "Le ZIP a écrit en dehors du dossier du composant.");
}

void CheckRejectsUnknownAndUnlisted()
{
    Clean();
    Throws<InvalidDataException>(() => PackComponents.Install(ArchiveWith(
        ("component.json", "{\"id\":\"lecteur-multimedia\",\"version\":\"1\",\"bytes\":1}"),
        ("SHA256SUMS.txt", "x  y")), ""));
    Throws<InvalidDataException>(() => PackComponents.Install(ArchiveWith(
        ("component.json", "{\"id\":\"firefox\",\"version\":\"3.13.7\",\"bytes\":1}")), ""));
    True(!PackComponents.IsInstalled(PackComponents.Firefox), "Un ZIP sans inventaire a été accepté.");
}

void CheckReinstallReplaces()
{
    Clean();
    PackComponents.Install(ValidArchive(PackComponents.PlaywrightDriver, "payload/ancien.txt"), "");
    File.WriteAllText(Path.Combine(PackComponents.Root(PackComponents.PlaywrightDriver), "reste-d-un-ancien-install.txt"), "residu");
    PackComponents.Install(ValidArchive(PackComponents.PlaywrightDriver, "payload/nouveau.txt", "3.13.8"), "");
    True(!File.Exists(Path.Combine(PackComponents.Root(PackComponents.PlaywrightDriver), "reste-d-un-ancien-install.txt")),
        "La réinstallation a laissé un fichier de l’installation précédente.");
    True(PackComponents.Describe(PackComponents.PlaywrightDriver)?.Version == "3.13.8", "La réinstallation n’a pas pris la nouvelle version.");
}

void CheckMissingIsNamed()
{
    Clean();
    foreach (var id in PackComponents.Known)
        True(PackComponents.MissingMessage(id).Contains(PackComponents.DisplayName(id), StringComparison.Ordinal),
            "Le message d’absence ne nomme pas le composant " + id + ".");
    True(PackComponents.Describe(PackComponents.WebView2Fixed) is null, "Un composant jamais installé se déclare présent.");
}

void CheckRegistryReflectsReality()
{
    Clean();
    PackComponents.Install(ValidArchive(PackComponents.WebView2Fixed), "");
    var ids = PackComponents.Installed().Select(c => c.Id).ToList();
    True(ids.Contains(PackComponents.WebView2Fixed), "Le composant installé manque au registre.");
    True(!ids.Contains(PackComponents.Firefox), "Le registre mentionne un composant jamais installé.");
    True(File.Exists(Path.Combine(temporary, "components.json")), "Le registre n’est pas écrit à côté des données de l’application.");
}

void CheckInstallsSocleWebView2()
{
    Clean();
    var archive = ArchiveWith(("runtime/webview2/msedgewebview2.exe", "moteur"), ("runtime/webview2/9.8.7.6.manifest", "<assembly/>"),
        ("runtime/webview2/Locales/fr.pak", "fr"));
    PackComponents.PinSocleWebView2ForTests("9.8.7.6", Hash(archive));
    var installed = PackComponents.Install(archive, expectedArchiveSha256: "");
    True(installed.Id == PackComponents.WebView2Fixed && installed.Version == "9.8.7.6", "Le ZIP du Socle n’est pas reconnu comme runtime fixe.");
    True(PackComponents.InstalledRootContaining(PackComponents.WebView2Fixed, Path.Combine("runtime", "webview2")) is { } root
        && File.Exists(Path.Combine(root, "runtime", "webview2", "msedgewebview2.exe")), "Le runtime du Socle n’est pas trouvé là où le démarrage le cherche.");
}

void CheckRejectsUnknownSocle()
{
    Clean();
    Throws<InvalidDataException>(() => PackComponents.Install(ArchiveWith(("runtime/webview2/msedgewebview2.exe", "autre"),
        ("runtime/webview2/9.9.9.9.manifest", "<assembly/>")), expectedArchiveSha256: ""));
    var outside = ArchiveWith(("runtime/webview2/msedgewebview2.exe", "moteur"), ("runtime/webview2/9.8.7.5.manifest", "<assembly/>"), ("wad/SHA256SUMS.txt", "x"));
    PackComponents.PinSocleWebView2ForTests("9.8.7.5", Hash(outside));
    Throws<InvalidDataException>(() => PackComponents.Install(outside, expectedArchiveSha256: ""));
    True(!PackComponents.IsInstalled(PackComponents.WebView2Fixed), "Un ZIP refusé a laissé un runtime installé.");
}

string ValidArchive(string id, string payload = "payload/fichier.txt", string version = "3.13.7")
{
    var staging = Path.Combine(temporary, "src-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(staging);
    var file = Path.Combine(staging, payload.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, "contenu du composant");
    File.WriteAllText(Path.Combine(staging, "component.json"),
        "{\"id\":\"" + id + "\",\"version\":\"" + version + "\",\"bytes\":21}" + Environment.NewLine);
    var lines = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
        .Where(f => !string.Equals(Path.GetFileName(f), "SHA256SUMS.txt", StringComparison.Ordinal))
        .OrderBy(f => f, StringComparer.Ordinal)
        .Select(f => Hash(f) + "  " + Path.GetRelativePath(staging, f).Replace(Path.DirectorySeparatorChar, '/'));
    File.WriteAllText(Path.Combine(staging, "SHA256SUMS.txt"), string.Join("\n", lines) + "\n");
    var archive = Path.Combine(temporary, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
    ZipFile.CreateFromDirectory(staging, archive);
    Directory.Delete(staging, true);
    return archive;
}

string Tamper(string archive, string entryName, string content)
{
    using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
    zip.GetEntry(entryName)?.Delete();
    using var writer = new StreamWriter(zip.CreateEntry(entryName).Open(), new UTF8Encoding(false));
    writer.Write(content);
    return archive;
}

string ArchiveWith(params (string Name, string Content)[] entries)
{
    var archive = Path.Combine(temporary, "hostile-" + Guid.NewGuid().ToString("N") + ".zip");
    using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
    foreach (var (name, content) in entries)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
    return archive;
}

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    using var hash = SHA256.Create();
    return Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
}

static void True(bool condition, string reason) { if (!condition) throw new Exception(reason); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    catch (Exception ex) { throw new Exception("Une autre exception a été levée : " + ex.GetType().Name + " — " + ex.Message); }
    throw new Exception("Aucune exception n’a été levée.");
}
