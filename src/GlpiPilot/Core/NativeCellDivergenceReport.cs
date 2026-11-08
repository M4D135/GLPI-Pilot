using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

/// <summary>Dedicated local diagnostic only. Never attach these values to ordinary logs or session summaries.</summary>
public sealed record NativeCellDivergence(string TicketId, int DisplayRow, int CsvRow, int DisplayColumn, int CsvColumn,
    string DisplayHeader, string CsvHeader, string Role, string RawDisplay, string RawCsv, string Category,
    bool DisplayCellMissing = false, bool CsvCellMissing = false);

public sealed record NativeCellDivergenceCodePoint(int Offset, string Code);

public sealed class NativeCellDivergenceEntry
{
    public string Phase { get; init; } = "";
    public string PhaseLabel => Phase == "initial" ? "Avant le téléchargement" : "Après le téléchargement";
    public string TicketId { get; init; } = "";
    public int DisplayRow { get; init; }
    public int CsvRow { get; init; }
    public int DisplayColumn { get; init; }
    public int CsvColumn { get; init; }
    public string DisplayHeader { get; init; } = "";
    public string CsvHeader { get; init; } = "";
    public string Role { get; init; } = "";
    public string RoleLabel => DashboardService.Roles.FirstOrDefault(role => role.Key == Role)?.Label ?? "Champ libre";
    public string Category { get; init; } = "";
    public string CategoryLabel => Category switch
    {
        "controls" => "Caractère de contrôle C1 présent",
        "empty" => "Valeur vide d’un seul côté",
        "date" => "Dates ou heures différentes",
        "missing-cell" => "Cellule absente d’un côté",
        _ => "Valeurs différentes"
    };
    public string RawDisplay { get; init; } = "";
    public string RawCsv { get; init; } = "";
    public int DisplayLength { get; init; }
    public int CsvLength { get; init; }
    public bool DisplayTruncated { get; init; }
    public bool CsvTruncated { get; init; }
    public bool MetadataTruncated { get; init; }
    public bool DisplayCellMissing { get; init; }
    public bool CsvCellMissing { get; init; }
    public int? FirstDifferenceOffset { get; init; }
    public bool AnalysisTruncated { get; init; }
    public int DisplayWindowStart { get; init; }
    public int CsvWindowStart { get; init; }
    public string DisplayWindow { get; init; } = "";
    public string CsvWindow { get; init; } = "";
    public IReadOnlyList<NativeCellDivergenceCodePoint> DisplayCodePoints { get; init; } = Array.Empty<NativeCellDivergenceCodePoint>();
    public IReadOnlyList<NativeCellDivergenceCodePoint> CsvCodePoints { get; init; } = Array.Empty<NativeCellDivergenceCodePoint>();
    public IReadOnlyList<string> Hints { get; init; } = Array.Empty<string>();
}

public sealed class NativeCellDivergenceDocument
{
    public int Version { get; init; } = 1;
    public string RunId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public IReadOnlyList<NativeCellDivergenceEntry> Entries { get; init; } = Array.Empty<NativeCellDivergenceEntry>();
    public int Count => Entries.Count;
    public int TotalCount { get; init; }
    public bool Truncated => TotalCount > Count || Entries.Any(entry => entry.DisplayTruncated || entry.CsvTruncated || entry.MetadataTruncated || entry.AnalysisTruncated);
    public string PositionUnit => "Indices de lignes et de colonnes à partir de 0 ; positions du texte en unités UTF-16, à partir de 0.";
    public string Note => "Valeurs de la liste et du CSV conservées dans ce fichier local dédié. Les pistes décrivent les caractères observés ; elles ne prouvent ni la cause de l’écart ni une correction à appliquer. Les équivalences déjà reconnues ne figurent pas dans ce rapport.";
}

