using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private string? _nativeScript;

    private void LogNativeInspection(NativeExportInspection inspection)
    {
        Log?.Invoke($"EXPORT · détection : plans={inspection.Plans.Count}, total annoncé={inspection.ExpectedTotal?.ToString() ?? "inconnu"}. {inspection.Reason}");
        foreach (var detail in inspection.Diagnostics.Take(16))
            Log?.Invoke("EXPORT-DIAG · " + AppPaths.SafeLog(detail.Length > 300 ? detail[..300] : detail));
    }

    public async Task<NativeExportInspection> InspectNativeExportAsync(TableChoice table, CancellationToken ct)
    {
        var frame = await ResolveFrameAsync(table, ct);
        if (_nativeScript == null)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("BrowserScripts.native-export.js", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            _nativeScript = reader.ReadToEnd();
        }
        await frame.EvaluateAsync(_nativeScript).WaitAsync(ct);
        return await ReadJsonAsync<NativeExportInspection>(frame, "selector => window.__glpiPilotNative.inspect(selector)", table.Selector, ct);
    }

    public async Task<NativeDownloadResult> ExportAllNativeAsync(TableChoice table, AppSettings settings, CancellationToken ct)
    {
        var custom = settings.NativeMenuSelector.Length > 0 || settings.NativeSubmitSelector.Length > 0 || settings.NativeFormatSelector.Length > 0 || settings.NativeScopeSelector.Length > 0;
        var frame = await ResolveFrameAsync(table, ct);
        NativeExportInspection inspection;
        NativeExportPlan plan;
        if (custom)
        {
            if (settings.NativeSelectorOrigin != Origin(frame.Url))
                throw new InvalidOperationException("Les repères d’export appartiennent à une autre adresse. Mémorise la page et enregistre de nouveau les repères dans Réglages.");
            if (settings.NativeMenuSelector.Length > 0)
                await ClickNativeControlAsync(frame, settings.NativeMenuSelector, true, table.FrameOrigin, ct);
            plan = await BuildCustomNativePlanAsync(frame, settings, ct);
            inspection = await InspectNativeExportAsync(table, ct);
            Log?.Invoke("EXPORT · repères personnalisés CSV / Toutes les pages validés.");
        }
        else
        {
            inspection = await InspectNativeExportAsync(table, ct);
            for (var opened = 0; opened < 2 && inspection.Plans.Count == 1 && inspection.Plans[0].Kind == "menu"; opened++)
            {
                var menu = inspection.Plans[0];
                Log?.Invoke("EXPORT · ouverture du menu d’export GLPI.");
                await ClickNativeControlAsync(frame, menu.MenuSelector, true, table.FrameOrigin, ct);
                // Bootstrap renders immediately; a small bounded poll also covers a lazy menu.
                for (var poll = 0; poll < 10; poll++)
                {
                    await Task.Delay(150, ct);
                    inspection = await InspectNativeExportAsync(table, ct);
                    if (inspection.Plans.Count == 1 && inspection.Plans[0].Kind != "menu") break;
                }
            }
            if (!inspection.Supported || inspection.Plans.Count != 1)
            {
                LogNativeInspection(inspection);
                if (inspection.Unavailable)
                    throw new NativeExportUnavailableException("Aucune commande CSV Toutes les pages n’est identifiable sur ce tableau.");
                throw new InvalidOperationException("L’export CSV de toutes les pages n’a pas été identifié sans ambiguïté. " + inspection.Reason + " Copie le diagnostic ; les repères d’export peuvent être mémorisés dans Réglages.");
            }
            plan = inspection.Plans[0];
        }
        if (!custom && plan.Kind == "menu")
            throw new NativeExportUnavailableException("Le menu d’export est présent, mais aucune option CSV Toutes les pages n’est disponible après son ouverture.");
        if (!plan.AllPages || plan.Format != "csv" || plan.Kind is not ("direct" or "form"))
            throw new InvalidOperationException("L’option Toutes les pages en CSV n’a pas été confirmée. L’export automatique s’arrête avant de récupérer seulement la page courante.");
        var observed = inspection.ExpectedTotal ?? await ReadExpectedTotalAsync(table, ct);
        if (settings.ExpectedTotal.HasValue && observed.HasValue && settings.ExpectedTotal != observed)
            throw new InvalidOperationException("Le total configuré ne correspond pas au total annoncé par GLPI. Corrigez le total attendu avant d’exporter.");
        var expected = settings.ExpectedTotal ?? observed;
        Log?.Invoke($"EXPORT · option toutes les pages CSV confirmée ; mode={plan.Kind}, total annoncé={(expected?.ToString() ?? "inconnu")}.");
        var path = await CaptureDownloadCoreAsync(table.Page!, settings.ExportFolder, async token =>
        {
            if (plan.Kind == "direct")
                await ClickNativeControlAsync(frame, plan.DirectSelector, false, table.FrameOrigin, token);
            else
            {
                if (plan.ScopeSelector.Length > 0)
                    await SelectNativeOptionAsync(frame, plan.ScopeSelector, plan.ScopeValue, true, token);
                if (_downloadWaiter?.Task.IsCompletedSuccessfully == true) return;
                if (plan.FormatSelector.Length > 0)
                    await SelectNativeOptionAsync(frame, plan.FormatSelector, plan.FormatValue, plan.FormatHidden, token);
                if (_downloadWaiter?.Task.IsCompletedSuccessfully == true) return;
                if (plan.SubmitSelector.Length == 0) throw new InvalidOperationException("Le bouton de validation de l’export est introuvable.");
                await ClickNativeControlAsync(frame, plan.SubmitSelector, false, table.FrameOrigin, token);
            }
        }, settings.DownloadTimeoutSeconds, ct);
        return new(path, expected, plan.Kind);
    }

    private async Task ClickNativeControlAsync(IFrame frame, string selector, bool menu, string expectedOrigin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Origin(frame.Url) != expectedOrigin) throw new InvalidOperationException("La page a quitté GLPI avant l’export.");
        var locator = frame.Locator(selector);
        if (await locator.CountAsync() != 1) throw new InvalidOperationException("Le repère d’export doit désigner un seul élément.");
        var safe = await locator.EvaluateAsync<bool>("""
            (el, arg) => {
              const text = [el.textContent,el.getAttribute('aria-label'),el.title,el.getAttribute('alt'),el.getAttribute('name'),el.getAttribute('value'),el.id,...[...el.querySelectorAll('img')].map(i => [i.alt,i.title].join(' '))].filter(Boolean).join(' ').normalize('NFD').replace(/[\u0300-\u036f]/g,'');
              if (/supprim|\bdelete\b|effac|clotur|archiv|\bpurge\b|\bupdate\b|\bsave\b|enregistr/i.test(text)) return false;
              if (el.matches(':disabled,[aria-disabled=true]') || el.closest('.disabled,[aria-disabled=true]')) return false;
              if (el.closest('.massiveactions-control')) return false;
              if (arg.menu) {
                if (el.tagName === 'BUTTON' && el.type === 'submit' && (el.form || el.closest('form'))) return false;
                const dropdown = el.getAttribute('data-bs-toggle') === 'dropdown' || el.getAttribute('data-toggle') === 'dropdown' || el.getAttribute('aria-haspopup') === 'menu';
                return dropdown && /export|download|telecharg|dropdown-export/i.test(text) && el.matches('button,a,[role=button],input[type=button]');
              }
              if (el.tagName === 'A') {
                const href = el.getAttribute('href');
                if (!href || /^javascript:/i.test(href.trim())) return false;
                const u = new URL(href, document.baseURI);
                if (!/^https?:$/.test(u.protocol) || u.origin !== arg.origin) return false;
                if ([...u.searchParams.keys()].some(k => /^(delete|purge|update|save|submit|archive)$/i.test(k))) return false;
                const modes = u.searchParams.getAll('display_type');
                return /\/front\/report\.dynamic\.php\/?$/i.test(u.pathname) && modes.length === 1 && /^-[1-9]\d*$/.test(modes[0]) && /csv|export|download|telecharg/i.test(text);
              }
              if (!el.matches('button,input[type=submit],input[type=image],input[type=button],[role=button]')) return false;
              if (!/export|download|telecharg|csv/i.test(text)) return false;
              const form = el.form || el.closest('form');
              if (!form) return false;
              const action = new URL(el.getAttribute('formaction') || form.getAttribute('action') || location.href, document.baseURI);
              const method = (el.getAttribute('formmethod') || form.getAttribute('method') || 'get').toLowerCase();
              if (action.origin !== arg.origin || !/^https?:$/.test(action.protocol) || method !== 'get' || !/\/front\/report\.dynamic\.php\/?$/i.test(action.pathname)) return false;
              if ([...form.querySelectorAll('[name]')].some(e => /^(delete|purge|remove|update|save|create|add|massiveaction|massive_action|archive|restore)$/i.test(e.name))) return false;
              return true;
            }
            """, new { menu, origin = expectedOrigin }).WaitAsync(ct);
        if (!safe) throw new InvalidOperationException("Le repère choisi n’est pas reconnu comme une commande d’export GLPI sur cette page.");
        try { await locator.ClickAsync(new() { Timeout = 7000 }); }
        catch (TimeoutException) when (_downloadWaiter?.Task.IsCompletedSuccessfully == true)
        { Log?.Invoke("EXPORT · téléchargement déjà démarré malgré le délai de navigation."); }
        ct.ThrowIfCancellationRequested();
    }

    private static async Task SelectNativeOptionAsync(IFrame frame, string selector, string value, bool force, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var locator = frame.Locator(selector);
        if (await locator.CountAsync() != 1) throw new InvalidOperationException("Le choix de format ou de portée ne désigne pas une liste unique.");
        await locator.SelectOptionAsync(new SelectOptionValue { Value = value }, new() { Timeout = 5000, Force = force });
        ct.ThrowIfCancellationRequested();
        if (await locator.InputValueAsync() != value) throw new InvalidOperationException("GLPI n’a pas retenu le format ou la portée demandée.");
    }

    private static bool AllPagesLabel(string text) => Regex.IsMatch(Regex.Replace(text.Normalize(System.Text.NormalizationForm.FormD), @"\p{M}", ""), @"all\s+(?:pages|results)|toutes?\s+(?:les\s+)?pages|tous\s+(?:les\s+)?resultats", RegexOptions.IgnoreCase);

    private async Task<NativeExportPlan> BuildCustomNativePlanAsync(IFrame frame, AppSettings settings, CancellationToken ct)
    {
        if (settings.NativeSubmitSelector.Length == 0) throw new InvalidOperationException("Renseigne le repère du lien ou du bouton d’export personnalisé.");
        var plan = new NativeExportPlan { Kind = settings.NativeFormatSelector.Length == 0 && settings.NativeScopeSelector.Length == 0 ? "direct" : "form", Format = "csv", SubmitSelector = settings.NativeSubmitSelector, DirectSelector = settings.NativeSubmitSelector };
        var csv = false;
        var all = false;
        if (settings.NativeFormatSelector.Length > 0)
        {
            var list = frame.Locator(settings.NativeFormatSelector);
            if (await list.CountAsync() != 1) throw new InvalidOperationException("Le sélecteur du format d’export est absent ou ambigu.");
            var options = await list.Locator("option").AllAsync();
            var matches = new List<ILocator>();
            foreach (var option in options) if (await option.GetAttributeAsync("value") == settings.NativeFormatValue) matches.Add(option);
            if (matches.Count != 1) throw new InvalidOperationException("La valeur mémorisée du format CSV n’existe pas ou n’est pas unique.");
            var label = await matches[0].TextContentAsync() ?? "";
            csv = label.Contains("CSV", StringComparison.OrdinalIgnoreCase);
            if (await list.GetAttributeAsync("name") == "display_type" && !Regex.IsMatch(settings.NativeFormatValue, @"^-[1-9]\d*$"))
                throw new InvalidOperationException("Le format natif GLPI doit désigner Toutes les pages (display_type négatif), pas la page courante.");
            all = AllPagesLabel(label) || (await list.GetAttributeAsync("name") == "display_type" && settings.NativeFormatValue.StartsWith('-') && csv);
            plan.FormatSelector = settings.NativeFormatSelector; plan.FormatValue = settings.NativeFormatValue;
            plan.FormatHidden = !await list.IsVisibleAsync();
        }
        if (settings.NativeScopeSelector.Length > 0)
        {
            var list = frame.Locator(settings.NativeScopeSelector);
            if (await list.CountAsync() != 1) throw new InvalidOperationException("Le sélecteur de portée de l’export est absent ou ambigu.");
            var matches = new List<ILocator>();
            foreach (var option in await list.Locator("option").AllAsync()) if (await option.GetAttributeAsync("value") == settings.NativeScopeValue) matches.Add(option);
            if (matches.Count != 1 || !AllPagesLabel(await matches[0].TextContentAsync() ?? ""))
                throw new InvalidOperationException("La portée personnalisée doit être une option explicitement libellée Toutes les pages.");
            all = true; plan.ScopeSelector = settings.NativeScopeSelector; plan.ScopeValue = settings.NativeScopeValue;
        }
        if (plan.Kind == "direct")
        {
            var link = frame.Locator(plan.DirectSelector);
            if (await link.CountAsync() != 1) throw new InvalidOperationException("Le lien d’export personnalisé est absent ou ambigu.");
            var label = (await link.TextContentAsync() ?? "") + " " + await link.GetAttributeAsync("title") + " " + await link.GetAttributeAsync("aria-label");
            csv = label.Contains("csv", StringComparison.OrdinalIgnoreCase);
            var nativeAll = await link.EvaluateAsync<bool>("""
                e => {
                  try {
                    const u = new URL(e.getAttribute('href'), document.baseURI), modes = u.searchParams.getAll('display_type');
                    return !e.hasAttribute('onclick') && u.origin === location.origin && /\/front\/report\.dynamic\.php\/?$/i.test(u.pathname) && modes.length === 1 && /^-[1-9]\d*$/.test(modes[0]);
                  } catch { return false; }
                }
                """).WaitAsync(ct);
            all = nativeAll;
        }
        if (!csv || !all) throw new InvalidOperationException("Les repères personnalisés ne confirment pas CSV + Toutes les pages. Corrige les options mémorisées.");
        plan.AllPages = true;
        return plan;
    }
}
