import { el, button, card, toolbar, badge, dialog, field, input, select, table, formatDate, formatNumber, isSimulation } from '../ui.js';
import { checkbox } from './analytics-ui.js';

const previewLimit = 50;
const absent = '(absent)';
const valueText = value => value === undefined ? absent : JSON.stringify(value);
const normalized = value => String(value ?? '').normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase('fr');
const sectionTitles = { TerrainServices: 'Terrain : cartes, météo et itinéraires', TicketActions: 'Actions et formulaires des tickets',
  Workplace: 'Lecture, rappels et modèles de travail', AlertRules: 'Règles d’alerte', TableViews: 'Vues enregistrées des tableaux',
  TableColumns: 'Ordre, largeur et visibilité des colonnes', Technicians: 'Préférences des techniciens', TourOrder: 'Organisation des tournées' };

// Keep every leaf, including empty arrays and objects. Paging limits the DOM,
// never the comparison or the values available for inspection.
function flatten(value, path = '', result = new Map()) {
  if (Array.isArray(value) && value.length) value.forEach((item, index) => flatten(item, `${path}[${index}]`, result));
  else if (value && typeof value === 'object' && Object.keys(value).length)
    for (const [key, child] of Object.entries(value)) flatten(child, path ? `${path}.${key}` : key, result);
  else result.set(path || '(valeur)', valueText(value));
  return result;
}

function comparedFields(review) {
  if (!review.token || !Array.isArray(review.sections) || review.sections.some(section => !Object.hasOwn(section, 'before') || !Object.hasOwn(section, 'after')))
    throw new Error('L’aperçu complet avant/après est indisponible. Aucun import ne peut être confirmé.');
  return review.sections.flatMap(section => {
    const before = flatten(section.before), after = flatten(section.after);
    return [...new Set([...before.keys(), ...after.keys()])].map(path => ({ section: section.key, title: section.title,
      shortTitle: ({ Settings: 'Application', Updates: 'Mises à jour', Library: 'Bibliothèque / PostgreSQL', Assistant: 'Assistant' })[section.key] || section.title,
      path, before: before.get(path) ?? absent, after: after.get(path) ?? absent,
      changed: before.get(path) !== after.get(path) }));
  });
}

function requireSaved(ctx) {
  if ([...ctx.root.querySelectorAll('.form-grid')].some(form => form.pilotHasUnsavedChanges?.()))
    throw new Error('Appliquez ou annulez les modifications ouvertes dans Réglages avant d’examiner une configuration complète.');
}

function ownDialog(ctx, modal, dialogs) {
  dialogs?.add(modal);
  const close = () => modal.close();
  ctx.signal?.addEventListener('abort', close, { once: true });
  modal.element.addEventListener('pilot:closed', () => { ctx.signal?.removeEventListener('abort', close); dialogs?.delete(modal); }, { once: true });
  return modal;
}