/// <summary>Bounded, value-bearing report explicitly separate from privacy-preserving acquisition diagnostics.</summary>
public sealed class NativeCellDivergenceReport
{
    public const int MaximumEntries = 80;
    public const int MaximumValueCharacters = 4096;
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    private const int MaximumAnalysisCharacters = 1024 * 1024;
    private const int MaximumWindowCharacters = 160;
    private const int MaximumCodePoints = 24;
    private static readonly Regex RunPattern = new("\\A(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{32})\\z", RegexOptions.CultureInvariant);
    private static readonly Regex FilePattern = new("\\AGLPI-differences-[0-9]{8}-[0-9]{9}-(?:[0-9a-f]{8}|[0-9a-f]{32})\\.json\\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _runId;
    private readonly DateTimeOffset _startedAt;
    private readonly List<NativeCellDivergenceEntry> _entries = new();
    public int Count => _entries.Count;
    public int TotalCount { get; private set; }

    public NativeCellDivergenceReport(string runId, DateTimeOffset startedAt)
    {
        if (runId is null || !RunPattern.IsMatch(runId)) throw new ArgumentException("L’identifiant de collecte du diagnostic est invalide.", nameof(runId));
        _runId = runId.ToLowerInvariant(); _startedAt = startedAt;
    }

    public void Add(string phase, NativeCellDivergence cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (phase is not ("initial" or "final") || !ValidCategory(cell.Category)
            || cell.DisplayRow < 0 || cell.CsvRow < 0 || cell.DisplayColumn < 0 || cell.CsvColumn < 0)
            throw new ArgumentException("Les coordonnées ou la catégorie du diagnostic sont invalides.", nameof(cell));
        if (TotalCount < int.MaxValue) TotalCount++;
        if (_entries.Count >= MaximumEntries) return;
        var shown = cell.RawDisplay ?? ""; var received = cell.RawCsv ?? "";
        var offset = FirstDifference(shown, received, out var analysisTruncated);
        var shownWindow = Window(shown, offset ?? 0); var csvWindow = Window(received, offset ?? 0);
        var rawShown = Prefix(shown, MaximumValueCharacters); var rawCsv = Prefix(received, MaximumValueCharacters);
        var id = Prefix(cell.TicketId ?? "", 128); var shownHeader = Prefix(cell.DisplayHeader ?? "", 512); var csvHeader = Prefix(cell.CsvHeader ?? "", 512);
        _entries.Add(new()
        {
            Phase = phase, TicketId = id, DisplayRow = cell.DisplayRow, CsvRow = cell.CsvRow,
            DisplayColumn = cell.DisplayColumn, CsvColumn = cell.CsvColumn, DisplayHeader = shownHeader, CsvHeader = csvHeader,
            Role = DashboardService.Roles.Any(role => role.Key == cell.Role) ? cell.Role : "other", Category = cell.Category,
            RawDisplay = rawShown, RawCsv = rawCsv, DisplayLength = shown.Length, CsvLength = received.Length,
            DisplayTruncated = rawShown.Length < shown.Length, CsvTruncated = rawCsv.Length < received.Length,
            MetadataTruncated = id.Length != (cell.TicketId?.Length ?? 0) || shownHeader.Length != (cell.DisplayHeader?.Length ?? 0) || csvHeader.Length != (cell.CsvHeader?.Length ?? 0),
            DisplayCellMissing = cell.DisplayCellMissing, CsvCellMissing = cell.CsvCellMissing,
            FirstDifferenceOffset = offset, AnalysisTruncated = analysisTruncated,
            DisplayWindowStart = shownWindow.Start, DisplayWindow = shownWindow.Text,
            CsvWindowStart = csvWindow.Start, CsvWindow = csvWindow.Text,
            DisplayCodePoints = Codes(shown, offset ?? 0), CsvCodePoints = Codes(received, offset ?? 0),
            Hints = Hints(cell.Category, rawShown, rawCsv, shownWindow.Text, csvWindow.Text, analysisTruncated)
        });
    }

    public string Save(string directory)
    {
        var document = new NativeCellDivergenceDocument { RunId = _runId, StartedAt = _startedAt, Entries = _entries.ToArray(), TotalCount = TotalCount };
        var name = FileName(document.RunId, document.StartedAt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumFileBytes) throw Invalid();
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, name);
        var temporary = Path.Combine(directory, ".glpi-cell-diagnostic-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return name;
    }

    public static NativeCellDivergenceDocument Read(string directory, string basename)
    {
        if (basename is null || !FilePattern.IsMatch(basename) || Path.GetFileName(basename) != basename) throw Invalid();
        var path = Path.Combine(directory, basename);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Invalid();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 2 or > MaximumFileBytes) throw Invalid();
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        NativeCellDivergenceDocument? document;
        try { document = JsonSerializer.Deserialize<NativeCellDivergenceDocument>(bytes, Json); }
        catch (JsonException) { throw Invalid(); }
        if (document is null || document.Version != 1 || document.RunId is null || !RunPattern.IsMatch(document.RunId)
            || FileName(document.RunId, document.StartedAt) != basename || document.Entries is null
            || document.Entries.Count > MaximumEntries || document.TotalCount < document.Entries.Count) throw Invalid();
        foreach (var entry in document.Entries) Validate(entry);
        return document;
    }

