using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class ScheduledTourView
{
    private readonly TextBlock _stateLabel = Label("État : Brouillon", 13, "TextBrush", FontWeights.SemiBold);
    private readonly TextBox _stateReason = new() { MinWidth = 260, MaxLength = 1000, TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _stateHistory = Table(true), _weatherGrid = Table(true);
    private readonly TextBox _distributionDays = new() { Text = "5", Width = 95, MaxLength = 2 };
    private readonly TextBox _routingEndpoint = new() { MinWidth = 300, MaxWidth = 650, MaxLength = 1000 }, _weatherEndpoint = new() { MinWidth = 300, MaxWidth = 650, MaxLength = 1000 };
    private readonly TextBox _routingProfile = new() { Text = "driving", Width = 150, MaxLength = 40 };
    private readonly CheckBox _allowCoordinates = new() { Content = "J’autorise l’envoi des coordonnées et des dates aux adresses saisies pour cette session.", Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _terrainStatus = Label("Aucune transmission automatique. Choisissez un service autorisé, puis lancez le calcul souhaité.", 12, "MutedBrush");
    private readonly Button _terrainCancel = new() { Content = "Annuler le calcul en cours", Margin = new Thickness(0, 0, 8, 5), Padding = new Thickness(11, 7, 11, 7), IsEnabled = false };
    private string TerrainSettingsPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path))!, "terrain-services.json");

    private FrameworkElement BuildLifecycle()
    {
        var panel = new StackPanel(); panel.Children.Add(_stateLabel);
        panel.Children.Add(Label("Une tournée prête doit être rapprochée, localisée, affectée et compatible avec les horaires déclarés. Toute modification remet la tournée en brouillon. Une visite future ne peut pas être déclarée effectuée.", 12, "MutedBrush"));
        panel.Children.Add(Field("Motif du changement d’état", _stateReason));
        var actions = new WrapPanel();
        actions.Children.Add(Command("Valider : prête", () => ChangeState("ready")));
        actions.Children.Add(Command("Déclarer effectuée", () => ChangeState("done")));
        actions.Children.Add(Command("Revenir au brouillon", () => ChangeState("draft"))); panel.Children.Add(actions);
        Column(_stateHistory, "Date", "At", 165, "dd/MM/yyyy HH:mm"); Column(_stateHistory, "De", "From", 95); Column(_stateHistory, "Vers", "To", 95); Column(_stateHistory, "Motif", "Reason", 430);
        _stateHistory.Height = 125; panel.Children.Add(new Expander { Header = "Historique des états", Content = _stateHistory }); return panel;
    }

    private void ChangeState(string target)
    {
        if (_context == null) return;
        var draft = ReadDraft(); RefreshDeclaredAvailability(draft);
        var updated = ScheduledTourService.Transition(draft, _context, target, _stateReason.Text);
        LoadTour(updated, true, true); SaveDraft(); _stateReason.Clear(); QueueCalculation();
    }

    private FrameworkElement BuildFieldDetails()
    {
        var panel = new StackPanel();
        panel.Children.Add(Label("Les contacts et le matériel reconnus dans les colonnes du relevé et le référentiel sont recueillis à la création. Vérifiez et complétez cette fiche avant le départ ; une valeur absente reste à renseigner.", 11, "MutedBrush"));
        foreach (var (label, property, limit) in new[] { ("Compétences requises · séparées par ;", nameof(ScheduledTourStop.SkillsText), 2000), ("Contacts sur place", nameof(ScheduledTourStop.Contact), 2000), ("Matériel à prévoir", nameof(ScheduledTourStop.Equipment), 2000) })
        {
            var input = new TextBox { MaxLength = limit, AcceptsReturn = property != nameof(ScheduledTourStop.SkillsText), TextWrapping = TextWrapping.Wrap, MinHeight = 27, MaxHeight = 75 };
            input.SetBinding(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
            input.TextChanged += (_, _) => Changed(); panel.Children.Add(Field(label, input));
        }
        return new Expander { Header = "Fiche terrain : compétences, contacts et matériel", Content = panel };
    }

    private void AllocateAcrossDays()
    {
        if (_context == null) return;
        var keys = _distributionTechnicians.SelectedItems.OfType<TourTechnicianChoice>().Select(item => item.Key).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length is < 1 or > 32) throw new InvalidDataException("Choisissez entre 1 et 32 techniciens pour la répartition.");
        if (!int.TryParse(_distributionDays.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days is < 1 or > 14) throw new InvalidDataException("La répartition couvre de 1 à 14 jours à partir de la date de la tournée.");
        var draft = ReadDraft(); var context = _context; var generation = BeginOperation(); var ct = _operation!.Token;
        Status("Recherche de créneaux selon les capacités, absences et compétences déclarées…");
        _work = AllocateAsync(draft, context, keys, days, generation, ct);
    }

    private async Task AllocateAsync(ScheduledTour draft, TourPlanningContext context, string[] keys, int days, long generation, CancellationToken ct)
    {
        try
        {
            var result = await Task.Run(() =>
            {
                var preferences = TechnicianPreferences.Load(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path))!, "technician-preferences.json"));
                var first = DateTime.ParseExact(draft.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture); var available = new List<TourTechnicianAvailability>();
                foreach (var key in keys)
                {
                    var preference = preferences.Get(key);
                    for (var offset = 0; offset < days; offset++)
                    {
                        ct.ThrowIfCancellationRequested(); var day = first.AddDays(offset);
                        available.Add(DeclaredAvailability(preference, key, day));
                    }
                }
                var allocation = ScheduledTourService.DistributeAcrossDays(draft, context, available, ct);
                return (Allocation: allocation, Schedule: ScheduledTourService.Calculate(allocation.Tour, context, ct), Unknown: available.Count(item => !item.CapacityMinutes.HasValue));
            }, ct);
            if (!Current(generation, ct)) return;
            LoadTour(result.Allocation.Tour, true); PublishSchedule(result.Schedule);
            _compareSummary.Text = $"{result.Allocation.AssignedStops} étapes affectées ; {result.Allocation.Unassigned.Count} étapes sans créneau compatible. {result.Unknown} capacités journalières non déclarées, exclues du calcul.\n"
                + string.Join("\n", result.Allocation.Unassigned.Take(20).Select(item => item.Place + " : " + item.Reason))
                + (result.Allocation.Unassigned.Count > 20 ? "\nLa liste des étapes permet de retrouver toutes les affectations manquantes." : "");
            Status("Proposition calculée. Vérifiez les étapes, les échéances et les trajets, puis enregistrez la tournée.", result.Allocation.Unassigned.Count > 0);
        }
        catch (OperationCanceledException) { if (generation == _generation) Status("Répartition annulée ; le brouillon précédent est conservé."); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        { if (Current(generation, ct)) Status("Répartition impossible : " + error.Message, true); }
        finally { FinishOperation(generation); }
    }

    private static TourTechnicianAvailability DeclaredAvailability(TechnicianPreference preference, string key, DateTime day)
    {
        var exception = preference.CapacityDays.FirstOrDefault(value => value.Date.Date == day.Date);
        return new(key, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), TechnicianPlanningService.CapacityForDate(preference, day), TechnicianPlanningService.IsUnavailable(preference, day),
            preference.Skills.ToArray(), preference.PrimaryTerritories.Concat(preference.BackupTerritories).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            MorningCapacityMinutes = TechnicianPlanningService.CapacityForDate(preference, day, "morning"),
            AfternoonCapacityMinutes = TechnicianPlanningService.CapacityForDate(preference, day, "afternoon"),
            WorkWindows = preference.WorkPeriods.Count == 0 ? null : preference.WorkPeriods.Where(value => value.Day == day.DayOfWeek)
                .Select(value => new TourWorkWindow(value.StartMinute, value.EndMinute,
                    value.StartMinute < 720 && exception?.MorningMinutes.HasValue == true || value.EndMinute > 720 && exception?.AfternoonMinutes.HasValue == true ? null : value.CapacityMinutes)).ToArray()
        };
    }

    private void RefreshDeclaredAvailability(ScheduledTour draft)
    {
        var preferences = TechnicianPreferences.Load(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path))!, "technician-preferences.json"));
        draft.Availability = draft.Stops.Select(stop => (Key: stop.TechnicianKey.Length == 0 ? draft.TechnicianKey : stop.TechnicianKey, Day: stop.Date.Length == 0 ? draft.Date : stop.Date))
            .Where(item => item.Key.Length > 0).Distinct().Select(item => DeclaredAvailability(preferences.Get(item.Key), item.Key, DateTime.ParseExact(item.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture))).ToList();
    }

    private void BuildTerrainServices()
    {
        var page = Page(); var header = new StackPanel();
        header.Children.Add(Label("Routes et météo · services facultatifs", 18, "TextBrush", FontWeights.SemiBold));
        header.Children.Add(Label("Routage : adresse de base d’un serveur compatible OSRM v5, par exemple votre instance interne. Météo : adresse complète d’un point d’accès de prévisions quotidiennes compatible Open-Meteo. Les adresses restent vides par défaut. Aucun titre, identifiant de ticket, nom de technicien ou identifiant GLPI n’est envoyé.", 12, "MutedBrush"));
        var routing = new WrapPanel(); routing.Children.Add(Field("Adresse du serveur routier", _routingEndpoint)); routing.Children.Add(Field("Profil du serveur", _routingProfile)); header.Children.Add(routing);
        header.Children.Add(Field("Adresse du point d’accès météo", _weatherEndpoint)); header.Children.Add(_allowCoordinates);
        var actions = new WrapPanel(); actions.Children.Add(Command("Enregistrer les adresses", () => { ReadServiceSettings().Save(TerrainSettingsPath); _terrainStatus.Text = "Adresses enregistrées localement. L’autorisation de transmission n’est pas mémorisée."; }));
        actions.Children.Add(Command("Calculer les routes", () => RunProvider(true))); actions.Children.Add(Command("Charger la météo des visites", () => RunProvider(false)));
        actions.Children.Add(_terrainCancel); _terrainCancel.Click += (_, _) => { _operation?.Cancel(); _terrainStatus.Text = "Annulation demandée…"; }; header.Children.Add(actions);
        header.Children.Add(_terrainStatus); header.Children.Add(Label("Les routes correspondent à l’ordre, aux coordonnées, aux dates et aux techniciens du calcul. Après modification, les segments périmés sont ignorés. Les prévisions dépendent de l’horizon du fournisseur ; une absence de réponse reste une information manquante. Les résultats reçus sont inclus dans la tournée lorsque vous l’enregistrez.", 12, "MutedBrush"));
        page.Children.Add(header);
        Column(_weatherGrid, "Date de visite", "Day", 110); Column(_weatherGrid, "Lieu", "Place", 220); Column(_weatherGrid, "Code WMO", "WeatherCode", 90); Column(_weatherGrid, "Min · °C", "MinimumCelsius", 85, "N1"); Column(_weatherGrid, "Max · °C", "MaximumCelsius", 85, "N1"); Column(_weatherGrid, "Pluie · %", "RainProbabilityPercent", 90, "N0"); Column(_weatherGrid, "Reçu le", "RetrievedAt", 160, "dd/MM/yyyy HH:mm"); Column(_weatherGrid, "Fournisseur", "Provider", 290);
        Grid.SetRow(_weatherGrid, 1); page.Children.Add(_weatherGrid); _tabs.Items.Add(new TabItem { Header = "Routes et météo", Content = page });
        try { var settings = TourServiceSettings.Load(TerrainSettingsPath); _routingEndpoint.Text = settings.RoutingEndpoint; _routingProfile.Text = settings.RoutingProfile; _weatherEndpoint.Text = settings.WeatherEndpoint; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { _terrainStatus.Text = "Réglages terrain illisibles ; vérifiez et enregistrez les adresses : " + error.Message; }
        _routingEndpoint.TextChanged += (_, _) => _allowCoordinates.IsChecked = false; _weatherEndpoint.TextChanged += (_, _) => _allowCoordinates.IsChecked = false;
    }

    private TourServiceSettings ReadServiceSettings()
    {
        var settings = new TourServiceSettings { RoutingEndpoint = _routingEndpoint.Text.Trim(), RoutingProfile = _routingProfile.Text.Trim(), WeatherEndpoint = _weatherEndpoint.Text.Trim() }; settings.Validate(); return settings;
    }

    private void RunProvider(bool routing)
    {
        if (_context == null) return;
        if (_allowCoordinates.IsChecked != true) throw new InvalidDataException("Cochez l’autorisation d’envoi des coordonnées et dates aux adresses saisies avant ce calcul.");
        var settings = ReadServiceSettings(); _ = TourExternalServices.Endpoint(routing ? settings.RoutingEndpoint : settings.WeatherEndpoint);
        var draft = ReadDraft(); var context = _context; var generation = BeginOperation(); var ct = _operation!.Token;
        _terrainStatus.Text = routing ? "Calcul des segments routiers auprès du serveur choisi…" : "Lecture des prévisions pour les coordonnées et dates des visites…";
        _work = ProviderAsync(routing, draft, context, settings, generation, ct);
    }

    private async Task ProviderAsync(bool routing, ScheduledTour draft, TourPlanningContext context, TourServiceSettings settings, long generation, CancellationToken ct)
    {
        try
        {
            using var service = new TourExternalServices();
            var result = routing ? await service.RouteAsync(draft, context, settings, true, ct) : await service.WeatherAsync(draft, context, settings, true, ct);
            var schedule = await Task.Run(() => ScheduledTourService.Calculate(result, context, ct), ct);
            if (!Current(generation, ct)) return;
            if (routing && result.State != "draft") { result.StateHistory.Add(new(result.State, "draft", DateTimeOffset.Now, "Recalcul routier du planning")); result.StateHistory = result.StateHistory.TakeLast(100).ToList(); result.State = "draft"; }
            LoadTour(result, true, true); PublishSchedule(schedule);
            _terrainStatus.Text = routing ? $"{result.RoadLegs.Count} segments reçus. Les durées routières sont utilisées dans le planning ; enregistrez pour les conserver."
                : $"{result.Weather.Count} prévisions reçues pour {result.Stops.Count} étapes. Vérifiez les dates et valeurs manquantes ; enregistrez pour les conserver.";
            Status(_terrainStatus.Text);
        }
        catch (OperationCanceledException) { if (generation == _generation) _terrainStatus.Text = ct.IsCancellationRequested ? "Demande annulée ; aucun résultat partiel n’a été appliqué." : "Le délai maximal de réponse du fournisseur est dépassé. Le brouillon est conservé."; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or HttpRequestException or InvalidOperationException or KeyNotFoundException)
        { if (Current(generation, ct)) { _terrainStatus.Text = "Service indisponible ou réponse inexploitable : " + error.Message + " Le brouillon précédent est conservé."; Status(_terrainStatus.Text, true); } }
        finally { FinishOperation(generation); }
    }
}