export function reviewDeploymentConfiguration(ctx, review, { seed = false, dialogs } = {}) {
  const sections = review.sections?.map(item => ({ ...item, title: sectionTitles[item.key] || item.title }));
  const fields = comparedFields({ ...review, sections }), modified = fields.filter(item => item.changed).length;
  let offset = 0;
  const search = input('', { type: 'search', placeholder: 'Chercher une option, un site, un chemin ou une valeur…', 'aria-label': 'Rechercher dans toutes les options' });
  const section = select([{ value: '', label: 'Toutes les sections' }, ...sections.map(item => ({ value: item.key, label: item.title }))]);
  section.setAttribute('aria-label', 'Section de configuration');
  const changes = select([{ value: 'all', label: 'Toutes les options' }, { value: 'changed', label: 'Uniquement les différences' }]);
  changes.setAttribute('aria-label', 'Options affichées');
  const rows = el('div', { class: 'deployment-comparison' }), paging = el('div'), summary = el('p', { role: 'status' });
  const acknowledged = checkbox('Je confirme le remplacement de toutes les options du profil par les valeurs affichées.');
  const content = el('div', { class: 'deployment-review' },
    el('p', { text: 'Examinez toutes les options avant de remplacer la configuration du profil. Les valeurs du fichier seront appliquées ensemble après votre confirmation.' }),
    el('p', {}, el('strong', { text: 'Fichier : ' }), el('code', { text: review.fileName })),
    el('p', { class: 'help deployment-hash', text: `SHA-256 : ${review.sha256}` }),
    review.warnings?.length ? el('div', { class: 'notice warning' }, el('ul', {}, ...review.warnings.map(text => el('li', { text })))) : null,
    table(sections, [{ key: 'title', label: 'Section' }, { label: 'Comparaison', render: item => badge(item.changed ? 'Modifiée' : 'Identique', item.changed ? 'warning' : '') }], { maxRows: sections.length }),
    el('h3', { text: 'Toutes les valeurs avant et après' }),
    toolbar(field('Section', section), field('Afficher', changes)), field('Recherche', search), summary, rows, paging,
    el('p', { class: 'help', text: 'Les tableaux de réglages sont détaillés par position [0], [1], etc. « (absent) » indique une valeur ajoutée ou retirée. Les chaînes restent entre guillemets pour conserver les espaces et caractères exacts.' }),
    acknowledged.element);
  const actions = [{ label: 'Annuler', action: close => close() },
    ...(seed ? [{ label: 'Garder mes réglages', action: async close => {
      await ctx.api.call('deployment.decline', { sha256: review.sha256 }, { signal: ctx.signal }); close();
      ctx.notify('Vos réglages sont conservés pour cette configuration du pack.');
    } }] : []),
    { label: 'Sauvegarder et remplacer', primary: true, disabled: true, action: async close => {
      if (!acknowledged.control.checked) throw new Error('Confirmez le remplacement après lecture de l’aperçu.');
      const args = { token: review.token, confirmed: true }; close();
      let result;
      try {
        result = await ctx.api.call('deployment.apply', args, { signal: ctx.signal });
      } catch (error) {
        if (error.name !== 'AbortError') dialog('Configuration non appliquée', el('p', { text: error.message || String(error) }));
        return;
      }
      try { await ctx.navigate('settings', { deploymentResult: { backupPath: result.backupPath, sha256: result.sha256 } }); }
      catch (error) { dialog('Configuration appliquée', el('div', {}, el('p', { text: 'Les réglages sont enregistrés. Rouvrez Réglages pour actualiser l’écran.' }),
        el('p', { text: error.message || String(error) }), el('pre', { class: 'deployment-value', text: result.backupPath }))); }
    } }];
  const modal = ownDialog(ctx, dialog('Examiner la configuration complète', content, actions), dialogs);
  modal.element.classList.add('deployment-review-modal');
  modal.element.dataset.deploymentSha256 = review.sha256;
  const apply = modal.element.querySelector('.modal-actions .button.primary');
  acknowledged.control.addEventListener('change', () => { apply.disabled = !acknowledged.control.checked; });
  function paint() {
    const needle = normalized(search.value);
    const selected = fields.filter(item => (!section.value || item.section === section.value)
      && (changes.value !== 'changed' || item.changed) && normalized(`${item.title} ${item.path} ${item.before} ${item.after}`).includes(needle));
    offset = Math.min(offset, Math.max(0, Math.ceil(selected.length / previewLimit) - 1) * previewLimit);
    summary.textContent = `${formatNumber(fields.length)} options examinables · ${formatNumber(modified)} différences · ${formatNumber(selected.length)} résultat(s)`;
    rows.replaceChildren(table(selected.slice(offset, offset + previewLimit), [
      { label: 'Section / option', render: item => el('div', {}, el('strong', { text: item.shortTitle, title: item.title }), el('code', { text: item.path, 'data-deployment-path': item.path })) },
      { label: 'Avant', render: item => el('pre', { class: 'deployment-value', text: item.before }) },
      { label: 'Après', render: item => el('pre', { class: 'deployment-value', text: item.after }) },
      { label: 'État', render: item => badge(item.changed ? 'Modifiée' : 'Identique', item.changed ? 'warning' : '') }
    ], { maxRows: previewLimit, emptyText: 'Aucune option ne correspond à cette recherche.' }));
    paging.replaceChildren(toolbar(button('Options précédentes', () => { offset -= previewLimit; paint(); }, { disabled: offset === 0 }),
      el('span', { text: `${selected.length ? offset + 1 : 0}–${Math.min(offset + previewLimit, selected.length)} / ${selected.length}`, 'data-deployment-page': '' }),
      button('Options suivantes', () => { offset += previewLimit; paint(); }, { disabled: offset + previewLimit >= selected.length })));
  }
  for (const control of [section, changes]) control.addEventListener('change', () => { offset = 0; paint(); });
  search.addEventListener('input', () => { offset = 0; paint(); }); paint();
  return modal;
}

