using GlpiPilot.Core;

internal static partial class Program
{
    private static TeamWorkspaceItem BusinessCapacity(int minutes) => SharedBusinessDocuments.Create("team-technician", TechnicianDashboardService.IdentityKey("Alice"),
        "Capacité commune d’Alice", new SharedTechnicianBusiness { DailyCapacityMinutes = minutes }, "Poste de test");
    private static int? Capacity(TeamWorkspaceItem item) => SharedBusinessDocuments.Data<SharedTechnicianBusiness>(item).DailyCapacityMinutes;

    private static async Task CheckBusinessStagingAsync(ReplicaFactory factory, string label)
    {
        await Check(label + ": an edit after a received but unapplied business version keeps the stale CAS base and both proposals", async () =>
        {
            await using var scope = await factory.CreateAsync();
            var a = await StoreAsync("business-received-a"); var b = await StoreAsync("business-received-b");
            var initial = await a.StageBusinessAsync(BusinessCapacity(300), null, null);
            await Sync(a, scope.Replica); await Sync(b, scope.Replica);
            var appliedOnB = await RequiredAsync(b, initial.Id);
            var currentA = await RequiredAsync(a, initial.Id);
            await a.StageBusinessAsync(BusinessCapacity(420), currentA.Revision, currentA.CentralRevision);
            await Sync(a, scope.Replica); await Sync(b, scope.Replica);
            // SQLite received 420, but an interrupted runtime left the file and
            // its applied baseline at 300. The user then changes that file to 360.
            var received = await RequiredAsync(b, initial.Id);
            Require(received.Revision > appliedOnB.Revision && Capacity(received) == 420, "The crash-window fixture needs a newer received document.");
            var staged = await b.StageBusinessAsync(BusinessCapacity(360), appliedOnB.Revision, appliedOnB.CentralRevision);
            Require(staged.Pending && staged.CentralRevision == appliedOnB.CentralRevision, "Staging incorrectly adopted an unseen central revision.");
            var copies = await b.ConflictsAsync(initial.Id);
            Require(copies.Any(x => Capacity(x.Current) == 420 && Capacity(x.Incoming) == 360), "The received version and local edit were not atomically archived.");
            b = new TeamWorkspaceStore(b.DatabasePath);
            var further = await b.StageBusinessAsync(BusinessCapacity(390), staged.Revision, staged.CentralRevision);
            var pending = (await b.PendingAsync(50, default)).Single();
            Require(pending.BaseRevision == appliedOnB.CentralRevision && Capacity(pending.Item) == 390,
                "A repeated file edit or restart incorrectly refreshed the pending CAS base.");
            var result = await Sync(b, scope.Replica); await Sync(a, scope.Replica);
            Require(result.Conflicts > 0 && Capacity(await RequiredAsync(a, initial.Id)) == 420
                && Capacity(await RequiredAsync(b, initial.Id)) == 420 && (await b.CountsAsync()).Pending == 0,
                "An edit based on an unapplied baseline overwrote the accepted remote state.");
            Require((await b.ConflictsAsync(initial.Id)).Any(x => Capacity(x.Incoming) == 390), "The final pending proposal was not retained for review.");
            Require(further.Revision > staged.Revision, "Each genuine staged edit must have a new local revision.");
        });

        await Check(label + ": a stale local business deletion cannot remove a received remote edit", async () =>
        {
            await using var scope = await factory.CreateAsync();
            var a = await StoreAsync("business-delete-a"); var b = await StoreAsync("business-delete-b");
            var initial = await a.StageBusinessAsync(BusinessCapacity(300), null, null);
            await Sync(a, scope.Replica); await Sync(b, scope.Replica);
            var appliedOnB = await RequiredAsync(b, initial.Id); var currentA = await RequiredAsync(a, initial.Id);
            await a.StageBusinessAsync(BusinessCapacity(420), currentA.Revision, currentA.CentralRevision);
            await Sync(a, scope.Replica); await Sync(b, scope.Replica);
            var tombstone = appliedOnB.Copy(); tombstone.Deleted = true;
            await b.StageBusinessAsync(tombstone, appliedOnB.Revision, appliedOnB.CentralRevision);
            await Sync(b, scope.Replica); await Sync(a, scope.Replica);
            var retained = await RequiredAsync(a, initial.Id);
            Require(!retained.Deleted && Capacity(retained) == 420, "A stale file removal deleted the accepted business document.");
            Require((await b.ConflictsAsync(initial.Id)).Any(x => x.Incoming.Deleted && Capacity(x.Incoming) == 300),
                "The proposed deletion and its original contents must remain reviewable.");
        });

        await Check(label + ": an unknown business baseline cannot claim a received revision and old receipts cannot clear a newer proposal", async () =>
        {
            await using var scope = await factory.CreateAsync();
            var a = await StoreAsync("business-first-a"); var b = await StoreAsync("business-first-b");
            var initial = await a.StageBusinessAsync(BusinessCapacity(300), null, null);
            await Sync(a, scope.Replica); await Sync(b, scope.Replica);
            var staged = await b.StageBusinessAsync(BusinessCapacity(360), null, null);
            var pending = (await b.PendingAsync(50, default)).Single();
            Require(staged.CentralRevision == 0 && pending.BaseRevision == 0, "An absent applied baseline must not borrow the received document's revision.");
            var receipt = await scope.Replica.PushAsync(pending);
            Require(receipt.Outcome == "conflict", "The first unknown baseline must preserve the accepted version.");
            var newer = await b.StageBusinessAsync(BusinessCapacity(390), staged.Revision, staged.CentralRevision);
            await b.AcknowledgeAsync(pending, receipt, default);
            var surviving = (await b.PendingAsync(50, default)).Single();
            Require(surviving.MutationId != pending.MutationId && surviving.BaseRevision == 0 && Capacity(surviving.Item) == 390
                && (await RequiredAsync(b, initial.Id)).Revision == newer.Revision, "An older receipt discarded the newer file-backed edit.");
            await Sync(b, scope.Replica); await Sync(a, scope.Replica);
            var final = await RequiredAsync(b, initial.Id);
            Require(Capacity(final) == 300 && (await b.ConflictsAsync(initial.Id)).Any(x => Capacity(x.Incoming) == 390),
                "Concurrent first-join values were overwritten rather than preserved.");
            var unchanged = await b.StageBusinessAsync(final.Copy(), null, null);
            Require(!unchanged.Pending && unchanged.Revision == final.Revision && (await b.CountsAsync()).Pending == 0,
                "An identical document should not create an artificial pending edit or conflict.");
        });
    }
}
