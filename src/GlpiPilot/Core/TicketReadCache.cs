using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

public sealed class TicketReadCacheEntry
{
    public bool HasContent { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public TicketReadSourceVersion? Version { get; set; }
    public bool FromLibrary { get; set; }
}

public sealed class TicketReadCacheStatus
{
    public string State { get; set; } = "unknown";
    public bool HasContent { get; set; }
    public bool AutoRead { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? SourceModifiedAt { get; set; }
    public string SourceModifiedRaw { get; set; } = "";
    public DateTimeOffset? CachedModifiedAt { get; set; }
    public string CachedModifiedRaw { get; set; } = "";
    public DateTimeOffset? SourceObservedAt { get; set; }
    public string SourceToken { get; set; } = "";
}

public static class TicketReadCache
{
    public static readonly TimeSpan UnknownVersionRetryDelay = TimeSpan.FromMinutes(5);
    public static string Fingerprint(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

    public static TicketReadSourceVersion Source(TicketReference ticket, ExtractionResult data, int rowIndex, AppSettings settings)
    {
        var column = DashboardService.ResolveColumns(data.Headers, settings.Dashboard).First(column => column.Role == "updated").Index;
        var raw = column is int index && index < data.Rows[rowIndex].Length ? data.Rows[rowIndex][index] : "";
        var time = Regex.Match(raw.Trim(), @"[ T]\d{2}:\d{2}(?<seconds>:\d{2})?(?:Z|[+-]\d{2}:?\d{2})?$", RegexOptions.CultureInvariant);
        var parsed = DashboardService.TrySourceDate(raw, settings.Dashboard, data.ExtractedAt.Offset, out var date);
        var instant = parsed ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), data.ExtractedAt.Offset) : (DateTimeOffset?)null;
        return new()
        {
            TicketKey = WorkplaceStore.TicketKey(ticket),
            Context = Fingerprint(new { installation = ObjectIdentity.Installation(ticket.Url), data.Provenance.SourceKind,
                settings.BrowserChannel, settings.Dashboard.DateCulture, settings.Dashboard.SourceTimeZone,
                settings.TicketDetails }),
            ModifiedRaw = raw.Length <= 1000 ? raw : "", ModifiedAt = instant,
            Precision = time.Success ? time.Groups["seconds"].Success ? "seconds" : "minutes" : "unknown",
            Reliable = time.Success && instant.HasValue && instant <= data.ExtractedAt && raw.Length <= 1000,
            ObservedAt = data.ExtractedAt,
            RowFingerprint = Fingerprint(new { data.Headers, values = data.Rows[rowIndex] })
        };
    }

    public static bool MainReadComplete(TicketDetailResult detail) => detail.Sections.Count > 0 && detail.Sections.All(section => section.Data.Complete);

    public static DateTimeOffset? MainReadAt(TicketReadCacheEntry? cached) =>
        cached?.Version?.MainReadAt ?? (cached?.FromLibrary == true ? cached.ReadAt : null);

    /// <summary>Choose a body without treating a later personal tab as a new main-ticket read.</summary>
    public static bool PreferLibrary(TicketReadSourceVersion? source, TicketReadCacheEntry? cached, TicketReadCacheEntry? library)
    {
        if (library?.HasContent != true) return false;
        if (cached?.HasContent != true) return true;
        var currentRead = MainReadAt(cached); var libraryRead = MainReadAt(library);
        // Body observation dates are independent of source-date parsing. Personal
        // and library tokens may use different timezone/date settings even when
        // both receipts say Reliable. Keep known body chronology authoritative.
        if (currentRead is { } kept && libraryRead is { } offered) return offered > kept;
        var current = cached.Version; var incoming = library.Version;
        // An undated legacy receipt may be ordered only within its recorded source
        // context and against a library version confirmed by that same source. An
        // older source cannot displace a newer personal version, and no last-tab
        // timestamp supplies the missing body date.
        if (currentRead is null && libraryRead is not null && source is not null
            && current is { Reliable: true, ModifiedAt: { } currentModified, Precision: "seconds" or "minutes" }
            && incoming is { Reliable: true, ModifiedAt: { } incomingModified, Precision: "seconds" or "minutes" }
            && current.TicketKey == source.TicketKey && current.TicketKey == incoming.TicketKey
            && current.Context == source.Context && current.ModifiedRaw != incoming.ModifiedRaw
            && Describe(source, library, false).State == "current")
        {
            var difference = incomingModified - currentModified;
            return difference >= (current.Precision == "seconds" ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1));
        }
        return false;
    }

