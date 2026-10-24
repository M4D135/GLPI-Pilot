using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>Machine-local settings, deliberately outside portable application configuration. Secrets use user-bound Windows DPAPI.</summary>
public sealed class LanguageAssistantStore
{
    private readonly string _path;
    public LanguageAssistantStore(string? path = null) => _path = Path.GetFullPath(path ?? Path.Combine(AppPaths.DataRoot, "language-assistant.local.json"));
    public LanguageAssistantSettings Load()
    {
        if (!File.Exists(_path)) return new();
        if (new FileInfo(_path).Length > 64 * 1024) throw new InvalidDataException("La configuration locale d’assistance dépasse 64 Kio.");
        var settings = JsonSerializer.Deserialize<LanguageAssistantSettings>(File.ReadAllText(_path), new JsonSerializerOptions { MaxDepth = 8 }) ?? throw new InvalidDataException("Configuration locale vide.");
        settings.Validate(); return settings;
    }
    public void Save(LanguageAssistantSettings settings)
    {
        settings.Validate(); var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void RememberKey(LanguageAssistantSettings settings, string apiKey)
    {
        LanguageAssistantSettings.ValidateEndpoint(settings.Endpoint);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8000 || apiKey.Any(char.IsControl)) throw new InvalidDataException("La clé d’accès est vide ou comporte des caractères invalides.");
        var plain = Encoding.UTF8.GetBytes(apiKey);
        try { settings.ProtectedApiKey = Convert.ToBase64String(Protect(plain, false)); settings.KeyEndpoint = settings.Endpoint; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static string ReadKey(LanguageAssistantSettings settings)
    {
        if (settings.ProtectedApiKey.Length == 0) return "";
        if (!string.Equals(settings.Endpoint, settings.KeyEndpoint, StringComparison.Ordinal))
            throw new InvalidOperationException("La clé mémorisée appartient à un autre endpoint. Saisissez une clé propre à la nouvelle destination.");
        var plain = Protect(Convert.FromBase64String(settings.ProtectedApiKey), true);
        try { return Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static byte[] Protect(byte[] input, bool decrypt, string description = "GLPI Pilot assistant credential")
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("La mémorisation d’une clé utilise la protection du compte Windows. La clé peut être fournie uniquement pour la session sur un autre système.");
        var pointer = Marshal.AllocHGlobal(input.Length); DataBlob output = default;
        try
        {
            Marshal.Copy(input, 0, pointer, input.Length); var blob = new DataBlob { Size = input.Length, Data = pointer };
            var succeeded = decrypt ? CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref blob, description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error(), "La protection locale de la clé a échoué.");
            if (output.Size is < 1 or > 32768) throw new InvalidDataException("Taille de clé protégée invalide.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(pointer, i, 0); Marshal.FreeHGlobal(pointer);
            if (output.Data != IntPtr.Zero) { for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