    private static void Validate(NativeCellDivergenceEntry entry)
    {
        if (entry is null || entry.Phase is not ("initial" or "final") || !ValidCategory(entry.Category)
            || entry.DisplayRow < 0 || entry.CsvRow < 0 || entry.DisplayColumn < 0 || entry.CsvColumn < 0
            || !Text(entry.TicketId, 128) || !Text(entry.DisplayHeader, 512) || !Text(entry.CsvHeader, 512)
            || !Text(entry.Role, 64) || !(entry.Role == "other" || DashboardService.Roles.Any(role => role.Key == entry.Role))
            || !Text(entry.RawDisplay, MaximumValueCharacters) || !Text(entry.RawCsv, MaximumValueCharacters)
            || entry.DisplayLength < entry.RawDisplay.Length || entry.CsvLength < entry.RawCsv.Length
            || entry.DisplayTruncated != (entry.RawDisplay.Length < entry.DisplayLength) || entry.CsvTruncated != (entry.RawCsv.Length < entry.CsvLength)
            || entry.FirstDifferenceOffset is < 0 || entry.FirstDifferenceOffset > Math.Max(entry.DisplayLength, entry.CsvLength)
            || !ValidWindow(entry.DisplayWindow, entry.DisplayWindowStart, entry.DisplayLength)
            || !ValidWindow(entry.CsvWindow, entry.CsvWindowStart, entry.CsvLength)
            || !ValidCodes(entry.DisplayCodePoints, entry.DisplayLength) || !ValidCodes(entry.CsvCodePoints, entry.CsvLength)
            || entry.Hints is null || entry.Hints.Count > 8 || entry.Hints.Any(hint => !Text(hint, 512))) throw Invalid();
    }

    private static bool Text(string? text, int limit) => text is not null && text.Length <= limit;
    private static bool ValidCategory(string? category) => category is "controls" or "empty" or "date" or "missing-cell" or "other";
    private static bool ValidWindow(string? text, int start, int length) => Text(text, MaximumWindowCharacters) && start >= 0 && start <= length && text!.Length <= length - start;
    private static bool ValidCodes(IReadOnlyList<NativeCellDivergenceCodePoint>? codes, int length) => codes is not null
        && codes.Count <= MaximumCodePoints && codes.All(code => code is not null && code.Offset >= 0 && code.Offset < length
            && code.Code is not null && Regex.IsMatch(code.Code, "\\AU\\+[0-9A-F]{4,6}\\z", RegexOptions.CultureInvariant));
    private static InvalidDataException Invalid() => new("Le fichier de diagnostic des cellules est invalide ou dépasse les limites autorisées.");
    private static string FileName(string runId, DateTimeOffset startedAt) => "GLPI-differences-"
        + startedAt.UtcDateTime.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + runId + ".json";

