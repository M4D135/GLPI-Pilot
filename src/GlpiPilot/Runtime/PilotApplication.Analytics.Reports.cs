using System.Globalization;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record MeetingPreview(string Token, string RunId, string[] WidgetIds, string PresentationFingerprint);
    private MeetingPreview? _meetingPreview;

    private static DashboardWidgetSettings[] AnalyticsReportSelection(AnalyticsRun run, JsonElement args)
    {
        if (!args.TryGetProperty("selectedWidgetIds", out var values) || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() is < 1 or > 120) throw new InvalidDataException("Choisissez au moins un bloc du tableau actif pour le rapport.");
        if (args.TryGetProperty("includeOverview", out var overview) && overview.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Les chiffres clés doivent être choisis comme blocs ; aucun bilan supplémentaire n’est ajouté au rapport.");
        var visible = run.Board.Widgets.Where(widget => !widget.Hidden && !run.Board.Sections.Any(section => section.Hidden && section.Key == widget.Section))
            .ToDictionary(widget => widget.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal); var result = new List<DashboardWidgetSettings>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 96 } id
                || !seen.Add(id) || !visible.TryGetValue(id, out var widget) || !run.Widgets.ContainsKey(id) && !run.UnavailableWidgets.ContainsKey(id))
                throw new InvalidDataException("La sélection contient un bloc absent, masqué ou répété. Reprenez la sélection sur le tableau actif.");
            result.Add(widget);
        }
        return result.ToArray();
    }

    private async Task<object?> AnalyticsPreviewReportAsync(JsonElement args, CancellationToken ct)
    {
        var run = RequireAnalyticsRun(args); var selection = AnalyticsReportSelection(run, args);
        var presentation = AnalyticsReportPresentation(run, selection, args);
        if (!run.HistoryLoaded && selection.Any(widget => widget.Kind == "history" || widget.Kind == "metric" && widget.ShowMiniHistory))
            await AnalyticsDashboardHistoryAsync(args, ct);
        RequireAnalyticsRun(args); ct.ThrowIfCancellationRequested();
        var report = AnalyticsMeetingReport(run, selection, ct) with { Presentation = presentation };
        var preview = new MeetingPreview(Guid.NewGuid().ToString("N"), run.Id, selection.Select(widget => widget.Id).ToArray(), AnalyticsPresentationFingerprint(presentation));
        _meetingPreview = preview;
        return new { selectionToken = preview.Token, runId = run.Id, revision = run.Revision, report.Title, report.Context,
            report.ExtractedAt, report.GeneratedAt, report.Warnings, widgets = report.Sections, presentation = report.Presentation };
    }

    private void AnalyticsRequireReportPreview(AnalyticsRun run, IReadOnlyList<DashboardWidgetSettings> selection, JsonElement args)
    {
        if (_meetingPreview is not { } preview || preview.RunId != run.Id || preview.Token != Text(args, "selectionToken", 96)
            || !preview.WidgetIds.SequenceEqual(selection.Select(widget => widget.Id), StringComparer.Ordinal)
            || preview.PresentationFingerprint != AnalyticsPresentationFingerprint(AnalyticsReportPresentation(run, selection, args)))
            throw new InvalidOperationException("Préparez l’aperçu de cette sélection avant son export PDF.");
    }

    private DashboardMeetingReport AnalyticsMeetingReport(AnalyticsRun run, IReadOnlyList<DashboardWidgetSettings> selection, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var source = run.IsLocalFileTest ? "Démonstration — données fictives" : run.IsFileSource ? "Fichier CSV / Excel" : IsHistorical ? "Archive du relevé" : "Collecte GLPI";
        if (IsCachedSource) source = "Cache local hors connexion · " + (run.Source.Provenance.SourceKind == "file" ? "Fichier CSV / Excel" : "Collecte GLPI");
        var origin = run.IsFileSource ? run.Source.Title : AppPaths.SafeUrl(run.Source.SourceUrl);
        var age = now - run.Source.ExtractedAt;
        var context = new List<KeyValuePair<string, string>>
        {
            new("Tableau", run.Board.Name), new("Source de données", source), new("Origine", string.IsNullOrWhiteSpace(origin) ? "Non renseignée" : origin),
            new("Date du relevé", run.Source.ExtractedAt.ToString("dd/MM/yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture)),
            new("Calcul des indicateurs", run.Current.CalculatedAt.ToString("dd/MM/yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture)),
            new("Export", now.ToString("dd/MM/yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture)),
            new("Fraîcheur", age < TimeSpan.Zero ? "Date du relevé future — à vérifier" : age.TotalHours < 24 ? Math.Floor(age.TotalHours) + " h depuis le relevé" : Math.Floor(age.TotalDays) + " jours depuis le relevé"),
            new("Recherche / contexte source", run.Source.Scope),
            new("Périmètre personnel", run.TechnicianKey.Length == 0 ? "Aucun filtre personnel" : run.TechnicianKey),
            new("Filtres communs", string.IsNullOrWhiteSpace(AnalyticsDescribe(run.Common)) ? "Aucun" : AnalyticsDescribe(run.Common)),
            new("Exhaustivité", run.IsFileSource ? "Contenu du fichier importé seulement ; exhaustivité GLPI non vérifiée. Détails des tickets indisponibles."
                : run.IsLocalFileTest ? "Données fictives de démonstration, sans conclusion sur la production."
                : run.Source.Complete ? "Relevé source complet confirmé" : "Relevé incomplet ou exhaustivité non confirmée"),
            new("Sélection", selection.Count + " blocs, dans l’ordre choisi. Tableaux complets des blocs sélectionnés, sans les autres analyses ni annexe générale des tickets.")
        };
        if (IsCachedSource)
        {
            context.Insert(4, new("Copie conservée le", _activeCachedEntry?.SavedAt.ToString("dd/MM/yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "Non renseignée"));
            context.Insert(5, new("Consultation hors connexion", "Relevé conservé sur ce poste, sans actualisation des données pendant cette consultation."));
            context.Insert(6, new("Récupération du cache", _cachedRecovered ? "Copie précédente vérifiée restaurée après une copie récente illisible." : "Dernière copie vérifiée."));
        }
        var warnings = run.Current.Warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).ToList();
        if (run.IsFileSource || run.IsLocalFileTest || !run.Source.Complete)
            warnings.Add(context.First(pair => pair.Key == "Exhaustivité").Value);
        if (age < TimeSpan.Zero) warnings.Add("La date du relevé est future ; sa fraîcheur doit être vérifiée.");
        else if (!IsHistorical && run.Board.FreshnessWarningHours > 0 && age.TotalHours > run.Board.FreshnessWarningHours)
            warnings.Add("Relevé à actualiser : " + Math.Floor(age.TotalHours) + " h depuis l’observation. Les volumes et couleurs restent ceux du relevé.");
        if (IsCachedSource) warnings.Add("Consultation hors connexion : le relevé conservé n’a pas été actualisé.");
        warnings = warnings.Distinct(StringComparer.Ordinal).ToList();
        if (warnings.Count > 0) context.Add(new("Réserves du calcul", string.Join("\n", warnings)));
        var sections = new List<DashboardReportSection>();
        foreach (var widget in selection)
        {
            if (run.UnavailableWidgets.TryGetValue(widget.Id, out var reason))
            {
                ct.ThrowIfCancellationRequested();
                sections.Add(new() { Id = widget.Id, Title = string.IsNullOrWhiteSpace(widget.Title) ? widget.SourceKey : widget.Title,
                    Kind = widget.Kind, Scope = "Périmètre indisponible", Filters = AnalyticsDescribe(run.Common.Concat(widget.Filters)),
                    Notes = { "Indisponible : " + reason } });
                continue;
            }
            ct.ThrowIfCancellationRequested(); var snapshot = run.Widgets[widget.Id];
            var filters = AnalyticsDescribe(run.Common.Concat(widget.Filters));
            var section = new DashboardReportSection { Id = widget.Id, Title = string.IsNullOrWhiteSpace(widget.Title) ? widget.SourceKey : widget.Title,
                Kind = widget.Kind, Scope = snapshot.ScopeLabel + " · " + snapshot.RowCount.ToString("N0", CultureInfo.CurrentCulture) + " " + snapshot.Grain,
                Filters = filters.Length == 0 ? "Aucun" : filters };
            sections.Add(section);
            static string V(object? value) => value switch { null => "Indisponible", double number => double.IsFinite(number) ? number.ToString("0.###", CultureInfo.InvariantCulture) : "Indisponible",
                DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
            void Table(string title, string[] headers, IEnumerable<object?[]> values)
            {
                var rows = new List<string[]>();
                foreach (var row in values) { ct.ThrowIfCancellationRequested(); rows.Add(row.Select(V).ToArray()); }
                section.Tables.Add(new(title, headers, rows));
            }
            void Note(string text) { if (!string.IsNullOrWhiteSpace(text)) section.Notes.Add(text); }
            void Missing(string reason = "Les données nécessaires à ce bloc sont indisponibles dans ce périmètre.") => Note("Indisponible : " + reason);
            void Distribution(DashboardDistribution? distribution)
            {
                if (distribution is null) { Missing(); return; }
                Note(distribution.Note); if (!distribution.Available) { Missing(); return; }
                var buckets = widget.Presentation == "pareto" ? distribution.Buckets.OrderByDescending(bucket => bucket.IsAggregate ? bucket.Value ?? double.NegativeInfinity : bucket.Count).ToArray() : distribution.Buckets.ToArray();
                section.Chart.AddRange(buckets.Select(bucket => new DashboardReportPoint(bucket.Label, bucket.IsAggregate ? bucket.Value : bucket.Count)));
                Table(distribution.Title, new[] { "Catégorie", "Résultat", "Unité", "Lignes", "Échantillon / dénominateur" },
                    buckets.Select(bucket => new object?[] { bucket.Label, bucket.IsAggregate ? bucket.Value : bucket.Count, bucket.IsAggregate ? bucket.Unit : snapshot.Grain, bucket.Count, bucket.Sample ?? distribution.Denominator }));
            }
            void History(DashboardHistorySeries? history)
            {
                if (history is null || history.Points.Count == 0) { Note("Historique : aucune observation disponible pour ce bloc."); return; }
                Note(history.Explanation);
                Table("Observations historiques · " + history.Title, new[] { "Date", "Valeur", "Unité", "Lignes", "Comparabilité", "Note" },
                    history.Points.Select(point => new object?[] { point.CapturedAt, point.Comparable && point.Complete ? point.Value : null, history.Unit, point.Rows,
                        point.Comparable && point.Complete ? "Comparable" : "Non comparable / incomplet", point.Note }));
            }
            switch (widget.Kind)
            {
                case "metric":
                    if (snapshot.Metric(widget.SourceKey) is not { } metric) { Missing(); break; }
                    section.MetricValue = V(metric.Value); section.Unit = metric.Unit; Note(metric.Explanation);
                    Table(metric.Label, new[] { "Indicateur", "Valeur", "Unité", "Échantillon" }, new[] { new object?[] { metric.Label, metric.Value, metric.Unit, metric.Sample } });
                    if (widget.Presentation == "weather-volume")
                    {
                        var weather = AnalyticsEvaluateStockWeather(widget, metric.Value, AnalyticsVolumeWeatherReliable(run, snapshot, widget), run.IsLocalFileTest,
                            AnalyticsWeatherOverride(run, widget.Id));
                        Note("Météo de stock : " + weather.Title + ". " + weather.Explanation);
                        if (run.StockWeather?.Contexts.GetValueOrDefault(widget.Id) is { } weatherContext) Note(weatherContext.Reason);
                        if (run.SelectionWeather.GetValueOrDefault(widget.Id)?.Evaluation is { Unit: "percent" } percentage)
                            Table("Calcul du pourcentage", new[] { "Numérateur", "Dénominateur", "Base", "Pourcentage" },
                                new[] { new object?[] { percentage.Numerator, percentage.Denominator, percentage.DenominatorLabel, percentage.EvaluatedValue } });
                    }
                    if (widget.ShowMiniHistory && run.HistoryLoaded)
                    {
                        var history = snapshot.History.FirstOrDefault(series => series.Key == widget.SourceKey);
                        if (history is { Points.Count: > 0 }) { var end = history.Points.Max(point => point.CapturedAt); History(history with { Points = history.Points.Where(point => point.CapturedAt >= end.AddDays(-2 * widget.HistoryPeriodDays)).ToArray() }); }
                    }
                    break;
                case "distribution": Distribution(snapshot.Distributions.FirstOrDefault(distribution => distribution.Key == widget.SourceKey)); break;
                case "histogram": Distribution(snapshot.NumericSeries.FirstOrDefault(series => series.Key == widget.SourceKey) is { } numeric ? DashboardService.Histogram(numeric, widget.HistogramBreaks) : null); break;
                case "weather":
                    var weatherProfile = snapshot.WeatherProfiles.GetValueOrDefault(widget.SourceKey) ?? snapshot.Weather;
                    section.MetricValue = V(weatherProfile.Score); section.Unit = "/100"; Note(weatherProfile.Title + ". " + weatherProfile.Description);
                    Table("Signaux et dénominateurs", new[] { "Signal", "Volume", "Échantillon", "Population", "Pondération", "Méthode" }, weatherProfile.Signals.Select(signal => new object?[] { signal.Label, signal.Count, signal.Sample, signal.Population, signal.Weight, signal.Explanation })); break;
                case "timeline":
                    Note("Événements datés présents dans ce relevé uniquement, sans reconstitution d’un historique global.");
                    bool DateMeasured(string role) => snapshot.DateValues.GetValueOrDefault(role)?.Count > 0
                        || snapshot.RowCount == 0 && snapshot.Column(role)?.Index is not null;
                    Table("Événements par jour", new[] { "Date", "Créations", "Résolutions", "Clôtures" }, snapshot.Timeline.Select(day => new object?[] { day.Date.ToString("yyyy-MM-dd"),
                        DateMeasured("created") ? day.Created.Length : null, DateMeasured("resolved") ? day.Resolved.Length : null, DateMeasured("closed") ? day.Closed.Length : null })); break;
                case "attention":
                    if (snapshot.Column("status")?.Index is null) { Missing("Une colonne Statut reconnue est nécessaire."); break; }
                    Table("Tickets à examiner", new[] { "ID", "Titre", "Statut", "Priorité", "Groupe", "Âge (jours)", "Motifs" }, snapshot.Attention.Select(ticket => new object?[] { ticket.Id, ticket.Title, ticket.Status, ticket.Priority, ticket.Group, ticket.AgeDays, ticket.Reasons })); break;
                case "note": Note(widget.Note); break;
                case "quality":
                    foreach (var warning in snapshot.Warnings) Note(warning);
                    Table("Correspondances des colonnes", new[] { "Rôle", "Colonne", "État" }, snapshot.Columns.Select(column => new object?[] { column.Label, column.Header, column.StateLabel })); break;
                case "matrix":
                    if (snapshot.Matrices.FirstOrDefault(matrix => matrix.Key == widget.SourceKey) is not { } matrix || !matrix.Available) { Missing(); break; }
                    Note(matrix.Explanation);
                    Table(matrix.Title, new[] { "Ligne", "Colonne", "Valeur", "Unité", "Lignes source" }, matrix.Cells.Select(cell => new object?[] { cell.RowLabel, cell.ColumnLabel, cell.Value, matrix.Unit, cell.Rows.Length })); break;
                case "boxplot":
                    if (snapshot.BoxSeries.FirstOrDefault(series => series.Key == widget.SourceKey) is not { } boxes || !boxes.Available) { Missing(); break; }
                    Note(boxes.Explanation + " Unité : " + boxes.Unit);
                    Table(boxes.Title, new[] { "Catégorie", "Échantillon", "Minimum", "Q1", "Médiane", "Q3", "P90", "Maximum" }, boxes.Boxes.Select(box => new object?[] { box.Label, box.Sample, box.Minimum, box.FirstQuartile, box.Median, box.ThirdQuartile, box.P90, box.Maximum })); break;
                case "scatter":
                    if (snapshot.Scatters.FirstOrDefault(series => series.Key == widget.SourceKey) is not { Points.Count: > 0 } scatter) { Missing(); break; }
                    Note(scatter.Explanation);
                    Table(scatter.Title, new[] { "Point", scatter.XLabel, scatter.YLabel }, scatter.Points.Select(point => new object?[] { point.Label, point.X, point.Y })); break;
                case "history": History(snapshot.History.FirstOrDefault(history => history.Key == widget.SourceKey)); break;
                case "map":
                    if (snapshot.Geography.Count == 0) { Missing("Aucun lieu du relevé ne dispose de coordonnées utilisables."); break; }
                    Note("Coordonnées et volumes observés dans le relevé ; aucun fond de carte distant nécessaire.");
                    Table("Sites géolocalisés", new[] { "Site", "Latitude", "Longitude", "Lignes", "Actifs" }, snapshot.Geography.Select(point => new object?[] { point.Label, point.Latitude, point.Longitude, point.Rows.Length, point.ActiveRows.Length })); break;
                case "participation":
                    var participation = AnalyticsParticipation(snapshot, widget); Note(participation.Explanation);
                    foreach (var warning in participation.Warnings) Note(warning);
                    if (!participation.Available || !participation.StatusAvailable
                        || widget.SourceKey == "authors" && !participation.FollowupsAvailable && !participation.TasksAvailable) { Missing(); break; }
                    var people = AnalyticsParticipants(participation, widget).ToArray();
                    if (widget.SourceKey == "authors")
                        Table("Participation des rédacteurs", new[] { "Personne", "Suivis", "Tâches", "Les deux", "Rédacteurs · union" }, people.Select(person => new object?[] { person.Name, person.Followups.Count, person.Tasks.Count, person.Both.Count, person.Writers.Count }));
                    else Table("Rédaction et attribution", new[] { "Personne", "Rédacteurs · union", "Attribution" }, people.Select(person => new object?[] { person.Name, person.Writers.Count, person.Assigned.Count }));
                    section.Chart.AddRange(people.Select(person => new DashboardReportPoint(person.Name, person.Writers.Count))); break;
                case "oldest":
                    if (snapshot.OldestGroups is not { Available: true } oldest) { Missing(snapshot.OldestGroups?.UnavailableReason ?? "Dates et statuts nécessaires."); break; }
                    Note(oldest.Explanation); var groups = AnalyticsOldestGroups(oldest, widget); var top = widget.SourceKey == "tickets" ? 10 : 5;
                    Note("Classement métier : " + top + " plus anciennes ouvertures par " + (widget.SourceKey == "tickets" ? "périmètre" : "groupe") + ". Tous les groupes sont inclus, au-delà de la page affichée. Jour de référence : " + oldest.ReferenceDate.ToString("yyyy-MM-dd"));
                    Table("Stocks et qualité des dates", new[] { "Groupe", "Stock", "Dates classables", "Absentes", "Invalides", "Futures", "Âge maximal (j)" }, groups.Select(group => new object?[] { group.Label, group.Count, group.DatedCount, group.MissingDateCount, group.InvalidDateCount, group.FutureDateCount, group.MaximumAgeDays }));
                    foreach (var group in groups) Table(group.Label + " · plus anciens", new[] { "Rang", "ID", "Titre", "Ouverture", "Âge (j)", "Statut", "Priorité", "Technicien" }, group.Oldest.Take(top).Select((ticket, index) => new object?[] { index + 1, ticket.Id, ticket.Title, ticket.OpenedAt, ticket.AgeDays, ticket.Status, ticket.Priority, ticket.Assignee })); break;
                case "sites":
                    var sites = snapshot.SiteWorkload;
                    if (!sites.Available) { Missing(sites.UnavailableReason); break; }
                    Note(sites.Explanation); var threshold = widget.WarningValue is >= 1 ? widget.WarningValue.Value : 4;
                    Table("Stock actif par site", new[] { "Site", "Repérage", "Stock", "Conseil" }, sites.Sites.Select(site => new object?[] { site.Label, site.Unknown ? "À vérifier" : "Site référencé", site.Rows.Length,
                        site.Rows.Length == 0 ? "Aucun ticket" : site.Unknown ? "À vérifier" : site.Rows.Length >= threshold ? "Intervention à planifier" : "À suivre" }));
                    var idColumn = snapshot.Column("id")?.Index; var titleColumn = snapshot.Column("title")?.Index;
                    string Cell(int row, int? column) => column is int index && index < run.Source.Rows[row].Length ? run.Source.Rows[row][index] : "";
                    Table("Tickets des sites sélectionnés", new[] { "Site", "ID", "Titre" }, sites.Sites.SelectMany(site => site.Rows.Select(row => new object?[] { site.Label, Cell(row, idColumn), Cell(row, titleColumn) })));
                    section.Chart.AddRange(sites.Sites.Select(site => new DashboardReportPoint(site.Label, site.Rows.Length))); break;
                default: throw new InvalidDataException("Type de bloc non pris en charge pour le rapport : " + widget.Kind);
            }
        }
        return new DashboardMeetingReport("Réunion · " + run.Board.Name, now, run.Source.ExtractedAt, context, sections) { Warnings = warnings };
    }
}
