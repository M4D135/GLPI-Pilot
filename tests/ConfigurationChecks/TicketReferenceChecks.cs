using GlpiPilot.Core;

internal static class TicketReferenceChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Fiche ticket : collecte vérifiée depuis l’accueil GLPI résolu vers la liste", ResolvedPortal);
        yield return ("Fiche ticket : origine, installation, recherche et preuve obligatoires après résolution", UnprovenSources);
        yield return ("Fiche ticket : critères configurés conservés et modèle de fiche toujours contrôlé", ResolvedFiltersAndTemplate);
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Refuse(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Une source ou un modèle non prouvé aurait dû rester refusé.");
    }

    private static (AppSettings Settings, ExtractionResult Data) Collected(string portal)
    {
        var settings = new AppSettings { PortalUrl = portal };
        var data = new ExtractionResult
        {
            SourceUrl = "https://glpi.example.test/glpi/front/ticket.php", Headers = new() { "ID", "Titre" },
            Rows = new() { new[] { "00042", "Ticket synthétique" } }, Complete = true,
            Provenance = new() { SourceKind = "browser", Method = "native-csv", RequestedUrl = portal, SearchVerified = true, ScopeVerified = true }
        };
        return (settings, data);
    }

    private static void ResolvedPortal()
    {
        foreach (var portal in new[] { "https://glpi.example.test/glpi/", "https://glpi.example.test/glpi/index.php",
            "https://glpi.example.test/glpi/front/central.php", "https://glpi.example.test/glpi/front/helpdesk.php" })
        {
            var (settings, data) = Collected(portal);
            Check(TargetUrlPolicy.IsPortalRoot(portal) && TargetUrlPolicy.IsTicketList(data.SourceUrl, portal),
                "Le scénario doit suivre la résolution de cible réellement permise par la navigation GLPI.");
            var ticket = TicketReferenceResolver.Resolve(data, data.Rows[0], settings);
            Check(ticket.Id == "42" && ticket.Url == "https://glpi.example.test/glpi/front/ticket.form.php?id=42",
                "La fiche doit utiliser l’installation et la liste réellement observées, sans reprendre le chemin de l’accueil.");
            data.Complete = false; data.Provenance.ScopeVerified = false; data.Provenance.InitialScopeVerified = true;
            data.Status = "Extraction à vérifier — contrôle final indisponible après l’export";
            data.Quality = new() { ScopeStable = false };
            var partialTicket = TicketReferenceResolver.Resolve(data, data.Rows[0], settings);
            Check(partialTicket == ticket && !data.Complete && !data.Provenance.ScopeVerified && data.Quality.ScopeStable == false,
                "La source initiale prouvée permet de lire une fiche après une redirection d’export, sans certifier le résultat partiel ni son périmètre final.");
        }
    }

    private static void UnprovenSources()
    {
        const string portal = "https://glpi.example.test/glpi/";
        foreach (var change in new Action<AppSettings, ExtractionResult>[]
        {
            (_, data) => data.SourceUrl = "https://other.example.test/glpi/front/ticket.php",
            (_, data) => data.SourceUrl = "http://glpi.example.test/glpi/front/ticket.php",
            (_, data) => data.SourceUrl = "https://glpi.example.test:444/glpi/front/ticket.php",
            (_, data) => data.SourceUrl = "https://glpi.example.test/other/front/ticket.php",
            (_, data) => data.SourceUrl = "https://glpi.example.test/glpi/front/computer.php",
            (_, data) => data.SourceUrl = "https://glpi.example.test/glpi/front/report.dynamic.php",
            (_, data) => data.SourceUrl = "https://glpi.example.test/glpi/front/displaypreference.form.php",
            (_, data) => data.SourceUrl = "https://name:secret@glpi.example.test/glpi/front/ticket.php",
            (_, data) => data.SourceUrl = "https://glpi.example.test/glpi/%252e%252e/front/ticket.php",
            (_, data) => data.Provenance.SearchVerified = false,
            (_, data) => { data.Provenance.ScopeVerified = false; data.Provenance.InitialScopeVerified = false; },
            (_, data) => data.Provenance.SourceKind = "file",
            (_, data) => data.Provenance.RequestedUrl = "",
            (_, data) => data.Provenance.RequestedUrl = "https://other.example.test/glpi/",
            (_, data) => data.Provenance.RequestedUrl = portal + "?criteria[0][value]=other",
            (settings, _) => settings.PortalUrl = portal + "?criteria[0][value]=new",
            (settings, _) => settings.PortalUrl = "https://glpi.example.test/glpi/plugins/custom/dashboard.php"
        })
        {
            foreach (var initialOnly in new[] { false, true })
            {
                var (settings, data) = Collected(portal);
                data.Provenance.ScopeVerified = !initialOnly; data.Provenance.InitialScopeVerified = initialOnly;
                change(settings, data);
                Refuse(() => TicketReferenceResolver.Resolve(data, data.Rows[0], settings));
            }
        }
    }

    private static void ResolvedFiltersAndTemplate()
    {
        const string portal = "https://glpi.example.test/glpi/front/central.php?criteria%5B0%5D%5Bfield%5D=12&criteria%5B0%5D%5Bvalue%5D=1";
        var (settings, data) = Collected(portal);
        settings.TicketDetails.UrlTemplate = "https://glpi.example.test/glpi/front/ticket.form.php?id={id}&forcetab=Log$1";
        var ticket = TicketReferenceResolver.Resolve(data, data.Rows[0], settings);
        Check(ticket.Url == "https://glpi.example.test/glpi/front/ticket.form.php?id=42&forcetab=Log%241"
            && settings.PortalUrl == portal && data.Provenance.RequestedUrl == portal,
            "La résolution ne doit modifier ni les filtres de collecte ni le modèle explicite de lecture.");
        settings.TicketDetails.UrlTemplate = "https://glpi.example.test/other/front/ticket.form.php?id={id}";
        Refuse(() => TicketReferenceResolver.Resolve(data, data.Rows[0], settings));
        settings.TicketDetails.UrlTemplate = "https://glpi.example.test/glpi/front/ticket.form.php?id={id}&delete=1";
        Refuse(() => TicketReferenceResolver.Resolve(data, data.Rows[0], settings));
    }
}
