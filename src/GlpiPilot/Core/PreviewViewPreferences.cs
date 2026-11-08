using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class PreviewColumnPreference
{
    public int SourceIndex { get; set; }
    public int DisplayIndex { get; set; }
    public bool Visible { get; set; } = true;
    public double Width { get; set; } = PreviewViewPreferences.DefaultWidth;
    public bool AutoWidth { get; set; }

    public PreviewColumnPreference Copy() => new()
    {
        SourceIndex = SourceIndex, DisplayIndex = DisplayIndex,
        Visible = Visible, Width = Width, AutoWidth = AutoWidth
    };
}

/// <summary>Local presentation only. The file contains hashed header structures and column numbers, never cell values.</summary>
public static class PreviewViewPreferences
{
    public const double DefaultWidth = 165;
    public const double MinimumWidth = 70;
    public const double MaximumWidth = 1200;
    public const int MaximumProfiles = 24;
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 12 };

    public static string Fingerprint(IReadOnlyList<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Length prefixes distinguish embedded separators, empty headers and duplicate names in different positions.
        Append(headers.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var header in headers) Append(header ?? "");
        return Convert.ToHexString(hash.GetHashAndReset());

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
            hash.AppendData(bytes);
        }
    }

    public static PreviewColumnPreference[] Defaults(int columnCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columnCount);
        return Enumerable.Range(0, columnCount).Select(index => new PreviewColumnPreference
        { SourceIndex = index, DisplayIndex = index }).ToArray();
    }

    public static PreviewColumnPreference[] Normalize(IEnumerable<PreviewColumnPreference>? columns, int columnCount)
    {
        var result = Defaults(columnCount);
        var seen = new HashSet<int>();
        foreach (var column in columns ?? Enumerable.Empty<PreviewColumnPreference>())
        {
            if (column == null || column.SourceIndex < 0 || column.SourceIndex >= columnCount || !seen.Add(column.SourceIndex)) continue;
            var item = column.Copy();
            item.DisplayIndex = item.DisplayIndex >= 0 && item.DisplayIndex < columnCount ? item.DisplayIndex : item.SourceIndex;
            item.Width = double.IsFinite(item.Width) ? Math.Clamp(item.Width, MinimumWidth, MaximumWidth) : DefaultWidth;
            result[item.SourceIndex] = item;
        }
        var ordered = result.OrderBy(column => column.DisplayIndex).ThenBy(column => column.SourceIndex).ToArray();
        for (var index = 0; index < ordered.Length; index++) ordered[index].DisplayIndex = index;
        if (ordered.Length > 0 && ordered.All(column => !column.Visible)) ordered[0].Visible = true;
        return result;
    }

    public static PreviewColumnPreference[] Load(string path, IReadOnlyList<string> headers)
    {
        var document = ReadDocument(path);
        var fingerprint = Fingerprint(headers);
        return Normalize(document.Profiles.TryGetValue(fingerprint, out var profile) ? profile?.Columns : null, headers.Count);
    }

    public static void Save(string path, IReadOnlyList<string> headers, IEnumerable<PreviewColumnPreference> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PreferenceDocument document;
        try { document = ReadDocument(path); }
        catch (JsonException)
        {
            // A malformed presentation file must not permanently prevent saving a new valid local view.
            document = new();
        }
        var fingerprint = Fingerprint(headers);
        document.Profiles[fingerprint] = new PreferenceProfile
        {
            Columns = Normalize(columns, headers.Count), UpdatedUtc = DateTimeOffset.UtcNow
        };
        document.Profiles = document.Profiles
            .Where(pair => ValidFingerprint(pair.Key) && pair.Value != null)
            .OrderByDescending(pair => pair.Key == fingerprint)
            .ThenByDescending(pair => pair.Value.UpdatedUtc)
            .Take(MaximumProfiles)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Les préférences de colonnes dépassent la taille autorisée.");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static PreferenceDocument ReadDocument(string path)
    {
        if (!File.Exists(path)) return new();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Le fichier des préférences de colonnes est trop volumineux.");
        var result = JsonSerializer.Deserialize<PreferenceDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("Le fichier des préférences de colonnes est vide.");
        if (result.Version != SchemaVersion)
            throw new NotSupportedException("La version des préférences de colonnes n’est pas reconnue.");
        if (result.Profiles == null) throw new InvalidDataException("Le fichier des préférences de colonnes n’a pas de profils valides.");
        return result;
    }

    private static bool ValidFingerprint(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    public sealed class PreferenceDocument
    {
        public int Version { get; set; } = SchemaVersion;
        public Dictionary<string, PreferenceProfile> Profiles { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class PreferenceProfile
    {
        public PreviewColumnPreference[] Columns { get; set; } = Array.Empty<PreviewColumnPreference>();
        public DateTimeOffset UpdatedUtc { get; set; }
    }
}