export async function offerDeploymentConfiguration(ctx, deploymentState) {
  if (!deploymentState?.offer?.available || deploymentState.simulation || isSimulation(ctx.state.session)) return null;
  const offer = deploymentState.offer;
  const modal = ownDialog(ctx, dialog('Configuration fournie avec ce pack', el('div', {},
    el('p', { text: offer.reason || 'Ce pack propose une configuration différente. Vous pouvez examiner toutes les valeurs avant de remplacer vos réglages, ou conserver votre configuration actuelle.' }),
    el('p', { text: 'Le choix est mémorisé pour cette version et cette empreinte du fichier. Aucune base ni session de connexion n’est transférée.' }),
    el('p', { text: `Version du pack : ${offer.version}` }),
    el('p', {}, el('strong', { text: 'Fichier : ' }), el('code', { text: deploymentState.fileName })),
    el('p', { class: 'help deployment-hash', text: `SHA-256 : ${offer.sha256}` })), [
    { label: 'Garder mes réglages', action: async close => {
      await ctx.api.call('deployment.decline', { sha256: offer.sha256 }, { signal: ctx.signal }); close();
      ctx.notify('Vos réglages sont conservés pour cette configuration du pack.');
    } },
    { label: 'Examiner la configuration du pack', primary: true, action: async close => {
      requireSaved(ctx); const preview = await ctx.api.call('deployment.seed.preview', {}, { signal: ctx.signal });
      close(); reviewDeploymentConfiguration(ctx, preview, { seed: true });
    } }
  ]));
  return modal;
}

