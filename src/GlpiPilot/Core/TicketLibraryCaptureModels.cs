namespace GlpiPilot.Core;

/// <summary>Local acquisition journal only; never part of the shared GLPI document protocol.</summary>
public sealed class TicketLibraryCaptureReport
{
    public string RunId { get; set; } = "";
    public string Status { get; set; } = "running";
    // Finishing a pass is not proof that every requested content was verified.
    // This describes the journal; consumers must still check current documents.
    public bool IsSuccessful => Status == "completed" && Targeted > 0 && Processed == Targeted && FinalScopeVerified
        && Failed == 0 && Partial == 0 && Conflicts == 0 && Unconfirmed == 0 && RetryCount == 0;
    public bool NeedsAttention => Status == "completed" && !IsSuccessful;
    public string StatusLabel => Status switch
    {
        "paused" => "Collecte en pause", "stopped" => "Collecte arrêtée",
        "completed" when IsSuccessful => "Collecte validée", "completed" => "Collecte terminée, à compléter",
        "cancelled" => "Collecte arrêtée", "interrupted" => "Collecte interrompue",
        "failed" => "Collecte non aboutie", _ => "Collecte en cours"
    };
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Mode { get; set; } = "incremental";
    public bool MissingOnly => Mode == "retry-missing";
    public string Scope { get; set; } = "";
    public int Observed { get; set; }
    public int Targeted { get; set; }
    public int Processed { get; set; }
    public int Read { get; set; }
    public int Reused { get; set; }
    public int Failed { get; set; }
    public int Partial { get; set; }
    public int Conflicts { get; set; }
    public int Unconfirmed { get; set; }
    public int RetryCount { get; set; }
    public bool FinalScopeVerified { get; set; }
    public List<string> Limitations { get; set; } = new();
    public string Message => $"{StatusLabel} · {Observed:N0} tickets observés, {Processed:N0}/{Targeted:N0} examinés, {Read:N0} contenus lus, {Reused:N0} réutilisés"
        + (Failed > 0 ? $" · {Failed:N0} lectures non abouties" : "")
        + (Partial > 0 ? $" · {Partial:N0} lectures partielles" : "")
        + (Conflicts > 0 ? $" · {Conflicts:N0} tickets avec versions en conflit" : "")
        + (Unconfirmed > 0 ? $" · {Unconfirmed:N0} contenus non confirmés" : "")
        + (RetryCount > 0 ? $" · {RetryCount:N0} fiches à reprendre" : "")
        + (Status == "completed" && !FinalScopeVerified ? " · Périmètre final non confirmé" : "")
        + (Status == "completed" && Targeted == 0 ? " · Aucun ticket ciblé : aucun contenu validé" : "");
}

public sealed class TicketLibraryCaptureEntry
{
    public string TicketKey { get; set; } = "";
    public string TicketId { get; set; } = "";
    public string State { get; set; } = "pending";
    public string StateLabel => State switch
    {
        "reading" => "Lecture en cours", "reused" => "Cache réutilisé", "confirmed" => "Contenu confirmé",
        "unconfirmed" => "Contenu à confirmer", "partial" => "Lecture partielle", "failed" => "Lecture non aboutie",
        "conflict" => "Versions à comparer", "unavailable" => "Absent de l’index actuel", "interrupted" => "Lecture à reprendre",
        _ => "Non encore lu"
    };
    public string Category { get; set; } = "pending";
    public string Message => State == "partial" && Category != "incomplete-content"
        ? TicketLibraryCaptureMessages.For("incomplete-content") + " " + TicketLibraryCaptureMessages.For(Category)
        : TicketLibraryCaptureMessages.For(Category);
    public bool Retryable { get; set; } = true;
    // Current local availability is independent of the outcome of a historical attempt.
    // A partial or undated body remains readable; it never becomes a confirmed complete one here.
    public bool HasContent { get; set; }
    public bool HasArchivedContent { get; set; }
    public bool DetailComplete { get; set; }
    public bool DetailsCurrent { get; set; }
    public List<string> DetailLimitations { get; set; } = new();
}

/// <summary>Read-only inventory of bodies already stored for the entries of one capture.</summary>
public sealed class TicketLibraryCaptureAvailability
{
    public string RunId { get; set; } = "";
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Observed { get; set; }
    public int Stored { get; set; }
    public int Complete { get; set; }
    public int Partial { get; set; }
    public int Missing { get; set; }
    public int Confirmed { get; set; }
    public int Unconfirmed { get; set; }
    public int Invalid { get; set; }
    public int Archived { get; set; }
}

