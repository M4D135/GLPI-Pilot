using System.Text.Json;
using GlpiPilot.Core;

internal static class ColumnPreparationChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(string temporary)
    {
        yield return ("Colonnes GLPI : défaut supervision, copie et anciens profils indépendants", Defaults);
        yield return ("Colonnes GLPI : partage du mode global et par recherche, valeurs strictes", () => Portable(temporary));
        yield return ("Colonnes GLPI : supervision ajoute les rôles manquants avec leur namespace", Supervision);
        yield return ("Colonnes GLPI : toutes les options offertes, sans doublon ni plafond caché", All);
        yield return ("Colonnes GLPI : ID demandeur, mappages explicites et ambiguïtés préservés", Conservative);
        yield return ("Colonnes GLPI : ajouts conservent les colonnes initiales dans leur ordre", HeaderOrder);
        yield return ("Colonnes GLPI : échantillon stable ou source mouvante distingués des critères", Samples);
        yield return ("Colonnes GLPI : repli explicite et source mouvante sans fausse certification", Receipt);
    }

    private static void Check(bool value, string message = "Assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static void Refuse(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Une valeur inconnue aurait dû être refusée.");
    }
    private static NativeColumnOption Option(string value, string label, string group = "", bool disabled = false) =>
        new() { Value = value, Label = label, Group = group, Disabled = disabled };
    private static void Defaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check(settings.ColumnPreparationMode == "supervision");
        settings.ColumnPreparationMode = "all"; Check(settings.Copy().ColumnPreparationMode == "all");
        var saved = new SourceProfile { Name = "Liste synthétique", Url = "https://glpi.example.test/front/ticket.php" };
        settings.PortalUrl = saved.Url; saved.Capture(settings);
        Check(saved.Acquisition["ColumnPreparationMode"] == "all");
        settings.ColumnPreparationMode = "current"; saved.Copy().ApplyTo(settings); Check(settings.ColumnPreparationMode == "all");
        saved.Acquisition.Remove("ColumnPreparationMode"); saved.ApplyTo(settings);
        Check(settings.ColumnPreparationMode == "supervision", "Un ancien profil ne doit pas hériter du mode toutes d’une autre source.");
    }
    private static void Portable(string temporary)
    {
        var source = new AppSettings { PortalUrl = "https://glpi.example.test/front/ticket.php", ColumnPreparationMode = "all" };
        var profile = new SourceProfile { Name = "Liste synthétique" }; profile.Capture(source); source.Sources.Add(profile);
        var path = Path.Combine(temporary, "columns-portable.json");
        ConfigurationTransferService.Export(path, source);
        var imported = ConfigurationTransferService.Import(path, new());
        Check(imported.ColumnPreparationMode == "all" && imported.Sources.Single().Acquisition["ColumnPreparationMode"] == "all");
        Check(ConfigurationReview.Apply(new() { ColumnPreparationMode = "current" }, imported, ConfigurationParts.Extraction, false).ColumnPreparationMode == "all");
        var json = File.ReadAllText(path);
        File.WriteAllText(path, json.Replace("\"ColumnPreparationMode\": \"all\"", "\"ColumnPreparationMode\": \"global\"", StringComparison.Ordinal));
        Refuse(() => ConfigurationTransferService.Import(path, new()));
        source.ColumnPreparationMode = "current"; profile.Acquisition["ColumnPreparationMode"] = "script";
        Refuse(() => ConfigurationTransferService.Export(path, source));
    }
    private static void Supervision()
    {
        var preferences = new NativeColumnPreferences { Options = new()
        {
            Option("1", "ID", "Demandeur"), Option("8", "Description", "Caractéristiques"),
            Option("12", "Statut", "Caractéristiques"), Option("8", "Groupe de techniciens", "Attribué à"),
            Option("66", "Rédacteur", "Suivis"), Option("95", "Rédacteur", "Tâches")
        } };
        // Values are unique in a proven native list; use independent synthetic IDs.
        preferences.Options[1].Value = "21";
        var added = new List<NativeColumnOption>();
        while (ColumnPreparationPolicy.Next("supervision", new[] { "ID", "Titre" }, preferences, new(), added) is { } next) added.Add(next);
        Check(added.Select(option => option.Value).SequenceEqual(new[] { "12", "8", "66", "95" }));
        Check(added.All(option => option.Group != "Demandeur" && option.Label != "Description"));
        Check(ColumnPreparationPolicy.Next("current", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>()) is null);
    }
    private static void All()
    {
        var preferences = new NativeColumnPreferences
        {
            SelectedIdsKnown = true, Selected = new() { new() { Value = "2", Label = "Titre" } },
            Options = Enumerable.Range(1, 300).Select(number => Option(number.ToString(), "Champ natif " + number)).ToList()
        };
        preferences.Options[0].Disabled = true;
        var added = new List<NativeColumnOption>();
        while (ColumnPreparationPolicy.Next("all", new[] { "Titre" }, preferences, new(), added) is { } next) added.Add(next);
        Check(added.Count == 298 && added[0].Value == "3" && added[^1].Value == "300");
    }
    private static void Conservative()
    {
        var preferences = new NativeColumnPreferences { Options = new() { Option("71", "ID", "Demandeur") } };
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>()) is null);
        preferences.Options = new() { Option("12", "Statut", "Caractéristiques"), Option("120", "Status", "Characteristics") };
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>()) is null);
        preferences.Options.RemoveAt(1);
        var mapping = new DashboardSettings(); mapping.ColumnMappings["status"] = "État interne exact";
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, mapping, Array.Empty<NativeColumnOption>()) is null);
        mapping.ColumnMappings["status"] = "Status";
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, mapping, Array.Empty<NativeColumnOption>())?.Value == "12");
    }
    private static void HeaderOrder()
    {
        Check(ColumnPreparationPolicy.PreservesHeaders(new[] { "ID", "Titre", "Statut" }, new[] { " ID ", "Titre", "Statut", "Priorité" }));
        Check(!ColumnPreparationPolicy.PreservesHeaders(new[] { "ID", "Titre", "Statut" }, new[] { "ID", "Statut", "Titre", "Priorité" }));
        Check(!ColumnPreparationPolicy.PreservesHeaders(new[] { "ID", "Titre", "Statut" }, new[] { "ID", "Titre", "Priorité" }));
    }
    private static void Samples()
    {
        var before = new TableSnapshot { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "0007", "Exemple" } }, ExpectedTotal = 1 };
        var after = new TableSnapshot { Headers = new() { "ID", "Titre", "Statut" }, Rows = new() { new[] { "7", "Exemple", "Nouveau" } }, ExpectedTotal = 1 };
        Check(!ColumnPreparationPolicy.SourceChanged(before, after, new()));
        after.Rows[0][0] = "8"; Check(ColumnPreparationPolicy.SourceChanged(before, after, new()));
        after.Rows[0][0] = "7"; after.ExpectedTotal = 2; Check(ColumnPreparationPolicy.SourceChanged(before, after, new()));
    }
    private static void Receipt()
    {
        var data = new ExtractionResult { Complete = true, Status = "Collecte terminée" };
        var receipt = new ColumnPreparationReceipt { State = "unavailable", Message = "Commande absente", Warnings = new() { "Commande absente" } };
        ColumnPreparationPolicy.ApplyReceipt(data, receipt);
        Check(data.Complete && data.Warnings.SequenceEqual(new[] { "Commande absente" }));
        receipt.SourceChanged = true; ColumnPreparationPolicy.ApplyReceipt(data, receipt);
        Check(!data.Complete && data.Status.Contains("liste modifiée", StringComparison.Ordinal) && data.Warnings.Count == 1);
    }
}
