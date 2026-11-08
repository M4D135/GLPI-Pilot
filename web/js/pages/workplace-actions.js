import { ticketControlHub } from './ticket-control-hub.js';
import { el, replaceChildren, button, card, toolbar, input, select, field, dialog, confirm, badge, isSimulation } from '../ui.js';
import { call, run, note, pre, date, textArea, checkbox, dataTable, personalEditor, textDialog, pager } from './workplace-common.js';

const compositions = new Map();

export async function renderDraft(ctx, root, ticket) {
  const simulation=isSimulation(ctx.state.session);
  const key = ticket.personal.key; const cached = compositions.get(key) || { template: 'Compte rendu d’intervention', findings: '', action: '', nextStep: '', context: [] };
  const template = select(['Compte rendu d’intervention', 'Diagnostic et prochaine action', 'Demande de complément', 'Passation technique'].map(value => ({ value, label: value })), cached.template);
  const findings = textArea(cached.findings, 6000, 3); const action = textArea(cached.action, 6000, 3); const next = textArea(cached.nextStep, 6000, 3);
  const draft = textArea(ticket.personal.followupDraft, 20000, 16); const state = el('p', { class: 'work-note', text: 'Brouillon local. Le texte n’est pas publié dans GLPI.' });
  let timer; let dirty = false; let disposed = false;
  const choices = (ticket.summary || []).slice(0, 60).map((row, index) => ({ index, ...checkbox(cached.context.includes(index), row[0] + ' · ' + row[1]) }));
  const remember = () => { compositions.set(key, { template: template.value, findings: findings.value, action: action.value, nextStep: next.value, context: choices.filter(c => c.control.checked).map(c => c.index) }); while (compositions.size > 12) compositions.delete(compositions.keys().next().value); };
  [template, findings, action, next, ...choices.map(c => c.control)].forEach(control => control.addEventListener('input', remember));
  const save = async () => {
    clearTimeout(timer); if (!dirty) return;
    const value = draft.value; await ctx.api.call('workplace.draft.save', { ticketKey: key, text: value });
    if (draft.value === value) dirty = false; if (!disposed) state.textContent = simulation?'Brouillon enregistré dans la simulation.':'Brouillon enregistré sur ce poste.';
  };
  draft.addEventListener('input', () => { dirty = true; state.textContent = 'Modification en attente d’enregistrement…'; clearTimeout(timer); timer = setTimeout(run(ctx, save), 700); });
  root.replaceChildren(note('Composez un texte à partir de vos constats et choisissez les faits lus à inclure. Relisez le brouillon avant de le copier ou de préparer son ajout.'),
    field('Modèle', template), el('div', { class: 'work-form-grid' }, field('Constat', findings), field('Action réalisée ou prévue', action), field('Suite attendue', next)),
    el('details', {}, el('summary', { text: 'Contexte effectivement lu à inclure' }), ...choices.map(c => c.element)),
    toolbar(button('Composer le brouillon', run(ctx, async () => { remember(); const result = await call(ctx, 'workplace.draft.compose', { ticketKey: key, ...compositions.get(key), contextRows: choices.filter(c => c.control.checked).map(c => c.index) }); draft.value = result.text; dirty = false; state.textContent = 'Brouillon composé et enregistré.'; }), { primary: true })),
    field('Texte modifiable', draft), state,
    toolbar(button('Enregistrer maintenant', run(ctx, save)), button('Copier le brouillon', run(ctx, async () => { await save(); await call(ctx, 'workplace.draft.copy', { ticketKey: key }); ctx.notify('Brouillon copié.'); })), button(simulation?'Préparer un ajout simulé…':'Préparer un ajout dans GLPI…', run(ctx, async () => { await save(); ctx.navigate('tickets', { ticketKey: key, section: 'actions' }); }))));
  return async () => { clearTimeout(timer); remember(); try { if (dirty) await save(); disposed = true; } catch (error) { ctx.notify('Brouillon non enregistré : ' + error.message, 'error'); throw error; } };
}

