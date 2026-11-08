import { el, button, toolbar, isSimulation } from '../ui.js';
import { call, run, note } from './workplace-common.js';

/** Commands retain the validated ticket key. Opening this hub never inspects,
 * submits a GLPI form, changes a ticket, or starts a collection implicitly. */
export function ticketControlHub(ctx, ticket, chooseAction) {
  const ticketKey = ticket.personal.key;
  const simulated = isSimulation(ctx.state.session);
  const capability = operation => !Array.isArray(ctx.state.capabilities) || ctx.state.capabilities.includes(operation);
  const navigate = (page, args) => run(ctx, () => ctx.navigate(page, args));
  const actions = toolbar(...[
    ['followup', 'Ajouter un suivi', 'log'],
    ['task', 'Ajouter une tâche', 'check'],
    ['assignment', 'Affecter', 'users']
  ].map(([kind, label, icon]) => button(label, () => chooseAction(kind), {
    icon, attributes: { 'data-ticket-command': kind, 'aria-pressed': 'false' }
  })), button('Toutes les actions', () => chooseAction(''), { attributes: { 'data-ticket-command': 'all', 'aria-pressed': 'true' } }));
  const native = button(simulated ? 'Consulter la fiche simulée' : 'Statut, priorité, résolution dans GLPI', run(ctx, async () => {
    await call(ctx, 'workplace.ticket.browser', { ticketKey });
    ctx.notify(simulated ? 'La fiche de simulation a été ouverte. Aucun échange GLPI.' : 'Fiche ouverte dans GLPI : vérifiez puis validez la modification dans son formulaire. Pilot n’a envoyé aucune modification.', 'info');
  }), { icon: 'ticket', disabled: !ticket.canRead, title: ticket.canRead ? 'Ouvrir la fiche, sans soumission automatique' : ticket.readReason || 'La fiche ne peut pas être ouverte dans cette source', attributes: { 'data-ticket-command': 'native' } });
  const local = el('details', { class: 'ticket-control-local' },
    el('summary', { text: 'Organiser l’intervention : planning, matériel et rappels' }),
    note('Organisation dans Pilot. Le planning, la préparation et les notes ne changent ni le statut ni l’affectation du ticket GLPI.'),
    toolbar(
      button('Planifier une intervention', navigate('interventions', { plan: true, ticketKey }), { icon: 'calendar', disabled: !capability('calendar.forTicket'), attributes: { 'data-ticket-command': 'plan' } }),
      button('Matériel et fiches terrain', navigate('interventions', { tab: 'preparation', fieldTicketKey: ticketKey }), { icon: 'layers', disabled: !capability('field.get'), attributes: { 'data-ticket-command': 'materials' } }),
      button('Notes, checklist et rappels', navigate('tickets', { ticketKey, section: 'personal' }), { icon: 'bell', attributes: { 'data-ticket-command': 'personal' } }),
      button('Préparer un compte rendu', navigate('tickets', { ticketKey, section: 'draft' }), { icon: 'log', attributes: { 'data-ticket-command': 'draft' } })));
  const element = el('section', { class: 'ticket-control-hub', 'aria-label': 'Piloter ce ticket' },
    el('h3', { text: `Piloter le ticket ${ticket.personal.id}` }), actions, native, local);
  return { element, select(kind) {
    for (const control of actions.querySelectorAll('[data-ticket-command]'))
      control.setAttribute('aria-pressed', String(control.dataset.ticketCommand === (kind || 'all')));
  } };
}
