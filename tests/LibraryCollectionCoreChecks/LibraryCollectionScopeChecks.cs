using System.Diagnostics;
using System.Globalization;
using GlpiPilot.Core;

internal static class LibraryCollectionScopeChecks
{
    private const string Installation = "https://scope.example/glpi";
    private static readonly string Reader = TicketLibraryAcquisition.ReaderSignature;
    private static readonly TicketLibraryCaptureContext Context = new(Installation, new string('d', 64), Reader);
    private static readonly DateTimeOffset Modified = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly LibraryPreferences Preferences = new() { SourceTimeZone = "UTC", SourceDateOrder = "ymd" };
    private static AppSettings Settings() => new() { PortalUrl = Installation + "/front/ticket.php", Dashboard = new() { SourceTimeZone = "UTC", DateCulture = "fr-FR" } };

    public static async Task<(int Passed, int Total)> RunAsync(string root)
    {
        var passed = 0; var total = 0;
        async Task Check(string label, Func<Task> action)
        {
            total++;
            try { await action(); passed++; Console.WriteLine("PASS Targeted scope: " + label); }
            catch (Exception error) { Console.WriteLine("FAIL Targeted scope: " + label + "\n" + error); }
        }
        TicketLibraryStore Store(string name) => new(Path.Combine(root, "scope-" + name + ".db"));

        await Check("date history combines with groups and users, using OR inside one field and AND between fields", () =>
        {
            var scope = new LibraryCollectionScope { Enabled = true, FromDate = "2025-01-01", ToDate = "2025-01-31", Fields =
                [new() { Column = "Attribué à - Groupe", Values = ["Support", "Infra"] }, new() { Column = "Attribué à - Technicien", Values = ["Alice"] }] };
            var plan = Plan(Data(), scope);
            Require(plan.Tickets.Select(seed => seed.Ticket.Id).SequenceEqual(["101", "102"]) && plan.UnknownDates == 1);
            Require(plan.InventoryCount == 5 && plan.Selected == 2 && plan.InventoryComplete && plan.Warnings.Count == 1);
            return Task.CompletedTask;
        });
        await Check("inclusive day boundaries follow the source timezone rather than the workstation", () =>
        {
            var data = Data(); data.Rows = [Row("1", created: "2025-01-01T23:30:00Z"), Row("2", created: "2025-01-02T22:59:59Z"), Row("3", created: "2025-01-02T23:00:00Z")];
            var settings = Settings(); settings.Dashboard.SourceTimeZone = "Europe/Paris";
            var plan = LibraryCollectionScopePlanner.Build(data, settings, new() { Enabled = true, FromDate = "2025-01-02", ToDate = "2025-01-02" }, Preferences);
            Require(plan.Tickets.Select(seed => seed.Ticket.Id).SequenceEqual(["1", "2"])); return Task.CompletedTask;
        });
        await Check("unavailable columns and malformed dates fail closed without widening", async () =>
        {
            await Refuse(() => Task.FromResult(Plan(Data(), new() { Enabled = true, FromDate = "2025-02-30" })));
            await Refuse(() => Task.FromResult(Plan(Data(), new() { Enabled = true, Fields = [new() { Column = "Absent", Values = ["Alice"] }] })));
            var settings = Settings(); settings.Dashboard.ColumnMappings["created"] = "Missing date";
            await Refuse(() => Task.FromResult(LibraryCollectionScopePlanner.Build(Data(), settings, new() { Enabled = true, FromDate = "2025-01-01" }, Preferences)));
            var data = Data(); data.Headers.Add("Attribué à - Groupe"); data.Rows = data.Rows.Select(row => row.Append(row[4]).ToArray()).ToList();
            await Refuse(() => Task.FromResult(Plan(data, new() { Enabled = true, Fields = [new() { Column = "Attribué à - Groupe", Values = ["Support"] }] })));
        });
        await Check("numeric IDs are exact and normalized; team groups may be absent without matching another group", () =>
        {
            var byId = Plan(Data(), new() { Enabled = true, TicketIds = ["000102"] });
            Require(byId.Selected == 1 && byId.Tickets[0].Ticket.Id == "102");
            var byGroup = Plan(Data(), new() { Enabled = true, Fields = [new() { Column = "Attribué à - Groupe", Values = ["Support", "Absent group"] }] });
            Require(byGroup.Selected == 3 && byGroup.Tickets.All(seed => seed.Ticket.Id != "103"));
            var none = Plan(Data(), new() { Enabled = true, Fields = [new() { Column = "Attribué à - Groupe", Values = ["Absent group"] }] });
            Require(none.Selected == 0); return Task.CompletedTask;
        });
        await Check("subject filtering is local, accent insensitive, and lexical similarity has a real threshold", () =>
        {
            var text = Plan(Data(), new() { Enabled = true, Text = "ACCÈS vpn" });
            Require(text.Tickets.Select(seed => seed.Ticket.Id).SequenceEqual(["102", "104", "105"]));
            var similar = Plan(Data(), new() { Enabled = true, SimilarSubject = "Accès VPN refusé" });
            Require(similar.Tickets.Select(seed => seed.Ticket.Id).SequenceEqual(["102", "104", "105"]));
            return Task.CompletedTask;
        });
        await Check("group identities keep accents and punctuation distinct while ignoring case only", () =>
        {
            var data = Data(); data.Rows = [Row("1", group: "Équipe"), Row("2", group: "Equipe"), Row("3", group: "éQUIPE"), Row("4", group: "Équi-pe")];
            var plan = Plan(data, new() { Enabled = true, Fields = [new() { Column = "Attribué à - Groupe", Values = ["Équipe"] }] });
            Require(plan.Tickets.Select(seed => seed.Ticket.Id).SequenceEqual(["1", "3"]));
            var values = LibraryCollectionScopePlanner.Catalog(data, Settings(), "Attribué à - Groupe", "equipe").Fields.Single(field => field.Role == "group").Values;
            Require(values.Count == 2 && values.Single(value => value.Value == "Équipe").Count == 2 && values.Single(value => value.Value == "Equipe").Count == 1);
            return Task.CompletedTask;
        });
        await Check("identical duplicate IDs are collected once and contradictory duplicates are excluded before filtering", () =>
        {
            var data = Data(); data.Rows.Add(data.Rows[0].ToArray());
            var plan = Plan(data, new() { Enabled = true }); Require(plan.Selected == 5 && plan.DuplicateRows == 1 && plan.AmbiguousRows == 0);
            var conflicting = data.Rows[1].ToArray(); conflicting[4] = "Invisible other team"; data.Rows.Add(conflicting);
            var limited = Plan(data, new() { Enabled = true, Fields = [new() { Column = "Attribué à - Groupe", Values = ["Support"] }] });
            Require(limited.Tickets.All(seed => seed.Ticket.Id != "102") && limited.AmbiguousRows == 1); return Task.CompletedTask;
        });
        await Check("facets split group participants and expose source freshness, missing capabilities and rare searchable values", () =>
        {
            var data = Data(); var catalog = LibraryCollectionScopePlanner.Catalog(data, Settings());
            Require(catalog.TitleAvailable && catalog.ObservedAt == data.ExtractedAt && catalog.InventoryCount == 5
                && catalog.DateRoles.Single(role => role.Role == "created").Available && !catalog.DateRoles.Single(role => role.Role == "closed").Available);
            Require(catalog.Fields.Single(field => field.Role == "group").Values.Any(value => value.Value == "Infra" && value.Count == 2));
            data.Rows = Enumerable.Range(1, 350).Select(id => Row(id.ToString(CultureInfo.InvariantCulture), requester: "Person " + id.ToString("D3", CultureInfo.InvariantCulture))).ToList();
            var all = LibraryCollectionScopePlanner.Catalog(data, Settings(), "Demandeur");
            Require(all.Fields.Single(field => field.Role == "requester").Truncated);
            var rare = LibraryCollectionScopePlanner.Catalog(data, Settings(), "Demandeur", "350");
            Require(rare.Fields.Single(field => field.Role == "requester").Values.Single().Value == "Person 350"); return Task.CompletedTask;
        });
        await Check("another GLPI origin is refused, and partial inventories never claim global completeness", async () =>
        {
            var data = Data(); data.SourceUrl = "https://another.example/glpi/front/ticket.php";
            await Refuse(() => Task.FromResult(Plan(data, new() { Enabled = true })));
            data = Data(); data.Complete = false; var plan = Plan(data, new() { Enabled = true });
            Require(!plan.InventoryComplete && plan.Warnings.Count > 0 && plan.Selected == 5);
        });
        await Check("an old complete confirmed version is reused without an age-based reread, including a compatible earlier reader", async () =>
        {
            var store = Store("reuse"); var seed = Seed("1"); await Save(store, seed, readAt: Modified.AddDays(1));
            var stored = (await store.GetAsync(seed.Ticket))!;
            Require(TicketLibraryStore.CanSkip(seed, stored, Reader) && LibraryCollectionScopePlanner.CanReuse(seed, stored, Reader));
            var earlier = Seed("2"); await Save(store, earlier, reader: "glpi-browser-library-v3-confirmed");
            Require(LibraryCollectionScopePlanner.CanReuse(earlier, await store.GetAsync(earlier.Ticket), Reader));
            seed.LastModifiedAt = Modified.AddHours(1); seed.LastModifiedRaw = "2025-01-01 13:00";
            Require(!LibraryCollectionScopePlanner.CanReuse(seed, stored, Reader));
        });
        await Check("SQL coverage categories are disjoint and unconfirmed content never inflates completion", async () =>
        {
            var store = Store("coverage"); var seeds = Enumerable.Range(1, 5).Select(id => Seed(id.ToString(CultureInfo.InvariantCulture))).ToArray();
            await Save(store, seeds[0]); await Save(store, seeds[2], complete: false); await Save(store, seeds[3]);
            var run = await store.BeginCaptureAsync(Context, "incremental");
            await store.SetCaptureIndexAsync(run, Index(seeds[4])); await SavePending(store, run, seeds[4]); await store.FinishCaptureAsync(run, "paused", []);
            seeds[3] = Seed("4", Modified.AddDays(1));
            var coverage = await store.CollectionCoverageAsync(seeds, Reader);
            Require(coverage.Selected == 5 && coverage.Complete == 1 && coverage.Missing == 1 && coverage.Partial == 1 && coverage.Changed == 1 && coverage.Unconfirmed == 1
                && coverage.Percent == 20 && coverage.ToRead == 4);
            var uncertain = Seed("1"); uncertain.LastModifiedReliable = false;
            Require((await store.CollectionCoverageAsync([uncertain], Reader)).Unconfirmed == 1);
            Require((await store.CollectionCoverageAsync([], Reader)).Percent is null);
        });
        await Check("durable plans survive restart, preserve exact membership and reject context changes or replacement", async () =>
        {
            var store = Store("durable"); var plan = Plan(Data(), new() { Enabled = true, TicketIds = ["101", "102"] });
            var run = await store.BeginCaptureAsync(Context, "incremental"); await store.SaveCollectionPlanAsync(run, Context, plan);
            await store.FinishCaptureAsync(run, "paused", []);
            var reopened = new TicketLibraryStore(store.DatabasePath); var loaded = (await reopened.CollectionPlanAsync(run, Context))!;
            Require(loaded.Fingerprint == plan.Fingerprint && loaded.SourceFingerprint == plan.SourceFingerprint && loaded.Selected == 2
                && loaded.Tickets.Select(seed => seed.Ticket.Id).SequenceEqual(["101", "102"]));
            await Refuse(() => reopened.CollectionPlanAsync(run, Context with { Fingerprint = new string('e', 64) }));
            await Refuse(() => reopened.SaveCollectionPlanAsync(run, Context, Plan(Data(), new() { Enabled = true, TicketIds = ["101"] })));
            var tampered = Plan(Data(), new() { Enabled = true }); tampered.Tickets.RemoveAt(0);
            await Refuse(() => reopened.SaveCollectionPlanAsync(run, Context, tampered));
        });
        await Check("fresh targeted evidence updates coverage while the inventory selection and its timestamp remain immutable", async () =>
        {
            var data = Data(); data.Rows = [Row("1"), Row("2")]; var plan = Plan(data, new() { Enabled = true });
            var store = Store("evidence"); var run = await store.BeginCaptureAsync(Context, "incremental"); await store.SaveCollectionPlanAsync(run, Context, plan);
            await Save(store, plan.Tickets[0]); await Save(store, plan.Tickets[1]);
            var fresh = Seed("1", Modified.AddDays(2)); fresh.ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await store.UpdateCollectionPlanEvidenceAsync(run, Context, [fresh, plan.Tickets[1]]);
            var changed = await store.CollectionPlanCoverageAsync(run, Context); Require(changed.Changed == 1 && changed.Complete == 1);
            await Save(store, fresh);
            Require((await store.CollectionPlanCoverageAsync(run, Context)).Complete == 2);
            var loaded = (await store.CollectionPlanAsync(run, Context))!;
            Require(loaded.Fingerprint == plan.Fingerprint && loaded.ObservedAt == plan.ObservedAt && loaded.VerifiedAt.HasValue && loaded.EvidenceCount == 2
                && loaded.Tickets[0].LastModifiedRaw == plan.Tickets[0].LastModifiedRaw);
            await store.UpdateCollectionPlanEvidenceAsync(run, Context, [fresh]);
            var absent = await store.CollectionPlanCoverageAsync(run, Context); Require(absent.Complete == 1 && absent.Unconfirmed == 1 && absent.Selected == 2);
            await Refuse(() => store.UpdateCollectionPlanEvidenceAsync(run, Context, [Seed("999")]));
            Require((await store.CollectionPlanCoverageAsync(run, Context)).Complete == 1);
            await store.FinishCaptureAsync(run, "completed", []);
            var nextRun = await store.BeginCaptureAsync(Context, "incremental"); await store.SaveCollectionPlanAsync(nextRun, Context, loaded);
            var next = (await store.CollectionPlanAsync(nextRun, Context))!;
            Require(next.EvidenceCount == 0 && next.VerifiedAt is null && next.Fingerprint == plan.Fingerprint);
            await store.UpdateCollectionPlanEvidenceAsync(nextRun, Context, [fresh]);
            Require((await store.CollectionPlanAsync(nextRun, Context))!.EvidenceCount == 1);
            await store.FinishCaptureAsync(nextRun, "completed", []);
        });
        await Check("a retry narrows the frozen worklist and cannot introduce a new identity", async () =>
        {
            var plan = Plan(Data(), new() { Enabled = true });
            var retry = LibraryCollectionScopePlanner.Restrict(plan, new HashSet<string>([TicketLibraryRules.Key(plan.Tickets[0].Ticket)], StringComparer.Ordinal));
            Require(retry.Selected == 1 && retry.Fingerprint != plan.Fingerprint && retry.SourceFingerprint == plan.SourceFingerprint && retry.Scope.Enabled);
            await Refuse(() => Task.FromResult(LibraryCollectionScopePlanner.Restrict(plan, new HashSet<string>([TicketLibraryRules.Key(Seed("999").Ticket)]))));
        });
        await Check("collection settings persist the explicit scope and reject invalid saved scopes", async () =>
        {
            var store = Store("state"); var state = await store.CollectionStateAsync(Context.Fingerprint);
            Require(!state.Scope.Enabled); state.Scope = new() { Enabled = true, FromDate = "2025-01-01" };
            await store.SaveCollectionStateAsync(Context.Fingerprint, state, state.Revision);
            var loaded = await new TicketLibraryStore(store.DatabasePath).CollectionStateAsync(Context.Fingerprint);
            Require(loaded.Scope.Enabled && loaded.Scope.FromDate == "2025-01-01");
            loaded.Scope.FromDate = "bad"; await Refuse(() => store.SaveCollectionStateAsync(Context.Fingerprint, loaded, loaded.Revision));
        });
        await Check("the original scheduled scope survives more than twenty narrowed retry journals", async () =>
        {
            var store = Store("retention"); var plan = Plan(Data(), new() { Enabled = true, TicketIds = ["101"] });
            var original = await store.BeginCaptureAsync(Context, "incremental"); await store.SaveCollectionPlanAsync(original, Context, plan);
            await store.FinishCaptureAsync(original, "completed", []);
            var state = await store.CollectionStateAsync(Context.Fingerprint); state.Scope = plan.Scope; state.RunId = original; state.ScopeRunId = original; state.State = "completed";
            await store.SaveCollectionStateAsync(Context.Fingerprint, state, state.Revision);
            for (var index = 0; index < TicketLibraryStore.CaptureHistoryLimit + 2; index++)
            {
                var run = await store.BeginCaptureAsync(Context, "incremental"); await store.FinishCaptureAsync(run, "completed", []);
            }
            Require((await store.CollectionPlanAsync(original, Context))!.Fingerprint == plan.Fingerprint);
            var invalid = await store.CollectionStateAsync(Context.Fingerprint); invalid.ScopeRunId = "not-a-run";
            await Refuse(() => store.SaveCollectionStateAsync(Context.Fingerprint, invalid, invalid.Revision));
        });
        await Check("180,000 inventory rows are filtered locally to one target, without the old 100,000 limit", () =>
        {
            var data = Data(); data.Rows = Enumerable.Range(1, 180000).Select(id => Row(id.ToString(CultureInfo.InvariantCulture))).ToList();
            data.ExpectedTotal = data.Rows.Count; var timer = Stopwatch.StartNew();
            var plan = Plan(data, new() { Enabled = true, TicketIds = ["180000"] });
            Require(plan.InventoryCount == 180000 && plan.Selected == 1 && plan.Tickets[0].Ticket.Id == "180000" && plan.InventoryComplete);
            Console.WriteLine($"PERF 180000 inventory rows -> 1 selected in {timer.Elapsed.TotalSeconds:F2}s (local metadata only)."); return Task.CompletedTask;
        });
        await Check("coverage accepts 180,000 distinct targets in bounded SQL batches without materializing ticket bodies", async () =>
        {
            var seeds = Enumerable.Range(1, 180000).Select(id => Seed(id.ToString(CultureInfo.InvariantCulture))).ToArray();
            var timer = Stopwatch.StartNew(); var coverage = await Store("large-coverage").CollectionCoverageAsync(seeds, Reader);
            Require(coverage.Selected == 180000 && coverage.Missing == 180000 && coverage.Complete == 0 && coverage.Percent == 0);
            Console.WriteLine($"PERF 180000 target coverage in {timer.Elapsed.TotalSeconds:F2}s (bounded metadata queries).");
        });
        await Check("an unfiltered 180,000-row wide inventory keeps only compact worklist metadata", () =>
        {
            var data = Data(); var extra = Enumerable.Range(1, 40).Select(index => "Extra column " + index).ToList(); data.Headers.AddRange(extra);
            var payload = new string('x', 1000);
            data.Rows = Enumerable.Range(1, 180000).Select(id => Row(id.ToString(CultureInfo.InvariantCulture)).Concat(Enumerable.Repeat(payload, extra.Count)).ToArray()).ToList();
            data.ExpectedTotal = data.Rows.Count; var memoryBefore = GC.GetTotalMemory(true); var timer = Stopwatch.StartNew();
            var plan = Plan(data, new() { Enabled = true }); timer.Stop(); var retained = Math.Max(0, GC.GetTotalMemory(true) - memoryBefore);
            Require(plan.Selected == 180000 && plan.Tickets.All(seed => seed.Fields.Count == 0 && seed.IndexHeaders.Count == 0 && seed.IndexValues.Count == 0));
            Console.WriteLine($"PERF 180000 rows x 48 columns -> 180000 compact targets in {timer.Elapsed.TotalSeconds:F2}s; retained plan memory approximately {retained / 1024d / 1024d:F1} MiB.");
            return Task.CompletedTask;
        });
        Console.WriteLine($"{passed}/{total} targeted collection core checks passed.");
        return (passed, total);
    }

