using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GlpiPilot.Core;

public sealed record TicketSearchHit(int SectionIndex, int RowIndex, string Section, string Kind, string Text);
public sealed record TicketTimelineFilter(string Type = "", string Author = "", DateTime? From = null, DateTime? To = null);
public sealed record TicketTimelineSelection(IReadOnlyList<string[]> Rows, int UnreadableDates, int Total);

public static class TicketReadingService
{
    public static IReadOnlyList<TicketSearchHit> Search(TicketDetailResult result, string search, int limit = 2000)
    {
        ArgumentNullException.ThrowIfNull(result);
        search = (search ?? "").Trim();
        if (search.Length == 0) return Array.Empty<TicketSearchHit>();
        var found = new List<TicketSearchHit>();
        for (var s = 0; s < result.Sections.Count; s++)
        {
            var section = result.Sections[s];
            for (var r = 0; r < section.Data.Rows.Count; r++)
            {
                var row = section.Data.Rows[r];
                if (Contains(section.Name, search) || row.Any(value => Contains(value, search)))
                    found.Add(new(s, r, section.Name, section.Kind, string.Join("\n", section.Data.Headers.Select((header, index) =>
                        header + " : " + (index < row.Length ? row[index] : "")))));
                if (found.Count >= Math.Clamp(limit, 1, 10000)) return found;
            }
        }
        return found;
    }

    public static TicketTimelineSelection Filter(IEnumerable<string[]> rows, TicketTimelineFilter filter)
    {
        var selected = new List<string[]>(); var total = 0; var invalid = 0;
        foreach (var row in rows)
        {
            total++;
            string At(int index) => index < row.Length ? row[index] ?? "" : "";
            if (filter.Type.Length > 0 && !string.Equals(At(0), filter.Type, StringComparison.OrdinalIgnoreCase)
                || filter.Author.Length > 0 && !Contains(At(2), filter.Author)) continue;
            if (filter.From.HasValue || filter.To.HasValue)
            {
                if (!TryDate(At(1), out var date)) { invalid++; continue; }
                if (filter.From.HasValue && date.Date < filter.From.Value.Date || filter.To.HasValue && date.Date > filter.To.Value.Date) continue;
            }
            selected.Add(row);
        }
        return new(selected, invalid, total);
    }

    public static Dictionary<string, string> SectionFingerprints(TicketDetailResult detail)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in detail.Sections.GroupBy(SectionKey, StringComparer.Ordinal))
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Append(string value)
            {
                var data = Encoding.UTF8.GetBytes(value ?? "");
                digest.AppendData(BitConverter.GetBytes(data.Length)); digest.AppendData(data);
            }
            foreach (var section in group)
            {
                Append(section.Kind);
                foreach (var header in section.Data.Headers) Append(header);
                foreach (var row in section.Data.Rows) { Append("ROW"); foreach (var value in row) Append(value); }
                Append(section.Data.Complete ? "complete" : "unconfirmed");
            }
            result[group.Key] = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        }
        return result;
    }

    public static string SectionKey(TicketDetailSection section) =>
        string.Join(" · ", new[] { section.TabKey, section.Name, section.Kind }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public static IReadOnlyList<string[]> TreatmentSummary(TicketDetailResult detail)
    {
        var result = new List<string[]>();
        var timeline = detail.Sections.Where(section => section.Kind == "timeline").SelectMany(section => section.Data.Rows).ToList();
        result.Add(new[] { "Lecture", detail.ReadAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss") });
        result.Add(new[] { "Couverture", $"{detail.Sections.Count} sections lues ; {detail.Tabs.Count(tab => !tab.Loaded)} onglets non lus. L’exhaustivité du ticket n’est pas confirmée." });
        foreach (var group in timeline.GroupBy(row => row.Length > 0 ? row[0] : "Type absent"))
            result.Add(new[] { group.Key + " · entrées lues", group.Count().ToString(CultureInfo.InvariantCulture) });
        foreach (var type in new[] { "Suivi", "Tâche", "Solution", "Validation" })
        {
            var entries = timeline.Where(row => row.Length > 3 && Contains(row[0], type))
                .Select(row => (Row: row, Valid: TryDate(row[1], out var date), Date: date)).Where(entry => entry.Valid)
                .OrderByDescending(entry => entry.Date).ToArray();
            if (entries.Length > 0)
            {
                var last = entries[0].Row;
                result.Add(new[] { type + " · dernière entrée datée lue", last[1] + " · " + last[2] + "\n" + last[3] });
            }
        }
        foreach (var field in detail.Sections.Where(section => section.Kind == "fields").SelectMany(section => section.Data.Rows)
            .Where(row => row.Length >= 2 && (Contains(row[0], "attente") || Contains(row[0], "validation") || Contains(row[0], "échéance"))).Take(20))
            result.Add(new[] { field[0] + " · champ lu", field[1] });
        return result;
    }

    public static string Handover(PersonalTicket ticket, TicketDetailResult? detail = null)
    {
        var report = new StringBuilder();
        report.AppendLine("PASSATION · Ticket " + ticket.Id).AppendLine(ticket.Title).AppendLine(ticket.Url).AppendLine();
        report.AppendLine("Notes personnelles").AppendLine(ticket.Notes.Length > 0 ? ticket.Notes : "À compléter").AppendLine();
        report.AppendLine("Prochaines actions personnelles");
        foreach (var item in ticket.Checklist) report.AppendLine((item.Done ? "[x] " : "[ ] ") + item.Text);
        if (ticket.Checklist.Count == 0) report.AppendLine("À compléter");
        if (ticket.ReminderAt.HasValue) report.AppendLine("Rappel personnel : " + ticket.ReminderAt.Value.LocalDateTime.ToString("dd/MM/yyyy HH:mm"));
        if (ticket.SnoozedUntil.HasValue) report.AppendLine("Report personnel jusqu’au : " + ticket.SnoozedUntil.Value.LocalDateTime.ToString("dd/MM/yyyy HH:mm") + " · " + ticket.SnoozeReason);
        if (detail != null)
        {
            report.AppendLine().AppendLine("Éléments lus dans GLPI");
            foreach (var row in TreatmentSummary(detail)) report.AppendLine(row[0] + "\n" + row[1] + "\n");
        }
        report.AppendLine().AppendLine("Responsable de la prochaine action : à préciser");
        report.AppendLine("Les notes, rappels et listes personnels ne sont pas publiés dans GLPI.");
        return report.ToString();
    }

    public static bool Contains(string? text, string value) => CultureInfo.GetCultureInfo("fr-FR").CompareInfo.IndexOf(text ?? "", value,
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    public static bool TryDate(string? value, out DateTime date) => DateTime.TryParse(value, CultureInfo.GetCultureInfo("fr-FR"),
        DateTimeStyles.AllowWhiteSpaces, out date);
}
