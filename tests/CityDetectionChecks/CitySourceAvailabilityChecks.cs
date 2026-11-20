using GlpiPilot.Core;

internal static class CitySourceAvailabilityChecks
{
    public static void Register(Action<string, Action> test)
    {
        test("City diagnostics distinguish missing, ambiguous and empty sources without private values", SourceDiagnostics);
        test("Location source must be configured explicitly and diagnostics explain missing context", ExplicitLocation);
        test("Thirteen city references recognize normalized variants without default assignment", ThirteenReferences);
    }

    private static void Require(bool value)
    { if (!value) throw new InvalidOperationException("City source observation mismatch."); }

    private static void SourceDiagnostics()
    {
        const string secret = "private-fixture-value";
        var data = new ExtractionResult
        {
            Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "91", secret }, new[] { "92", "" } }
        };
        var settings = new CitySettings { Cities = new() { new() { Name = "Ville fictive", Aliases = new() { "VF" } } } };
        var log = string.Join('\n', CityDetectionService.DescribeSourceAvailability(data, settings, new()));
        Require(log.Contains("références=1 ; variantes=1", StringComparison.Ordinal)
            && log.Contains("rôle=titre ; source=utilisée ; colonne reconnue ; valeurs présentes=1/2", StringComparison.Ordinal)
            && !log.Contains(secret, StringComparison.Ordinal) && !log.Contains("Ville fictive", StringComparison.Ordinal));
        data.Headers.Add("Sujet");
        log = string.Join('\n', CityDetectionService.DescribeSourceAvailability(data, settings, new()));
        Require(log.Contains("rôle=titre ; source=utilisée ; colonne ambiguë", StringComparison.Ordinal));
        settings.TitleColumn = "En-tête privé absent";
        log = string.Join('\n', CityDetectionService.DescribeSourceAvailability(data, settings, new()));
        Require(log.Contains("rôle=titre ; source=utilisée ; colonne absente", StringComparison.Ordinal)
            && !log.Contains(settings.TitleColumn, StringComparison.Ordinal));
        settings.Enabled = false;
        Require(CityDetectionService.DescribeSourceAvailability(data, settings, new())[0].Contains("désactivé", StringComparison.Ordinal));
    }

    private static void ExplicitLocation()
    {
        var data = new ExtractionResult { Headers = new() { "ID", "Titre", "Lieu" }, Rows = new() { new[] { "1", "Aucun lieu dans le titre", "Lyon" } } };
        var city = new CityDefinition { Name = "Lyon", Rules = new() { new() { Term = "LY", ContextColumn = "Contexte privé", RequiredTerms = new() { "Agence" } } } };
        var settings = new CitySettings { Cities = new() { city }, SourcePriority = new() { "location" } };
        var log = string.Join('\n', CityDetectionService.DescribeSourceAvailability(data, settings, new()));
        Require(log.Contains("rôle=lieu ; source=utilisée ; colonne non configurée", StringComparison.Ordinal)
            && log.Contains("colonnes de contexte indisponibles=1/1", StringComparison.Ordinal)
            && !log.Contains("Contexte privé", StringComparison.Ordinal));
        Require(CityDetectionService.Enrich(data, settings, new()).MatchedCount == 0);
        settings.LocationColumn = "Lieu";
        Require(CityDetectionService.Enrich(data, settings, new()).MatchedCount == 1);
        data.Headers.Add("Lieu");
        log = string.Join('\n', CityDetectionService.DescribeSourceAvailability(data, settings, new()));
        Require(log.Contains("rôle=lieu ; source=utilisée ; colonne ambiguë", StringComparison.Ordinal)
            && CityDetectionService.Enrich(data, settings, new()).MatchedCount == 0);
    }

    private static void ThirteenReferences()
    {
        var names = new[] { "Saint-Étienne", "Évry-Courcouronnes", "L'Haÿ-les-Roses", "Aix-en-Provence", "Châlons-en-Champagne",
            "Besançon", "Orléans", "Nîmes", "Béziers", "Périgueux", "Sète", "Alençon", "Mâcon" };
        var settings = new CitySettings { Cities = names.Select((name, index) => new CityDefinition { Name = name, Aliases = new() { "REF" + (index + 1) } }).ToList() };
        var titles = new[] { "saint etienne", "EVRY COURCOURONNES", "l hay les roses", "AIX-EN-PROVENCE", "chalons en champagne",
            "BESANCON", "orleans", "NIMES", "beziers", "PERIGUEUX", "sete", "ALENCON", "macon" };
        var data = new ExtractionResult { Headers = new() { "ID", "Titre" } };
        data.Rows = titles.Select((title, index) => new[] { (index + 1).ToString(), "Incident à " + title }).ToList();
        data.Rows.Add(new[] { "14", "Lieu inconnu" }); data.Rows.Add(new[] { "15", "" });
        data.Rows.Add(new[] { "16", "REF1 et REF2" }); data.Rows.Add(new[] { "17", "PréfixeREF3suffixe" });
        var result = CityDetectionService.Enrich(data, settings, new());
        Require(result.MatchedCount == 13 && result.UnmatchedCount == 2 && result.TitleUnavailableCount == 1 && result.AmbiguousCount == 1);
        Require(result.Matches.Take(13).Select(match => match.CityName).SequenceEqual(names));
        Require(result.Matches.Skip(13).All(match => match.CityId is null));
        foreach (var (city, index) in settings.Cities.Select((city, index) => (city, index)))
            Require(CityDetectionService.ExplainTitle("Incident REF" + (index + 1), settings).CityId == city.Id);
    }
}
