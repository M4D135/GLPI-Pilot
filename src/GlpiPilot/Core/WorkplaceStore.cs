using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class PersonalChecklistItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public PersonalChecklistItem Copy() => new() { Text = Text, Done = Done };
}

public sealed class TicketReadingPosition
{
    public string SectionKey { get; set; } = "";
    public string Search { get; set; } = "";
    public string WholeTicketSearch { get; set; } = "";
    public string IntelligenceSearch { get; set; } = "";
    public int ViewMode { get; set; }
    public string Type { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public double ScrollOffset { get; set; }
    public int RowIndex { get; set; } = -1;
    public int PageIndex { get; set; }
    public TicketReadingPosition Copy() => (TicketReadingPosition)MemberwiseClone();
}

public sealed class PersonalTicket
{
    public string Key { get; set; } = "";
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Pinned { get; set; }
    public bool Offline { get; set; }
    public int? QueueOrder { get; set; }
    public string Notes { get; set; } = "";
    public string FollowupDraft { get; set; } = "";
    public string LastActionReceipt { get; set; } = "";
    public List<PersonalChecklistItem> Checklist { get; set; } = new();
    public DateTimeOffset? ReminderAt { get; set; }
    public DateTimeOffset? ReminderAcknowledgedAt { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public string SnoozeReason { get; set; } = "";
    public DateTimeOffset? LastOpenedAt { get; set; }
    public DateTimeOffset? LastReadAt { get; set; }
    public string PreferredSection { get; set; } = "";
    public TicketReadingPosition ReadingPosition { get; set; } = new();
    public Dictionary<string, List<string>> RowFingerprints { get; set; } = new(StringComparer.Ordinal);
    public bool RowsFullyCompared { get; set; } = true;
    public Dictionary<string, string> SectionFingerprints { get; set; } = new(StringComparer.Ordinal);
    public PersonalTicket Copy() => new()
    {
        Key = Key, Id = Id, Url = Url, Title = Title, Pinned = Pinned, Offline = Offline,
        QueueOrder = QueueOrder, Notes = Notes, FollowupDraft = FollowupDraft, LastActionReceipt = LastActionReceipt, ReminderAt = ReminderAt,
        ReminderAcknowledgedAt = ReminderAcknowledgedAt, SnoozedUntil = SnoozedUntil, SnoozeReason = SnoozeReason,
        LastOpenedAt = LastOpenedAt, LastReadAt = LastReadAt, PreferredSection = PreferredSection,
        Checklist = Checklist.Select(item => item.Copy()).ToList(),
        SectionFingerprints = new(SectionFingerprints, StringComparer.Ordinal),
        ReadingPosition = ReadingPosition.Copy(), RowFingerprints = RowFingerprints.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.Ordinal), RowsFullyCompared = RowsFullyCompared
    };
}

/// <summary>Personal state stays on this computer and never changes a GLPI ticket.</summary>
public sealed class WorkplaceDocument
{
    public int Version { get; set; } = 1;
    public List<PersonalTicket> Tickets { get; set; } = new();
    public List<string> OpenTicketKeys { get; set; } = new();
    public string ActiveTicketKey { get; set; } = "";
    public List<string> FavoriteSections { get; set; } = new() { "treatment", "overview" };
    public string DefaultSection { get; set; } = "treatment";
    public List<string> SectionOrder { get; set; } = new();
    public string DaySearch { get; set; } = "";
    public int DayScope { get; set; }
    public string DaySelectedKey { get; set; } = "";
    public DateTimeOffset? LastSessionStartedAt { get; set; }
    public string DayBaselineScope { get; set; } = "";
    public DateTimeOffset? DayBaselineAt { get; set; }
    public List<PersonalDayObservation> DayBaseline { get; set; } = new();
    public bool GroupTimelineByDay { get; set; } = true;
    public bool DesktopReminders { get; set; } = true;
    public string ActiveWorkspace { get; set; } = "team";
    public DateTimeOffset? LastDayViewedAt { get; set; }
    public Dictionary<string, List<string>> ChecklistTemplates { get; set; } = new(StringComparer.Ordinal)
    {
        ["Intervention"] = new() { "Vérifier le contexte et le contact", "Préparer le matériel", "Vérifier le résultat", "Préparer le compte rendu" },
        ["Passation"] = new() { "Résumer le contexte", "Préciser la prochaine action", "Identifier le relais", "Vérifier les échéances connues" }
    };
    public WorkplaceDocument Copy() => new()
    {
        Version = Version, Tickets = Tickets.Select(ticket => ticket.Copy()).ToList(),
        OpenTicketKeys = OpenTicketKeys.ToList(), ActiveTicketKey = ActiveTicketKey,
        FavoriteSections = FavoriteSections.ToList(), DefaultSection = DefaultSection, SectionOrder = SectionOrder.ToList(),
        DaySearch = DaySearch, DayScope = DayScope, DaySelectedKey = DaySelectedKey, LastSessionStartedAt = LastSessionStartedAt,
        DayBaselineScope = DayBaselineScope, DayBaselineAt = DayBaselineAt, DayBaseline = DayBaseline.ToList(),
        GroupTimelineByDay = GroupTimelineByDay, DesktopReminders = DesktopReminders, ActiveWorkspace = ActiveWorkspace, LastDayViewedAt = LastDayViewedAt,
        ChecklistTemplates = ChecklistTemplates.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal)
    };
}

