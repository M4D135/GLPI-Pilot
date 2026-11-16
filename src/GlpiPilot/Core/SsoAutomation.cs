using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    public string ResolvedTargetUrl { get; private set; } = "";
    private readonly HashSet<(IFrame Frame, string Url, string Kind)> _connectionActions = new();
    private int _connectionActionCount;
    private bool _supervisorSelected;

    private sealed class ConnectionAction
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
    }

    private void ResetConnectionActions()
    {
        ResolvedTargetUrl = "";
        _connectionActions.Clear();
        _connectionActionCount = 0;
        _supervisorSelected = false;
    }

    private const string ConnectionDom = """
        const visible = e => e && e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden' && !e.closest('[hidden],[aria-hidden=true]');
        const enabled = e => visible(e) && !e.disabled && e.getAttribute('aria-disabled') !== 'true';
        const norm = t => (t || '').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/[’‘]/g,"'").replace(/\s+/g,' ').trim().toLowerCase();
        const label = e => norm(e.getAttribute('aria-label') || (e.matches('input') ? e.getAttribute('value') : e.textContent));
        const sensitive = () => [...document.querySelectorAll('input[type=password],input[autocomplete=username],input[type=email],input[autocomplete=one-time-code],input[name=otp],input[name=otc],input[name=verificationCode],iframe[title*=captcha i],iframe[src*=captcha i],[class*=captcha],[id*=captcha]')].some(visible);
        const none = {kind:''};
        """;

    // All mutations are synchronous in the browser. Await their completion before
    // observing cancellation so an abandoned action cannot reach the next run.
    private static async Task<ConnectionAction> RunConnectionActionAsync(IFrame frame, string body, object args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var json = await frame.EvaluateAsync<string>("args => {" + ConnectionDom + "return JSON.stringify((() => {" + body + "})());}", args);
        ct.ThrowIfCancellationRequested();
        return JsonSerializer.Deserialize<ConnectionAction>(json)!;
    }

    private async Task<bool> TryRememberSsoAsync(IPage page, AppSettings settings, CancellationToken ct)
    {
        if (!settings.Automation.SsoRememberMe || _connectionActionCount >= settings.Automation.SsoAutomationMaxActions || page.IsClosed) return false;
        // Called only for the selected connection page or its verified opener popup.
        foreach (var frame in page.Frames.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (frame.IsDetached || Origin(frame.Url).Length == 0) continue;
            var key = (frame, frame.Url, "remember");
            if (_connectionActions.Contains(key)) continue;
            try
            {
                var visible = true;
                for (IFrame? current = frame; current != null && current != page.MainFrame; current = current.ParentFrame)
                {
                    await using var element = await current.FrameElementAsync().WaitAsync(ct);
                    if (!await element.IsVisibleAsync().WaitAsync(ct)) { visible = false; break; }
                }
                if (!visible) continue;
                var result = await RunConnectionActionAsync(frame, """
                    if (sensitive() || document.__glpiPilotRememberSubmitted) return none;
                    const boxes = [...document.querySelectorAll('input[type=checkbox]')].filter(enabled);
                    const remembers = boxes.filter(e => /^(?:se souvenir de (?:mon choix|moi)|rester connecte|garder ma session|memoriser (?:mon choix|ma connexion|mon compte)|remember (?:me|my choice)|keep me signed in|stay signed in)$/.test(norm(e.getAttribute('aria-label') || [...(e.labels || [])].map(l => l.textContent).join(' '))));
                    if (remembers.length !== 1 || boxes.length !== 1) return none;
                    const remember = remembers[0], form = remember.form;
                    const controls = [...document.querySelectorAll('button,input[type=submit],input[type=button],[role=button]')].filter(enabled);
                    const buttons = controls.filter(e => /^(?:se connecter|connexion|sign in|log in|login|continuer|continue|valider|confirmer|oui|yes)$/.test(label(e)) && (form ? e.form === form || form.contains(e) : !e.form));
                    if (buttons.length !== 1) return none;
                    const scope = form || document;
                    // No account, provider, OTP, consent or other choice is made on the user's behalf.
                    if ([...scope.querySelectorAll('input:not([type=hidden]):not([type=checkbox]):not([type=submit]):not([type=button]),textarea')].some(visible)) return none;
                    if ([...scope.querySelectorAll('select')].filter(visible).some(s => s.selectedOptions.length !== 1 || !s.value || s.selectedOptions[0].disabled)) return none;
                    document.__glpiPilotRememberSubmitted = true;
                    if (!remember.checked) remember.click();
                    if (!remember.checked || !enabled(buttons[0]) || sensitive()) return none;
                    buttons[0].click();
                    return {kind:'remember'};
                    """, new { }, ct);
                if (result.Kind != "remember") continue;
                _connectionActions.Add(key); _connectionActionCount++;
                Log?.Invoke("SSO · préférence « Se souvenir de mon choix / Se souvenir de moi » activée ; continuation automatique effectuée une seule fois.");
                return true;
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase))
            {
                // Navigation may destroy the return value after the click. Never submit twice.
                _connectionActions.Add(key); _connectionActionCount++;
                return true;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { /* Retry observation, not an unrecognized action. */ }
        }
        return false;
    }

    private async Task<bool> TryRecoverTicketNavigationAsync(IPage page, AppSettings settings, CancellationToken ct)
    {
        if (!settings.Automation.SsoProfileMenuNavigation || _connectionActionCount >= settings.Automation.SsoAutomationMaxActions
            || page.IsClosed || Origin(page.Url) != Origin(settings.PortalUrl)
            || !(TargetUrlPolicy.IsPortalRoot(settings.PortalUrl) || TargetUrlPolicy.IsTicketList(settings.PortalUrl, settings.PortalUrl))) return false;
        var frame = page.MainFrame;
        var key = (frame, frame.Url, "menu");
        try
        {
            var result = await RunConnectionActionAsync(frame, """
                if (sensitive()) return none;
                const safe = value => { try { const u = new URL(value, location.href); return u.origin === args.origin && u.pathname.startsWith(args.base) && !u.username && !u.password ? u : null; } catch { return null; } };
                const supervisor = e => /^(?:superviseur|supervisor)$/.test(label(e));
                if (!args.profileDone) {
                    const selectors = [...document.querySelectorAll('select[name=profiles_id],select[name=newprofile]')].filter(enabled);
                    const choices = selectors.flatMap(s => [...s.options].filter(o => !o.disabled && supervisor(o)).map(o => ({s,o})));
                    if (choices.length === 1 && choices[0].s.value !== choices[0].o.value) {
                        const {s,o} = choices[0], form = s.form;
                        if (!form || !safe(form.action)) return none;
                        const submits = [...form.querySelectorAll('button[type=submit],input[type=submit],button:not([type])')].filter(enabled);
                        if (!s.onchange && submits.length !== 1) return none;
                        s.value = o.value;
                        // GLPI versions with onchange submit themselves; never also submit the form.
                        if (s.onchange) s.dispatchEvent(new Event('change',{bubbles:true}));
                        else submits[0].click();
                        return {kind:'profile'};
                    }
                    const profiles = [...document.querySelectorAll('a[href]')].filter(a => enabled(a) && supervisor(a) && /(?:[?&])(?:newprofile|profiles_id)=/.test(a.getAttribute('href') || '') && safe(a.href));
                    if (profiles.length === 1) { profiles[0].click(); return {kind:'profile'}; }
                    // Expand only an observed profile menu that actually contains Superviseur.
                    const hidden = [...document.querySelectorAll('a[href]')].filter(a => !visible(a) && supervisor(a) && /(?:[?&])(?:newprofile|profiles_id)=/.test(a.href) && safe(a.href));
                    if (hidden.length === 1) {
                        const container = hidden[0].closest('.dropdown,.nav-item');
                        const triggers = container ? [...container.querySelectorAll('[data-bs-toggle=dropdown],[data-toggle=dropdown],[aria-haspopup=true]')].filter(enabled) : [];
                        if (triggers.length === 1 && !args.expanded) { triggers[0].click(); return {kind:'expand-profile'}; }
                    }
                }
                const links = [...document.querySelectorAll('a[href]')].filter(enabled);
                const tickets = links.filter(a => { const u = safe(a.href); return u && /(?:^|\/)front\/ticket\.php$/i.test(u.pathname) && /^tickets?$/.test(label(a)); });
                const urls = [...new Set(tickets.map(a => a.href))];
                if (urls.length === 1) return {kind:'tickets',url:urls[0]};
                if (!args.expanded) {
                    const assistance = [...document.querySelectorAll('a[href],button')].filter(e => enabled(e) && /^(?:assistance|helpdesk)$/.test(label(e)) && (!e.matches('a') || safe(e.href)));
                    if (assistance.length === 1) { assistance[0].click(); return {kind:'expand-assistance'}; }
                }
                return none;
                """, new { origin = Origin(settings.PortalUrl), @base = TargetUrlPolicy.DeploymentBase(settings.PortalUrl), profileDone = _supervisorSelected, expanded = _connectionActions.Contains(key) }, ct);
            if (result.Kind.Length == 0) return false;
            if (result.Kind == "tickets")
            {
                if (_connectionActions.Contains((frame, result.Url, "tickets"))) return false;
                if (!TargetUrlPolicy.IsTicketList(result.Url, settings.PortalUrl)) return false;
                var target = TargetUrlPolicy.IsPortalRoot(settings.PortalUrl)
                    ? TargetUrlPolicy.MergeQuery(result.Url, settings.PortalUrl) : settings.PortalUrl;
                _connectionActions.Add((frame, result.Url, "tickets")); _connectionActionCount++;
                ResolvedTargetUrl = target;
                Log?.Invoke("MENU · Assistance > Tickets retrouvé ; recherche complète conservée.");
                await NavigateTargetAsync(page, target, ct);
            }
            else
            {
                _connectionActions.Add(key); _connectionActionCount++;
                if (result.Kind == "profile") { _supervisorSelected = true; Log?.Invoke("PROFIL · Superviseur sélectionné parmi les profils accessibles."); }
                else Log?.Invoke("MENU · ouverture du menu GLPI pour retrouver Assistance > Tickets.");
            }
            return true;
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase))
        {
            _connectionActions.Add(key); _connectionActionCount++;
            return true;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return false; }
    }
}
