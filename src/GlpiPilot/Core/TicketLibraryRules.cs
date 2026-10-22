using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>Version ordering is based exclusively on the GLPI token, never on a client read clock.</summary>
public static class TicketLibraryRules
{
    public const int ProtocolVersion = 1;
    public const int MaximumDocumentBytes = 8 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, MaxDepth = 64 };

    public static string Key(TicketReference ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (string.IsNullOrWhiteSpace(ticket.Id) || ticket.Id.Length > 20 || !ticket.Id.All(char.IsAsciiDigit)
            || !Uri.TryCreate(ticket.Url, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0
            || uri.Scheme is not ("http" or "https") || !uri.AbsolutePath.EndsWith("/front/ticket.form.php", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La bibliothèque attend une fiche de ticket GLPI HTTP(S).");
        var ids = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => Uri.UnescapeDataString(p[0]) == "id").ToArray();
        if (ids.Length != 1 || ids[0].Length != 2 || Uri.UnescapeDataString(ids[0][1]).TrimStart('0') != ticket.Id.TrimStart('0'))
            throw new InvalidDataException("L’adresse de la fiche ne correspond pas à son identifiant.");
        return ObjectIdentity.Key(ticket.Url, "Ticket", ticket.Id);
    }

    public static bool CanSkip(TicketLibrarySeed seed, TicketLibraryDocument? stored, string readerSignature, TicketLibraryCachePolicy? policy = null)
    {
        policy ??= new();
        if (policy.ForceRefresh || stored is null || !stored.DetailComplete || !IsContentConfirmed(stored)
            || string.IsNullOrEmpty(readerSignature) || stored.ReaderSignature != readerSignature
            || !seed.LastModifiedReliable || !stored.Seed.LastModifiedReliable || seed.LastModifiedAt is not { } modified
            || stored.ReadAt is not { } read || seed.LastModifiedRaw.Length == 0 || stored.DetailModifiedRaw != seed.LastModifiedRaw
            || stored.DetailModifiedAt != modified || stored.Seed.LastModifiedPrecision != seed.LastModifiedPrecision
            || Key(seed.Ticket) != stored.Key || policy.MaximumAge <= TimeSpan.Zero || policy.SafetyMargin < TimeSpan.Zero)
            return false;
        var interval = Interval(seed);
        return interval > TimeSpan.Zero && read >= modified + interval + policy.SafetyMargin
            && policy.Now >= read && policy.Now - read <= policy.MaximumAge;
    }

    public static bool IsContentConfirmed(TicketLibraryDocument? document) => document is { Detail: not null, DetailsCurrent: true, ConfirmationPending: false }
        && document.ContentHash.Length == 64 && document.ConfirmedContentHash == document.ContentHash && Guid.TryParseExact(document.ConfirmationId, "N", out _);

    internal static TimeSpan Interval(TicketLibrarySeed seed) => seed.LastModifiedPrecision switch
    { "seconds" => TimeSpan.FromSeconds(1), "minutes" => TimeSpan.FromMinutes(1), _ => TimeSpan.Zero };

    /// <returns>1 newer, -1 older, 0 same proven version, null incomparable/overlapping intervals.</returns>
    public static int? CompareVersion(TicketLibrarySeed incoming, TicketLibrarySeed current)
    {
        if (!incoming.LastModifiedReliable || !current.LastModifiedReliable || incoming.LastModifiedAt is not { } a
            || current.LastModifiedAt is not { } b || Interval(incoming) is var ai && ai == TimeSpan.Zero
            || Interval(current) is var bi && bi == TimeSpan.Zero) return null;
        // Different workstation date/fuseau preferences cannot turn the same observed
        // GLPI token into proof that one source version is newer than the other.
        if (a != b && incoming.LastModifiedRaw.Length > 0
            && string.Equals(incoming.LastModifiedRaw, current.LastModifiedRaw, StringComparison.Ordinal)) return null;
        if (a == b && incoming.LastModifiedPrecision == current.LastModifiedPrecision) return 0;
        if (a >= b + Interval(current)) return 1;
        if (a + Interval(incoming) <= b) return -1;
        return null;
    }

    public static TicketLibrarySeed ValidateSeed(TicketLibrarySeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        _ = Key(seed.Ticket);
        if (seed.Title is null || seed.Title.Length > 10000 || seed.Status is null || seed.Status.Length > 2000
            || seed.Category is null || seed.Category.Length > 10000 || seed.LastModifiedRaw is null || seed.LastModifiedRaw.Length > 1000
            || seed.LastModifiedPrecision is not ("unknown" or "minutes" or "seconds")
            || seed.Fields is null || seed.Fields.Count > 500 || seed.Fields.Any(p => p.Key.Length > 1000 || p.Value is null || p.Value.Length > 500000)
            || seed.IndexHeaders is null || seed.IndexValues is null || seed.IndexHeaders.Count > 500
            || seed.IndexHeaders.Count != seed.IndexValues.Count || seed.IndexHeaders.Any(s => s is null || s.Length > 10000)
            || seed.IndexValues.Any(s => s is null || s.Length > 500000)
            || seed.LastModifiedReliable && (seed.LastModifiedRaw.Length == 0 || seed.LastModifiedPrecision == "unknown"))
            throw new InvalidDataException("Les métadonnées du ticket de bibliothèque sont invalides.");
        return seed;
    }

    internal static TicketLibraryDocument FromSeed(TicketLibrarySeed seed)
    {
        var copy = Copy(ValidateSeed(seed));
        return Seal(new() { Key = Key(copy.Ticket), Installation = ObjectIdentity.Installation(copy.Ticket.Url), Seed = copy });
    }

    internal static TicketLibraryDocument FromDetail(TicketLibrarySeed seed, TicketDetailResult detail, TicketLibraryCoverage coverage, string readerSignature)
    {
        var document = FromSeed(seed);
        if (detail is null || coverage is null || Key(detail.Ticket) != document.Key || string.IsNullOrEmpty(readerSignature) || readerSignature.Length > 200)
            throw new InvalidDataException("La lecture ne correspond pas au ticket de bibliothèque.");
        document.Detail = Copy(detail); document.Coverage = Copy(coverage); document.ReaderSignature = readerSignature;
        document.DetailComplete = coverage.Complete && coverage.TabsLoaded == coverage.TabsDiscovered
            && detail.Sections.Count > 0 && detail.Tabs.All(t => t.Loaded) && detail.Sections.All(s => s.Data.Complete);
        document.DetailsCurrent = true;
        document.DetailModifiedRaw = seed.LastModifiedRaw; document.DetailModifiedAt = seed.LastModifiedAt; document.ReadAt = detail.ReadAt;
        Seal(document);
        // This low-level API is for an already verified read. Browser acquisition uses
        // SaveCaptureDetailAsync, which removes this proof before persisting the body.
        document.ConfirmationId = Guid.NewGuid().ToString("N"); document.ConfirmedContentHash = document.ContentHash;
        return document;
    }

    internal static TicketLibraryDocument Seal(TicketLibraryDocument document)
    {
        Validate(document, checkHash: false);
        document.ContentHash = Hash(document);
        return document;
    }

    public static TicketLibraryDocument Validate(TicketLibraryDocument document, bool checkHash = true)
    {
        if (document is null || document.Format != ProtocolVersion) throw new InvalidDataException("Version du document de bibliothèque non prise en charge.");
        ValidateSeed(document.Seed);
        if (document.Key != Key(document.Seed.Ticket) || document.Installation != ObjectIdentity.Installation(document.Seed.Ticket.Url)
            || document.Coverage is null || document.Coverage.TabsDiscovered < 0 || document.Coverage.TabsDiscovered > 200
            || document.Coverage.TabsLoaded < 0 || document.Coverage.TabsLoaded > document.Coverage.TabsDiscovered
            || document.Coverage.Limitations is null || document.Coverage.Limitations.Count > 500
            || document.Coverage.Limitations.Any(s => s is null || s.Length > 10000) || document.ReaderSignature is null || document.ReaderSignature.Length > 200
            || document.DetailModifiedRaw is null || document.DetailModifiedRaw.Length > 1000)
            throw new InvalidDataException("Structure du document de bibliothèque invalide.");
        if (document.Detail is { } detail)
        {
            if (Key(detail.Ticket) != document.Key || document.ReadAt != detail.ReadAt || detail.Title is null || detail.Title.Length > 10000
                || detail.Sections is null || detail.Sections.Count > 200 || detail.Tabs is null || detail.Tabs.Count > 200
                || detail.Warnings is null || detail.Warnings.Count > 500 || detail.ObservedItems is null || detail.ObservedItems.Count > 6000)
                throw new InvalidDataException("Contenu de la fiche de bibliothèque invalide.");
            long cells = 0;
            foreach (var section in detail.Sections)
            {
                if (section is null || section.Data is null || section.Name is null || section.Kind is null || section.TabKey is null
                    || section.Data.Headers is null || section.Data.Rows is null || section.Data.Headers.Count > 500
                    || section.Data.Rows.Count > 100000 || section.Data.Headers.Any(s => s is null || s.Length > 10000)
                    || section.Data.Rows.Any(r => r is null || r.Any(s => s is null || s.Length > 500000)))
                    throw new InvalidDataException("Section de la fiche de bibliothèque invalide.");
                cells += section.Data.Rows.Sum(r => (long)r.Length);
                if (cells > 120000) throw new InvalidDataException("La fiche de bibliothèque dépasse 120 000 cellules.");
            }
            if (detail.Tabs.Any(t => t is null || t.Key is null || t.Name is null)
                || detail.ObservedItems.Any(i => i is null || i.Title is null || i.Title.Length > 500000))
                throw new InvalidDataException("Onglet ou observation de bibliothèque invalide.");
            if (document.DetailComplete && (!document.Coverage.Complete || detail.Sections.Count == 0
                || document.Coverage.TabsLoaded != document.Coverage.TabsDiscovered || detail.Tabs.Any(t => !t.Loaded) || detail.Sections.Any(s => !s.Data.Complete)))
                throw new InvalidDataException("Une lecture partielle ne peut pas être déclarée complète.");
            if (document.DetailsCurrent && (document.DetailModifiedRaw != document.Seed.LastModifiedRaw || document.DetailModifiedAt != document.Seed.LastModifiedAt))
                throw new InvalidDataException("Le contenu ne porte pas la version actuelle du ticket.");
        }
        else if (document.DetailComplete || document.DetailsCurrent || document.ReadAt is not null)
            throw new InvalidDataException("La fiche déclarée n’est pas présente dans la bibliothèque.");
        if (document.ConfirmationId is null || document.ConfirmationId.Length != 0 && !Guid.TryParseExact(document.ConfirmationId, "N", out _)
            || document.ConfirmedContentHash is null || document.ConfirmedContentHash.Length > 64
            || document.ConfirmationPending && (document.Detail is null || document.DetailsCurrent || document.ConfirmedContentHash.Length > 0)
            || document.ConfirmedContentHash.Length > 0 && (!document.DetailsCurrent || document.ConfirmedContentHash != document.ContentHash || document.ConfirmationId.Length == 0)
            || !document.DetailsCurrent && !document.ConfirmationPending && document.ConfirmationId.Length > 0)
            throw new InvalidDataException("La confirmation ne correspond pas au contenu de bibliothèque.");
        if (Encoding.UTF8.GetByteCount(Serialize(document)) > MaximumDocumentBytes)
            throw new InvalidDataException("Une fiche de bibliothèque dépasse 8 Mio.");
        if (checkHash && document.ContentHash != Hash(document)) throw new InvalidDataException("L’empreinte du document de bibliothèque est invalide.");
        return document;
    }

    internal static string Hash(TicketLibraryDocument document)
    {
        // Observation clocks, DOM selectors and local/central revisions do not define GLPI content.
        var d = document.Detail;
        var content = new
        {
            document.Key, Seed = SeedContent(document.Seed), document.DetailComplete,
            document.DetailModifiedRaw, document.DetailModifiedAt,
            Detail = d is null ? null : new
            {
                d.Title,
                Sections = d.Sections.Select(s => new { s.Name, s.Kind, s.TabName, s.TabKey, s.Data.Headers, s.Data.Rows, s.Data.Complete }),
                Tabs = d.Tabs.Select(t => new { t.Key, t.Name, t.Category, t.Loaded }),
                Observed = d.ObservedItems.Select(i => new { i.Kind, i.Identity, i.SourceSection, i.Title, i.Date, i.Author, i.AuthorRole, i.Owner, i.Status, i.Due, i.PlannedStart, i.PlannedEnd, i.CompletedAt, i.RequestedAt, i.WaitingStart, i.WaitingEnd, i.WaitingReason, i.Url, i.RelatedType })
            },
            Coverage = new { document.Coverage.TabsDiscovered, document.Coverage.TabsLoaded, document.Coverage.Complete, document.Coverage.Limitations }
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, Json)))).ToLowerInvariant();
    }

    private static object SeedContent(TicketLibrarySeed s) => new
    {
        s.Title, s.Status, s.StatusCode, s.Category, s.LastModifiedRaw, s.LastModifiedAt, s.LastModifiedReliable, s.LastModifiedPrecision,
        Fields = s.Fields.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(), s.IndexHeaders, s.IndexValues
    };

    internal static bool SameSeed(TicketLibrarySeed a, TicketLibrarySeed b) => JsonSerializer.Serialize(SeedContent(a), Json) == JsonSerializer.Serialize(SeedContent(b), Json);
    internal static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static TicketLibraryDocument Deserialize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes) throw new InvalidDataException("Une fiche de bibliothèque dépasse 8 Mio.");
        return QualifyLegacy(Validate(JsonSerializer.Deserialize<TicketLibraryDocument>(json, Json) ?? throw new InvalidDataException("Fiche de bibliothèque vide.")));
    }

    private static TicketLibraryDocument QualifyLegacy(TicketLibraryDocument document)
    {
        if (document.DetailsCurrent && document.ConfirmedContentHash.Length == 0)
        {
            // Legacy 3.2.0 bytes remain available, but its pre-final-check flag is not a v2 proof.
            document = Copy(document); document.DetailsCurrent = false; document.ConfirmationPending = true; document.ConfirmationId = "";
        }
        return document;
    }

    internal static LibraryMerge Merge(TicketLibraryDocument? current, TicketLibraryDocument candidate)
    {
        Validate(candidate); candidate = QualifyLegacy(candidate);
        if (current is not null) current = QualifyLegacy(current);
        if (current is null) return new(Copy(candidate), "inserted", false);
        if (current.Key != candidate.Key) throw new InvalidDataException("Les deux versions ne désignent pas le même ticket.");
        if (current.ContentHash == candidate.ContentHash)
        {
            var same = Copy(current);
            // A final confirmation can promote only the exact body explicitly awaiting it.
            // A false state caused by a real version conflict is never lifted by a replay.
            var matchingConfirmation = current.ConfirmationId.Length > 0 && current.ConfirmationId == candidate.ConfirmationId
                // A legacy copy has no attempt to supersede. A new exact-body proof
                // received from another client may confirm it; a real conflict is not pending.
                || current.ConfirmationPending && current.ConfirmationId.Length == 0 && IsContentConfirmed(candidate);
            var replayPending = current.DetailsCurrent && candidate.ConfirmationPending && (matchingConfirmation || candidate.ConfirmationId.Length == 0);
            same.DetailsCurrent = current.DetailsCurrent && candidate.DetailsCurrent || replayPending
                || current.ConfirmationPending && candidate.DetailsCurrent && matchingConfirmation && candidate.ConfirmedContentHash == candidate.ContentHash;
            same.ConfirmationPending = !same.DetailsCurrent && (current.ConfirmationPending && (candidate.DetailsCurrent || candidate.ConfirmationPending)
                || current.DetailsCurrent && candidate.ConfirmationPending);
            var useCandidateObservation = same.DetailsCurrent
                ? candidate.DetailsCurrent && (!current.DetailsCurrent || candidate.ReadAt > current.ReadAt)
                : same.ConfirmationPending && candidate.ConfirmationPending && candidate.ConfirmationId.Length > 0;
            same.ConfirmationId = same.DetailsCurrent
                ? (useCandidateObservation ? candidate.ConfirmationId : current.ConfirmationId)
                : same.ConfirmationPending ? (candidate.ConfirmationPending && candidate.ConfirmationId.Length > 0 ? candidate.ConfirmationId : current.ConfirmationId) : "";
            same.ConfirmedContentHash = same.DetailsCurrent
                ? (useCandidateObservation ? candidate.ConfirmedContentHash : current.ConfirmedContentHash) : "";
            if (useCandidateObservation)
            { same.Detail = Copy(candidate.Detail); same.ReadAt = candidate.ReadAt; same.ReaderSignature = candidate.ReaderSignature; }
            if (candidate.Seed.ObservedAt > current.Seed.ObservedAt) same.Seed.ObservedAt = candidate.Seed.ObservedAt;
            return new(same, "identical", false);
        }
        var version = CompareVersion(candidate.Seed, current.Seed);
        if (version == -1) return new(Copy(current), "older-source-version", true);
        if (version == 1)
        {
            if (candidate.Detail is null || !candidate.DetailComplete && current.DetailComplete)
            {
                var next = Copy(current); next.Seed = Copy(candidate.Seed); next.DetailsCurrent = false;
                next.ConfirmationPending = false; next.ConfirmationId = ""; next.ConfirmedContentHash = "";
                return new(Seal(next), candidate.Detail is null ? "newer-metadata" : "newer-partial-read", candidate.Detail is not null);
            }
            return new(Copy(candidate), "newer-source-version", false);
        }
        // Initial content enriches an identical index entry; this cannot replace an existing complete read.
        if (SameSeed(candidate.Seed, current.Seed) && candidate.Detail is not null
            && (current.Detail is null || !current.DetailsCurrent && candidate.DetailComplete && current.DetailModifiedAt != candidate.DetailModifiedAt
                || !current.DetailComplete && candidate.DetailComplete))
            return new(Copy(candidate), "content-added", false);
        if (candidate.Detail is null && SameSeed(candidate.Seed, current.Seed)) return new(Copy(current), "identical", false);
        // Unknown or equal GLPI versions never use client read times to choose a winner.
        var uncertain = Copy(current); uncertain.DetailsCurrent = false;
        uncertain.ConfirmationPending = false; uncertain.ConfirmationId = ""; uncertain.ConfirmedContentHash = "";
        return new(Seal(uncertain), version == 0 ? "same-version-different-content" : "incomparable-source-version", true);
    }

    internal sealed record LibraryMerge(TicketLibraryDocument Document, string Outcome, bool Conflict);
}
