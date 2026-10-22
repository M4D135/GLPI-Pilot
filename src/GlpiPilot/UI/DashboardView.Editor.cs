using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class DashboardView
{
    private sealed record Choice(string Key, string Label);
    private sealed record CatalogItem(string Kind, string Key, string Label, string Category, string Description = "");

    private FrameworkElement BuildEditor()
    {
        var content = new StackPanel();
        content.Children.Add(Text("Composer votre espace", 18, weight: FontWeights.SemiBold));
        content.Children.Add(Text(Board.Name, 12, "MutedBrush", margin: new Thickness(0, 5, 0, 13)));
        var tabs = new WrapPanel();
        foreach (var (key, label) in new[] { ("library", "Catalogue"), ("widget", "Widget"), ("boards", "Dispositions") })
        {
            var tab = DescribedCommand(label, key switch
            {
                "library" => "Rechercher et ajouter un widget, ou retrouver un widget masqué.",
                "widget" => "Régler le contenu, l’apparence et la taille du widget sélectionné.",
                _ => "Organiser vos dispositions et choisir un modèle de départ."
            }, () => { _editorMode = key; Render(); }, key != "widget" || _selectedWidget != null);
            tab.Padding = new Thickness(10, 7, 10, 7); tab.Margin = new Thickness(0, 0, 6, 7);
            if (_editorMode == key) { tab.SetResourceReference(Button.BackgroundProperty, "SelectedBrush"); tab.SetResourceReference(Button.BorderBrushProperty, "AccentBrush"); }
            tabs.Children.Add(tab);
        }
        content.Children.Add(tabs);
        var body = new StackPanel(); var actions = new StackPanel();
        if (_editorMode == "widget" && Board.Widgets.FirstOrDefault(widget => widget.Id == _selectedWidget) is { } selected)
            BuildWidgetEditor(body, actions, selected);
        else if (_editorMode == "boards") BuildBoardsEditor(body);
        else BuildLibrary(body);
        content.Children.Add(body); content.Children.Add(actions);
        _contextHelp = Text("Survolez une option ou utilisez Tab pour comprendre son effet. Les changements appliqués sont enregistrés automatiquement.", 11, "MutedBrush");
        var hint = new Border { Child = _contextHelp, Padding = new Thickness(11), Margin = new Thickness(0, 13, 0, 0), CornerRadius = new CornerRadius(9) };
        hint.SetResourceReference(Border.BackgroundProperty, "NoticeBrush"); content.Children.Add(hint);
        var card = Card(content); card.Width = 340; card.VerticalAlignment = VerticalAlignment.Top; card.Margin = new Thickness(18, 0, 0, 0); return card;
    }

    private IEnumerable<CatalogItem> Catalog()
    {
        yield return new("weather", "", "Météo du relevé", "Synthèse", "Une lecture des échéances, priorités, affectations et signes d’ancienneté.");
        yield return new("attention", "", "Tickets à regarder en priorité", "Synthèse", "Les lignes à examiner selon leur échéance, priorité et ancienneté.");
        yield return new("timeline", "", "Activité datée sur 30 jours", "Synthèse", "Créations, résolutions et clôtures observées dans le relevé.");
        yield return new("quality", "", "Qualité, avertissements et colonnes", "Synthèse", "Périmètre, champs reconnus, doublons et données manquantes.");
        yield return new("note", "", "Note et consigne personnelle", "Texte", "Une consigne, un contexte ou un repère de lecture écrit par vous.");
        foreach (var (key, title) in new[] { ("deadlines", "Météo des échéances"), ("quality", "Météo de qualité"), ("geography", "Météo des lieux") })
            yield return new("weather", key, title, "Synthèse", "Indice descriptif spécialisé, avec couverture et méthode visibles.");
        foreach (var (key, title) in new[] { ("technician-city", "Techniciens × villes"), ("city-status", "Villes × statuts"), ("category-status", "Catégories × statuts"), ("urgency-impact", "Urgence × impact") })
            yield return new("matrix", key, title, "Analyses croisées", "Matrice chiffrée et sélection de ses lignes ; peut aussi être affichée en barres empilées.");
        foreach (var (key, title) in new[] { ("age", "Histogramme d’ancienneté"), ("inactivity", "Histogramme des modifications"), ("resolution", "Histogramme des délais de résolution"), ("overdue-delay", "Histogramme des retards") })
            yield return new("histogram", key, title, "Analyses croisées", "Tranches réglables dans les options du widget ; valeurs absentes exclues.");
        foreach (var key in new[] { "age", "resolution" }) foreach (var role in new[] { "city", "assignee", "category" })
            yield return new("boxplot", key + "-" + role, (key == "age" ? "Âge" : "Résolution") + " · dispersion par " + (role == "city" ? "ville" : role == "assignee" ? "technicien" : "catégorie"), "Analyses croisées", "Médiane, quartiles, minimum, maximum et P90, avec l’effectif de chaque segment.");
        yield return new("scatter", "age-deadline", "Âge × échéance", "Analyses croisées", "Chaque point correspond à un ticket actif avec deux dates exploitables.");
        foreach (var (key, title) in new[] { ("total", "Stock observé"), ("active", "Actifs observés"), ("overdue", "Échéances dépassées observées"), ("high", "Priorités hautes observées") })
            yield return new("history", key, title, "Historique", "Relevés comparables conservés localement. Les périodes manquantes et incompatibles restent signalées.");
        if (_data != null)
        {
            foreach (var metric in _data.Metrics) yield return new("metric", metric.Key, metric.Label, CatalogCategory("metric", metric.Key), metric.Explanation);
            foreach (var distribution in _data.Distributions) yield return new("distribution", distribution.Key, distribution.Title, CatalogCategory("distribution", distribution.Key), distribution.Note);
        }
        else
        {
            foreach (var (key, label) in new[]
            {
                ("total", "Total du relevé"), ("unique", "Identifiants distincts"), ("duplicate", "Lignes avec ID répété"), ("missing-id", "Sans ID exploitable"),
                ("active", "Tickets actifs"), ("new", "Nouveaux"), ("processing", "En cours"), ("pending", "En attente"), ("resolved", "Résolus"), ("closed", "Clos"), ("unknown-status", "Statuts non classés"),
                ("high", "Priorités hautes actives"), ("major", "Priorités majeures actives"), ("high-urgency", "Urgences hautes actives"), ("high-impact", "Impacts hauts actifs"),
                ("with-solution", "Solution renseignée"), ("completed-no-solution", "Terminés sans solution visible"),
                ("overdue", "Échéances dépassées"), ("soon", "Échéances proches"), ("unassigned", "Sans affectation"), ("stale", "Sans modification récente"), ("old", "Tickets anciens"),
                ("without-technician", "Actifs sans technicien"), ("without-group", "Actifs sans groupe"), ("multi-technician", "Actifs avec plusieurs techniciens"),
                ("active-no-due", "Actifs sans échéance renseignée"), ("due-today", "Échéances aujourd’hui"), ("due-tomorrow", "Échéances demain"), ("due-week", "Échéances d’ici la fin de semaine"),
                ("new-unassigned", "Nouveaux sans affectation"), ("high-overdue", "Priorités hautes en retard"), ("high-stale", "Priorités hautes sans modification récente"),
                ("pending-old", "Tickets en attente anciens"), ("pending-stale", "Tickets en attente sans modification récente"), ("new-stale", "Nouveaux sans modification récente"),
                ("high-without-technician", "Priorités hautes sans technicien"), ("overdue-without-technician", "Échéances dépassées sans technicien"), ("group-only", "Actifs affectés à un groupe sans technicien"),
                ("planned-overdue", "Tickets planifiés en retard"), ("old-without-technician", "Tickets anciens sans technicien"), ("active-no-category", "Actifs sans catégorie"),
                ("resolved-aging", "Résolus en attente de clôture"), ("resolved-no-date", "Résolus sans date de résolution exploitable"), ("high-incidents", "Incidents actifs de priorité haute"),
                ("missing-category", "Catégorie non renseignée"), ("missing-requester", "Demandeur non renseigné"),
                ("invalid-dates", "Dates non interprétables"), ("future-dates", "Dates historiques futures"), ("inconsistent-dates", "Chronologie incohérente"),
                ("active-incidents", "Incidents actifs"), ("active-requests", "Demandes actives")
            })
                yield return new("metric", key, label, CatalogCategory("metric", key), "Un chiffre calculé sur votre périmètre, avec accès aux lignes correspondantes.");
            foreach (var (key, label) in new[] { ("completed-share", "Part résolue ou close"), ("active-share", "Part active"), ("pending-share", "Part des actifs en attente"), ("unassigned-share", "Part sans affectation"), ("overdue-share", "Part à échéance dépassée"), ("high-share", "Part de priorités hautes"), ("without-technician-share", "Part sans technicien"), ("without-group-share", "Part sans groupe") })
                yield return new("metric", key, label, CatalogCategory("metric", key), "Une proportion sur l’échantillon exploitable. La base de calcul reste affichée après chargement du relevé.");
            foreach (var (key, label) in new[] { ("city-matched", "Villes reconnues"), ("city-unmatched", "Sans correspondance de ville"), ("city-ambiguous", "Correspondances ambiguës"), ("city-no-title", "Titre indisponible pour la ville"), ("city-coverage", "Couverture des correspondances") })
                yield return new("metric", key, label, "Localisation", "Une mesure issue du dictionnaire de villes et des titres exploitables du relevé.");
            yield return new("distribution", "city-detection", "État des correspondances de villes", "Localisation", "Villes reconnues, absences de correspondance, ambiguïtés et titres indisponibles.");
            foreach (var (key, label) in new[] { ("age", "Âge des actifs"), ("inactivity", "Ancienneté de la dernière modification"), ("resolution", "Délai ouverture → résolution"), ("closure", "Délai ouverture → clôture"), ("resolution-closure", "Délai résolution → clôture"), ("resolved-waiting", "Attente de clôture des résolus") })
                foreach (var (suffix, statistic) in new[] { ("mean", "moyenne"), ("median", "médiane"), ("p90", "90e percentile"), ("max", "maximum") })
                    yield return new("metric", key + "-" + suffix, label + " · " + statistic, CatalogCategory("metric", key + "-" + suffix), "Durée calendaire issue des dates exploitables du relevé, avec son échantillon. Ne mesure pas le temps de travail.");
            foreach (var (key, label) in new[] { ("created", "Créations"), ("resolved", "Résolutions datées"), ("closed", "Clôtures datées") })
                foreach (var days in new[] { 7, 30 })
                    yield return new("metric", key + "-" + days, label + " sur " + days + " j", "Indicateurs", "Événements datés des lignes récupérées, sur la période glissante indiquée.");
            foreach (var (key, label) in new[] { ("status", "Statuts"), ("priority", "Priorités"), ("active-priority", "Priorités des actifs"), ("age", "Âge des actifs"), ("inactivity", "Dernière modification des actifs"), ("urgency", "Urgences"), ("impact", "Impacts"), ("type", "Incidents et demandes"), ("active-type", "Types des actifs"), ("assignment-coverage", "Couverture des affectations"), ("overdue-delay", "Retard des échéances"), ("due-horizon", "Horizon des échéances") })
                yield return new("distribution", key, label, CatalogCategory("distribution", key), "Une répartition consultable en barres, en anneau ou en liste. Toutes les catégories restent accessibles avec recherche.");
            foreach (var role in DashboardService.Roles.Where(role => role.Key is "group" or "assignee" or "category" or "entity" or "requester" or "location" or "city" or "postal" or "source" or "updatedBy"))
            {
                yield return new("distribution", role.Key, role.Label + " · relevé", CatalogCategory("distribution", role.Key), "Les valeurs de cette colonne dans le périmètre du relevé.");
                if (role.Key is not ("requester" or "updatedBy"))
                    yield return new("distribution", "active-" + role.Key, role.Label + " · actifs", CatalogCategory("distribution", "active-" + role.Key), "Les valeurs de cette colonne parmi les lignes actives reconnues.");
            }
            foreach (var role in DashboardService.Roles.Where(role => role.Key is "created" or "updated" or "resolved" or "closed" or "due"))
                yield return new("distribution", "quality-" + role.Key, "Qualité · " + role.Label, CatalogCategory("distribution", "quality-" + role.Key), "Disponibilité et validité des valeurs de date présentes dans cette colonne.");
        }
    }

    private static string CatalogCategory(string kind, string key)
    {
        if (key.StartsWith("query:", StringComparison.Ordinal) || key.StartsWith("query-group:", StringComparison.Ordinal)) return "Mes requêtes";
        if (key.StartsWith("city-", StringComparison.Ordinal) || key is "city" or "location" or "postal" or "active-city" or "active-location" or "active-postal") return "Localisation";
        if (key.StartsWith("quality-", StringComparison.Ordinal) || key is "unique" or "duplicate" or "missing-id" or "unknown-status" or "missing-category" or "missing-requester" or "invalid-dates" or "future-dates" or "inconsistent-dates" or "resolved-no-date" or "active-no-category" or "completed-no-solution") return "Qualité des données";
        if (key.StartsWith("age-", StringComparison.Ordinal) || key.StartsWith("inactivity-", StringComparison.Ordinal)
            || key.StartsWith("resolution-", StringComparison.Ordinal) || key.StartsWith("closure-", StringComparison.Ordinal)
            || key.StartsWith("resolved-waiting-", StringComparison.Ordinal) || key is "age" or "inactivity") return "Durées et ancienneté";
        if (key.Contains("overdue", StringComparison.Ordinal) || key.StartsWith("due-", StringComparison.Ordinal) || key is "soon" or "active-no-due") return "Échéances";
        if (key.Contains("technician", StringComparison.Ordinal) || key.Contains("unassigned", StringComparison.Ordinal)
            || key is "without-group" or "without-group-share" or "group-only" or "group" or "active-group" or "assignee" or "active-assignee" or "assignment-coverage") return "Affectations";
        if (key.StartsWith("high", StringComparison.Ordinal) || key.StartsWith("pending-", StringComparison.Ordinal)
            || key is "old" or "stale" or "new-stale" or "major" or "resolved-aging") return "Priorités et suivi";
        return kind == "metric" ? "Indicateurs" : "Répartitions";
    }

    private static string FavoriteKey(CatalogItem item) => item.Kind + "\n" + item.Key;
    private bool CatalogAvailable(CatalogItem item) => item.Kind == "note" || (_data != null && (item.Kind switch
    {
        "metric" => _data.Metric(item.Key)?.Value is double value && double.IsFinite(value),
        "distribution" => _data.Distributions.FirstOrDefault(distribution => distribution.Key == item.Key)?.Available == true,
        "timeline" => _data.Columns.Any(column => column.Index.HasValue && column.Role is "created" or "resolved" or "closed"),
        "weather" => (item.Key.Length == 0 || item.Key == "overall" ? _data.Weather : _data.WeatherProfiles.GetValueOrDefault(item.Key))?.State is { } state && state != "unknown",
        "matrix" => _data.Matrices.Any(x => x.Key == item.Key && x.Available),
        "histogram" => _data.NumericSeries.Any(x => x.Key == item.Key && x.Values.Count > 0),
        "boxplot" => _data.BoxSeries.Any(x => x.Key == item.Key && x.Available && x.Boxes.Count > 0),
        "scatter" => _data.Scatters.Any(x => x.Key == item.Key && x.Points.Count > 0),
        "history" => _data.History.Any(x => x.Key == item.Key && x.Points.Any(p => p.Comparable && p.Value.HasValue)),
        _ => true
    }));

    private void BuildLibrary(Panel body)
    {
        body.Children.Add(Text("Trouver le bon widget", 15, weight: FontWeights.SemiBold, margin: new Thickness(0, 4, 0, 5)));
        body.Children.Add(Text("Vous pouvez ajouter plusieurs fois une même source avec des présentations différentes.", 11, "MutedBrush"));
        var search = Input(body, "Rechercher un widget", _libraryQuery, 180);
        Help(search, "Rechercher un widget", "Filtrer les noms et descriptions du catalogue. Cette recherche ne modifie pas les données du relevé.");
        var all = Catalog().ToArray();
        var categories = new[] { new Choice("all", "Toutes les catégories") }.Concat(all.Select(item => item.Category).Distinct().Select(name => new Choice(name, name)));
        var category = Select(body, "Catégorie", categories, _libraryCategory);
        var available = Check(body, "Disponibles seulement", _libraryAvailableOnly);
        Help(available, "Disponibles seulement", "Afficher les sources calculables avec le relevé chargé et les notes. Une valeur zéro calculée reste disponible ; une colonne absente ne l’est pas.");
        var favorites = Check(body, "Mes favoris mémorisés", _libraryFavoritesOnly);
        Help(favorites, "Filtrer mes widgets favoris", "Retrouver les widgets marqués d’une étoile entre les sessions. Les favoris font partie de cette configuration de dashboards.");
        var count = Text("", 11, "MutedBrush", margin: new Thickness(0, 12, 0, 2)); body.Children.Add(count);
        var list = new StackPanel(); body.Children.Add(list);
        var libraryButtons = new HashSet<Button>();
        void Track(Button button) { libraryButtons.Add(button); }
        bool Matches(string value) => string.IsNullOrWhiteSpace(_libraryQuery)
            || CultureInfo.CurrentCulture.CompareInfo.IndexOf(value, _libraryQuery.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        void Fill(bool resetPage = false)
        {
            if (resetPage) _libraryPage = 0;
            _commands.RemoveAll(pair => libraryButtons.Contains(pair.Button)); libraryButtons.Clear(); list.Children.Clear();
            _libraryQuery = search.Text; _libraryCategory = Selected(category);
            _libraryAvailableOnly = available.IsChecked == true; _libraryFavoritesOnly = favorites.IsChecked == true;
            var results = all.Where(item => (_libraryCategory == "all" || item.Category == _libraryCategory)
                && (!_libraryAvailableOnly || CatalogAvailable(item))
                && (!_libraryFavoritesOnly || _libraryFavorites.Contains(FavoriteKey(item)))
                && (Matches(item.Label) || Matches(item.Description))).ToArray();
            const int pageSize = 6;
            var pageCount = Math.Max(1, (results.Length + pageSize - 1) / pageSize);
            _libraryPage = Math.Clamp(_libraryPage, 0, pageCount - 1);
            var visible = results.Skip(_libraryPage * pageSize).Take(pageSize).ToArray();
            count.Text = $"{results.Length:N0} résultat(s) sur {all.Length:N0} sources · {all.Count(CatalogAvailable):N0} disponibles au total";
            void Pager()
            {
                if (pageCount == 1) return;
                var pager = new WrapPanel { Margin = new Thickness(0, 8, 0, 3) };
                var previous = DescribedCommand("←", "Page précédente du catalogue", () => { _libraryPage--; Fill(); }, _libraryPage > 0);
                Help(previous, "Catalogue · page précédente", "Afficher les six widgets précédents du catalogue filtré.");
                pager.Children.Add(previous); Track(previous);
                pager.Children.Add(Text($"Page {_libraryPage + 1} / {pageCount}", 11, "MutedBrush", margin: new Thickness(2, 9, 10, 0)));
                var next = DescribedCommand("→", "Page suivante du catalogue", () => { _libraryPage++; Fill(); }, _libraryPage + 1 < pageCount);
                Help(next, "Catalogue · page suivante", "Afficher les six widgets suivants du catalogue filtré.");
                pager.Children.Add(next); Track(next); list.Children.Add(pager);
            }
            Pager();
            foreach (var group in visible.GroupBy(item => item.Category))
            {
                list.Children.Add(Text(group.Key, 11, "MutedBrush", FontWeights.SemiBold, new Thickness(0, 11, 0, 7)));
                foreach (var item in group)
                {
                    var content = new StackPanel();
                    var heading = new DockPanel();
                    var add = Text("+", 18, "AccentBrush", FontWeights.SemiBold, new Thickness(10, 0, 0, 0)); DockPanel.SetDock(add, Dock.Right); heading.Children.Add(add);
                    heading.Children.Add(Text(item.Label, 12, weight: FontWeights.SemiBold)); content.Children.Add(heading);
                    var description = Text(PreviewText(item.Description, 140), 11, "MutedBrush", margin: new Thickness(0, 5, 0, 0)); content.Children.Add(description);
                    var used = Board.Widgets.Count(widget => widget.Kind == item.Kind && widget.SourceKey == item.Key);
                    var unavailable = _data != null && !CatalogAvailable(item);
                    if (unavailable) content.Children.Add(Text("Données à renseigner", 10, "WarningBrush", margin: new Thickness(0, 6, 0, 0)));
                    if (used > 0) content.Children.Add(Text($"Déjà présent · {used} exemplaire(s)", 10, "MutedBrush", margin: new Thickness(0, 5, 0, 0)));
                    var button = Command(content, () => AddWidget(item), Board.Widgets.Count < 60);
                    button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(11); button.Margin = new Thickness(0, 0, 0, 7);
                    Help(button, "Ajouter · " + item.Label, "Ajouter ce widget à la disposition puis régler son apparence. Une source indisponible restera signalée tant que sa colonne n’est pas reconnue.");
                    button.ToolTip = item.Description;
                    var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                    row.Children.Add(button); Track(button);
                    var favorite = _libraryFavorites.Contains(FavoriteKey(item));
                    var star = DescribedCommand(favorite ? "★" : "☆", (favorite ? "Retirer des favoris · " : "Ajouter aux favoris · ") + item.Label,
                        () => { if (!_libraryFavorites.Add(FavoriteKey(item))) _libraryFavorites.Remove(FavoriteKey(item)); _layout.CatalogFavorites = _libraryFavorites.Order(StringComparer.Ordinal).ToList(); Persist(); Fill(); }, quiet: true);
                    Help(star, (favorite ? "Retirer des favoris · " : "Ajouter aux favoris · ") + item.Label, "Mémoriser ce favori dans le catalogue pour les prochaines sessions.");
                    star.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"); star.FontSize = 19; star.Padding = new Thickness(7); star.MinWidth = 32; star.Margin = new Thickness(4, 2, 0, 0); star.VerticalAlignment = VerticalAlignment.Top;
                    if (favorite) star.SetResourceReference(Button.ForegroundProperty, "AccentBrush");
                    Grid.SetColumn(star, 1); row.Children.Add(star); Track(star); list.Children.Add(row);
                }
            }
            Pager();
            if (results.Length == 0)
            {
                list.Children.Add(Text("Aucun widget ne correspond à ces filtres.", 12, "MutedBrush", margin: new Thickness(0, 14, 0, 10)));
                var clear = DescribedCommand("Effacer les filtres", "Revenir à l’ensemble du catalogue de widgets.", () => { search.Text = ""; category.SelectedValue = "all"; available.IsChecked = favorites.IsChecked = false; });
                list.Children.Add(clear); Track(clear);
            }
        }
        Fill(); search.TextChanged += (_, _) => Fill(true); category.SelectionChanged += (_, _) => Fill(true);
        available.Checked += (_, _) => Fill(true); available.Unchecked += (_, _) => Fill(true);
        favorites.Checked += (_, _) => Fill(true); favorites.Unchecked += (_, _) => Fill(true);
        if (Board.Widgets.Count >= 60) body.Children.Add(Text("Cette disposition contient déjà 60 widgets. Retirez-en un ou créez une autre disposition.", 12, "WarningBrush", margin: new Thickness(0, 10, 0, 0)));
        var hidden = Board.Widgets.Where(widget => widget.Hidden).ToArray();
        if (hidden.Length > 0)
        {
            var restore = Section(body, $"Widgets masqués · {hidden.Length}", "Ils sont conservés dans cette disposition et peuvent être affichés à nouveau.", true);
            foreach (var widget in hidden)
                restore.Children.Add(DescribedCommand("Réafficher · " + WidgetTitle(widget), "Réafficher ce widget à sa place dans la disposition.", () => Change(() => { widget.Hidden = false; _selectedWidget = widget.Id; })));
        }
    }

    private void AddWidget(CatalogItem item)
    {
        if (Board.Widgets.Count >= 60) return;
        var widget = new DashboardWidgetSettings { Kind = item.Kind, SourceKey = item.Key, Title = item.Label, Span = item.Kind is "weather" or "timeline" or "attention" or "quality" ? 12 : item.Kind == "metric" ? 3 : 4, Height = item.Kind == "metric" ? 200 : item.Kind == "attention" ? 420 : 340 };
        Change(() => { Board.Widgets.Add(widget); _selectedWidget = widget.Id; _editorMode = "widget"; });
    }

    private void BuildWidgetEditor(Panel body, Panel actions, DashboardWidgetSettings widget)
    {
        body.Children.Add(Text("Options du widget", 15, weight: FontWeights.SemiBold, margin: new Thickness(0, 4, 0, 4)));
        body.Children.Add(Text("Ajustez les options, puis appliquez vos changements.", 11, "MutedBrush"));
        var previewTitle = Text(WidgetTitle(widget), 14, weight: FontWeights.SemiBold);
        var previewContent = new StackPanel(); previewContent.Children.Add(previewTitle);
        previewContent.Children.Add(Text("Aperçu du style de carte", 11, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
        var preview = Card(previewContent); preview.Margin = new Thickness(0, 12, 0, 2); preview.Padding = new Thickness(14); body.Children.Add(preview);

        var data = Section(body, "Contenu", "Choisir ce que le widget doit afficher.", true);
        var title = Input(data, "Titre personnalisé", widget.Title, 180);
        Help(title, "Titre personnalisé", "Donner un titre au widget. Un titre vide reprend le nom de sa source.");
        var kinds = DashboardWidgetSettings.Kinds.Select(key => new Choice(key, KindLabel(key))).ToArray();
        var kind = Select(data, "Type de contenu", kinds, widget.Kind);
        var sourcePanel = new StackPanel(); data.Children.Add(sourcePanel); ComboBox? source = null;
        Action refreshPreview = () => { }, updateOptions = () => { };
        void FillSources()
        {
            sourcePanel.Children.Clear(); var currentKind = Selected(kind);
            if (currentKind is not ("metric" or "distribution" or "weather" or "matrix" or "histogram" or "boxplot" or "scatter" or "history")) { source = null; return; }
            var choices = Catalog().Where(item => item.Kind == currentKind).Select(item => new Choice(item.Key, item.Label)).ToList();
            if (currentKind == widget.Kind && !choices.Any(choice => choice.Key == widget.SourceKey)) choices.Add(new(widget.SourceKey, widget.SourceKey));
            source = Select(sourcePanel, "Source de données", choices, currentKind == widget.Kind ? widget.SourceKey : choices.FirstOrDefault()?.Key ?? "");
            source.SelectionChanged += (_, _) => refreshPreview();
        }
        FillSources();
        var notePanel = new StackPanel(); data.Children.Add(notePanel);
        var note = Input(notePanel, "Texte de la note", widget.Note, 4000); note.AcceptsReturn = true; note.TextWrapping = TextWrapping.Wrap; note.Height = 115; note.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Help(note, "Texte de la note", "Écrire une consigne ou un repère. Cette note fait partie de la disposition et sera incluse dans sa configuration partagée.");
        var sectionName = Input(data, "Section du dashboard · facultative", widget.Section, 80);
        var hist = Input(data, "Bornes d’histogramme · séparées par ;", string.Join(" ; ", widget.HistogramBreaks.Select(x => x.ToString("G", CultureInfo.CurrentCulture))), 500);
        Help(hist, "Tranches de l’histogramme", "Exemple : 1 ; 7 ; 14 ; 30 ; 90. Les valeurs sont exprimées dans l’unité de la série, et la borne supérieure est exclue.");
        var filterPanel = Section(body, "Filtres de ce widget", "Ces conditions s’ajoutent au périmètre commun du dashboard.", false);
        var readFilters = ScopeEditor(filterPanel, widget.Filters);

        var appearance = Section(body, "Apparence", "Prévisualiser les bordures, la couleur et la présentation avant d’appliquer.", true);
        var styles = Select(appearance, "Style de carte", new[] { new Choice("surface", "Surface sobre"), new Choice("outline", "Contour coloré"), new Choice("accent", "Accent latéral"), new Choice("minimal", "Minimal") }, widget.Style);
        var accentChoices = new[] { new Choice("blue", "Bleu"), new Choice("teal", "Sarcelle"), new Choice("green", "Vert"), new Choice("orange", "Ambre"), new Choice("red", "Rouge"), new Choice("purple", "Violet"), new Choice("slate", "Ardoise") };
        var accents = Select(appearance, "Couleur d’accent", accentChoices, widget.Accent);
        var swatches = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var accent in accentChoices)
        {
            var swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Accent(accent.Key) };
            var choose = Command(swatch, () => accents.SelectedValue = accent.Key); choose.Padding = new Thickness(7); choose.MinHeight = 30; choose.Margin = new Thickness(0, 0, 6, 4);
            Help(choose, "Accent · " + accent.Label, "Prévisualiser cette couleur d’accent ; le bouton Appliquer enregistre le choix."); swatches.Children.Add(choose);
        }
        appearance.Children.Add(swatches);
        var presentationPanel = new StackPanel(); appearance.Children.Add(presentationPanel);
        var presentation = Select(presentationPanel, "Présentation", new[] { new Choice("bars", "Barres / détaillée"), new Choice("list", "Liste chiffrée"), new Choice("compact", "Compacte"), new Choice("donut", "Anneau"), new Choice("gauge", "Jauge avec objectif"), new Choice("pareto", "Pareto"), new Choice("columns", "Colonnes"), new Choice("heatmap", "Matrice"), new Choice("stacked", "Barres empilées") }, widget.Presentation);
        void ConfigurePresentation()
        {
            var selected = Selected(presentation);
            var choices = Selected(kind) switch
            {
                "distribution" => new[] { new Choice("bars", "Barres"), new Choice("donut", "Anneau"), new Choice("pareto", "Pareto"), new Choice("columns", "Colonnes"), new Choice("list", "Liste chiffrée"), new Choice("compact", "Liste compacte") },
                "matrix" => new[] { new Choice("heatmap", "Matrice colorée"), new Choice("stacked", "Barres empilées"), new Choice("list", "Liste chiffrée") },
                "histogram" => new[] { new Choice("columns", "Colonnes"), new Choice("bars", "Barres"), new Choice("list", "Liste chiffrée") },
                "metric" => new[] { new Choice("bars", "Détaillée"), new Choice("gauge", "Jauge avec objectif"), new Choice("compact", "Compacte") },
                _ => new[] { new Choice("bars", "Détaillée"), new Choice("compact", "Compacte") }
            };
            presentation.ItemsSource = choices; presentation.SelectedValue = choices.Any(choice => choice.Key == selected) ? selected : choices[0].Key;
        }
        ConfigurePresentation();
        var topPanel = new StackPanel(); appearance.Children.Add(topPanel);
        var top = Input(topPanel, "Valeurs visibles · de 3 à 30", widget.TopCount.ToString(), 2);
        Help(top, "Nombre de valeurs visibles", "Choisir combien de barres ou valeurs afficher d’abord. La liste complète du widget reste accessible avec recherche et pagination.");
        var descriptions = Check(appearance, "Afficher les explications", widget.ShowDescription);

        var gaugePanel = new StackPanel { Margin = new Thickness(0, 13, 0, 0) }; appearance.Children.Add(gaugePanel);
        gaugePanel.Children.Add(Text("Objectif et seuils de la jauge", 13, weight: FontWeights.SemiBold));
        var gaugeUnit = Text("", 11, "MutedBrush", margin: new Thickness(0, 5, 0, 0)); gaugePanel.Children.Add(gaugeUnit);
        var target = Input(gaugePanel, "Objectif · nombre strictement positif", widget.TargetValue?.ToString("G", CultureInfo.CurrentCulture) ?? "", 40);
        Help(target, "Objectif de cette jauge", "Exprimer l’objectif dans la même unité que l’indicateur. Laisser vide conserve une jauge à paramétrer. Ce repère de présentation ne modifie pas le calcul des tickets ou des SLA.");
        var direction = Select(gaugePanel, "Sens souhaité", new[] { new Choice("lower", "Plus bas est préférable"), new Choice("higher", "Plus haut est préférable") }, widget.LowerIsBetter ? "lower" : "higher");
        var warning = Input(gaugePanel, "Seuil de vigilance · facultatif", widget.WarningValue?.ToString("G", CultureInfo.CurrentCulture) ?? "", 40);
        var critical = Input(gaugePanel, "Seuil critique · facultatif", widget.CriticalValue?.ToString("G", CultureInfo.CurrentCulture) ?? "", 40);
        Help(warning, "Seuil de vigilance de cette jauge", "Déclencher la couleur de vigilance quand la valeur atteint ou dépasse ce seuil si plus bas est préférable, ou atteint ce seuil en baissant si plus haut est préférable.");
        Help(critical, "Seuil critique de cette jauge", "Le seuil critique prime sur la vigilance. Il doit être au-dessus du seuil de vigilance si plus bas est préférable, et au-dessous si plus haut est préférable.");
        gaugePanel.Children.Add(Text("Les nombres acceptent la virgule ou le point. La jauge est bornée graphiquement ; la valeur et le rapport à l’objectif restent affichés. La hauteur automatique s’adapte au contenu.", 11, "MutedBrush", margin: new Thickness(0, 10, 0, 0)));

        var sizing = Section(body, "Taille et position", "La largeur s’adapte à la fenêtre ; vos dimensions préférées restent mémorisées.", false);
        var span = Select(sizing, "Largeur de la carte", Enumerable.Range(3, 10).Select(value => new Choice(value.ToString(), $"{100d * value / 12:0.#} % · {value}/12")), widget.Span.ToString());
        var autoHeight = Check(sizing, "Adapter la hauteur au contenu", widget.AutoHeight);
        Help(autoHeight, "Hauteur automatique du widget", "Réduire les espaces vides. Les détails dépliés agrandissent la carte et restent accessibles dans le défilement principal.");
        var height = Input(sizing, "Hauteur minimale manuelle · 160 à 960", widget.Height.ToString(), 3);
        height.IsEnabled = !widget.AutoHeight;
        autoHeight.Checked += (_, _) => height.IsEnabled = false;
        autoHeight.Unchecked += (_, _) => height.IsEnabled = true;
        sizing.Children.Add(Text("En mode manuel, la hauteur choisie reste un minimum. La carte peut grandir pour afficher tout son contenu. La valeur manuelle est conservée lorsque vous revenez en automatique.", 11, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
        var presets = new UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (label, width, pixels) in new[] { ("Petit", 3, 200), ("Moyen", 4, 340), ("Large", 6, 340), ("Pleine largeur", 12, 420) })
        {
            var size = DescribedCommand(label, "Préparer cette largeur et une hauteur minimale manuelle. Appliquer enregistre la nouvelle taille.", () => { span.SelectedValue = width.ToString(); height.Text = pixels.ToString(); autoHeight.IsChecked = false; });
            size.Padding = new Thickness(8, 6, 8, 6); presets.Children.Add(size);
        }
        sizing.Children.Add(presets);
        var moving = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        moving.Children.Add(DescribedCommand("↑ Avant", "Déplacer immédiatement le widget avant le précédent.", () => MoveBy(widget, -1), !widget.Locked && Board.Widgets.IndexOf(widget) > 0));
        moving.Children.Add(DescribedCommand("↓ Après", "Déplacer immédiatement le widget après le suivant.", () => MoveBy(widget, 1), !widget.Locked && Board.Widgets.IndexOf(widget) < Board.Widgets.Count - 1)); sizing.Children.Add(moving);
        var visibility = Section(body, "Visibilité et protection", "Conserver un widget masqué ou empêcher son déplacement.", false);
        var locked = Check(visibility, "Verrouiller la position et la taille", widget.Locked);
        var hidden = Check(visibility, "Masquer ce widget", widget.Hidden);
        var explicitlyUnlocked = !widget.Locked;
        sizing.IsEnabled = !widget.Locked;
        locked.Checked += (_, _) => sizing.IsEnabled = false;
        locked.Unchecked += (_, _) => { explicitlyUnlocked = true; sizing.IsEnabled = true; };

        refreshPreview = () =>
        {
            var style = Selected(styles);
            preview.SetResourceReference(Border.BackgroundProperty, style == "minimal" ? "WindowBrush" : "PanelBrush");
            preview.SetResourceReference(Border.BorderBrushProperty, style == "minimal" ? "WindowBrush" : "LineBrush");
            preview.CornerRadius = new CornerRadius(style == "minimal" ? 0 : 12);
            preview.BorderThickness = style == "accent" ? new Thickness(4, 1, 1, 1) : new Thickness(style == "outline" ? 2 : 1);
            if (style is "accent" or "outline") preview.BorderBrush = Accent(Selected(accents));
            previewTitle.Text = !string.IsNullOrWhiteSpace(title.Text) ? title.Text
                : source?.SelectedItem is Choice selectedSource ? selectedSource.Label : KindLabel(Selected(kind));
            var sourceMetric = source is null ? null : _data?.Metric(Selected(source));
            gaugeUnit.Text = sourceMetric == null ? "L’unité sera celle de l’indicateur choisi après le chargement du relevé."
                : "Unité de l’indicateur : " + sourceMetric.Unit + ". Objectif et seuils s’appliquent uniquement à ce widget.";
        };
        updateOptions = () =>
        {
            var selectedKind = Selected(kind);
            notePanel.Visibility = selectedKind == "note" ? Visibility.Visible : Visibility.Collapsed;
            presentationPanel.Visibility = selectedKind is "distribution" or "metric" or "weather" or "matrix" or "histogram" ? Visibility.Visible : Visibility.Collapsed;
            topPanel.Visibility = selectedKind is "distribution" or "matrix" or "boxplot" ? Visibility.Visible : Visibility.Collapsed;
            hist.Visibility = selectedKind == "histogram" ? Visibility.Visible : Visibility.Collapsed;
            gaugePanel.Visibility = selectedKind == "metric" && Selected(presentation) == "gauge" ? Visibility.Visible : Visibility.Collapsed;
        };
        kind.SelectionChanged += (_, _) => { FillSources(); ConfigurePresentation(); updateOptions(); refreshPreview(); };
        title.TextChanged += (_, _) => refreshPreview(); styles.SelectionChanged += (_, _) => refreshPreview(); accents.SelectionChanged += (_, _) => refreshPreview();
        presentation.SelectionChanged += (_, _) => updateOptions(); updateOptions(); refreshPreview();

        var utility = Section(body, "Autres actions", "La duplication et la suppression peuvent être annulées pendant la session.", false);
        utility.Children.Add(DescribedCommand("Dupliquer ce widget", "Ajouter une copie indépendante du widget avec ses réglages actuels enregistrés.", () => Change(() =>
        {
            var clone = widget.Copy(); clone.Id = DashboardLayoutSettings.NewId(); clone.Locked = false; clone.Hidden = false; clone.Title = PreviewText(WidgetTitle(widget), 170) + " · copie";
            Board.Widgets.Insert(Board.Widgets.IndexOf(widget) + 1, clone); _selectedWidget = clone.Id;
        }), Board.Widgets.Count < 60));
        var remove = DescribedCommand("Supprimer ce widget", "Retirer ce widget de la disposition. Annuler permet de le retrouver pendant la session.", () => Change(() => { Board.Widgets.Remove(widget); _selectedWidget = null; _editorMode = "library"; }), !widget.Locked);
        remove.SetResourceReference(Button.ForegroundProperty, "ErrorBrush"); utility.Children.Add(remove);

        var error = Text("", 12, "ErrorBrush", margin: new Thickness(0, 7, 0, 5)); actions.Children.Add(error);
        var apply = DescribedCommand("Appliquer au widget", "Appliquer les options de ce panneau et enregistrer la disposition.", () =>
        {
            if (!int.TryParse(height.Text, out var h) || h is < 160 or > 960 || !int.TryParse(top.Text, out var n) || n is < 3 or > 30)
            { error.Text = "Vérifiez la hauteur (160 à 960) et les valeurs visibles (3 à 30)."; return; }
            if (!explicitlyUnlocked && locked.IsChecked == true && (h != widget.Height || Selected(span) != widget.Span.ToString() || (autoHeight.IsChecked == true) != widget.AutoHeight))
            { error.Text = "Déverrouillez le widget pour appliquer une nouvelle taille."; return; }
            if ((Selected(kind) is "metric" or "distribution") && (source == null || string.IsNullOrWhiteSpace(Selected(source))))
            { error.Text = "Choisissez une source de données pour ce widget."; return; }
            if (!TryWidgetNumber(target.Text, true, out var targetValue) || !TryWidgetNumber(warning.Text, false, out var warningValue)
                || !TryWidgetNumber(critical.Text, false, out var criticalValue))
            { error.Text = "Objectif : nombre positif, ou vide. Seuils : nombres positifs ou nuls, ou vides. Maximum : 1 000 000 000 000."; return; }
            var lowerIsBetter = Selected(direction) == "lower";
            if (warningValue.HasValue && criticalValue.HasValue && (lowerIsBetter ? criticalValue < warningValue : criticalValue > warningValue))
            { error.Text = lowerIsBetter ? "Le seuil critique doit être supérieur ou égal au seuil de vigilance." : "Le seuil critique doit être inférieur ou égal au seuil de vigilance."; return; }
            List<DashboardScopeFilter> filters; List<double> boundaries;
            try
            {
                filters = readFilters(); DashboardScopeFilter.ValidateList(filters);
                boundaries = hist.Text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x.Trim().Replace(',', '.'), CultureInfo.InvariantCulture)).Distinct().Order().ToList();
                if (boundaries.Count > 30 || boundaries.Any(x => !double.IsFinite(x) || Math.Abs(x) > 1e9)) throw new FormatException("Trente bornes finies au maximum sont autorisées.");
            }
            catch (Exception ex) { error.Text = ex.Message; return; }
            Change(() =>
            {
                widget.Title = title.Text.Trim(); widget.Kind = Selected(kind); widget.SourceKey = source is null ? "" : Selected(source);
                widget.Style = Selected(styles); widget.Accent = Selected(accents); widget.Presentation = Selected(presentation);
                widget.Span = int.Parse(Selected(span), CultureInfo.InvariantCulture); widget.Height = h; widget.AutoHeight = autoHeight.IsChecked == true; widget.TopCount = n; widget.Note = note.Text;
                widget.ShowDescription = descriptions.IsChecked == true; widget.Locked = locked.IsChecked == true; widget.Hidden = hidden.IsChecked == true;
                widget.TargetValue = targetValue; widget.WarningValue = warningValue; widget.CriticalValue = criticalValue; widget.LowerIsBetter = lowerIsBetter;
                widget.Section = sectionName.Text.Trim(); widget.Filters = filters; widget.HistogramBreaks = boundaries;
            });
        });
        apply.SetResourceReference(Button.StyleProperty, "PrimaryButton"); apply.HorizontalContentAlignment = HorizontalAlignment.Center; apply.Margin = new Thickness(0); actions.Children.Add(apply);
    }

    private void BuildBoardsEditor(Panel body)
    {
        body.Children.Add(Text("Vos dispositions", 15, weight: FontWeights.SemiBold, margin: new Thickness(0, 4, 0, 6)));
        body.Children.Add(Text("La disposition globale et la disposition personnelle se règlent indépendamment. Les vues des techniciens utilisent le même modèle personnel ; leurs chiffres gardent leur propre périmètre.", 11, "MutedBrush"));
        var name = Input(body, "Nom de la disposition", Board.Name, 120); var error = Text("", 12, "ErrorBrush");
        body.Children.Add(DescribedCommand("Renommer", "Enregistrer le nouveau nom de la disposition actuelle.", () => { if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = "Choisissez un nom pour cette disposition."; return; } Change(() => Board.Name = name.Text.Trim()); }));
        body.Children.Add(DescribedCommand("Dupliquer cette disposition", "Créer une copie indépendante avec les mêmes widgets et styles.", () => Change(() =>
        {
            var copy = Board.Copy(); copy.Id = DashboardLayoutSettings.NewId(); copy.Name = PreviewText(copy.Name, 110) + " · copie"; foreach (var widget in copy.Widgets) widget.Id = DashboardLayoutSettings.NewId(); _layout.Boards.Add(copy); _layout.ActiveBoardId = copy.Id; _selectedWidget = null;
        }), _layout.Boards.Count < 12));
        body.Children.Add(error);
        body.Children.Add(Text("Partir d’un modèle", 14, weight: FontWeights.SemiBold, margin: new Thickness(0, 16, 0, 5)));
        body.Children.Add(Text("Chaque modèle crée une nouvelle disposition et conserve celles qui existent.", 11, "MutedBrush", margin: new Thickness(0, 0, 0, 12)));
        foreach (var (key, label, description) in new[]
        {
            ("overview", "Pilotage", "Météo, indicateurs clés, tickets prioritaires et qualité."),
            ("personal", "Mon quotidien", "À regarder, charge, échéances et tickets sans modification récente."),
            ("team", "Charge de l’équipe", "Affectations et répartition descriptive de la charge du périmètre."),
            ("deadlines", "Échéances", "Retards, horizon des échéances et durées observées."),
            ("quality", "Qualité des données", "Couverture des champs, valeurs manquantes et dates non exploitables."),
            ("geography", "Villes et territoires", "Villes reconnues, ambiguïtés, charge active et répartition des lieux."),
            ("operations", "Exploitation", "Échéances, affectations, ancienneté et charge."),
            ("analysis", "Analyse", "Délais, activité, urgence, impact, lieux et catégories."),
            ("empty", "Page libre", "Un espace vide à composer depuis le catalogue.")
        })
        {
            var content = new StackPanel(); content.Children.Add(Text(label, 13, weight: FontWeights.SemiBold)); content.Children.Add(Text(description, 11, "MutedBrush", margin: new Thickness(0, 5, 0, 0)));
            var preset = Command(content, () => Change(() => { var board = DashboardLayoutSettings.CreateBoard(label, key); _layout.Boards.Add(board); _layout.ActiveBoardId = board.Id; _selectedWidget = null; }), _layout.Boards.Count < 12);
            preset.Margin = new Thickness(0, 0, 0, 8); preset.Padding = new Thickness(12); Help(preset, "Créer · " + label, "Créer une nouvelle disposition avec ce modèle. Les indicateurs utilisent les données du relevé."); body.Children.Add(preset);
        }
        body.Children.Add(Text($"{_layout.Boards.Count} / 12 dispositions · {Board.Widgets.Count} / 60 widgets dans celle-ci", 11, "MutedBrush", margin: new Thickness(0, 8, 0, 10)));
        var remove = DescribedCommand("Supprimer cette disposition", "Retirer cette disposition. Vous pourrez annuler pendant la session ; au moins une disposition doit rester.", () => Change(() => { var current = Board; _layout.Boards.Remove(current); _layout.ActiveBoardId = _layout.Boards[0].Id; _selectedWidget = null; }), _layout.Boards.Count > 1);
        remove.SetResourceReference(Button.ForegroundProperty, "ErrorBrush"); body.Children.Add(remove);
        body.Children.Add(Text("Réglages > Exporter la configuration transmet les dispositions, widgets et styles. L’identité « C’est moi » reste propre à chaque poste.", 11, "MutedBrush", margin: new Thickness(0, 9, 0, 0)));
    }

    private static bool TryWidgetNumber(string text, bool positive, out double? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var normalized = text.Trim().Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "").Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value > 1_000_000_000_000d || (positive ? value <= 0 : value < 0)) return false;
        result = value; return true;
    }

    private StackPanel Section(Panel parent, string title, string description, bool expanded)
    {
        var content = new StackPanel { Margin = new Thickness(0, 2, 0, 9) };
        var section = new Expander { Header = title, Content = content, IsExpanded = expanded, Margin = new Thickness(0, 12, 0, 0) };
        Help(section, title, description); parent.Children.Add(section); return content;
    }
    private TextBox Input(Panel parent, string label, string value, int maxLength)
    {
        parent.Children.Add(Text(label, 11, "MutedBrush", margin: new Thickness(0, 10, 0, 5)));
        var box = new TextBox { Text = value, MaxLength = maxLength }; Help(box, label, label); parent.Children.Add(box); return box;
    }
    private ComboBox Select(Panel parent, string label, IEnumerable<Choice> choices, string selected)
    {
        parent.Children.Add(Text(label, 11, "MutedBrush", margin: new Thickness(0, 10, 0, 5)));
        var array = choices.ToArray(); var box = new ComboBox { ItemsSource = array, DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Key), SelectedValue = selected };
        if (box.SelectedIndex < 0 && array.Length > 0) box.SelectedIndex = 0;
        Help(box, label, label); parent.Children.Add(box); return box;
    }
    private static string Selected(ComboBox box) => (box.SelectedItem as Choice)?.Key ?? "";
    private CheckBox Check(Panel parent, string label, bool value)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 12, 0, 0) };
        Help(box, label, label); parent.Children.Add(box); return box;
    }
}
