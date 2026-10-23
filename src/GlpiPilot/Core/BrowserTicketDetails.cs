using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private readonly SemaphoreSlim _ticketReadGate = new(1, 1);
    private readonly ConcurrentDictionary<IPage, byte> _ticketPages = new();
    private readonly ConcurrentDictionary<IPage, IPage> _ticketPageRoots = new();
    private IPage? _ticketPage;
    private IPage? _ticketAnchorPage;
    private string? _ticketBridge;
    private int _ticketDetailTimeoutMilliseconds = 30000;
    public IPage? TicketPage => _ticketPage is { IsClosed: false } ? _ticketPage : _ticketAnchorPage is { IsClosed: false } ? _ticketAnchorPage : null;
    private bool IsTicketDetailPage(IPage page) => _ticketPages.ContainsKey(page);

    private void OwnTicketPage(IPage page, IPage? root = null)
    {
        if (!_ticketPages.TryAdd(page, 0)) return;
        root ??= page;
        _ticketPageRoots[page] = root;
        var owner = root;
        page.Popup += (_, popup) =>
        {
            OwnTicketPage(popup, owner);
            // An explicit "Voir le navigateur" must reach a challenge opened
            // by the ticket flow, without automatically activating its window.
            _ticketPage = popup;
        };
        page.Close += (_, _) =>
        {
            if (ReferenceEquals(_ticketPage, page)) _ticketPage = null;
            _ticketPages.TryRemove(page, out _);
            _ticketPageRoots.TryRemove(page, out _);
        };
    }

    private sealed class TicketDomSection
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("selector")] public string Selector { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "table";
    }
    private sealed class TicketDomInspection
    {
        [JsonPropertyName("ready")] public bool Ready { get; set; }
        [JsonPropertyName("authentication")] public bool Authentication { get; set; }
        [JsonPropertyName("busy")] public bool Busy { get; set; }
        [JsonPropertyName("wrongId")] public bool WrongId { get; set; }
        [JsonPropertyName("identityConfirmed")] public bool IdentityConfirmed { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("sections")] public List<TicketDomSection> Sections { get; set; } = new();
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    }
    private sealed class TicketDomData
    {
        [JsonPropertyName("found")] public bool Found { get; set; }
        [JsonPropertyName("matches")] public int Matches { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
        [JsonPropertyName("headers")] public List<string> Headers { get; set; } = new();
        [JsonPropertyName("rows")] public List<string[]> Rows { get; set; } = new();
        [JsonPropertyName("totalRows")] public int TotalRows { get; set; }
        [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
    }
    private sealed class TicketDomTab
    {
        [JsonPropertyName("allowed")] public bool Allowed { get; set; }
        [JsonPropertyName("matches")] public int Matches { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
        [JsonPropertyName("selector")] public string Selector { get; set; } = "";
        [JsonPropertyName("alreadyActive")] public bool AlreadyActive { get; set; }
        [JsonPropertyName("proof")] public string Proof { get; set; } = "";
        [JsonPropertyName("targetSelector")] public string TargetSelector { get; set; } = "";
        [JsonPropertyName("key")] public string Key { get; set; } = "";
    }
    private sealed class TicketDomTabInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("selector")] public string Selector { get; set; } = "";
        [JsonPropertyName("category")] public string Category { get; set; } = "other";
        [JsonPropertyName("isActive")] public bool IsActive { get; set; }
        [JsonPropertyName("key")] public string Key { get; set; } = "";
    }

    /// <summary>Reads only the selected ticket in a separate browser page. It never navigates the search page.</summary>
    public Task<TicketDetailResult> ReadTicketDetailsAsync(TicketReference ticket, AppSettings settings,
        IProgress<ExtractionProgress>? progress, CancellationToken ct) => ReadTicketDetailsCoreAsync(ticket, settings, null, progress, ct);

    /// <summary>Loads one previously discovered, independently revalidated consultation tab.</summary>
    public Task<TicketDetailResult> ReadTicketTabAsync(TicketReference ticket, AppSettings settings, TicketDetailTab tab,
        IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        if (tab == null || string.IsNullOrWhiteSpace(tab.Key) || tab.Key.Length > 2048 || string.IsNullOrWhiteSpace(tab.Name) || tab.Name.Length > 120)
            throw new InvalidOperationException("L’identité de l’onglet est absente ou invalide. Actualise la fiche.");
        return ReadTicketDetailsCoreAsync(ticket, settings, tab, progress, ct);
    }

    private async Task<TicketDetailResult> ReadTicketDetailsCoreAsync(TicketReference ticket, AppSettings settings, TicketDetailTab? requestedTab,
        IProgress<ExtractionProgress>? progress, CancellationToken ct, TicketLibraryCoverage? libraryCoverage = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(settings);
        var reference = ValidateTicketReference(ticket, settings);
        var options = settings.TicketDetails.Copy();
        if (libraryCoverage != null)
        {
            options.IncludeAutoDetected = true; options.AutoReadTreatment = false;
            options.MaxRowsPerSection = Math.Min(options.MaxRowsPerSection, Math.Clamp(settings.MaxRows, 1, 1000000));
        }
        if (options.MaxRowsPerSection is < 1 or > 100000)
            throw new InvalidOperationException("La limite de lignes par section doit être comprise entre 1 et 100 000.");
        await _ticketReadGate.WaitAsync(ct);
        _ticketDetailTimeoutMilliseconds = Math.Clamp(settings.DetailTimeoutSeconds, 5, 600) * 1000;
        IPage? page = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_context == null) await OpenAsync(settings, ct, background: true);
            if (_context == null) throw new InvalidOperationException("Le navigateur piloté n’est pas disponible.");
            if (TicketPage == null)
            {
                var pending = _context.NewPageAsync();
                try { page = await pending.WaitAsync(ct); }
                catch (OperationCanceledException) { _ = CloseLateTicketPageAsync(pending); throw; }
                OwnTicketPage(page);
                _ticketPage = page;
            }
            else page = TicketPage;
            _ticketAnchorPage = page;
            Log?.Invoke("FICHE · ouverture en lecture dans l’onglet dédié ; la recherche reste ouverte.");
            progress?.Report(new(0, 0, null, "Ouverture du ticket sélectionné…"));
            await NavigateTicketAsync(page!, reference.Url, ct);
            var ready = await WaitForTicketAsync(page!, reference, settings, options, progress, ct);
            page = ready.Page;
            _ticketPage = page;
            _ticketAnchorPage = page;
            var inspected = ready.Inspections;
            var result = new TicketDetailResult { Ticket = reference, ReadAt = DateTimeOffset.Now };
            result.Title = inspected.Where(item => item.Inspection.Ready).Select(item => item.Inspection.Title).FirstOrDefault(title => title.Length > 0) ?? "Fiche ticket";
            if (libraryCoverage == null)
                result.Warnings.Add("Cette lecture couvre les sections visibles et les sections configurées. Les autres onglets et contenus masqués ne sont pas déclarés complets.");
            result.Tabs = await DiscoverTicketTabsAsync(page, reference, ct);
            if (requestedTab != null)
            {
                var selected = result.Tabs.SingleOrDefault(tab => tab.Key == requestedTab.Key && tab.Name == requestedTab.Name);
                if (selected == null) throw new InvalidOperationException("Cet onglet n’est plus disponible en consultation sur ce ticket.");
                await ReadDiscoveredTicketTabAsync(page, reference, options, result, selected, progress, ct);
                return result;
            }
            var seen = new HashSet<(IFrame Frame, string Selector, string Kind)>();
            if (options.IncludeAutoDetected)
            {
                foreach (var item in inspected.Where(item => item.Inspection.Ready))
                foreach (var section in item.Inspection.Sections)
                {
                    ct.ThrowIfCancellationRequested();
                    // A manual name for the same initial section takes precedence.
                    if (options.Sections.Any(definition => definition.TabSelector.Length == 0 && definition.Selector == section.Selector && definition.Kind == section.Kind)) continue;
                    if (!seen.Add((item.Frame, section.Selector, section.Kind))) continue;
                    await AddTicketSectionAsync(page!, reference, result, item.Frame, new()
                    { Name = section.Name, Selector = section.Selector, Kind = section.Kind }, options.MaxRowsPerSection, progress, ct);
                }
                foreach (var tab in result.Tabs.Where(tab => tab.IsActive)) tab.Loaded = true;
            }
            for (var index = 0; index < options.Sections.Count; index++)
            {
                var definition = options.Sections[index];
                var label = definition.Name.Length > 0 ? definition.Name : "Section configurée " + (index + 1);
                ct.ThrowIfCancellationRequested();
                if (definition.Kind is not ("table" or "fields" or "text" or "timeline") || string.IsNullOrWhiteSpace(definition.Selector))
                { result.Warnings.Add(label + " : type ou sélecteur absent/invalide."); continue; }
                var retained = result.Sections.FirstOrDefault(section => section.Selector == definition.Selector && section.Kind == definition.Kind &&
                    (section.TabSelector == definition.TabSelector || definition.TabSelector.Length == 0));
                if (retained != null) { retained.Name = label; retained.Data.Title = label; continue; }
                if (definition.TabSelector.Length > 0)
                {
                    var tabReason = await OpenTicketReadTabAsync(page!, reference, definition.TabSelector, ct);
                    if (tabReason.Length > 0) { result.Warnings.Add(label + " : " + tabReason); continue; }
                }
                var located = await LocateTicketSectionAsync(page!, reference, definition, options.MaxRowsPerSection, definition.TabSelector.Length > 0, ct);
                if (located.Frame == null)
                { result.Warnings.Add(label + " : " + located.Reason); continue; }
                if (!seen.Add((located.Frame, definition.Selector, definition.Kind)) && definition.TabSelector.Length == 0) continue;
                await AddTicketSectionAsync(page!, reference, result, located.Frame, definition, options.MaxRowsPerSection, progress, ct);
            }
            if (options.AutoReadTreatment)
            {
                var treatment = result.Tabs.FirstOrDefault(tab => tab.Category == "treatment");
                if (treatment != null && !result.Sections.Any(section => section.Kind == "timeline"))
                    await ReadDiscoveredTicketTabAsync(page, reference, options, result, treatment, progress, ct);
                else if (treatment != null) treatment.Loaded = true;
            }
            if (libraryCoverage != null)
                await ReadLibraryConsultationTabsAsync(page, reference, settings, options, result, libraryCoverage, progress, ct);
            ct.ThrowIfCancellationRequested();
            EnsureTicketPageIdentity(page!, reference);
            if (result.Sections.Count == 0) result.Warnings.Add("Aucune section exploitable n’a été trouvée. Les sélecteurs peuvent être ajustés dans les réglages des tickets.");
            Log?.Invoke($"FICHE · lecture terminée : {result.Sections.Count} sections, {result.Sections.Sum(section => section.Data.Rows.Count)} lignes, {result.Warnings.Count} indications de périmètre ou d’absence.");
            return result;
        }
        catch (OperationCanceledException)
        {
            // Stop the actual navigation, not just the wait: a canceled selection
            // must not navigate the page after the next selection has started.
            if (page != null) await CloseTicketFamilyAsync(page);
            Log?.Invoke("FICHE · lecture annulée ; l’onglet de recherche est conservé.");
            throw;
        }
        catch (PlaywrightException)
        {
            // Playwright errors can include URLs, selectors or DOM excerpts.
            throw new InvalidOperationException("La page de détail a changé ou s’est fermée pendant la lecture. Relance la lecture du ticket sélectionné.");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("La fiche ou une section n’a pas répondu dans le délai prévu. Vérifie le SSO ou l’onglet de consultation dans le navigateur, puis relance la lecture.");
        }
        finally { _ticketReadGate.Release(); }
    }

    private static TicketReference ValidateTicketReference(TicketReference ticket, AppSettings settings)
    {
        var id = TicketReferenceResolver.NormalizeId(ticket.Id);
        var target = TicketReferenceResolver.ReadHttpUrl(ticket.Url, "L’adresse de la fiche");
        var parameters = TicketReferenceResolver.ParseQuery(target);
        if (parameters.Count(pair => pair.Key == "id") != 1 || TicketReferenceResolver.NormalizeId(parameters.First(pair => pair.Key == "id").Value) != id)
            throw new InvalidOperationException("L’adresse de la fiche ne correspond pas à l’identifiant sélectionné.");
        var template = target.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", parameters.Select(pair =>
            pair.Key + "=" + (pair.Key == "id" ? "{id}" : Uri.EscapeDataString(pair.Value))));
        if (target.Fragment.Length != 0) throw new InvalidOperationException("Une fiche ticket doit être ouverte sans fragment d’adresse.");
        var validated = TicketReferenceResolver.ValidateTemplate(template, settings.PortalUrl);
        return new(id, validated.Replace("{id}", id, StringComparison.Ordinal));
    }

    private async Task EnsureTicketBridgeAsync(IFrame frame, CancellationToken ct)
    {
        if (_ticketBridge == null)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("BrowserScripts.ticket-details.js", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            _ticketBridge = reader.ReadToEnd();
        }
        await frame.EvaluateAsync(_ticketBridge).WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    private Task<T> ReadTicketJsonAsync<T>(IFrame frame, string function, object? argument, CancellationToken ct) =>
        ReadJsonAsync<T>(frame, function, argument, ct).WaitAsync(TimeSpan.FromMilliseconds(_ticketDetailTimeoutMilliseconds), ct);

    private static bool TicketUrlMatches(string url, TicketReference ticket, out bool wrongTicket)
    {
        wrongTicket = false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var actual)) return false;
        var requested = new Uri(ticket.Url);
        if (!TicketReferenceResolver.SameOrigin(actual, requested) || actual.AbsolutePath != requested.AbsolutePath) return false;
        var values = TicketReferenceResolver.ParseQuery(actual);
        if (values.Any(pair => TicketReferenceResolver.IsBusinessParameter(pair.Key)))
            throw new InvalidOperationException("La navigation a quitté la fiche en consultation. Aucune donnée n’a été lue.");
        var identities = values.Where(pair => pair.Key == "id").ToArray();
        if (identities.Length != 1 || identities[0].Value.Length == 0) return false;
        try { wrongTicket = TicketReferenceResolver.NormalizeId(identities[0].Value) != ticket.Id; }
        catch (InvalidOperationException) { wrongTicket = true; }
        return !wrongTicket;
    }

    private static void EnsureTicketPageIdentity(IPage page, TicketReference ticket)
    {
        if (page.IsClosed || !TicketUrlMatches(page.Url, ticket, out _))
            throw new InvalidOperationException("La fiche affichée ne correspond plus au ticket sélectionné. La lecture a été arrêtée.");
    }

    private async Task NavigateTicketAsync(IPage page, string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = _ticketDetailTimeoutMilliseconds }).WaitAsync(ct); }
        catch (TimeoutException) { /* Continue observing natural SSO redirects. */ }
        catch (PlaywrightException ex) when (ex.Message.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("interrupted by another navigation", StringComparison.OrdinalIgnoreCase)) { }
        ct.ThrowIfCancellationRequested();
    }

    private async Task<List<IFrame>> TicketFramesAsync(IPage page, TicketReference ticket, CancellationToken ct)
    {
        EnsureTicketPageIdentity(page, ticket);
        var frames = new List<IFrame>();
        foreach (var frame in page.Frames.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (frame.IsDetached || Origin(frame.Url) != Origin(ticket.Url)) continue;
            if (frame != page.MainFrame)
            {
                var visible = true;
                for (IFrame? current = frame; current != null && current != page.MainFrame; current = current.ParentFrame)
                {
                    await using var element = await current.FrameElementAsync().WaitAsync(ct);
                    if (!await element.IsVisibleAsync().WaitAsync(ct)) { visible = false; break; }
                }
                if (!visible) continue;
                TicketUrlMatches(frame.Url, ticket, out var wrong);
                if (wrong) continue;
            }
            await EnsureTicketBridgeAsync(frame, ct);
            frames.Add(frame);
        }
        return frames;
    }

    private async Task<List<(IFrame Frame, TicketDomInspection Inspection)>> InspectTicketAsync(IPage page, TicketReference ticket, TicketDetailSettings options, CancellationToken ct)
    {
        var result = new List<(IFrame, TicketDomInspection)>();
        foreach (var frame in await TicketFramesAsync(page, ticket, ct))
        {
            var inspection = await ReadTicketJsonAsync<TicketDomInspection>(frame, "arg => window.__glpiPilotTicket.inspect(arg)",
                new { id = ticket.Id, origin = Origin(ticket.Url), selectors = options.Sections.Select(section => section.Selector).ToArray() }, ct);
            if (inspection.WrongId) throw new InvalidOperationException("L’identifiant présent dans la fiche ne correspond pas au ticket sélectionné. Aucune donnée n’a été retenue.");
            result.Add((frame, inspection));
        }
        EnsureTicketPageIdentity(page, ticket);
        return result;
    }

    private async Task<(IPage Page, List<(IFrame Frame, TicketDomInspection Inspection)> Inspections)> WaitForTicketAsync(IPage page, TicketReference ticket, AppSettings settings,
        TicketDetailSettings options, IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var stableSince = timer.Elapsed;
        var lastUrl = "";
        var resumed = 0;
        var lastStatus = TimeSpan.FromSeconds(-5);
        var owner = _ticketPageRoots.GetValueOrDefault(page, page);
        while (timer.Elapsed.TotalSeconds < Math.Clamp(settings.TimeoutSeconds, 5, 3600))
        {
            ct.ThrowIfCancellationRequested();
            if (_context == null) throw new InvalidOperationException("Le navigateur a été fermé pendant la lecture de la fiche.");
            // Only descendants of this dedicated page may complete its SSO.
            // Another browser tab, including the search page, is never adopted.
            foreach (var popup in _ticketPageRoots.Where(pair => ReferenceEquals(pair.Value, owner)).Select(pair => pair.Key).ToArray())
            {
                if (ReferenceEquals(popup, page) || popup.IsClosed || !TicketUrlMatches(popup.Url, ticket, out _)) continue;
                if (_navigationTimes.TryGetValue(popup, out var navigated) && (DateTimeOffset.UtcNow - navigated).TotalMilliseconds < 500) continue;
                try
                {
                    var popupResult = await InspectTicketAsync(popup, ticket, options, ct);
                    if (popupResult.Any(item => item.Inspection.Ready) && !popupResult.Any(item => item.Inspection.Authentication || item.Inspection.Busy))
                    {
                        _ticketPage = popup;
                        Log?.Invoke("FICHE · connexion terminée dans un nouvel onglet dédié, recherche conservée.");
                        return (popup, popupResult);
                    }
                }
                catch (PlaywrightException) { }
            }
            if (page.IsClosed)
            {
                if (_ticketPageRoots.Any(pair => ReferenceEquals(pair.Value, owner) && !pair.Key.IsClosed)) { await Task.Delay(250, ct); continue; }
                throw new InvalidOperationException("L’onglet de détail a été fermé pendant la lecture.");
            }
            if (page.Url != lastUrl) { lastUrl = page.Url; stableSince = timer.Elapsed; }
            var exact = TicketUrlMatches(page.Url, ticket, out var wrong);
            if (wrong) throw new InvalidOperationException("GLPI a affiché une autre fiche que le ticket sélectionné. Aucune donnée n’a été retenue.");
            var authentication = false;
            if (exact)
            {
                try
                {
                    var result = await InspectTicketAsync(page, ticket, options, ct);
                    authentication = result.Any(item => item.Inspection.Authentication);
                    if (!authentication && result.Any(item => item.Inspection.Ready) && !result.Any(item => item.Inspection.Busy) && (timer.Elapsed - stableSince).TotalMilliseconds >= 500)
                        return (page, result);
                }
                catch (PlaywrightException) { /* A frame may still be navigating. */ }
            }
            else if (Origin(page.Url) == Origin(ticket.Url) && !IsAuthenticationUrl(page.Url))
            {
                var probe = await ProbeFrameAsync(page.MainFrame, settings, ct);
                authentication = probe?.Challenge == true || probe?.Login == true;
                if (!authentication && (probe?.Authenticated == true || probe?.Search == true) && resumed < 2 && (timer.Elapsed - stableSince).TotalSeconds >= 1)
                {
                    resumed++;
                    Log?.Invoke("FICHE · connexion terminée, retour à la fiche demandée.");
                    await NavigateTicketAsync(page, ticket.Url, ct);
                    stableSince = timer.Elapsed;
                    continue;
                }
            }
            else authentication = true;
            if ((timer.Elapsed - lastStatus).TotalSeconds >= 5)
            {
                lastStatus = timer.Elapsed;
                progress?.Report(new(0, 0, null, authentication
                    ? "Fiche : attente de la connexion SSO. Le navigateur reste accessible."
                    : "Fiche : attente du contenu de consultation…"));
                Log?.Invoke($"FICHE · attente {timer.Elapsed.TotalSeconds:F0} s ; connexion={(authentication ? "en cours" : "non demandée")}, reprise={resumed}.");
            }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("La fiche ticket n’est pas devenue lisible dans le délai prévu. Vérifie la connexion dans le navigateur puis relance sa lecture.");
    }

    private async Task<string> OpenTicketReadTabAsync(IPage page, TicketReference ticket, string selector, CancellationToken ct)
    {
        var matches = new List<(IFrame Frame, TicketDomTab Tab)>();
        foreach (var frame in await TicketFramesAsync(page, ticket, ct))
        {
            var tab = await ReadTicketJsonAsync<TicketDomTab>(frame, "arg => window.__glpiPilotTicket.tab(arg)", new { selector, id = ticket.Id, origin = Origin(ticket.Url), ticketPath = new Uri(ticket.Url).AbsolutePath }, ct);
            if (tab.Matches > 0) matches.Add((frame, tab));
        }
        if (matches.Count != 1 || matches[0].Tab.Matches != 1) return "Le sélecteur d’onglet est absent ou ambigu ; aucun clic n’a été effectué.";
        var choice = matches[0];
        if (!choice.Tab.Allowed) return choice.Tab.Reason.Length > 0 ? choice.Tab.Reason : "Le contrôle n’est pas un onglet de consultation reconnu.";
        if (choice.Tab.AlreadyActive) return "";
        EnsureTicketPageIdentity(page, ticket);
        var again = await ReadTicketJsonAsync<TicketDomTab>(choice.Frame, "arg => window.__glpiPilotTicket.tab(arg)", new { selector = choice.Tab.Selector, id = ticket.Id, origin = Origin(ticket.Url), ticketPath = new Uri(ticket.Url).AbsolutePath }, ct);
        if (!again.Allowed || again.Proof != choice.Tab.Proof || again.Key != choice.Tab.Key || again.Matches != 1) return "Le repère de l’onglet a changé ; aucun clic n’a été effectué.";
        await choice.Frame.Locator(again.Selector).ClickAsync(new() { Timeout = _ticketDetailTimeoutMilliseconds }).WaitAsync(ct);
        await Task.Delay(200, ct);
        EnsureTicketPageIdentity(page, ticket);
        Log?.Invoke("FICHE · onglet de consultation reconnu ouvert.");
        return "";
    }

    private async Task<List<TicketDetailTab>> DiscoverTicketTabsAsync(IPage page, TicketReference ticket, CancellationToken ct)
    {
        var found = new List<TicketDetailTab>();
        foreach (var frame in await TicketFramesAsync(page, ticket, ct))
        {
            var tabs = await ReadTicketJsonAsync<List<TicketDomTabInfo>>(frame, "arg => window.__glpiPilotTicket.tabs(arg)",
                new { id = ticket.Id, origin = Origin(ticket.Url), ticketPath = new Uri(ticket.Url).AbsolutePath }, ct);
            found.AddRange(tabs.Select(tab => new TicketDetailTab
            { Name = tab.Name, Key = tab.Key, Selector = tab.Selector, Category = tab.Category, IsActive = tab.IsActive }));
        }
        return found.GroupBy(tab => tab.Key, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single()).Take(60).ToList();
    }

    private async Task ReadDiscoveredTicketTabAsync(IPage page, TicketReference ticket, TicketDetailSettings options,
        TicketDetailResult result, TicketDetailTab tab, IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new(result.Sections.Count, 0, null, "Lecture de l’onglet de consultation…"));
        var reason = await OpenTicketReadTabAsync(page, ticket, tab.Selector, ct);
        if (reason.Length > 0) { result.Warnings.Add(tab.Name + " : " + reason); return; }
        var timer = Stopwatch.StartNew();
        var frames = new List<(IFrame Frame, TicketDomInspection Inspection)>();
        do
        {
            frames.Clear();
            foreach (var frame in await TicketFramesAsync(page, ticket, ct))
            {
                var proof = await ReadTicketJsonAsync<TicketDomTab>(frame, "arg => window.__glpiPilotTicket.tab(arg)",
                    new { selector = tab.Selector, id = ticket.Id, origin = Origin(ticket.Url), ticketPath = new Uri(ticket.Url).AbsolutePath }, ct);
                if (!proof.Allowed || !proof.AlreadyActive || proof.Key != tab.Key || proof.TargetSelector.Length == 0) continue;
                var inspection = await ReadTicketJsonAsync<TicketDomInspection>(frame, "arg => window.__glpiPilotTicket.inspect(arg)",
                    new { id = ticket.Id, origin = Origin(ticket.Url), scopeSelector = proof.TargetSelector }, ct);
                if (inspection.WrongId || inspection.Authentication)
                    throw new InvalidOperationException("La fiche a changé ou attend une authentification pendant la lecture de l’onglet.");
                frames.Add((frame, inspection));
            }
            if (timer.ElapsedMilliseconds >= 500 && frames.Count > 0 && frames.All(item => item.Inspection.Ready && !item.Inspection.Busy)) break;
            await Task.Delay(250, ct);
        } while (timer.Elapsed.TotalMilliseconds < _ticketDetailTimeoutMilliseconds);
        if (frames.Count == 0 || frames.Any(item => item.Inspection.Busy || !item.Inspection.Ready))
        { result.Warnings.Add(tab.Name + " : le contenu de cet onglet n’est pas devenu lisible dans le délai prévu."); return; }
        var initialCount = result.Sections.Count; var initialObservations = result.ObservedItems.Count;
        foreach (var item in frames)
        foreach (var section in item.Inspection.Sections)
        {
            ct.ThrowIfCancellationRequested();
            if (result.Sections.Any(existing => existing.Selector == section.Selector && existing.Kind == section.Kind && existing.TabSelector == tab.Selector)) continue;
            await AddTicketSectionAsync(page, ticket, result, item.Frame, new()
            { Name = section.Name == "Contenu de l’onglet" ? tab.Name : section.Name, Selector = section.Selector, Kind = section.Kind, TabSelector = tab.Selector }, options.MaxRowsPerSection, progress, ct);
        }
        foreach (var section in result.Sections.Skip(initialCount)) { section.TabName = tab.Name; section.TabKey = tab.Key; }
        foreach (var observation in result.ObservedItems.Skip(initialObservations)) observation.SourceTabKey = tab.Key;
        tab.Loaded = true;
        if (result.Sections.Count == initialCount) result.Warnings.Add(tab.Name + " : aucun contenu de consultation exploitable n’a été trouvé.");
        Log?.Invoke($"FICHE · onglet lu : {result.Sections.Count - initialCount} section(s) ; autres onglets accessibles à la demande.");
    }

    private async Task<(IFrame? Frame, string Reason)> LocateTicketSectionAsync(IPage page, TicketReference ticket, TicketSectionDefinition definition,
        int maximum, bool wait, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        string reason = "La section configurée est absente.";
        do
        {
            var candidates = new List<(IFrame Frame, TicketDomData Data)>();
            foreach (var frame in await TicketFramesAsync(page, ticket, ct))
            {
                var data = await ReadTicketJsonAsync<TicketDomData>(frame, "arg => window.__glpiPilotTicket.read(arg)", new { selector = definition.Selector, kind = definition.Kind, maxRows = maximum, id = ticket.Id, origin = Origin(ticket.Url) }, ct);
                if (data.Matches > 0) candidates.Add((frame, data));
                if (data.Reason.Contains("invalide", StringComparison.OrdinalIgnoreCase)) reason = data.Reason;
            }
            if (candidates.Sum(item => item.Data.Matches) > 1) return (null, "Le sélecteur correspond à plusieurs éléments ou cadres ; la lecture est refusée.");
            if (candidates.Count == 1)
            {
                if (candidates[0].Data.Found) return (candidates[0].Frame, "");
                reason = candidates[0].Data.Reason;
            }
            if (!wait) break;
            await Task.Delay(250, ct);
        } while (timer.Elapsed.TotalMilliseconds < _ticketDetailTimeoutMilliseconds);
        return (null, reason);
    }

    private async Task AddTicketSectionAsync(IPage page, TicketReference ticket, TicketDetailResult result, IFrame frame,
        TicketSectionDefinition definition, int maximum, IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        EnsureTicketPageIdentity(page, ticket);
        if (Origin(frame.Url) != Origin(ticket.Url)) throw new InvalidOperationException("Le cadre de la fiche a changé d’origine. La lecture a été arrêtée.");
        await EnsureTicketBridgeAsync(frame, ct);
        var identity = await ReadTicketJsonAsync<TicketDomInspection>(frame, "arg => window.__glpiPilotTicket.inspect(arg)", new { id = ticket.Id, origin = Origin(ticket.Url) }, ct);
        if (identity.WrongId || identity.Authentication)
            throw new InvalidOperationException("La fiche a changé ou attend une authentification. La lecture a été arrêtée.");
        var data = await ReadTicketJsonAsync<TicketDomData>(frame, "arg => window.__glpiPilotTicket.read(arg)", new { selector = definition.Selector, kind = definition.Kind, maxRows = maximum, id = ticket.Id, origin = Origin(ticket.Url) }, ct);
        EnsureTicketPageIdentity(page, ticket);
        if (Origin(frame.Url) != Origin(ticket.Url)) throw new InvalidOperationException("Le cadre de la fiche a changé d’origine. La lecture a été arrêtée.");
        if (!data.Found) { result.Warnings.Add((definition.Name.Length > 0 ? definition.Name : "Section") + " : " + data.Reason); return; }
        var name = definition.Name.Length > 0 ? definition.Name : "Section " + (result.Sections.Count + 1);
        if (data.Headers.Count == 0 || data.Rows.Count == 0 && definition.Kind == "fields") return;
        result.Sections.Add(new()
        {
            Name = name, Selector = definition.Selector, Kind = definition.Kind, TabSelector = definition.TabSelector,
            Data = new()
            {
                Headers = data.Headers, Rows = data.Rows, SourceUrl = AppPaths.SafeUrl(ticket.Url), Title = name,
                ExtractedAt = DateTimeOffset.Now, Pages = 1, ExpectedTotal = data.TotalRows,
                Scope = "Section visible de la fiche ticket", Complete = data.Rows.Count == data.TotalRows,
                Status = data.Rows.Count == data.TotalRows ? "Section visible lue" : "Section limitée", Warnings = data.Warnings
            }
        });
        var observed = await ReadTicketJsonAsync<List<TicketObservedItem>>(frame, "arg => window.__glpiPilotTicket.observe(arg)",
            new { selector = definition.Selector, maxRows = maximum, id = ticket.Id, origin = Origin(ticket.Url), ticketPath = new Uri(ticket.Url).AbsolutePath }, ct);
        EnsureTicketPageIdentity(page, ticket);
        var observedKeys = result.ObservedItems.Select(item => (item.Kind, item.Identity, item.Title, item.Url, item.Date)).ToHashSet();
        foreach (var item in observed)
        {
            if (result.ObservedItems.Count >= 6000) { result.Warnings.Add("L’index structuré est limité à 6 000 éléments ; les autres éléments ne sont pas comptés."); break; }
            item.SourceSection = name; item.SourceTabSelector = definition.TabSelector; item.SourceSelector = definition.Selector; item.ObservedAt = result.ReadAt;
            if (observedKeys.Add((item.Kind, item.Identity, item.Title, item.Url, item.Date))) result.ObservedItems.Add(item);
        }
        progress?.Report(new(result.Sections.Count, result.Sections.Sum(section => section.Data.Rows.Count), null, "Lecture des sections du ticket…"));
    }

    private async Task CloseLateTicketPageAsync(Task<IPage> pending)
    {
        try { var page = await pending; OwnTicketPage(page); await CloseTicketPageAsync(page); }
        catch (PlaywrightException) { }
    }
    private async Task CloseTicketFamilyAsync(IPage page)
    {
        var owner = _ticketPageRoots.GetValueOrDefault(page, page);
        var family = _ticketPageRoots.Where(pair => ReferenceEquals(pair.Value, owner)).Select(pair => pair.Key).ToArray();
        await Task.WhenAll(family.Append(page).Distinct().Select(CloseTicketPageAsync));
    }
    private async Task CloseTicketPageAsync(IPage page)
    {
        // This cleanup may only close pages explicitly created/owned by the
        // ticket flow, never the user's search or another browser tab.
        if (!_ticketPages.ContainsKey(page)) return;
        if (ReferenceEquals(_ticketPage, page)) _ticketPage = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Closed(object? _, IPage __) => closed.TrySetResult();
        page.Close += Closed;
        try
        {
            if (page.IsClosed) return;
            using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                var close = page.CloseAsync(new() { RunBeforeUnload = false });
                // The browser action can complete after our bounded wait. Its
                // failure must remain observed even when recovery closes it.
                _ = close.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                try
                {
                    await close.WaitAsync(budget.Token);
                    if (!page.IsClosed) await closed.Task.WaitAsync(budget.Token);
                }
                catch (Exception ex) when (ex is PlaywrightException or OperationCanceledException or TimeoutException) { }
            }
            if (page.IsClosed) return;
            // Cancellation of GotoAsync's WaitAsync does not cancel Chromium's
            // navigation. A target can remain alive while its first close is
            // pending: stop that exact owned target, close it, and observe the
            // page-closed event before releasing the ticket operation gate.
            await RecoverTicketPageCloseAsync(page, closed.Task);
            if (!page.IsClosed) Log?.Invoke("FICHE · fermeture de l’onglet temporaire non confirmée par le navigateur.");
        }
        finally { page.Close -= Closed; }
    }
    private static async Task RecoverTicketPageCloseAsync(IPage page, Task closed)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ICDPSession? session = null;
        Task<ICDPSession>? pending = null;
        try
        {
            if (page.IsClosed) return;
            pending = page.Context.NewCDPSessionAsync(page);
            session = await pending.WaitAsync(budget.Token);
            var response = await session.SendAsync("Target.getTargetInfo").WaitAsync(budget.Token);
            if (response is not { } information || !information.TryGetProperty("targetInfo", out var target)
                || !target.TryGetProperty("targetId", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String
                || id.GetString() is not { Length: > 0 } targetId) return;
            var stop = session.SendAsync("Page.stopLoading");
            _ = stop.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try { await stop.WaitAsync(TimeSpan.FromSeconds(1), budget.Token); }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }
            // A stalled renderer must not consume the whole recovery budget:
            // target closure is a separate browser command and is still tried.
            budget.Token.ThrowIfCancellationRequested();
            if (!page.IsClosed)
                await session.SendAsync("Target.closeTarget", new() { ["targetId"] = targetId }).WaitAsync(budget.Token);
            if (!page.IsClosed) await closed.WaitAsync(budget.Token);
        }
        catch (Exception ex) when (ex is PlaywrightException or OperationCanceledException or TimeoutException) { }
        finally
        {
            if (session != null)
            {
                try { await session.DetachAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }
            }
            else if (pending != null) _ = DetachLateTicketCleanupSessionAsync(pending);
        }
    }
    private static async Task DetachLateTicketCleanupSessionAsync(Task<ICDPSession> pending)
    {
        try
        {
            var session = await pending;
            await session.DetachAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }
    }
}