public sealed class TicketLibraryCaptureEntryPage
{
    public long Total { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
    public List<TicketLibraryCaptureEntry> Items { get; set; } = new();
}

public sealed record TicketLibraryCaptureContext(string Installation, string Fingerprint, string ReaderSignature)
{
    public static TicketLibraryCaptureContext FromSettings(AppSettings settings, LibraryPreferences preferences)
    {
        var source = TargetUrlPolicy.PersistedTarget(settings.PortalUrl);
        var context = TicketLibraryRules.Serialize(new
        {
            source, settings.ActiveSourceId, settings.BrowserChannel, settings.TicketDetails,
            preferences.SourceTimeZone, preferences.SourceDateOrder
        });
        return new(ObjectIdentity.Installation(source), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(context))).ToLowerInvariant(), TicketLibraryAcquisition.ReaderSignature);
    }
}
public sealed record TicketLibraryCaptureTarget(string TicketKey, string TicketId);

public static class TicketLibraryCaptureMessages
{
    // Provider text, page values and credentials are deliberately not accepted as diagnostic messages.
    public static string For(string category) => category switch
    {
        "pending" => "La fiche n’a pas encore été lue pendant cet essai.",
        "reading" => "La lecture de cette fiche est en cours.",
        "cache-reused" => "La copie complète et déjà confirmée a été réutilisée avec le même jeton GLPI.",
        "confirmed" => "Le contenu reçu et son jeton de modification ont été confirmés dans le même périmètre.",
        "awaiting-final-check" => "Le contenu a été reçu ; sa fraîcheur attend le contrôle final de l’index.",
        "incomplete-content" => "Certaines sections n’ont pas été lues complètement. Les contenus déjà enregistrés restent consultables. Une ancienne copie complète peut avoir été conservée.",
        "read-timeout" => "Le délai de lecture a été dépassé. La fiche pourra être relue.",
        "browser-unavailable" => "La lecture du navigateur n’a pas abouti. Vérifiez la session GLPI avant de reprendre.",
        "invalid-content" => "La fiche reçue n’a pas satisfait les contrôles de structure ou d’identité.",
        "local-changed" => "La copie locale a changé pendant cet essai. Une nouvelle lecture est nécessaire.",
        "local-storage" => "L’enregistrement local n’a pas abouti. Vérifiez le dossier et l’espace disponibles.",
        "final-check-failed" => "Le contrôle final n’a pas abouti ; le contenu reçu reste à confirmer.",
        "scope-not-confirmed" => "Le périmètre final diffère ou reste incomplet ; la fraîcheur n’est pas confirmée.",
        "changed-during-capture" => "Les métadonnées GLPI ont changé pendant cet essai. Le contenu doit être relu.",
        "missing-last-modified" => "Le contenu enregistré reste consultable. Sa fraîcheur n’est pas confirmée : la date, son format ou le fuseau d’affichage GLPI manque ou reste ambigu. Vérifiez ces réglages avant une nouvelle vérification en ligne.",
        "stored-preserved" => "Une copie est déjà conservée. Ce passage complète seulement les contenus absents : cette copie n’a pas été relue, remplacée ni déclarée fraîche.",
        "no-longer-visible" => "Cette fiche n’apparaît pas dans le nouvel index accessible. Aucune lecture ni suppression n’a été effectuée.",
        "interrupted" => "L’essai s’est arrêté avant la fin de cette lecture ou de sa confirmation.",
        "conflicting-version" => "Des versions différentes ont été conservées ; aucune n’est choisie automatiquement comme version actuelle.",
        "read-near-modification" => "Cette copie a été lue trop près de sa dernière modification dans GLPI pour en confirmer la fraîcheur ; elle sera relue à la prochaine modification du ticket.",
        _ => "Le résultat de cette lecture doit être vérifié."
    };

    internal static bool IsKnown(string value) => value is "pending" or "reading" or "cache-reused" or "confirmed"
        or "awaiting-final-check" or "incomplete-content" or "read-timeout" or "browser-unavailable" or "invalid-content"
        or "local-changed" or "local-storage" or "final-check-failed" or "scope-not-confirmed" or "changed-during-capture"
        or "missing-last-modified" or "no-longer-visible" or "interrupted" or "conflicting-version" or "read-near-modification" or "stored-preserved";
}
