import { el, button, card, toolbar, empty, badge, input, select, field, table, dialog, call, command, heading, caption, details,
  number, formatNumber, formatDate, rowDialog, checkbox, textArea, getRevision, error } from './analytics-ui.js';

export async function renderAssistant(ctx) {
  const cleanup = heading(ctx, 'Assistant de langage', 'Une demande prépare un contenu visible. Le moteur propose un brouillon de requête ou une synthèse citée ; chaque envoi et chaque enregistrement restent explicites.');
  const profile = await call(ctx, 'assistant.config.get'); if (ctx.signal.aborted) return cleanup;
  const operation = select([{ value: 'query', label: 'Proposer une requête' }, { value: 'summary', label: 'Synthétiser des tickets' }], ctx.params?.operation ?? 'query');
  const provider = select([{ value: 'local', label: 'Moteur local déterministe' }, { value: 'compatible', label: 'Endpoint compatible chat/completions' }], profile.provider);
  const endpoint = input(profile.endpoint, { placeholder: 'https://fournisseur.example/v1/chat/completions', maxLength: 2000 }); const model = input(profile.model, { maxLength: 120 });
  const maximum = input(profile.maximumTickets, { type: 'number', min: 1, max: 200 }); const timeout = input(profile.timeoutSeconds, { type: 'number', min: 10, max: 180 });
  const external = checkbox('Activer explicitement l’envoi externe pour cette préparation', false);
  const prompt = textArea('', 5); prompt.maxLength = 2000; prompt.placeholder = 'Exemple local : compte les tickets actifs par technicien dont « Titre » contient « VPN »';
  const scope = select([{ value: 'all', label: 'Toutes les lignes du relevé' }, ...(ctx.params?.runId ? [{ value: 'dashboard', label: 'Périmètre du dashboard depuis lequel cet assistant a été ouvert' }] : [])], ctx.params?.runId ? 'dashboard' : 'all');
  const identifiers = textArea('', 2); identifiers.placeholder = 'Facultatif : identifiants précis séparés par espace, virgule ou retour à la ligne';
  const roleControls = new Map(); const roles = el('div', { class: 'analytics-role-grid' });
  for (const role of profile.roles) {
    const selected = checkbox(role.label, profile.defaultRoles.includes(role.key)); selected.control.disabled = role.key === 'id';
    roleControls.set(role.key, selected.control); roles.append(selected.element);
  }
  const key = input('', { type: 'password', autocomplete: 'off', maxLength: 8000 }); const remember = checkbox('Mémoriser cette clé avec la protection de mon compte Windows');
  const preparationHost = el('div'); const proposalHost = el('div'); const state = caption('Aucune préparation active. Aucun envoi réseau effectué.');
  const consent = checkbox('J’ai vérifié la destination et les seules données affichées ; j’autorise cet envoi', false);
  let prepared = null; let generation = 0; let generateAbort = null;
  const options = () => ({ provider: provider.value, endpoint: endpoint.value.trim(), model: model.value.trim(), externalEnabled: external.control.checked,
    maximumTickets: Number(maximum.value), timeoutSeconds: Number(timeout.value) });
  const invalidate = () => {
    generation++; prepared = null; consent.control.checked = false; generate.disabled = true;
    preparationHost.replaceChildren(); proposalHost.replaceChildren(); state.textContent = 'Demande modifiée : préparez à nouveau le contenu avant de générer.';
  };
  const requestForm = el('div', {}, el('div', { class: 'analytics-form-grid' }, field('Objectif', operation), field('Moteur', provider), field('Endpoint complet', endpoint), field('Modèle', model),
    field('Tickets fournis au maximum', maximum), field('Délai maximal (secondes)', timeout)), external.element, field('Votre demande', prompt),
    el('div', { class: 'analytics-summary-options' }, field('Périmètre de synthèse', scope), field('Limiter à des identifiants précis', identifiers), details('Champs de tickets autorisés dans la synthèse', roles,
      caption('Les champs personnels et les titres restent exclus tant que vous ne les sélectionnez pas. Une demande de requête transmet seulement le schéma, jamais les cellules des tickets.'))));
  requestForm.addEventListener('input', invalidate); requestForm.addEventListener('change', invalidate);
  const updateMode = () => {
    const remote = provider.value === 'compatible'; endpoint.disabled = model.disabled = !remote; external.control.disabled = !remote;
    requestForm.querySelector('.analytics-summary-options').hidden = operation.value !== 'summary';
  };
  operation.addEventListener('change', updateMode); provider.addEventListener('change', updateMode); updateMode();
  const generate = command(ctx, 'Générer depuis cette préparation', async () => {
    if (!prepared) throw new Error('Préparez les données et vérifiez leur contenu avant de générer.');
    const captured = generation; const request = prepared;
    if (request.provider === 'compatible' && !consent.control.checked) throw new Error('Vérifiez le contenu préparé puis cochez l’autorisation de cet envoi précis.');
    generateAbort?.abort(); generateAbort = new AbortController(); state.textContent = request.provider === 'local' ? 'Calcul local de la proposition…' : `Requête explicite en cours vers ${request.destination}…`;
    try {
      const result = await ctx.api.call('assistant.generate', { preparationId: request.preparationId, revision: request.revision, confirmed: consent.control.checked, apiKey: key.value },
        { signal: AbortSignal.any([ctx.signal, generateAbort.signal]) });
      if (captured !== generation || ctx.signal.aborted) return;
      state.textContent = 'Proposition reçue et contrôlée contre le périmètre fourni. Aucun enregistrement automatique.';
      showProposal(result);
    } catch (reason) {
      state.textContent = reason.name === 'AbortError' || reason.code === 'CANCELLED' ? 'Génération annulée ; aucune proposition appliquée.' : 'La génération n’a pas abouti.';
      throw reason;
    }
  }, { primary: true, disabled: true });
  const prepare = command(ctx, 'Préparer et voir le contenu exact', async () => {
    const captured = generation; const ticketIds = identifiers.value.trim() ? identifiers.value.trim().split(/[\s,;]+/).filter(Boolean) : null;
    const request = { ...options(), revision: getRevision(ctx), operation: operation.value, prompt: prompt.value,
      roles: [...roleControls].filter(([, control]) => control.checked).map(([role]) => role), ticketIds,
      ...(scope.value === 'dashboard' ? { runId: ctx.params.runId } : {}) };
    const response = await call(ctx, 'assistant.prepare', request); if (captured !== generation || ctx.signal.aborted) return;
    prepared = response; consent.control.checked = false; generate.disabled = false; proposalHost.replaceChildren();
    const exact = el('pre', { class: 'analytics-prepared-content', text: response.preview });
    preparationHost.replaceChildren(card('Contenu préparé · pas encore envoyé', badge(response.destination),
      caption(`${formatDate(response.extractedAt)} · ${response.scopeRows} lignes dans le périmètre · ${response.tickets} tickets fournis · ${response.complete ? 'source complète' : 'source incomplète / non confirmée'}`),
      caption(response.scope), ...response.limitations.map(limit => caption(limit, 'warning')), exact));
    consent.element.hidden = response.provider !== 'compatible'; state.textContent = response.note;
  }, { disabled: !ctx.state.session?.loaded });
  ctx.root.append(card('Demande et destination', caption(profile.note), requestForm),
    details('Clé d’accès et profil local', field('Clé pour cette session (facultative)', key), remember.element,
      profile.hasSavedKey ? caption(`Une clé protégée est conservée pour ${profile.keyEndpoint}. Elle n’est jamais affichée ni exportée.`) : caption('Aucune clé mémorisée.'),
      toolbar(command(ctx, 'Enregistrer le profil local', async () => {
        await call(ctx, 'assistant.config.save', { ...options(), apiKey: key.value, rememberKey: remember.control.checked }); invalidate(); ctx.notify('Profil local enregistré ; l’envoi externe restera désactivé à la prochaine ouverture.');
      }), command(ctx, 'Oublier la clé mémorisée', async () => {
        await call(ctx, 'assistant.config.save', { ...options(), forgetKey: true }); key.value = ''; invalidate(); ctx.notify('Clé mémorisée retirée.');
      }))),
    card('Préparer puis générer', toolbar(prepare, generate, button('Annuler la génération', () => generateAbort?.abort())), consent.element, state), preparationHost, proposalHost,
    details('Capacités et limites', caption('Le moteur local reconnaît une grammaire bornée de comptage, filtres, regroupements et agrégats. Une formulation non reconnue est refusée ; aucun périmètre plus large n’est deviné.'),
      caption('Le moteur compatible doit prendre en charge chat/completions, le mode JSON et max_completion_tokens. Les redirections, outils et appels de fonctions sont refusés. Les réponses et demandes sont bornées.'),
      caption('Les synthèses sont limitées aux tickets à identifiant unique effectivement fournis. Chaque affirmation doit citer des identifiants connus et des extraits exacts. Le contrôle de ces références ne prouve pas la justesse de toute interprétation.')));
  consent.element.hidden = true;

  function showProposal(result) {
    proposalHost.replaceChildren();
    if (result.kind === 'query') {
      const previewHost = el('div');
      const definition = result.query;
      const body = card('Proposition de requête · brouillon', el('h3', { text: definition.name }), caption(result.explanation), ...result.assumptions.map(a => caption(a, 'warning')),
        table(definition.conditions, [{ key: 'field', label: 'Champ' }, { key: 'operator', label: 'Opérateur' }, { key: 'value', label: 'Valeur' }, { key: 'value2', label: 'Borne haute' }]),
        caption(`Population : ${definition.baseProbe} · agrégat : ${definition.aggregate} · regroupements : ${[definition.groupBy, definition.groupBySecondary, definition.groupByTertiary].filter(Boolean).join(' › ') || 'aucun'}`),
        caption(result.note), toolbar(command(ctx, 'Prévisualiser les résultats', async () => {
          const preview = await call(ctx, 'queries.preview', { revision: result.revision, query: definition });
          previewHost.replaceChildren(badge(preview.available ? 'Prévisualisation disponible' : 'Prévisualisation indisponible', preview.available ? 'success' : 'warning'),
            caption(`${number(preview.metric.value, preview.metric.unit)} · ${preview.rows} lignes · ${preview.unknownRows} décisions inconnues`), caption(preview.message), caption(preview.metric.explanation),
            ...preview.available ? [command(ctx, 'Voir les lignes retenues', () => rowDialog(ctx, 'queries.rows', { runId: preview.runId, revision: preview.revision }, 'Prévisualisation de la proposition'))] : []);
        }), command(ctx, 'Ouvrir le brouillon dans l’atelier', () => ctx.navigate('queries', { assistantDraft: result.draftId }), { primary: true })), previewHost);
      proposalHost.append(body);
    } else {
      const claims = result.claims.map(claim => card('', el('p', { text: claim.text }), el('div', { class: 'analytics-citations' }, ...claim.citations.map(reference =>
        details(`Ticket ${reference.ticketId} · ${reference.field}`, el('blockquote', { text: reference.quote }), command(ctx, 'Ouvrir la ligne source', () => ctx.navigate('tickets', { rowIndex: reference.rowIndex, revision: result.revision })))))));
      proposalHost.append(card(result.title, caption(result.note), ...result.limitations.map(limit => caption(limit, 'warning')), ...claims));
    }
    proposalHost.scrollIntoView({ block: 'start', behavior: 'smooth' });
  }
  return () => { generateAbort?.abort(); key.value = ''; cleanup(); };
}
