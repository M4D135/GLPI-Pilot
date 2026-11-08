import { navigationVisible } from '../navigation.js';
import { el, replaceChildren, button, card, toolbar, input, select, field, form, dialog, confirm, badge } from '../ui.js';
import { call, run, revision, note, pre, date, label, heading, dataTable, pager, tabs, checkbox, textArea, ticketButton } from './workplace-common.js';
import { publishSharedOperation } from './shared-operations.js';

const isoDay = (value = new Date()) => { const d = value instanceof Date ? value : new Date(value); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; };
const nullableNumber = control => control.value === '' ? null : Number(control.value);
const words = control => control.value.split(/[;\n]/).map(value => value.trim()).filter(Boolean);

export async function renderTechnicians(ctx) {
  let page = 0; const search = input('', { placeholder: 'Nom du technicien', maxLength: 300 }); const favorite = checkbox(false, 'Favoris seulement'); const at = input(isoDay(), { type: 'date' });
  const intro = el('div'); const list = el('div'); const compareRoot = el('div'); const comparing = new Map(); let opened = false;
  const load = async () => {
    const result = await call(ctx, 'workplace.technicians.get', { revision: revision(ctx), search: search.value, favorites: favorite.control.checked, date: at.value, page });
    intro.replaceChildren(note(result.message), toolbar(badge('Relevé du ' + date(result.sourceDate)), badge(result.complete ? 'Complet' : 'Partiel ou non confirmé', result.complete ? '' : 'warning'), button(`${result.unassigned} sans technicien`, run(ctx, () => showRows(ctx, '', 'unassigned'))), button(`${result.multiple} affectations multiples`, run(ctx, () => showRows(ctx, '', 'multiple')))));
    list.replaceChildren(dataTable(result.rows, [
      { key: 'compare', label: 'Comparer', render: row => { const box = checkbox(comparing.has(row.key), ''); box.control.setAttribute('aria-label', 'Comparer ' + row.name); box.control.addEventListener('change', () => { if (box.control.checked) { if (comparing.size === 8) { box.control.checked = false; ctx.notify('Comparez au maximum huit techniciens.'); return; } comparing.set(row.key, row); } else comparing.delete(row.key); showComparison(); }); return box.element; } },
      { key: 'name', label: 'Technicien', render: row => button((row.favorite ? '★ ' : '') + row.name, run(ctx, () => editTechnician(ctx, row.key, row.name, load))) },
      { key: 'activeCount', label: 'Actifs', render: row => metricLink(row, 'active', row.activeCount) }, { key: 'pendingCount', label: 'En attente', render: row => metricLink(row, 'pending', row.pendingCount) },
      { key: 'highCount', label: 'Priorité élevée', render: row => metricLink(row, 'high', row.highCount) }, { key: 'overdueCount', label: 'Échéance dépassée', render: row => metricLink(row, 'overdue', row.overdueCount) },
      { key: 'unknownStatusCount', label: 'Statuts inconnus' }, { key: 'capacityMinutes', label: 'Capacité déclarée (min)' }, { key: 'loadPercent', label: 'Actifs / objectif', render: row => row.loadPercent == null ? '—' : row.loadPercent.toFixed(0) + ' %' },
      { key: 'actions', label: 'Mon espace', render: row => toolbar(button(result.personalTechnician === row.key ? 'Mon identité' : 'C’est moi', run(ctx, async () => { await call(ctx, 'workplace.technicians.me', { technicianKey: row.key }); ctx.navigate('day'); }), { primary: result.personalTechnician === row.key }), button('Dashboard', () => ctx.navigate('dashboard', { technicianKey: row.key }))) }
    ]), pager(result.page, result.pageSize, result.total, run(ctx, async value => { page = value; await load(); })));
    if (!opened && ctx.params?.technicianKey) { opened = true; await editTechnician(ctx, ctx.params.technicianKey, ctx.params.technicianKey, load); }
  };
  const metricLink = (row, measure, value) => value == null ? 'Indéterminé' : button(String(value), run(ctx, () => showRows(ctx, row.key, measure)), { title: row.measures.find(m => m.key === measure)?.coverage || '' });
  function showComparison() {
    const rows = [...comparing.values()];
    compareRoot.replaceChildren(rows.length ? card('Comparaison du même relevé', note('Une affectation multiple figure dans chaque périmètre concerné. Les effectifs inconnus et les capacités déclarées restent distincts.'), dataTable(rows, [{ key: 'name', label: 'Technicien' }, { key: 'total', label: 'Observés' }, { key: 'activeCount', label: 'Actifs' }, { key: 'overdueCount', label: 'En retard' }, { key: 'oldCount', label: 'Anciens actifs' }, { key: 'staleCount', label: 'Sans mouvement' }, { key: 'meanAgeDays', label: 'Âge moyen (j)', render: row => row.meanAgeDays?.toFixed(1) ?? '—' }, { key: 'meanInactivityDays', label: 'Inactivité moyenne (j)', render: row => row.meanInactivityDays?.toFixed(1) ?? '—' }, { key: 'capacityMinutes', label: 'Minutes déclarées' }])) : note('Cochez des techniciens pour comparer leurs charges observées.'));
  }
  ctx.root.replaceChildren(heading('Équipe', 'Charges calculées sur le relevé, compétences déclarées et capacités locales.'),
    toolbar(search, favorite.element, field('Date de capacité', at), button('Appliquer', run(ctx, async () => { page = 0; await load(); }), { primary: true }), button('Organisation', () => ctx.navigate('planning')), button('Couverture sur 7 jours', run(ctx, () => showCoverage(ctx, at.value))), button('Exporter la charge CSV…', run(ctx, async () => { const result = await call(ctx, 'workplace.technicians.export', { revision: revision(ctx), date: at.value }); if (result.saved) ctx.notify('Export enregistré : ' + result.name); }))),
    toolbar(button('Partager les compétences et capacités', run(ctx, () => publishSharedOperation(ctx, 'workspace.publishTechnicians', { revision: revision(ctx) })), { icon: 'users' }), button('Voir le travail partagé', () => ctx.navigate('library', { view: 'team' }))), intro, list, compareRoot);
  showComparison(); await load();
}