export async function renderActions(ctx, root, ticket, onApplied) {
  const simulation = isSimulation(ctx.state.session), ticketKey = ticket.personal.key;
  let state = await call(ctx, 'workplace.actions.get', { ticketKey });
  if (ctx.signal?.aborted) return () => {};
  const status = el('div', { class: 'work-action-status', 'aria-live': 'polite' }), editor = el('div'), receipts = el('div');
  const choice = select([], ''), text = textArea(ticket.personal.followupDraft, 20000, 9), assignment = select([], ''), detail = el('div');
  let selected, previewToken = '', busy = false, preparing = false, confirming = false, disposed = false, generation = 0, requestedKind = '', inspectBusy = false, previewDialog = null, submission = null, settleSubmission = null, closing = false;
  const usesChoices = candidate => ['assignment', 'status', 'priority'].includes(candidate?.kind);
  const all = () => [...(state.candidates || []), ...(state.adapters || []).filter(a => !(state.candidates || []).some(c => c.key === a.key))];
  const available = () => all().filter(candidate => !requestedKind || candidate.kind === requestedKind);
  const active = () => !disposed && !ctx.signal?.aborted;
  const invalidate = () => {
    generation++;
    if (previewToken) {
      previewToken = '';
      ctx.api.call('workplace.actions.invalidate', {}).catch(error => { if (active()) ctx.notify(error.message, 'error'); });
    }
  };
  const hub = ticketControlHub(ctx, ticket, kind => { if (closing || busy || preparing || confirming) return; requestedKind = kind; hub.select(kind); updateChoices(); });
  text.addEventListener('input', invalidate); assignment.addEventListener('change', invalidate);
  function showEditor() {
    invalidate(); selected = available().find(candidate => candidate.key === choice.value);
    if (!selected) {
      const label = { followup: 'ajout de suivi', task: 'ajout de tâche', assignment: 'affectation' }[requestedKind];
      editor.replaceChildren(note(label ? `Aucun formulaire d’${label} reconnu. Ouvrez ce formulaire dans GLPI puis inspectez-le. Aucune autre action ne sera choisie à sa place.`
        : simulation ? 'Inspectez les formulaires de simulation pour choisir une action.' : 'Affichez la fiche GLPI, ouvrez le formulaire voulu puis inspectez-le.'));
      return;
    }
    if (usesChoices(selected)) {
      assignment.replaceChildren(...(selected.choices || []).map(value => el('option', { value: value.value, text: value.label })));
      assignment.value = selected.currentValue || assignment.options[0]?.value || '';
    }
    const needsChoices = usesChoices(selected) && !assignment.options.length;
    detail.replaceChildren(note(needsChoices ? 'Ce formulaire est mémorisé, mais ses choix ne sont pas encore vérifiés. Inspectez le formulaire ouvert pour les relire.'
      : selected.otherFields || (simulation ? 'L’aperçu décrit la modification de la copie de simulation.' : 'L’aperçu relira les autres champs et la visibilité du formulaire avant tout envoi.')));
    const advanced = el('details', { class: 'ticket-action-settings' }, el('summary', { text: 'Réglages du formulaire reconnu' }), toolbar(
      button('Mémoriser cet adaptateur', run(ctx, async () => { state = await call(ctx, 'workplace.actions.adapter.save', { ticketKey, adapterKey: selected.key }); if (active()) ctx.notify('Adaptateur mémorisé pour cette installation.'); }), { disabled: !(state.candidates || []).some(c => c.key === selected.key) }),
      button('Retirer l’adaptateur mémorisé', run(ctx, async () => { state = await call(ctx, 'workplace.actions.adapter.remove', { ticketKey, adapterKey: selected.key }); if (active()) updateChoices(); }))));
    editor.replaceChildren(field(selected.kind === 'status' ? 'Statut simulé' : selected.kind === 'priority' ? 'Priorité proposée' : selected.kind === 'assignment' ? (simulation ? 'Affectation simulée' : 'Affectation proposée par GLPI') : 'Texte à ajouter', usesChoices(selected) ? assignment : text), detail,
      toolbar(button('Prévisualiser l’action…', run(ctx, preview), { primary: true, disabled: !state.canWrite || needsChoices })), advanced);
  }
  function updateChoices() {
    const previous = choice.value;
    choice.replaceChildren(...available().map(candidate => el('option', { value: candidate.key, text: candidate.name })));
    if (available().some(candidate => candidate.key === previous)) choice.value = previous;
    choice.disabled = !choice.options.length;
    showEditor(); showReceipts();
  }
  choice.addEventListener('change', showEditor);
  function showReceipts() {
    receipts.replaceChildren(card('Reçus conservés', ...(state.receipts || []).map(receipt => {
      const review = ['unknown', 'pending'].includes(receipt.state);
      return el('details', { class: 'work-receipt', open: review && !receipt.reviewedAt }, el('summary', {},
        badge(simulation ? 'Simulation · ' + (receipt.state === 'verified' ? 'Appliquée' : receipt.state === 'notSent' ? 'Non appliquée' : 'À vérifier') : receipt.state === 'verified' ? 'Vérifié' : receipt.state === 'notSent' ? 'Non envoyé' : 'À vérifier', receipt.state === 'verified' ? 'success' : 'warning'),
        el('span', { text: `${date(receipt.requestedAt)} · ${receipt.operation}` })),
        note(receipt.message), pre(`Référence : ${receipt.id}\nAvant : ${receipt.before}\n\nAprès :\n${receipt.after}\n\nAutres champs :\n${receipt.otherFields}`),
        receipt.reviewedAt ? note('Vérification manuelle déclarée le ' + date(receipt.reviewedAt)) : review ? button('J’ai vérifié cet envoi dans GLPI…', run(ctx, async () => {
          if (!await confirm('Vérification manuelle', 'Vérifiez la fiche et la référence unique de ce reçu dans GLPI. Cette déclaration ne transforme pas un résultat indéterminé en résultat prouvé.', 'Confirmer ma vérification')) return;
          await call(ctx, 'workplace.actions.review', { ticketKey, receiptId: receipt.id, confirmed: true }); state = await call(ctx, 'workplace.actions.get', { ticketKey }); if (active()) showReceipts();
        })) : null);
    }), !(state.receipts || []).length ? note('Aucun reçu pour ce dossier.') : null,
      state.legacyReceipt ? el('details', {}, el('summary', { text: 'Dernier reçu historique du poste' }), pre(state.legacyReceipt)) : null));
  }
  async function preview() {
    if (!active() || closing || busy || preparing || confirming || !selected || !state.canWrite) return;
    const value = usesChoices(selected) ? assignment.value : text.value, adapterKey = selected.key;
    const expectedGeneration = generation;
    preparing = true; editor.setAttribute('aria-busy', 'true');
    try {
      const result = await call(ctx, 'workplace.actions.preview', { ticketKey, adapterKey, value });
      if (!active() || generation !== expectedGeneration) {
        await ctx.api.call('workplace.actions.invalidate', {});
        if (active()) ctx.notify('La saisie a changé pendant la lecture. Préparez un nouvel aperçu.', 'info');
        return;
      }
      if (!Number.isFinite(Date.parse(result.expiresAt))) { await ctx.api.call('workplace.actions.invalidate', {}); throw new Error('L’aperçu ne contient pas de durée de validité vérifiable. Aucun envoi autorisé.'); }
      previewToken = result.token;
      const content = el('div', { class: 'work-write-plan' }, badge(simulation ? 'Aperçu de la simulation' : 'Aperçu · aucune soumission'), el('h3', { text: `Ticket ${result.ticket.id} · ${result.operation}` }),
        note(result.ticket.url), field('Avant', pre(result.before)), field('Après', pre(result.after)), field('Autres champs transmis par le formulaire', pre(result.otherFields)),
        note('Vérifiez les destinataires et la visibilité du suivi. Valable jusqu’au ' + date(result.expiresAt) + '. Le formulaire sera contrôlé une nouvelle fois.'));
      previewDialog = dialog(simulation ? 'Relire la modification simulée' : 'Relire l’action GLPI', content, [
        { label: 'Fermer', action: close => close() },
        { label: simulation ? 'Appliquer dans la simulation…' : 'Confirmer l’aperçu et envoyer une fois…', danger: !simulation, primary: simulation, action: run(ctx, async close => {
          if (!active() || closing || busy || confirming) return;
          if (previewToken !== result.token || Date.now() >= Date.parse(result.expiresAt)) throw new Error('Aperçu périmé. Préparez un nouvel aperçu.');
          confirming = true;
          try {
            if (!await confirm(simulation ? 'Modifier le ticket simulé ?' : 'Envoyer dans GLPI ?', `Ticket ${result.ticket.id} · ${result.operation}\n` + (simulation ? 'Les valeurs de l’aperçu seront appliquées à la copie de simulation.' : 'Cette confirmation autorise une seule soumission avec les valeurs de l’aperçu.'), simulation ? 'Appliquer la simulation' : 'Envoyer une fois')) return;
            if (!active() || previewToken !== result.token || Date.now() >= Date.parse(result.expiresAt)) throw new Error('Aperçu périmé. Aucun nouvel envoi demandé.');
            busy = true; submission = new Promise(resolve => { settleSubmission = resolve; });
            previewToken = ''; close(); root.inert = true;
            status.replaceChildren(note(simulation ? 'Application de la modification simulée…' : 'Soumission unique puis relecture. Un résultat indéterminé devra être vérifié dans GLPI, sans renvoi automatique.'));
            try {
              const receipt = await call(ctx, 'workplace.actions.confirm', { ticketKey, token: result.token, confirmed: true });
              if (!active()) return;
              status.replaceChildren(badge(simulation ? (receipt.state === 'verified' ? 'Modification simulée appliquée' : 'Résultat de la simulation') : receipt.state === 'verified' ? 'Résultat vérifié' : receipt.state === 'notSent' ? 'Aucun envoi' : 'Résultat indéterminé', receipt.state === 'verified' ? 'success' : 'warning'), note(receipt.message));
              if (receipt.state === 'verified' && onApplied) await onApplied(await call(ctx, 'workplace.ticket.read', { ticketKey }));
            } finally {
              busy = false; root.inert = false;
              state = await call(ctx, 'workplace.actions.get', { ticketKey }); if (active()) showReceipts();
            }
          } finally {
            confirming = false;
            // A close requested after submission waits through the receipt refresh,
            // even if that refresh fails. Never discard the request or submit twice.
            const settle = settleSubmission; settleSubmission = null; submission = null; settle?.();
          }
        }) }
      ]);
      previewDialog.element.addEventListener('pilot:closed', () => { previewDialog = null; invalidate(); }, { once: true });
    } finally { preparing = false; editor.removeAttribute('aria-busy'); }
  }
  replaceChildren(root, hub.element,
    note(simulation ? 'Simulation locale : les essais ne modifient pas GLPI.' : 'Un formulaire reconnu, un aperçu, puis une confirmation. Les droits restent ceux de votre session GLPI. Aucun envoi automatique ni groupé.'),
    !state.canWrite ? note(simulation ? 'Ouvrez une fiche du relevé de simulation pour préparer une action.' : 'Chargez un relevé GLPI actuel et vérifié pour préparer une écriture. Les archives et imports restent en lecture seule.') : null,
    toolbar(button(simulation ? 'Afficher les formulaires simulés' : 'Afficher la fiche GLPI', run(ctx, async () => {
      await call(ctx, 'workplace.ticket.browser', { ticketKey });
      if (simulation) { state = await call(ctx, 'workplace.actions.inspect', { ticketKey }); if (active()) updateChoices(); }
      else ctx.notify('Ouvrez le formulaire natif voulu dans la fiche, puis revenez l’inspecter.');
    }), { disabled: !ticket.canRead }), button(simulation ? 'Inspecter les formulaires simulés' : 'Inspecter le formulaire ouvert', run(ctx, async () => {
      if (closing || inspectBusy || busy || preparing) return;
      inspectBusy = true; invalidate();
      try { state = await call(ctx, 'workplace.actions.inspect', { ticketKey }); if (active()) { updateChoices(); status.replaceChildren(note(`${available().length} formulaire(s) reconnu(s) pour l’action choisie.`)); } }
      finally { inspectBusy = false; }
    }), { disabled: !state.canWrite })), field('Adaptateur de formulaire', choice), editor, status, receipts);
  updateChoices();
  const aborted = () => { disposed = true; previewDialog?.close(); invalidate(); };
  ctx.signal?.addEventListener('abort', aborted, { once: true });
  return async () => {
    if (submission) { closing = true; await submission; }
    if (busy || confirming) throw new Error('Une action est en cours de confirmation ou d’envoi. Consultez son résultat avant de quitter le panneau.');
    disposed = true; ctx.signal?.removeEventListener('abort', aborted); previewDialog?.close(); invalidate();
  };
}

