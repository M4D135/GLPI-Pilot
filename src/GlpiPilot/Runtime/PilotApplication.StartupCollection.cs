using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record StartupCollectionOffer(bool Eligible, string Reason, bool Attempted, string Token, string Message);
    private StartupCollectionDecision? _startupCollectionDecision;
    private bool _startupCollectionAttempted;
    private bool _startupCollectionReserved;
    private bool _startupOtherCollectionRequested;
    private CancellationTokenSource? _startupCollectionCancellation;
    private string _startupCollectionToken = "";
    private string _startupConfigurationStamp = "";
    private string _startupSourceStamp = "";
    private HashSet<string> _startupExistingOperations = new(StringComparer.Ordinal);
    private Func<AppSettings, bool> _startupBrowserAvailable = StartupCollectionPolicy.BrowserAvailable;

    private void RegisterStartupCollectionOperations()
    {
        _startupExistingOperations = _journal.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        Register("collect.startup", RunStartupCollectionAsync);
        Register("collect.startup.interrupt", (args, _) => Task.FromResult<object?>(InterruptStartupCollection(args)));
    }

    // The first bootstrap freezes one offer. A later refresh or settings change
    // never silently arms another collection during the same application launch.
    private StartupCollectionOffer PrepareStartupCollection()
    {
        lock (_executionAdmission)
        {
            if (_startupCollectionDecision is null)
            {
                _startupCollectionToken = Guid.NewGuid().ToString("N");
                _startupCollectionDecision = EvaluateStartupCollection("app.bootstrap");
                if (_startupCollectionDecision.Eligible)
                {
                    _startupConfigurationStamp = StartupConfigurationStamp();
                    _startupSourceStamp = StartupSourceStamp();
                }
            }
            return StartupCollectionState();
        }
    }

    private StartupCollectionOffer StartupCollectionState()
    {
        var decision = _startupCollectionDecision ?? new(false, "not-ready", "L’interface doit terminer son ouverture avant la collecte.");
        return new(!_startupCollectionAttempted && decision.Eligible,
            _startupCollectionAttempted && decision.Eligible ? "already-attempted" : decision.Reason,
            _startupCollectionAttempted, _startupCollectionToken,
            _startupCollectionAttempted && decision.Eligible ? "La collecte à l’ouverture a déjà été demandée pendant ce lancement." : decision.Message);
    }

    private object SkipStartupCollection(string? reason = null, string? message = null)
    {
        var state = StartupCollectionState();
        if (reason is not null) state = state with { Eligible = false, Reason = reason, Message = message ?? state.Message };
        return new { started = false, skipped = true, startupCollection = state };
    }

    // Called under the regular admission lock, before waiting for its semaphore.
    // A valid request is consumed immediately, including a busy or cancelled one;
    // an invalid token cannot consume the UI's valid offer.
    private bool TryStartupCollectionAdmission(string operation, JsonElement args, out object? reply)
    {
        reply = null;
        if (operation == "collect.startup.interrupt")
        {
            reply = InterruptStartupCollection(args);
            return true;
        }
        if (operation != "collect.startup")
        {
            if (operation is "records.list" or "workplace.ticket.list" or "workplace.ticket.open")
                InterruptStartupForTickets();
            if (operation is "collect.run" or "collect.manual" or "collect.campaign" or "collect.captureDownload" or "automation.recipe" or "library.capture")
                _startupOtherCollectionRequested = true;
            return false;
        }
        if (_startupCollectionDecision is null || !ValidStartupToken(args))
        {
            reply = SkipStartupCollection("not-ready", "Cette demande ne correspond pas à l’ouverture courante de l’interface.");
            return true;
        }
        if (_startupCollectionAttempted)
        {
            reply = SkipStartupCollection("already-attempted", "La collecte à l’ouverture a déjà été demandée pendant ce lancement.");
            return true;
        }
        _startupCollectionAttempted = true;
        if (!_startupCollectionDecision.Eligible) { reply = SkipStartupCollection(); return true; }
        if (_pendingExecutions.Count > 0 || IsBusy || Jobs.HasPendingWork)
            _startupCollectionDecision = new(false, "busy", "Un traitement est déjà en cours ; la collecte à l’ouverture n’est pas mise en attente.");
        else if (SessionLocked)
            _startupCollectionDecision = new(false, "session-locked", "La session est verrouillée ; aucune collecte à l’ouverture ne sera lancée plus tard.");
        else if (EditorOpen)
            _startupCollectionDecision = new(false, "editor-open", "Un formulaire est ouvert ; la collecte à l’ouverture n’est pas lancée.");
        if (!_startupCollectionDecision.Eligible) { reply = SkipStartupCollection(); return true; }
        _startupCollectionReserved = true;
        return false;
    }

    private object InterruptStartupCollection(JsonElement args)
    {
        var interrupted = ValidStartupToken(args) && InterruptStartupForTickets();
        return new { interrupted, startupCollection = StartupCollectionState() };
    }

    // Only the automatic startup owns this cancellation source. A ticket request
    // cancels its work but keeps the ordinary execution gate: browser cleanup and
    // temporary-column restoration finish before the waiting reader can start.
    private bool InterruptStartupForTickets()
    {
        if (_startupCollectionCancellation is not { IsCancellationRequested: false } cancellation) return false;
        _startupCollectionDecision = new(false, "ticket-consultation", "La collecte à l’ouverture s’arrête pour laisser consulter les tickets.");
        cancellation.Cancel();
        Log("DÉMARRAGE · arrêt de la collecte automatique pour consulter les tickets ; restauration de la vue GLPI avant la lecture.");
        return true;
    }

    private bool ValidStartupToken(JsonElement args) => args.TryGetProperty("token", out var token)
        && token.ValueKind == JsonValueKind.String && token.GetString() == _startupCollectionToken;

    private async Task<object?> RunStartupCollectionAsync(JsonElement args, CancellationToken ct)
    {
        lock (_executionAdmission)
        {
            if (!_startupCollectionReserved || !ValidStartupToken(args)) return SkipStartupCollection("not-ready");
            _startupCollectionReserved = false;
            _startupCollectionDecision = EvaluateStartupCollection("collect.startup");
            if (_startupCollectionDecision.Eligible && _startupConfigurationStamp != StartupConfigurationStamp())
                _startupCollectionDecision = new(false, "configuration-changed", "Les réglages ont changé depuis l’ouverture ; lancez la collecte depuis la recherche choisie.");
            else if (_startupCollectionDecision.Eligible && _startupSourceStamp != StartupSourceStamp())
                _startupCollectionDecision = new(false, "source-changed", "Le relevé affiché a changé depuis l’ouverture ; aucune collecte automatique ne le remplace.");
            _startupConfigurationStamp = ""; _startupSourceStamp = "";
            if (!_startupCollectionDecision.Eligible) return SkipStartupCollection();
        }
        ct.ThrowIfCancellationRequested();
        Log("DÉMARRAGE · collecte de la recherche GLPI active.");
        // Keep the normal collection, cancellation and export path. Recurring
        // campaign/recipe settings and the library collector do not select this run.
        using var empty = JsonDocument.Parse("{}");
        var result = await _handlers["collect.run"](empty.RootElement, ct).ConfigureAwait(false);
        return new { started = true, skipped = false, startupCollection = StartupCollectionState(), result };
    }

    private StartupCollectionDecision EvaluateStartupCollection(string ownOperation)
    {
        var ownsExecution = IsBusy && CurrentOperation == ownOperation;
        var source = Result?.Provenance.SourceKind ?? "";
        bool browserAvailable, networkAvailable;
        try { browserAvailable = _startupBrowserAvailable(Settings); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { browserAvailable = false; }
        try { networkAvailable = _networkAvailable(); }
        catch (NetworkInformationException) { networkAvailable = false; }
        return StartupCollectionPolicy.Evaluate(Settings, new()
        {
            Simulation = IsTestMode || source is "demo" or "simulation",
            LocalSource = Result is not null && (IsLocalResult(Result) || IsCachedSource && source is not ("glpi" or "browser")),
            HistoricalSource = IsHistorical && !IsCachedSource,
            Busy = IsBusy && !ownsExecution || _pendingExecutions.Count > (ownsExecution ? 1 : 0) || Jobs.HasPendingWork,
            EditorOpen = EditorOpen, SessionLocked = SessionLocked,
            AlreadyCollected = _startupOtherCollectionRequested || _journal.Entries.Any(entry => !_startupExistingOperations.Contains(entry.Id)
                && entry.Title.StartsWith("Collecte", StringComparison.Ordinal)),
            BrowserAvailable = browserAvailable, NetworkAvailable = networkAvailable
        });
    }

    private string StartupConfigurationStamp() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    { Settings.PortalUrl, source = SourceContextSignature(Settings), configuration = SessionConfigurationSignature(Settings) }, Json)));

    private string StartupSourceStamp() => JsonSerializer.Serialize(new
    { Revision, loaded = Result is not null, source = Result?.Provenance.SourceKind, run = Result?.Provenance.RunId, IsHistorical, IsCachedSource, IsTestMode }, Json);
}
