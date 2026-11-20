using System.Security.Cryptography;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

internal static class LibraryCaptureAvailabilityChecks
{
    private const string Installation = "https://availability.example/glpi";
    private static readonly TicketLibraryCaptureContext Context = new(Installation, new string('c', 64), TicketLibraryAcquisition.ReaderSignature);

    public static async Task<(int Passed, int Total)> RunAsync(string root)
    {
        var passed = 0; var total = 0;
        async Task Check(string name, Func<Task> test)
        {
            total++;
            try { await test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { Console.WriteLine("FAIL " + name + "\n" + error); }
        }

        await Check("local availability distinguishes stored complete, partial and undated bodies from missing tickets without any write", async () =>
        {
            var f = await Fixture(root, "inventory");
            var bodies = await Task.WhenAll(f.Seeds.Take(4).Select(seed => f.Store.GetAsync(seed.Ticket)));
            var before = Digest(f.Store); var report = JsonSerializer.Serialize(await f.Store.GetCaptureAsync(f.Run));
            var availability = await f.Store.CaptureAvailabilityAsync(f.Run);
            Require(availability.RunId == f.Run && availability.Observed == 5 && availability.Stored == 4
                && availability.Complete == 2 && availability.Partial == 2 && availability.Missing == 1
                && availability.Confirmed == 2 && availability.Unconfirmed == 2 && availability.Invalid == 0 && availability.Archived == 0,
                "Stored coverage and freshness were conflated or the historical read count was used as availability.");
            Require(Digest(f.Store) == before, "Read-only availability changed the SQLite file.");
            Require(JsonSerializer.Serialize(await f.Store.GetCaptureAsync(f.Run)) == report, "The historical capture report was rewritten.");
            for (var i = 0; i < bodies.Length; i++)
            {
                var after = await f.Store.GetAsync(f.Seeds[i].Ticket);
                Require(after!.ContentHash == bodies[i]!.ContentHash && after.LocalRevision == bodies[i]!.LocalRevision
                    && JsonSerializer.Serialize(after.Detail) == JsonSerializer.Serialize(bodies[i]!.Detail),
                    "Existing body, content hash or revision changed during the local check.");
            }
        });

        await Check("historical partial-date warnings expose preserved content and real section limits without reclassifying the old run", async () =>
        {
            var f = await Fixture(root, "entries"); var before = Digest(f.Store);
            var page = await f.Store.CaptureEntriesAsync(f.Run);
            var partial = page.Items.Single(entry => entry.TicketId == "3");
            Require(partial.HasContent && !partial.DetailComplete && !partial.DetailsCurrent
                && partial.State == "partial" && partial.Category == "missing-last-modified"
                && partial.DetailLimitations.SequenceEqual(["Un onglet de consultation n’est pas devenu lisible."])
                && partial.Message.Contains("sections", StringComparison.Ordinal) && partial.Message.Contains("fuseau", StringComparison.Ordinal),
                "The date warning hid the actual partial-content reason or blocked the stored body.");
            var retained = page.Items.Single(entry => entry.TicketId == "2");
            Require(retained.State == "failed" && retained.HasContent && retained.DetailComplete && retained.DetailsCurrent,
                "A later failed attempt hid a previously stored confirmed body.");
            Require(!page.Items.Single(entry => entry.TicketId == "5").HasContent && Digest(f.Store) == before,
                "Missing content was invented or displaying diagnostics mutated stored data.");
        });

        await Check("missing-only retries exclude complete and partial undated bodies and preserve the full retry option", async () =>
        {
            var f = await Fixture(root, "missing-only"); var before = Digest(f.Store);
            var all = await f.Store.CaptureRetryTargetsAsync(f.Run, Context);
            var missing = await f.Store.CaptureRetryTargetsAsync(f.Run, Context, missingOnly: true);
            Require(all.Select(target => target.TicketId).SequenceEqual(["1", "2", "3", "4", "5"])
                && missing.Select(target => target.TicketId).SequenceEqual(["5"]),
                "Completing missing tickets would recollect an existing complete or partial body.");
            Require(Digest(f.Store) == before, "Choosing the missing-only list changed existing documents.");
        });

        await Check("malformed stored documents are reported separately and never selected as missing content", async () =>
        {
            var f = await Fixture(root, "malformed");
            using (var connection = new SqliteConnection(f.Store.ConnectionString))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "UPDATE ticket_library_tickets SET document_json=$json WHERE ticket_key=$key";
                command.Parameters.AddWithValue("$json", "{ malformed stored evidence");
                command.Parameters.AddWithValue("$key", TicketLibraryRules.Key(f.Seeds[0].Ticket)); command.ExecuteNonQuery();
            }
            var before = Digest(f.Store); var availability = await f.Store.CaptureAvailabilityAsync(f.Run);
            Require(availability.Stored == 3 && availability.Invalid == 1 && availability.Missing == 1
                && availability.Observed == availability.Stored + availability.Invalid + availability.Missing,
                "Malformed evidence was counted as lost or falsely usable.");
            var page = await f.Store.CaptureEntriesAsync(f.Run);
            Require(!page.Items.Single(entry => entry.TicketId == "1").HasContent, "Malformed data was exposed as a valid body.");
            var missing = await f.Store.CaptureRetryTargetsAsync(f.Run, Context, missingOnly: true);
            Require(missing.Count == 1 && missing[0].TicketId == "5" && Digest(f.Store) == before,
                "Malformed saved evidence was rewritten or scheduled for an automatic replacement.");
        });

        await Check("a cancelled local inspection and an unknown run leave the database intact", async () =>
        {
            var f = await Fixture(root, "cancelled"); var before = Digest(f.Store);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await f.Store.CaptureAvailabilityAsync(f.Run, cancellation.Token); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
            try { await f.Store.CaptureAvailabilityAsync(Guid.NewGuid().ToString("N")); throw new InvalidDataException("Unknown capture accepted."); }
            catch (InvalidOperationException) { }
            Require(Digest(f.Store) == before, "A cancelled or rejected audit changed the database.");
        });

        await Check("a preserved body in history or conflicts is reported separately and never collected as missing", async () =>
        {
            foreach (var source in new[] { "history", "conflict" })
            {
                var f = await Fixture(root, "archived-" + source); var seed = f.Seeds[4];
                var archived = TicketLibraryRules.FromDetail(seed, new() { Ticket = seed.Ticket, Title = seed.Title,
                    Sections = [new() { Name = "Fiche", Kind = "fields", Data = new() { Headers = ["Texte"], Rows = [["Copie originale archivée"]], Complete = true } }] },
                    new() { Complete = true }, TicketLibraryAcquisition.ReaderSignature);
                using (var connection = new SqliteConnection(f.Store.ConnectionString))
                {
                    connection.Open(); using var command = connection.CreateCommand();
                    command.CommandText = source == "history"
                        ? "INSERT INTO ticket_library_history(ticket_key,document_json,reason,recorded_at) VALUES($key,$json,'synthetic-preserved',$at)"
                        : "INSERT INTO ticket_library_conflicts(conflict_id,ticket_key,reason,recorded_at,current_json,incoming_json) VALUES('synthetic-preserved',$key,'incomparable-source-version',$at,$json,$json)";
                    command.Parameters.AddWithValue("$key", archived.Key); command.Parameters.AddWithValue("$json", TicketLibraryRules.Serialize(archived));
                    command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
                }
                var before = Digest(f.Store); var availability = await f.Store.CaptureAvailabilityAsync(f.Run);
                Require(availability.Missing == 0 && availability.Archived == 1 && availability.Stored == 4
                    && availability.Observed == availability.Stored + availability.Missing + availability.Archived + availability.Invalid,
                    "Archived evidence was counted as absent or silently promoted to current content.");
                var entry = (await f.Store.CaptureEntriesAsync(f.Run)).Items.Single(item => item.TicketId == "5");
                Require(!entry.HasContent && entry.HasArchivedContent && !entry.DetailsCurrent,
                    "The historical copy was chosen as a current version.");
                Require((await f.Store.CaptureRetryTargetsAsync(f.Run, Context, missingOnly: true)).Count == 0 && Digest(f.Store) == before,
                    "Archived evidence was scheduled for recollection or changed during inspection.");
            }
        });

        await Check("missing-only retries retain source-context and active-run protections", async () =>
        {
            var f = await Fixture(root, "context");
            await Refuse(() => f.Store.CaptureRetryTargetsAsync(f.Run, Context with { Fingerprint = new string('d', 64) }, missingOnly: true));
            var running = await f.Store.BeginCaptureAsync(Context, "incremental");
            try { await Refuse(() => f.Store.CaptureRetryTargetsAsync(running, Context, missingOnly: true)); }
            finally { await f.Store.FinishCaptureAsync(running, "stopped", []); }
        });

        await Check("missing-only membership survives a pause before the first index and excludes every other partial ticket", async () =>
        {
            var f = await Fixture(root, "pre-index"); var targets = await f.Store.CaptureRetryTargetsAsync(f.Run, Context, missingOnly: true);
            var run = await f.Store.BeginCaptureAsync(Context, "retry", f.Run, retryTargets: targets, missingOnly: true);
            var report = await f.Store.GetCaptureAsync(run);
            Require(report.MissingOnly && report.Targeted == 1 && report.Observed == 0, "The selection was not durable before navigation.");
            await f.Store.FinishCaptureAsync(run, "paused", []);
            var reopened = new TicketLibraryStore(f.Store.DatabasePath); await reopened.ResumeCaptureAsync(run, Context);
            var keys = await reopened.ReconcileCaptureIndexAsync(run, Index(f.Seeds));
            Require(keys.SequenceEqual([TicketLibraryRules.Key(f.Seeds[4].Ticket)])
                && (await reopened.CaptureEntriesAsync(run)).Items.Single().TicketId == "5",
                "Pre-index resume widened the missing-only selection to all retryable partial bodies.");
            var otherBefore = JsonSerializer.Serialize(await reopened.GetAsync(f.Seeds[0].Ticket));
            var unrelated = TicketLibraryRules.Copy(f.Seeds[0]); unrelated.Title = "Later unrelated metadata";
            await reopened.ConfirmCaptureTicketAsync(run, unrelated, true);
            Require(JsonSerializer.Serialize(await reopened.GetAsync(f.Seeds[0].Ticket)) == otherBefore,
                "Final confirmation rewrote an unrelated body outside the selected retry.");
            await reopened.FinishCaptureAsync(run, "stopped", []);
        });

        await Check("a frozen retry accepts its first index without widening and rejects a replacement target list", async () =>
        {
            var f = await Fixture(root, "frozen-first-index"); var missing = await f.Store.CaptureRetryTargetsAsync(f.Run, Context, missingOnly: true);
            var all = await f.Store.CaptureRetryTargetsAsync(f.Run, Context);
            var run = await f.Store.BeginCaptureAsync(Context, "retry", f.Run, retryTargets: missing, missingOnly: true);
            await Refuse(() => f.Store.SetCaptureIndexAsync(run, Index(f.Seeds), all));
            await f.Store.SetCaptureIndexAsync(run, Index(f.Seeds));
            Require((await f.Store.CaptureEntriesAsync(run)).Items.Single().TicketId == "5", "The first index replaced frozen membership.");
            await Refuse(() => f.Store.SetCaptureIndexAsync(run, Index(f.Seeds), missing));
            await f.Store.FinishCaptureAsync(run, "stopped", []);
        });

        await Check("missing-only global and window resumes preserve a newly stored partial body without rereading or confirming it", async () =>
        {
            foreach (var window in new[] { false, true })
            {
                var f = await Fixture(root, window ? "window-preserve" : "global-preserve"); var seed = f.Seeds[4];
                var targets = await f.Store.CaptureRetryTargetsAsync(f.Run, Context, missingOnly: true);
                var run = await f.Store.BeginCaptureAsync(Context, "retry", f.Run, retryTargets: targets, missingOnly: true);
                LibraryCollectionPlan? plan = null;
                var index = Index([seed]); index.IsTargeted = window; index.RequestedCount = 1;
                if (window)
                {
                    var source = new ExtractionResult { SourceUrl = Installation + "/front/ticket.php", Complete = true, ExpectedTotal = 1,
                        Headers = ["ID", "Titre", "Dernière modification"], Rows = [["5", seed.Title, seed.LastModifiedRaw]],
                        Provenance = new() { SourceKind = "glpi", SearchVerified = true, ScopeVerified = true,
                            RequestedUrl = Installation + "/front/ticket.php", ScopeFingerprint = "synthetic-scope" } };
                    plan = LibraryCollectionScopePlanner.Build(source, new() { PortalUrl = source.SourceUrl }, new() { Enabled = true },
                        new() { SourceDateOrder = "dmy", SourceTimeZone = "UTC" });
                    await f.Store.SaveCollectionPlanAsync(run, Context, plan); await f.Store.InitializeCollectionWindowsAsync(run, Context, plan);
                    await f.Store.ApplyCollectionWindowIndexAsync(run, Context, index, 0, 1);
                    await f.Store.UpdateCollectionWindowEvidenceAsync(run, Context, 0, 1, [seed]);
                }
                else await f.Store.SetCaptureIndexAsync(run, index, targets);
                Require(!await f.Store.PreserveCaptureContentAsync(run, TicketLibraryRules.Key(seed.Ticket)), "A missing body was skipped.");
                var observed = await f.Store.ObserveCaptureSeedAsync(run, seed);
                await f.Store.SaveCaptureDetailAsync(run, seed, new()
                {
                    Coverage = new() { Complete = false, TabsDiscovered = 2, TabsLoaded = 1, Limitations = ["Onglet incomplet conservé"] },
                    Detail = new() { Ticket = seed.Ticket, Title = seed.Title, ReadAt = DateTimeOffset.UtcNow,
                        Sections = [new() { Name = "Description", Kind = "fields", Data = new() { Headers = ["Texte"], Rows = [["Corps reçu avant la pause"]], Complete = true } }] }
                }, observed.Document.LocalRevision);
                await f.Store.FinishCaptureAsync(run, "paused", []);
                var reopened = new TicketLibraryStore(f.Store.DatabasePath); await reopened.ResumeCaptureAsync(run, Context);
                var before = JsonSerializer.Serialize(await reopened.GetAsync(seed.Ticket));
                if (window)
                {
                    await reopened.InitializeCollectionWindowsAsync(run, Context, plan!);
                    await reopened.ApplyCollectionWindowIndexAsync(run, Context, index, 0, 1);
                    await reopened.UpdateCollectionWindowEvidenceAsync(run, Context, 0, 1, [seed]);
                }
                else await reopened.ReconcileCaptureIndexAsync(run, index);
                Require(await reopened.PreserveCaptureContentAsync(run, TicketLibraryRules.Key(seed.Ticket)), "The partial body would be downloaded again.");
                await reopened.ConfirmCaptureTicketAsync(run, seed, true);
                if (window)
                {
                    await reopened.UpdateCollectionWindowEvidenceAsync(run, Context, 0, 1, [seed]);
                    await reopened.CompleteCollectionWindowAsync(run, Context, 0, 1);
                    Require((await reopened.CollectionPlanAsync(run, Context))!.NextOrdinal == 1, "A preserved partial body blocked the finite-window checkpoint.");
                }
                await reopened.FinishCaptureVerificationAsync(run, true, true); await reopened.FinishCaptureAsync(run, "completed", []);
                var after = await reopened.GetAsync(seed.Ticket); var entry = (await reopened.CaptureEntriesAsync(run)).Items.Single();
                Require(JsonSerializer.Serialize(after) == before && after is { DetailComplete: false, DetailsCurrent: false }
                    && entry.State == "partial" && entry.Category == "stored-preserved" && !entry.DetailsCurrent,
                    "Preserving a body changed its bytes, hash, revision or freshness proof.");
                var report = await reopened.GetCaptureAsync(run);
                Require(report.MissingOnly && report.Read == 1 && report.Reused == 0, "Preservation was counted as a new or confirmed reused read.");
            }
        });
        return (passed, total);
    }

