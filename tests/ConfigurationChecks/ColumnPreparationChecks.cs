using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

internal static class ColumnPreparationChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(string temporary)
    {
        yield return ("Colonnes GLPI : défaut utile, migration unique et choix enregistrés préservés", () => Defaults(temporary));
        yield return ("Colonnes GLPI : partage du mode global et par recherche, valeurs strictes", () => Portable(temporary));
        yield return ("Colonnes GLPI : supervision ajoute les rôles manquants avec leur namespace", Supervision);
        yield return ("Colonnes GLPI : tous les rôles opérationnels, sans textes lourds ni champs arbitraires", UsefulRoles);
        yield return ("Colonnes GLPI : textes ajoutés seulement par correspondance ou usage configuré", ConfiguredText);
        yield return ("Colonnes GLPI : rôles des requêtes, opérandes et calculs explicitement utilisés", QueryText);
        yield return ("Colonnes GLPI : colonnes exactes des regroupements, filtres et agrégats", QueryExactColumns);
        yield return ("Colonnes GLPI : dépendances exactes des calculs référencés, hors littéraux et calculs inactifs", QueryExactComputations);
        yield return ("Colonnes GLPI : en-tête exact unique, sans substitution ni ajout répété", QueryExactIdentity);
        yield return ("Colonnes GLPI : reçu des colonnes exactes absentes ou ambiguës", QueryExactReceipt);
        yield return ("Colonnes GLPI : toutes les options offertes, sans doublon ni plafond caché", All);
        yield return ("Colonnes GLPI : ID demandeur, mappages explicites et ambiguïtés préservés", Conservative);
        yield return ("Colonnes GLPI : ajouts conservent les colonnes initiales dans leur ordre", HeaderOrder);
        yield return ("Colonnes GLPI : échantillon stable ou source mouvante distingués des critères", Samples);
        yield return ("Colonnes GLPI : repli explicite et source mouvante sans fausse certification", Receipt);
        yield return ("Colonnes GLPI : nouvel échantillon natif entièrement vérifié après préparation", RevalidatedReceipt);
        yield return ("Colonnes GLPI : preuve partielle ou divergente ne revalide jamais la source", IncompleteRevalidation);
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
    private static void Defaults(string temporary)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check(settings.ColumnPreparationMode == "supervision");
        foreach (var json in new[] { "{}", "{\"ColumnPreparationMode\":null}", "{\"ColumnPreparationMode\":\"\"}", "{\"ColumnPreparationMode\":\"  \"}" })
            Check(SettingsRecovery.Read(Encoding.UTF8.GetBytes(json)).ColumnPreparationMode == "supervision");
        settings.ColumnPreparationMode = "all"; Check(settings.Copy().ColumnPreparationMode == "all");
        var saved = new SourceProfile { Name = "Liste synthétique", Url = "https://glpi.example.test/front/ticket.php" };
        settings.PortalUrl = saved.Url; saved.Capture(settings);
        Check(saved.Acquisition["ColumnPreparationMode"] == "all");
        settings.ColumnPreparationMode = "current"; saved.Copy().ApplyTo(settings); Check(settings.ColumnPreparationMode == "all");
        saved.Acquisition.Remove("ColumnPreparationMode"); settings.ColumnPreparationMode = "current"; saved.ApplyTo(settings);
        Check(settings.ColumnPreparationMode == "supervision", "Une recherche sans mode enregistré doit utiliser le nouveau défaut, sans hériter du mode d’une autre source.");

        saved.Acquisition["ColumnPreparationMode"] = "all";
        var legacy = JsonSerializer.Serialize(new { ColumnPreparationMode = "all", ColumnPreparationDefaultsVersion = 1, PortalUrl = saved.Url, ActiveSourceId = saved.Id, Sources = new[] { saved } });
        var path = Path.Combine(temporary, "columns-local-settings.json"); File.WriteAllText(path, legacy);
        var upgraded = SettingsRecovery.Load(path);
        Check(upgraded.ColumnPreparationMode == "supervision"
            && upgraded.ColumnPreparationDefaultsVersion == AppSettings.CurrentColumnPreparationDefaultsVersion
            && upgraded.Sources.Single().Acquisition["ColumnPreparationMode"] == "all"
            && File.ReadAllText(path) == legacy,
            "La migration locale doit changer le défaut courant une seule fois, sans modifier les recherches ni écrire pendant la lecture.");
        SettingsRecovery.Save(path, upgraded);
        using var persisted = JsonDocument.Parse(File.ReadAllText(path));
        Check(persisted.RootElement.GetProperty("ColumnPreparationDefaultsVersion").GetInt32() == AppSettings.CurrentColumnPreparationDefaultsVersion);
        upgraded.ColumnPreparationMode = "all"; upgraded.Normalize(); SettingsRecovery.Save(path, upgraded);
        Check(SettingsRecovery.Load(path).ColumnPreparationMode == "all" && upgraded.Copy().ColumnPreparationMode == "all",
            "Un choix explicite de toutes les colonnes après la mise à jour doit survivre à la copie, l’enregistrement et la réouverture.");
        upgraded.ColumnPreparationMode = "supervision"; upgraded.Sources.Single().ApplyTo(upgraded);
        Check(upgraded.ColumnPreparationMode == "all", "Une recette enregistrée doit conserver son choix explicite de toutes les colonnes.");
        foreach (var mode in new[] { "current", "supervision" })
            foreach (var marker in new[] { "", ",\"ColumnPreparationDefaultsVersion\":0", ",\"ColumnPreparationDefaultsVersion\":1" })
            {
                File.WriteAllText(path, "{\"ColumnPreparationMode\":\"" + mode + "\"" + marker + "}");
                Check(SettingsRecovery.Load(path).ColumnPreparationMode == mode, "Les choix vue actuelle et colonnes utiles doivent rester inchangés.");
            }
        foreach (var marker in new[] { "", ",\"ColumnPreparationDefaultsVersion\":0", ",\"ColumnPreparationDefaultsVersion\":1" })
        {
            File.WriteAllText(path, "{\"ColumnPreparationMode\":\"all\"" + marker + "}");
            Check(SettingsRecovery.Load(path).ColumnPreparationMode == "supervision");
        }
        File.WriteAllText(path, "{\"ColumnPreparationMode\":\"all\",\"ColumnPreparationDefaultsVersion\":3}");
        var future = SettingsRecovery.Load(path);
        Check(future.ColumnPreparationMode == "all" && future.ColumnPreparationDefaultsVersion == 3,
            "Une version plus récente du reçu local doit rester intacte.");

        File.WriteAllText(path, legacy);
        var restored = SettingsRecovery.Read(path, applyColumnPreparationUpgrade: false);
        Check(restored.ColumnPreparationMode == "all" && restored.ColumnPreparationDefaultsVersion == AppSettings.CurrentColumnPreparationDefaultsVersion);
        SettingsRecovery.Save(path, restored);
        Check(SettingsRecovery.Load(path).ColumnPreparationMode == "all",
            "Une restauration explicite d’une ancienne configuration doit conserver son choix, y compris après le prochain démarrage.");
    }
    private static void Portable(string temporary)
    {
        var source = new AppSettings { PortalUrl = "https://glpi.example.test/front/ticket.php", ColumnPreparationMode = "all" };
        var profile = new SourceProfile { Name = "Liste synthétique" }; profile.Capture(source); source.Sources.Add(profile);
        var path = Path.Combine(temporary, "columns-portable.json");
        ConfigurationTransferService.Export(path, source);
        var imported = ConfigurationTransferService.Import(path, new());
        Check(imported.ColumnPreparationMode == "all" && imported.Sources.Single().Acquisition["ColumnPreparationMode"] == "all"
            && SettingsRecovery.Read(JsonSerializer.SerializeToUtf8Bytes(imported)).ColumnPreparationMode == "all");
        Check(ConfigurationReview.Apply(new() { ColumnPreparationMode = "current" }, imported, ConfigurationParts.Extraction, false).ColumnPreparationMode == "all");
        Check(ConfigurationReview.Describe(new(), imported).Single(item => item.Part == ConfigurationParts.Extraction).Current.Contains("utiles à ma configuration", StringComparison.Ordinal));
        var json = File.ReadAllText(path);
        Check(!json.Contains("ColumnPreparationDefaultsVersion", StringComparison.Ordinal), "Le reçu local de migration ne doit pas être partagé.");
        File.WriteAllText(path, json.Replace("\"ColumnPreparationMode\": \"all\"", "\"ColumnPreparationMode\": \"global\"", StringComparison.Ordinal));
        Refuse(() => ConfigurationTransferService.Import(path, new()));
        source.ColumnPreparationMode = "current"; profile.Acquisition["ColumnPreparationMode"] = "script";
        Refuse(() => ConfigurationTransferService.Export(path, source));

        source.ColumnPreparationMode = "supervision"; profile.Acquisition["ColumnPreparationMode"] = "supervision";
        ConfigurationTransferService.Export(path, source); imported = ConfigurationTransferService.Import(path, new());
        Check(imported.ColumnPreparationMode == "supervision" && imported.Sources.Single().Acquisition["ColumnPreparationMode"] == "supervision"
            && SettingsRecovery.Read(JsonSerializer.SerializeToUtf8Bytes(imported)).ColumnPreparationMode == "supervision");
        File.WriteAllText(path, "{\"PortalUrl\":\"https://glpi.example.test/front/ticket.php\",\"ColumnPreparationMode\":\"all\",\"ColumnPreparationDefaultsVersion\":1}");
        imported = ConfigurationTransferService.Import(path, new());
        Check(imported.ColumnPreparationMode == "all" && imported.ColumnPreparationDefaultsVersion == AppSettings.CurrentColumnPreparationDefaultsVersion
            && SettingsRecovery.Read(JsonSerializer.SerializeToUtf8Bytes(imported)).ColumnPreparationMode == "all",
            "Un choix importé explicitement ne doit pas être remplacé par la migration du profil local.");
        File.WriteAllText(path, "{\"PortalUrl\":\"https://glpi.example.test/front/ticket.php\"}");
        var current = new AppSettings { ColumnPreparationMode = "current" };
        Check(ConfigurationTransferService.Import(path, current).ColumnPreparationMode == "supervision");
        Check(ConfigurationTransferService.Import(path, current, out _).ColumnPreparationMode == "current",
            "Un aperçu d’import partiel qui ne fournit pas le mode doit conserver le choix courant.");
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
    private static void UsefulRoles()
    {
        var preferences = new NativeColumnPreferences { Options = DashboardService.Roles.Select(role => Option(role.Key, role.Aliases[0])).ToList() };
        preferences.Options.AddRange(Enumerable.Range(1, 300).Select(number => Option("plugin-" + number, "Champ supplémentaire " + number, "Extension")));
        var added = new List<NativeColumnOption>();
        while (ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), added) is { } next) added.Add(next);
        Check(added.Count == 31 && added.Select(option => option.Value).ToHashSet(StringComparer.Ordinal)
            .SetEquals(new[] { "id", "title", "status", "priority", "urgency", "impact", "type", "created", "updated", "resolved", "closed", "due",
                "assignee", "group", "requester", "category", "entity", "location", "city", "postal", "source", "updatedBy", "satisfaction", "satisfaction-invited",
                "followupAuthor", "taskAuthor", "ttrProgress", "takeIntoAccount", "reportedAge", "reportedGroupCount", "reportedAuthorCount" }),
            "Les rôles des filtres, métriques, dates et participants sont tous couverts, sans demander les textes ou les champs de plugins non utilisés.");
        Check(ColumnPreparationPolicy.MissingRoles(added.Select(ColumnPreparationPolicy.Header).ToArray(), new()).Count == 0);
    }
    private static void ConfiguredText()
    {
        var preferences = new NativeColumnPreferences { Options = new()
        {
            Option("21", "Description", "Caractéristiques"), Option("40", "Description", "Solutions"),
            Option("900", "Résumé métier", "Extension choisie"), Option("901", "Texte interne", "Autre extension")
        } };
        var settings = new DashboardSettings();
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, settings, Array.Empty<NativeColumnOption>()) is null);
        settings.ColumnMappings["description"] = "Extension choisie - Résumé métier";
        settings.ColumnMappings["solution"] = "Solutions - Description";
        var added = new List<NativeColumnOption>();
        while (ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, settings, added) is { } next) added.Add(next);
        Check(added.Select(option => option.Value).ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "40", "900" }),
            "Les textes explicitement mappés sont ajoutés avec leur namespace ; les textes natifs ou plugins concurrents ne les remplacent pas.");
        var missing = ColumnPreparationPolicy.MissingRoles(Array.Empty<string>(), settings);
        Check(missing.Contains("Solution") && missing.Contains("Description du ticket"));
        settings.ColumnMappings["description"] = "Champ configuré absent";
        Check(ColumnPreparationPolicy.Next("supervision", new[] { "Solutions - Description" }, preferences, settings, Array.Empty<NativeColumnOption>()) is null,
            "Un texte configuré absent ne doit pas être remplacé par une autre Description offerte.");

        static DashboardBoardSettings Board() => DashboardLayoutSettings.CreateBoard("Texte configuré", "empty");
        foreach (var configure in new Action<DashboardSettings>[]
        {
            value => { var board = Board(); board.Widgets.Add(new() { SourceKey = "with-solution" }); value.Layout.Boards.Add(board); },
            value => { var board = Board(); board.Widgets.Add(new() { SourceKey = "completed-no-solution" }); value.PersonalLayout.Boards.Add(board); },
            value => { var board = Board(); board.Id = "business-followup"; board.Widgets.Add(new() { SourceKey = "with-solution" }); value.Business.BoardOverrides.Add(board); },
            value => { var board = Board(); board.Filters.Add(new() { Kind = "metric", SourceKey = "with-solution" }); value.Layout.Boards.Add(board); },
            value => { var board = Board(); board.Widgets.Add(new() { Filters = new() { new() { Kind = "metric", SourceKey = "completed-no-solution" } } }); value.Layout.Boards.Add(board); },
            value => { var board = Board(); board.ComparisonEnabled = true; board.ComparisonFilters.Add(new() { Kind = "metric", SourceKey = "with-solution" }); value.Layout.Boards.Add(board); },
            value => { var board = Board(); board.SavedSelection = new() { CrossFilters = new() { new() { Kind = "metric", SourceKey = "with-solution" } } }; value.Layout.Boards.Add(board); },
            value => value.Business.WeatherRules.Add(new() { SourceKey = "with-solution" })
        })
        {
            settings = new(); configure(settings);
            Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, settings, Array.Empty<NativeColumnOption>())?.Value == "40",
                "Une métrique ou un filtre de solution réellement configuré doit obtenir sa colonne source.");
            missing = ColumnPreparationPolicy.MissingRoles(Array.Empty<string>(), settings);
            Check(missing.Contains("Solution") && !missing.Contains("Description du ticket"));
        }
        settings = new(); var inactive = Board();
        inactive.Widgets.Add(new() { SourceKey = "with-solution", Hidden = true });
        inactive.Widgets.Add(new() { SourceKey = "with-solution", Section = "hidden" });
        inactive.Sections.Add(new() { Key = "hidden", Hidden = true });
        inactive.ComparisonFilters.Add(new() { Kind = "metric", SourceKey = "with-solution" });
        settings.Layout.Boards.Add(inactive);
        settings.Layout.FilterPresets.Add(new() { Filters = new() { new() { Kind = "metric", SourceKey = "with-solution" } } });
        settings.Business.WeatherRules.Add(new() { SourceKey = "with-solution", Visible = false, WeatherEnabled = false });
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, settings, Array.Empty<NativeColumnOption>()) is null,
            "Les widgets masqués, les comparaisons désactivées et les contextes non appliqués ne déclenchent pas l’ajout de textes lourds.");
    }
    private static void QueryText()
    {
        var preferences = new NativeColumnPreferences { Options = new() { Option("21", "Description"), Option("40", "Solution") } };
        var queries = new QueryLibrarySettings { Queries = new() { new() { Conditions = new() { new() { Field = "role:description" } } } } };
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries)?.Value == "21");
        Check(ColumnPreparationPolicy.MissingRoles(Array.Empty<string>(), new(), queries).Contains("Description du ticket"));
        foreach (var configure in new Action<SavedQuery>[]
        {
            query => query.GroupBy = "role:solution",
            query => { query.GroupBy = "role:status"; query.GroupBySecondary = "role:solution"; },
            query => { query.GroupBy = "role:status"; query.GroupBySecondary = "role:type"; query.GroupByTertiary = "role:solution"; },
            query => query.Conditions.Add(new() { Field = "role:title", ValueSource = "field", Value = "role:solution" }),
            query => query.Conditions.Add(new() { Field = "role:title", Operator = "number-between", Value2Source = "field", Value2 = "role:solution" }),
            query => { query.Aggregate = "sum"; query.AggregateField = "role:solution"; },
            query => { query.GroupBy = "calc:texte"; query.ComputedFields.Add(new() { Key = "texte", Field = "role:solution" }); }
        })
        {
            var query = new SavedQuery(); configure(query); queries.Queries = new() { query };
            Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries)?.Value == "40",
                "Un rôle de texte explicitement utilisé dans une requête doit être préparé, même sans correspondance manuelle.");
        }
        queries.Queries = new() { new()
        {
            AggregateField = "role:solution", Conditions = new() { new() { Value = "role:description" } },
            ComputedFields = new() { new() { Key = "inutilise", Field = "role:solution" } }
        } };
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries) is null,
            "Une valeur littérale, un champ de calcul inactif ou un calcul non référencé ne doivent pas activer les textes lourds.");
    }
    private static void QueryExactColumns()
    {
        var preferences = new NativeColumnPreferences { Options = new()
        {
            Option("21", "Description"), Option("40", "Solution"),
            Option("900", "Coût validé", "Extension métier"), Option("901", "Texte inutilisé", "Autre extension")
        } };
        foreach (var (header, id) in new[] { ("Description", "21"), ("Solution", "40"), ("Extension métier - Coût validé", "900") })
        foreach (var configure in new Action<SavedQuery, string>[]
        {
            (query, field) => query.GroupBy = field,
            (query, field) => { query.GroupBy = "role:status"; query.GroupBySecondary = field; },
            (query, field) => { query.GroupBy = "role:status"; query.GroupBySecondary = "role:type"; query.GroupByTertiary = field; },
            (query, field) => query.Conditions.Add(new() { Field = field }),
            (query, field) => query.Conditions.Add(new() { Field = "role:title", ValueSource = "field", Value = field }),
            (query, field) => query.Conditions.Add(new() { Field = "role:title", Operator = "number-between", Value2Source = "field", Value2 = field }),
            (query, field) => { query.Aggregate = "sum"; query.AggregateField = field; }
        })
        {
            var query = new SavedQuery(); configure(query, "column:" + header);
            var queries = new QueryLibrarySettings { Queries = new() { query } };
            var added = new List<NativeColumnOption>();
            while (ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), added, queries) is { } next) added.Add(next);
            Check(added.Select(option => option.Value).SequenceEqual(new[] { id }),
                "La requête doit préparer uniquement la colonne exacte qu’elle utilise, y compris un champ de plugin sans rôle natif.");
        }
    }
    private static void QueryExactComputations()
    {
        var preferences = new NativeColumnPreferences { Options = new()
        {
            Option("21", "Description"), Option("40", "Solution"),
            Option("900", "Coût validé", "Extension métier"), Option("901", "Texte inutilisé", "Autre extension")
        } };
        var query = new SavedQuery
        {
            GroupBy = "calc:combine",
            ComputedFields = new()
            {
                new() { Key = "combine", Function = "concat", Field = "calc:description", OtherField = "column:Solution" },
                new() { Key = "description", Function = "lower", Field = "column:Description" },
                new() { Key = "inactif", Field = "column:Extension métier - Coût validé" }
            },
            AggregateField = "column:Autre extension - Texte inutilisé",
            Conditions = new() { new() { Field = "role:title", Value = "column:Autre extension - Texte inutilisé" } }
        };
        var queries = new QueryLibrarySettings { Queries = new() { query } };
        var added = new List<NativeColumnOption>();
        while (ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), added, queries) is { } next) added.Add(next);
        Check(added.Select(option => option.Value).SequenceEqual(new[] { "21", "40" }),
            "Les deux opérandes d’un calcul utilisé sont préparés, sans calcul inactif, valeur littérale ni champ d’agrégat du comptage.");
        query.GroupBy = "";
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries) is null,
            "Supprimer le dernier usage d’un calcul doit arrêter la préparation de ses textes lourds.");
        query.Aggregate = "sum"; query.AggregateField = "calc:inactif";
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries)?.Value == "900",
            "Un calcul nouvellement utilisé dans l’agrégat doit obtenir sa colonne exacte.");
        foreach (var op in new[] { "empty", "not-empty" })
        {
            queries.Queries = new() { new() { Conditions = new() { new()
            {
                Field = "role:title", Operator = op, ValueSource = "field", Value = "column:Description",
                Value2Source = "field", Value2 = "role:solution"
            } } } };
            Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries) is null,
                "Les opérateurs vide/non vide ne lisent aucune borne, même si un ancien choix de champ reste enregistré.");
        }
        foreach (var op in new[] { "equals", "number-gte", "date-before", "last-days" })
        {
            queries.Queries = new() { new() { Conditions = new() { new()
            { Field = "role:title", Operator = op, Value2Source = "field", Value2 = "column:Solution" } } } };
            Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries) is null,
                "Un opérateur à une seule borne doit ignorer le champ exact conservé dans sa seconde borne inactive.");
        }
        foreach (var op in new[] { "number-between", "number-outside", "date-between", "date-outside" })
        {
            queries.Queries = new() { new() { Conditions = new() { new()
            { Field = "role:title", Operator = op, Value2Source = "field", Value2 = "column:Solution" } } } };
            Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries)?.Value == "40",
                "Les opérateurs d’intervalle utilisent bien la deuxième borne exacte.");
        }
    }
    private static void QueryExactIdentity()
    {
        const string header = "Extension métier - Coût validé";
        var queries = new QueryLibrarySettings { Queries = new() { new() { GroupBy = "column:" + header } } };
        var preferences = new NativeColumnPreferences { Options = new()
        { Option("900", "Coût validé", "Extension métier"), Option("901", "Coût validé", "Extension métier") } };
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries) is null,
            "Deux options natives portant le même en-tête exact restent ambiguës : aucune ne doit être ajoutée.");
        var ambiguousRole = new NativeColumnPreferences { Options = new() { Option("12", "Statut"), Option("112", "Statut") } };
        var roleQuery = new QueryLibrarySettings { Queries = new() { new() { GroupBy = "column:Statut" } } };
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), ambiguousRole, new(), Array.Empty<NativeColumnOption>(), roleQuery) is null,
            "Le rôle opérationnel Statut ne doit pas contourner l’ambiguïté de la colonne exacte demandée.");
        preferences.Options.RemoveAt(1);
        var chosen = ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries);
        Check(chosen?.Value == "900");
        Check(ColumnPreparationPolicy.Next("supervision", new[] { header }, preferences, new(), Array.Empty<NativeColumnOption>(), queries) is null);
        Check(ColumnPreparationPolicy.Next("supervision", new[] { header, header }, preferences, new(), Array.Empty<NativeColumnOption>(), queries) is null,
            "Une ambiguïté déjà présente dans le relevé ne doit pas déclencher un troisième ajout.");
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), new[] { chosen! }, queries) is null);
        foreach (var mismatch in new[] { "extension métier - Coût validé", "Extension métier - Cout valide", " " + header, "Coût validé" })
        {
            queries.Queries[0].GroupBy = "column:" + mismatch;
            Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries) is null,
                "Une référence exacte ne doit pas utiliser la comparaison normalisée des rôles ni ignorer le namespace.");
        }
        queries.Queries[0].GroupBy = "column:" + header;
        preferences.Options[0].Disabled = true;
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries) is null);
        preferences.Options[0].Disabled = false; preferences.SelectedIdsKnown = true;
        preferences.Selected.Add(new() { Value = "900", Label = "Coût validé" });
        Check(ColumnPreparationPolicy.Next("supervision", Array.Empty<string>(), preferences, new(), Array.Empty<NativeColumnOption>(), queries) is null);
    }
    private static void QueryExactReceipt()
    {
        const string header = "Extension métier - Coût validé";
        const string receipt = "Colonne exacte « Extension métier - Coût validé »";
        var queries = new QueryLibrarySettings { Queries = new() { new() { GroupBy = "column:" + header } } };
        Check(ColumnPreparationPolicy.MissingRoles(Array.Empty<string>(), new(), queries).Contains(receipt));
        Check(!ColumnPreparationPolicy.MissingRoles(new[] { header }, new(), queries).Contains(receipt));
        Check(ColumnPreparationPolicy.MissingRoles(new[] { header, header }, new(), queries).Contains(receipt),
            "Un en-tête exact dupliqué est indisponible pour QueryService et ne doit pas être confirmé dans le reçu.");
        Check(ColumnPreparationPolicy.MissingRoles(new[] { header.ToLowerInvariant() }, new(), queries).Contains(receipt));
        queries.Queries[0].GroupBy = "";
        Check(!ColumnPreparationPolicy.MissingRoles(Array.Empty<string>(), new(), queries).Contains(receipt));
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

    private static ExtractionResult VerifiedNative() => new()
    {
        Complete = true, Status = "Collecte terminée", ExpectedTotal = 1,
        Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "7", "Exemple" } },
        Provenance = new() { Method = "native-csv", ScopeVerified = true },
        Quality = new()
        {
            AllPagesRequested = true, RowCountVerified = true, FinalTotalVerified = true, ScopeStable = true,
            InitialSample = new() { IdentityVerified = true, RepresentationVerified = true },
            FinalSample = new() { IdentityVerified = true, RepresentationVerified = true }
        }
    };

    private static void RevalidatedReceipt()
    {
        var data = VerifiedNative();
        var receipt = new ColumnPreparationReceipt { SourceChanged = true, RecoveryPending = true, RestorationState = "pending" };
        ColumnPreparationPolicy.ApplyReceipt(data, receipt);
        Check(data.Complete && receipt.SourceChanged && receipt.SourceRevalidated && receipt.RecoveryPending);
        Check(data.Status == "Collecte terminée" && data.Rows[0][1] == "Exemple" && data.Warnings.Count == 1);
        ColumnPreparationPolicy.ApplyReceipt(data, receipt);
        Check(data.Complete && receipt.SourceRevalidated && data.Warnings.Count == 1);
    }

    private static void IncompleteRevalidation()
    {
        foreach (var weaken in new Action<ExtractionResult>[]
        {
            data => data.Complete = false,
            data => data.Provenance.Method = "pagination",
            data => data.Provenance.ScopeVerified = false,
            data => data.Quality = null,
            data => data.Quality!.AllPagesRequested = false,
            data => data.Quality!.RowCountVerified = false,
            data => data.Quality!.FinalTotalVerified = null,
            data => data.Quality!.ScopeStable = false,
            data => data.Quality!.InitialSample!.IdentityVerified = null,
            data => data.Quality!.FinalSample!.IdentityVerified = false,
            data => data.Quality!.InitialSample!.RepresentationVerified = false,
            data => data.Quality!.FinalSample = null
        })
        {
            var data = VerifiedNative(); weaken(data);
            var receipt = new ColumnPreparationReceipt { SourceChanged = true, SourceRevalidated = true };
            ColumnPreparationPolicy.ApplyReceipt(data, receipt);
            Check(!data.Complete && !receipt.SourceRevalidated && data.Status.Contains("liste modifiée", StringComparison.Ordinal));
        }
    }
}
