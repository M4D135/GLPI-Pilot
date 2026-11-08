using System.IO;
using GlpiPilot.Core;

internal static partial class Program
{
    private static TeamWorkspaceItem CalendarItem(string title) => CalendarWorkspaceDocuments.ForIntervention(new CalendarIntervention
    { Title = title, StartLocal = "2026-10-26T09:00", EndLocal = "2026-10-26T10:00", TimeZoneId = "Europe/Paris", ParticipantKeys = new() { "tech-a" } }, "Auteur synthétique");
    private static TeamWorkspaceItem CalendarTitle(TeamWorkspaceItem item, string title)
    {
        var value = CalendarWorkspaceDocuments.ReadIntervention(item); value.Title = title;
        return CalendarWorkspaceDocuments.ForIntervention(value, "Auteur synthétique");
    }
    private static async Task CheckCalendarLocalAsync()
    {
        await Check("Calendar SQLite batches reject one stale member without changing any row or outbox", async () =>
        {
            var store = await StoreAsync("calendar-stale"); var saved = await store.SaveCalendarBatchAsync(new[] { (CalendarItem("A"), (long?)null), (CalendarItem("B"), (long?)null) });
            var a = saved[0]; var b = saved[1]; var changed = await store.SaveAsync(CalendarTitle(a, "A récente"), a.Revision);
            await RejectAsync<InvalidOperationException>(() => store.SaveCalendarBatchAsync(new[] { (CalendarTitle(a, "A périmée"), (long?)a.Revision), (CalendarTitle(b, "B à rejeter"), (long?)b.Revision) }));
            Require((await RequiredAsync(store, a.Id)).Title == changed.Title && (await RequiredAsync(store, b.Id)).Title == "B", "A rejected member must rollback the whole batch.");
            var pending = (await store.PendingCalendarBatchAsync(default))!;
            Require(pending.Mutations.Count == 2 && pending.Mutations.Single(value => value.Item.Id == a.Id).Item.Title == "A récente", "Editing one pending member must retain the coherent whole group.");
        });

        await Check("Changing the central workspace keeps split-series families together after previous receipts settled", async () =>
        {
            var store = await StoreAsync("calendar-new-central"); var anchor = new CalendarIntervention { Title = "Série", StartLocal = "2026-10-26T09:00", EndLocal = "2026-10-26T10:00", TimeZoneId = "Europe/Paris", Recurrence = new() { Frequency = "daily", Count = 4 } };
            var following = anchor.Copy(); following.StartLocal = "2026-10-28T14:00"; following.EndLocal = "2026-10-28T15:00";
            var split = InterventionCalendarService.SplitFollowing(anchor, "2026-10-28T09:00", following);
            await store.SaveCalendarBatchAsync(new[] { (CalendarWorkspaceDocuments.ForIntervention(split.Before, "Auteur"), (long?)null), (CalendarWorkspaceDocuments.ForIntervention(split.Following, "Auteur"), (long?)null) });
            await Sync(store, new InMemoryTeamWorkspaceReplica()); Require((await store.CountsAsync()).Pending == 0, "Initial transaction should have settled.");
            await store.PrepareRemoteAsync(Guid.NewGuid().ToString("N"), default); var pending = (await store.PendingCalendarBatchAsync(default))!;
            Require(pending.Mutations.Count == 2 && pending.Mutations.All(value => value.BaseRevision == 0), "Changing central identity must resend the coherent split family as one atomic transaction.");
        });
        await Check("Calendar refuses a non-atomic replica without draining any intervention", async () =>
        {
            var store = await StoreAsync("calendar-capability"); await store.SaveCalendarBatchAsync(new[] { (CalendarItem("A"), (long?)null), (CalendarItem("B"), (long?)null) });
            var unsupported = new LegacyCalendarReplica(new InMemoryTeamWorkspaceReplica());
            await RejectAsync<InvalidOperationException>(() => Sync(store, unsupported));
            Require(unsupported.Pushes == 0 && (await store.CountsAsync()).Pending == 2, "Legacy peer must not receive partial calendar writes.");
        });
    }
    private static async Task CheckCalendarReplicationAsync(ReplicaFactory factory, string label)
    {
        await Check(label + ": calendar transactions and complete feeds remain atomic even with page size one", async () =>
        {
            await using var scope = await factory.CreateAsync(); var replica = (ITeamWorkspaceBatchReplica)scope.Replica;
            var a = await StoreAsync("calendar-batch-a"); var b = await StoreAsync("calendar-batch-b");
            var saved = await a.SaveCalendarBatchAsync(new[] { (CalendarItem("Diagnostic"), (long?)null), (CalendarItem("Retour"), (long?)null) });
            var sync = await Sync(a, replica); var changes = await replica.ChangesAsync(0, 1);
            Require(sync.Pushed == 2 && sync.Pending == 0 && changes.Count == 2 && changes.Select(value => value.BatchId).Distinct().Count() == 1 && changes.All(value => value.BatchSize == 2), "A calendar feed page must contain the entire atomic group.");
            await Sync(b, replica); Require((await b.CountsAsync()).Items == 2 && saved.All(item => b.GetAsync(item.Id).GetAwaiter().GetResult()?.Title == item.Title), "Both appointments must arrive together with stable identities.");
            await scope.VerifyProjectionAsync(2);
        });
        await Check(label + ": lost calendar receipt retries idempotently and rejects request-id reuse", async () =>
        {
            await using var scope = await factory.CreateAsync(); var replica = (ITeamWorkspaceBatchReplica)scope.Replica; var store = await StoreAsync("calendar-retry");
            await store.SaveCalendarBatchAsync(new[] { (CalendarItem("A"), (long?)null), (CalendarItem("B"), (long?)null) });
            await store.PrepareRemoteAsync(await replica.InitializeAsync(), default); var batch = (await store.PendingCalendarBatchAsync(default))!;
            var first = await replica.PushBatchAsync(batch); var second = await replica.PushBatchAsync(batch);
            Require(first.Receipts.Select(value => value.Current.CentralRevision).SequenceEqual(second.Receipts.Select(value => value.Current.CentralRevision)) && (await replica.ChangesAsync(0, 200)).Count == 2, "Retry after a lost response must not reapply or append another transaction.");
            var forged = new TeamWorkspaceBatchMutation(batch.MutationId, batch.Mutations.Select((value, index) => index == 0 ? value with { Item = CalendarTitle(value.Item, "Autre contenu") } : value).ToArray());
            await RejectAsync<InvalidDataException>(() => replica.PushBatchAsync(forged));
            await store.AcknowledgeCalendarBatchAsync(batch, second, default); Require((await store.CountsAsync()).Pending == 0, "The complete acknowledged group should settle.");
        });
        await Check(label + ": one concurrent calendar member rejects the whole split and exposes rejected future copies", async () =>
        {
            await using var scope = await factory.CreateAsync(); var replica = (ITeamWorkspaceBatchReplica)scope.Replica;
            var a = await StoreAsync("calendar-conflict-a"); var b = await StoreAsync("calendar-conflict-b");
            var original = await a.SaveAsync(CalendarItem("Série initiale"), null); await Sync(a, replica); await Sync(b, replica);
            var aOld = await RequiredAsync(a, original.Id); var bOld = await RequiredAsync(b, original.Id);
            var future = CalendarItem("Suite proposée");
            await a.SaveCalendarBatchAsync(new[] { (CalendarTitle(aOld, "Passé proposé"), (long?)aOld.Revision), (future, (long?)null) });
            await b.SaveAsync(CalendarTitle(bOld, "Version déjà partagée"), bOld.Revision); await Sync(b, replica);
            var rejected = await Sync(a, replica); await Sync(b, replica);
            Require(rejected.Conflicts >= 2 && (await RequiredAsync(a, original.Id)).Title == "Version déjà partagée" && (await RequiredAsync(a, future.Id)).Deleted
                && (await RequiredAsync(b, original.Id)).Title == "Version déjà partagée" && (await RequiredAsync(b, future.Id)).Deleted, "No orphan future appointment may survive a failed original comparison.");
            var groups = await a.CalendarConflictGroupsAsync(); Require(groups.Count == 1 && groups[0].Items == 2, "The complete failed transaction must remain reviewable, including hidden tombstones.");
            var group = (await a.CalendarConflictGroupAsync(groups[0].Id))!;
            Require(group.Members.Any(value => value.Incoming.Id == future.Id && value.Incoming.Title == "Suite proposée" && !value.Incoming.Deleted), "The never-published future copy must be retained.");
            var expected = group.Latest.ToDictionary(value => value.Id, value => value.Revision, StringComparer.Ordinal);
            await a.ResolveCalendarConflictAsync(group.Id, "incoming", expected, actor: "Responsable synthétique"); await Sync(a, replica); await Sync(b, replica);
            Require((await RequiredAsync(b, original.Id)).Title == "Passé proposé" && !(await RequiredAsync(b, future.Id)).Deleted && await a.CalendarConflictGroupCountAsync() == 0 && await b.CalendarConflictGroupCountAsync() == 0, "Choosing the complete proposal must publish both members atomically and settle the review on every workstation.");
        });
        await Check(label + ": a stale group-resolution preview never overwrites a newer appointment", async () =>
        {
            await using var scope = await factory.CreateAsync(); var replica = (ITeamWorkspaceBatchReplica)scope.Replica;
            var a = await StoreAsync("calendar-resolution-a"); var b = await StoreAsync("calendar-resolution-b");
            var saved = await a.SaveCalendarBatchAsync(new[] { (CalendarItem("A"), (long?)null), (CalendarItem("B"), (long?)null) }); await Sync(a, replica); await Sync(b, replica);
            var oldA = await RequiredAsync(a, saved[0].Id); var oldB = await RequiredAsync(a, saved[1].Id); var other = await RequiredAsync(b, saved[0].Id);
            await a.SaveCalendarBatchAsync(new[] { (CalendarTitle(oldA, "A proposé"), (long?)oldA.Revision), (CalendarTitle(oldB, "B proposé"), (long?)oldB.Revision) });
            await b.SaveAsync(CalendarTitle(other, "A partagé"), other.Revision); await Sync(b, replica); await Sync(a, replica);
            Require((await RequiredAsync(a, oldB.Id)).Title == "B", "The non-conflicting member of a rejected group must remain unchanged too.");
            var groupId = (await a.CalendarConflictGroupsAsync()).Single().Id; var review = (await a.CalendarConflictGroupAsync(groupId))!; var expected = review.Latest.ToDictionary(value => value.Id, value => value.Revision);
            var latest = await RequiredAsync(a, oldB.Id); await a.SaveAsync(CalendarTitle(latest, "B plus récent"), latest.Revision);
            await RejectAsync<InvalidOperationException>(() => a.ResolveCalendarConflictAsync(groupId, "incoming", expected));
            Require((await RequiredAsync(a, oldB.Id)).Title == "B plus récent" && await a.CalendarConflictGroupCountAsync() == 1, "A stale resolution must preserve the changed row and unresolved group.");
        });
        await Check(label + ": an incomplete calendar feed cannot advance the SQLite cursor or install a partial group", async () =>
        {
            await using var scope = await factory.CreateAsync(); var replica = (ITeamWorkspaceBatchReplica)scope.Replica; var a = await StoreAsync("calendar-feed-a"); var b = await StoreAsync("calendar-feed-b");
            await a.SaveCalendarBatchAsync(new[] { (CalendarItem("A"), (long?)null), (CalendarItem("B"), (long?)null) }); await Sync(a, replica);
            var partial = (await replica.ChangesAsync(0, 1)).Take(1).ToArray(); await RejectAsync<InvalidDataException>(() => b.ApplyCalendarChangesAsync(partial, default));
            Require(await b.CursorAsync(default) == 0 && (await b.CountsAsync()).Items == 0, "Incomplete transaction must leave the recipient untouched.");
            await Sync(b, replica); Require((await b.CountsAsync()).Items == 2, "A later complete pull must recover normally.");
        });

        await Check(label + ": a remote pull archives the complete overlapping local pending group", async () =>
        {
            await using var scope = await factory.CreateAsync(); var replica = (ITeamWorkspaceBatchReplica)scope.Replica;
            var a = await StoreAsync("calendar-pull-a"); var b = await StoreAsync("calendar-pull-b");
            var saved = await a.SaveCalendarBatchAsync(new[] { (CalendarItem("A"), (long?)null), (CalendarItem("B"), (long?)null) }); await Sync(a, replica); await Sync(b, replica);
            var beforeA = await RequiredAsync(a, saved[0].Id); var beforeB = await RequiredAsync(a, saved[1].Id);
            await a.SaveCalendarBatchAsync(new[] { (CalendarTitle(beforeA, "A local en attente"), (long?)beforeA.Revision), (CalendarTitle(beforeB, "B local en attente"), (long?)beforeB.Revision) });
            var other = await RequiredAsync(b, beforeA.Id); await b.SaveAsync(CalendarTitle(other, "A reçu du serveur"), other.Revision); await Sync(b, replica);
            var changes = await replica.ChangesAsync(await a.CursorAsync(default), 200); await a.ApplyCalendarChangesAsync(changes, default);
            var groups = await a.CalendarConflictGroupsAsync(); var review = (await a.CalendarConflictGroupAsync(groups.Single().Id))!;
            Require(review.Members.Count == 2 && review.Members.Any(value => value.Incoming.Title == "A local en attente") && review.Members.Any(value => value.Incoming.Title == "B local en attente")
                && (await a.CountsAsync()).Pending == 2, "The unchanged local group member must remain recoverable alongside the overwritten member, with its whole outbox intact.");
        });
        await Check(label + ": calendar edit during an in-flight batch acknowledgement preserves the newer whole outbox", async () =>
        {
            await using var scope = await factory.CreateAsync(); var replica = (ITeamWorkspaceBatchReplica)scope.Replica; var a = await StoreAsync("calendar-inflight");
            var saved = await a.SaveCalendarBatchAsync(new[] { (CalendarItem("A"), (long?)null), (CalendarItem("B"), (long?)null) });
            await a.PrepareRemoteAsync(await replica.InitializeAsync(), default); var request = (await a.PendingCalendarBatchAsync(default))!; var receipt = await replica.PushBatchAsync(request);
            var before = await RequiredAsync(a, saved[0].Id); await a.SaveAsync(CalendarTitle(before, "Saisie plus récente"), before.Revision);
            await a.AcknowledgeCalendarBatchAsync(request, receipt, default); var pending = (await a.PendingCalendarBatchAsync(default))!;
            Require((await a.CountsAsync()).Pending == 2 && pending.MutationId != request.MutationId && pending.Mutations.Single(value => value.Item.Id == before.Id).Item.Title == "Saisie plus récente", "An older receipt must not acknowledge or overwrite any member of the new merged group.");
        });
    }

