using System.Globalization;
using System.Net;
using System.Text;
using GlpiPilot.Core;

internal static class TerrainChecks
{
    private const string Paris = "11111111111111111111111111111111", Lyon = "22222222222222222222222222222222", Nice = "33333333333333333333333333333333", Site = "44444444444444444444444444444444";
    private static readonly DateOnly Day = new(2026, 10, 26);
    private static void Check(bool value, string reason = "Assertion failed") { if (!value) throw new InvalidOperationException(reason); }
    private static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static string Key(string value) => TechnicianDashboardService.IdentityKey(value);
    private static CitySettings Cities() => new()
    {
        Cities = new() { new() { Id = Paris, Name = "Paris", Latitude = 48.85, Longitude = 2.35 }, new() { Id = Lyon, Name = "Lyon", Latitude = 45.76, Longitude = 4.83 }, new() { Id = Nice, Name = "Nice", Latitude = 43.70, Longitude = 7.26 } },
        Sites = new() { new() { Id = Site, CityId = Paris, Name = "Agence centrale", Latitude = 48.86, Longitude = 2.36, Contact = "Accueil", Equipment = "Badge" } }
    };
    private static ExtractionResult Source() => new()
    {
        Headers = new() { "ID", "Titre", "Statut", "Priorité", "Temps de résolution", "Attribué à - Technicien", "Contact", "Matériel" },
        Rows = new() { new[] { "100", "Agence centrale imprimante", "Nouveau", "Haute", "26/10/2026 12:00", "Alice", "Mme Test <contact>", "Câble & écran" },
            new[] { "101", "Lyon réseau", "Nouveau", "Haute", "27/10/2026 12:00", "Bob", "", "Routeur" }, new[] { "102", "Nice réseau", "Nouveau", "Basse", "", "Alice", "", "" } },
        Complete = true, SourceUrl = "https://terrain.example.invalid/glpi/front/ticket.php", ExtractedAt = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero)
    };
    private static TourPlanningContext Context(CitySettings? cities = null, ExtractionResult? source = null)
    {
        cities ??= Cities(); source ??= Source(); var settings = new DashboardSettings(); return TourPlanningService.Prepare(CityDetectionService.Enrich(source, cities, settings), cities, settings, source.ExtractedAt);
    }
    private static ScheduledTour Tour(TourPlanningContext context, params int[] rows)
    {
        var tour = ScheduledTourService.Create(context, rows.Length > 0 ? rows : new[] { 0, 1, 2 }, "Tournée terrain", Day, "Alice"); tour.TimeZoneId = "UTC"; tour.StartTime = "09:00"; tour.EndTime = "17:00";
        tour.Start = new() { Label = "Dépôt local", Latitude = 48.80, Longitude = 2.30 }; foreach (var stop in tour.Stops) stop.TravelMinutes = 0; return tour;
    }
    private static TourTechnicianAvailability Availability(string person, string day, int? minutes = 480, bool absent = false, params string[] skills)
        => new(Key(person), day, minutes, absent, skills, Array.Empty<string>());
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static TourServiceSettings Services() => new() { RoutingEndpoint = "https://route.example.invalid/osrm", WeatherEndpoint = "https://weather.example.invalid/v1/forecast" };
    private static string Routes(int count, int metres = 1200, int seconds = 600) => "{\"code\":\"Ok\",\"routes\":[{\"legs\":[" + string.Join(',', Enumerable.Repeat($"{{\"distance\":{metres},\"duration\":{seconds}}}", count)) + "]}]}";
    private static string Forecast(string date = "2026-10-26", string min = "4", string max = "13", string rain = "60") => $"{{\"daily\":{{\"time\":[\"{date}\"],\"weather_code\":[61],\"temperature_2m_min\":[{min}],\"temperature_2m_max\":[{max}],\"precipitation_probability_max\":[{rain}]}}}}";

    public static void Register(Action<string, Action> test)
    {
        test("Tour lifecycle validates readiness records reasons and guards future completion", () =>
        {
            var context = Context(); var draft = Tour(context, 0); var ready = ScheduledTourService.Transition(draft, context, "ready", "Planning confirmé");
            Check(ready.State == "ready" && draft.State == "draft" && ready.StateHistory.Single().Reason == "Planning confirmé");
            Reject<InvalidDataException>(() => ScheduledTourService.Transition(draft, context, "done", "Terminé"));
            Reject<InvalidDataException>(() => ScheduledTourService.Transition(ready, context, "done", "Terminé", new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero)));
            var done = ScheduledTourService.Transition(ready, context, "done", "Compte rendu saisi", new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
            Check(done.State == "done" && ScheduledTourService.Transition(done, context, "draft", "Reprise").StateHistory.Count == 3);
        });
        test("Tour readiness rejects unassigned missing or impossible visits", () =>
        {
            var context = Context(); var tour = Tour(context, 0); tour.TechnicianKey = ""; tour.Stops[0].TechnicianKey = "";
            Reject<InvalidDataException>(() => ScheduledTourService.Transition(tour, context, "ready", "Vérifié"));
            tour.Stops[0].TechnicianKey = Key("Alice"); tour.Stops[0].Latest = "08:00"; Reject<InvalidDataException>(() => ScheduledTourService.Transition(tour, context, "ready", "Vérifié"));
            tour.Stops[0].Latest = ""; Reject<InvalidDataException>(() => ScheduledTourService.Transition(tour, context, "ready", " "));
        });
        test("Approved edits return to draft with audit and do not mutate saved state", () =>
        {
            var context = Context(); var ready = ScheduledTourService.Transition(Tour(context, 0), context, "ready", "Validée"); var edit = ScheduledTourService.PrepareEdit(ready, "Nouvelle durée");
            Check(edit.State == "draft" && edit.StateHistory.Count == 2 && ready.State == "ready");
            var allocated = ScheduledTourService.DistributeAcrossDays(ready, context, new[] { Availability("Alice", "2026-10-26", 480) });
            Check(allocated.Tour.State == "draft" && allocated.Tour.StateHistory.Count == 2 && ready.State == "ready");
        });
        test("Site recurrent split openings move the full visit beyond lunch", () =>
        {
            var cities = Cities(); cities.Sites[0].Openings = new() { new() { Day = DayOfWeek.Monday, From = "09:00", Until = "12:00" }, new() { Day = DayOfWeek.Monday, From = "14:00", Until = "17:00" } };
            var context = Context(cities); var tour = Tour(context, 0); tour.StartTime = "11:45"; tour.Stops[0].DurationMinutes = 30;
            var visit = ScheduledTourService.Calculate(tour, context).Visits.Single(); Check(visit.Start.Hour == 14 && visit.WaitMinutes == 135 && !visit.SiteClosed && !visit.Late);
        });
        test("Exceptional closure and absent weekday block readiness while unknown hours stay explicit", () =>
        {
            var cities = Cities(); cities.Sites[0].ClosedDates.Add("2026-10-26"); var context = Context(cities); var tour = Tour(context, 0);
            Check(ScheduledTourService.Calculate(tour, context).Visits.Single().SiteClosed); Reject<InvalidDataException>(() => ScheduledTourService.Transition(tour, context, "ready", "Vérifié"));
            cities.Sites[0].ClosedDates.Clear(); cities.Sites[0].Openings.Add(new() { Day = DayOfWeek.Tuesday }); context = Context(cities); Check(ScheduledTourService.Calculate(Tour(context, 0), context).Visits.Single().SiteClosed);
            cities.Sites[0].Openings.Clear(); context = Context(cities); Check(!ScheduledTourService.Calculate(Tour(context, 0), context).Visits.Single().SiteClosed);
        });
        test("Day planning uses stop dates independently and exposes saved day assignments", () =>
        {
            var context = Context(); var tour = Tour(context, 0, 1); tour.Stops[1].Date = "2026-10-27"; tour.Stops[1].TechnicianKey = Key("Bob");
            var schedule = ScheduledTourService.Calculate(tour, context); Check(schedule.Technicians.Count == 2 && schedule.Visits.All(visit => visit.Start.Hour == 9));
            var store = new ScheduledTourStore().With(tour); var day = store.ForDate(new(2026, 10, 27), " bob ").Single(); Check(day.TicketIds.SequenceEqual(new[] { "101" }) && day.TechnicianKey == Key("Bob") && store.ForDate(new(2026, 10, 28)).Count == 0);
        });
        test("Multi day allocation excludes absent and unknown capacity technicians", () =>
        {
            var context = Context(); var tour = Tour(context, 0, 1); foreach (var stop in tour.Stops) stop.DurationMinutes = 60;
            var available = new[] { Availability("Alice", "2026-10-26", 60), Availability("Alice", "2026-10-27", 60), Availability("Bob", "2026-10-26", 400, true), Availability("Bob", "2026-10-27", null) };
            var result = ScheduledTourService.DistributeAcrossDays(tour, context, available); Check(result.AssignedStops == 2 && result.Unassigned.Count == 0 && result.Tour.Stops.Select(stop => stop.Date).Distinct().Count() == 2);
            Check(result.Tour.Stops.All(stop => stop.TechnicianKey == Key("Alice")) && tour.Stops.All(stop => stop.Date == ""));
        });
        test("Declared afternoon presence starts travel after presence begins and persists the constraint", () =>
        {
            var context = Context(); var tour = Tour(context, 0); tour.Stops[0].TravelMinutes = 15; tour.ReturnMinutes = 15;
            var availability = Availability("Alice", "2026-10-26", 240) with { MorningCapacityMinutes = 0, AfternoonCapacityMinutes = 240, WorkWindows = new[] { new TourWorkWindow(780, 1020, 240) } };
            var result = ScheduledTourService.DistributeAcrossDays(tour, context, new[] { availability }); var schedule = ScheduledTourService.Calculate(result.Tour, context);
            Check(result.AssignedStops == 1 && schedule.Visits.Single().Start.Hour == 13 && schedule.Visits.Single().Start.Minute == 15 && schedule.Technicians.Single().WaitMinutes == 0);
            var restored = System.Text.Json.JsonSerializer.Deserialize<ScheduledTour>(System.Text.Json.JsonSerializer.Serialize(result.Tour))!; restored.Validate();
            Check(restored.Availability.Single().WorkWindows!.Single().FromMinute == 780 && ScheduledTourService.Calculate(restored, context).Visits.Single().Start == schedule.Visits.Single().Start);
        });
        test("Split technician presence defers an indivisible visit past lunch without charging the break", () =>
        {
            var context = Context(); var tour = Tour(context, 0, 1); tour.Stops[0].DurationMinutes = 165; tour.Stops[1].DurationMinutes = 30;
            var availability = Availability("Alice", "2026-10-26", 420) with { MorningCapacityMinutes = 180, AfternoonCapacityMinutes = 240, WorkWindows = new[] { new TourWorkWindow(540, 720, 180), new TourWorkWindow(780, 1020, 240) } };
            var result = ScheduledTourService.DistributeAcrossDays(tour, context, new[] { availability }); var schedule = ScheduledTourService.Calculate(result.Tour, context);
            Check(result.AssignedStops == 2 && schedule.Visits.Single(visit => visit.Stop.Id == tour.Stops[1].Id).Start.Hour == 13 && schedule.Technicians.Single().WaitMinutes == 0 && !schedule.Technicians.Single().OutsideDay);
        });
        test("Half day capacity overrides restrict otherwise continuous declared presence", () =>
        {
            var context = Context(); var tour = Tour(context, 0, 1); tour.Stops[0].DurationMinutes = 90; tour.Stops[1].DurationMinutes = 30;
            var availability = Availability("Alice", "2026-10-26", 300) with { MorningCapacityMinutes = 60, AfternoonCapacityMinutes = 240, WorkWindows = new[] { new TourWorkWindow(540, 1020) } };
            var result = ScheduledTourService.DistributeAcrossDays(tour, context, new[] { availability }); var schedule = ScheduledTourService.Calculate(result.Tour, context);
            Check(result.AssignedStops == 2 && schedule.Visits.All(visit => visit.Start.Hour >= 12) && schedule.Technicians.Single().InterventionMinutes == 120);
            result.Tour.Availability[0] = availability with { AfternoonCapacityMinutes = 0 };
            Check(ScheduledTourService.Calculate(result.Tour, context).Visits.Any(visit => visit.TechnicianUnavailable));
            Reject<InvalidDataException>(() => ScheduledTourService.Transition(result.Tour, context, "ready", "Vérification"));
        });
        test("A positive capacity exception cannot invent presence on an empty declared calendar", () =>
        {
            var context = Context(); var tour = Tour(context, 0);
            var availability = Availability("Alice", "2026-10-26", 240) with { AfternoonCapacityMinutes = 240, WorkWindows = Array.Empty<TourWorkWindow>() };
            var result = ScheduledTourService.DistributeAcrossDays(tour, context, new[] { availability }); Check(result.AssignedStops == 0 && result.Unassigned.Count == 1 && result.Tour.Stops[0].TechnicianKey == "");
            availability = availability with { WorkWindows = new[] { new TourWorkWindow(540, 720), new TourWorkWindow(600, 780) } };
            Reject<InvalidDataException>(() => ScheduledTourService.DistributeAcrossDays(tour, context, new[] { availability }));
        });
        test("Return travel must also fit presence and declared half day budget", () =>
        {
            var context = Context(); var tour = Tour(context, 0); tour.Stops[0].DurationMinutes = 45; tour.Stops[0].TravelMinutes = 10; tour.ReturnMinutes = 15;
            var availability = Availability("Alice", "2026-10-26", 120) with { MorningCapacityMinutes = 0, AfternoonCapacityMinutes = 120, WorkWindows = new[] { new TourWorkWindow(780, 840) } };
            var result = ScheduledTourService.DistributeAcrossDays(tour, context, new[] { availability }); Check(result.Unassigned.Count == 1);
            availability = availability with { WorkWindows = new[] { new TourWorkWindow(780, 900) }, AfternoonCapacityMinutes = 60 };
            Check(ScheduledTourService.DistributeAcrossDays(tour, context, new[] { availability }).Unassigned.Count == 1);
        });
        test("Multi day skills require declared matches and leave incompatible work unassigned", () =>
        {
            var context = Context(); var tour = Tour(context, 0, 1); tour.Stops[0].RequiredSkills.Add("Réseau"); tour.Stops[1].RequiredSkills.Add("Onduleur");
            var result = ScheduledTourService.DistributeAcrossDays(tour, context, new[] { Availability("Alice", "2026-10-26", 480, false, "RESEAU"), Availability("Bob", "2026-10-26", 480) });
            Check(result.AssignedStops == 1 && result.Unassigned.Count == 1 && result.Tour.Stops[0].TechnicianKey == Key("Alice") && result.Tour.Stops[1].TechnicianKey == "");
        });
        test("Allocation capacity includes site waiting time return and actual final stop order", () =>
        {
            var context = Context(); var tour = Tour(context, 0, 1); tour.Stops[0].Earliest = "13:00"; tour.Stops[1].DurationMinutes = 40;
            var result = ScheduledTourService.DistributeAcrossDays(tour, context, new[] { Availability("Alice", "2026-10-26", 280), Availability("Alice", "2026-10-27", 280) });
            var schedule = ScheduledTourService.Calculate(result.Tour, context); Check(result.Unassigned.Count == 0 && schedule.Technicians.All(group => group.InterventionMinutes + group.WaitMinutes + group.TravelMinutes <= 280));
            var returnTour = Tour(context, 0); returnTour.ReturnMinutes = 60; Check(ScheduledTourService.DistributeAcrossDays(returnTour, context, new[] { Availability("Alice", "2026-10-26", 60) }).Unassigned.Count == 1);
        });
        test("Locked allocation is retained and invalid locked capacity fails before changes", () =>
        {
            var context = Context(); var tour = Tour(context, 0, 1); tour.Stops[0].Locked = true; var available = new[] { Availability("Alice", "2026-10-26", 60), Availability("Bob", "2026-10-26", 60) };
            var result = ScheduledTourService.DistributeAcrossDays(tour, context, available); Check(result.Tour.Stops[0].Locked && result.Tour.Stops[0].Id == tour.Stops[0].Id && result.Tour.Stops[0].TechnicianKey == Key("Alice"));
            Reject<InvalidDataException>(() => ScheduledTourService.DistributeAcrossDays(tour, context, new[] { Availability("Alice", "2026-10-26", 10) }));
        });
        test("Allocation respects closure dates and honors cancellation without mutation", () =>
        {
            var cities = Cities(); cities.Sites[0].ClosedDates.Add("2026-10-26"); var context = Context(cities); var tour = Tour(context, 0);
            var result = ScheduledTourService.DistributeAcrossDays(tour, context, new[] { Availability("Alice", "2026-10-26"), Availability("Alice", "2026-10-27") }); Check(result.Tour.Stops[0].Date == "2026-10-27");
            using var cts = new CancellationTokenSource(); cts.Cancel(); Reject<OperationCanceledException>(() => ScheduledTourService.DistributeAcrossDays(tour, context, new[] { Availability("Alice", "2026-10-26") }, cts.Token)); Check(tour.Stops[0].Date == "");
        });
        test("Scenario facts count due dates and missing deadlines without invented road kilometres", () =>
        {
            var context = Context(); var tour = Tour(context); tour.Stops[0].Date = "2026-10-28"; var facts = ScheduledTourService.CompareFacts(tour, context);
            Check(facts.MissedDueDates == 1 && facts.UnknownDueDates == 1 && facts.RoadKm == null && facts.RoutedLegs == 0);
        });
        test("Field briefing automatically collects reference and ticket contacts equipment and encodes HTML", () =>
        {
            var context = Context(); var tour = Tour(context, 0); Check(tour.Stops[0].Contact.Contains("Accueil") && tour.Stops[0].Contact.Contains("Mme Test <contact>") && tour.Stops[0].Equipment.Contains("Câble & écran") && tour.Stops[0].Equipment.Contains("Badge"));
            var html = ScheduledTourService.ToBriefingHtml(ScheduledTourService.Calculate(tour, context), context); Check(html.Contains("&lt;contact&gt;") && !html.Contains("<contact>") && html.Contains("&amp;") && WebUtility.HtmlDecode(html).Contains("Câble & écran"));
        });
        test("Provider requests require consent before any HTTP call and reject embedded credentials", () =>
        {
            var context = Context(); var tour = Tour(context, 0); using var handler = new FixtureHandler((_, _) => Task.FromResult(Json("{}"))); using var service = new TourExternalServices(handler);
            Reject<InvalidOperationException>(() => service.RouteAsync(tour, context, Services(), false).GetAwaiter().GetResult()); Reject<InvalidOperationException>(() => service.WeatherAsync(tour, context, Services(), false).GetAwaiter().GetResult()); Check(handler.Requests.Count == 0);
            Reject<InvalidDataException>(() => TourExternalServices.Endpoint("https://name:secret@route.example.invalid")); Reject<InvalidDataException>(() => TourExternalServices.Endpoint("https://route.example.invalid/?key=secret"));
            Reject<InvalidDataException>(() => new TourServiceSettings { RoutingProfile = "../bad" }.Validate());
        });
        test("OSRM fixture produces real route distances durations and coordinate only requests", () =>
        {
            var context = Context(); var tour = Tour(context, 0, 1); tour.End = tour.Start.Copy(); using var handler = new FixtureHandler((_, _) => Task.FromResult(Json(Routes(3)))); using var service = new TourExternalServices(handler);
            var routed = service.RouteAsync(tour, context, Services(), true).GetAwaiter().GetResult(); var schedule = ScheduledTourService.Calculate(routed, context);
            Check(routed.RoadLegs.Count == 3 && tour.RoadLegs.Count == 0 && schedule.Technicians.Single().TravelMinutes == 30 && Math.Abs(schedule.Technicians.Single().RoadKm!.Value - 3.6) < .00001);
            var uri = handler.Requests.Single(); Check(uri.Contains("/osrm/route/v1/driving/2.3,48.8;2.36,48.86") && !uri.Contains("Alice") && !uri.Contains("100") && !uri.Contains("Agence"));
            Check(ScheduledTourService.CompareFacts(routed, context).RoadKm.HasValue);
        });
        test("Changed coordinates or ordering invalidate routed legs and never reuse unrelated durations", () =>
        {
            var context = Context(); var tour = Tour(context, 0, 1); using var service = new TourExternalServices(new FixtureHandler((_, _) => Task.FromResult(Json(Routes(2)))));
            var routed = service.RouteAsync(tour, context, Services(), true).GetAwaiter().GetResult(); routed.Start.Latitude = 49;
            var first = ScheduledTourService.Calculate(routed, context); Check(first.Visits[0].RoadKm == null && first.Visits[0].TravelMinutesUsed == 0 && first.Warnings.Any(value => value.Contains("ne correspondent plus")));
            routed.Stops.Reverse(); Check(ScheduledTourService.Calculate(routed, context).Visits.All(visit => visit.RoadKm == null));
        });
        test("Provider route errors invalid legs and redirects fail atomically", () =>
        {
            var context = Context(); var tour = Tour(context, 0); foreach (var response in new[] { "{\"code\":\"NoRoute\"}", Routes(2), "{\"code\":\"Ok\",\"routes\":[{\"legs\":[{\"distance\":\"bad\",\"duration\":1}]}]}" })
            { using var service = new TourExternalServices(new FixtureHandler((_, _) => Task.FromResult(Json(response)))); Reject<InvalidDataException>(() => service.RouteAsync(tour, context, Services(), true).GetAwaiter().GetResult()); Check(tour.RoadLegs.Count == 0); }
            using var redirect = new FixtureHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.Found))); using var redirected = new TourExternalServices(redirect);
            Reject<InvalidDataException>(() => redirected.RouteAsync(tour, context, Services(), true).GetAwaiter().GetResult()); Check(redirect.Requests.Count == 1);
        });
        test("Provider network cancellation and oversized responses preserve the draft", () =>
        {
            var context = Context(); var tour = Tour(context, 0); using var cts = new CancellationTokenSource(); cts.Cancel(); using var handler = new FixtureHandler((_, _) => Task.FromResult(Json(Routes(1)))); using var service = new TourExternalServices(handler);
            Reject<OperationCanceledException>(() => service.RouteAsync(tour, context, Services(), true, cts.Token).GetAwaiter().GetResult()); Check(handler.Requests.Count == 0);
            using var oversized = new TourExternalServices(new FixtureHandler((_, _) => Task.FromResult(Json(new string('x', 2 * 1024 * 1024 + 1))))); Reject<InvalidDataException>(() => oversized.RouteAsync(tour, context, Services(), true).GetAwaiter().GetResult()); Check(tour.RoadLegs.Count == 0);
        });
        test("Weather fixture retains values date source and includes verified forecast in briefing", () =>
        {
            var context = Context(); var tour = Tour(context, 0); using var handler = new FixtureHandler((_, _) => Task.FromResult(Json(Forecast()))); using var service = new TourExternalServices(handler);
            var result = service.WeatherAsync(tour, context, Services(), true).GetAwaiter().GetResult(); var weather = result.Weather.Single(); Check(weather.WeatherCode == 61 && weather.MinimumCelsius == 4 && weather.MaximumCelsius == 13 && weather.RainProbabilityPercent == 60 && weather.Day == "2026-10-26" && weather.Provider == Services().WeatherEndpoint);
            Check(handler.Requests.Single().Contains("latitude=48.86") && !handler.Requests[0].Contains("100") && tour.Weather.Count == 0);
            var html = ScheduledTourService.ToBriefingHtml(ScheduledTourService.Calculate(result, context), context); Check(html.Contains("Prévision météo du 2026-10-26") && html.Contains("weather.example.invalid"));
        });
        test("Weather missing values remain null and wrong dates or entirely empty forecasts fail", () =>
        {
            var context = Context(); var tour = Tour(context, 0); using var service = new TourExternalServices(new FixtureHandler((_, _) => Task.FromResult(Json(Forecast(min: "null", rain: "null")))));
            var weather = service.WeatherAsync(tour, context, Services(), true).GetAwaiter().GetResult().Weather.Single(); Check(weather.MinimumCelsius == null && weather.RainProbabilityPercent == null && weather.MaximumCelsius == 13);
            using var wrong = new TourExternalServices(new FixtureHandler((_, _) => Task.FromResult(Json(Forecast("2026-10-27"))))); Reject<InvalidDataException>(() => wrong.WeatherAsync(tour, context, Services(), true).GetAwaiter().GetResult());
            using var empty = new TourExternalServices(new FixtureHandler((_, _) => Task.FromResult(Json("{\"daily\":{\"time\":[\"2026-10-26\"]}}")))); Reject<InvalidDataException>(() => empty.WeatherAsync(tour, context, Services(), true).GetAwaiter().GetResult());
        });
        test("Weather cache de duplicates same coordinates and ignores stale visit dates", () =>
        {
            var cities = Cities(); cities.Cities[1].Latitude = cities.Sites[0].Latitude; cities.Cities[1].Longitude = cities.Sites[0].Longitude; var context = Context(cities); var tour = Tour(context, 0, 1);
            using var handler = new FixtureHandler((_, _) => Task.FromResult(Json(Forecast()))); using var service = new TourExternalServices(handler); var result = service.WeatherAsync(tour, context, Services(), true).GetAwaiter().GetResult();
            Check(handler.Requests.Count == 1 && result.Weather.Count == 2 && ScheduledTourService.WeatherForStop(result, context, result.Stops[0]) != null);
            result.Stops[0].Date = "2026-10-27"; Check(ScheduledTourService.WeatherForStop(result, context, result.Stops[0]) == null);
            var duplicate = ScheduledTourService.Duplicate(result, "Scénario"); Check(duplicate.Weather.Count == 0 && duplicate.RoadLegs.Count == 0);
        });
        test("Missing coordinates never fabricate forecasts or trigger HTTP", () =>
        {
            var cities = Cities(); cities.Sites[0].Latitude = cities.Sites[0].Longitude = null; var context = Context(cities); var tour = Tour(context, 0); using var handler = new FixtureHandler((_, _) => Task.FromResult(Json(Forecast()))); using var service = new TourExternalServices(handler);
            Reject<InvalidDataException>(() => service.WeatherAsync(tour, context, Services(), true).GetAwaiter().GetResult()); Reject<InvalidDataException>(() => service.RouteAsync(tour, context, Services(), true).GetAwaiter().GetResult()); Check(handler.Requests.Count == 0);
        });
        test("Terrain persistence retains state per day constraints and provider provenance without consent", () =>
        {
            var context = Context(); var tour = Tour(context, 0); tour.Stops[0].RequiredSkills.Add("Réseau"); tour.Stops[0].Date = "2026-10-27"; tour = ScheduledTourService.Transition(tour, context, "ready", "Validée");
            var directory = Path.Combine(Path.GetTempPath(), "terrain-save-" + Guid.NewGuid().ToString("N")); var path = Path.Combine(directory, "tours.json");
            try
            {
                new ScheduledTourStore().With(tour).Save(path); var read = ScheduledTourStore.Load(path).Tours.Single(); Check(read.State == "ready" && read.StateHistory.Single().Reason == "Validée" && read.Stops[0].RequiredSkills.Single() == "Réseau" && read.Stops[0].Date == "2026-10-27");
                var configPath = Path.Combine(directory, "services.json"); Services().Save(configPath); var config = TourServiceSettings.Load(configPath); Check(config.RoutingEndpoint == Services().RoutingEndpoint && !File.ReadAllText(configPath).Contains("Consent"));
                File.WriteAllText(configPath, "{\"RoutingEndpoint\":null}"); Reject<InvalidDataException>(() => TourServiceSettings.Load(configPath));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!.AbsoluteUri); return respond(request, cancellationToken); }
    }
}
