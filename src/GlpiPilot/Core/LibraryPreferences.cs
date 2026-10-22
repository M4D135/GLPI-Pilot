using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace GlpiPilot.Core;

/// <summary>Machine-local connection settings. Never included in team configuration exports.</summary>
public sealed class LibraryPreferences
{
    public int Version { get; set; } = 1;
    public string LocalFolder { get; set; } = Path.Combine(AppPaths.DataRoot, "Library");
    public bool PostgresEnabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "glpi_pilot";
    public string Username { get; set; } = "";
    public string TlsMode { get; set; } = "VerifyFull";
    public bool AutoSync { get; set; } = true;
    public int IntervalMinutes { get; set; } = 5;
    public string SourceTimeZone { get; set; } = "";
    public string SourceDateOrder { get; set; } = "";
    public int MaxAgeDays { get; set; } = 7;
    public string DisplayName { get; set; } = "Technicien";
    public string ProtectedPassword { get; set; } = "";
    public string PasswordBinding { get; set; } = "";
    public string DatabasePath => Path.Combine(LocalFolder, "glpi-pilot-library.db");
    public LibraryPreferences Copy() => (LibraryPreferences)MemberwiseClone();
    public void Validate()
    {
        if (Version != 1 || Port is < 1 or > 65535 || IntervalMinutes is < 1 or > 1440 || MaxAgeDays is < 1 or > 365
            || TlsMode is not ("VerifyFull" or "Require" or "Disable") || SourceDateOrder is not ("" or "dmy" or "mdy" or "ymd"))
            throw new InvalidDataException("Réglages de bibliothèque invalides : port, intervalle, âge du cache ou TLS.");
        foreach (var (value, maximum) in new[] { (Host, 500), (Database, 200), (Username, 200), (DisplayName, 120), (SourceTimeZone, 200) })
            if (value is null || value.Length > maximum || value.Any(char.IsControl)) throw new InvalidDataException("Un réglage de bibliothèque contient un texte invalide.");
        if (PostgresEnabled && (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(Username)))
            throw new InvalidDataException("Renseignez le serveur, la base et l’utilisateur PostgreSQL.");
        if (ProtectedPassword is null || ProtectedPassword.Length > 32768 || PasswordBinding is null || PasswordBinding.Length > 100)
            throw new InvalidDataException("Identifiant de connexion local invalide.");
        try { if (SourceTimeZone.Length > 0) _ = TimeZoneInfo.FindSystemTimeZoneById(SourceTimeZone); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new InvalidDataException("Le fuseau horaire des dates GLPI n’est pas reconnu."); }
        LocalFolder = LibraryPreferencesStore.ValidateLocalFolder(LocalFolder);
    }
    public string ConnectionBinding() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Host.Trim().ToLowerInvariant() + "\n" + Port.ToString(CultureInfo.InvariantCulture) + "\n" + Database + "\n" + Username + "\n" + TlsMode)));
    public string ConnectionString(string password)
    {
        Validate();
        if (!PostgresEnabled) throw new InvalidOperationException("La base centrale n’est pas activée.");
        if (password.Length > 8000 || password.Contains('\0')) throw new InvalidDataException("Mot de passe de connexion invalide.");
        return new NpgsqlConnectionStringBuilder
        {
            Host = Host.Trim(), Port = Port, Database = Database, Username = Username, Password = password,
            SslMode = Enum.Parse<SslMode>(TlsMode), Timeout = 8, CommandTimeout = 30,
            Pooling = true, IncludeErrorDetail = false, ApplicationName = "GLPI-Pilot"
        }.ConnectionString;
    }
}