    private static ExtractionResult Data() => new()
    {
        Headers = ["ID", "Titre", "Date d'ouverture", "Dernière modification", "Attribué à - Groupe", "Attribué à - Technicien", "Demandeur", "Statut"],
        Rows = [Row("101", "Panne VPN Bordeaux", "2025-01-01 00:00", "Support;Infra"), Row("102", "VPN accès refusé", "2025-01-31 23:59", "Support"),
            Row("103", "Imprimante hors service", "2024-12-31 23:59", "Support B"), Row("104", "Accès VPN bloqué", "2025-02-01 00:00", "Infra", "Eve"),
            Row("105", "Échec accès VPN", "non connu", "Support")],
        SourceUrl = Installation + "/front/ticket.php", ExtractedAt = new(2025, 3, 1, 12, 0, 0, TimeSpan.Zero), Complete = true,
        Provenance = new() { SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "fixture-native-scope", SourceKind = "glpi", RequestedUrl = Installation + "/front/ticket.php" }
    };
    private static string[] Row(string id, string title = "Sujet du ticket", string created = "2025-01-01 12:00", string group = "Support", string assignee = "Alice", string requester = "Bob")
        => [id, title, created, "2025-01-01 12:00", group, assignee, requester, "Résolu"];
    private static LibraryCollectionPlan Plan(ExtractionResult data, LibraryCollectionScope scope) => LibraryCollectionScopePlanner.Build(data, Settings(), scope, Preferences);
    private static TicketLibrarySeed Seed(string id, DateTimeOffset? modified = null)
    {
        var at = modified ?? Modified;
        return new() { Ticket = new(id, Installation + "/front/ticket.form.php?id=" + id), Title = "Sujet du ticket", Status = "Résolu", StatusCode = 5,
            LastModifiedRaw = at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), LastModifiedAt = at, LastModifiedReliable = true, LastModifiedPrecision = "minutes" };
    }
    private static TicketLibraryIndex Index(params TicketLibrarySeed[] seeds) => new() { SourceUrl = Installation + "/front/ticket.php", AllStatuses = true, Complete = true, ScopeFingerprint = "fixture-native-scope", Tickets = seeds.ToList(), ExpectedTotal = seeds.Length };
    private static TicketDetailResult Detail(TicketLibrarySeed seed, DateTimeOffset? readAt = null) => new()
    {
        Ticket = seed.Ticket, Title = seed.Title, ReadAt = readAt ?? DateTimeOffset.UtcNow.AddMinutes(-5),
        Sections = [new() { Name = "Traitement", Kind = "timeline", Data = new() { Headers = ["Texte"], Rows = [["Contenu synthétique"]], Complete = true } }]
    };
    private static Task Save(TicketLibraryStore store, TicketLibrarySeed seed, bool complete = true, DateTimeOffset? readAt = null, string? reader = null)
        => store.SaveDetailAsync(seed, Detail(seed, readAt), new() { Complete = complete, TabsDiscovered = 0, TabsLoaded = 0 }, reader ?? Reader);
    private static async Task SavePending(TicketLibraryStore store, string run, TicketLibrarySeed seed)
    {
        var observed = await store.ObserveCaptureSeedAsync(run, seed); await store.RecordCaptureReadingAsync(run, TicketLibraryRules.Key(seed.Ticket));
        await store.SaveCaptureDetailAsync(run, seed, new() { Detail = Detail(seed), Coverage = new() { Complete = true, TabsDiscovered = 0, TabsLoaded = 0 }, ReaderSignature = Reader }, observed.Document.LocalRevision);
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Targeted collection invariant failed."); }
    private static async Task Refuse(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException) { return; }
        throw new InvalidOperationException("Expected targeted collection refusal.");
    }
}