async function showRows(ctx, technicianKey, measure) {
  const root = el('div'); let page = 0; const panel = dialog('Tickets du périmètre', root, [{ label: 'Fermer', action: close => close() }]);
  const load = async () => {
    const result = await call(ctx, 'workplace.technicians.rows', { revision: revision(ctx), technicianKey, measure, page });
    replaceChildren(root, dataTable(result.rows, [{ key: 'open', label: 'Fiche', render: row => button('Ouvrir', () => { panel.close(); ctx.navigate('tickets', { rowIndex: row.rowIndex, revision: result.revision }); }) }, ...result.headers.map((header, index) => ({ key: String(index), label: header, render: row => row.values[index] || '' }))]), pager(result.page, result.pageSize, result.total, run(ctx, async value => { page = value; await load(); })), navigationVisible('enrichment', ctx.state.settings?.interface) ? button('Préparer la lecture de cette page', run(ctx, async () => { await call(ctx, 'workplace.enrichment.prepare', { revision: result.revision, rows: result.rows.map(r => r.rowIndex), label: 'Indicateur technicien · ' + measure }); panel.close(); ctx.navigate('enrichment'); })) : null);
  }; await load();
}

/** Array editors keep typed controls per field. No executable strings or JSON are accepted from the page. */
function arrayEditor(ctx, title, items, columns, make, limit) {
  const root = el('div'); let page = 0;
  const draw = () => {
    const rows = items.slice(page * 15, page * 15 + 15);
    root.replaceChildren(dataTable(rows, [...columns.map(column => ({ key: column.key, label: column.label, render: row => {
      let control;
      if (column.options) control = select(column.options, String(row[column.key]));
      else control = input(row[column.key] == null ? '' : String(row[column.key]), { type: column.type || 'text', min: column.min, max: column.max, maxLength: column.maxLength });
      control.setAttribute('aria-label', column.label); control.addEventListener('input', () => { row[column.key] = column.number ? control.value === '' ? null : Number(control.value) : control.value; });
      control.addEventListener('change', () => { row[column.key] = column.number ? control.value === '' ? null : Number(control.value) : control.value; }); return control;
    } })), { key: 'remove', label: 'Retirer', render: row => button('×', () => { items.splice(items.indexOf(row), 1); page = Math.min(page, Math.max(0, Math.ceil(items.length / 15) - 1)); draw(); }, { title: 'Retirer cette ligne' }) }]),
      pager(page, 15, items.length, value => { page = value; draw(); }), button('Ajouter une ligne', () => { if (items.length >= limit) { ctx.notify(`Limite de ${limit} lignes atteinte.`, 'error'); return; } items.push(make()); page = Math.floor((items.length - 1) / 15); draw(); }));
  }; draw(); return card(title, root);
}