    private static async Task CheckPostgresCalendarRollbackAsync(ReplicaFactory factory)
    {
        await Check("PostgreSQL: failure on the second calendar member rolls back items, feed and receipt together", async () =>
        {
            await using var scope = await factory.CreateAsync(); var replica = (ITeamWorkspaceBatchReplica)scope.Replica; var store = await StoreAsync("calendar-sql-rollback");
            await store.SaveCalendarBatchAsync(new[] { (CalendarItem("A"), (long?)null), (CalendarItem("B"), (long?)null) });
            await store.PrepareRemoteAsync(await replica.InitializeAsync(), default); var batch = (await store.PendingCalendarBatchAsync(default))!;
            var secondId = batch.Mutations.Select(value => value.Item.Id).Order(StringComparer.Ordinal).Last(); await scope.FailCalendarMemberAsync(secondId);
            await RejectAsync<Npgsql.PostgresException>(() => replica.PushBatchAsync(batch));
            await scope.VerifyProjectionAsync(0); Require((await replica.ChangesAsync(0, 200)).Count == 0 && (await store.CountsAsync()).Pending == 2, "A server failure mid-batch must publish no partial item or feed and retain the entire local outbox.");
            await scope.FailCalendarMemberAsync(null); var retry = await replica.PushBatchAsync(batch); await store.AcknowledgeCalendarBatchAsync(batch, retry, default);
            Require(retry.Outcome == "accepted" && retry.Receipts.Count == 2 && (await store.CountsAsync()).Pending == 0, "The same unacknowledged transaction must retry successfully after rollback.");
            await scope.VerifyProjectionAsync(2);
        });
    }
    private sealed class LegacyCalendarReplica(ITeamWorkspaceReplica inner) : ITeamWorkspaceReplica
    {
        public int Pushes { get; private set; }
        public Task<string> InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public Task<TeamWorkspaceReceipt> PushAsync(TeamWorkspaceMutation mutation, CancellationToken ct = default) { Pushes++; return inner.PushAsync(mutation, ct); }
        public Task<IReadOnlyList<TeamWorkspaceChange>> ChangesAsync(long cursor, int limit, CancellationToken ct = default) => inner.ChangesAsync(cursor, limit, ct);
    }
}
