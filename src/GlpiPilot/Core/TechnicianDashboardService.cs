using System.Text;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

public sealed record TechnicianEntry(string Key, string Name, int[] Rows, int? ActiveCount, int UnknownStatusCount = 0);

public sealed record TechnicianRoster(bool Available, string Header, string Message,
    IReadOnlyList<TechnicianEntry> Technicians);

/// <summary>Derives technician views from the actual assignment column of one immutable extraction.</summary>
public static class TechnicianDashboardService
{
    public static TechnicianRoster Detect(ExtractionResult data, DashboardSettings settings, CancellationToken ct = default)
        => DetectCore(data, settings, null, ct);

    public static TechnicianRoster DetectSubset(ExtractionResult data, DashboardSettings settings,
        IReadOnlyList<int> sourceRows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRows);
        return DetectCore(data, settings, sourceRows, ct);
    }

    private static TechnicianRoster DetectCore(ExtractionResult data, DashboardSettings settings,
        IReadOnlyList<int>? sourceRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();
        var selectedRows = sourceRows?.Distinct().Order().ToArray();
        if (selectedRows?.Any(index => index < 0 || index >= data.Rows.Count) == true)
            throw new ArgumentOutOfRangeException(nameof(sourceRows), "Le périmètre contient une ligne absente du relevé.");
        DashboardService.ValidateSettings(settings);
        var columns = DashboardService.ResolveColumns(data.Headers, settings);
        var assignee = columns.Single(column => column.Role == "assignee");
        if (assignee.Index is not int technicianColumn)
            return new(false, "", assignee.State == "ambiguous"
                ? "Plusieurs colonnes peuvent désigner le technicien attribué. Choisissez la correspondance dans Réglages → Dashboard."
                : "La colonne du technicien attribué est absente ou non reconnue. Ajoutez-la au relevé ou choisissez sa correspondance dans Réglages → Dashboard.",
                Array.Empty<TechnicianEntry>());
        var statusColumn = columns.Single(column => column.Role == "status").Index;
        var names = new Dictionary<string, (string Name, List<int> Rows, int Active, int Known)>(StringComparer.Ordinal);
        var multipleAssignments = false;
        var iteration = 0;
        foreach (var index in selectedRows ?? Enumerable.Range(0, data.Rows.Count))
        {
            if ((iteration++ & 255) == 0) ct.ThrowIfCancellationRequested();
            var row = data.Rows[index];
            if (technicianColumn >= row.Length) continue;
            var technicians = DashboardService.SplitTechnicianNames(DashboardService.NormalizeValue(row[technicianColumn], "assignee", data.Headers[technicianColumn], settings), settings.MultiValueSeparators);
            multipleAssignments |= technicians.Count > 1;
            var status = statusColumn is int statusIndex && statusIndex < row.Length
                ? DashboardService.Status(DashboardService.NormalizeValue(row[statusIndex], "status", data.Headers[statusIndex], settings)) : 0;
            foreach (var technician in technicians)
            {
                var key = IdentityKey(technician);
                if (!names.TryGetValue(key, out var item)) item = (technician, new(), 0, 0);
                item.Rows.Add(index);
                if (status is >= 1 and <= 4) item.Active++;
                if (status != 0) item.Known++;
                names[key] = item;
            }
        }
        var entries = names.Select(pair => new TechnicianEntry(pair.Key, pair.Value.Name, pair.Value.Rows.ToArray(),
                pair.Value.Known > 0 ? pair.Value.Active : null, pair.Value.Rows.Count - pair.Value.Known))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var message = entries.Length == 0
            ? "Aucun technicien renseigné dans la colonne sélectionnée pour ce relevé."
            : multipleAssignments
                ? "Techniciens détectés dans le relevé. Les affectations multiples figurent dans chaque vue concernée ; leurs totaux ne s’additionnent pas."
                : "Techniciens détectés dans la colonne d’affectation du relevé. Le choix « C’est moi » est mémorisé sur ce poste.";
        if (entries.Any(entry => entry.UnknownStatusCount > 0))
            message += " Certains statuts sont absents ou non reconnus ; les volumes actifs connus peuvent être partiels.";
        return new(true, assignee.Header, message, entries);
    }

    public static DashboardSnapshot CalculateForTechnician(ExtractionResult data, DashboardSettings settings,
        string technicianKey, DateTimeOffset? referenceTime = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(technicianKey))
            throw new InvalidOperationException("Choisissez votre nom parmi les techniciens détectés avant d’ouvrir cette vue personnelle.");
        var roster = Detect(data, settings, ct);
        return CalculateForKnownTechnician(data, settings, roster, technicianKey, referenceTime, ct);
    }

    /// <summary>The caller retains the roster together with its immutable source extraction.</summary>
    public static DashboardSnapshot CalculateForKnownTechnician(ExtractionResult data, DashboardSettings settings,
        TechnicianRoster roster, string technicianKey, DateTimeOffset? referenceTime = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (string.IsNullOrWhiteSpace(technicianKey))
            throw new InvalidOperationException("Choisissez un technicien avant d’ouvrir son tableau de bord.");
        if (!roster.Available) throw new InvalidOperationException(roster.Message);
        var key = IdentityKey(technicianKey);
        var entry = roster.Technicians.FirstOrDefault(technician => technician.Key == key);
        var label = "Technicien · " + (entry?.Name ?? DisplayName(technicianKey));
        var snapshot = DashboardService.CalculateSubset(data, settings, entry?.Rows ?? Array.Empty<int>(), label, referenceTime, ct);
        if (entry is null)
            snapshot.Warnings.Add("Le technicien choisi n’est pas présent dans la colonne d’affectation de ce relevé. Aucun ticket de ce relevé ne lui est attribué ; cela ne décrit pas les tickets absents de la recherche.");
        if (snapshot.RowCount == 0)
            snapshot.Weather = new("unknown", "Aucun ticket dans cette vue", "◌", null, 0,
                "Aucun ticket correspondant n’est présent dans le relevé récupéré. Aucune météo ne peut être calculée.", Array.Empty<DashboardSignal>());
        return snapshot;
    }

    /// <summary>Case/whitespace normalization only: accents and punctuation identify distinct people.</summary>
    public static string IdentityKey(string? value) => DisplayName(value).ToUpperInvariant();

    public static IReadOnlyList<string> SplitTechnicians(string? value) => DashboardService.SplitTechnicianNames(value);

    private static string DisplayName(string? value) => Regex.Replace((value ?? "").Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();

}