export async function createDeploymentConfiguration(ctx, { onState } = {}) {
  const state = await ctx.api.call('deployment.state', {}, { signal: ctx.signal });
  onState?.(state);
  const simulation = state.simulation || isSimulation(ctx.state.session), dialogs = new Set();
  const status = el('div', { class: 'deployment-status', 'data-deployment-state': state.seedStatus || '' },
    el('p', {}, badge('Premier lancement du profil', 'info'), ' ', state.seedMessage || 'Aucun résultat de configuration initiale disponible.'),
    state.seedAppliedAt ? el('p', { class: 'help', text: `Application initiale : ${formatDate(state.seedAppliedAt)}` }) : null);
  const result = ctx.params?.deploymentResult;
  const element = card('Configuration complète et déploiement',
    el('p', { text: 'Transférez les options du logiciel : sources, sites et communes, règles, interface, automatisations, connexions publiques, assistant et mises à jour par dossier réseau.' }),
    el('p', { class: 'help', text: 'Les bases, tickets, calendriers enregistrés, sessions SSO et secrets restent sur chaque poste. Un import ne donne aucun pouvoir supplémentaire au lanceur Standard.' }),
    simulation ? el('p', { class: 'notice warning', text: 'Quittez la démonstration pour importer ou exporter la configuration complète du profil réel.' }) : null,
    toolbar(button('Définir comme configuration par défaut', () => {
      requireSaved(ctx);
      ownDialog(ctx, dialog('Configuration par défaut du pack', el('div', {},
        el('p', { text: `Enregistrer toutes les options actuelles dans wad/${state.fileName}, dans ce dossier PROD ? Le dossier wad est créé si nécessaire.` }),
        el('pre', { class: 'deployment-value', text: state.defaultPath || state.seedPath }),
        el('p', { text: 'Vous pourrez diffuser ce dossier avec sa configuration. Un profil neuf la recevra automatiquement ; un profil existant pourra l’examiner avant de remplacer ses réglages.' }),
        el('p', { class: 'help', text: 'Si ce dossier contient déjà une configuration par défaut, sa copie sera conservée dans AppData avant remplacement.' })), [
        { label: 'Annuler', action: close => close() },
        { label: 'Enregistrer comme défaut', primary: true, action: async close => {
          const saved = await ctx.api.call('deployment.default.save', { confirmed: true, expectedSha256: state.defaultSha256 || '' }, { signal: ctx.signal });
          Object.assign(state, saved.state); onState?.(state); close();
          ownDialog(ctx, dialog('Configuration par défaut enregistrée', el('div', {},
            el('p', { text: 'Ce fichier est enregistré dans wad. Vous pouvez diffuser le dossier PROD complet avec les options enregistrées.' }),
            el('pre', { class: 'deployment-value', text: saved.path }),
            el('p', { class: 'help deployment-hash', text: `SHA-256 : ${saved.sha256}` }),
            saved.backupPath ? el('div', {}, el('strong', { text: 'Ancienne configuration du pack conservée :' }), el('pre', { class: 'deployment-value', text: saved.backupPath })) : null)), dialogs);
        } }
      ]), dialogs);
    }, { disabled: simulation, icon: 'check' }), button('Exporter la configuration complète', async () => {
      requireSaved(ctx); const exported = await ctx.api.call('deployment.export', {}, { signal: ctx.signal });
      if (exported.cancelled) return;
      ownDialog(ctx, dialog('Configuration prête pour le pack PROD', el('div', {},
        el('p', { text: `Copiez le fichier exporté sous le nom ${state.fileName} dans le dossier wad du pack PROD décompressé. Les lanceurs GLPI-Pilot.exe et GLPI-Pilot-Admin.exe restent à la racine.` }),
        el('p', { text: 'Le premier lancement d’un profil AppData neuf l’applique automatiquement. Un profil existant conserve ses options et reçoit une proposition avec aperçu avant remplacement.' }),
        exported.path ? el('pre', { class: 'deployment-value', text: exported.path }) : null,
        exported.sha256 ? el('p', { class: 'help deployment-hash', text: `SHA-256 : ${exported.sha256}` }) : null)), dialogs);
    }, { disabled: simulation, icon: 'download' }), button('Examiner un fichier complet', async () => {
      requireSaved(ctx); const preview = await ctx.api.call('deployment.preview', {}, { signal: ctx.signal });
      if (!preview.cancelled) reviewDeploymentConfiguration(ctx, preview, { dialogs });
    }, { disabled: simulation, primary: true, icon: 'folder' }),
    state.offer?.available ? button('Examiner la configuration du pack', async () => {
      requireSaved(ctx); const preview = await ctx.api.call('deployment.seed.preview', {}, { signal: ctx.signal });
      reviewDeploymentConfiguration(ctx, preview, { seed: true, dialogs });
    }, { disabled: simulation }) : null),
    el('details', { class: 'deployment-pack-location' }, el('summary', { text: 'Préparer la configuration du premier lancement' }),
      el('p', { text: `Emplacement attendu : wad/${state.fileName}. Diffusez le dossier wad avec les deux lanceurs du pack PROD.` }),
      el('p', { class: 'help', text: 'Un ancien fichier à la racine reste reconnu si aucun fichier n’existe dans wad. Le fichier dans wad est prioritaire ; un fichier invalide est signalé sans appliquer silencieusement l’ancien.' }),
      el('p', { text: 'Sur un profil existant, Garder les réglages mémorise le refus pour cette version et ce fichier. Vous pouvez toujours importer manuellement une configuration complète.' }),
      el('p', { class: 'help', text: 'Emplacement d’enregistrement dans cette installation :' }), el('pre', { class: 'deployment-value', text: state.defaultPath || state.seedPath }),
      state.seedPath && state.seedPath !== state.defaultPath ? el('div', {}, el('p', { class: 'help', text: 'Ancienne configuration actuellement reconnue :' }), el('pre', { class: 'deployment-value', text: state.seedPath })) : null), status,
    result ? el('section', { class: 'notice success', 'data-deployment-applied': '' }, el('strong', { text: 'Configuration complète appliquée' }),
      el('p', { text: 'La configuration précédente est conservée dans cette sauvegarde complète. Pour la reprendre, sélectionnez-la avec « Examiner un fichier complet ».' }),
      el('pre', { class: 'deployment-value', text: result.backupPath }), el('p', { class: 'help deployment-hash', text: `SHA-256 appliqué : ${result.sha256}` })) : null);
  element.classList.add('deployment-settings');
  return { element, dispose() { for (const modal of dialogs) modal.close(); dialogs.clear(); } };
}