export async function editTechnician(ctx, technicianKey, name, afterSave) {
  const result = await call(ctx, 'workplace.technicians.preference', { technicianKey }); const p = result.preference;
  const favorite = checkbox(p.favorite, 'Technicien favori'); const target = input(p.activeTarget == null ? '' : String(p.activeTarget), { type: 'number', min: 1, max: 100000 }); const notes = textArea(p.notes, 1200, 3);
  const skills = textArea(p.skills.join('\n'), 8000, 4); const primary = textArea(p.primaryTerritories.join('\n'), 8000, 4); const backup = textArea(p.backupTerritories.join('\n'), 8000, 4);
  const capacity = input(p.dailyCapacityMinutes == null ? '' : String(p.dailyCapacityMinutes), { type: 'number', min: 0, max: 1440 }); const estimate = input(p.estimatedTicketMinutes == null ? '' : String(p.estimatedTicketMinutes), { type: 'number', min: 1, max: 10080 });
  const absentFrom = input(p.unavailableFrom?.slice(0, 10) || '', { type: 'date' }); const absentUntil = input(p.unavailableUntil?.slice(0, 10) || '', { type: 'date' }); const availability = textArea(p.availabilityNote, 1200, 3);
  const clock = minutes => `${String(Math.floor(minutes / 60)).padStart(2, '0')}:${String(minutes % 60).padStart(2, '0')}`;
  const periods = p.workPeriods.map(item => ({ day: item.day, start: clock(item.startMinute), end: clock(item.endMinute), capacityMinutes: item.capacityMinutes }));
  const days = p.capacityDays.map(item => ({ date: item.date.slice(0, 10), morningMinutes: item.morningMinutes, afternoonMinutes: item.afternoonMinutes })); const models = p.categoryEstimates.map(item => ({ ...item }));
  const dayOptions = [{ value: '1', label: 'Lundi' }, { value: '2', label: 'Mardi' }, { value: '3', label: 'Mercredi' }, { value: '4', label: 'Jeudi' }, { value: '5', label: 'Vendredi' }, { value: '6', label: 'Samedi' }, { value: '0', label: 'Dimanche' }];
  const content = el('div', { class: 'work-technician-editor' }, note('Déclarations locales : une capacité absente reste inconnue et une indisponibilité déclarée donne une capacité nulle.'),
    favorite.element, el('div', { class: 'work-form-grid' }, field('Objectif de tickets actifs', target), field('Capacité quotidienne (minutes)', capacity), field('Estimation générale par ticket (minutes)', estimate)), field('Notes locales', notes),
    el('div', { class: 'work-form-grid' }, field('Compétences (une par ligne)', skills), field('Territoires principaux', primary), field('Territoires de remplacement', backup)),
    el('div', { class: 'work-form-grid' }, field('Indisponible du', absentFrom), field('Au', absentUntil)), field('Note de disponibilité', availability),
    arrayEditor(ctx, 'Plages hebdomadaires de présence', periods, [{ key: 'day', label: 'Jour', options: dayOptions, number: true }, { key: 'start', label: 'Début', type: 'time' }, { key: 'end', label: 'Fin', type: 'time' }, { key: 'capacityMinutes', label: 'Capacité (min)', type: 'number', number: true, min: 0, max: 1440 }], () => ({ day: 1, start: '09:00', end: '12:00', capacityMinutes: 180 }), 28),
    note('Une exception remplace le budget de sa demi-journée ; elle ne crée pas de nouvel horaire de présence. Une case vide conserve la capacité habituelle ou son caractère inconnu.'),
    arrayEditor(ctx, 'Exceptions par date et demi-journée', days, [{ key: 'date', label: 'Date', type: 'date' }, { key: 'morningMinutes', label: 'Matin (min)', type: 'number', number: true, min: 0, max: 720 }, { key: 'afternoonMinutes', label: 'Après-midi (min)', type: 'number', number: true, min: 0, max: 720 }], () => ({ date: isoDay(), morningMinutes: 0, afternoonMinutes: null }), 732),
    arrayEditor(ctx, 'Modèles de durée par catégorie', models, [{ key: 'category', label: 'Catégorie', maxLength: 300 }, { key: 'minimumMinutes', label: 'Minimum (min)', type: 'number', number: true, min: 1, max: 10080 }, { key: 'minutes', label: 'Centrale (min)', type: 'number', number: true, min: 1, max: 10080 }, { key: 'maximumMinutes', label: 'Maximum (min)', type: 'number', number: true, min: 1, max: 10080 }], () => ({ category: '', minimumMinutes: 15, minutes: 30, maximumMinutes: 60 }), 200));
  dialog('Déclarations · ' + name, content, [{ label: 'Fermer', action: close => close() }, { label: 'Enregistrer les déclarations', primary: true, action: run(ctx, async close => {
    const minute = time => { if (!/^\d{2}:\d{2}$/.test(time)) throw new Error('Horaire attendu au format HH:mm.'); const [h, m] = time.split(':').map(Number); return h * 60 + m; };
    const preference = { favorite: favorite.control.checked, activeTarget: nullableNumber(target), notes: notes.value, skills: words(skills), primaryTerritories: words(primary), backupTerritories: words(backup), dailyCapacityMinutes: nullableNumber(capacity), estimatedTicketMinutes: nullableNumber(estimate), unavailableFrom: absentFrom.value ? absentFrom.value + 'T00:00:00' : null, unavailableUntil: absentUntil.value ? absentUntil.value + 'T00:00:00' : null, availabilityNote: availability.value,
      workPeriods: periods.map(item => ({ day: Number(item.day), startMinute: minute(item.start), endMinute: minute(item.end), capacityMinutes: item.capacityMinutes })), capacityDays: days.map(item => ({ ...item, date: item.date + 'T00:00:00' })), categoryEstimates: models };
    await call(ctx, 'workplace.technicians.save', { technicianKey, preference }); close(); ctx.notify('Déclarations enregistrées.', 'success'); if (afterSave) await afterSave();
  }) }]);
}

