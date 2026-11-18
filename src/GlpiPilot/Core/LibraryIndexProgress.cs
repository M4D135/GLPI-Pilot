namespace GlpiPilot.Core;

/// <summary>Safe index diagnostics: phase codes and application messages only, never page content or URLs.</summary>
public sealed record LibraryIndexProgress(string Phase, string Category, string Message)
{
    public string PhaseLabel => LabelFor(Phase);
    internal static string LabelFor(string phase) => phase switch
    {
        "session" => "Vérification de la session GLPI",
        "open-list" => "Ouverture de la liste des tickets",
        "status" => "Préparation du critère Statut",
        "ticket-id" => "Préparation du critère Identifiant",
        "targeted" => "Recherche des tickets sélectionnés",
        "scope" => "Vérification du périmètre GLPI",
        "all-status" => "Recherche de tous les statuts",
        "page" => "Lecture de la pagination des tickets",
        "restore" => "Restauration du filtre précédent",
        _ => "Index de la bibliothèque"
    };
}

/// <summary>A failed index operation, distinct from reading an individual ticket. Messages are supplied by the reader.</summary>
public sealed class GlpiLibraryIndexException(string phase, string category, string message) : InvalidOperationException(message)
{
    public string Phase { get; } = phase;
    public string PhaseLabel => LibraryIndexProgress.LabelFor(Phase);
    public string Category { get; } = category;
}

// Time is a bound on inactivity, never on the duration of a native request.
// The browser adapter serializes calls to this state under its event lock.
internal sealed class LibraryIndexActivityState
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _inactivity;
    private readonly HashSet<object> _pending = new(ReferenceEqualityComparer.Instance);
    private long _progressAt;
    private long _noticeAt;
    private string _signature = "";
    private bool _documentLoading;

    public LibraryIndexActivityState(int inactivitySeconds, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _inactivity = TimeSpan.FromSeconds(Math.Clamp(inactivitySeconds, 5, 600));
        BeginPhase();
    }

    public bool HasPendingRequest => _pending.Count > 0;
    public bool DocumentLoading => _documentLoading;
    public bool CanWait => HasPendingRequest || DocumentLoading || _clock.GetElapsedTime(_progressAt) < _inactivity;
    public void BeginPhase() { _signature = ""; _progressAt = _noticeAt = _clock.GetTimestamp(); }
    // This marker belongs to Playwright's real DOMContentLoaded waiter. It is
    // not inferred from readyState=interactive or from unrelated asset traffic.
    public void BeginDocument() { _documentLoading = true; _progressAt = _noticeAt = _clock.GetTimestamp(); }
    public void DocumentLoaded() { _documentLoading = false; _progressAt = _clock.GetTimestamp(); }
    public void RequestStarted(object request) { if (_pending.Add(request)) _progressAt = _clock.GetTimestamp(); }
    public void ResponseReceived(object request) { if (_pending.Contains(request)) _progressAt = _clock.GetTimestamp(); }
    public void RequestEnded(object request) { if (_pending.Remove(request)) _progressAt = _clock.GetTimestamp(); }
    public void Observe(string signature)
    {
        if (_signature == signature) return;
        _signature = signature; _progressAt = _clock.GetTimestamp();
    }
    public bool TakePendingNotice()
    {
        if ((!HasPendingRequest && !DocumentLoading) || _clock.GetElapsedTime(_noticeAt) < TimeSpan.FromSeconds(5)) return false;
        _noticeAt = _clock.GetTimestamp(); return true;
    }
}

internal static class LibraryIndexRequestPolicy
{
    // GLPI 9.5/10 ajax/search.php and Search::displaySearchoption. Only the
    // observed native request is classified; no endpoint is called here.
    public static bool IsNative(Uri source, string url, string method, string resourceType, bool navigation, string? postData, string nativeField = "12")
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.UserInfo.Length != 0
            || target.Scheme is not ("https" or "http")
            || !string.Equals(target.GetLeftPart(UriPartial.Authority), source.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) return false;
        if (resourceType == "document") return navigation && method == "GET" && target.AbsolutePath == source.AbsolutePath;
        if (resourceType is not ("xhr" or "fetch") || method is not ("GET" or "POST")
            || target.AbsolutePath != source.AbsolutePath.Replace("/front/ticket.php", "/ajax/search.php", StringComparison.Ordinal)) return false;
        var fields = target.Query.TrimStart('?') + (method == "POST" && !string.IsNullOrEmpty(postData) ? "&" + postData : "");
        if (fields.Length > 262144) return false;
        string? action = null, itemtype = null, field = null;
        var actions = 0; var itemtypes = 0; var fieldsSeen = 0;
        foreach (var part in fields.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            if (key is not ("action" or "itemtype" or "field")) continue;
            var value = Uri.UnescapeDataString((pair.Length == 2 ? pair[1] : "").Replace('+', ' '));
            if (key == "action") { action = value; actions++; }
            else if (key == "itemtype") { itemtype = value; itemtypes++; }
            else { field = value; fieldsSeen++; }
        }
        return actions == 1 && itemtypes == 1 && itemtype == "Ticket"
            && (action == "display_results" || action is "display_searchoption" or "display_searchoption_value" && fieldsSeen == 1 && field == nativeField);
    }
}
