using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    // Keep the actual nodes selected by the native inspection. Re-resolving
    // its CSS in Playwright can match a renewed copy or an unrelated shadow
    // tree that the document-scoped native adapter never inspected.
    private sealed class ColumnCapture<T>(IJSHandle snapshot, T state, IFrame frame, string origin, string role) : IAsyncDisposable
        where T : class
    {
        public T State { get; } = state;
        public string Role { get; } = role;

        public async Task<IElementHandle?> ElementAsync(CancellationToken ct, string property = "element")
        {
            CheckColumnFrame(frame, origin, ct);
            var value = await snapshot.GetPropertyAsync(property).WaitAsync(ct);
            // A missing control is returned as a JSHandle for JavaScript null.
            // Playwright's AsElement casts instead of returning null for that
            // handle, so use its actual interface before taking ownership.
            var element = value as IElementHandle;
            if (element is null) await value.DisposeAsync();
            return element;
        }

        public async Task<ColumnCaptureValidation<T>> ValidateAsync(CancellationToken ct)
        {
            CheckColumnFrame(frame, origin, ct);
            var json = await snapshot.EvaluateAsync<string>("capture => JSON.stringify(capture.validate())").WaitAsync(ct);
            var current = JsonSerializer.Deserialize<ColumnCaptureValidation<T>>(json)
                ?? throw new InvalidOperationException("La vérification du contrôle de colonnes n’a pas renvoyé de résultat exploitable.");
            if (current.State is null) throw new InvalidOperationException("La vue native du contrôle de colonnes n’est plus vérifiable.");
            RejectColumnAmbiguity(current.State);
            return current;
        }

        public async ValueTask DisposeAsync() => await snapshot.DisposeAsync();
    }

    private sealed class ColumnCaptureValidation<T> where T : class
    {
        public ColumnCaptureValidation() { }
        [JsonPropertyName("state")] public T State { get; set; } = null!;
        [JsonPropertyName("sameElement")] public bool SameElement { get; set; }
        [JsonPropertyName("sameTable")] public bool SameTable { get; set; }
    }

    private sealed class ColumnAmbiguityException(string role, int count) : InvalidOperationException(
        "Le contrôle « " + ColumnRoleLabel(role) + " » présente " + count + " éléments natifs concurrents. "
        + "Aucune nouvelle commande n’est envoyée pour ce contrôle.");

    private static string ColumnRoleLabel(string role) => role switch
    {
        "table" => "tableau de tickets",
        "opener" => "ouverture des colonnes",
        "iframe" => "fenêtre des préférences",
        "close" => "fermeture des colonnes",
        "personal-view" => "vue personnelle",
        "personal-tab" => "onglet personnel",
        "personal-tab-select" => "menu de l’onglet personnel",
        "activate" => "activation de la vue personnelle",
        "disable" => "retour à la vue globale initiale",
        "remove" => "retrait d’une colonne temporaire",
        "select" => "choix de la colonne",
        "add" => "ajout de la colonne",
        "options" => "options de colonne",
        _ => "préparation des colonnes"
    };

    private static void RejectColumnAmbiguity<T>(T state)
    {
        var (role, count) = state switch
        {
            NativeColumnInspection window => (window.AmbiguousRole, window.MatchCount),
            NativeColumnPreferences preferences => (preferences.AmbiguousRole, preferences.MatchCount),
            _ => ("", 0)
        };
        if (role.Length > 0 && count > 1) throw new ColumnAmbiguityException(role, count);
    }

    private static void CheckColumnFrame(IFrame frame, string origin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (frame.IsDetached || Origin(frame.Url) != origin)
            throw new InvalidOperationException("Le contexte des colonnes a changé avant la vérification du contrôle natif.");
    }

    private async Task<ColumnCapture<T>> CaptureColumnAsync<T>(IFrame frame, string origin, string role,
        string? tableSelector, CancellationToken ct) where T : class
    {
        await EnsureColumnReaderAsync(frame, origin, ct);
        var snapshot = await frame.EvaluateHandleAsync("""
            request => request.tableSelector === null
              ? window.__glpiPilotColumns.capturePreferences(request.role)
              : window.__glpiPilotColumns.captureWindow(request.tableSelector, request.role)
            """, new { role, tableSelector }).WaitAsync(ct);
        try
        {
            var json = await snapshot.EvaluateAsync<string>("capture => JSON.stringify(capture.state)").WaitAsync(ct);
            var state = JsonSerializer.Deserialize<T>(json)
                ?? throw new InvalidOperationException("L’inspection des colonnes n’a pas renvoyé de résultat exploitable.");
            RejectColumnAmbiguity(state);
            return new(snapshot, state, frame, origin, role);
        }
        catch { await snapshot.DisposeAsync(); throw; }
    }

    private Task<ColumnCapture<NativeColumnPreferences>> CaptureColumnPreferencesAsync(IFrame frame, string origin,
        string role, CancellationToken ct) => CaptureColumnAsync<NativeColumnPreferences>(frame, origin, role, null, ct);

    private Task<ColumnCapture<NativeColumnInspection>> CaptureColumnWindowAsync(IFrame frame, string origin,
        string selector, string role, CancellationToken ct) => CaptureColumnAsync<NativeColumnInspection>(frame, origin, role, selector, ct);

    private async Task<ColumnCapture<NativeColumnInspection>> CaptureCurrentColumnWindowAsync(TableChoice table,
        IFrame frame, string role, CancellationToken ct)
    {
        var capture = await CaptureColumnWindowAsync(frame, table.FrameOrigin, table.Selector, role, ct);
        if (capture.State.TableSelector.Length > 0) return capture;
        TableChoice? current;
        try { current = await FindCurrentColumnTableAsync(table, table.Headers.Count, ct); }
        catch { await capture.DisposeAsync(); throw; }
        if (current is null) return capture;
        await capture.DisposeAsync();
        RememberCurrentTableReference(table, current);
        return await CaptureColumnWindowAsync(frame, table.FrameOrigin, table.Selector, role, ct);
    }

    private static bool SameColumnWindow(NativeColumnInspection before, ColumnCaptureValidation<NativeColumnInspection> current) =>
        current.State.Supported && current.State.Proof == before.Proof && current.SameElement && current.SameTable;
}
