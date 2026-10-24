using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>User-bound local key, outside transferable settings and never sent to the main interface.</summary>
public sealed class GoogleMapsSettingsStore(string path)
{
    private sealed class Stored { public string ProtectedKey { get; set; } = ""; }
    public bool HasKey => Load().ProtectedKey.Length > 0;
    private Stored Load()
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Réglage Google Maps local trop volumineux.");
        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new InvalidDataException("Réglage Google Maps local vide.");
        if (stored.ProtectedKey is null || stored.ProtectedKey.Length > 16000) throw new InvalidDataException("Clé Google Maps locale invalide.");
        return stored;
    }
    public string ReadKey()
    {
        var value = Load().ProtectedKey; if (value.Length == 0) return "";
        var bytes = LanguageAssistantStore.Protect(Convert.FromBase64String(value), true);
        try { var key = Encoding.UTF8.GetString(bytes); GoogleMapsService.ValidateKey(key); return key; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void SaveKey(string key)
    {
        GoogleMapsService.ValidateKey(key); var bytes = Encoding.UTF8.GetBytes(key);
        try
        {
            var encrypted = LanguageAssistantStore.Protect(bytes, false, "GLPI Pilot Google Maps key");
            var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored { ProtectedKey = Convert.ToBase64String(encrypted) })); File.Move(temporary, full, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Forget() { if (File.Exists(path)) File.Delete(path); }
}
