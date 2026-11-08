using System.IO;

namespace GlpiPilot.Core;

/// <summary>Isolated shared-workspace peer for offline simulation and protocol fault tests; it never opens a network connection.</summary>
public sealed class InMemoryTeamWorkspaceReplica : ITeamWorkspaceBatchReplica
{
    private readonly string _identity = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TeamWorkspaceItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Hash, TeamWorkspaceReceipt Receipt)> _receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Hash, TeamWorkspaceBatchReceipt Receipt)> _batchReceipts = new(StringComparer.Ordinal);
    private readonly List<TeamWorkspaceChange> _changes = [];
    public Task<string> InitializeAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_identity); }
    public async Task<TeamWorkspaceReceipt> PushAsync(TeamWorkspaceMutation mutation, CancellationToken ct = default)
    {
        if (CalendarWorkspaceDocuments.IsKind(mutation.Item.Kind)) throw new InvalidOperationException("Le calendrier exige un lot atomique.");
        await _gate.WaitAsync(ct);
        try
        {
            TeamWorkspaceCodec.Validate(mutation.Item); var hash = TeamWorkspaceCodec.ContentHash(mutation.Item) + ":" + mutation.BaseRevision;
            if (_receipts.TryGetValue(mutation.MutationId, out var cached))
            { if (cached.Hash != hash) throw new InvalidDataException("Identité de mutation réutilisée."); return new(cached.Receipt.Outcome, cached.Receipt.Current.Copy()); }
            var current = _items.GetValueOrDefault(mutation.Item.Id); var same = current is not null && TeamWorkspaceCodec.ContentHash(current) == TeamWorkspaceCodec.ContentHash(mutation.Item);
            var conflict = !same && (current?.CentralRevision ?? 0) != mutation.BaseRevision;
            if (conflict && current is null) throw new InvalidDataException("Version attendue absente.");
            var next = (same || conflict ? current! : mutation.Item).Copy(); var outcome = same ? "unchanged" : conflict ? "conflict" : "accepted";
            if (!same)
            {
                next.CentralRevision = _changes.Count + 1; next.Revision = 0; next.Pending = false; next.Conflicts = 0; _items[next.Id] = next.Copy();
                _changes.Add(new(next.CentralRevision, next.Copy(), conflict ? mutation.Item.Copy() : null, conflict ? "concurrent-edit" : ""));
            }
            var receipt = new TeamWorkspaceReceipt(outcome, next); _receipts.Add(mutation.MutationId, (hash, receipt)); return new(outcome, next.Copy());
        }
        finally { _gate.Release(); }
    }
    public async Task<TeamWorkspaceBatchReceipt> PushBatchAsync(TeamWorkspaceBatchMutation mutation, CancellationToken ct = default)
    {
        var hash = TeamWorkspaceCalendarProtocol.RequestHash(mutation); await _gate.WaitAsync(ct);
        try
        {
            if (_batchReceipts.TryGetValue(mutation.MutationId, out var cached))
            { if (cached.Hash != hash) throw new InvalidDataException("Identité de lot réutilisée."); return Copy(cached.Receipt); }
            var current = mutation.Mutations.ToDictionary(value => value.Item.Id, value => _items.GetValueOrDefault(value.Item.Id), StringComparer.Ordinal);
            if (mutation.Mutations.Any(value => current[value.Item.Id] is { } old && old.Kind != value.Item.Kind)) throw new InvalidDataException("Le type partagé ne peut pas changer.");
            bool Same(TeamWorkspaceMutation value) => current[value.Item.Id] is { } item && TeamWorkspaceCodec.ContentHash(item) == TeamWorkspaceCodec.ContentHash(value.Item);
            var conflict = mutation.Mutations.Any(value => !Same(value) && (current[value.Item.Id]?.CentralRevision ?? 0) != value.BaseRevision);
            var unchanged = !conflict && mutation.ResolvesGroups.Count == 0 && mutation.Mutations.All(Same); var outcome = conflict ? "conflict" : unchanged ? "unchanged" : "accepted";
            var resolved = conflict ? Array.Empty<string>() : mutation.ResolvesGroups.ToArray();
            var receipts = new List<TeamWorkspaceReceipt>(); var staged = new List<TeamWorkspaceChange>();
            foreach (var value in mutation.Mutations.OrderBy(value => value.Item.Id, StringComparer.Ordinal))
            {
                var next = (conflict || unchanged ? current[value.Item.Id] ?? value.Item : value.Item).Copy();
                if (conflict && current[value.Item.Id] is null) next.Deleted = true;
                next.Revision = 0; next.Pending = false; next.Conflicts = 0;
                if (!unchanged)
                {
                    next.CentralRevision = _changes.Count + staged.Count + 1;
                    staged.Add(new(next.CentralRevision, next.Copy(), conflict ? value.Item.Copy() : null, conflict ? "calendar-batch-conflict" : "") { BatchId = mutation.MutationId, BatchSize = mutation.Mutations.Count, ResolvedGroups = resolved });
                }
                receipts.Add(new(outcome, next));
            }
            var receipt = new TeamWorkspaceBatchReceipt(outcome, receipts) { ResolvedGroups = resolved }; TeamWorkspaceCalendarProtocol.ValidateReceipt(mutation, receipt); ct.ThrowIfCancellationRequested();
            foreach (var change in staged) _items[change.Item.Id] = change.Item.Copy(); _changes.AddRange(staged); _batchReceipts.Add(mutation.MutationId, (hash, Copy(receipt))); return receipt;
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<TeamWorkspaceChange>> ChangesAsync(long cursor, int limit, CancellationToken ct = default)
    {
        if (cursor < 0 || limit is < 1 or > 200) throw new InvalidDataException("Curseur invalide.");
        await _gate.WaitAsync(ct);
        try
        {
            var chosen = _changes.Where(change => change.Sequence > cursor).Take(limit).ToList();
            if (chosen.LastOrDefault() is { BatchId.Length: > 0 } last)
            { chosen.RemoveAll(change => change.BatchId == last.BatchId); chosen.AddRange(_changes.Where(change => change.BatchId == last.BatchId)); }
            return chosen.Select(change => new TeamWorkspaceChange(change.Sequence, change.Item.Copy(), change.Incoming?.Copy(), change.Reason) { BatchId = change.BatchId, BatchSize = change.BatchSize, ResolvedGroups = change.ResolvedGroups.ToArray() }).ToArray();
        }
        finally { _gate.Release(); }
    }
    private static TeamWorkspaceBatchReceipt Copy(TeamWorkspaceBatchReceipt receipt) => new(receipt.Outcome, receipt.Receipts.Select(value => new TeamWorkspaceReceipt(value.Outcome, value.Current.Copy())).ToArray()) { ResolvedGroups = receipt.ResolvedGroups.ToArray() };
}