async function showCoverage(ctx, start) {
  const root = el('div'); const dateInput = input(start || isoDay(), { type: 'date' }); const territory = input('', { maxLength: 160, placeholder: 'Tous les secteurs' }); const skill = input('', { maxLength: 160, placeholder: 'Toutes les compétences' }); let page = 0;
  const body = el('div'); const load = async () => {
    const result = await call(ctx, 'workplace.technicians.coverage', { revision: revision(ctx), date: dateInput.value, territory: territory.value, skill: skill.value, page });
    body.replaceChildren(dataTable(result.rows, [{ key: 'territory', label: 'Territoire' }, { key: 'skill', label: 'Compétence' }, { key: 'date', label: 'Date', render: row => date(row.date) }, { key: 'period', label: 'Période' }, { key: 'technicians', label: 'Intervenants déclarés' }, { key: 'capacityMinutes', label: 'Capacité (min)' }, { key: 'evidence', label: 'Portée' }]), pager(result.page, result.pageSize, result.total, run(ctx, async value => { page = value; await load(); })));
  };
  root.replaceChildren(note('Les capacités sont partagées entre les compétences et secteurs d’une même personne : ne pas additionner les cellules. Une présence réelle n’est pas attestée par ces déclarations.'), toolbar(field('Début', dateInput), territory, skill, button('Calculer 7 jours', run(ctx, async () => { page = 0; await load(); }), { primary: true })), body);
  dialog('Couverture sur 7 jours', root, [{ label: 'Fermer', action: close => close() }]); await load();
}

