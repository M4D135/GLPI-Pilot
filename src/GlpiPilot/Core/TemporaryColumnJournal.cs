using System.IO;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

/// <summary>Only column schema and opaque context hashes are persisted, never ticket values or session tokens.</summary>
public sealed class TemporaryColumnEntry
{
    public string Value { get; set; } = "";
    public string PreferenceId { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Fixed { get; set; }
    [JsonIgnore] public string Identity => PreferenceId.Length > 0 ? "preference:" + PreferenceId
        : Value.Length > 0 ? "option:" + Value : Fixed ? "fixed:" + Label : "";
}

public sealed class TemporaryColumnView
{
    public bool PersonalActive { get; set; }
    public List<TemporaryColumnEntry> Columns { get; set; } = new();
}

public sealed class TemporaryColumnCommand
{
    public string Kind { get; set; } = "";
    public string OptionValue { get; set; } = "";
    public string OptionLabel { get; set; } = "";
    public string OptionHeader { get; set; } = "";
    public string TargetIdentity { get; set; } = "";
    public bool ResponseObserved { get; set; }
    public TemporaryColumnView Before { get; set; } = new();
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TemporaryColumnJournalRecord
{
    public int Version { get; set; } = 1;
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string InstallationKey { get; set; } = "";
    public string BrowserChannel { get; set; } = "";
    public string Origin { get; set; } = "";
    public string PreferencesUrl { get; set; } = "";
    public string UsersId { get; set; } = "";
    public string Layout { get; set; } = "";
    public string SourceScopeFingerprint { get; set; } = "";
    public string ContextFingerprint { get; set; } = "";
    public bool ContextObserved { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string State { get; set; } = "prepared";
    public TemporaryColumnView Original { get; set; } = new();
    public TemporaryColumnView Expected { get; set; } = new();
    public TemporaryColumnView? ActivationBaseline { get; set; }
    public List<TemporaryColumnEntry> Added { get; set; } = new();
    public TemporaryColumnCommand? PendingCommand { get; set; }
}

/// <summary>
/// One recovery record per dedicated browser profile. Every write precedes its
/// native command, is flushed to disk, then atomically replaces the old record.
/// An unreadable/foreign record is never overwritten by a new collection.
/// </summary>
public sealed class TemporaryColumnJournalStore(string? root = null)
{
    private string Folder => root ?? Path.Combine(AppPaths.DataRoot, "Recovery", "Columns");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 24 };
    private static readonly ConcurrentDictionary<string, object> Gates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static string Key(string installationKey, string channel) => installationKey + "-"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(channel)))[..16].ToLowerInvariant();

    public string PathFor(AppSettings settings) => Path.Combine(Folder,
        Key(ObjectIdentity.InstallationKey(settings.PortalUrl), settings.BrowserChannel) + ".json");

    public bool Exists(AppSettings settings) => File.Exists(PathFor(settings));

