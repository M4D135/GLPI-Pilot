using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

/// <summary>Stdio fixture for real page-to-handler tests. Network and installer launch are forbidden.</summary>
internal static class TerrainWebFixture
{
    public static async Task RunAsync(string root)
    {
        const string cityA = "11111111111111111111111111111111", cityB = "22222222222222222222222222222222", site = "33333333333333333333333333333333";
        var settings = new AppSettings { Cities = new() { Cities = new() {
            new() { Id = cityA, Name = "Alpha", Latitude = 50, Longitude = 2 }, new() { Id = cityB, Name = "Beta", Latitude = 49, Longitude = 3 } },
            Sites = new() { new() { Id = site, CityId = cityA, Name = "Campus Nord", Latitude = 50.1, Longitude = 2.1, Contact = "Accueil", Equipment = "Adaptateur" } } } };
        var source = new ExtractionResult { Title = "Relevé synthétique terrain", SourceUrl = "https://fixture.invalid/front/ticket.php", Complete = true, ExtractedAt = DateTimeOffset.UtcNow,
            Headers = new() { "ID", "Titre", "Statut", "Priorité", "Attribué à - Technicien", "Temps de résolution" },
            Rows = new() {
                new[] { "1", "Campus Nord · réseau", "Nouveau", "Haute", "Alice", "07/01/2030 17:00" },
                new[] { "2", "Beta · imprimante", "En cours (attribué)", "Moyenne", "Alice", "07/01/2030 17:00" },
                new[] { "3", "Alpha ou Beta · lieu à vérifier", "Nouveau", "Basse", "Bob", "" }
            } };
        var preferences = new TechnicianPreferences().With("Alice", new TechnicianPreference { DailyCapacityMinutes = 450, Skills = new() { "Réseau", "Impression" } });
        preferences.Save(Path.Combine(root, "technician-preferences.json"));
        var reference = Path.Combine(root, "reference.csv"); ExportService.WriteCsv(CityReferenceService.Export(settings.Cities), reference);
        var host = new TerrainTestHost { OpenPath = reference, SavePath = Path.Combine(root, "export.csv") };
        void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, PilotApplication.Json));
        await using var app = new PilotApplication(host, (name, data) => Write(new { eventName = name, data }), settings);
        app.LoadFixture(source);
        Write(new { ready = true, revision = app.Revision });
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            using var document = JsonDocument.Parse(line); var request = document.RootElement; var id = request.GetProperty("id").GetInt32();
            try
            {
                var operation = request.GetProperty("op").GetString()!;
                if (!(operation.StartsWith("terrain.", StringComparison.Ordinal) || operation.StartsWith("update.", StringComparison.Ordinal)
                    || operation.StartsWith("maps.", StringComparison.Ordinal))
                    || operation is "terrain.tours.route" or "terrain.tours.weather" or "update.findOfficial" or "update.downloadOfficial" or "update.install")
                    throw new InvalidOperationException("Network or installer execution is prohibited in this offline fixture.");
                var result = await app.ExecuteAsync(operation, request.GetProperty("args")); Write(new { id, result });
            }
            catch (Exception error) { Write(new { id, error = new { message = error.Message, code = "FIXTURE_ERROR" } }); }
        }
    }
}