export async function renderInsights(ctx, root, ticket) {
  const ticketKey = ticket.personal.key; const data = await call(ctx, 'workplace.ticket.insights', { ticketKey }); const report = data.report;
  const observed = { key: 'observedAt', label: 'Lu le', render: row => date(row.observedAt) };
  const bool = value => value == null ? 'Indéterminé' : value ? 'Oui' : 'Non';
  const linkColumns = [{ key: 'identity', label: 'Identifiant' }, { key: 'title', label: 'Titre' }, { key: 'relatedType', label: 'Objet' }, observed,
    { key: 'open', label: 'Action', render: row => button(row.kind === 'relation' ? 'Consulter' : 'Télécharger…', run(ctx, async () => {
      const result = await call(ctx, row.kind === 'relation' ? 'workplace.relation.open' : 'workplace.document.download', { ticketKey, index: row.index, readAt: data.readAt });
      if (result.ticketKey) ctx.navigate('tickets', { ticketKey: result.ticketKey }); else if (result.saved) ctx.notify('Document enregistré : ' + result.name);
    }), { disabled: !row.allowed, title: row.reason }) }];
  const paged = (title, rows, columns) => {
    const body = el('div'); let page = 0;
    const draw = () => body.replaceChildren(dataTable(rows.slice(page * 50, page * 50 + 50), columns), toolbar(button('Précédent', () => { page--; draw(); }, { disabled: page === 0 }), note(`${rows.length} élément(s) · page ${page + 1}`), button('Suivant', () => { page++; draw(); }, { disabled: (page + 1) * 50 >= rows.length })));
    draw(); return card(title, body);
  };
  const links = (title, kind, initial, total) => {
    const body = el('div'); let page = 0; let rows = initial;
    const draw = () => body.replaceChildren(dataTable(rows, linkColumns), pager(page, 50, total, run(ctx, async value => {
      const next = await call(ctx, 'workplace.ticket.links', { ticketKey, kind, page: value, readAt: data.readAt });
      rows = next.rows; page = next.page; draw();
    })));
    draw(); return card(title, body);
  };
  root.replaceChildren(note(report.coverage),
    paged('Tâches planifiées', report.tasks, [{ key: 'title', label: 'Tâche' }, { key: 'owner', label: 'Responsable' }, { key: 'start', label: 'Début', render: r => date(r.start) }, { key: 'due', label: 'Échéance', render: r => date(r.due) }, { key: 'completed', label: 'Achevée', render: r => bool(r.completed) }, { key: 'overdue', label: 'En retard à la lecture', render: r => bool(r.overdue) }, observed]),
    paged('Validations', report.validations, [{ key: 'title', label: 'Validation' }, { key: 'responsible', label: 'Responsable' }, { key: 'status', label: 'État lu' }, { key: 'requestedAt', label: 'Demandée le', render: r => date(r.requestedAt) }, { key: 'ageDays', label: 'Ancienneté (jours)', render: r => r.ageDays == null ? '—' : r.ageDays.toFixed(1) }, observed]),
    paged('Mises en attente', report.waits, [{ key: 'reason', label: 'Motif' }, { key: 'start', label: 'Début', render: r => date(r.start) }, { key: 'end', label: 'Fin', render: r => date(r.end) }, { key: 'hours', label: 'Durée connue (h)', render: r => r.hours == null ? 'Indéterminée' : r.hours.toFixed(1) }, observed]),
    card('Dernier échange daté recueilli', report.lastExchange ? pre(`${date(report.lastExchange.date)} · ${report.lastExchange.author} · ${report.lastExchange.role}\n${report.lastExchange.content}\n\n${report.lastExchange.waitingFor}`) : note('Aucun échange daté recueilli.')),
    links('Relations observées', 'relation', data.relations, data.relationTotal), note('Le contenu des documents n’est pas indexé. Le transfert utilise la session authentifiée et un emplacement choisi explicitement.'), links('Documents observés', 'document', data.documents, data.documentTotal));
}