    public static TicketReadSourceVersion? Observe(TicketReadSourceVersion? source, TicketDetailResult detail)
    {
        if (source is null || source.TicketKey != WorkplaceStore.TicketKey(detail.Ticket)) return null;
        var result = source.Copy(); result.MainReadComplete = MainReadComplete(detail); result.MainReadAt = detail.ReadAt;
        var precision = source.Precision == "seconds" ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1);
        result.Reliable = source.Reliable && source.ObservedAt <= detail.ReadAt
            && source.ModifiedAt + precision + TimeSpan.FromMinutes(2) <= detail.ReadAt;
        return result;
    }

    public static TicketReadCacheStatus Describe(TicketReadSourceVersion? source, TicketReadCacheEntry? cached,
        bool allowAutomatic, bool lookupKnown = true, string unavailableReason = "", DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        // A tab can update the combined view's ReadAt without reading the main body.
        // Library receipts describe the body itself. Legacy personal copies without
        // a separate main-read date do not borrow their last tab's timestamp.
        var mainReadAt = MainReadAt(cached);
        var status = new TicketReadCacheStatus
        {
            HasContent = cached?.HasContent == true, ReadAt = mainReadAt,
            SourceModifiedAt = source?.ModifiedAt, SourceModifiedRaw = source?.ModifiedRaw ?? "",
            CachedModifiedAt = cached?.Version?.ModifiedAt, CachedModifiedRaw = cached?.Version?.ModifiedRaw ?? "",
            SourceObservedAt = source?.ObservedAt,
            SourceToken = source is null ? "" : Fingerprint(new { source.TicketKey, source.Context, source.ModifiedRaw,
                source.ModifiedAt, source.Precision, source.Reliable, source.RowFingerprint }),
            AutoRead = allowAutomatic
        };
        if (!status.HasContent)
        {
            status.State = lookupKnown ? "missing" : "unknown";
            status.Reason = lookupKnown ? "Aucune copie de cette fiche n’est disponible dans les caches locaux."
                : unavailableReason.Length > 0 ? unavailableReason : "La présence d’une copie locale n’a pas pu être vérifiée.";
            return status;
        }
        var version = cached!.Version;
        var sameContext = source is not null && version is not null && source.TicketKey == version.TicketKey
            && (cached.FromLibrary || source.Context == version.Context);
        var sameVersion = sameContext && source!.Reliable && version!.Reliable
            && source.ModifiedRaw == version.ModifiedRaw && source.ModifiedAt == version.ModifiedAt && source.Precision == version.Precision
            && (cached.FromLibrary || source.RowFingerprint == version.RowFingerprint);
        if (sameVersion && version!.MainReadComplete)
        {
            status.State = "current"; status.AutoRead = false;
            status.Reason = "La copie correspond à la même version que le relevé. Les onglets non lus restent à charger explicitement.";
            return status;
        }
        // Even a read made too near the prior modification to prove freshness
        // must be invalidated when the observed modification itself changes.
        var changed = sameContext && source!.Reliable && version!.ModifiedAt.HasValue && version.Precision != "unknown"
            && (source.ModifiedRaw != version.ModifiedRaw || source.ModifiedAt != version.ModifiedAt
                || source.Precision != version.Precision || !cached.FromLibrary && source.RowFingerprint != version.RowFingerprint);
        if (changed)
        {
            status.State = "stale"; status.Reason = "Le repère de modification ou les valeurs du relevé ont changé depuis cette lecture.";
            return status;
        }
        if (version?.MainReadComplete == true && (sameContext || source is null) && mainReadAt is { } readAt
            && clock >= readAt && clock - readAt <= UnknownVersionRetryDelay)
        {
            status.State = "recent"; status.AutoRead = false;
            var precision = version.Precision == "seconds" ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1);
            var closeModification = !cached.FromLibrary && sameContext && source!.Reliable && !version.Reliable
                && version.ModifiedAt.HasValue && version.Precision is "seconds" or "minutes" && version.ObservedAt <= readAt
                && version.ModifiedAt + precision + TimeSpan.FromMinutes(2) > readAt;
            status.Reason = closeModification
                ? "Lecture récente, version à vérifier : la date de dernière modification est connue, mais cette lecture est trop proche de cette modification pour confirmer la version."
                : source?.ModifiedAt is not null || version.ModifiedAt is not null
                    ? "Lecture récente, version à vérifier : une date de dernière modification est disponible, mais elle ne suffit pas à confirmer la version de cette copie."
                    : "Lecture récente, version à vérifier : la date de dernière modification est absente ou ne peut pas être interprétée de façon fiable.";
        }
        else
        {
            status.State = "unknown";
            status.Reason = unavailableReason.Length > 0 ? unavailableReason
                : version is { MainReadComplete: false } ? "La lecture principale est incomplète ; une nouvelle lecture reste nécessaire."
                : "La version de cette copie ne peut pas être comparée de façon fiable au relevé courant.";
        }
        return status;
    }
}
