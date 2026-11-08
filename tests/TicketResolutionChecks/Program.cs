using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-ResolutionChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0; var total = 0;
try
{
    await Check("Shared title terms lead the ranking and category-only or one-word automatic suggestions are refused", async () =>
    {
        var store = Store("ranking"); var source = Seed("1", "Erreur imprimante laser", 2); source.Category = "Imprimante laser";
        await store.ObserveSeedAsync(source);
        var best = Seed("2", "Imprimante laser", 5); best.Category = source.Category; await Save(store, best);
        var second = Seed("3", "Imprimante laser", 5); second.Category = "Autre"; await Save(store, second);
        var category = Seed("4", "Écran noir", 5); category.Category = source.Category; await Save(store, category);
        var single = Seed("5", "Imprimante bourrage", 5); single.Category = source.Category; await Save(store, single);
        var result = await Find(store, source);
        Require(result.Matches.Select(row => row.Ticket.Id).SequenceEqual(["2", "3"]));
        Require(result.Matches[0].SameCategory && result.Matches[0].TitleTerms.Count == 2 && !result.Matches[1].SameCategory);
    });
    await Check("Resolved complete confirmed predecessors stay in the source installation and exclude the source ticket", async () =>
    {
        var store = Store("eligibility"); var source = Seed("1", "Connexion VPN bloquée", 5); await Save(store, source);
        await Save(store, Seed("2", "Connexion VPN bloquée", 6));
        var otherHost = Seed("3", "Connexion VPN bloquée", 5); otherHost.Ticket = new("3", "https://other.example/glpi/front/ticket.form.php?id=3"); await Save(store, otherHost);
        var otherPath = Seed("4", "Connexion VPN bloquée", 5); otherPath.Ticket = new("4", "https://resolution.example/other/front/ticket.form.php?id=4"); await Save(store, otherPath);
        await Save(store, Seed("5", "Connexion VPN bloquée", 2));
        var partial = Seed("6", "Connexion VPN bloquée", 5); await store.SaveDetailAsync(partial, Detail(partial), new() { Complete = false }, "fixture-v1");
        var stale = Seed("7", "Connexion VPN bloquée", 5); await Save(store, stale); stale.LastModifiedRaw = "2026-10-20 10:00:00"; stale.LastModifiedAt = At(10); await store.ObserveSeedAsync(stale);
        var legacy = Seed("8", "Connexion VPN bloquée", 5); await Save(store, legacy);
        using (var connection = new SqliteConnection(store.ConnectionString))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ticket_library_tickets SET document_json=json_remove(document_json,'$.confirmationId','$.confirmedContentHash','$.confirmationPending') WHERE ticket_key=$key";
            command.Parameters.AddWithValue("$key", TicketLibraryRules.Key(legacy.Ticket)); await command.ExecuteNonQueryAsync();
        }
        var result = await Find(store, source);
        Require(result.Matches.Select(row => row.Ticket.Id).SequenceEqual(["2"]));
    });
    await Check("Distinctive alphanumeric error codes survive token selection without becoming ticket identifiers", async () =>
    {
        var store = Store("codes"); var source = Seed("1", "Erreur E-1234", 2); await store.ObserveSeedAsync(source);
        await Save(store, Seed("2", "Résoudre E-1234", 5)); await Save(store, Seed("1234", "Erreur E-9999", 5));
        var result = await Find(store, source);
        Require(result.Available && result.Terms.Contains("E-1234") && result.Matches.Select(row => row.Ticket.Id).SequenceEqual(["2"]));
        var hex = Seed("9", "Erreur 0x80070005", 2); await store.ObserveSeedAsync(hex);
        await Save(store, Seed("10", "0x80070005", 5));
        Require((await Find(store, hex)).Matches.Single().Ticket.Id == "10");
    });
    await Check("A stale source uses only its conserved index title and cannot reuse old detail symptoms", async () =>
    {
        var store = Store("stale-source"); var source = Seed("1", "Imprimante laser", 2); await Save(store, source, "Imprimante laser ancienne");
        source.Title = "Connexion VPN bloquée"; source.LastModifiedRaw = "2026-10-20 10:00:00"; source.LastModifiedAt = At(10);
        await store.ObserveSeedAsync(source);
        await Save(store, Seed("2", "Connexion VPN bloquée", 5)); await Save(store, Seed("3", "Imprimante laser", 5));
        var result = await Find(store, source);
        Require(!result.SourceContentConfirmed && result.SourceTitle == source.Title && result.Matches.Select(row => row.Ticket.Id).SequenceEqual(["2"]));
    });
    await Check("An insufficient title explains the limitation while an explicit one-term search remains local and useful", async () =>
    {
        var store = Store("manual"); var source = Seed("1", "Demande de ticket", 2); await store.ObserveSeedAsync(source);
        await Save(store, Seed("2", "Imprimante réseau", 5));
        var automatic = await Find(store, source); Require(!automatic.Available && automatic.CandidatesExamined == 0 && automatic.Message.Length > 0);
        var manual = await TicketLibraryResolutionService.FindAsync(store, TicketLibraryRules.Key(source.Ticket), "imprimante");
        Require(manual.Available && manual.Manual && manual.Matches.Single().Ticket.Id == "2");
        Require(!(await TicketLibraryResolutionService.FindAsync(store, TicketLibraryRules.Key(source.Ticket), "le et pour")).Available);
    });
    await Check("Solution citations retain their exact text, C1, hash, section and original validated URL", async () =>
    {
        var store = Store("evidence"); var source = Seed("1", "Imprimante laser", 2); await store.ObserveSeedAsync(source);
        var candidate = Seed("2", "Imprimante laser", 5); const string original = "<b>Réparer</b>\u0092\nTexte exactement recueilli.";
        var saved = await Save(store, candidate, original);
        var match = (await Find(store, source)).Matches.Single(); var citation = match.Citations.Single(item => item.IsSolution);
        Require(citation.Excerpt == original && citation.Current && citation.ContentHash == saved.Document.ContentHash
            && citation.SectionKey == "timeline" && citation.Url == candidate.Ticket.Url && citation.Ticket == candidate.Ticket);
    });
    await Check("Evidence within collected content can justify a match and is distinguished from title overlap", async () =>
    {
        var store = Store("content"); var source = Seed("1", "Imprimante laser", 2); await store.ObserveSeedAsync(source);
        await Save(store, Seed("2", "Intervention technique", 5), "Réinitialiser l’imprimante laser puis relancer.");
        var result = await Find(store, source); var match = result.Matches.Single();
        Require(match.TitleTerms.Count == 0 && match.ContentTerms.Count == 2 && match.Citations.Any(item => item.Excerpt.Contains("imprimante laser")));
    });
    await Check("Large result sets are explicitly bounded and the resolution read cannot mutate tickets or outbox", async () =>
    {
        var store = Store("bounds"); var source = Seed("1", "Imprimante laser", 2); await store.ObserveSeedAsync(source);
        for (var index = 100; index < 205; index++) await Save(store, Seed(index.ToString(), "Imprimante laser", 5), new string('x', 7000));
        var before = await store.GetStateAsync(); var result = await Find(store, source); var after = await store.GetStateAsync();
        Require(result.CandidatesFound == 105 && result.CandidatesExamined == 100 && result.DocumentsRechecked <= 16
            && result.Matches.Count == 8 && result.Limited && result.Matches.All(row => row.Citations.Count <= 4 && row.Citations.All(citation => citation.Excerpt.Length <= 3000))
            && before.TicketCount == after.TicketCount && before.PendingCount == after.PendingCount && before.ConflictCount == after.ConflictCount);
    });
    await Check("Cancellation and oversized manual queries stop before any search output is produced", async () =>
    {
        var store = Store("cancel"); var source = Seed("1", "Imprimante laser", 2); await store.ObserveSeedAsync(source);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await TicketLibraryResolutionService.FindAsync(store, TicketLibraryRules.Key(source.Ticket), ct: cancel.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        try { await TicketLibraryResolutionService.FindAsync(store, TicketLibraryRules.Key(source.Ticket), new string('a', 301)); throw new Exception("Bound ignored."); }
        catch (InvalidDataException) { }
    });
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"{passed}/{total} ticket resolution checks passed.");
Environment.ExitCode = passed == total ? 0 : 1;