public sealed class LibraryPreferencesStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 8 };
    public LibraryPreferencesStore(string? path = null) => _path = Path.GetFullPath(path ?? Path.Combine(AppPaths.DataRoot, "library.local.json"));
    public LibraryPreferences Load()
    {
        if (!File.Exists(_path)) return new();
        if (new FileInfo(_path).Length > 64 * 1024) throw new InvalidDataException("Les réglages de bibliothèque dépassent 64 Kio.");
        LibraryPreferences value;
        try { value = JsonSerializer.Deserialize<LibraryPreferences>(File.ReadAllText(_path), Json) ?? throw new InvalidDataException("Réglages de bibliothèque vides."); }
        catch (JsonException) { throw new InvalidDataException("Les réglages de bibliothèque sont illisibles."); }
        value.Validate(); return value;
    }
    public void Save(LibraryPreferences value)
    {
        value.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Json)); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string ValidateLocalFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 3000 || folder.Any(char.IsControl)
            || folder.StartsWith("\\\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal) || !Path.IsPathFullyQualified(folder))
            throw new InvalidDataException("Choisissez un dossier local pour le cache. La base commune utilise la connexion PostgreSQL ; les exports peuvent être enregistrés sur Samba.");
        var path = Path.GetFullPath(folder);
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new InvalidDataException("Le cache SQLite doit rester sur un disque local. Utilisez PostgreSQL pour partager la base entre postes.");
        for (var parent = new DirectoryInfo(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Le dossier du cache ne doit pas passer par une liaison de fichiers. Choisissez directement son emplacement local.");
        if (OperatingSystem.IsLinux() && File.Exists("/proc/mounts"))
        {
            var mount = File.ReadLines("/proc/mounts").Select(line => line.Split(' ')).Where(parts => parts.Length > 2)
                .Select(parts => new { Path = parts[1].Replace("\\040", " "), Type = parts[2] })
                .Where(item => path == item.Path || path.StartsWith(item.Path.TrimEnd('/') + "/", StringComparison.Ordinal))
                .OrderByDescending(item => item.Path.Length).FirstOrDefault();
            if (mount?.Type is "cifs" or "smb3" or "nfs" or "nfs4" or "fuse.sshfs")
                throw new InvalidDataException("Le cache SQLite doit rester sur un disque local. Configurez la base centrale pour le partage réseau.");
        }
        return path;
    }
    public static void RememberPassword(LibraryPreferences value, string password)
    {
        if (password.Length is < 1 or > 8000 || password.Contains('\0')) throw new InvalidDataException("Mot de passe invalide.");
        var plain = Encoding.UTF8.GetBytes(password);
        try { value.ProtectedPassword = Convert.ToBase64String(LocalUserSecret.Protect(plain, false)); value.PasswordBinding = value.ConnectionBinding(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static string ReadPassword(LibraryPreferences value)
    {
        if (value.ProtectedPassword.Length == 0) return "";
        if (value.PasswordBinding != value.ConnectionBinding()) throw new InvalidOperationException("Le mot de passe mémorisé appartient à une autre connexion centrale. Saisissez celui de la destination choisie.");
        var plain = LocalUserSecret.Protect(Convert.FromBase64String(value.ProtectedPassword), true);
        try { return Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static DateTimeOffset? ParseModification(string raw, string zoneId, string sourceDateOrder = "")
    {
        var value = raw.Trim();
        if (Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var instant)) return instant;
        if (zoneId.Length == 0) return null;
        var parts = Regex.Match(value, @"^(\d{2})[/\-](\d{2})[/\-]\d{4}\s", RegexOptions.CultureInvariant);
        if (sourceDateOrder.Length == 0 && parts.Success && int.Parse(parts.Groups[1].Value, CultureInfo.InvariantCulture) <= 12
            && int.Parse(parts.Groups[2].Value, CultureInfo.InvariantCulture) <= 12 && parts.Groups[1].Value != parts.Groups[2].Value) return null;
        var formats = new List<string> { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm" };
        if (sourceDateOrder is "" or "dmy") formats.AddRange(["dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy HH:mm"]);
        if (sourceDateOrder is "" or "mdy") formats.AddRange(["MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy HH:mm", "MM-dd-yyyy HH:mm:ss", "MM-dd-yyyy HH:mm"]);
        if (!DateTime.TryParseExact(value, formats.ToArray(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(date) || zone.IsInvalidTime(date)) return null;
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date, zone), TimeSpan.Zero);
    }
}

internal static class LocalUserSecret
{
    public static byte[] Protect(byte[] input, bool decrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("La mémorisation du mot de passe utilise la protection du compte Windows ; fournissez-le pour la session sur cet environnement.");
        var pointer = Marshal.AllocHGlobal(input.Length); Blob output = default;
        try
        {
            Marshal.Copy(input, 0, pointer, input.Length); var blob = new Blob { Size = input.Length, Data = pointer };
            var success = decrypt ? CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref blob, "GLPI Pilot central database", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "La protection locale du mot de passe a échoué.");
            if (output.Size is < 1 or > 32768) throw new InvalidDataException("Taille de secret protégé invalide.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(pointer, i, 0); Marshal.FreeHGlobal(pointer);
            if (output.Data != IntPtr.Zero) { for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
