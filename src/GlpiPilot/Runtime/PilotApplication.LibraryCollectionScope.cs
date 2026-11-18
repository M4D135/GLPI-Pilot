using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    // A preview is tied to the exact loaded inventory and source configuration. A
    // durable run receives its own immutable copy; editing the next draft cannot
    // change the tickets of a paused run.
    private sealed record CollectionScopePreview(string Token, long Revision, string Context,
        ExtractionResult? Source, LibraryCollectionScope Scope, LibraryCollectionPlan? Plan);
    private CollectionScopePreview? _collectionScopePreview;

    private sealed class CollectionScopeModel
    {
        public LibraryCollectionScope Scope { get; init; } = new() { Enabled = true };
        public LibraryCollectionScopeCatalog? Catalog { get; init; }
        public LibraryCollectionCoverage? Coverage { get; init; }
        public object? Inventory { get; init; }
        public bool Available { get; init; }
        public string Message { get; init; } = "";
        public string PreviewToken { get; init; } = "";
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public IReadOnlyList<object> Teams { get; init; } = Array.Empty<object>();
    }

    private object[] CollectionTeams() => BusinessDashboardTemplates.WeatherRules(EffectiveSettings.Dashboard,
        new(Array.Empty<string>(), Array.Empty<string>())).Where(rule => rule.Kind == "team" && rule.Values.Count > 0)
        .Select(rule => (object)new { key = "team:" + rule.Id, label = rule.Label, groups = rule.Values.ToArray() }).ToArray();

    private void RegisterLibraryCollectionScopeOperations()
    {
        Register("library.collection.scope", async (args, ct) =>
        {
            if (Text(args, "column", 10000).Length > 0)
            {
                if (!HasCollectionInventory(out var message)) throw new InvalidOperationException(message);
                var source = RequireResult(); var settings = EffectiveSettings.Copy();
                var catalog = await Task.Run(() => LibraryCollectionScopePlanner.Catalog(source, settings,
                    Text(args, "column", 10000), Text(args, "search", 2000), Number(args, "limit", 200, 1, 500), ct), ct).ConfigureAwait(false);
                return new CollectionScopeModel { Catalog = catalog, Available = true, Message = message };
            }
            var context = LibraryCaptureContext(Settings, LibrarySettings);
            var state = await TicketLibrary.CollectionStateAsync(context.Fingerprint, ct).ConfigureAwait(false);
            return await CollectionScopeModelAsync(state, context, args, ct).ConfigureAwait(false);
        });
        Register("library.collection.preview", PreviewLibraryCollectionScopeAsync);
    }

    private bool HasCollectionInventory(out string message)
    {
        if (IsTestMode) { message = "Les données simulées ne peuvent pas préparer une collecte GLPI réelle."; return false; }
        if (Result is null) { message = "Charger un relevé de la liste GLPI pour choisir les tickets à collecter. Les filtres utiliseront ses colonnes et sa date d’observation."; return false; }
        try { RequireCurrentResult(); }
        catch (InvalidOperationException error) { message = error.Message; return false; }
        message = "Les filtres portent sur le relevé chargé. Les tickets apparus depuis ce relevé seront proposés après son actualisation.";
        return true;
    }

    private static object CollectionInventory(LibraryCollectionPlan plan) => new
    {
        sourceUrl = AppPaths.SafeUrl(plan.SourceUrl), observedAt = plan.ObservedAt,
        complete = plan.InventoryComplete, count = plan.InventoryCount, selected = plan.Selected,
        duplicateRows = plan.DuplicateRows, invalidRows = plan.InvalidRows,
        ambiguousRows = plan.AmbiguousRows, unknownDates = plan.UnknownDates,
        scopeFingerprint = plan.Fingerprint, sourceFingerprint = plan.SourceFingerprint,
        verifiedAt = plan.VerifiedAt
    };

    private async Task<CollectionScopeModel> CollectionScopeModelAsync(LibraryCollectionState state,
        TicketLibraryCaptureContext context, JsonElement args, CancellationToken ct)
    {
        var available = HasCollectionInventory(out var message);
        var plan = state.RunId.Length > 0 ? await TicketLibrary.CollectionPlanAsync(state.RunId, context, ct).ConfigureAwait(false) : null;
        var catalog = available ? await Task.Run(() => LibraryCollectionScopePlanner.Catalog(RequireResult(), EffectiveSettings,
            Text(args, "column", 10000), Text(args, "search", 2000), Number(args, "limit", 200, 1, 500), ct), ct).ConfigureAwait(false) : null;
        var coverage = plan is not null ? await TicketLibrary.CollectionPlanCoverageAsync(state.RunId, context, ct).ConfigureAwait(false) : null;
        return new()
        {
            Scope = plan?.Scope ?? (state.Scope.Enabled ? state.Scope : new() { Enabled = true }),
            Catalog = catalog, Coverage = coverage,
            Inventory = plan is not null ? CollectionInventory(plan) : catalog is null ? null : new
            {
                sourceUrl = AppPaths.SafeUrl(catalog.SourceUrl), observedAt = catalog.ObservedAt,
                complete = catalog.InventoryComplete, count = catalog.InventoryCount, selected = (int?)null,
                sourceFingerprint = catalog.SourceFingerprint
            },
            Available = available, Message = message, Warnings = plan?.Warnings ?? new(), Teams = CollectionTeams()
        };
    }

    private async Task<object?> PreviewLibraryCollectionScopeAsync(JsonElement args, CancellationToken ct)
    {
        var context = LibraryCaptureContext(Settings, LibrarySettings);
        if (ReadCollectionActivity().State is "running" or "pausing" or "stopping")
            throw new InvalidOperationException("Mettre la collecte en pause avant de préparer un autre périmètre.");
        var scope = Read<LibraryCollectionScope>(args.GetProperty("scope")); scope.Validate();
        if (!scope.Enabled)
        {
            var token = Guid.NewGuid().ToString("N");
            _collectionScopePreview = new(token, Revision, context.Fingerprint, Result, TicketLibraryRules.Copy(scope), null);
            return new CollectionScopeModel { Scope = scope, Available = true, PreviewToken = token,
                Message = "La collecte globale examine tous les tickets accessibles. Son volume sera connu après lecture de la liste GLPI ; aucun taux ciblé ne peut être calculé à cet aperçu." };
        }
        if (!HasCollectionInventory(out var message)) throw new InvalidOperationException(message);
        var source = RequireResult(); var revision = Revision; var settings = EffectiveSettings.Copy(); var preferences = LibrarySettings.Copy();
        var plan = await Task.Run(() => LibraryCollectionScopePlanner.Build(source, settings, scope, preferences, ct), ct).ConfigureAwait(false);
        var coverage = await TicketLibrary.CollectionCoverageAsync(plan.Tickets, TicketLibraryAcquisition.ReaderSignature, ct).ConfigureAwait(false);
        var catalog = await Task.Run(() => LibraryCollectionScopePlanner.Catalog(source, settings, ct: ct), ct).ConfigureAwait(false);
        if (revision != Revision || !ReferenceEquals(source, Result) || CurrentCollectionContext()?.Fingerprint != context.Fingerprint)
            throw new InvalidOperationException("Le relevé ou la source a changé. Prévisualiser de nouveau le périmètre.");
        var previewToken = Guid.NewGuid().ToString("N");
        _collectionScopePreview = new(previewToken, revision, context.Fingerprint, source, TicketLibraryRules.Copy(scope), plan);
        return new CollectionScopeModel { Scope = plan.Scope, Catalog = catalog, Coverage = coverage, Inventory = CollectionInventory(plan),
            Available = true, Message = message, PreviewToken = previewToken, Warnings = plan.Warnings, Teams = CollectionTeams() };
    }

    private async Task<LibraryCollectionPlan?> ResolveCollectionPlanAsync(JsonElement args, string mode,
        string resumeId, string parent, LibraryCollectionState state, TicketLibraryCaptureContext context,
        IReadOnlyList<TicketLibraryCaptureTarget>? retryTargets, CancellationToken ct)
    {
        if (mode is "resume" or "retry")
        {
            var saved = await TicketLibrary.CollectionPlanAsync(mode == "resume" ? resumeId : parent, context, ct).ConfigureAwait(false);
            if (saved is null) return null; // An older global journal remains resumable by explicit command.
            return mode == "retry" && retryTargets is not null
                ? LibraryCollectionScopePlanner.Restrict(saved, retryTargets.Select(t => t.TicketKey).ToHashSet(StringComparer.Ordinal)) : saved;
        }
        if (Flag(args, "automatic"))
        {
            if (!state.Scope.Enabled || state.RunId.Length == 0)
                throw new InvalidOperationException("Enregistrer un premier périmètre ciblé avant de programmer les collectes suivantes.");
            // The last accepted selection survives a restart. A new inventory is
            // included only after an explicit preview/start, never by broadening a
            // running or scheduled collection behind the user's back.
            return await TicketLibrary.CollectionPlanAsync(state.ScopeRunId.Length > 0 ? state.ScopeRunId : state.RunId, context, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Le périmètre programmé est absent. Prévisualiser de nouveau la sélection.");
        }
        if (!args.TryGetProperty("scope", out var scopeInput))
        {
            if (state.Scope.Enabled)
                throw new InvalidOperationException("Prévisualiser le périmètre ciblé avant de lancer un nouveau passage.");
            if (!Flag(args, "confirmedGlobal"))
                throw new InvalidOperationException("Choisir et prévisualiser un périmètre de collecte. Un parcours global exige un choix explicite.");
            return null; // Existing integrations can still choose the global mode explicitly.
        }
        var scope = Read<LibraryCollectionScope>(scopeInput); scope.Validate();
        var preview = _collectionScopePreview;
        if (preview is null || preview.Token != Text(args, "previewToken", 32) || preview.Revision != Revision
            || preview.Context != context.Fingerprint || !ReferenceEquals(preview.Source, Result)
            || TicketLibraryRules.Serialize(preview.Scope) != TicketLibraryRules.Serialize(scope))
            throw new InvalidOperationException("L’aperçu a changé ou a expiré. Prévisualiser le périmètre avant de lancer la collecte.");
        if (!scope.Enabled)
        {
            if (!Flag(args, "confirmedGlobal")) throw new InvalidOperationException("Choisir explicitement la collecte globale de tous les tickets accessibles.");
            return null;
        }
        if (!HasCollectionInventory(out var message)) throw new InvalidOperationException(message);
        return TicketLibraryRules.Copy(preview.Plan ?? throw new InvalidDataException("Le plan ciblé de l’aperçu est absent."));
    }

    private static LibraryCollectionCoverage? AdjustCollectionCoverage(LibraryCollectionCoverage? current,
        LibraryCollectionCoverage before, LibraryCollectionCoverage after)
    {
        if (current is null) return null;
        return new()
        {
            Selected = current.Selected,
            Complete = current.Complete - before.Complete + after.Complete,
            Missing = current.Missing - before.Missing + after.Missing,
            Partial = current.Partial - before.Partial + after.Partial,
            Changed = current.Changed - before.Changed + after.Changed,
            Unconfirmed = current.Unconfirmed - before.Unconfirmed + after.Unconfirmed
        };
    }
}
