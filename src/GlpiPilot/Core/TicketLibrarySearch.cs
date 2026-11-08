using System.Text;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

public static class TicketLibrarySearch
{
    public const int MaximumPageSize = 100;
    internal const int ChunkCharacters = 3000;

    /// <summary>A whole numeric query can identify a ticket without rounding a large ID.</summary>
    internal static string NumericIdentifier(string text)
    {
        var value = text.Trim();
        return value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit) ? value.TrimStart('0') : "";
    }

    /// <summary>Only literal Unicode word tokens reach MATCH; caller text cannot become FTS syntax.</summary>
    internal static string MatchQuery(string text)
    {
        if (text.Length > 4000) throw new ArgumentException("La recherche est limitée à 4 000 caractères.");
        return string.Join(" OR ", Regex.Matches(text.Normalize(NormalizationForm.FormC), @"[\p{L}\p{N}]{2,64}|\p{N}")
            .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).Select(t => "\"" + t + "\""));
    }

    internal static IEnumerable<TicketLibraryCitation> Chunks(TicketLibraryDocument document)
    {
        var ordinal = 0;
        TicketLibraryCitation Citation(string section, string sectionKey, int row, string excerpt, bool solution = false) => new()
        {
            ChunkKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(document.Key + "\n" + document.ContentHash + "\n" + (ordinal++).ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToLowerInvariant(),
            Key = document.Key, Ticket = document.Seed.Ticket, Section = section, SectionKey = sectionKey, Row = row,
            Excerpt = excerpt, Url = document.Seed.Ticket.Url, ObservedAt = document.ReadAt ?? document.Seed.ObservedAt,
            IsSolution = solution, Current = document.DetailsCurrent, ContentHash = document.ContentHash
        };
        foreach (var part in Parts(document.Seed.Ticket.Id + "\n" + document.Seed.Title + "\n" + document.Seed.Category)) yield return Citation("Index GLPI", "index", -1, part);
        if (document.Detail is not { } detail) yield break;
        foreach (var section in detail.Sections)
        {
            for (var rowIndex = 0; rowIndex < section.Data.Rows.Count; rowIndex++)
            {
                var row = section.Data.Rows[rowIndex];
                var isSolution = document.Seed.StatusCode is 5 or 6 && document.DetailsCurrent && document.DetailComplete
                    && section.Kind == "timeline" && row.Length > 3 && string.Equals(row[0], "Solution", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(row[3]);
                var text = isSolution ? row[3] : string.Join("\n", row.Select((value, index) => string.IsNullOrWhiteSpace(value) ? ""
                    : index < section.Data.Headers.Count ? section.Data.Headers[index] + ": " + value : value).Where(s => s.Length > 0));
                foreach (var part in Parts(text)) yield return Citation(section.Name, section.TabKey, rowIndex, part, isSolution);
            }
        }
    }

    private static IEnumerable<string> Parts(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(ChunkCharacters, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
            yield return text.Substring(offset, length);
            offset += length;
        }
    }
}
