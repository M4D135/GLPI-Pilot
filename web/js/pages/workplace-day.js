import { el, button, card, toolbar, input, select, field, dialog, confirm, badge, empty } from '../ui.js';
import { call, run, message, revision, note, pre, date, heading, dataTable, pager, tabs, checkbox, personalEditor, textArea } from './workplace-common.js';

export async function renderDay(ctx) {
  const prefs = await call(ctx, 'workplace.get'); let page = 0; let active = ctx.params?.section || 'list'; let selected = null; let currentData; let disposed = false;
  const search = input(prefs.daySearch || '', { maxLength: 300, placeholder: 'Ticket, titre, ville ou notes' });
  const scopes = ['Aujourd’hui', 'Mes tickets du relevé', 'File personnelle', 'Épinglés', 'Rappels à traiter', 'Reportés', 'Tous les conservés'];
  const scope = select(scopes.map((label, value) => ({ value: String(value), label })), String(prefs.dayScope || 0));
  const intro = el('div'); const content = el('div'); const selectedContent = el('div', { class: 'work-day-editor' });
  const choose = async row => {
    const selectedRevision = currentData.revision;
    selected = row; await call(ctx, 'workplace.preferences.save', { daySelectedKey: row.ticketKey, daySearch: search.value, dayScope: Number(scope.value) });
    const ticket = await call(ctx, 'workplace.ticket.open', row.saved ? { ticketKey: row.ticketKey } : { rowIndex: row.rowIndex, revision: selectedRevision });
    selectedContent.replaceChildren(card(`#${row.id} · ${row.title}`, toolbar(button('Lire la fiche', () => ctx.navigate('tickets', { ticketKey: ticket.personal.key })), button('↑ Dans la file', run(ctx, async () => { await call(ctx, 'workplace.queue.move', { ticketKey: ticket.personal.key, direction: -1 }); await load(); })), button('↓ Dans la file', run(ctx, async () => { await call(ctx, 'workplace.queue.move', { ticketKey: ticket.personal.key, direction: 1 }); await load(); }))), personalEditor(ctx, ticket.personal, currentData.templates, async () => { await load(); })));
  };
  const saveView = () => call(ctx, 'workplace.preferences.save', { daySearch: search.value, dayScope: Number(scope.value), daySelectedKey: selected?.ticketKey || prefs.daySelectedKey || '' });
  const load = async () => {
    currentData = await call(ctx, 'workplace.day.get', { search: search.value, scope: Number(scope.value), page }); if (disposed) return;
    intro.replaceChildren(note(currentData.personalName ? `Mon identité : ${currentData.personalName} · relevé du ${date(currentData.sourceDate)}` : 'Choisissez votre identité dans Équipe pour retrouver les tickets de votre périmètre.'), toolbar(button('Choisir mon identité', () => ctx.navigate('technicians')), button('Mon tableau de bord', () => ctx.navigate('dashboard', { technicianKey: currentData.personalTechnician }), { disabled: !currentData.personalTechnician }), badge(currentData.complete ? 'Relevé complet' : 'Couverture partielle ou non confirmée')));
    draw();
  };
  function draw() {
    const body = el('div'); const data = currentData;
    content.replaceChildren(tabs([{ key: 'list', label: 'À faire' }, { key: 'briefing', label: 'Briefing de session' }, { key: 'tours', label: 'Tournées du jour' }, { key: 'templates', label: 'Modèles de listes' }, { key: 'preferences', label: 'Mes rappels' }], active, value => { active = value; draw(); }), body);
    if (active === 'list') {
      body.replaceChildren(toolbar(search, scope, button('Appliquer', run(ctx, async () => { page = 0; await saveView(); await load(); }), { primary: true })),
        dataTable(data.rows, [{ key: 'id', label: 'Ticket', render: row => button(row.id, run(ctx, () => choose(row))) }, { key: 'title', label: 'Titre' }, { key: 'status', label: 'État observé' }, { key: 'city', label: 'Ville' }, { key: 'due', label: 'Échéance GLPI', render: row => date(row.due) }, { key: 'reminderAt', label: 'Rappel personnel', render: row => el('span', { class: row.reminderDue ? 'work-urgent' : '', text: date(row.reminderAt) }) }, { key: 'snoozedUntil', label: 'Reprise personnelle', render: row => date(row.snoozedUntil) }, { key: 'coverage', label: 'Couverture', render: row => row.inSource ? 'Présent dans le relevé' : 'Absent du relevé · état non vérifié' }], { key: 'ticketKey', selected: selected?.ticketKey, onSelect: run(ctx, choose) }),
        pager(data.page, data.pageSize, data.total, run(ctx, async value => { page = value; await load(); })), selectedContent);
    } else if (active === 'briefing') {
      let briefingPage = 0; const rows = el('div'); const render = () => rows.replaceChildren(dataTable(data.briefing.lines.slice(briefingPage * 50, briefingPage * 50 + 50), [{ key: 'ticketId', label: 'Ticket' }, { key: 'kind', label: 'Événement observé' }, { key: 'message', label: 'Détail' }]), pager(briefingPage, 50, data.briefing.lines.length, value => { briefingPage = value; render(); })); render(); body.replaceChildren(note(data.briefing.summary), rows);
    } else if (active === 'tours') {
      body.replaceChildren(note('Tournées enregistrées pour aujourd’hui : votre identité et les tournées sans technicien désigné. En l’absence d’identité choisie, toutes les tournées du jour sont visibles.'), dataTable(data.tours, [{ key: 'name', label: 'Tournée', render: row => button(row.name, () => ctx.navigate('tours', { tourId: row.tourId })) }, { key: 'state', label: 'État', render: row => ({ draft: 'Brouillon', ready: 'Prête', done: 'Terminée' }[row.state] || row.state) }, { key: 'startTime', label: 'Début' }, { key: 'endTime', label: 'Fin' }, { key: 'ticketIds', label: 'Tickets', render: row => row.ticketIds.join(', ') }]), button('Ouvrir les tournées', () => ctx.navigate('tours')));
    } else if (active === 'templates') {
      const choice = select([{ value: '', label: 'Nouveau modèle' }, ...Object.keys(data.templates).map(value => ({ value, label: value }))], ''); const name = input('', { maxLength: 100 }); const lines = textArea('', 20000, 12);
      choice.addEventListener('change', () => { name.value = choice.value; lines.value = (data.templates[choice.value] || []).join('\n'); });
      body.replaceChildren(field('Modèle enregistré', choice), field('Nom du modèle', name), field('Une action par ligne (40 maximum)', lines), toolbar(button('Enregistrer le modèle', run(ctx, async () => { await call(ctx, 'workplace.template.save', { name: name.value, items: lines.value.split(/\r?\n/).filter(Boolean) }); await load(); ctx.notify('Modèle enregistré.'); }), { primary: true }), button('Supprimer ce modèle…', run(ctx, async () => { if (!choice.value || !await confirm('Supprimer le modèle ?', choice.value, 'Supprimer')) return; await call(ctx, 'workplace.template.remove', { name: choice.value }); await load(); }), { danger: true })));
    } else {
      const enabled = checkbox(data.desktopReminders, 'Afficher une notification Windows pour mes rappels');
      enabled.control.addEventListener('change', run(ctx, async () => { await call(ctx, 'workplace.preferences.save', { desktopReminders: enabled.control.checked }); data.desktopReminders = enabled.control.checked; ctx.notify('Préférence de rappels enregistrée.'); }));
      body.replaceChildren(enabled.element, note('Pendant l’ouverture de GLPI Pilot, les rappels restent visibles dans cette liste. Les notifications affichent uniquement un nombre. Les plages de silence et le verrouillage de session suspendent l’affichage sans consommer les rappels.'), button('Vérifier les rappels maintenant', run(ctx, async () => { const result = await call(ctx, 'workplace.reminders.check'); ctx.notify(`${result.count} rappel(s) à traiter` + (result.notified ? ' · notification affichée.' : result.deferred ? ' · notification différée.' : '.')); })));
    }
  }
  ctx.root.replaceChildren(heading('Ma journée', 'Votre file, vos rappels et les changements observés depuis la précédente session.'), intro, content);
  scope.addEventListener('change', run(ctx, async () => { page = 0; await saveView(); await load(); }));
  search.addEventListener('keydown', e => { if (e.key === 'Enter') run(ctx, async () => { page = 0; await saveView(); await load(); })(); });
  await load();
  const restored = currentData.rows.find(row => row.ticketKey === prefs.daySelectedKey);
  if (restored) await choose(restored);
  const unsubscribe = ctx.api.on('tours.changed', () => run(ctx, load)());
  return () => { disposed = true; unsubscribe(); };
}

