using System.IO;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

public sealed record NativeSampleVerification(bool Available, bool Matches, int Sample, int Missing, string Explanation)
{
    // Counts and fixed role names only. Never store cell contents, IDs or free-form headers here.
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public bool? IdentityVerified { get; init; }
    public int ComparedCells { get; init; }
    public int DifferentCells { get; init; }
    public int EquivalentFormatCells { get; init; }
    public ExtractionSampleObservation Observation() => new()
    {
        IdentityVerified = IdentityVerified,
        RepresentationVerified = Available && Missing == 0 && ComparedCells > 0 ? Matches : null,
        SampleSize = Sample, MissingSampleIds = Missing, ComparedCells = ComparedCells,
        DifferentCells = DifferentCells, EquivalentFormatCells = EquivalentFormatCells
    };
}

/// <summary>Local acquisition checks. No credentials or downloaded values are logged.</summary>
public static class ExtractionVerificationService
{
    public static void Preflight(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings); ct.ThrowIfCancellationRequested();
        TargetUrlPolicy.Validate(settings.PortalUrl);
        if (settings.BrowserChannel is not ("msedge" or "chrome")) throw new InvalidOperationException("Choisissez Edge ou Chrome dans les réglages du navigateur.");
        if (settings.AutoMode is not ("native" or "pagination")) throw new InvalidOperationException("La méthode d’extraction configurée est inconnue.");
        if (settings.TimeoutSeconds is < 1 or > 1800 || settings.DownloadTimeoutSeconds is < 1 or > 7200)
            throw new InvalidOperationException("Les délais configurés doivent être compris entre 1 et 1 800 s pour le SSO, et entre 1 et 7 200 s pour le téléchargement.");
        if (settings.MaxPages is < 1 or > 10000 || settings.MaxRows is < 1 or > 1000000 || settings.ExpectedTotal is < 0 or > 10000000)
            throw new InvalidOperationException("Vérifiez les limites de pages, de lignes et le total attendu dans les réglages.");
        if (settings.ExpectedTotal > settings.MaxRows)
            throw new InvalidOperationException("Le total attendu dépasse la limite de lignes. Augmentez la limite ou réduisez la recherche avant de commencer.");
        if (settings.AutoMode == "native" && settings.NativeSubmitSelector.Length == 0
            && (settings.NativeMenuSelector.Length > 0 || settings.NativeFormatSelector.Length > 0 || settings.NativeScopeSelector.Length > 0))
            throw new InvalidOperationException("Les repères d’export personnalisés sont incomplets : renseignez le bouton d’export ou effacez les repères pour revenir à la détection automatique.");
        if (string.IsNullOrWhiteSpace(settings.ExportFolder)) throw new InvalidOperationException("Choisissez un dossier d’export avant de lancer l’extraction.");
        try
        {
            Directory.CreateDirectory(settings.ExportFolder);
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(settings.ExportFolder));
            if (!string.IsNullOrEmpty(driveRoot))
            {
                var drive = new DriveInfo(driveRoot);
                if (drive.IsReady && drive.AvailableFreeSpace < settings.MinimumFreeSpaceMb * 1024L * 1024L)
                    throw new IOException($"Espace libre insuffisant : {settings.MinimumFreeSpaceMb} Mio sont requis pour les fichiers temporaires et les exports.");
            }
            var probe = Path.Combine(settings.ExportFolder, ".glpi-write-check-" + Guid.NewGuid().ToString("N"));
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0); stream.Flush();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new InvalidOperationException("Le dossier d’export n’est pas accessible en écriture. Choisissez un dossier disponible dans Réglages. " + AppPaths.SafeError(error)); }
        ct.ThrowIfCancellationRequested();
    }

    public static NativeSampleVerification VerifyNativeSample(TableSnapshot visible, ExtractionResult downloaded,
        DashboardSettings settings, CancellationToken ct = default)
    {
        var shownRoles = DashboardService.ResolveColumns(visible.Headers, settings);
        var nativeRoles = DashboardService.ResolveColumns(downloaded.Headers, settings);
        var shownColumn = shownRoles.FirstOrDefault(x => x.Role == "id")?.Index;
        var nativeColumn = nativeRoles.FirstOrDefault(x => x.Role == "id")?.Index;
        if (shownColumn is not int left || nativeColumn is not int right)
            return new(false, false, 0, 0, "Rapprochement d’identifiants indisponible : la colonne ID doit être présente et non ambiguë dans la liste et dans l’export.");
        var sample = new HashSet<string>(StringComparer.Ordinal);
        var stride = Math.Max(1, visible.Rows.Count / 25);
        for (var row = 0; row < visible.Rows.Count && sample.Count < 25; row += stride)
        {
            ct.ThrowIfCancellationRequested();
            if (left >= visible.Rows[row].Length || NormalizeId(visible.Rows[row][left]) is not { Length: > 0 } id)
                return new(false, false, sample.Count, 0, "Rapprochement indisponible : l’échantillon visible contient un identifiant illisible.");
            sample.Add(id);
        }
        if (sample.Count == 0) return new(false, false, 0, 0, "La liste affichée ne fournit aucun identifiant à rapprocher.");
        // Establish the identity first, even if non-ID headers later turn out to
        // be ambiguous. A header ambiguity must never hide a missing sample ID.
        var found = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var row in downloaded.Rows)
        {
            ct.ThrowIfCancellationRequested();
            if (right < row.Length)
            {
                var id = NormalizeId(row[right]);
                if (sample.Contains(id)) found.TryAdd(id, row);
            }
            if (found.Count == sample.Count) break;
        }
        var missing = sample.Count - found.Count;
        if (missing > 0)
            return new(true, false, sample.Count, missing,
                $"{missing} identifiant(s) de la liste affichée manquent dans l’export sur un échantillon de {sample.Count}. Le fichier reçu peut correspondre à un autre périmètre ou à une recherche ayant évolué.")
            { IdentityVerified = false };
        var shownRows = visible.Rows.Select((cells, index) => new { Cells = cells, Index = index })
            .Where(row => left < row.Cells.Length && sample.Contains(NormalizeId(row.Cells[left])))
            .GroupBy(row => NormalizeId(row.Cells[left])).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var shownHeaders = ComparisonHeaders(visible.Headers);
        var nativeHeaders = ComparisonHeaders(downloaded.Headers);
        var comparable = new List<SampleColumn>();
        var diagnostics = new List<string>();
        var ambiguous = new List<int>();
        for (var index = 0; index < shownHeaders.Length; index++)
        {
            if (index == left) continue;
            var candidates = Enumerable.Range(0, nativeHeaders.Length).Where(i => nativeHeaders[i] == shownHeaders[index]).ToArray();
            if (candidates.Length == 0) continue;
            if (candidates.Length != 1 || shownHeaders.Count(header => header == shownHeaders[index]) != 1)
            { ambiguous.Add(index + 1); continue; }
            var nativeIndex = candidates[0];
            var role = shownRoles.FirstOrDefault(role => role.Index == index && nativeRoles.Any(other => other.Role == role.Role && other.Index == nativeIndex))?.Role ?? "other";
            comparable.Add(new(index, nativeIndex, role));
        }
        if (ambiguous.Count > 0)
            return new(false, false, sample.Count, 0, "Rapprochement des valeurs indisponible : plusieurs en-têtes se confondent après normalisation. Le diagnostic indique les positions concernées sans recopier les en-têtes.")
            { IdentityVerified = true, Diagnostics = new[] { "Colonnes liste ambiguës (positions à partir de 1) : " + string.Join(", ", ambiguous) + ". Aucun rapprochement par première occurrence." } };
        diagnostics.Add($"Colonnes hors ID : liste={visible.Headers.Count - 1} ; CSV={downloaded.Headers.Count - 1} ; associées sans ambiguïté={comparable.Count}. Positions à partir de 1 ; rôles standard uniquement, aucun en-tête ni contenu recopié.");
        foreach (var (id, row) in found)
        {
            ct.ThrowIfCancellationRequested();
            var shownRow = shownRows[id];
            foreach (var pair in comparable)
            {
                if (pair.Left >= shownRow.Cells.Length || pair.Right >= row.Length)
                { pair.Compared++; pair.Changed++; pair.MissingCells++; continue; }
                var shown = ComparableText(shownRow.Cells[pair.Left]); var received = ComparableText(row[pair.Right]);
                if (shown == received) { pair.Compared++; continue; }
                var controls = HasC1(shown) || HasC1(received);
                // An ellipsis inside ordinary text is data, not proof of truncation.
                // Even a terminal ellipsis only excludes a cell if the full CSV starts
                // with the entire displayed prefix. A different prefix must still fail.
                if (!controls && IsMatchingAbbreviation(shown, received)) { pair.Abbreviated++; continue; }
                pair.Compared++;
                // GLPI 9.5/10 Search::csv_clean emits two ASCII apostrophes
                // for each double quote. Compare that exact one-way export
                // representation only; do not rewrite either original cell.
                if (!controls && shown.Contains('"') && shown.Replace("\"", "''", StringComparison.Ordinal) == received)
                { pair.CsvQuotes++; continue; }
                if (!controls && MatchesProvenCsvRepresentation(visible, shownRow.Index, pair.Left, shownRow.Cells[pair.Left], received))
                { pair.RichFormats++; continue; }
                DateTime a = default, b = default;
                var dates = !controls && IsDateRole(pair.Role)
                    && DashboardService.TryDate(shown, settings.DateCulture, TimeSpan.Zero, out a)
                    && DashboardService.TryDate(received, settings.DateCulture, TimeSpan.Zero, out b);
                if (dates && a == b) { pair.DateFormats++; continue; }
                pair.Changed++;
                if (controls) pair.Controls++;
                else if (shown.Length == 0 || received.Length == 0) pair.Empty++;
                else if (dates) pair.Dates++;
                else pair.Other++;
            }
        }
        var comparedCells = comparable.Sum(column => column.Compared);
        var changedCells = comparable.Sum(column => column.Changed);
        var abbreviated = comparable.Sum(column => column.Abbreviated);
        foreach (var column in comparable.Where(column => column.Changed > 0 || column.Abbreviated > 0 || column.CsvQuotes > 0 || column.RichFormats > 0))
            diagnostics.Add($"Colonne liste {column.Left + 1} → CSV {column.Right + 1} ; rôle={column.Role} ; écarts={column.Changed}/{column.Compared} ; abrégées compatibles={column.Abbreviated} ; format guillemets CSV={column.CsvQuotes} ; format HTML/CSV prouvé={column.RichFormats} ; avec contrôle C1={column.Controls} ; vide/non-vide={column.Empty} ; date/heure={column.Dates} ; cellule absente={column.MissingCells} ; autres={column.Other}.");
        if (comparable.Any(column => column.Controls > 0))
            diagnostics.Add("Les écarts contenant un contrôle C1 restent des divergences. Aucune conversion Windows-1252 ni substitution de ponctuation n’a été appliquée ; ces compteurs ne prouvent pas leur origine.");
        return new(true, changedCells == 0, sample.Count, 0, changedCells > 0
            ? $"{found.Count}/{sample.Count} identifiants de l’échantillon ont été rapprochés, mais {changedCells}/{comparedCells} valeurs comparables diffèrent entre la liste et l’export. L’affichage peut différer du CSV, ou la recherche peut avoir évolué. Le journal précise les colonnes et catégories d’écarts sans enregistrer les valeurs."
            : $"Les {sample.Count} identifiants et {comparedCells} valeurs comparables de l’échantillon correspondent à l’export. {abbreviated} cellule(s) abrégée(s) au préfixe compatible sont exclues. Ce contrôle complète le comptage sans prouver à lui seul toute la recherche.")
        {
            IdentityVerified = true, ComparedCells = comparedCells, DifferentCells = changedCells,
            EquivalentFormatCells = comparable.Sum(column => column.CsvQuotes + column.RichFormats + column.DateFormats),
            Diagnostics = diagnostics
        };
    }

    private static bool MatchesProvenCsvRepresentation(TableSnapshot visible, int row, int column, string source, string received)
    {
        if (visible.CsvRepresentations is not { } representations || row >= representations.Count
            || representations[row] is not { } cells || column >= cells.Length) return false;
        var proof = cells[column];
        return proof is { Kind: "glpi10-rich-text-v1", Source.Length: <= 262144, Text.Length: <= 262144 }
            && proof.Row == row && proof.Column == column && proof.Source == source
            && !HasC1(proof.Text) && ComparableText(proof.Text) == received;
    }

    // U+0085 is both a C1 control and .NET whitespace. Do not let Trim/\s erase
    // this evidence of an encoding problem, including at the start/end of a cell.
    private static string ComparableText(string value) => Regex.Replace(value, @"[\t\n\v\f\r\p{Zs}\p{Zl}\p{Zp}]+", " ").Trim(' ');
    private static string[] ComparisonHeaders(IReadOnlyList<string> headers)
    {
        var normalized = headers.Select(DashboardService.Normalize).ToArray();
        return headers.Select((header, index) =>
        {
            // Both readers disambiguate repeated labels with " (2)". That suffix
            // does not establish that the two exports ordered those fields alike.
            var duplicate = Regex.Match(header, @"^(.*) \((?:[2-9]|[1-9][0-9]+)\)$");
            var original = duplicate.Success ? DashboardService.Normalize(duplicate.Groups[1].Value) : "";
            return duplicate.Success && normalized.Contains(original, StringComparer.Ordinal) ? original : normalized[index];
        }).ToArray();
    }
    private static bool HasC1(string value) => value.Any(character => character is >= '\u0080' and <= '\u009f');
    private static bool IsDateRole(string role) => role is "created" or "updated" or "resolved" or "closed" or "due" or "satisfaction-invited";
    private static bool IsMatchingAbbreviation(string shown, string received)
    {
        var suffix = shown.EndsWith('…') ? 1 : shown.EndsWith("...", StringComparison.Ordinal) ? 3 : 0;
        if (suffix == 0) return false;
        var prefix = shown[..^suffix].TrimEnd(' ');
        return prefix.Length > 0 && received.Length > prefix.Length && received.StartsWith(prefix, StringComparison.Ordinal);
    }

    private sealed class SampleColumn(int left, int right, string role)
    {
        public int Left { get; } = left;
        public int Right { get; } = right;
        public string Role { get; } = role;
        public int Compared, Changed, Abbreviated, CsvQuotes, RichFormats, DateFormats, Controls, Empty, Dates, MissingCells, Other;
    }

    private static string NormalizeId(string value)
    {
        try { return TicketReferenceResolver.NormalizeId(value); }
        catch (InvalidOperationException) { return ""; }
    }
}

/// <summary>A held OS file handle protects the dedicated profile; stale files alone never block recovery.</summary>
internal sealed class BrowserProfileLease : IDisposable
{
    private readonly FileStream _stream;
    private BrowserProfileLease(FileStream stream) => _stream = stream;
    public static BrowserProfileLease Acquire(string profile)
    {
        try
        {
            Directory.CreateDirectory(profile);
            return new(new FileStream(Path.Combine(profile, "pilot-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (UnauthorizedAccessException)
        { throw new InvalidOperationException("Le profil navigateur réservé à GLPI Pilot n’est pas accessible en écriture. Vérifiez les droits de votre dossier utilisateur."); }
        catch (IOException)
        { throw new InvalidOperationException("Le profil navigateur réservé à GLPI Pilot est occupé ou indisponible. Fermez l’autre instance de GLPI Pilot et son navigateur, puis réessayez. Aucun profil n’a été supprimé."); }
    }
    public void Dispose() => _stream.Dispose();
}
