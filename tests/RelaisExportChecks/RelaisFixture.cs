using System.Globalization;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

internal static class RelaisFixture
{
    internal const string Installation = "https://relais-fixture.example/glpi";
    internal const string LargeId = "9007199254740993";
    internal static DateTimeOffset At => new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    internal static TicketLibrarySeed Seed(string id = LargeId, string installation = Installation) => new()
    {
        Ticket = new(id, installation + "/front/ticket.form.php?id=" + id), Title = "Incident synthétique « réseau » " + id,
        Status = "Résolu", StatusCode = 5, Category = "Réseau & impression",
        LastModifiedRaw = "2026-11-20T09:00:00Z", LastModifiedAt = At, LastModifiedReliable = true, LastModifiedPrecision = "seconds", ObservedAt = At.AddMinutes(1),
        Fields = new() { ["clé/avec~séparateur"] = "Valeur exacte : été <balise> & Ω 😀", ["vide"] = "" },
        IndexHeaders = ["ID", "Priorité", "Libellé répété", "Libellé répété"], IndexValues = [id, "Haute", "Première", ""]
    };
    internal static TicketLibraryCoverage Coverage(bool complete = true) => new()
    { Complete = complete, TabsDiscovered = complete ? 1 : 2, TabsLoaded = 1, Limitations = ["Documents référencés sans contenu binaire."] };
    internal static TicketDetailResult Detail(TicketLibrarySeed seed, string solution = "Solution synthétique intacte <b>privée</b> : redémarrage contrôlé 😀") => new()
    {
        Ticket = seed.Ticket, Title = "Fiche détaillée synthétique", ReadAt = At.AddMinutes(2),
        Tabs = [new() { Key = "treatment", Name = "Traitement", Loaded = true }],
        Sections = [
            new() { Name = "Chronologie", Kind = "timeline", TabKey = "treatment", Data = new()
            {
                Headers = ["Type", "Date", "Auteur", "Contenu", "Informations"],
                Rows = [["Solution", "2026-11-20", "Auteur fictif", solution, "Acceptée\nPrivé"], ["Suivi", "2026-11-20", "Technicien fictif", "Contrôle sans marque privée", ""]],
                SourceUrl = seed.Ticket.Url, Complete = true, ExtractedAt = At.AddMinutes(2)
            } },
            new() { Name = "Valeurs absentes et vides", TabKey = "fields", Data = new()
                { Headers = ["Champ", "Valeur"], Rows = [[""], ["Texte", "", "Sans en-tête"]], Complete = true, ExtractedAt = At.AddMinutes(2) } }
        ],
        ObservedItems = [new() { Kind = "followup", Identity = "synthetic-followup", SourceTabKey = "treatment", SourceSection = "Chronologie",
            Title = "Élément observé synthétique", Author = "Auteur observation", Owner = "Équipe fictive", Date = "2026-11-20", Url = seed.Ticket.Url, ObservedAt = At.AddMinutes(2) }]
    };

    internal static async Task<TicketLibraryStore> Ready(string path, string id = LargeId)
    {
        var store = new TicketLibraryStore(path); var seed = Seed(id);
        await store.SaveDetailAsync(seed, Detail(seed), Coverage(), "relais-synthetic-reader"); return store;
    }

    internal static async Task<TicketLibraryStore> Mixed(string path)
    {
        var store = await Ready(path);
        var second = Seed("2"); await store.SaveDetailAsync(second, Detail(second, "Deuxième solution synthétique"), Coverage(), "relais-synthetic-reader");
        await store.ObserveSeedAsync(Seed("3"));
        var partial = Seed("4"); await store.SaveDetailAsync(partial, Detail(partial), Coverage(false), "relais-synthetic-reader");
        var pending = Seed("5");
        var context = new TicketLibraryCaptureContext(Installation, new string('a', 64), TicketLibraryAcquisition.ReaderSignature);
        var run = await store.BeginCaptureAsync(context, "all");
        await store.SetCaptureIndexAsync(run, new() { SourceUrl = Installation + "/front/ticket.php", ScopeFingerprint = "relais-synthetic-scope", AllStatuses = true, Complete = true, Tickets = [pending] });
        var observed = await store.ObserveCaptureSeedAsync(run, pending);
        await store.SaveCaptureDetailAsync(run, pending, new() { Detail = Detail(pending), Coverage = Coverage() }, observed.Document.LocalRevision);
        await store.FinishCaptureAsync(run, "cancelled", []);
        var stale = Seed("6"); await store.SaveDetailAsync(stale, Detail(stale), Coverage(), "relais-synthetic-reader");
        stale.LastModifiedRaw = "2026-11-20T10:00:00Z"; stale.LastModifiedAt = At.AddHours(1); await store.ObserveSeedAsync(stale);
        var conflict = Seed("7"); await store.SaveDetailAsync(conflict, Detail(conflict, "Alternative A synthétique"), Coverage(), "relais-synthetic-reader");
        await store.SaveDetailAsync(conflict, Detail(conflict, "Alternative B synthétique"), Coverage(), "relais-synthetic-reader");
        var other = Seed(LargeId, "https://other-relais-fixture.example/glpi");
        await store.SaveDetailAsync(other, Detail(other, "Autre installation, texte exclu"), Coverage(), "relais-synthetic-reader");
        using var connection = new SqliteConnection(store.ConnectionString); connection.Open();
        using var revisions = connection.CreateCommand();
        revisions.CommandText = "UPDATE ticket_library_tickets SET local_revision=$local,central_revision=$central WHERE ticket_key=$key";
        revisions.Parameters.AddWithValue("$local", 9_007_199_254_740_993L); revisions.Parameters.AddWithValue("$central", long.MaxValue);
        revisions.Parameters.AddWithValue("$key", TicketLibraryRules.Key(Seed().Ticket)); revisions.ExecuteNonQuery();
        return store;
    }
}

internal sealed class RelaisInlineProgress(Action<RelaisExportProgress> report) : IProgress<RelaisExportProgress>
{ public void Report(RelaisExportProgress value) => report(value); }
