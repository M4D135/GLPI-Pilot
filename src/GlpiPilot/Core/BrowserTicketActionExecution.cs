using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private sealed record PendingTicketAction(IPage Page, IFrame Frame, TicketReference Ticket, TicketActionRequest Request, TicketFormState State, TicketActionPreview Preview, IReadOnlySet<string> ExistingIds);
    private readonly Dictionary<string, PendingTicketAction> _pendingTicketActions = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<TicketActionCandidate>> LearnTicketActionsAsync(TicketReference ticket, AppSettings settings, CancellationToken ct)
    {
        ticket = ValidateTicketReference(ticket, settings);
        await _ticketReadGate.WaitAsync(ct);
        try
        {
            _pendingTicketActions.Clear();
            var page = TicketPage ?? throw new InvalidOperationException("Ouvrez la fiche dans GLPI puis affichez le formulaire d’affectation, de suivi ou de tâche à inspecter.");
            var forms = await InspectActionFormsAsync(page, ticket, ct);
            return forms.Select(item => new TicketActionCandidate(new TicketActionAdapter
            {
                Name = TicketActionService.Operation(item.State.Kind) + " · " + item.State.FieldName,
                Kind = item.State.Kind, Installation = TicketActionService.Installation(ticket), FormSelector = item.State.Selector,
                ActionPath = item.State.ActionPath, FieldName = item.State.FieldName, SubmitName = item.State.SubmitName,
                SubmitValue = item.State.SubmitValue, StructureHash = TicketActionService.Hash(item.State.Structure)
            }, item.State.Choices, item.State.Current, item.State.OtherFields)).ToArray();
        }
        catch (PlaywrightException) { throw new InvalidOperationException("Le formulaire a changé pendant l’inspection. Ouvrez-le à nouveau et relancez l’inspection."); }
        finally { _ticketReadGate.Release(); }
    }

    public async Task<TicketActionPreview> PreviewTicketActionAsync(TicketReference ticket, TicketActionRequest request, AppSettings settings, CancellationToken ct)
    {
        ticket = ValidateTicketReference(ticket, settings);
        var adapter = TicketActionService.Validate(request.Adapter, ticket);
        var value = TicketActionService.ValidateValue(adapter.Kind, request.Value);
        await _ticketReadGate.WaitAsync(ct);
        try
        {
            _pendingTicketActions.Clear();
            var page = TicketPage ?? throw new InvalidOperationException("La fiche GLPI n’est plus ouverte. Inspectez à nouveau son formulaire.");
            var forms = await InspectActionFormsAsync(page, ticket, ct);
            var matches = forms.Where(item => MatchesAdapter(item.State, adapter)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Le formulaire mémorisé est absent, ambigu ou a changé de structure. Inspectez le formulaire affiché dans GLPI.");
            var (frame, state) = matches[0];
            var token = Guid.NewGuid().ToString("N");
            var display = value;
            if (adapter.Kind == "assignment")
            {
                var choice = state.Choices.SingleOrDefault(choice => choice.Value == value) ?? throw new InvalidOperationException("Cette affectation n’est pas un choix actuellement proposé par GLPI.");
                display = choice.Label + " (identifiant " + choice.Value + ")";
            }
            else
            {
                // A visible per-action reference makes readback and manual duplicate checks unambiguous.
                value += "\n\n[Référence GLPI Pilot : " + token[..12] + "]";
                display = value;
            }
            var before = adapter.Kind == "assignment" ? state.Choices.FirstOrDefault(choice => choice.Value == state.Current)?.Label ?? state.Current : state.Current;
            var evidence = await ReadActionEvidenceAsync(page, ticket, adapter.Kind, ct);
            var preview = new TicketActionPreview(token, ticket, TicketActionService.Operation(adapter.Kind), before.Length == 0 ? "(vide)" : before, display, state.OtherFields, DateTimeOffset.UtcNow.AddMinutes(2));
            _pendingTicketActions[token] = new(page, frame, ticket, new(adapter, value, display), state, preview, evidence.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal));
            return preview;
        }
        catch (PlaywrightException) { throw new InvalidOperationException("Le formulaire n’a pas pu être prévisualisé ; aucune soumission n’a été effectuée."); }
        finally { _ticketReadGate.Release(); }
    }

    private static bool MatchesAdapter(TicketFormState state, TicketActionAdapter adapter) => state.Selector == adapter.FormSelector && state.Kind == adapter.Kind
        && state.FieldName == adapter.FieldName && state.ActionPath == adapter.ActionPath && state.SubmitName == adapter.SubmitName && state.SubmitValue == adapter.SubmitValue
        && TicketActionService.Hash(state.Structure) == adapter.StructureHash;

    public async Task<TicketActionOutcome> ConfirmTicketActionAsync(string previewToken, bool explicitlyConfirmed, AppSettings settings, CancellationToken ct)
    {
        if (!explicitlyConfirmed) throw new InvalidOperationException("Une confirmation explicite de l’aperçu est nécessaire pour cette action.");
        await _ticketReadGate.WaitAsync(ct);
        var submitted = false;
        try
        {
            if (!_pendingTicketActions.Remove(previewToken, out var pending)) throw new InvalidOperationException("Cet aperçu est absent ou a déjà été consommé. Aucun nouvel envoi n’a été effectué.");
            if (pending.Preview.ExpiresAt < DateTimeOffset.UtcNow) throw new InvalidOperationException("Cet aperçu a expiré. Prévisualisez à nouveau le formulaire.");
            _ = ValidateTicketReference(pending.Ticket, settings);
            _ = TicketActionService.Validate(pending.Request.Adapter, pending.Ticket);
            if (!ReferenceEquals(TicketPage, pending.Page)) throw new InvalidOperationException("La fiche a changé depuis l’aperçu. Aucune soumission n’a été effectuée.");
            var forms = await InspectActionFormsAsync(pending.Page, pending.Ticket, ct);
            var matches = forms.Where(item => ReferenceEquals(item.Frame, pending.Frame) && MatchesAdapter(item.State, pending.Request.Adapter)).ToArray();
            if (matches.Length != 1 || matches[0].State.Snapshot != pending.State.Snapshot) throw new InvalidOperationException("Les valeurs ou la structure du formulaire ont changé depuis l’aperçu. Prévisualisez-les à nouveau avant confirmation.");
            var state = matches[0].State; var adapter = pending.Request.Adapter;
            var form = pending.Frame.Locator("css=" + state.Selector);
            var field = form.Locator("css=[name='" + state.FieldName + "']");
            if (adapter.Kind == "assignment") await field.SelectOptionAsync(pending.Request.Value, new() { Timeout = 10000, Force = state.HiddenSelectWidget }).WaitAsync(ct);
            else if (state.EditorId.Length > 0)
            {
                // The inspector proved the textarea/TinyMCE/iframe association. Locate that same iframe by its DOM id.
                var escaped = await pending.Frame.EvaluateAsync<string>("id => CSS.escape(id + '_ifr')", state.EditorId).WaitAsync(ct);
                await pending.Frame.FrameLocator("css=#" + escaped).Locator("body[contenteditable='true']").FillAsync(pending.Request.Value, new() { Timeout = 10000 }).WaitAsync(ct);
                await pending.Frame.EvaluateAsync("id => { const editor=window.tinymce?.get(id); if(!editor) throw new Error('editor unavailable'); editor.save(); }", state.EditorId).WaitAsync(ct);
            }
            else await field.FillAsync(pending.Request.Value, new() { Timeout = 10000 }).WaitAsync(ct);
            // Filling may fire portal handlers. Recheck the full form, identity, destination and resulting intended value before the one allowed click.
            var finalForms = await InspectActionFormsAsync(pending.Page, pending.Ticket, ct);
            var final = finalForms.Where(item => ReferenceEquals(item.Frame, pending.Frame) && MatchesAdapter(item.State, adapter)).ToArray();
            if (final.Length != 1 || NormalizeActionText(final[0].State.Current) != NormalizeActionText(pending.Request.Value)) throw new InvalidOperationException("Le formulaire n’a pas conservé la valeur prévue. Aucun envoi n’a été déclenché.");
            var beforeFields = System.Text.Json.JsonSerializer.Deserialize<string[][]>(state.Snapshot)!;
            var afterFields = System.Text.Json.JsonSerializer.Deserialize<string[][]>(final[0].State.Snapshot)!;
            if (!beforeFields.Where(pair => pair[0] != state.FieldName).Select(pair => string.Join("\0", pair)).SequenceEqual(afterFields.Where(pair => pair[0] != state.FieldName).Select(pair => string.Join("\0", pair))))
                throw new InvalidOperationException("Un autre champ a changé pendant la préparation. Aucun envoi n’a été déclenché ; inspectez le formulaire.");
            ct.ThrowIfCancellationRequested();
            var submit = form.Locator("css=[name='" + state.SubmitName + "']").Filter(new() { Visible = true });
            if (await submit.CountAsync().WaitAsync(ct) != 1 || await submit.GetAttributeAsync("value").WaitAsync(ct) is string actual && actual != state.SubmitValue)
                throw new InvalidOperationException("Le bouton de soumission a changé. Aucun envoi n’a été déclenché.");
            submitted = true; // From here any interruption is ambiguous and must never trigger an automatic retry.
            await submit.ClickAsync(new() { Timeout = Math.Clamp(settings.DetailTimeoutSeconds, 5, 600) * 1000 }).WaitAsync(ct);
            await NavigateTicketAsync(pending.Page, pending.Ticket.Url, ct);
            await WaitForTicketAsync(pending.Page, pending.Ticket, settings, settings.TicketDetails, null, ct);
            var verified = false;
            if (adapter.Kind == "assignment") verified = await VerifyAssignmentAsync(pending.Page, pending.Ticket, adapter.FieldName, pending.Request.Value, ct);
            else
            {
                var tabs = await DiscoverTicketTabsAsync(pending.Page, pending.Ticket, ct);
                var treatment = tabs.FirstOrDefault(tab => tab.Category == "treatment");
                if (treatment != null && !treatment.IsActive)
                    await ReadDiscoveredTicketTabAsync(pending.Page, pending.Ticket, settings.TicketDetails, new() { Ticket = pending.Ticket }, treatment, null, ct);
                var entries = await ReadActionEvidenceAsync(pending.Page, pending.Ticket, adapter.Kind, ct);
                verified = entries.Any(entry => !pending.ExistingIds.Contains(entry.Id) && entry.Text == NormalizeActionText(pending.Request.Value));
            }
            return verified ? new(true, "Soumission effectuée une fois et résultat vérifié après relecture de la fiche GLPI.")
                : new(false, "Soumission déclenchée une fois, résultat non confirmé par la relecture. Vérifiez la fiche GLPI et la référence de l’aperçu avant toute nouvelle action. Aucune relance automatique.");
        }
        catch (Exception error) when (submitted && error is not OutOfMemoryException)
        {
            return new(false, "Résultat indéterminé après déclenchement de la soumission. Vérifiez la fiche GLPI avant toute nouvelle action ; l’envoi n’a pas été répété.");
        }
        catch (PlaywrightException) { throw new InvalidOperationException("Le formulaire n’est plus exploitable ; aucune soumission n’a été déclenchée. Inspectez-le à nouveau."); }
        finally { _ticketReadGate.Release(); }
    }
    private static string NormalizeActionText(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private async Task<bool> VerifyAssignmentAsync(IPage page, TicketReference ticket, string fieldName, string value, CancellationToken ct)
    {
        foreach (var frame in await TicketFramesAsync(page, ticket, ct))
        {
            var verified = await frame.EvaluateAsync<bool>("""
            arg => {
              const visible=e=>!!e&&!e.closest('[hidden],[aria-hidden="true"]')&&!!(e.offsetWidth||e.offsetHeight||e.getClientRects().length);
              const inputs=[...document.querySelectorAll('select')].filter(e=>e.name===arg.field && (visible(e) || e.matches('.select2-hidden-accessible,.select2-offscreen,[data-select2-id]') && [e.previousElementSibling,e.nextElementSibling,e.id?document.getElementById('s2id_'+e.id):null].some(widget=>widget?.matches('.select2,.select2-container')&&visible(widget))));
              if(inputs.length===1 && inputs[0].value===arg.value) return true;
              const type=arg.field.includes('group')?'group':'user';
              return [...document.querySelectorAll('[data-actor-type="2"] a[href],[data-actortype="2"] a[href]')].filter(visible).some(a=> { const u=new URL(a.href,location.href); return u.origin===location.origin && u.pathname===arg.prefix+'front/'+type+'.form.php' && [...u.searchParams].length===1 && u.searchParams.get('id')===arg.value; });
            }
            """, new { field = fieldName, value, prefix = new Uri(TicketActionService.Installation(ticket)).AbsolutePath }).WaitAsync(ct);
            if (verified) return true;
        }
        return false;
    }
}
