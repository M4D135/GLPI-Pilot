using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private const string SimulationReaderSignature = "glpi-pilot-local-simulation-v1";
    private readonly DateTimeOffset _simulationVersionOrigin = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);
    private readonly SimulationTicketReplica _simulationTicketReplica = new();
    private readonly SimulationTeamReplica _simulationTeamReplica = new();

    private void InitializeSimulationLibrarySettings()
    {
        var settings = new LibraryPreferences
        {
            LocalFolder = Path.Combine(_simulationRoot!, "Library"), PostgresEnabled = true,
            Host = "replique-locale.simulation.invalid", Database = "simulation_locale", Username = "simulation",
            DisplayName = "Opérateur de simulation", SourceTimeZone = "UTC", SourceDateOrder = "dmy"
        };
        _libraryPreferencesStore.Save(settings); _libraryPreferences = settings;
    }

    private TicketLibrarySeed SimulationSeed(int rowIndex)
    {
        var data = _simulationLiveData ?? RequireResult(); var row = data.Rows[rowIndex];
        var version = _simulationVersions.GetValueOrDefault(rowIndex, 1); var status = SimulationCell(rowIndex, "status");
        var headers = data.Headers.Take(500).Select(header => header.Length > 10000 ? header[..10000] : header).ToList();
        var budget = 256000; var values = new List<string>(); var clipped = data.Headers.Count > headers.Count;
        for (var index = 0; index < headers.Count; index++)
        {
            var value = index < row.Length ? row[index] : ""; var length = Math.Min(value.Length, budget);
            values.Add(value[..length]); budget -= length; clipped |= length != value.Length;
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Simulation"] = "Copie locale isolée ; version interne simulée, sans preuve de lecture GLPI.",
            ["Ligne du relevé importé"] = (rowIndex + 1).ToString(CultureInfo.InvariantCulture)
        };
        if (clipped) fields["Limite de copie"] = "L’index de bibliothèque est abrégé. Le relevé et ses cellules originales restent disponibles dans la page Données.";
        static string Limit(string text, int length) => text.Length > length ? text[..length] : text;
        var code = DashboardService.Status(status);
        return new()
        {
            Ticket = SimulationReference(rowIndex), Title = Limit(SimulationCell(rowIndex, "title"), 10000), Status = Limit(status, 2000), StatusCode = code is >= 1 and <= 6 ? code : null,
            Category = Limit(SimulationCell(rowIndex, "category"), 10000), IndexHeaders = headers, IndexValues = values, Fields = fields,
            LastModifiedRaw = "Version simulée " + version.ToString(CultureInfo.InvariantCulture), LastModifiedReliable = true,
            LastModifiedAt = _simulationVersionOrigin.AddSeconds(version * 2), LastModifiedPrecision = "seconds", ObservedAt = DateTimeOffset.Now
        };
    }

    private static TicketLibraryCoverage SimulationCoverage(TicketDetailResult detail) => new()
    {
        TabsDiscovered = detail.Tabs.Count, TabsLoaded = detail.Tabs.Count, Complete = true,
        Limitations = new() { "Couverture de la copie locale et de ses essais uniquement. Aucune collecte GLPI ; aucun contenu absent du fichier n’est présenté comme observé dans GLPI." }
    };

    private Task<object?> SimulationLibrarySettingsAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var copy = JsonNode.Parse(args.GetRawText()) as JsonObject ?? throw new InvalidDataException("Réglages de bibliothèque manquants.");
        var settingsNode = copy["settings"] as JsonObject ?? throw new InvalidDataException("Réglages de bibliothèque manquants.");
        // Automatic writes remain sandboxed even when the settings form contains
        // a real folder or server. Secret inputs are intentionally never retained.
        settingsNode["localFolder"] = Path.Combine(_simulationRoot!, "Library");
        settingsNode["host"] = "replique-locale.simulation.invalid";
        settingsNode["database"] = "simulation_locale"; settingsNode["username"] = "simulation";
        var next = ReadLibrarySettings(JsonSerializer.SerializeToElement(copy, Json));
        next.ProtectedPassword = ""; next.PasswordBinding = "";
        _libraryPreferencesStore.Save(next); _libraryPreferences = next; _nextLibrarySync = null;
        _libraryPassword = ""; _libraryPasswordBinding = "";
        Publish("library.changed", new { settings = PublicLibrarySettings(next), simulated = true });
        return Task.FromResult<object?>(new { settings = PublicLibrarySettings(next), copied = false, simulated = true,
            message = "Réglages enregistrés dans la simulation. La réplique et le dossier restent locaux ; aucun mot de passe conservé." });
    }

    private async Task<object?> SimulationLibraryCaptureAsync(JsonElement args, CancellationToken ct)
    {
        var mode = Text(args, "mode", 30, "incremental");
        if (mode is not ("incremental" or "all" or "retry")) throw new InvalidDataException("Mode de collecte de bibliothèque inconnu.");
        var data = _simulationLiveData!; var local = TicketLibrary;
        var context = new TicketLibraryCaptureContext(ObjectIdentity.Installation(SimulationPortal), TicketActionService.Hash(data.Provenance.ScopeFingerprint).ToLowerInvariant(), SimulationReaderSignature);
        var parentId = mode == "retry" ? Text(args, "runId", 32) : "";
        var targets = mode == "retry" ? await local.CaptureRetryTargetsAsync(parentId, context, ct).ConfigureAwait(false) : null;
        if (targets is { Count: 0 }) return new { skipped = true, simulated = true, message = "Aucune fiche locale à reprendre." };
        var runId = await local.BeginCaptureAsync(context, mode, parentId, ct).ConfigureAwait(false); var finished = false;
        var limitations = new[] { "Capture simulée de la copie locale uniquement ; aucun portail ni serveur contacté." };
        try
        {
            var index = new TicketLibraryIndex
            {
                SourceUrl = SimulationPortal, ScopeDescription = "Corpus importé de simulation · tous les statuts présents dans ce fichier uniquement",
                ScopeFingerprint = context.Fingerprint, ObservedAt = DateTimeOffset.Now, ExpectedTotal = data.Rows.Count,
                Tickets = Enumerable.Range(0, data.Rows.Count).Select(SimulationSeed).ToList(), Complete = true,
                AllStatuses = true, SearchRestored = true, PagesRead = 1, Limitations = limitations.ToList()
            };
            await local.SetCaptureIndexAsync(runId, index, targets, ct).ConfigureAwait(false);
            var selected = targets?.Select(target => target.TicketKey).ToHashSet(StringComparer.Ordinal);
            var count = 0;
            foreach (var seed in index.Tickets.Where(seed => selected is null || selected.Contains(TicketLibraryRules.Key(seed.Ticket))))
            {
                ct.ThrowIfCancellationRequested(); var observed = await local.ObserveCaptureSeedAsync(runId, seed, ct).ConfigureAwait(false);
                var key = TicketLibraryRules.Key(seed.Ticket);
                if (mode == "incremental" && TicketLibraryStore.CanSkip(seed, observed.Document, SimulationReaderSignature, new() { SafetyMargin = TimeSpan.Zero }))
                    await local.RecordCaptureReuseAsync(runId, key, ct).ConfigureAwait(false);
                else
                {
                    await local.RecordCaptureReadingAsync(runId, key, ct).ConfigureAwait(false);
                    var detail = SimulationDetail(seed.Ticket);
                    await local.SaveCaptureDetailAsync(runId, seed, new() { Detail = detail, Coverage = SimulationCoverage(detail), ReaderSignature = SimulationReaderSignature }, observed.Document.LocalRevision, ct).ConfigureAwait(false);
                }
                await local.ConfirmCaptureTicketAsync(runId, seed, true, ct).ConfigureAwait(false);
                LibraryProgress("Simulation locale · " + (++count).ToString(CultureInfo.InvariantCulture) + "/" + index.Tickets.Count.ToString(CultureInfo.InvariantCulture) + " fiches");
            }
            await local.FinishCaptureVerificationAsync(runId, true, true, ct).ConfigureAwait(false);
            await local.FinishCaptureAsync(runId, "completed", limitations, ct).ConfigureAwait(false); finished = true;
            return SimulationReply(await local.GetCaptureAsync(runId, ct).ConfigureAwait(false), "Capture locale simulée terminée. Aucun contenu GLPI distant n’a été consulté.");
        }
        finally
        {
            if (!finished)
            {
                using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await local.FinishCaptureAsync(runId, ct.IsCancellationRequested ? "cancelled" : "failed", limitations, finish.Token).ConfigureAwait(false);
            }
            TicketLibraryStore.ReleaseCapture(runId); Publish("library.changed", new { captured = true, runId, simulated = true });
        }
    }

    private async Task<object?> SimulationLibrarySyncAsync(CancellationToken ct)
    {
        _librarySyncState = "running"; _librarySyncLabel = "Synchronisation locale simulée en cours";
        try
        {
            var tickets = await TicketLibrarySyncService.SynchronizeAsync(TicketLibrary, _simulationTicketReplica, ct: ct).ConfigureAwait(false);
            var team = await TeamWorkspaceSyncService.SynchronizeAsync(TeamWorkspace, _simulationTeamReplica, ct: ct).ConfigureAwait(false);
            _librarySyncState = "ready"; _librarySyncError = "";
            _librarySyncLabel = "Simulation synchronisée · " + (tickets.Pushed + team.Pushed).ToString(CultureInfo.InvariantCulture) + " envois locaux, " + (tickets.Pulled + team.Pulled).ToString(CultureInfo.InvariantCulture) + " réceptions locales";
            Publish("library.changed", new { synchronized = true, simulated = true });
            return new { message = _librarySyncLabel, tickets, team, pending = tickets.Pending + team.Pending, simulated = true };
        }
        catch (OperationCanceledException) { _librarySyncState = "paused"; _librarySyncLabel = "Échange local interrompu · reprise possible"; throw; }
        finally { _nextLibrarySync = DateTimeOffset.UtcNow.AddMinutes(LibrarySettings.IntervalMinutes); }
    }

    // The regular outbox/cursor engines run against independent in-memory replicas.
    // Copies prevent the client's merge logic from mutating the simulated server.
    private sealed class SimulationTicketReplica : ITicketLibraryReplica
    {
        private readonly string _identity = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, TicketLibraryDocument> _documents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TicketLibraryPushReceipt> _receipts = new(StringComparer.Ordinal);
        private readonly List<TicketLibraryChange> _changes = new();
        public Task<string> InitializeAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_identity); }
        public Task<TicketLibraryPushReceipt> PushAsync(TicketLibraryMutation mutation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_receipts.TryGetValue(mutation.Id, out var receipt)) return Task.FromResult(TicketLibraryRules.Copy(receipt));
            var merge = TicketLibraryRules.Merge(_documents.GetValueOrDefault(mutation.Document.Key), mutation.Document);
            var document = TicketLibraryRules.Copy(merge.Document); document.CentralRevision = _changes.Count + 1; document.LocalRevision = 0; document.Pending = false;
            _documents[document.Key] = TicketLibraryRules.Copy(document);
            _changes.Add(new(document.CentralRevision, TicketLibraryRules.Copy(document), merge.Conflict ? TicketLibraryRules.Copy(mutation.Document) : null, merge.Outcome));
            receipt = new(document.CentralRevision, merge.Outcome, merge.Conflict, document); _receipts[mutation.Id] = TicketLibraryRules.Copy(receipt);
            return Task.FromResult(receipt);
        }
        public Task<IReadOnlyList<TicketLibraryChange>> PullAsync(long afterRevision, int limit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TicketLibraryChange>>(_changes.Where(change => change.Revision > afterRevision).Take(limit).Select(TicketLibraryRules.Copy).ToArray());
        }
    }

    private sealed class SimulationTeamReplica : ITeamWorkspaceReplica
    {
        private readonly string _identity = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, TeamWorkspaceItem> _items = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TeamWorkspaceReceipt> _receipts = new(StringComparer.Ordinal);
        private readonly List<TeamWorkspaceChange> _changes = new();
        public Task<string> InitializeAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_identity); }
        public Task<TeamWorkspaceReceipt> PushAsync(TeamWorkspaceMutation mutation, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); TeamWorkspaceCodec.Validate(mutation.Item);
            if (_receipts.TryGetValue(mutation.MutationId, out var previous)) return Task.FromResult(new TeamWorkspaceReceipt(previous.Outcome, previous.Current.Copy()));
            var current = _items.GetValueOrDefault(mutation.Item.Id);
            var conflict = (current?.CentralRevision ?? 0) != mutation.BaseRevision;
            if (conflict && current is null) throw new InvalidDataException("Version de réplique locale inconnue.");
            var next = (conflict ? current! : mutation.Item).Copy(); next.Revision = 0; next.Pending = false; next.Conflicts = 0; next.CentralRevision = _changes.Count + 1;
            _items[next.Id] = next.Copy(); _changes.Add(new(next.CentralRevision, next.Copy(), conflict ? mutation.Item.Copy() : null, conflict ? "concurrent-edit" : ""));
            var receipt = new TeamWorkspaceReceipt(conflict ? "conflict" : "accepted", next.Copy()); _receipts[mutation.MutationId] = receipt;
            return Task.FromResult(new TeamWorkspaceReceipt(receipt.Outcome, receipt.Current.Copy()));
        }
        public Task<IReadOnlyList<TeamWorkspaceChange>> ChangesAsync(long cursor, int limit, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TeamWorkspaceChange>>(_changes.Where(change => change.Sequence > cursor).Take(limit)
                .Select(change => new TeamWorkspaceChange(change.Sequence, change.Item.Copy(), change.Incoming?.Copy(), change.Reason)).ToArray());
        }
    }
}
