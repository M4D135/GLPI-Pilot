using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private void RegisterWorkplaceTickets()
    {
        WorkRegister("workplace.ticket.list", WorkTicketList);
        WorkRegister("workplace.ticket.open", WorkTicketOpen);
        WorkRegister("workplace.ticket.close", a => { var doc = WorkDocument().Copy(); var key = WorkPersonal(a).Key; doc.OpenTicketKeys.Remove(key); if (doc.ActiveTicketKey == key) doc.ActiveTicketKey = doc.OpenTicketKeys.LastOrDefault() ?? ""; WorkSave(doc); return WorkState(); });
        Register("workplace.ticket.read", WorkTicketReadAsync);
        Register("workplace.ticket.tab", WorkTicketTabAsync);
        WorkRegister("workplace.ticket.section", WorkTicketSection);
        WorkRegister("workplace.ticket.cell", WorkTicketCell);
        WorkRegister("workplace.ticket.position", a => { var doc = WorkDocument().Copy(); var p = doc.Tickets.Single(t => t.Key == WorkPersonal(a).Key); p.ReadingPosition = WorkArgument<TicketReadingPosition>(a, "position"); WorkSave(doc); return new { saved = true }; });
        WorkRegister("workplace.ticket.search", a => TicketReadingService.Search(WorkDetail(WorkPersonal(a).Key), Text(a, "search", 500)).Select(h => new { h.SectionIndex, h.RowIndex, h.Section, h.Kind, text = WorkClip(h.Text, 6000) }).ToArray());
        WorkRegister("workplace.ticket.insights", WorkTicketInsights);
        WorkRegister("workplace.ticket.links", WorkTicketLinks);
        WorkRegister("workplace.ticket.intelligence", a => { var result = TicketIntelligenceService.Analyze(WorkDetail(WorkPersonal(a).Key), RequireResult(), EffectiveSettings.Dashboard, _workplaceDetails.Values); return new { revision = Revision, result.Available, result.Message, result.Related, result.ComparedRows, result.ExtractedAt, result.SourceUrl }; });
        Register("workplace.ticket.recurrence", WorkRecurrenceAsync);
        Register("workplace.ticket.handover", async (a, ct) => { var p = WorkPersonal(a); var text = TicketReadingService.Handover(p, WorkOptionalDetail(p)); if (Flag(a, "copy")) await Host.SetClipboardAsync(text); return new { text }; });
        Register("workplace.ticket.browser", async (a, ct) => { var p = WorkPersonal(a); var reference = WorkValidateReference(new(p.Id, p.Url)); await Browser.ReadTicketDetailsAsync(reference, Settings.Copy(), null, ct); await Browser.ShowBrowserAsync(Browser.TicketPage, ct); return new { shown = true }; });
    }
    private object WorkTicketList(JsonElement a)
    {
        var search = Text(a, "search", 300); var page = Number(a, "page", 0, 0, 100000); var size = Number(a, "pageSize", 50, 1, 100);
        if (Flag(a, "saved") || Result is null)
        {
            var found = WorkDocument().Tickets.Where(t => search.Length == 0 || TicketReadingService.Contains(t.Id + " " + t.Title + " " + t.Notes, search)).OrderByDescending(t => t.Pinned).ThenByDescending(t => t.LastOpenedAt).ToArray();
            return new { revision = Revision, total = found.Length, page, pageSize = size, saved = true, rows = found.Skip(page * size).Take(size).Select(t => new { ticketKey = t.Key, id = t.Id, title = t.Title, status = "État à relire", t.Pinned, t.LastReadAt }).ToArray() };
        }
        CheckRevision(a); var data = RequireResult(); var columns = DashboardService.ResolveColumns(data.Headers, EffectiveSettings.Dashboard);
        string Cell(int index, string role) => columns.FirstOrDefault(c => c.Role == role)?.Index is int col ? data.Rows[index][col] : "";
        var filtered = Enumerable.Range(0, data.Rows.Count).Where(i => search.Length == 0 || data.Rows[i].Any(cell => TicketReadingService.Contains(cell, search))).ToArray();
        return new { revision = Revision, total = filtered.Length, page, pageSize = size, saved = false, rows = filtered.Skip(page * size).Take(size).Select(i => new { rowIndex = i, id = Cell(i, "id"), title = Cell(i, "title"), status = Cell(i, "status"), assignee = Cell(i, "assignee"), due = Cell(i, "due") }).ToArray() };
    }
    private object WorkTicketOpen(JsonElement a)
    {
        var doc = WorkDocument().Copy(); PersonalTicket p;
        if (a.TryGetProperty("rowIndex", out _))
        {
            CheckRevision(a); var data = RequireResult(); var index = Number(a, "rowIndex", -1, 0, Math.Max(0, data.Rows.Count - 1));
            if (data.Rows.Count == 0) throw new InvalidOperationException("Le relevé ne contient aucune ligne.");
            var settings = EffectiveSettings; var reference = TicketReferenceResolver.Resolve(data, data.Rows[index], settings);
            var titleCol = DashboardService.ResolveColumns(data.Headers, settings.Dashboard).FirstOrDefault(c => c.Role == "title")?.Index;
            p = WorkplaceStore.Remember(doc, reference, titleCol is int col ? data.Rows[index][col] : "Ticket " + reference.Id);
        }
        else { var known = WorkPersonal(a); p = WorkplaceStore.Remember(doc, new(known.Id, known.Url), known.Title); }
        WorkSave(doc); return WorkTicketView(p.Key);
    }
    private TicketDetailResult? WorkOptionalDetail(PersonalTicket p)
    {
        if (_workplaceDetails.TryGetValue(p.Key, out var detail)) return detail;
        if (!p.Offline) return null;
        detail = WorkplaceStore.LoadOffline(DataPath("TicketCache"), new(p.Id, p.Url));
        if (detail is not null) WorkCache(detail); return detail;
    }
    private TicketDetailResult WorkDetail(string key)
    {
        var p = WorkDocument().Tickets.SingleOrDefault(t => t.Key == key) ?? throw new InvalidOperationException("Dossier absent de l’espace personnel.");
        return WorkOptionalDetail(p) ?? throw new InvalidOperationException("Lisez cette fiche pour consulter ses rubriques, ou conservez une copie hors connexion.");
    }
    private object WorkTicketView(string key)
    {
        var p = WorkDocument().Tickets.Single(t => t.Key == key); var detail = WorkOptionalDetail(p); var canRead = true; var reason = "";
        try { WorkValidateReference(new(p.Id, p.Url)); } catch (InvalidOperationException error) { canRead = false; reason = AppPaths.SafeError(error); }
        var changes = _workplaceChanges.GetValueOrDefault(key);
        return new
        {
            revision = Revision, personal = WorkPersonalView(p), canRead, readReason = reason, loaded = detail is not null,
            readAt = detail?.ReadAt, title = detail?.Title ?? p.Title, warnings = detail?.Warnings.ToArray() ?? Array.Empty<string>(),
            sections = detail?.Sections.Select((s, i) => new { index = i, key = TicketReadingService.SectionKey(s), s.Name, s.Kind, s.TabKey, s.TabName, rows = s.Data.Rows.Count, s.Data.Complete, readAt = s.Data.ExtractedAt }).ToArray(),
            tabs = detail?.Tabs.Select(t => new { t.Key, t.Name, t.Category, t.Loaded, t.IsActive }).ToArray(),
            summary = detail is null ? Array.Empty<string[]>() : TicketReadingService.TreatmentSummary(detail).Select(row => row.Select(v => WorkClip(v)).ToArray()).ToArray(),
            changes = changes is null ? null : new { changes.PreviousReadAt, changes.Summary, changes.AddedSections, changes.ChangedSections, changes.MissingSections, changes.RowComparisonComplete }
        };
    }
    private void WorkCache(TicketDetailResult detail)
    {
        var key = WorkplaceStore.TicketKey(detail.Ticket); _workplaceDetails[key] = detail;
        while (_workplaceDetails.Count > 12)
        { var remove = _workplaceDetails.Where(pair => pair.Key != key).OrderBy(pair => pair.Value.ReadAt).First().Key; _workplaceDetails.Remove(remove); _workplaceChanges.Remove(remove); }
    }
    private void WorkAcceptDetail(TicketDetailResult detail, bool open = false)
    {
        var doc = WorkDocument().Copy(); var p = WorkplaceStore.Remember(doc, detail.Ticket, detail.Title, open);
        var changes = WorkplaceStore.ObserveRead(p, detail);
        if (p.Offline) WorkplaceStore.SaveOffline(DataPath("TicketCache"), detail);
        WorkSave(doc); WorkCache(detail); _workplaceChanges[p.Key] = changes;
    }
    private async Task<object?> WorkTicketReadAsync(JsonElement a, CancellationToken ct)
    {
        var p = WorkPersonal(a); var ticket = WorkValidateReference(new(p.Id, p.Url));
        var detail = await Browser.ReadTicketDetailsAsync(ticket, Settings.Copy(), new WorkProgress<ExtractionProgress>(progress => Publish("workplace.ticket.progress", new { ticketKey = p.Key, progress })), ct);
        ct.ThrowIfCancellationRequested(); if (detail.Ticket != ticket) throw new InvalidDataException("La lecture reçue correspond à un autre ticket.");
        WorkAcceptDetail(detail); return WorkTicketView(p.Key);
    }
    private async Task<object?> WorkTicketTabAsync(JsonElement a, CancellationToken ct)
    {
        var p = WorkPersonal(a); var old = WorkDetail(p.Key); var key = Text(a, "tabKey", 2048);
        var tab = old.Tabs.SingleOrDefault(t => t.Key == key) ?? throw new InvalidOperationException("Cet onglet n’a pas été découvert dans la fiche. Actualisez la lecture.");
        var ticket = WorkValidateReference(old.Ticket); var part = await Browser.ReadTicketTabAsync(ticket, Settings.Copy(), tab, null, ct);
        ct.ThrowIfCancellationRequested(); if (part.Ticket != ticket) throw new InvalidDataException("L’onglet reçu appartient à un autre ticket.");
        if (!part.Tabs.Any(t => t.Key == key && t.Loaded)) return new { loaded = false, warnings = part.Warnings, ticket = WorkTicketView(p.Key) };
        // Replace evidence only after the requested tab was genuinely read. Other observation dates remain unchanged.
        var merged = new TicketDetailResult
        {
            Ticket = old.Ticket, Title = part.Title.Length > 0 ? part.Title : old.Title, ReadAt = part.ReadAt,
            Sections = old.Sections.Where(s => s.TabKey != key && s.TabSelector != tab.Selector).Concat(part.Sections).ToList(),
            ObservedItems = old.ObservedItems.Where(i => i.SourceTabKey != key && i.SourceTabSelector != tab.Selector).Concat(part.ObservedItems).ToList(),
            Tabs = old.Tabs.Select(t =>
            {
                var observed = part.Tabs.FirstOrDefault(n => n.Key == t.Key);
                return observed is null ? t : new TicketDetailTab { Key = observed.Key, Name = observed.Name, Selector = observed.Selector, Category = observed.Category, IsActive = observed.IsActive, Loaded = observed.Loaded || t.Loaded && t.Key != key };
            }).Concat(part.Tabs.Where(t => old.Tabs.All(o => o.Key != t.Key))).ToList(),
            Warnings = old.Warnings.Concat(part.Warnings).Distinct().Take(200).ToList()
        };
        WorkAcceptDetail(merged); return new { loaded = true, warnings = part.Warnings, ticket = WorkTicketView(p.Key) };
    }
    private object WorkTicketSection(JsonElement a)
    {
        var p = WorkPersonal(a); var detail = WorkDetail(p.Key); var index = Number(a, "sectionIndex", 0, 0, Math.Max(0, detail.Sections.Count - 1));
        if (detail.Sections.Count == 0) throw new InvalidOperationException("Aucune rubrique collectée.");
        var section = detail.Sections[index]; var page = Number(a, "page", 0, 0, 100000); var size = Number(a, "pageSize", 50, 1, 100); var search = Text(a, "search", 500);
        var type = Text(a, "type", 100); var author = Text(a, "author", 200); var from = WorkOffset(a, "from")?.Date; var to = WorkOffset(a, "to")?.Date;
        if (from > to) throw new InvalidDataException("La fin du filtre précède son début.");
        var filtered = section.Kind == "timeline" ? TicketReadingService.Filter(section.Data.Rows, new(type, author, from, to)) : new TicketTimelineSelection(section.Data.Rows, 0, section.Data.Rows.Count);
        var allowed = filtered.Rows.ToHashSet(ReferenceEqualityComparer.Instance);
        var rows = section.Data.Rows.Select((row, rowIndex) => (row, rowIndex)).Where(v => allowed.Contains(v.row) && (search.Length == 0 || v.row.Any(c => TicketReadingService.Contains(c, search)))).ToArray();
        if (a.TryGetProperty("focusRowIndex", out _))
        {
            var focus = Number(a, "focusRowIndex", -1, -1, Math.Max(0, section.Data.Rows.Count - 1));
            var position = Array.FindIndex(rows, row => row.rowIndex == focus);
            if (position >= 0) page = position / size;
        }
        var changes = _workplaceChanges.GetValueOrDefault(p.Key); var highlights = changes?.NewOrChangedRows.GetValueOrDefault(TicketReadingService.SectionKey(section));
        return new
        {
            sectionIndex = index, key = TicketReadingService.SectionKey(section), detail.ReadAt, section.Name, section.Kind, headers = section.Data.Headers,
            total = rows.Length, page, pageSize = size, filtered.UnreadableDates, section.Data.Complete, warnings = section.Data.Warnings,
            previousReadAt = changes?.PreviousReadAt, changedRowsComplete = changes?.RowComparisonComplete ?? false,
            types = section.Kind == "timeline" ? section.Data.Rows.Where(r => r.Length > 0).Select(r => r[0]).Distinct().Take(100).ToArray() : Array.Empty<string>(),
            rows = rows.Skip(page * size).Take(size).Select(v => new
            {
                index = v.rowIndex, values = v.row.Select(c => WorkClip(c)).ToArray(),
                truncatedColumns = v.row.Select((c, column) => (c, column)).Where(c => c.c.Length > 12000).Select(c => c.column).ToArray(),
                occurredAt = section.Kind == "timeline" && v.row.Length > 1 && TicketReadingService.TryDate(v.row[1], out var occurredAt) ? occurredAt : (DateTime?)null,
                changed = highlights?.Contains(TicketReadingService.RowFingerprint(v.row)) == true
            }).ToArray()
        };
    }
    private object WorkTicketCell(JsonElement a)
    {
        var detail = WorkDetail(WorkPersonal(a).Key);
        if (WorkOffset(a, "readAt") != detail.ReadAt) throw new InvalidOperationException("La fiche a changé ; actualisez la rubrique avant de consulter son texte complet.");
        var sectionIndex = Number(a, "sectionIndex", -1, 0, Math.Max(0, detail.Sections.Count - 1));
        if (sectionIndex < 0 || sectionIndex >= detail.Sections.Count) throw new InvalidDataException("Rubrique inconnue.");
        var section = detail.Sections[sectionIndex]; var rowIndex = Number(a, "rowIndex", -1, 0, Math.Max(0, section.Data.Rows.Count - 1));
        if (rowIndex < 0 || rowIndex >= section.Data.Rows.Count) throw new InvalidDataException("Ligne inconnue.");
        var row = section.Data.Rows[rowIndex]; var columnIndex = Number(a, "columnIndex", -1, 0, Math.Max(0, row.Length - 1));
        if (columnIndex < 0 || columnIndex >= row.Length) throw new InvalidDataException("Colonne inconnue.");
        return new { text = row[columnIndex], section = section.Name, header = columnIndex < section.Data.Headers.Count ? section.Data.Headers[columnIndex] : "Colonne " + (columnIndex + 1), detail.ReadAt, rowIndex, columnIndex };
    }
    private object WorkTicketInsights(JsonElement a)
    {
        var detail = WorkDetail(WorkPersonal(a).Key); var report = TicketInsightService.Analyze(detail, DateTimeOffset.Now);
        return new { report, detail.ReadAt, relations = WorkLinkRows(detail, "relation", 0, 50), documents = WorkLinkRows(detail, "document", 0, 50), relationTotal = detail.ObservedItems.Count(i => i.Kind == "relation"), documentTotal = detail.ObservedItems.Count(i => i.Kind == "document") };
    }
    private object WorkTicketLinks(JsonElement a)
    {
        var detail = WorkDetail(WorkPersonal(a).Key); var kind = Text(a, "kind", 30);
        if (kind is not ("relation" or "document")) throw new InvalidDataException("Type de lien inconnu.");
        if (a.TryGetProperty("readAt", out _) && WorkOffset(a, "readAt") != detail.ReadAt) throw new InvalidOperationException("La fiche a changé ; actualisez les relations et documents.");
        var page = Number(a, "page", 0, 0, 100000); var size = Number(a, "pageSize", 50, 1, 100);
        return new { total = detail.ObservedItems.Count(i => i.Kind == kind), page, pageSize = size, detail.ReadAt, rows = WorkLinkRows(detail, kind, page * size, size) };
    }
    private static object[] WorkLinkRows(TicketDetailResult detail, string kind, int skip, int size) =>
        detail.ObservedItems.Select((item, index) => (item, index)).Where(pair => pair.item.Kind == kind).Skip(skip).Take(size).Select(pair =>
        {
            var allowed = true; var reason = ""; try { TicketLinkPolicy.Validate(detail.Ticket, pair.item); } catch (InvalidDataException error) { allowed = false; reason = error.Message; }
            return (object)new { index = pair.index, pair.item.Kind, pair.item.Identity, title = WorkClip(pair.item.Title, 2000), pair.item.RelatedType, pair.item.Url, pair.item.SourceSection, observedAt = pair.item.ObservedAt ?? detail.ReadAt, allowed, reason };
        }).ToArray();
    private Task<object?> WorkRecurrenceAsync(JsonElement a, CancellationToken ct)
    {
        var p = WorkPersonal(a); var source = RequireResult(); var settings = EffectiveSettings;
        var sources = new List<TicketRecurrenceSource> { new(source, settings.Dashboard, "Relevé courant") }; var warnings = new List<string>();
        var sourceKey = source.Provenance is { SearchVerified: true, ScopeVerified: true } ? SnapshotStore.SourceKey(source) : "";
        if (sourceKey.Length == 0) warnings.Add("Source ou contexte non confirmé : aucune archive rapprochée.");
        else foreach (var entry in Snapshots.List().Where(s => s.SourceKey == sourceKey && s.CapturedAt <= source.ExtractedAt).Take(19))
        {
            ct.ThrowIfCancellationRequested();
            try { var snapshot = Snapshots.Load(entry.Id, ct); if (snapshot.Data.ExtractedAt != source.ExtractedAt) sources.Add(new(snapshot.Data, snapshot.Dashboard, entry.CapturedAt.ToString("dd/MM/yyyy HH:mm"))); }
            catch (Exception error) when (error is IOException or InvalidDataException) { warnings.Add("Archive exclue : " + AppPaths.SafeError(error)); }
        }
        var result = TicketRecurrenceService.Analyze(new(p.Id, p.Url), sources, Text(a, "equipmentColumn", 500), ct);
        return Task.FromResult<object?>(new { result, warnings, headers = source.Headers });
    }
}