public sealed record TicketReadChanges(DateTimeOffset? PreviousReadAt, IReadOnlyList<string> AddedSections,
    IReadOnlyList<string> ChangedSections, IReadOnlyList<string> MissingSections)
{
    public Dictionary<string, IReadOnlySet<string>> NewOrChangedRows { get; init; } = new(StringComparer.Ordinal);
    public bool RowComparisonComplete { get; init; } = true;
    public bool HasPrevious => PreviousReadAt.HasValue;
    public string Summary => !HasPrevious ? "Première lecture mémorisée."
        : $"Depuis le {PreviousReadAt!.Value.LocalDateTime:dd/MM à HH:mm} : {AddedSections.Count} rubrique(s) nouvelle(s), {ChangedSections.Count} modifiée(s), {MissingSections.Count} non relue(s).";
}

public static class WorkplaceStore
{
    public const int MaximumTickets = 500;
    public const int MaximumOpenTickets = 12;
    public const int MaximumNotes = 8000;
    public const int MaximumChecklistItems = 40;
    public const int MaximumBytes = 12 * 1024 * 1024;
    public const int MaximumCachedBytes = 8 * 1024 * 1024;
    public const long MaximumCacheBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 32 };

    public static string TicketKey(TicketReference ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (string.IsNullOrWhiteSpace(ticket.Id) || ticket.Id.Length > 160 || ticket.Id.Any(char.IsControl)
            || !Uri.TryCreate(ticket.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new InvalidDataException("La référence du ticket n’est pas une adresse web valide.");
        var origin = uri.GetLeftPart(UriPartial.Path);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin + "\n" + ticket.Id))).ToLowerInvariant();
    }

    public static WorkplaceDocument Load(string path)
    {
        if (!File.Exists(path)) return new();
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("L’espace personnel dépasse la taille autorisée.");
        return Validate(JsonSerializer.Deserialize<WorkplaceDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("L’espace personnel est vide."));
    }

    public static void Save(string path, WorkplaceDocument document)
    {
        var checkedDocument = Validate(document);
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(checkedDocument, JsonOptions), MaximumBytes);
    }

    public static WorkplaceDocument Validate(WorkplaceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != 1 || document.Tickets == null || document.Tickets.Count > MaximumTickets
            || document.OpenTicketKeys == null || document.OpenTicketKeys.Count > MaximumOpenTickets)
            throw new InvalidDataException("La structure ou les limites de l’espace personnel ne sont pas reconnues.");
        var result = new WorkplaceDocument
        {
            ActiveTicketKey = document.ActiveTicketKey ?? "", DefaultSection = Clean(document.DefaultSection, 300),
            GroupTimelineByDay = document.GroupTimelineByDay, DesktopReminders = document.DesktopReminders, ActiveWorkspace = Clean(document.ActiveWorkspace, 40),
            LastDayViewedAt = document.LastDayViewedAt, ChecklistTemplates = new(StringComparer.Ordinal),
            FavoriteSections = (document.FavoriteSections ?? new()).Select(item => Clean(item, 300)).Distinct(StringComparer.Ordinal).ToList(),
            SectionOrder = (document.SectionOrder ?? new()).Select(item => Clean(item, 1000)).Distinct(StringComparer.Ordinal).ToList(),
            DaySearch = Clean(document.DaySearch, 300), DayScope = Math.Clamp(document.DayScope, 0, 6), DaySelectedKey = Clean(document.DaySelectedKey, 64), LastSessionStartedAt = document.LastSessionStartedAt,
            DayBaselineScope = Clean(document.DayBaselineScope, 64), DayBaselineAt = document.DayBaselineAt, DayBaseline = document.DayBaseline?.ToList() ?? new()
        };
        if (result.DayBaseline.Count > 5000 || result.DayBaseline.Any(item => item == null || item.Key?.Length != 64 || !item.Key.All(Uri.IsHexDigit) || item.Id == null || item.Id.Length > 160 || item.Title == null || item.Title.Length > 2000 || item.Status == null || item.Status.Length > 500 || item.Due == null || item.Due.Length > 100))
            throw new InvalidDataException("Le repère de briefing est invalide ou dépasse 5 000 dossiers.");
        if (result.SectionOrder.Count > 200 || result.FavoriteSections.Count > 60 || document.ChecklistTemplates == null || document.ChecklistTemplates.Count > 30)
            throw new InvalidDataException("Trop de rubriques favorites ou de modèles personnels.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ticket in document.Tickets)
        {
            if (ticket == null || TicketKey(new(ticket.Id, ticket.Url)) != ticket.Key || !keys.Add(ticket.Key))
                throw new InvalidDataException("Une identité de ticket est invalide ou dupliquée dans l’espace personnel.");
            if (ticket.QueueOrder is < 0 or > MaximumTickets || ticket.Checklist == null || ticket.Checklist.Count > MaximumChecklistItems || ticket.Checklist.Any(item => item == null)
                || ticket.SectionFingerprints == null || ticket.SectionFingerprints.Count > 100)
                throw new InvalidDataException("Les limites d’un ticket personnel sont dépassées.");
            if (ticket.RowFingerprints == null || ticket.RowFingerprints.Count > 100 || ticket.RowFingerprints.Sum(p => p.Value?.Count ?? 10001) > 10000
                || ticket.RowFingerprints.Any(p => p.Key.Length > 1000 || p.Value.Any(v => v == null || v.Length != 64 || !v.All(Uri.IsHexDigit))) || ticket.ReadingPosition == null)
                throw new InvalidDataException("Les empreintes détaillées ou la position de lecture sont invalides.");
            var copy = ticket.Copy();
            copy.ReadingPosition.SectionKey = Clean(copy.ReadingPosition.SectionKey, 1000);
            copy.ReadingPosition.Search = Clean(copy.ReadingPosition.Search, 500);
            copy.ReadingPosition.WholeTicketSearch = Clean(copy.ReadingPosition.WholeTicketSearch, 500);
            copy.ReadingPosition.IntelligenceSearch = Clean(copy.ReadingPosition.IntelligenceSearch, 300);
            copy.ReadingPosition.Type = Clean(copy.ReadingPosition.Type, 100);
            copy.ReadingPosition.Author = Clean(copy.ReadingPosition.Author, 200);
            if (!double.IsFinite(copy.ReadingPosition.ScrollOffset) || copy.ReadingPosition.ScrollOffset < 0 || copy.ReadingPosition.RowIndex is < -1 or > 100000 || copy.ReadingPosition.PageIndex is < 0 or > 20 || copy.ReadingPosition.ViewMode is < 0 or > 1)
                throw new InvalidDataException("La position de lecture est invalide.");
            copy.Url = Clean(ticket.Url, 8192); copy.Title = Clean(ticket.Title, 2000);
            copy.Notes = Clean(ticket.Notes, MaximumNotes, multiline: true);
            copy.FollowupDraft = Clean(ticket.FollowupDraft, 20000, multiline: true);
            copy.LastActionReceipt = Clean(ticket.LastActionReceipt, 40000, multiline: true);
            copy.SnoozeReason = Clean(ticket.SnoozeReason, 1200, multiline: true);
            copy.PreferredSection = Clean(ticket.PreferredSection, 300);
            copy.Checklist = ticket.Checklist.Select(item => item == null ? throw new InvalidDataException("Une tâche personnelle est invalide.")
                : new PersonalChecklistItem { Text = Clean(item.Text, 500), Done = item.Done }).ToList();
            copy.SectionFingerprints = ticket.SectionFingerprints.ToDictionary(pair => Clean(pair.Key, 1000), pair =>
                pair.Value?.Length == 64 && pair.Value.All(Uri.IsHexDigit) ? pair.Value : throw new InvalidDataException("Une empreinte de lecture est invalide."), StringComparer.Ordinal);
            result.Tickets.Add(copy);
        }
        result.OpenTicketKeys = document.OpenTicketKeys.Where(key => keys.Contains(key)).Distinct(StringComparer.Ordinal).ToList();
        if (!keys.Contains(result.ActiveTicketKey)) result.ActiveTicketKey = "";
        foreach (var pair in document.ChecklistTemplates)
        {
            if (pair.Value == null || pair.Value.Count > MaximumChecklistItems) throw new InvalidDataException("Le modèle de liste est trop long.");
            result.ChecklistTemplates.Add(Clean(pair.Key, 100), pair.Value.Select(text => Clean(text, 500)).Where(text => text.Length > 0).ToList());
        }
        return result;
    }

    public static PersonalTicket Remember(WorkplaceDocument document, TicketReference reference, string title, bool open = true)
    {
        var key = TicketKey(reference);
        var ticket = document.Tickets.FirstOrDefault(item => item.Key == key);
        if (ticket == null)
        {
            if (document.Tickets.Count >= MaximumTickets)
            {
                var disposable = document.Tickets.Where(item => !item.Pinned && !item.Offline && !item.QueueOrder.HasValue && item.Notes.Length == 0
                    && item.Checklist.Count == 0 && item.FollowupDraft.Length == 0 && item.LastActionReceipt.Length == 0 && !item.ReminderAt.HasValue && !document.OpenTicketKeys.Contains(item.Key))
                    .OrderBy(item => item.LastOpenedAt).FirstOrDefault();
                if (disposable == null) throw new InvalidDataException($"L’espace personnel contient déjà {MaximumTickets} tickets conservés. Retirez un dossier avant d’en ajouter un.");
                document.Tickets.Remove(disposable);
            }
            ticket = new() { Key = key, Id = reference.Id, Url = reference.Url };
            document.Tickets.Add(ticket);
        }
        ticket.Title = DisplayTitle(title);
        if (open)
        {
            ticket.LastOpenedAt = DateTimeOffset.Now;
            if (!document.OpenTicketKeys.Contains(key)) document.OpenTicketKeys.Add(key);
            while (document.OpenTicketKeys.Count > MaximumOpenTickets) document.OpenTicketKeys.RemoveAt(0);
            document.ActiveTicketKey = key;
        }
        return ticket;
    }

    public static void MoveQueue(WorkplaceDocument document, string key, int direction)
    {
        var queue = document.Tickets.Where(ticket => ticket.QueueOrder.HasValue).OrderBy(ticket => ticket.QueueOrder).ThenBy(ticket => ticket.Key, StringComparer.Ordinal).ToList();
        var index = queue.FindIndex(ticket => ticket.Key == key);
        if (index < 0) return;
        var next = Math.Clamp(index + Math.Sign(direction), 0, queue.Count - 1);
        (queue[index], queue[next]) = (queue[next], queue[index]);
        for (var position = 0; position < queue.Count; position++) queue[position].QueueOrder = position;
    }

    public static TicketReadChanges ObserveRead(PersonalTicket record, TicketDetailResult result)
    {
        var current = TicketReadingService.SectionFingerprints(result);
        var previous = record.SectionFingerprints;
        var changes = new TicketReadChanges(record.LastReadAt,
            current.Keys.Where(key => !previous.ContainsKey(key)).ToArray(),
            current.Keys.Where(key => previous.TryGetValue(key, out var digest) && current[key] != digest).ToArray(),
            previous.Keys.Where(key => !current.ContainsKey(key)).ToArray());
        var rowDigests = new Dictionary<string, List<string>>(StringComparer.Ordinal); var count = 0;
        foreach (var section in result.Sections)
        {
            var key = TicketReadingService.SectionKey(section);
            if (!rowDigests.TryGetValue(key, out var list)) rowDigests[key] = list = new();
            foreach (var row in section.Data.Rows)
            { if (count++ >= 10000) break; list.Add(TicketReadingService.RowFingerprint(row)); }
        }
        var addedRows = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        if (record.LastReadAt.HasValue && record.RowsFullyCompared)
            foreach (var pair in rowDigests)
            {
                var previousRows = record.RowFingerprints.GetValueOrDefault(pair.Key)?.ToHashSet(StringComparer.Ordinal) ?? new();
                addedRows[pair.Key] = pair.Value.Where(digest => !previousRows.Contains(digest)).ToHashSet(StringComparer.Ordinal);
            }
        changes = changes with { NewOrChangedRows = addedRows, RowComparisonComplete = count <= 10000 && record.RowsFullyCompared };
        record.RowFingerprints = rowDigests; record.RowsFullyCompared = count <= 10000;
        record.SectionFingerprints = current; record.LastReadAt = result.ReadAt;
        return changes;
    }

    public static void SaveOffline(string directory, TicketDetailResult detail)
    {
        var key = TicketKey(detail.Ticket);
        ValidateDetail(detail);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(detail, JsonOptions);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".json");
        var others = new DirectoryInfo(directory).EnumerateFiles("*.json").Where(file => file.Name != key + ".json").ToArray();
        if (others.Sum(file => file.Length) + bytes.Length > MaximumCacheBytes)
            throw new InvalidDataException("Le cache hors connexion atteint 64 Mo. Retirez une fiche hors connexion avant d’en conserver une autre.");
        WriteAtomic(path, bytes, MaximumCachedBytes);
    }

    public static TicketDetailResult? LoadOffline(string directory, TicketReference ticket)
    {
        var key = TicketKey(ticket); var path = Path.Combine(directory, key + ".json");
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumCachedBytes) throw new InvalidDataException("La fiche hors connexion dépasse la limite autorisée.");
        var detail = JsonSerializer.Deserialize<TicketDetailResult>(stream, JsonOptions) ?? throw new InvalidDataException("La fiche hors connexion est vide.");
        ValidateDetail(detail);
        if (TicketKey(detail.Ticket) != key) throw new InvalidDataException("La fiche hors connexion ne correspond pas au ticket demandé.");
        return detail;
    }

    public static void RemoveOffline(string directory, TicketReference ticket)
    {
        var path = Path.Combine(directory, TicketKey(ticket) + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    private static void ValidateDetail(TicketDetailResult detail)
    {
        _ = TicketKey(detail.Ticket);
        if (detail.Title == null || detail.Title.Length > 10000 || detail.Sections == null || detail.Sections.Count > 100
            || detail.Tabs == null || detail.Tabs.Count > 100 || detail.Warnings == null || detail.Warnings.Count > 200
            || detail.ObservedItems == null || detail.ObservedItems.Count > 6000 || detail.ObservedItems.Any(i => i == null || i.Title == null || i.Title.Length > 500000 || i.Url == null || i.Url.Length > 8192 || new[] { i.Kind, i.Identity, i.SourceSection, i.SourceTabKey, i.SourceTabSelector, i.SourceSelector, i.Date, i.Author, i.AuthorRole, i.Owner, i.Status, i.Due, i.PlannedStart, i.PlannedEnd, i.CompletedAt, i.RequestedAt, i.WaitingStart, i.WaitingEnd, i.WaitingReason, i.RelatedType }.Any(value => value == null || value.Length > 20000)))
            throw new InvalidDataException("La structure de la fiche hors connexion est invalide.");
        long cells = 0;
        foreach (var section in detail.Sections)
        {
            if (section == null || section.Data == null || section.Name == null || section.Kind == null || section.TabKey == null
                || section.Data.Headers == null || section.Data.Rows == null || section.Data.Warnings == null
                || section.Data.Headers.Count > 500 || section.Data.Rows.Count > 100000 || section.Data.Headers.Any(header => header == null)
                || section.Data.Rows.Any(row => row == null || row.Any(value => value == null)))
                throw new InvalidDataException("Une section de la fiche hors connexion est invalide.");
            cells += section.Data.Rows.Sum(row => (long)row.Length);
            if (cells > 120000) throw new InvalidDataException("La fiche hors connexion contient trop de cellules.");
        }
        if (detail.Tabs.Any(tab => tab == null || tab.Key == null || tab.Name == null || tab.Selector == null))
            throw new InvalidDataException("Un onglet de la fiche hors connexion est invalide.");
    }

    public static string DisplayTitle(string? title) => new string((title ?? "").Select(character => char.IsControl(character) ? ' ' : character).Take(2000).ToArray()).Trim();

    private static string Clean(string? text, int maximum, bool multiline = false)
    {
        text ??= "";
        if (text.Length > maximum || text.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')))
            throw new InvalidDataException($"Un texte personnel dépasse {maximum:N0} caractères ou contient un caractère non autorisé.");
        return text.Replace("\r\n", "\n").Trim();
    }

    private static void WriteAtomic(string path, byte[] bytes, int maximum)
    {
        if (bytes.Length > maximum) throw new InvalidDataException("Le fichier personnel dépasse la limite de taille autorisée.");
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, full, overwrite: true); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
