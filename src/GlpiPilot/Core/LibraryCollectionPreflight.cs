namespace GlpiPilot.Core;

/// <summary>Read-only date qualification of a selected inventory. No body or proof is changed.</summary>
public sealed class LibraryCollectionPreflight
{
    public string State { get; init; } = "unknown";
    public int Checked { get; init; }
    public int ReliableDates { get; init; }
    public int UnreliableDates { get; init; }
    public string SourceTimeZone { get; init; } = "";
    public string SourceDateOrder { get; init; } = "";
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();

    // A saved inventory may lack the modification column that the live index
    // adds. Its dates inform the preview; only a fresh index can stop body reads.
    public static LibraryCollectionPreflight Preview(IEnumerable<TicketLibrarySeed>? tickets, LibraryPreferences preferences)
    {
        var result = Inspect(tickets, preferences);
        if (result.State != "blocked") return result;
        return new() { State = "warning", Checked = result.Checked, ReliableDates = 0, UnreliableDates = result.UnreliableDates,
            SourceTimeZone = result.SourceTimeZone, SourceDateOrder = result.SourceDateOrder,
            Messages = [$"Aucune des {result.Checked:N0} dates de modification du relevé conservé n’est interprétable. Vérifiez le format et le fuseau dans les réglages de bibliothèque. La collecte vérifiera d’abord l’index GLPI actuel et arrêtera la lecture des fiches si aucune date n’y est fiable."] };
    }

    public static LibraryCollectionPreflight Inspect(IEnumerable<TicketLibrarySeed>? tickets, LibraryPreferences preferences)
    {
        var count = 0; var reliable = 0;
        if (tickets is not null)
            foreach (var seed in tickets)
            {
                count++;
                if (seed.LastModifiedReliable && seed.LastModifiedPrecision is "seconds" or "minutes"
                    && LibraryPreferencesStore.ParseModification(seed.LastModifiedRaw, preferences.SourceTimeZone, preferences.SourceDateOrder) is not null)
                    reliable++;
            }
        var missing = count - reliable;
        var state = count == 0 ? "unknown" : reliable == 0 ? "blocked" : missing > 0 ? "warning" : "ready";
        var message = state switch
        {
            "blocked" => $"Aucune des {count:N0} dates de modification ne peut être confirmée. Vérifiez la colonne de dernière modification, son format et le fuseau GLPI dans les réglages de bibliothèque avant de collecter les fiches. Les contenus déjà conservés restent inchangés.",
            "warning" => $"{missing:N0} date(s) de modification sur {count:N0} restent non fiables. Les contenus de ces tickets pourront être conservés, mais leur fraîcheur ne pourra pas être confirmée tant que leur date reste absente ou ambiguë.",
            "ready" => $"Les {count:N0} dates de modification sont interprétables. Les versions et le périmètre seront encore contrôlés en ligne avant de confirmer les contenus.",
            _ => "Les dates seront contrôlées dans l’index GLPI avant la lecture des fiches. Aucun réglage de fuseau n’est déduit automatiquement."
        };
        return new() { State = state, Checked = count, ReliableDates = reliable, UnreliableDates = missing,
            SourceTimeZone = preferences.SourceTimeZone, SourceDateOrder = preferences.SourceDateOrder, Messages = [message] };
    }
}