export async function renderPlanning(ctx) {
  let page = 0; let proposalPage = 0; let data; let selectedTicket; let recommendations; let selectedTechnician; let active = 'tickets';
  const name = input('', { maxLength: 200 }); const at = input(isoDay(), { type: 'date' }); const search = input('', { placeholder: 'Ticket, titre, catégorie ou territoire', maxLength: 300 }); const required = input('', { placeholder: 'Compétences requises séparées par ;', maxLength: 8000 });
  const intro = el('div'); const nav = el('div'); const content = el('div'); const detail = el('div'); let initialized = false;
  const skills = () => words(required);
  const load = async () => {
    const updated = await call(ctx, 'workplace.planning.get', { revision: revision(ctx), search: search.value, page, proposalPage });
    if (data && data.revision !== updated.revision) { selectedTicket = null; recommendations = null; selectedTechnician = null; detail.replaceChildren(); }
    data = updated;
    if (!initialized) { name.value = data.name; at.value = data.date.slice(0, 10); initialized = true; }
    intro.replaceChildren(toolbar(field('Nom du scénario', name), field('Date du scénario', at), button('Enregistrer le scénario', run(ctx, async () => { await call(ctx, 'workplace.planning.save', { name: name.value, date: at.value }); recommendations = null; detail.replaceChildren(); await load(); ctx.notify('Scénario enregistré sur ce poste.'); }), { primary: true }), button('Exporter les propositions CSV…', run(ctx, async () => { const result = await call(ctx, 'workplace.planning.export', { revision: data.revision }); if (result.saved) ctx.notify('Propositions exportées : ' + result.name); })), button('Effacer les propositions…', run(ctx, async () => { if (!await confirm('Effacer les propositions locales ?', `${data.proposalTotal} proposition(s) seront retirées du scénario.`, 'Effacer')) return; await call(ctx, 'workplace.planning.save', { clear: true, confirmed: true }); await load(); }), { danger: true })), note(`Calcul sur le relevé ${data.historical ? 'historique' : 'courant'} · ${data.complete ? 'couverture complète' : 'couverture partielle ou non confirmée'}. Les propositions ne sont jamais appliquées à GLPI par cette page.`));
    intro.append(toolbar(button('Enregistrer et partager la répartition', run(ctx, async () => {
      await call(ctx, 'workplace.planning.save', { name: name.value, date: at.value });
      await publishSharedOperation(ctx, 'workspace.publishDispatch', { revision: data.revision });
      await load();
    }), { icon: 'users' }), button('Voir le travail partagé', () => ctx.navigate('library', { view: 'team' }))));
    draw();
  };
  function draw() {
    nav.replaceChildren(tabs([{ key: 'tickets', label: 'Tickets et propositions' }, { key: 'loads', label: 'Charge avant / après' }, { key: 'coverage', label: 'Couverture' }, { key: 'handover', label: 'Passation d’absence' }], active, value => { active = value; draw(); }));
    if (active === 'tickets') {
      content.replaceChildren(toolbar(search, required, button('Rechercher', run(ctx, async () => { page = 0; await load(); }))),
        dataTable(data.tickets, [{ key: 'id', label: 'Ticket', render: row => ticketButton(row.id, run(ctx, () => recommend(row))) }, { key: 'title', label: 'Titre' }, { key: 'territory', label: 'Secteur' }, { key: 'category', label: 'Catégorie' }, { key: 'activeKnown', label: 'État', render: row => row.activeKnown ? 'Actif confirmé' : 'État inconnu' }, { key: 'currentTechnicians', label: 'Affectations lues', render: row => row.currentTechnicians.map(key => data.technicians.find(t => t.key === key)?.name || key).join(', ') }], { key: 'key', selected: selectedTicket?.key, onSelect: run(ctx, recommend) }),
        pager(data.page, data.pageSize, data.total, run(ctx, async value => { page = value; await load(); })), detail,
        card('Propositions enregistrées', dataTable(data.proposals, [{ key: 'ticketId', label: 'Ticket' }, { key: 'title', label: 'Titre / couverture' }, { key: 'technicianKey', label: 'Relais proposé', render: row => data.technicians.find(t => t.key === row.technicianKey)?.name || row.technicianKey }, { key: 'estimatedMinutes', label: 'Minutes prévues' }, { key: 'note', label: 'Note' }, { key: 'remove', label: 'Retirer', render: row => button('Retirer', run(ctx, async () => { await call(ctx, 'workplace.planning.remove', { ticketKey: row.ticketKey }); await load(); })) }]), pager(data.proposalPage, data.pageSize, data.proposalTotal, run(ctx, async value => { proposalPage = value; await load(); }))));
    } else if (active === 'loads') {
      let p = 0; const body = el('div'); const drawLoads = () => body.replaceChildren(dataTable(data.loads.slice(p * 50, p * 50 + 50), [{ key: 'technician', label: 'Technicien' }, { key: 'beforeMinutes', label: 'Avant (min estimées)' }, { key: 'afterMinutes', label: 'Après (min estimées)' }, { key: 'beforeMinimumMinutes', label: 'Avant minimum' }, { key: 'beforeMaximumMinutes', label: 'Avant maximum' }, { key: 'afterMinimumMinutes', label: 'Après minimum' }, { key: 'afterMaximumMinutes', label: 'Après maximum' }, { key: 'capacityMinutes', label: 'Capacité déclarée' }, { key: 'proposedTickets', label: 'Propositions' }, { key: 'coverage', label: 'Portée' }]), pager(p, 50, data.loads.length, value => { p = value; drawLoads(); })); drawLoads(); content.replaceChildren(note('Modèles déclarés par catégorie et estimations générales. Un ticket absent ne contribue pas à la simulation. Une proposition retire l’estimation du responsable remplacé avant d’ajouter les minutes prévues.'), body);
    } else if (active === 'coverage') {
      let p = 0; const body = el('div'); const drawCoverage = () => body.replaceChildren(dataTable(data.coverage.slice(p * 50, p * 50 + 50), [{ key: 'territory', label: 'Secteur' }, { key: 'primary', label: 'Principaux' }, { key: 'backup', label: 'Remplaçants' }, { key: 'availableDeclared', label: 'Déclarés disponibles' }, { key: 'gaps', label: 'Lacunes' }]), pager(p, 50, data.coverage.length, value => { p = value; drawCoverage(); })); drawCoverage();
      content.replaceChildren(button('Matrice sur 7 jours', run(ctx, () => showCoverage(ctx, at.value))), body);
    }
    else {
      const departing = select(data.technicians.map(t => ({ value: t.key, label: t.name })), data.technicians[0]?.key || ''); const output = el('div');
      const prepare = async (save = false, copy = false) => { const result = await call(ctx, 'workplace.planning.handover', { revision: data.revision, technicianKey: departing.value, requiredSkills: skills(), save, copy }); output.replaceChildren(pre(result.text), note(`${result.proposed} proposition(s) ${result.saved ? 'enregistrée(s)' : 'préparée(s) sans enregistrement'}.`)); if (copy) ctx.notify('Passation copiée.'); };
      content.replaceChildren(note('Les relais sont proposés selon les déclarations à la date du scénario. Un dossier sans durée déclarée reste à compléter.'), field('Technicien à remplacer', departing), toolbar(button('Préparer les relais', run(ctx, () => prepare())), button('Enregistrer les propositions de relais', run(ctx, () => prepare(true)), { primary: true }), button('Copier la passation préparée', run(ctx, () => prepare(false, true)))), output);
    }
  }
  async function recommend(ticket) {
    const selectedRevision = data.revision;
    selectedTicket = ticket; recommendations = await call(ctx, 'workplace.planning.recommend', { revision: selectedRevision, ticketKey: ticket.key, requiredSkills: skills() });
    const choose = select(recommendations.recommendations.map(r => ({ value: r.technicianKey, label: r.name + (r.availableForScenario ? '' : ' · contrainte non satisfaite') })), recommendations.recommendations[0]?.technicianKey || '');
    const minutes = input('30', { type: 'number', min: 1, max: 10080 }); const noteText = input('', { maxLength: 1200 }); const evidence = el('div'); let recommendationPage = 0; const ranking = el('div');
    const show = () => { selectedTechnician = recommendations.recommendations.find(r => r.technicianKey === choose.value); const estimate = recommendations.estimates.find(r => r.technicianKey === choose.value)?.estimate; replaceChildren(evidence, note(selectedTechnician?.evidence || 'Aucun technicien déclaré.'), estimate ? note(`Durée centrale ${label(estimate.minutes)} min · fourchette ${label(estimate.minimumMinutes)} à ${label(estimate.maximumMinutes)} min. ${estimate.evidence}`) : null); };
    const drawRanking = () => ranking.replaceChildren(dataTable(recommendations.recommendations.slice(recommendationPage * 50, recommendationPage * 50 + 50), [{ key: 'name', label: 'Technicien' }, { key: 'availableForScenario', label: 'Critères satisfaits', render: row => row.availableForScenario ? 'Oui, déclarés' : 'Non' }, { key: 'score', label: 'Score des critères' }, { key: 'estimatedLoadMinutes', label: 'Charge indicative (min)' }, { key: 'capacityMinutes', label: 'Capacité' }, { key: 'evidence', label: 'Critères expliqués' }]), pager(recommendationPage, 50, recommendations.recommendations.length, value => { recommendationPage = value; drawRanking(); })); drawRanking();
    choose.addEventListener('change', show); show();
    detail.replaceChildren(card('Proposition · #' + ticket.id + ' · ' + ticket.title, field('Technicien proposé', choose), evidence,
      toolbar(field('Minutes prévues', minutes), button('Durée du modèle de catégorie', () => { const model = recommendations.estimates.find(r => r.technicianKey === choose.value)?.estimate; if (model?.minutes == null) ctx.notify('Durée non déclarée. Renseignez une estimation explicite.', 'warning'); else minutes.value = String(model.minutes); }), field('Note de proposition', noteText)),
      toolbar(button('Enregistrer cette proposition locale', run(ctx, async () => {
        let overrideConfirmed = false; if (!selectedTechnician) throw new Error('Choisissez un technicien.');
        if (!selectedTechnician.availableForScenario) { overrideConfirmed = await confirm('Dérogation locale aux déclarations ?', selectedTechnician.evidence, 'Conserver cette proposition'); if (!overrideConfirmed) return; }
        await call(ctx, 'workplace.planning.assign', { revision: selectedRevision, ticketKey: ticket.key, technicianKey: choose.value, minutes: Number(minutes.value), note: noteText.value, requiredSkills: skills(), overrideConfirmed }); ctx.notify('Proposition locale enregistrée.'); await load();
      }), { primary: true }), button('Ouvrir la fiche pour une action assistée', () => ctx.navigate('tickets', { rowIndex: ticket.sourceIndex, revision: selectedRevision, section: 'actions' }))), el('details', {}, el('summary', { text: 'Comparer les propositions et leurs critères' }), ranking)));
  }
  ctx.root.replaceChildren(heading('Répartition préparée', 'Scénarios datés, capacités déclarées et impact estimé avant toute affectation réelle.'), intro, nav, content); await load();
}
