using System.IO;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

public static class AppPaths
{
    public static string ApplicationVersion => typeof(AppPaths).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GLPI-Pilot");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string DefaultExports => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GLPI-Pilot", "Exports");
    public static string Profile(string channel) => Path.Combine(DataRoot, "Browser", channel == "chrome" ? "Chrome" : "Edge");
    public static string Profile(string channel, string portalUrl) => Path.Combine(Profile(channel), ObjectIdentity.InstallationKey(portalUrl));
    public static string LogFile => Path.Combine(DataRoot, "Logs", DateTime.Today.ToString("yyyy-MM-dd") + ".log");

    public static string SafeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "";
        var clean = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        return clean.Uri.GetLeftPart(UriPartial.Path);
    }

    public static string SafeError(Exception error)
    {
        // Browser call logs can contain snippets of the page or locator text.
        // Keep only the error's first line, never an HTML snippet or a stack trace.
        var message = error.GetBaseException().Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? error.GetType().Name;
        message = Regex.Replace(message, "<[^>]+>.*", "[détail de page masqué]");
        return SafeLog(message);
    }

    public static string SafeLog(string message)
    {
        message = Regex.Replace(message, @"https?://[^\s<>""']+", match => SafeUrl(match.Value));
        message = Regex.Replace(message, @"(?i)\b(password|passwd|access_token|id_token|refresh_token|client_secret|SAMLResponse|SAMLRequest|authorization|cookie)\s*[:=]\s*[^\s,;]+", "$1=[masqué]");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0) message = message.Replace(profile, "%PROFIL_UTILISATEUR%", StringComparison.OrdinalIgnoreCase);
        message = Regex.Replace(message, @"(?i)[a-z]:\\Users\\[^\\\s]+", "%PROFIL_UTILISATEUR%");
        return message.Length > 1800 ? message[..1800] + "…" : message;
    }

    public static void AppendLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss} {SafeLog(message)}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class ObjectIdentity
{
    public static string Installation(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0) throw new InvalidDataException("Adresse d’installation GLPI invalide.");
        var path = uri.AbsolutePath;
        var segments = new[] { "/front/", "/ajax/", "/apirest.php", "/api.php" };
        var boundary = segments.Select(segment => path.IndexOf(segment, StringComparison.OrdinalIgnoreCase)).Where(index => index >= 0).DefaultIfEmpty(path.LastIndexOf('/')).Min();
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + path[..Math.Max(0, boundary)];
    }
    public static string InstallationKey(string url) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Installation(url)))).ToLowerInvariant()[..24];
    public static string Key(string url, string objectType, string id)
    {
        if (!Regex.IsMatch(objectType ?? "", @"^[A-Za-z][A-Za-z0-9_\\]{0,119}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Type d’objet GLPI invalide.");
        if (!Regex.IsMatch(id?.Trim() ?? "", @"^[0-9]{1,20}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Identifiant d’objet GLPI invalide.");
        var normalized = id!.Trim().TrimStart('0'); if (normalized.Length == 0) throw new InvalidDataException("L’identifiant d’objet GLPI doit être supérieur à zéro.");
        return InstallationKey(url) + ":" + objectType!.ToLowerInvariant() + ":" + normalized;
    }
}