    public TemporaryColumnJournalRecord? Load(AppSettings settings)
    {
        var path = PathFor(settings);
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length is <= 0 or > 4_194_304)
                throw new InvalidDataException("Le journal des colonnes est vide ou trop volumineux.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var record = JsonSerializer.Deserialize<TemporaryColumnJournalRecord>(stream, Json)
                ?? throw new InvalidDataException("Le journal des colonnes est vide.");
            Validate(record, settings);
            return record;
        }
        catch (JsonException)
        { throw new InvalidDataException("Le journal de restauration des colonnes est illisible. Aucune préférence GLPI n’est modifiée."); }
    }

    public void Create(AppSettings settings, TemporaryColumnJournalRecord record)
    {
        lock (Gates.GetOrAdd(PathFor(settings), _ => new()))
        {
            Validate(record, settings);
            if (Exists(settings))
            {
                var completed = Load(settings);
                if (completed?.State != "restored")
                    throw new InvalidOperationException("Une restauration de colonnes est encore en attente. Aucune nouvelle préparation n’est autorisée.");
                // A prior completion may have survived a temporarily locked
                // directory. Remove only a validated completed record; a failed
                // deletion still stops before any new preference mutation.
                File.Delete(PathFor(settings));
            }
            Write(settings, record, create: true);
        }
    }

    public void Save(AppSettings settings, TemporaryColumnJournalRecord record)
    {
        lock (Gates.GetOrAdd(PathFor(settings), _ => new()))
        {
            var existing = Load(settings);
            if (existing is null || existing.SessionId != record.SessionId)
                throw new InvalidOperationException("Le journal des colonnes a changé. Aucune autre commande GLPI n’est envoyée.");
            Write(settings, record, create: false);
        }
    }

    public void Complete(AppSettings settings, TemporaryColumnJournalRecord record)
    {
        lock (Gates.GetOrAdd(PathFor(settings), _ => new()))
        {
            record.State = "restored";
            record.PendingCommand = null;
            Save(settings, record);
            // The durable completion remains authoritative even if antivirus or a
            // read-only directory delays removal of the now harmless journal file.
            try { File.Delete(PathFor(settings)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Write(AppSettings settings, TemporaryColumnJournalRecord record, bool create)
    {
        Validate(record, settings);
        Directory.CreateDirectory(Folder);
        record.UpdatedAt = DateTimeOffset.UtcNow;
        var path = PathFor(settings);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, record, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: !create);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static bool IsObservedPreferencesUrl(string address, string origin)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")
            || url.UserInfo.Length > 0 || url.Fragment.Length > 0 || url.GetLeftPart(UriPartial.Authority) != origin
            || !url.AbsolutePath.EndsWith("/front/displaypreference.form.php", StringComparison.Ordinal)) return false;
        // Persist exactly the reviewed native GET, without CSRF/session/search
        // parameters, credentials or a manufactured endpoint.
        return url.Query.TrimStart('?') == "itemtype=Ticket";
    }

    public static void Validate(TemporaryColumnJournalRecord record, AppSettings settings)
    {
        static bool Positive(string? value) => value is { Length: > 0 and <= 20 } && value.All(char.IsAsciiDigit) && value[0] != '0';
        static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
        static bool Entry(TemporaryColumnEntry? column) => column is not null && column.Label is { Length: <= 2048 }
            && column.Value is not null && column.PreferenceId is not null
            && (column.Value.Length == 0 || Positive(column.Value))
            && (column.PreferenceId.Length == 0 || Positive(column.PreferenceId))
            && column.Identity.Length > 0;
        static bool View(TemporaryColumnView? view) => view is not null && view.Columns is { Count: <= 2048 }
            && (view.PersonalActive || view.Columns.Count == 0)
            && view.Columns.All(Entry)
            && view.Columns.Select(column => column.Identity).Distinct(StringComparer.Ordinal).Count() == view.Columns.Count;
        if (record.Version != 1 || !Guid.TryParseExact(record.SessionId, "N", out _)
            || record.InstallationKey != ObjectIdentity.InstallationKey(settings.PortalUrl)
            || record.BrowserChannel != settings.BrowserChannel || !Positive(record.UsersId)
            || record.Layout is not ("modern" or "legacy")
            || !IsObservedPreferencesUrl(record.PreferencesUrl, record.Origin)
            || record.Origin != new Uri(settings.PortalUrl).GetLeftPart(UriPartial.Authority) && record.Origin != settings.TableFrameOrigin
            || record.Origin == new Uri(settings.PortalUrl).GetLeftPart(UriPartial.Authority)
                && ObjectIdentity.InstallationKey(record.PreferencesUrl) != record.InstallationKey
            || !Hash(record.SourceScopeFingerprint) || !Hash(record.ContextFingerprint)
            || record.State is not ("prepared" or "active" or "restoring" or "pending" or "restored")
            || !View(record.Original) || !View(record.Expected)
            || record.ActivationBaseline is not null && !View(record.ActivationBaseline)
            || record.Added is not { Count: <= 2048 }
            || record.Added.Any(column => !Entry(column) || column.Fixed)
            || record.Added.Select(column => column.Identity).Distinct(StringComparer.Ordinal).Count() != record.Added.Count
            || record.Added.Any(column => record.Original.Columns.Any(original => original.Identity == column.Identity)))
            throw new InvalidDataException("Le journal des colonnes ne correspond pas à ce contexte GLPI ou son état n’est pas vérifiable. Aucune préférence n’est modifiée.");
        if (record.PendingCommand is { } pending && (pending.Kind is not ("activate" or "add" or "remove" or "disable")
            || !View(pending.Before) || pending.Kind == "add" && !Positive(pending.OptionValue)
            || pending.OptionLabel is not { Length: <= 2048 } || pending.OptionHeader is not { Length: <= 4096 }
            || pending.TargetIdentity is null
            || pending.Kind == "remove" && !record.Added.Any(column => column.Identity == pending.TargetIdentity)))
            throw new InvalidDataException("Une commande du journal de colonnes est invalide. Elle ne sera ni envoyée ni rejouée.");
    }
}
