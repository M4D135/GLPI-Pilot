using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace GlpiPilot.Core;

public static partial class InterventionCalendarService
{
    /// <summary>Exports the visible occurrences as independent stable events, including cancellations, without leaking any private ticket cache.</summary>
    public static string ExportIcs(IEnumerable<CalendarOccurrence> occurrences, string title = "Interventions GLPI Pilot", DateTimeOffset? createdAt = null)
    {
        var stamp = (createdAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//GLPI Pilot//Interventions//FR", "CALSCALE:GREGORIAN", "X-WR-CALNAME:" + IcsText(title) };
        foreach (var item in occurrences.GroupBy(value => value.Key, Keys).Select(group => group.First()).OrderBy(value => value.StartUtc).ThenBy(value => value.Key, Keys))
        {
            var uid = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Key))).ToLowerInvariant() + "@glpi-pilot.local";
            lines.AddRange(["BEGIN:VEVENT", "UID:" + uid, "DTSTAMP:" + stamp, "SEQUENCE:" + Math.Max(0, item.Revision).ToString(CultureInfo.InvariantCulture)]);
            if (item.AllDay)
            {
                lines.Add("DTSTART;VALUE=DATE:" + ParseLocal(item.StartLocal).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                lines.Add("DTEND;VALUE=DATE:" + ParseLocal(item.EndLocal).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }
            else
            {
                lines.Add("DTSTART:" + item.StartUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
                lines.Add("DTEND:" + item.EndUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            }
            var description = new List<string>();
            if (item.ParticipantKeys.Count > 0) description.Add("Participants : " + string.Join(", ", item.ParticipantKeys));
            if (item.Tickets.Count > 0) description.Add("Tickets : " + string.Join(", ", item.Tickets.Select(ticket => ticket.Id + (ticket.Title.Length > 0 ? " — " + ticket.Title : ""))));
            if (item.Notes.Length > 0) description.Add(item.Notes);
            if (item.Contact.Length > 0) description.Add("Contact : " + item.Contact);
            if (item.Equipment.Length > 0) description.Add("Matériel : " + item.Equipment);
            if (item.Warnings.Count > 0) description.AddRange(item.Warnings);
            lines.AddRange(["SUMMARY:" + IcsText(item.Title), "LOCATION:" + IcsText(item.Remote ? "À distance" : string.Join(" · ", new[] { item.Location, item.Address }.Where(value => value.Length > 0))),
                "DESCRIPTION:" + IcsText(string.Join("\n", description)), "STATUS:" + (item.State == "cancelled" ? "CANCELLED" : item.State == "planned" ? "TENTATIVE" : "CONFIRMED"), "TRANSP:OPAQUE"]);
            if (item.ReminderMinutes.HasValue && item.State != "cancelled")
                lines.AddRange(["BEGIN:VALARM", "TRIGGER:-PT" + item.ReminderMinutes.Value.ToString(CultureInfo.InvariantCulture) + "M", "ACTION:DISPLAY", "DESCRIPTION:" + IcsText(item.Title), "END:VALARM"]);
            lines.Add("END:VEVENT");
        }
        lines.Add("END:VCALENDAR"); return string.Join("\r\n", lines.Select(FoldIcs)) + "\r\n";
    }

    public static string ExportHtml(IEnumerable<CalendarOccurrence> occurrences, string title = "Interventions GLPI Pilot")
    {
        static string H(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("<!doctype html><html lang=\"fr\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
            .Append(H(title)).Append("</title><style>body{font:15px system-ui,sans-serif;max-width:1000px;margin:2rem auto;padding:0 1.5rem;color:#17283f}h1{font-size:26px}article{border:1px solid #d4dfeb;border-radius:12px;padding:1.2rem;margin:1rem 0;break-inside:avoid}h2{margin:0 0 .4rem;font-size:20px}p{line-height:1.5;margin:.4rem 0}.muted{color:#536578}.notes{white-space:pre-wrap}.tag{display:inline-block;background:#eaf1fb;border-radius:5px;padding:3px 8px;margin-right:5px}button{padding:.6rem 1rem}@media print{button{display:none}body{margin:0;max-width:none}article{box-shadow:none}}</style><body><button onclick=\"window.print()\">Imprimer / PDF</button><h1>")
            .Append(H(title)).Append("</h1>");
        foreach (var item in occurrences.GroupBy(value => value.Key, Keys).Select(group => group.First()).OrderBy(value => value.StartUtc))
        {
            html.Append("<article><h2>").Append(H(item.Title)).Append("</h2><p class=\"muted\">").Append(H(item.StartLocal.Replace('T', ' '))).Append(" → ").Append(H(item.EndLocal.Replace('T', ' '))).Append(" · ").Append(H(item.TimeZoneId)).Append("</p>");
            html.Append("<p><span class=\"tag\">").Append(H(StateLabel(item.State))).Append("</span>").Append(H(item.Remote ? "À distance" : string.Join(" · ", new[] { item.Location, item.Address }.Where(value => value.Length > 0)))).Append("</p>");
            if (item.ParticipantKeys.Count > 0) html.Append("<p><strong>Participants :</strong> ").Append(H(string.Join(", ", item.ParticipantKeys))).Append("</p>");
            if (item.Tickets.Count > 0) html.Append("<p><strong>Tickets :</strong> ").Append(H(string.Join(" · ", item.Tickets.Select(ticket => ticket.Id + " — " + ticket.Title)))).Append("</p>");
            foreach (var (label, value) in new[] { ("Instructions", item.Notes), ("Contact", item.Contact), ("Matériel", item.Equipment) })
                if (value.Length > 0) html.Append("<p class=\"notes\"><strong>").Append(label).Append(" :</strong> ").Append(H(value)).Append("</p>");
            if (item.Warnings.Count > 0) html.Append("<p class=\"notes\">").Append(H(string.Join("\n", item.Warnings))).Append("</p>");
            html.Append("</article>");
        }
        return html.Append("</body></html>").ToString();
    }
    private static string StateLabel(string state) => state switch { "confirmed" => "Confirmée", "inProgress" => "En cours", "done" => "Terminée", "cancelled" => "Annulée", _ => "Planifiée" };
    private static string IcsText(string value) => value.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");
    private static string FoldIcs(string line)
    {
        var builder = new StringBuilder(); var octets = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > 75) { builder.Append("\r\n "); octets = 1; }
            builder.Append(rune.ToString()); octets += size;
        }
        return builder.ToString();
    }
}