    private static string Prefix(string value, int limit)
    {
        var end = Math.Min(value.Length, limit);
        if (end > 0 && end < value.Length && char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end])) end--;
        return value[..end];
    }

    private static int? FirstDifference(string shown, string received, out bool truncated)
    {
        var common = Math.Min(shown.Length, received.Length); var limit = Math.Min(common, MaximumAnalysisCharacters);
        for (var index = 0; index < limit; index++)
            if (shown[index] != received[index]) { truncated = false; return index; }
        truncated = common > limit;
        return truncated || shown.Length == received.Length ? null : common;
    }

    private static (int Start, string Text) Window(string value, int offset)
    {
        var start = Math.Max(0, Math.Min(offset, value.Length) - 48);
        if (start > 0 && start < value.Length && char.IsLowSurrogate(value[start]) && char.IsHighSurrogate(value[start - 1])) start--;
        var end = Math.Min(value.Length, start + MaximumWindowCharacters);
        if (end > start && end < value.Length && char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end])) end--;
        return (start, value[start..end]);
    }

    private static IReadOnlyList<NativeCellDivergenceCodePoint> Codes(string value, int offset)
    {
        var start = Math.Max(0, Math.Min(offset, value.Length) - 6);
        if (start > 0 && start < value.Length && char.IsLowSurrogate(value[start]) && char.IsHighSurrogate(value[start - 1])) start--;
        var result = new List<NativeCellDivergenceCodePoint>();
        for (var index = start; index < value.Length && result.Count < MaximumCodePoints;)
        {
            var valid = Rune.TryGetRuneAt(value, index, out var rune);
            var code = valid ? rune.Value : value[index];
            result.Add(new(index, "U+" + code.ToString("X4", CultureInfo.InvariantCulture)));
            index += valid ? rune.Utf16SequenceLength : 1;
        }
        return result;
    }

    private static IReadOnlyList<string> Hints(string category, string shown, string received, string shownWindow, string csvWindow, bool truncated)
    {
        var samples = new[] { shown, received, shownWindow, csvWindow }; var hints = new List<string>();
        if (category == "controls" || samples.Any(sample => sample.Any(character => character is >= '\u0080' and <= '\u009f')))
            hints.Add("Un contrôle C1 est présent. Un problème d’encodage est une piste à examiner ; aucune conversion automatique n’a été appliquée.");
        if (samples.Any(sample => sample.Contains('<') && sample.Contains('>')))
            hints.Add("Une notation ressemblant à du HTML est présente. Elle peut être du contenu littéral ; aucune équivalence avec le rendu n’est supposée.");
        if (samples.Any(sample => sample.Any(character => character is '\r' or '\n' or '\t' or '\u00a0' or '\u202f')))
            hints.Add("Des retours à la ligne, tabulations ou espaces particuliers sont présents. Leur présence ne suffit pas à expliquer l’écart restant.");
        if (samples.Any(sample => sample.Contains('…') || sample.EndsWith("...", StringComparison.Ordinal)))
            hints.Add("Des points de suspension sont présents. Ils peuvent appartenir au texte ; une troncature n’est pas démontrée.");
        if (category == "date") hints.Add("Les deux valeurs sont des dates interprétables différentes selon la règle configurée ; le rapport ne choisit pas laquelle est correcte.");
        if (category == "missing-cell") hints.Add("Les coordonnées existent dans les en-têtes, mais une ligne ne fournit pas cette cellule. Une cellule absente reste distincte d’une valeur vide.");
        if (truncated) hints.Add("La recherche de la première différence a atteint sa limite. Les longueurs et les préfixes restent disponibles, sans position exacte certifiée.");
        if (hints.Count == 0) hints.Add("Les valeurs diffèrent après les équivalences autorisées. Le rapport ne permet pas à lui seul d’attribuer cet écart au format ou à une modification du ticket.");
        return hints;
    }
}
