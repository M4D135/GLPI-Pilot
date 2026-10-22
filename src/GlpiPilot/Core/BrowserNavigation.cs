using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    public event Action<string>? ConnectionProgress;

    private sealed class SessionProbe
    {
        [JsonPropertyName("password")] public bool Password { get; set; }
        [JsonPropertyName("challenge")] public bool Challenge { get; set; }
        [JsonPropertyName("login")] public bool Login { get; set; }
        [JsonPropertyName("glpi")] public bool Glpi { get; set; }
        [JsonPropertyName("search")] public bool Search { get; set; }
        [JsonPropertyName("table")] public bool Table { get; set; }
        [JsonPropertyName("native")] public bool Native { get; set; }
        [JsonPropertyName("authenticated")] public bool Authenticated { get; set; }
        public bool ExplicitReady { get; set; }
        public bool Readable => !Password && !Challenge && !Login && (Search || Table || ExplicitReady);
    }

    private static bool IsAuthenticationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return Regex.IsMatch(uri.AbsolutePath, @"(?:^|/)(?:login|signin|sign-in|sso|oauth2?|saml2?)(?:[/.]|$)", RegexOptions.IgnoreCase);
    }

    private async Task<SessionProbe?> ProbeFrameAsync(IFrame frame, AppSettings settings, CancellationToken ct)
    {
        if (frame.IsDetached || Origin(frame.Url).Length == 0) return null;
        try
        {
            var probe = await ReadJsonAsync<SessionProbe>(frame, """
                () => {
                  const visible = e => e && e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden' && !e.closest('[hidden],[aria-hidden=true]');
                  const any = s => [...document.querySelectorAll(s)].some(visible);
                  const password = any('input[type=password]');
                  const challenge = password || any('input[autocomplete=one-time-code],input[name=otp],input[name=otc],input[name=verificationCode],iframe[title*=captcha i]');
                  const busy = any('table.search-results[aria-busy=true],[data-glpi-search-results][aria-busy=true]');
                  const search = !busy && any('table.search-results,[data-glpi-search-results]');
                  const native = !!document.querySelector('[id^=dropdown-export],select[name=display_type],a[href*="report.dynamic.php"][href*="display_type=-"]');
                  const authenticated = [...document.querySelectorAll('a[href],button')].some(e => /(?:^|\/)logout(?:\.php)?(?:[?#]|$)/i.test(e.getAttribute('href') || '') || /^(?:se déconnecter|déconnexion|log out|logout|sign out)$/i.test((e.textContent || '').trim()));
                  const login = !search && (password || any('form#login,form[name=login],.login-form') || /(?:^|\/)(?:login|signin|sign-in)(?:\.php)?(?:\/|$)/i.test(location.pathname));
                  const table = any('table,[role=grid],[role=table]');
                  const glpi = search || /glpi/i.test(document.querySelector('meta[name=generator]')?.content || '') || (/glpi/i.test(document.title) && authenticated);
                  return {password,challenge,search,authenticated,login,table,glpi,native};
                }
                """, null, ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
            if (!string.IsNullOrWhiteSpace(settings.ReadySelector) && settings.SelectorOrigin == Origin(frame.Url))
            {
                var ready = frame.Locator(settings.ReadySelector);
                probe.ExplicitReady = await ready.CountAsync() == 1 && await ready.IsVisibleAsync();
                if (probe.ExplicitReady && !probe.Challenge) probe.Login = false;
            }
            return probe;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return null; }
    }

    private static bool TrustedTableOrigin(string origin, AppSettings settings) =>
        origin.Length > 0 && (origin == Origin(settings.PortalUrl) ||
        (settings.TableFrameOrigin.Length > 0 && origin == settings.TableFrameOrigin));

    private async Task<SessionProbe?> ProbeAsync(IPage page, AppSettings settings, CancellationToken ct)
    {
        if (page.IsClosed || Origin(page.Url).Length == 0) return null;
        var probe = await ProbeFrameAsync(page.MainFrame, settings, ct);
        if (probe == null || Origin(page.Url) != Origin(settings.PortalUrl)) return probe;
        foreach (var frame in page.Frames.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (frame == page.MainFrame || frame.IsDetached || !TrustedTableOrigin(Origin(frame.Url), settings)) continue;
            try
            {
                // A nested frame is useful only if every enclosing iframe is visible.
                var visible = true;
                for (IFrame? current = frame; current != null && current != page.MainFrame; current = current.ParentFrame)
                {
                    await using var element = await current.FrameElementAsync().WaitAsync(ct);
                    if (!await element.IsVisibleAsync().WaitAsync(ct)) { visible = false; break; }
                }
                if (!visible) continue;
                var child = await ProbeFrameAsync(frame, settings, ct);
                if (child == null) continue;
                probe.Password |= child.Password;
                probe.Challenge |= child.Challenge;
                probe.Login |= child.Login;
                probe.Search |= child.Search;
                probe.Table |= child.Table;
                probe.Native |= child.Native;
                probe.Glpi |= child.Glpi;
                probe.Authenticated |= child.Authenticated;
                probe.ExplicitReady |= child.ExplicitReady;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { /* A navigating frame will be probed again. */ }
        }
        return probe;
    }

    private async Task NavigateTargetAsync(IPage page, string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Log?.Invoke("CIBLE · navigation vers " + AppPaths.SafeUrl(url) + $" (paramètres de recherche conservés : {(new Uri(url).Query.Length > 0 ? "oui" : "aucun")}).");
        // Finish the bounded browser action before releasing the operation gate.
        // WaitAsync(ct) alone would leave an old navigation running into the next run.
        try { await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 }); }
        catch (TimeoutException) { Log?.Invoke("SSO · navigation en cours ; poursuite de l’attente automatique."); }
        catch (PlaywrightException ex) when (ex.Message.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("interrupted by another navigation", StringComparison.OrdinalIgnoreCase))
        { Log?.Invoke("SSO · redirection détectée, suivi du navigateur."); }
        ct.ThrowIfCancellationRequested();
    }

    private int PageScore(IPage page, SessionProbe probe, AppSettings settings, IPage? preferred)
    {
        var sameOrigin = Origin(page.Url) == Origin(settings.PortalUrl);
        // Another already-authenticated GLPI instance must never outrank the
        // requested portal, even while that portal is displaying its login page.
        // An identity-provider page may still be followed until the target opens.
        if (!sameOrigin)
            return ReferenceEquals(page, preferred) ? -10000 : ReferenceEquals(page, ActivePage) ? -10001 : -20000;
        var samePath = TargetUrlPolicy.SamePage(page.Url, settings.PortalUrl);
        var score = samePath ? 100 : sameOrigin ? 30 : 0;
        if (TargetUrlPolicy.IncludesSearch(page.Url, settings.PortalUrl)) score += 20;
        if (probe.Search) score += 100;
        if (probe.Glpi) score += 30;
        if (probe.Authenticated) score += 15;
        if (probe.ExplicitReady) score += 90;
        if (probe.Table && (sameOrigin || probe.Glpi)) score += 20;
        if (probe.Password || probe.Challenge) score -= 200;
        if (probe.Login) score -= 100;
        if (ReferenceEquals(page, preferred)) score += 2;
        if (ReferenceEquals(page, ActivePage)) score += 1;
        return score;
    }

    public async Task<IPage?> FindBestPageAsync(AppSettings settings, IPage? preferred, CancellationToken ct)
    {
        if (_context == null) return null;
        IPage? best = null;
        var bestScore = int.MinValue;
        foreach (var page in _context.Pages.ToArray())
        {
            if (IsTicketDetailPage(page)) continue;
            var probe = await ProbeAsync(page, settings, ct);
            if (probe == null) continue;
            var score = PageScore(page, probe, settings, preferred);
            if (score > bestScore) { best = page; bestScore = score; }
        }
        best ??= preferred is { IsClosed: false } && !IsTicketDetailPage(preferred) ? preferred : ActivePage ?? _context.Pages.FirstOrDefault(p => !p.IsClosed && !IsTicketDetailPage(p));
        if (best != null && Origin(best.Url) != Origin(settings.PortalUrl) && preferred is { IsClosed: false } && !IsTicketDetailPage(preferred))
            best = preferred; // Keep following the original SSO tab if its probe is temporarily unavailable.
        if (best != null) SetActivePage(best);
        return best;
    }

    public async Task<IPage> OpenTargetAsync(AppSettings settings, CancellationToken ct)
    {
        await OpenAsync(settings, ct);
        return await WaitForTargetAsync(settings, ct);
    }

    public async Task<IPage> WaitForTargetAsync(AppSettings settings, CancellationToken ct)
    {
        var target = TargetUrlPolicy.Validate(settings.PortalUrl).AbsoluteUri;
        var timer = Stopwatch.StartNew();
        var targetRetries = 0;
        var loginRefreshes = 0;
        var lastRetryAt = TimeSpan.FromSeconds(-30);
        var lastStatusAt = TimeSpan.FromSeconds(-10);
        IPage? stablePage = null;
        string stableUrl = "";
        TimeSpan stableSince = TimeSpan.Zero;
        var reenteredAfterAuth = false;
        bool? previousChallenge = null;
        Log?.Invoke($"SSO · attente automatique, délai {settings.TimeoutSeconds} s ; nouveaux onglets suivis.");
        ConnectionProgress?.Invoke("Connexion SSO automatique en cours… Le navigateur reste accessible.");
        while (timer.Elapsed < TimeSpan.FromSeconds(settings.TimeoutSeconds))
        {
            ct.ThrowIfCancellationRequested();
            if (_context == null) throw new InvalidOperationException("Le navigateur a été fermé pendant la connexion.");
            var page = await FindBestPageAsync(settings, ActivePage, ct);
            if (page == null) { await Task.Delay(350, ct); continue; }
            var probe = await ProbeAsync(page, settings, ct);
            if (probe == null) { await Task.Delay(350, ct); continue; }
            if (probe.Login || probe.Challenge) _authenticationNavigationObserved = true;
            if (!ReferenceEquals(stablePage, page) || stableUrl != page.Url)
            { stablePage = page; stableUrl = page.Url; stableSince = timer.Elapsed; }
            var stableFor = timer.Elapsed - stableSince;
            var samePath = TargetUrlPolicy.SamePage(page.Url, target);
            var sameOrigin = Origin(page.Url) == Origin(target);
            var dataReady = probe.Readable && (settings.AutoMode != "native" || probe.Search || probe.Native || probe.ExplicitReady
                || probe.Table && (probe.Authenticated || settings.Automation.AutomaticPaginationFallback));
            if (dataReady && samePath && stableFor.TotalMilliseconds >= 700)
            {
                if (samePath && !reenteredAfterAuth && !TargetUrlPolicy.IncludesSearch(page.Url, target))
                {
                    reenteredAfterAuth = true; targetRetries++;
                    Log?.Invoke("CIBLE · les paramètres ne sont plus dans l’adresse affichée ; réouverture unique de la recherche complète mémorisée.");
                    await NavigateTargetAsync(page, target, ct);
                    lastRetryAt = timer.Elapsed; stableSince = timer.Elapsed;
                    continue;
                }
                SetActivePage(page);
                if (BackgroundMode) await SetPageWindowAsync(page, false, ct);
                Log?.Invoke($"SSO · page cible disponible après {timer.Elapsed.TotalSeconds:F1} s ; authentification observée={_authenticationNavigationObserved}, reprises={targetRetries}, actualisations={loginRefreshes}.");
                ConnectionProgress?.Invoke("Connexion établie. La recherche demandée est prête.");
                return page;
            }

            var authenticatedLanding = !probe.Challenge && !probe.Login && (probe.Authenticated || probe.Search || probe.ExplicitReady);
            if (!samePath && sameOrigin && authenticatedLanding && stableFor.TotalSeconds >= 1 && targetRetries < 3 && (timer.Elapsed - lastRetryAt).TotalSeconds >= 3)
            {
                targetRetries++;
                Log?.Invoke("CIBLE · connexion terminée sur une autre page ; retour automatique à la recherche demandée.");
                await NavigateTargetAsync(page, target, ct);
                lastRetryAt = timer.Elapsed; stableSince = timer.Elapsed;
                continue;
            }

            // A stale portal login screen sometimes needs a GET after SSO has completed.
            // Never refresh an identity-provider page, password form or MFA challenge.
            if (sameOrigin && probe.Login && !probe.Challenge && loginRefreshes < 2 && stableFor.TotalSeconds >= (loginRefreshes == 0 ? 8 : 20) && (timer.Elapsed - lastRetryAt).TotalSeconds >= 8)
            {
                loginRefreshes++;
                Log?.Invoke($"SSO · écran du portail inchangé ; actualisation automatique {loginRefreshes}/2.");
                await NavigateTargetAsync(page, target, ct);
                lastRetryAt = timer.Elapsed; stableSince = timer.Elapsed;
                continue;
            }
            if ((timer.Elapsed - lastStatusAt).TotalSeconds >= 5 || previousChallenge != probe.Challenge)
            {
                lastStatusAt = timer.Elapsed;
                previousChallenge = probe.Challenge;
                Log?.Invoke($"SSO · attente {timer.Elapsed.TotalSeconds:F0}/{settings.TimeoutSeconds} s ; onglets={_context.Pages.Count}, cible={(samePath ? "oui" : "non")}, authentification={(probe.Login || probe.Challenge ? "en cours" : "non détectée")}, tableau={(probe.Table || probe.Search ? "oui" : "non")}.");
                ConnectionProgress?.Invoke(probe.Challenge
                    ? "SSO : saisie ou validation détectée. Utilisez « Voir le navigateur » ; la suite reprendra automatiquement."
                    : $"Connexion SSO en cours ({timer.Elapsed.TotalSeconds:F0} s). « Voir le navigateur » permet de suivre la connexion.");
            }
            await Task.Delay(400, ct);
        }
        throw new TimeoutException("La recherche GLPI n’est pas apparue dans le délai prévu. Utilise « Voir le navigateur » pour vérifier le SSO, puis relance « Extraire tout ». Le délai SSO peut être augmenté dans Réglages ; le diagnostic reste disponible.");
    }

    public async Task<TableChoice> ChooseAutoTableAsync(IPage page, AppSettings settings, CancellationToken ct, bool quiet = false)
    {
        var tables = (await InspectAsync(page, ct, quiet)).Where(table => TrustedTableOrigin(table.FrameOrigin, settings)).ToList();
        if (tables.Count == 0) throw new InvalidOperationException("La page cible ne contient pas de tableau visible. Affiche la liste GLPI souhaitée puis mémorise cette page.");
        if (settings.TableHeaderFingerprint.Length > 0 && TargetUrlPolicy.SamePage(settings.TableTargetUrl, settings.PortalUrl))
        {
            var matches = tables.Where(t => t.HeaderFingerprint == settings.TableHeaderFingerprint && t.FrameOrigin == settings.TableFrameOrigin).ToList();
            var exact = matches.Where(t => t.Selector == settings.TableSelector && t.FrameName == settings.TableFrameName).ToList();
            if (exact.Count == 1) { if (!quiet) Log?.Invoke("TABLEAU · repère mémorisé retrouvé."); return exact[0]; }
            if (matches.Count == 1) { if (!quiet) Log?.Invoke("TABLEAU · colonnes mémorisées retrouvées malgré le changement d’identifiant HTML."); return matches[0]; }
        }
        var ranked = new List<(TableChoice Table, int Score)>();
        foreach (var table in tables)
        {
            var recognition = await table.Frame!.Locator(table.Selector).EvaluateAsync<int[]>("""
                e => {
                  const form = e.closest('form[data-search-itemtype][data-count]');
                  const raw = form?.getAttribute('data-count')?.trim() || '';
                  const official = e.matches('table.search-results');
                  const empty = official && /^\d+$/.test(raw) && Number(raw) === 0;
                  return [official ? 1000 : form ? 800 : e.matches('[data-glpi-search-results]') ? 700 : 0, empty ? 1 : 0];
                }
                """).WaitAsync(ct);
            var score = recognition[0] + Math.Min(table.RowCount, 50) + Math.Min(table.ColumnCount, 30) * 2;
            if (table.ColumnCount < 2 && recognition[1] != 1) score = 0;
            ranked.Add((table, score));
        }
        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (ranked[0].Score <= 0 || (ranked.Count > 1 && ranked[0].Score == ranked[1].Score))
            throw new InvalidOperationException("Plusieurs tableaux se ressemblent. Dans Outils manuels, désigne le tableau une seule fois puis clique sur « Mémoriser cette page ».");
        var selected = ranked[0].Table;
        if (!quiet) Log?.Invoke($"TABLEAU · sélection automatique parmi {tables.Count} tableaux ; {selected.ColumnCount} colonnes, {selected.RowCount} lignes visibles.");
        return selected;
    }

    public async Task<int?> ReadExpectedTotalAsync(TableChoice table, CancellationToken ct)
    {
        var (_, snapshot, _) = await ReadAsync(table, "", ct);
        return snapshot.ExpectedTotal;
    }

    public async Task<TableChoice> WaitForAutoTableAsync(IPage page, AppSettings settings, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        string reason = "Le tableau de recherche n’est pas encore prêt.";
        string tableState = "", exportState = "";
        TimeSpan unsupportedSince = TimeSpan.Zero;
        NativeExportInspection? lastInspection = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var table = await ChooseAutoTableAsync(page, settings, ct, quiet: true);
                var state = table.Selector + "|" + table.HeaderFingerprint + "|" + table.RowCount;
                if (state != tableState)
                {
                    tableState = state;
                    Log?.Invoke($"TABLEAU · tableau de recherche retenu ; {table.ColumnCount} colonnes, {table.RowCount} lignes visibles.");
                }
                if (settings.AutoMode != "native" || settings.NativeSubmitSelector.Length > 0) return table;
                var native = await InspectNativeExportAsync(table, ct);
                lastInspection = native;
                if (native.ExpectedTotal == 0 && await table.Frame!.Locator(table.Selector)
                    .EvaluateAsync<bool>("e => e.matches('table.search-results') && !!e.closest('form[data-search-itemtype][data-count]')").WaitAsync(ct))
                {
                    Log?.Invoke("TABLEAU · recherche GLPI vide confirmée ; aucun menu d’export n’est attendu.");
                    return table;
                }
                if (native.Plans.Count == 1 || native.DisplayPlan != null)
                {
                    LogNativeInspection(native);
                    return table;
                }
                reason = "L’export de toutes les pages n’a pas été reconnu. " + native.Reason;
                var export = state + "|" + native.Reason + "|" + string.Join("|", native.Diagnostics);
                if (export != exportState)
                {
                    exportState = export;
                    unsupportedSince = timer.Elapsed;
                    Log?.Invoke("EXPORT · vérification des contrôles de la page ; attente d’un éventuel chargement différé…");
                }
                // A stable, unsupported toolbar is not a loading screen. Give
                // late scripts a short grace period without flooding the log.
                if (timer.Elapsed - unsupportedSince >= TimeSpan.FromSeconds(5))
                {
                    if (settings.Automation.AutomaticPaginationFallback && native.Unavailable)
                    {
                        LogNativeInspection(native);
                        Log?.Invoke("EXPORT · aucun export natif identifiable après stabilisation ; la lecture paginée depuis le début est autorisée.");
                        return table;
                    }
                    break;
                }
            }
            catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
            { reason = AppPaths.SafeError(ex); exportState = ""; }
            await Task.Delay(500, ct);
        } while (timer.Elapsed < TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 2, 30)));
        if (lastInspection != null) LogNativeInspection(lastInspection);
        throw new InvalidOperationException(reason + " Le diagnostic contient les contrôles d’export et d’affichage détectés. Copie-le pour adapter les repères.");
    }
}
