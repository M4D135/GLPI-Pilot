import { replaceChildren, el, button, card, toolbar, empty, badge, form, table, dialog, input, select, field, formatNumber, formatDate, icon, extractionStatus, isSimulation } from '../ui.js';
import { importLocalTestFile } from '../local-file-test.js';
import { navigationVisible } from '../navigation.js';
import { createWorkspacePanels } from '../workspace-panels.js';
import { renderSources } from './foundation.js';
import { renderQuality, renderMapping } from './records.js';
import { renderLibrary } from './library.js';
import { renderEnrichment } from './workplace-day.js';

export async function renderDataWorkspace(ctx) {
  const { root, api, signal, state, notify, navigate } = ctx;
  const panels = createWorkspacePanels(ctx), simulation = isSimulation(state.session);
  const call = (operation, args = {}) => api.call(operation, args, { signal });
  root.classList.add('data-workspace'); root.dataset.workspace = 'data';
  let revision = state.session.revision;
  const selectedSource = state.settings.sources.find(item => item.id === state.settings.activeSourceId);
  const receipt = extractionStatus(state.session);
  const hero = el('section', { class: 'hero workspace-intro' }, el('div', {},
    el('p', { class: 'workspace-eyebrow', text: 'DONNÉES ET CONNEXIONS' }), el('h2', { text: 'Une source claire. Des chiffres fiables.' }),
    el('p', { text: 'Chargez votre extrait, connectez une recherche GLPI et vérifiez les champs reconnus. Les six espaces utilisent ensuite les mêmes données.' }),
    toolbar(button('Importer CSV / Excel', () => importLocalTestFile(ctx), { primary: true, icon: 'folder' }),
      button(simulation ? 'Changer le scénario' : 'Essayer la simulation', async () => { await call('app.demo'); await navigate('home'); }, { icon: 'spark' }))),
    el('div', { class: 'hero-icon', 'aria-hidden': 'true' }, icon('layers')));
  const current = el('section', { class: 'workspace-data-source' }, el('div', {},
    el('p', { class: 'workspace-eyebrow', text: simulation ? 'SOURCE DE SIMULATION' : 'SOURCE ACTIVE' }),
    el('h3', { text: state.session.loaded ? state.session.title || selectedSource?.name || 'Relevé chargé' : selectedSource?.name || 'Aucune liste chargée' }),
    el('p', { class: 'help', text: state.session.loaded ? `${formatDate(state.session.date)} · ${receipt.receipt}` : 'Importez un fichier ou lancez une collecte ci-dessous.' })),
    toolbar(state.session.loaded ? button('Voir les tickets', () => navigate('records'), { icon: 'ticket' }) : null,
      state.session.loaded ? button('Lire le pilotage', () => navigate('pilot'), { icon: 'chart' }) : null));
  root.append(hero, current);
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
  const grid = el('div', { class: 'workspace-section-grid' });
  root.append(grid);
  const source = select([{ value: '', label: 'Configuration courante' }, ...state.settings.sources.map(item => ({ value: item.id, label: item.name }))], state.settings.activeSourceId);
  source.addEventListener('change', async () => { if (source.value) { await call('sources.select', { id: source.value, revision }); await ctx.refresh(); } });
  const connection = form([
    { key: 'portalUrl', label: simulation ? 'Adresse de la source simulée' : 'Adresse de la recherche GLPI', required: true, help: 'L’adresse complète conserve les filtres de la recherche.' },
    { key: 'browserChannel', label: 'Navigateur', type: 'select', options: [{ value: 'msedge', label: 'Microsoft Edge' }, { value: 'chrome', label: 'Google Chrome' }] },
    { key: 'autoMode', label: 'Collecte', type: 'select', options: [{ value: 'native', label: 'Export natif GLPI' }, { value: 'pagination', label: 'Tableau et pagination' }] }
  ], { ...state.settings, portalUrl: simulation ? state.settings.portalUrl || 'https://simulation.glpi.local/front/ticket.php' : state.settings.portalUrl });
  const saveConnection = async () => { const result = await call('settings.save', { revision, settings: { ...state.settings, ...connection.read() } }); revision = result.revision; };
  const connectionStatus = el('p', { class: 'help', role: 'status', 'aria-live': 'polite' });
  grid.append(card('Actualiser depuis GLPI', field('Recherche mémorisée', source), connection.element,
    toolbar(button(simulation ? 'Collecter la simulation' : 'Lancer la collecte', async () => { await saveConnection(); await call('collect.run'); await navigate('pilot'); }, { primary: true, icon: 'download' }),
      button(simulation ? 'Connecter la source simulée' : 'Se connecter', async () => { await saveConnection(); const result = await call('collect.connect'); connectionStatus.textContent = result.status?.message || result.status?.detail || 'Session connectée.'; }, { icon: 'arrow' })),
    connectionStatus, el('p', { class: 'help', text: 'La collecte reste discrète. Une authentification à terminer peut demander une intervention dans le navigateur.' }),
    toolbar(button('Mes recherches', () => openTool('sources'), { icon: 'layers', attributes: { 'data-open-panel': 'sources' } }),
      button('Inspection et reprise', () => openTool('collect'), { attributes: { 'data-open-panel': 'collect' } }),
      button('Lancer la campagne', async () => { await saveConnection(); const result = await call('collect.campaign'); dialog('Résultat de la campagne', table(result.sources, [{ key: 'name', label: 'Source' }, { key: 'state', label: 'État' }, { key: 'rows', label: 'Lignes' }, { key: 'detail', label: 'Résultat' }])); }))));
  grid.append(card('Vérifier et enrichir les données', el('p', { text: 'Reliez les colonnes du fichier aux champs métier et vérifiez les valeurs interprétées avant d’exploiter les indicateurs.' }),
    el('div', { class: 'workspace-settings-tools' }, toolButton('Correspondances', 'Colonnes, statuts, dates et valeurs multiples', 'mapping', 'layers'),
      toolButton('Qualité et couverture', 'Lignes reçues, champs manquants et valeurs à vérifier', 'quality', 'shield'),
      toolButton('Sites et détection', 'Noms, alias, communes et coordonnées', 'sites', 'map'),
      navigationVisible('enrichment', state.settings.interface) ? toolButton('Lectures ciblées', 'Compléter explicitement une sélection de fiches', 'enrichment', 'search') : null)));
  const libraryTools = el('section', { class: 'card workspace-library-tools' }, el('div', { class: 'pilot-block-heading' }, el('div', {},
    el('p', { class: 'workspace-eyebrow', text: 'MÉMOIRE DES TICKETS' }), el('h2', { text: 'Retrouver, comprendre et partager' }),
    el('p', { class: 'help', text: 'Les contenus conservés restent consultables, y compris lorsque le ticket sort du relevé courant.' })),
    button(simulation ? 'Actualiser le cache simulé' : 'Actualiser le cache', async () => { const captureResult = await call('library.capture', { mode: 'incremental' }); await openTool('library', { captureResult }); }, { icon: 'refresh' })),
    el('div', { class: 'workspace-section-grid' }, toolEntry('Chercher une fiche', 'Explorer les tickets et leurs contenus déjà conservés.', 'library', 'folder'),
      toolEntry('Retrouver une solution', 'Rechercher parmi les résolutions avec leur ticket source.', 'resolution', 'search'),
      toolEntry('Échanges de l’équipe', 'Contributions, partages et conflits à examiner.', 'team', 'users'),
      toolEntry('Connexion et synchronisation', 'Emplacement local et central facultatif.', 'connection', 'settings')));
  root.append(libraryTools); root.dataset.ready = 'true';

  function toolButton(title, subtitle, panel, symbol) {
    const control = button('', () => openTool(panel), { attributes: { 'data-open-panel': panel } });
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
      library: ['Rechercher une fiche conservée', renderLibrary], resolution: ['Retrouver une résolution', renderLibrary],
      team: ['Échanges et contributions', renderLibrary], connection: ['Configurer la mémoire partagée', renderLibrary],
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
        { label: 'Inspecter', render: item => button('Détecter les tableaux', async () => { const result = await local.api.call('collect.inspect', { page: item.index, revision: item.revision }); selected = null; displayTables(result); }) }]));
    }
    host.append(el('p', { class: 'help', text: 'Utilisez ces commandes pour une liste particulière ou pour reprendre une collecte qui demande une sélection manuelle.' }),
      toolbar(button('Actualiser les onglets', loadTabs, { icon: 'refresh' }), button(simulation ? 'Afficher la source simulée' : 'Afficher le navigateur', () => local.api.call('collect.show')),
        button('Lire un téléchargement GLPI', async () => { await local.api.call('collect.captureDownload'); await local.navigate('records'); })), tabs, tables,
      el('details', {}, el('summary', { text: 'Repères de cette installation' }), toolbar(...[['ready', 'Liste prête'], ['next', 'Page suivante'], ['table', 'Tableau']].map(([kind, label]) =>
        button(label, async () => { const result = await local.api.call('collect.pick', { kind }); notify(result.cancelled ? 'Repérage annulé.' : 'Repère enregistré.'); })))));
    await loadTabs();
  }
  if (ctx.params?.panel) await openTool(ctx.params.panel);
  return async () => { await panels.dispose(); root.classList.remove('data-workspace'); };
}

export const pages = [{ id: 'data', title: 'Données', icon: 'layers', group: 'Espaces', render: renderDataWorkspace }];
