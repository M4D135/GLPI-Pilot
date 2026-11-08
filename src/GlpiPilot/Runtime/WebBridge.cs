using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

/// <summary>A bounded, closed JSON protocol. Cancellation never waits behind a running business operation.</summary>
public sealed class WebBridge : IAsyncDisposable
{
    public const int MaximumMessageCharacters = 4 * 1024 * 1024;
    public const int MaximumPending = 32;
    private readonly HashSet<string> _operations;
    private readonly Func<string, JsonElement, CancellationToken, Task<object?>> _execute;
    private readonly Action<string> _post;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _tasks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _admission = new();
    private volatile bool _disposed;

    public WebBridge(IEnumerable<string> operations, Func<string, JsonElement, CancellationToken, Task<object?>> execute, Action<string> post)
    { _operations = new(operations, StringComparer.Ordinal); _execute = execute; _post = post; }

    public void Receive(string json)
    {
        if (_disposed) return;
        string? id = null;
        try
        {
            if (json.Length > MaximumMessageCharacters) throw new InvalidDataException("Message trop volumineux.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Message invalide.");
            RequireUniqueProperties(root);
            if (!root.TryGetProperty("id", out var identity) || identity.ValueKind != JsonValueKind.String ||
                !Regex.IsMatch(identity.GetString()!, "^[A-Za-z0-9_-]{1,80}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Identifiant de message invalide.");
            id = identity.GetString();
            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "cancel")
            {
                if (root.EnumerateObject().Count() != 2) throw new InvalidDataException("Annulation invalide.");
                // Keep lookup and cancellation under the same lock as removal/disposal.
                // A completed request may otherwise dispose the CTS between the two calls.
                lock (_admission)
                {
                    if (!_disposed && _requests.TryGetValue(id!, out var cancellation)) cancellation.Cancel();
                }
                return;
            }
            if (root.EnumerateObject().Count() != 3 || !root.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String ||
                !_operations.Contains(op.GetString()!) || !root.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Opération inconnue ou paramètres invalides.");
            var arguments = args.Clone(); var operation = op.GetString()!; var requestId = id!;
            lock (_admission)
            {
                if (_disposed) return;
                if (_requests.ContainsKey(requestId)) { Publish("bridge.rejected", new { code = "DUPLICATE_REQUEST" }); return; }
                // Reserve one bounded control slot when the business queue is full.
                // Cancel accepted bridge requests too: some worker tasks may not yet
                // have reached the application's admission lock when cancel runs.
                var cancelling = operation == "operations.cancel";
                if (_requests.Count >= MaximumPending + (cancelling ? 1 : 0)) throw new InvalidDataException("Trop d’opérations en attente. Patientez ou annulez l’opération en cours.");
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _requests[requestId] = cancellation;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _tasks[requestId] = completion.Task;
                Task<object?>? control = null;
                if (cancelling)
                {
                    // The registered application control runs outside its business
                    // semaphore. Block bridge admissions until it has synchronously
                    // cancelled admitted work, then cancel workers not yet admitted.
                    try { control = _execute(operation, arguments, cancellation.Token); }
                    catch (Exception error) { control = Task.FromException<object?>(error); }
                    foreach (var pending in _requests.Where(pair => pair.Key != requestId).Select(pair => pair.Value).ToArray()) pending.Cancel();
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Task<object?> execution;
                        if (control is not null) execution = control;
                        else lock (_admission)
                        { cancellation.Token.ThrowIfCancellationRequested(); execution = _execute(operation, arguments, cancellation.Token); }
                        var result = await execution.ConfigureAwait(false);
                        Send(new { id = requestId, ok = true, result });
                    }
                    catch (OperationCanceledException) { Failure(requestId, "CANCELLED", "Opération annulée."); }
                    catch (Exception error)
                    {
                        AppPaths.AppendLog("Interface · " + operation + " · " + AppPaths.SafeError(error));
                        Failure(requestId, "OPERATION_FAILED", AppPaths.SafeError(error));
                    }
                    finally
                    {
                        lock (_admission)
                        {
                            // Remove both entries before accepting this identity again; an old
                            // worker must never remove the replacement worker's shutdown task.
                            _requests.TryRemove(requestId, out _);
                            _tasks.TryRemove(requestId, out _);
                            cancellation.Dispose();
                            completion.TrySetResult();
                        }
                    }
                });
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            if (id is not null) Failure(id, "INVALID_REQUEST", AppPaths.SafeError(error));
            else Publish("bridge.rejected", new { code = "INVALID_REQUEST" });
        }
    }

    public void Publish(string name, object? data) => Send(new { type = "event", name, data });
    private void Failure(string id, string code, string message) => Send(new { id, ok = false, error = new { code, message } });
    private void Send(object value)
    {
        if (_disposed) return;
        var json = JsonSerializer.Serialize(value, PilotApplication.Json);
        if (json.Length > 32 * 1024 * 1024) throw new InvalidDataException("Résultat trop volumineux. Réduisez le périmètre ou utilisez un export.");
        _post(json);
    }
    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Propriété JSON répétée."); RequireUniqueProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RequireUniqueProperties(item);
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_admission) { if (_disposed) return; _disposed = true; _lifetime.Cancel(); tasks = _tasks.Values.ToArray(); }
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { AppPaths.AppendLog("Fermeture : des opérations annulées achèvent leur nettoyage."); }
        _lifetime.Dispose();
    }
}
