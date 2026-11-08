import { replaceChildren, el, button, card, toolbar, empty, badge, form, table, dialog, input, select, field, formatNumber, formatDate, icon, extractionStatus, isSimulation, isFileSource, fileSourceDetailHelp } from '../ui.js';
import { importLocalTestFile } from '../local-file-test.js';
import { navigationVisible } from '../navigation.js';
import { createWorkspacePanels } from '../workspace-panels.js';
import { renderSources, browserOptions } from './foundation.js';
import { renderQuality, renderMapping } from './records.js';
import { renderLibrary } from './library.js';
import { renderLibraryCollection } from './library-collection.js';
import { renderEnrichment } from './workplace-day.js';
import { sourceCacheCard } from '../source-cache.js';
import { renderSharedRecords } from '../shared-records.js';
import { openGroupedCollection } from '../grouped-collection.js';

export async function renderDataWorkspace(ctx) {
  const { root, api, signal, state, notify, navigate } = ctx;
  const panels = createWorkspacePanels(ctx), simulation = isSimulation(state.session), demo = simulation && state.session.demo;
  const call = (operation, args = {}) => api.call(operation, args, { signal });
  root.classList.add('data-workspace'); root.dataset.workspace = 'data';
  let revision = state.session.revision;
  const selectedSource = state.settings.sources.find(item => item.id === state.settings.activeSourceId);
  const receipt = extractionStatus(state.session);
  const hero = el('section', { class: 'hero workspace-intro data-intro' }, el('div', {},
    el('p', { class: 'workspace-eyebrow', text: 'DONNÉES ET CONNEXIONS' }), el('h2', { text: simulation ? demo ? 'Les données de votre démonstration' : 'Votre fichier, en simulation' : 'Vos recherches GLPI, à jour' }),
    el('p', { text: simulation ? demo ? 'Essayez la collecte sur les données fictives. Votre connexion GLPI et vos réglages habituels restent séparés.' : 'Essayez la collecte sur les données du fichier chargé. Vos essais restent dans cet espace de simulation.' : 'Collectez vos tickets directement depuis GLPI. Le fichier CSV / Excel reste disponible pour travailler sur un extrait.' }),
    state.session.loaded ? el('div', { class: 'data-current-origin', 'data-current-origin': 'true' }, el('span', { text: 'Relevé affiché :' }), badge(receipt.label, receipt.tone), el('span', { text: `${formatNumber(state.session.rows)} lignes · ${formatDate(state.session.date)}` })) : null),
    el('div', { class: 'hero-icon', 'aria-hidden': 'true' }, icon('download')));
  const current = el('section', { class: 'workspace-data-source' }, el('div', {},
    el('p', { class: 'workspace-eyebrow', text: simulation ? 'SOURCE DE SIMULATION' : 'SOURCE ACTIVE' }),
    el('h3', { text: state.session.loaded ? state.session.title || selectedSource?.name || 'Relevé chargé' : selectedSource?.name || 'Aucune liste chargée' }),
    el('p', { class: 'help', text: state.session.loaded ? `${formatDate(state.session.date)} · ${receipt.receipt}` : 'Lancez une collecte GLPI ci-dessus, ou importez un fichier.' })),
    toolbar(state.session.loaded ? button('Voir les tickets', () => navigate('records'), { icon: 'ticket' }) : null,
      state.session.loaded ? button('Lire le pilotage', () => navigate('pilot'), { icon: 'chart' }) : null));
  const grid = el('div', { class: 'workspace-acquisition-grid', 'aria-label': 'Choisir comment actualiser les données' });
  root.append(hero, grid);
  const source = select([{ value: '', label: 'Configuration courante' }, ...state.settings.sources.map(item => ({ value: item.id, label: item.name }))], state.settings.activeSourceId);
  source.addEventListener('change', async () => { if (source.value) { await call('sources.select', { id: source.value, revision }); await ctx.refresh(); } });
  const connection = form([
    { key: 'portalUrl', label: simulation ? 'Adresse de la source simulée' : 'Adresse de la recherche GLPI', required: true, help: simulation ? demo ? 'La collecte utilise le scénario fictif, sans connexion à un portail réel.' : 'La collecte réutilise les lignes du fichier chargé, sans connexion à un portail réel.' : 'Collez l’adresse complète de votre recherche pour conserver ses filtres.' },
    { key: 'browserChannel', label: 'Navigateur', type: 'select', options: browserOptions(state) },
    { key: 'autoMode', label: 'Collecte', type: 'select', options: [{ value: 'native', label: 'Export natif GLPI' }, { value: 'pagination', label: 'Tableau et pagination' }] }
  ], { ...state.settings, portalUrl: simulation ? state.settings.portalUrl || 'https://simulation.glpi.local/front/ticket.php' : state.settings.portalUrl });
  const savedSettings = result => { revision = result.revision; };
  const saveConnection = async () => { const result = await call('settings.save', { revision, settings: { ...state.settings, ...connection.read() } }); savedSettings(result); connection.markSaved(); };
  const prepareConnection = async () => { if (connection.element.pilotHasUnsavedChanges()) await saveConnection(); };
  const manageSearches = async () => { await prepareConnection(); await openTool('sources'); };
  const stopSettings = api.on('settings.changed', updated => {
    // A saved-search panel can change the selected recipe. Refresh clean fields
    // in place; a locally edited connection keeps its revision and stale guard.
    if (signal.aborted || connection.element.pilotHasUnsavedChanges()) return;
    revision = updated.revision;
    for (const key of ['portalUrl', 'browserChannel', 'autoMode']) connection.controls.get(key).control.value = updated.settings[key];
    connection.markSaved();
    replaceChildren(source, ...[{ value: '', label: 'Configuration courante' }, ...updated.settings.sources.map(item => ({ value: item.id, label: item.name }))]
      .map(item => el('option', { value: item.value, text: item.label })));
    source.value = updated.settings.activeSourceId;
  });
  signal.addEventListener('abort', stopSettings, { once: true });
  const connectionStatus = el('p', { class: 'help', role: 'status', 'aria-live': 'polite' });
  const live = card(simulation ? 'Collecter dans la simulation' : 'GLPI en direct', field('Recherche mémorisée', source), connection.element,
    toolbar(button(simulation ? 'Collecter la simulation' : 'Lancer la collecte', async () => { await saveConnection(); await call('collect.run'); await navigate('pilot'); }, { primary: true, icon: 'download' }),
      button(simulation ? 'Connecter la source simulée' : 'Se connecter', async () => { await saveConnection(); const result = await call('collect.connect'); connectionStatus.textContent = result.status?.message || result.status?.detail || 'Session connectée.'; }, { icon: 'arrow' })),
    connectionStatus, el('p', { class: 'help data-connection-help', text: simulation ? 'Connexion et téléchargements restent simulés. Vos essais ne modifient pas le profil réel.' : 'La collecte reste discrète. Si le SSO demande une action, le navigateur vous permet de terminer la connexion.' }),
    toolbar(button('Mes recherches', manageSearches, { icon: 'layers', attributes: { 'data-open-panel': 'sources' } }),
      button('Collecter plusieurs recherches', () => openGroupedCollection(ctx, { beforeOpen: prepareConnection, onSaved: savedSettings, manageSources: () => openTool('sources') }), { icon: 'layers' }),
      button('Inspection et reprise', () => openTool('collect'), { attributes: { 'data-open-panel': 'collect' } })));
  live.classList.add('workspace-live-source'); live.dataset.sourceMethod = simulation ? 'simulation' : 'glpi';
  const alternatives = card('Autres façons de travailler',
    el('div', { class: 'data-alternative', 'data-source-method': 'file' }, el('h3', { text: 'Un extrait CSV / Excel' }),
      el('p', { class: 'help', text: 'Chargez un fichier lorsque vous disposez déjà d’un extrait. Les indicateurs décriront son contenu, sans actualisation GLPI.' }),
      button('Importer CSV / Excel', () => importLocalTestFile(ctx), { icon: 'folder' })),
    el('p', { class: 'help', text: simulation ? 'Quitter la simulation restaure votre espace habituel.' : 'Votre dernier relevé réussi est aussi conservé sur ce poste pour une consultation hors connexion.' }));
  alternatives.classList.add('workspace-source-alternatives');
  grid.append(live, alternatives);
  root.append(current);
  const sourceCache = await sourceCacheCard(ctx); if (signal.aborted) return () => panels.dispose();
  if (sourceCache) root.append(sourceCache);
  if(isFileSource(state.session))root.append(card('Source CSV / Excel',
    el('p',{text:fileSourceDetailHelp}),el('p',{class:'help',text:'Listes, filtres, indicateurs, cartes et interventions restent disponibles. La collecte programmée est suspendue tant que ce fichier est actif.'}),
    button(state.session.fileCanRestore?'Revenir à la source précédente':'Fermer cette source',async()=>{await api.call('records.file.close');await navigate('data');},{icon:'close'})));
  const metrics = el('div', { class: 'workspace-data-metrics' }); root.append(metrics);
  const numberCard = (value, label, detail, panel) => {
    const node = el('div', { class: 'metric' }, el('div', { class: 'metric-label', text: label }),
      el('div', { class: 'metric-value', text: value == null ? '—' : formatNumber(value) }), el('small', { text: detail }));
    if (panel) node.append(button('Examiner', () => openTool(panel), { attributes: { 'data-open-panel': panel } })); return node;
  };
  metrics.append(numberCard(state.session.loaded ? state.session.rows : null, 'Lignes chargées', 'Périmètre réellement disponible'),
    numberCard(state.session.loaded ? state.session.columns : null, 'Colonnes source', 'Les valeurs brutes restent conservées', 'mapping'));
  const modelResults = await Promise.allSettled([state.session.loaded ? call('records.quality') : Promise.resolve(null), call('library.get')]);
  if (signal.aborted) return () => panels.dispose();
  const quality = modelResults[0].status === 'fulfilled' ? modelResults[0].value : null;
  const library = modelResults[1].status === 'fulfilled' ? modelResults[1].value : null;
  metrics.append(numberCard(quality?.issueCount, 'Points à examiner', quality ? 'Contrôles de couverture et de lecture' : 'Chargez une liste pour lancer les contrôles', 'quality'),
    numberCard(library?.stats?.ticketCount, 'Tickets conservés', 'Mémoire locale consultable', 'library'));
  for (const result of modelResults) if (result.status === 'rejected') root.append(el('p', { class: 'notice warning', text: result.reason.message || 'Un état local n’a pas pu être chargé.' }));
  if (receipt.attention && state.session.loaded) root.append(el('p', { class: 'notice warning', text: receipt.attention }));
  root.append(card('Vérifier et enrichir les données', el('p', { text: 'Reliez les colonnes aux champs métier et vérifiez les valeurs interprétées avant d’exploiter les indicateurs.' }),
    el('div', { class: 'workspace-settings-tools' }, toolButton('Correspondances', 'Colonnes, statuts, dates et valeurs multiples', 'mapping', 'layers'),
      toolButton('Qualité et couverture', 'Lignes reçues, champs manquants et valeurs à vérifier', 'quality', 'shield'),
      toolButton('Sites et détection', 'Noms, alias, communes et coordonnées', 'sites', 'map'),
      navigationVisible('enrichment', state.settings.interface) ? toolButton('Lectures ciblées', 'Compléter explicitement une sélection de fiches', 'enrichment', 'search') : null)));
  const libraryTools = el('section', { class: 'card workspace-library-tools' }, el('div', { class: 'pilot-block-heading' }, el('div', {},
    el('p', { class: 'workspace-eyebrow', text: 'MÉMOIRE DES TICKETS' }), el('h2', { text: 'Retrouver, comprendre et partager' }),
    el('p', { class: 'help', text: 'Les contenus conservés restent consultables, y compris lorsque le ticket sort du relevé courant.' })),
    button(simulation ? 'Actualiser le cache simulé' : 'Actualiser le cache', async () => { const captureResult = await call('library.capture', { mode: 'incremental' }); await openTool('library', { captureResult }); }, { icon: 'refresh', disabled:isFileSource(state.session)||state.session.cachedSource, title:state.session.cachedSource?'Une nouvelle collecte GLPI est nécessaire pour relire les fiches en ligne.':isFileSource(state.session)?fileSourceDetailHelp:'' })),
    el('div', { class: 'workspace-section-grid' }, toolEntry('Chercher une fiche', 'Explorer les tickets et leurs contenus déjà conservés.', 'library', 'folder'),
      toolEntry('Collecte et reprise', 'Rythme, pause, reprise et transfert de la bibliothèque.', 'collection', 'download'),
      toolEntry('Retrouver une solution', 'Rechercher parmi les résolutions avec leur ticket source.', 'resolution', 'search'),
      toolEntry('Échanges de l’équipe', 'Contributions, partages et conflits à examiner.', 'team', 'users'),
      toolEntry('Relevés partagés', 'Retrouver la liste exacte de tickets recueillie sur un autre poste.', 'shared-records', 'layers'),
      toolEntry('Partage entre postes', 'Dossier réseau facultatif ou PostgreSQL.', 'connection', 'settings')));
  root.append(libraryTools); root.dataset.ready = 'true';

  function toolButton(title, subtitle, panel, symbol) {
    const unavailable=(isFileSource(state.session)||state.session.cachedSource)&&panel==='enrichment';
    const control = button('', () => openTool(panel), { disabled:unavailable,title:unavailable?fileSourceDetailHelp:'',attributes: { 'data-open-panel': panel } });
    replaceChildren(control, icon(symbol), el('span', {}, el('strong', { text: title }), el('small', { text: subtitle }))); return control;
  }
  function toolEntry(title, subtitle, panel, symbol) {
    return el('article', { class: 'workspace-entry' }, icon(symbol), el('div', {}, el('h3', { text: title }), el('p', { class: 'help', text: subtitle }),
      button('Ouvrir', () => openTool(panel), { attributes: { 'data-open-panel': panel } })));
  }
  async function openTool(name, extra = {}) {
    if (name === 'sites') return navigate('terrain-map', { panel: 'reference' });
    if (name === 'enrichment' && !navigationVisible('enrichment', state.settings.interface)) return;
    const definitions = {
      quality: ['Vérifier la qualité des données', renderQuality], mapping: ['Relier les colonnes aux champs métier', renderMapping],
      sources: ['Recherches GLPI mémorisées', renderSources], collect: ['Inspecter et reprendre la collecte', inspectCollection],
      collection: ['Collecte des tickets', renderLibraryCollection],
      library: ['Rechercher une fiche conservée', ctx.params?.view === 'collection' ? renderLibraryCollection : renderLibrary], resolution: ['Retrouver une résolution', renderLibrary],
      team: ['Échanges et contributions', renderLibrary], connection: ['Configurer le partage entre postes', renderLibrary],
      'shared-records': ['Relevés disponibles dans l’espace commun', renderSharedRecords],
      enrichment: ['Compléter les fiches sélectionnées', renderEnrichment]
    };
    const definition = definitions[name]; if (!definition) return;
    return panels.open(definition[0], definition[1], { ...ctx.params, ...extra, panel: name, embedded: true,
      view: ['resolution', 'team', 'connection'].includes(name) ? name : extra.view || ctx.params?.view || 'library' });
  }
  async function inspectCollection(local) {
    const host = local.root, tabs = el('div'), tables = el('div'); let selected = null;
    const displayTables = rows => {
      replaceChildren(tables, table(rows, [{ key: 'label', label: 'Tableau' }, { key: 'rowCount', label: 'Lignes' }, { key: 'columnCount', label: 'Colonnes' },
        { label: 'Choisir', render: item => button('Utiliser ce tableau', async () => { selected = await local.api.call('collect.table', { index: item.index, revision: item.revision }); notify('Tableau sélectionné.'); }, { primary: true }) }]),
        toolbar(button('Extraire ce tableau et les pages suivantes', async () => { if (!selected) throw new Error('Choisissez un tableau.'); await local.api.call('collect.manual', { revision: selected.revision, followingPages: true }); await local.navigate('records'); }, { primary: true }),
          button('Extraire cette page', async () => { if (!selected) throw new Error('Choisissez un tableau.'); await local.api.call('collect.manual', { revision: selected.revision, followingPages: false }); await local.navigate('records'); })));
    };
    async function loadTabs() {
      const result = await local.api.call('collect.pages', {}, { signal: local.signal });
      replaceChildren(tabs, table(result.pages || [], [{ key: 'title', label: 'Onglet GLPI' }, { key: 'url', label: 'Adresse' },
        { label: 'Inspecter', render: item => button('Détecter les tableaux', async () => { const result = await local.api.call('collect.inspect', { page: item.index, revision: item.revision }); selected = null; displayTables(result); }) } ]));
    }
    host.append(el('p', { class: 'help', text: 'Utilisez ces commandes pour une liste particulière ou pour reprendre une collecte qui demande une sélection manuelle.' }),
      toolbar(button('Actualiser les onglets', loadTabs, { icon: 'refresh' }), button(simulation ? 'Afficher la source simulée' : 'Afficher le navigateur', () => local.api.call('collect.show')),
        button('Lire un téléchargement GLPI', async () => { await local.api.call('collect.captureDownload'); await local.navigate('records'); })), tabs, tables,
      el('details', {}, el('summary', { text: 'Repères de cette installation' }), toolbar(...[['ready', 'Liste prête'], ['next', 'Page suivante'], ['table', 'Tableau']].map(([kind, label]) =>
        button(label, async () => { const result = await local.api.call('collect.pick', { kind }); notify(result.cancelled ? 'Repérage annulé.' : 'Repère enregistré.'); })))));
    await loadTabs();
  }
  if (ctx.params?.panel) await openTool(ctx.params.panel);
  return async () => { stopSettings(); await panels.dispose(); root.classList.remove('data-workspace'); };
}

export const pages = [{ id: 'data', title: 'Données', icon: 'layers', group: 'Espaces', render: renderDataWorkspace }];
