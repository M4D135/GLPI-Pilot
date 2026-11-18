using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

/// <summary>A bounded live tail plus an ordered durable writer; browser callbacks never wait on SQLite.</summary>
internal sealed class LibraryCollectionJournalSession
{
    private const int MemoryLimit = 1000;
    private readonly object _gate = new();
    private readonly TicketLibraryStore? _store;
    private readonly Action _storageFailure;
    private readonly List<TicketLibraryCollectionEvent> _recent = new();
    private readonly Channel<(string RunId, TicketLibraryCollectionEvent Item)> _writes = Channel.CreateUnbounded<(string, TicketLibraryCollectionEvent)>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writer;
    private string _runId = "";
    private long _sequence;
    private bool _failed;
    private bool _completed;

    public LibraryCollectionJournalSession(TicketLibraryStore? store, TicketLibraryCaptureContext context, Action storageFailure)
    {
        _store = store; Context = context; _storageFailure = storageFailure;
        _writer = WriteAsync();
    }
    public TicketLibraryCaptureContext Context { get; }
    public string RunId { get { lock (_gate) return _runId; } }
    public bool Persisted { get { lock (_gate) return _runId.Length > 0 && !_failed; } }

    public void Bind(string runId)
    {
        lock (_gate)
        {
            if (_store is null) throw new InvalidOperationException("Ce journal de secours ne dispose pas de stockage local.");
            if (_runId.Length > 0) throw new InvalidOperationException("Le journal de cette tentative est déjà associé à une collecte.");
            _runId = runId;
            foreach (var item in _recent) _writes.Writer.TryWrite((runId, item));
        }
    }
    public bool Append(TicketLibraryCollectionEvent item)
    {
        lock (_gate)
        {
            if (_completed) return false;
            item = item with { Sequence = ++_sequence };
            _recent.Add(item);
            if (_recent.Count > MemoryLimit) _recent.RemoveRange(0, _recent.Count - MemoryLimit);
            if (_runId.Length > 0) _writes.Writer.TryWrite((_runId, item));
            return true;
        }
    }
    public TicketLibraryCollectionEventPage MemoryPage(int offset, int limit)
    {
        offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 100);
        lock (_gate) return new() { RunId = _runId, Total = _recent.Count, Offset = offset, Limit = limit, Persisted = false,
            Items = _recent.AsEnumerable().Reverse().Skip(offset).Take(limit).ToList() };
    }
    public async Task CompleteAsync(CancellationToken ct)
    {
        lock (_gate) { _completed = true; _writes.Writer.TryComplete(); }
        try { await _writer.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { _stop.Cancel(); StorageFailed(); }
    }
    private async Task WriteAsync()
    {
        try
        {
            await foreach (var pending in _writes.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                bool failed; lock (_gate) failed = _failed;
                if (failed) continue;
                try
                {
                    if (_store is null) throw new InvalidOperationException("Stockage du journal indisponible.");
                    await _store.AppendCollectionEventAsync(pending.RunId, Context, pending.Item, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is SqliteException or IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException)
                { StorageFailed(); }
            }
        }
        catch (OperationCanceledException) { StorageFailed(); }
    }
    private void StorageFailed()
    {
        lock (_gate) { if (_failed) return; _failed = true; }
        _storageFailure();
    }
}