    private sealed record FixtureData(TicketLibraryStore Store, string Run, TicketLibrarySeed[] Seeds);

    private static async Task<FixtureData> Fixture(string root, string name)
    {
        var store = new TicketLibraryStore(Path.Combine(root, "availability-" + name + ".db"));
        var seeds = Enumerable.Range(1, 5).Select(id => new TicketLibrarySeed
        {
            Ticket = new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), Installation + "/front/ticket.form.php?id=" + id),
            Title = "Contenu synthétique " + id, LastModifiedRaw = "20/10/2025 12:00",
            LastModifiedAt = id is 1 or 3 ? null : new DateTimeOffset(2025, 10, 20, 10, 0, 0, TimeSpan.Zero),
            LastModifiedReliable = id is not (1 or 3), LastModifiedPrecision = "minutes"
        }).ToArray();
        var run = await store.BeginCaptureAsync(Context, "incremental");
        await store.SetCaptureIndexAsync(run, new() { SourceUrl = Installation + "/front/ticket.php", AllStatuses = true,
            Complete = true, ScopeFingerprint = "synthetic-scope", Tickets = seeds.ToList(), ExpectedTotal = seeds.Length });
        foreach (var seed in seeds)
        {
            var observed = await store.ObserveCaptureSeedAsync(run, seed);
            if (seed.Ticket.Id == "5") { await store.RecordCaptureFailureAsync(run, observed.Document.Key, "read-timeout"); continue; }
            var complete = seed.Ticket.Id is "1" or "2";
            await store.SaveCaptureDetailAsync(run, seed, new()
            {
                Coverage = new() { Complete = complete, TabsDiscovered = complete ? 1 : 2, TabsLoaded = 1,
                    Limitations = complete ? [] : ["Un onglet de consultation n’est pas devenu lisible."] },
                Detail = new() { Ticket = seed.Ticket, Title = seed.Title, ReadAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    Tabs = [new() { Key = "form", Name = "Ticket", Loaded = true }],
                    Sections = [new() { Name = "Fiche", Kind = "fields", TabKey = "form", Data = new()
                    { Headers = ["Champ", "Valeur"], Rows = [["Description", "Contenu original conservé " + seed.Ticket.Id]], Complete = true } }] }
            }, observed.Document.LocalRevision);
            await store.ConfirmCaptureTicketAsync(run, seed, true);
        }
        // The local body must remain available even when a later action in the same
        // journal failed. Historical counters are not a current inventory.
        await store.RecordCaptureFailureAsync(run, TicketLibraryRules.Key(seeds[1].Ticket), "read-timeout");
        await store.FinishCaptureVerificationAsync(run, true, true);
        await store.FinishCaptureAsync(run, "completed", []);
        return new(store, run, seeds);
    }

    private static string Digest(TicketLibraryStore store) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(store.DatabasePath)));
    private static TicketLibraryIndex Index(IEnumerable<TicketLibrarySeed> seeds)
    {
        var list = seeds.ToList();
        return new() { SourceUrl = Installation + "/front/ticket.php", AllStatuses = true, Complete = true,
            ScopeFingerprint = "synthetic-scope", Tickets = list, ExpectedTotal = list.Count };
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Refuse(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected context or active-capture refusal.");
    }
}