export async function renderEnrichment(ctx) {
  let queue = await call(ctx, 'workplace.enrichment.get'); let page = 0; let selected = ''; let running = false; let controller; let disposed = false;
  const title = input(queue.label, { maxLength: 1000 }); const limit = input(String(queue.limit), { type: 'number', min: 1, max: 100 });
  const status = el('div', { class: 'work-job-progress', 'aria-live': 'polite' }); const actions = el('div'); const list = el('div');
  const update = async args => { queue = await call(ctx, 'workplace.enrichment.update', { label: title.value, limit: Number(limit.value), ...args }); draw(); };
  function drawActions() {
    actions.replaceChildren(toolbar(field('Nom de la file', title), field('Lectures par lancement (1 à 100)', limit),
      button('Enregistrer les réglages', run(ctx, () => update({})), { disabled: running }),
      button('Lire les tickets restants', run(ctx, launch), { primary: true, disabled: running || queue.total === 0 }),
      button('Annuler la lecture', () => controller?.abort(), { disabled: !running }),
      button('Mettre en tête', run(ctx, () => update({ priorityKey: selected })), { disabled: running || !selected }),
      button('Retirer de la file', run(ctx, () => update({ removeKey: selected })), { disabled: running || !selected }),
      button('Réinitialiser pour relire…', run(ctx, async () => { if (await confirm('Relire volontairement cette file ?', 'Les résultats datés de la file seront remis à zéro. Les données historiques et les notes resteront conservées.', 'Réinitialiser la file')) await update({ reset: true, confirmed: true }); }), { disabled: running })));
  }
  function draw() {
    if (disposed) return; drawActions();
    const rows = queue.rows.slice(page * 50, page * 50 + 50);
    list.replaceChildren(dataTable(rows, [{ key: 'order', label: 'Ordre' }, { key: 'id', label: 'Ticket', render: row => button(row.id, run(ctx, async () => { const result = await call(ctx, 'workplace.enrichment.open', { ticketKey: row.ticketKey }); ctx.navigate('tickets', { ticketKey: result.ticketKey }); }), { disabled: running }) }, { key: 'state', label: 'Lecture', render: row => row.outcome?.state || 'Non lu' }, { key: 'observedStatus', label: 'Statut observé', render: row => row.outcome?.observedStatus || 'Inconnu' }, { key: 'readAt', label: 'Lu le', render: row => date(row.outcome?.readAt) }, { key: 'sections', label: 'Rubriques', render: row => row.outcome?.sections ?? '—' }, { key: 'message', label: 'Détail', render: row => row.outcome?.message || 'Aucune conclusion avant lecture.' }], { key: 'ticketKey', selected, onSelect: row => { selected = row.ticketKey; drawActions(); } }), pager(page, 50, queue.total, value => { page = value; draw(); }));
  }
  async function launch() {
    await update({}); controller = new AbortController(); const abort = () => controller.abort(); ctx.signal?.addEventListener('abort', abort, { once: true }); running = true; drawActions();
    status.replaceChildren(note('Lecture séquentielle des tickets encore sans résultat réussi…'));
    try {
      const result = await ctx.api.call('workplace.enrichment.run', {}, { signal: controller.signal }); queue = result.queue;
      status.replaceChildren(note(result.interrupted ? 'Lecture interrompue. Les tickets non lus restent d’état inconnu.' : `${result.attempted} lecture(s) tentée(s). Les résultats portent leur propre date d’observation.`));
    } catch (error) {
      if (error.name === 'AbortError') status.replaceChildren(note('Annulation demandée. Les observations terminées sont conservées.'));
      else { status.replaceChildren(note('Lecture arrêtée : ' + error.message)); message(ctx, error); }
    } finally {
      running = false; ctx.signal?.removeEventListener('abort', abort);
      if (!disposed && !ctx.signal?.aborted) { queue = await call(ctx, 'workplace.enrichment.get'); draw(); }
    }
  }
  const unsubscribe = ctx.api.on('workplace.enrichment.progress', data => {
    const row = queue.rows.find(row => row.ticketKey === data.ticketKey); if (row) row.outcome = data.outcome;
    status.replaceChildren(note(`${data.completed} / ${data.total} ticket(s) lus avec succès. État observé séparément du relevé et des archives.`)); draw();
  });
  ctx.root.replaceChildren(heading('Lectures ciblées', 'Préparez une sélection depuis le relevé ou l’historique, puis choisissez explicitement de la lire.'),
    note('La restauration d’une file ne lance aucune lecture. Un prochain lancement reprend les candidats sans lecture réussie. Un statut récemment relu ne réécrit jamais une comparaison historique.'), actions, status, list);
  draw(); return () => { disposed = true; controller?.abort(); unsubscribe(); };
}