async Task Check(string name, Func<Task> action)
{ total++; try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); } }
static void Require(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
TicketLibraryStore Store(string name) => new(Path.Combine(root, name + ".db"));
static DateTimeOffset At(int hour) => new(2026, 10, 20, hour, 0, 0, TimeSpan.Zero);
static TicketLibrarySeed Seed(string id, string title, int status) => new()
{
    Ticket = new(id, "https://resolution.example/glpi/front/ticket.form.php?id=" + id), Title = title, StatusCode = status,
    Status = status == 5 ? "Résolu" : status == 6 ? "Clos" : "En cours", Category = "Technique",
    LastModifiedRaw = "2026-10-20 09:00:00", LastModifiedAt = At(9), LastModifiedReliable = true, LastModifiedPrecision = "seconds", ObservedAt = At(12)
};
static TicketDetailResult Detail(TicketLibrarySeed seed, string solution = "Intervention effectuée") => new()
{
    Ticket = seed.Ticket, Title = seed.Title, ReadAt = At(12), Tabs = [new() { Key = "timeline", Name = "Traitement", Loaded = true }],
    Sections = [new() { Name = "Traitement recueilli", Kind = "timeline", TabKey = "timeline", Data = new()
    { Headers = ["Type", "Date", "Auteur", "Contenu"], Rows = [new[] { "Solution", "2026-10-20 09:00", "Technicien synthétique", solution }], Complete = true } }]
};
static Task<TicketLibraryWriteResult> Save(TicketLibraryStore store, TicketLibrarySeed seed, string solution = "Intervention effectuée")
    => store.SaveDetailAsync(seed, Detail(seed, solution), new() { Complete = true, TabsLoaded = 1, TabsDiscovered = 1 }, "fixture-v1");
static Task<TicketLibraryResolutionResult> Find(TicketLibraryStore store, TicketLibrarySeed seed)
    => TicketLibraryResolutionService.FindAsync(store, TicketLibraryRules.Key(seed.Ticket));
