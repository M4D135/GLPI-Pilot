import { replaceChildren, el, button, card, toolbar, badge, form, dialog, select, field, icon, confirm, formatDate, formatNumber } from '../ui.js';
import { createWorkspacePanels } from '../workspace-panels.js';
import { openNavigationEditor, navigationVisible } from '../navigation.js';
import { renderSettingsEditor, renderHelp } from './settings.js';
import { renderBusinessSettings } from './analytics-business.js';
import { renderOperations } from './foundation.js';
import { renderUpdates } from './terrain-update.js';
import { pages as automationTools } from './automation.js';
import { createMotionPreview } from '../motion-controls.js';
import { createDeploymentConfiguration } from './settings-deployment.js';
import { themeOptions, themePreference, themeChange } from '../theme.js';

// Réglages follows the Paramètres screen of Domenia 2 (v2/web/js/pages/parametres.js): a hero with the state of the
// installation and a search of the settings, a sliding section rail and one section at a time. Every section is built
// once and kept while the screen is open: changing section never loses a value typed and not yet applied, and the
// guards of the shell (identity change, configuration review) still see every open form.
const fold = text => String(text ?? '').normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase('fr');

const SECTIONS = [
  { id: 'apparence', label: 'Apparence', desc: 'Thème, zoom, densité, menu', icon: 'monitor', tone: 'accent',
    lead: 'Le confort de lecture de ce profil : thème, animations, zoom, densité, couleurs, ordre du menu et fonctions visibles.' },
  { id: 'pilotage', label: 'Pilotage', desc: 'Seuils, météo, sites, alertes', icon: 'sun', tone: 'warn',
    lead: 'Les règles qui donnent leur sens aux indicateurs : seuils, équipes, météo du service, référentiel des sites et alertes.' },
  { id: 'collecte', label: 'Collecte', desc: 'Délais, limites, exports, programmation', icon: 'download', tone: 'info',
    lead: 'Les délais et les limites d’une collecte GLPI, les exports, les repères de votre installation et les collectes programmées.' },
  { id: 'tickets', label: 'Tickets', desc: 'Sections lues, repères, limites', icon: 'ticket', tone: 'info',
    lead: 'Ce que GLPI Pilot lit dans la fiche d’un ticket : sections, repères de contenu, colonne de l’identifiant et adresse de la fiche.' },
  { id: 'historique', label: 'Historique', desc: 'Archives, durée, budget', icon: 'clock', tone: 'ok',
    lead: 'Les archives des collectes conservées sur ce poste : activation, durée, nombre et budget de stockage.' },
  { id: 'equipe', label: 'Équipe et partage', desc: 'Partager, fusionner, reprendre', icon: 'users', tone: 'accent',
    lead: 'Partager certaines sections avec l’équipe, examiner un import avant de l’appliquer, reprendre une version locale ou régler le partage entre postes.' },
  { id: 'deploiement', label: 'Configuration à diffuser', desc: 'Défaut du pack, export, import', icon: 'folder', tone: 'accent',
    lead: 'La configuration complète du logiciel : celle que reçoit un profil neuf au premier lancement, son export et l’examen d’un fichier.' },
  { id: 'entretien', label: 'Mises à jour et journal', desc: 'Pack, diagnostic, journal', icon: 'refresh', tone: 'info',
    lead: 'Vérifier et installer un pack, lire le journal des opérations et préparer un rapport de diagnostic.' },
  { id: 'demonstration', label: 'Démonstration', desc: 'Tickets fictifs, espace séparé', icon: 'spark', tone: 'accent',
    lead: 'Un espace séparé avec 60 tickets fictifs pour découvrir GLPI Pilot sans toucher à vos données ni à vos réglages.' },
  { id: 'apropos', label: 'À propos et aide', desc: 'Version, guide, raccourcis', icon: 'help', tone: 'info',
    lead: 'La version installée, la prise en main et les raccourcis clavier.' }
];
const sectionIds = new Set(SECTIONS.map(section => section.id));
// Older names and the contextual panels lead to the section that holds them.
const ALIASES = { demo: 'demonstration', deployment: 'deploiement', about: 'apropos', appearance: 'apparence', storage: 'historique',
  history: 'historique', team: 'equipe', profile: 'equipe', acquisition: 'collecte', lecture: 'tickets' };
const PANEL_SECTION = { 'pilot-settings': 'pilotage', sites: 'pilotage', 'alert-rules': 'pilotage', alerts: 'pilotage',
  acquisition: 'collecte', automation: 'collecte', tickets: 'tickets', storage: 'historique', profile: 'equipe', connection: 'equipe',
  updates: 'entretien', operations: 'entretien', help: 'apropos' };
// Cards of the settings editor shown inline, by title: section and search target.
const EDITOR_CARDS = { 'Collecte et exports': ['collecte', 'acquisition'], 'Adaptation à votre installation': ['collecte', 'selectors'],
  'Lecture des tickets': ['tickets', 'ticket-reading'], 'Historique local': ['historique', 'history'], 'Configuration d’équipe': ['equipe', 'team'] };
// The panels that are now a section of their own: opening them shows the section.
const INLINE_PANELS = { acquisition: 'acquisition', storage: 'history', tickets: 'ticket-reading', profile: 'team' };

// Settings index of the search box: label, words, section and the element to light up (data-setting, field name or panel).
const INDEX = [
  ['Thème clair, sombre ou contraste élevé', 'theme couleurs sombre clair contraste windows', 'apparence', 'theme'],
  ['Couleur d’accent', 'accent couleur bleu violet vert ambre rose', 'apparence', 'accent'],
  ['Animations expressives, réduites ou de Windows', 'animations mouvement reduire meteo reflets', 'apparence', 'motionMode'],
  ['Densité des listes', 'densite compacte equilibree confortable lignes', 'apparence', 'density'],
  ['Zoom de l’interface', 'zoom taille agrandir reduire ctrl', 'apparence', 'interfaceZoomPercent'],
  ['Perception des couleurs', 'daltonisme deuteranopie protanopie tritanopie couleurs vision', 'apparence', 'colorVision'],
  ['Contrastes renforcés', 'contraste bordures lisibilite', 'apparence', 'highContrast'],
  ['Mode concentration', 'concentration f11 plein ecran', 'apparence', 'focusMode'],
  ['Colonnes figées du relevé', 'colonnes figees source tableau releve', 'apparence', 'frozenColumns'],
  ['Organiser le menu', 'menu navigation ordre favoris masquer espaces', 'apparence', 'menu'],
  ['Écran de la fenêtre', 'ecran moniteur fenetre deplacer', 'apparence', 'screen'],
  ['Fonctions facultatives', 'assistant langage lectures ciblees effectifs sites fonctions visibles', 'apparence', 'optional'],
  ['Seuils, équipes et règles météo', 'seuils equipes meteo regles pilotage indicateurs actualite service', 'pilotage', 'pilot-settings'],
  ['Sites et communes', 'sites communes referentiel carte detection lieux', 'pilotage', 'sites'],
  ['Règles d’alerte', 'alertes surveillance conditions prise en compte', 'pilotage', 'alert-rules'],
  ['Délais de connexion et d’export', 'attente delai sso connexion export liste fiche secondes reconnexion', 'collecte', 'timeoutSeconds'],
  ['Pages et lignes maximales', 'pages lignes maximales limites total attendu', 'collecte', 'maxRows'],
  ['Reprise de pagination', 'reprise pagination validite minutes', 'collecte', 'resumePagination'],
  ['Espace disque minimal', 'espace disque mio libre', 'collecte', 'minimumFreeSpaceMb'],
  ['Classeur Excel après la collecte', 'excel xlsx classeur enregistrer', 'collecte', 'autoSaveXlsx'],
  ['Dossier des exports', 'dossier exports emplacement', 'collecte', 'export-folder'],
  ['Repères avancés de l’installation GLPI', 'reperes selecteurs cadre tableau page suivante export natif', 'collecte', 'selectors'],
  ['Collectes programmées', 'programmer calendrier automatique recette sante sources', 'collecte', 'automation'],
  ['Sections lues dans un ticket', 'sections fiche ticket suivis taches solutions traitement lecture', 'tickets', 'ticket-reading'],
  ['Modèle d’adresse de fiche', 'adresse url modele fiche identifiant colonne', 'tickets', 'urlTemplate'],
  ['Archiver les collectes', 'archives historique archiver', 'historique', 'enabled'],
  ['Conservation des archives', 'conservation jours nombre budget stockage mio', 'historique', 'retentionDays'],
  ['Rouvrir la dernière archive au démarrage', 'demarrage rouvrir derniere archive', 'historique', 'restoreLastOnStartup'],
  ['Exporter ou importer une configuration d’équipe', 'equipe exporter importer fusionner conflits versions locales', 'equipe', 'team'],
  ['Partage entre postes', 'partage dossier reseau postgresql postes planning donnees communes', 'equipe', 'connection'],
  ['Définir comme configuration par défaut', 'defaut pack wad premier lancement diffuser deploiement', 'deploiement', 'deployment'],
  ['Exporter ou examiner la configuration complète', 'configuration complete exporter examiner importer remplacer sauvegarde', 'deploiement', 'deployment'],
  ['Mettre à jour GLPI Pilot', 'mise a jour pack zip dossier partage version installer', 'entretien', 'updates'],
  ['Journal et diagnostic', 'journal diagnostic rapport erreurs operations', 'entretien', 'operations'],
  ['Ouvrir la démonstration', 'demonstration tickets fictifs essayer decouvrir simulation', 'demonstration', 'demo'],
  ['Version de GLPI Pilot', 'version a propos windows webview2 c#', 'apropos', 'about'],
  ['Guide et raccourcis', 'guide aide raccourcis clavier prise en main', 'apropos', 'help']
].map(([label, words, section, target]) => ({ label, section, target, words: fold(`${label} ${words}`) }));
const MOTION_LABELS = { full: 'animations expressives', system: 'animations de Windows', reduce: 'animations réduites' };

// The section shown when this screen is rebuilt in place by Actualiser; a plain visit opens Apparence.
let kept = null;
const sectionOf = name => sectionIds.has(name) ? name : ALIASES[name] ?? null;

export async function renderSettingsWorkspace(ctx) {
  const { root, api, state, signal, navigate, notify } = ctx;
  const panels = createWorkspacePanels(ctx);
  const settings = await api.call('settings.get', {}, { signal });
  if (signal.aborted) return () => panels.dispose();
  root.classList.add('settings-workspace'); root.dataset.workspace = 'settings';
  const params = ctx.params ?? {};
  const keptSection = kept && performance.now() - kept.at < 10000 ? kept.section : null; kept = null;
  let current = null, deploymentState = null;

  // ----- Hero: title, chips, search of a setting and the state of the installation -----
  const search = el('input', { type: 'search', class: 'cfg-search-input', id: 'cfg-search', placeholder: 'Rechercher un réglage…',
    autocomplete: 'off', spellcheck: false, role: 'combobox', 'aria-expanded': 'false', 'aria-controls': 'cfg-search-list',
    'aria-autocomplete': 'list', 'aria-label': 'Rechercher un réglage', 'aria-keyshortcuts': '/' });
  const list = el('ul', { class: 'cfg-search-list', id: 'cfg-search-list', role: 'listbox', 'aria-label': 'Réglages trouvés', hidden: true });
  const tiles = new Map();
  const tile = (id, label, symbol, section) => {
    const node = el('button', { type: 'button', class: 'cfg-pulse-tile', dataset: { pulse: id, goSection: section },
      onClick: () => show(section, { focus: true }) },
    el('span', { class: 'cfg-pulse-ico' }, icon(symbol)), el('span', { class: 'cfg-pulse-k', text: label }),
    el('span', { class: 'cfg-pulse-v', text: '—' }), el('span', { class: 'cfg-pulse-s' }));
    tiles.set(id, node); return node;
  };
  const session = state.session || {};
  const mode = session.demo || session.demoSimulation ? ['Démonstration', 'warning'] : session.testMode ? ['Simulation', 'info'] : ['Espace habituel', 'success'];
  root.append(el('div', { class: 'cfg-page' },
    el('section', { class: 'hero workspace-intro cfg-hero' },
      el('div', { class: 'cfg-hero-main' },
        el('p', { class: 'workspace-eyebrow', text: 'VOTRE INSTALLATION' }), el('h2', { text: 'Un outil à votre manière.' }),
        el('p', { text: 'Apparence, pilotage, collecte, lecture des tickets, historique et configuration à diffuser : les réglages de ce profil, une section à la fois.' }),
        el('div', { class: 'workspace-intro-meta cfg-chips' }, badge(mode[0], mode[1]), badge(`GLPI Pilot ${state.version}`, 'info'),
          el('span', { text: 'Préférences du profil utilisateur' })),
        el('div', { class: 'cfg-search', role: 'search' },
          el('span', { class: 'cfg-search-box' }, icon('search'), search, el('kbd', { text: '/' })), list)),
      el('div', { class: 'cfg-pulse', role: 'group', 'aria-label': 'État de l’installation' },
        tile('appearance', 'Apparence', 'monitor', 'apparence'), tile('records', 'Relevé ouvert', 'table', 'collecte'),
        tile('history', 'Historique local', 'clock', 'historique'), tile('pack', 'Configuration du pack', 'folder', 'deploiement'))),
    el('div', { class: 'cfg-layout' })));
  const layout = root.querySelector('.cfg-layout');

  // ----- Section rail and panes -----
  const pill = el('span', { class: 'cfg-rail-pill', 'aria-hidden': 'true' });
  const railItems = SECTIONS.map((section, index) => {
    const item = el('button', { type: 'button', class: 'cfg-rail-item', 'aria-current': 'false',
      dataset: { settingsNav: section.id }, onClick: () => show(section.id) },
    el('span', { class: 'cfg-rail-ico', dataset: { tone: section.tone } }, icon(section.icon)),
    el('span', { class: 'cfg-rail-text' }, el('span', { class: 'cfg-rail-label', text: section.label }), el('span', { class: 'cfg-rail-desc', text: section.desc })),
    el('span', { class: 'cfg-rail-badge', dataset: { badge: section.id }, hidden: true }));
    item.style.setProperty('--j', String(index)); return item;
  });
  const rail = el('nav', { class: 'cfg-rail', 'aria-label': 'Sections des réglages' }, pill, el('ul', {}, ...railItems.map(item => el('li', {}, item))));
  const live = el('p', { class: 'cfg-live', role: 'status', 'aria-live': 'polite' });
  const content = el('div', { class: 'cfg-content', tabIndex: -1 }, live);
  const panes = new Map(SECTIONS.map(section => {
    const grid = el('div', { class: 'cfg-grid' });
    const element = el('section', { class: 'cfg-pane', hidden: true, 'aria-labelledby': `cfg-title-${section.id}`, dataset: { settingsPane: section.id } },
      el('header', { class: 'cfg-pane-head' }, el('span', { class: 'cfg-pane-icon', dataset: { tone: section.tone } }, icon(section.icon)),
        el('div', { class: 'cfg-pane-titles' }, el('h2', { id: `cfg-title-${section.id}`, text: section.label }), el('p', { text: section.lead }))),
      grid);
    content.append(element);
    return [section.id, { element, grid }];
  }));
  layout.append(rail, content);
  // Remembers the editor card an action started from, so that its save rebuilds that card.
  const slotOf = event => { lastSlot = event.target instanceof Element ? event.target.closest('.cfg-slot') : null; };
  content.addEventListener('pointerdown', slotOf, true);
  content.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') slotOf(event); }, true);
  const into = (id, ...cards) => { for (const item of cards) if (item) panes.get(id).grid.append(item); };
  const mark = (node, setting, ...classes) => { node.dataset.setting = setting; node.classList.add(...classes); return node; };

  // Apparence: comfort, presentation, menu and optional functions.
  const appearance = form([
    { key: 'theme', label: 'Thème', type: 'select', options: themeOptions },
    { key: 'accent', label: 'Accent', type: 'select', options: [{ value: 'blue', label: 'Bleu' }, { value: 'violet', label: 'Violet' }, { value: 'green', label: 'Vert' }, { value: 'orange', label: 'Ambre' }, { value: 'rose', label: 'Rose' }] },
    { key: 'motionMode', label: 'Animations', type: 'select', options: [{ value: 'full', label: 'Expressives' }, { value: 'system', label: 'Suivre Windows' }, { value: 'reduce', label: 'Réduites' }], help: 'Expressives active le mouvement de Domenia. Suivre Windows peut le mettre en pause. Le choix prend effet avec Appliquer.' },
    { key: 'density', label: 'Densité', type: 'select', options: [{ value: 'compact', label: 'Compacte' }, { value: 'balanced', label: 'Équilibrée' }, { value: 'comfortable', label: 'Confortable' }] },
    { key: 'interfaceZoomPercent', label: 'Zoom (%)', type: 'number', min: 75, max: 150 },
    { key: 'colorVision', label: 'Perception des couleurs', type: 'select', options: [{ value: 'standard', label: 'Standard' }, { value: 'deuteranopia', label: 'Deutéranopie' }, { value: 'protanopia', label: 'Protanopie' }, { value: 'tritanopia', label: 'Tritanopie' }] },
    { key: 'highContrast', label: 'Contrastes renforcés', type: 'checkbox', help: 'Bordures et textes renforcés dans les thèmes clair et sombre.' },
    { key: 'focusMode', label: 'Mode concentration', type: 'checkbox' },
    { key: 'frozenColumns', label: 'Colonnes source figées', type: 'number', min: 0, max: 8 }
  ], { ...settings.interface, theme: themePreference(settings), interfaceZoomPercent: settings.interfaceZoomPercent,
    motionMode: settings.interface.reduceMotion ? 'reduce' : settings.interface.followWindowsMotion === false ? 'full' : 'system' });
  const motionPreview = createMotionPreview(state);
  const screenButton = button('Choisir l’écran', async () => { const screens = await api.call('host.screens'); const choice = select(screens.map(screen => ({ value: screen.id, label: `${screen.primary ? 'Principal · ' : ''}${screen.width} × ${screen.height} · ${screen.id}` })), state.settings.interface.preferredMonitor);
    dialog('Déplacer la fenêtre', field('Écran', choice), [{ label: 'Déplacer', primary: true, action: async close => { await api.call('host.screen', { id: choice.value }); close(); } }]);
  }, { icon: 'monitor' });
  screenButton.dataset.setting = 'screen';
  const optional = form([
    { key: 'assistant', label: 'Assistant de langage', type: 'checkbox', help: 'À activer lorsqu’un service de langage est configuré.' },
    { key: 'enrichment', label: 'Lectures ciblées', type: 'checkbox', help: 'Compléter une sélection depuis Données.' },
    { key: 'populations', label: 'Effectifs des sites', type: 'checkbox', help: 'Utiliser les effectifs datés dans Terrain.' }
  ], Object.fromEntries(['assistant', 'enrichment', 'populations'].map(key => [key, navigationVisible(key, settings.interface)])));
  optional.element.dataset.setting = 'optional';
  const menuButton = button('Organiser le menu', () => openNavigationEditor({ pages: ctx.pages, preferences: state.settings.interface,
    save: async preferences => { await api.call('app.appearance', { preferences: { ...state.settings.interface, ...preferences } }); notify('Menu enregistré.'); } }), { icon: 'layers' });
  menuButton.dataset.setting = 'menu';
  into('apparence',
    mark(card('Confort et présentation', motionPreview.element, appearance.element,
      toolbar(button('Appliquer', async () => {
        const { theme, interfaceZoomPercent, motionMode, ...preferences } = appearance.read();
        await api.call('app.appearance', { ...themeChange(theme, { ...state.settings.interface, ...preferences,
          reduceMotion: motionMode === 'reduce', followWindowsMotion: motionMode === 'system' }), zoom: interfaceZoomPercent });
        appearance.markSaved();
        notify('Présentation enregistrée.');
      }, { primary: true, icon: 'check' }), screenButton)), 'presentation', 'cfg-main'),
    mark(card('Menu et fonctions', el('p', { class: 'help', text: 'Rangez les six espaces du menu, épinglez vos favoris et choisissez les fonctions facultatives affichées dans leur contexte.' }),
      toolbar(menuButton), optional.element,
      toolbar(button('Appliquer les fonctions visibles', async () => {
        const values = optional.read(), hiddenNavigation = (state.settings.interface.hiddenNavigation || []).filter(key => !Object.hasOwn(values, key));
        for (const [key, enabled] of Object.entries(values)) if (!enabled) hiddenNavigation.push(key);
        await api.call('app.appearance', { preferences: { ...state.settings.interface, hiddenNavigation } }); optional.markSaved(); notify('Fonctions contextuelles actualisées.');
      }, { primary: true }))), 'menu-card', 'cfg-side'));

  // Pilotage, collection schedule, sharing, maintenance and help: the existing contextual panels.
  into('pilotage', card('Règles du pilotage', el('div', { class: 'workspace-settings-tools cfg-tools' },
    tool('Seuils, équipes et règles météo', 'Valeurs de départ, correspondances et actualité du service', 'pilot-settings', 'sun'),
    tool('Sites et communes', 'Importer le référentiel et régler la détection', 'sites', 'map'),
    tool('Alertes', 'Conditions de surveillance et prise en compte', 'alert-rules', 'bell'))),
  card('Où ces règles s’appliquent', el('ul', { class: 'cfg-notes' },
    note('Les seuils, les équipes et la météo du service s’appliquent aux indicateurs de Pilotage.'),
    note('Le référentiel des sites place les tickets sur la carte des interventions.'),
    note('Les règles d’alerte se règlent dans Accueil, où les alertes s’affichent.'))));
  const editorHosts = new Map();
  let editorRevision = null, lastSlot = null;
  const dirty = host => [...host.querySelectorAll('.form-grid')].some(item => item.pilotHasUnsavedChanges?.());
  for (const [title, [section, setting]] of Object.entries(EDITOR_CARDS)) {
    const host = el('div', { class: 'cfg-slot', dataset: { setting, editorCard: title } });
    editorHosts.set(title, host); into(section, host);
  }
  into('collecte', card('Collectes programmées', el('p', { class: 'help', text: 'Planifiez les collectes, vérifiez leur recette et la santé des sources sans ouvrir GLPI.' }),
    el('div', { class: 'workspace-settings-tools cfg-tools' }, tool('Programmer les collectes', 'Calendrier, recette et santé des sources', 'automation', 'clock'))));
  into('equipe', card('Partage entre postes', el('p', { class: 'help', text: 'Un dossier réseau facultatif ou PostgreSQL partage le planning et les données communes de l’équipe. Le réglage se fait dans Données.' }),
    el('div', { class: 'workspace-settings-tools cfg-tools' }, tool('Partage entre postes', 'Dossier réseau facultatif ou PostgreSQL · planning et données communes', 'connection', 'users'))));
  into('entretien', card('Entretien', el('div', { class: 'workspace-settings-tools cfg-tools' },
    tool('Mettre à jour', 'Vérifier un pack et préparer son installation', 'updates', 'refresh'),
    tool('Journal et diagnostic', 'Résultats des opérations et rapport partageable', 'operations', 'log'))),
  card('Sources des mises à jour', el('ul', { class: 'cfg-notes' },
    note('Un pack vient d’un dossier réseau partagé ou d’un ZIP local ; GLPI Pilot ne contacte aucun site de publication.'),
    note('Chaque pack est contrôlé (SHA-256, version et contenu) et la version en place est sauvegardée avant l’installation.'))));
  const demonstration = demonstrationCard();
  into('demonstration', demonstration);
  into('apropos', mark(card('À propos', el('p', { text: `GLPI Pilot ${state.version} · Windows x64 · C# et WebView2` }),
    el('p', { class: 'help', text: 'L’interface et les calculs restent locaux. Les collectes, les fiches GLPI, les données météo et les mises à jour n’utilisent le réseau que pour les opérations prévues par leur écran.' })), 'about'),
  card('Aide', el('div', { class: 'workspace-settings-tools cfg-tools' }, tool('Guide et raccourcis', 'Prise en main, clavier et lecture des périmètres', 'help', 'help'))));

  const deployment = await createDeploymentConfiguration(ctx, { onState: value => { deploymentState = value; paintTiles(); } });
  if (signal.aborted) { deployment.dispose(); motionPreview.dispose(); return () => panels.dispose(); }
  into('deploiement', mark(deployment.element, 'deployment'));
  await paintEditor();
  if (signal.aborted) { deployment.dispose(); motionPreview.dispose(); return () => panels.dispose(); }

  // ----- Tiles -----
  function setTile(id, value, detail, tone) {
    const node = tiles.get(id); if (!node) return;
    node.dataset.tone = tone;
    node.querySelector('.cfg-pulse-v').textContent = value;
    node.querySelector('.cfg-pulse-s').textContent = detail;
    node.title = `${node.querySelector('.cfg-pulse-k').textContent} : ${value}${detail ? ` · ${detail}` : ''}`;
  }
  function paintTiles() {
    const saved = state.settings || settings, view = saved.interface || {}, history = saved.history || {}, records = state.session || {};
    const motion = view.reduceMotion ? 'reduce' : view.followWindowsMotion === false ? 'full' : 'system';
    setTile('appearance', themeOptions.find(option => option.value === themePreference(saved))?.label || 'Suivre Windows',
      `${MOTION_LABELS[motion]} · zoom ${saved.interfaceZoomPercent ?? 100} %`, 'accent');
    const kind = records.demo ? 'Démonstration' : records.testMode ? 'Simulation' : records.cachedSource ? 'Cache local' : records.historical ? 'Archive' : records.fileSource ? 'Fichier local' : 'Collecte GLPI';
    if (records.loaded) setTile('records', `${formatNumber(records.rows)} lignes`, `${kind}${records.date ? ` · ${formatDate(records.date)}` : ''}`, records.demo ? 'warn' : 'info');
    else setTile('records', 'Aucun relevé', 'Ouvrez une collecte depuis Données', 'off');
    setTile('history', history.enabled === false ? 'Archivage désactivé' : 'Archivage actif',
      `${formatNumber(history.retentionDays ?? 0)} jours · ${formatNumber(history.maximumSnapshots ?? 0)} archives · ${formatNumber(history.storageBudgetMb ?? 0)} Mio`, history.enabled === false ? 'warn' : 'ok');
    if (!deploymentState) setTile('pack', '—', '', 'off');
    else if (deploymentState.simulation) setTile('pack', 'Profil fictif', 'Quittez la démonstration pour diffuser la configuration', 'off');
    else setTile('pack', deploymentState.defaultSha256 ? 'Défaut présent' : 'Aucun défaut', deploymentState.defaultSha256 ? `wad/${deploymentState.fileName}` : 'Un profil neuf démarre avec les valeurs de base', deploymentState.defaultSha256 ? 'ok' : 'off');
    const badgeNode = rail.querySelector('[data-badge=demonstration]');
    badgeNode.hidden = !(records.demo || records.demoSimulation);
    badgeNode.textContent = '•'; badgeNode.title = 'Démonstration en cours';
  }
  paintTiles();
  const offSettings = api.on?.('settings.changed', () => queueMicrotask(paintTiles));
  // A new relevé changes the revision the editor cards save with: rebuild the cards that hold no unapplied value.
  const offSession = api.on?.('session.changed', () => queueMicrotask(() => {
    if (signal.aborted) return; paintTiles();
    if (state.session?.revision !== editorRevision) paintEditor().catch(error => { if (error?.name !== 'AbortError') notify(error.message, 'danger'); });
  }));

  // ----- Rail -----
  function movePill(item) {
    if (!item || !item.offsetHeight) { pill.classList.remove('on'); return; }
    pill.style.setProperty('--y', `${item.offsetTop}px`); pill.style.setProperty('--h', `${item.offsetHeight}px`); pill.classList.add('on');
  }
  const reduced = () => document.documentElement.dataset.motion === 'reduce' || document.documentElement.dataset.domeniaMotion === 'off';
  function show(id, { setting = '', focus = false, first = false } = {}) {
    const section = sectionOf(id) || 'apparence';
    if (section !== current) {
      current = section;
      for (const [key, pane] of panes) {
        const on = key === section; pane.element.hidden = !on;
        if (on && !first) { pane.element.classList.remove('cfg-enter'); void pane.element.offsetWidth; pane.element.classList.add('cfg-enter'); }
      }
      for (const item of railItems) { const on = item.dataset.settingsNav === section; item.setAttribute('aria-current', on ? 'page' : 'false'); if (on) movePill(item); }
      if (!first) live.textContent = `Section ${SECTIONS.find(item => item.id === section).label}`;
    }
    if (setting) requestAnimationFrame(() => highlight(section, setting));
    else if (focus) content.focus({ preventScroll: true });
  }
  function highlight(section, target) {
    const pane = panes.get(section)?.element; if (!pane || signal.aborted) return;
    const key = CSS.escape(target);
    const node = pane.querySelector(`[data-setting="${key}"]`) || pane.querySelector(`[name="${key}"]`)?.closest('.field') || pane.querySelector(`[data-open-panel="${key}"]`);
    if (!node) return;
    for (let parent = node.parentElement; parent && parent !== pane; parent = parent.parentElement) if (parent instanceof HTMLDetailsElement) parent.open = true;
    node.scrollIntoView({ block: 'center', behavior: reduced() ? 'auto' : 'smooth' });
    node.classList.remove('cfg-flash'); void node.offsetWidth; node.classList.add('cfg-flash');
    const control = node.matches('button,input,select,textarea') ? node : node.querySelector('button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),summary');
    control?.focus({ preventScroll: true });
  }
  rail.addEventListener('keydown', event => {
    if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    const at = railItems.indexOf(document.activeElement); if (at < 0) return;
    event.preventDefault();
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? railItems.length - 1 : (at + (event.key === 'ArrowDown' ? 1 : -1) + railItems.length) % railItems.length;
    railItems[next].focus();
  });
  const resize = 'ResizeObserver' in window ? new ResizeObserver(() => movePill(railItems.find(item => item.getAttribute('aria-current') === 'page'))) : null;
  resize?.observe(rail);

  // ----- Search of a setting -----
  let hits = [], active = -1;
  function paintHits() {
    const words = fold(search.value.trim()).split(/\s+/).filter(Boolean);
    hits = words.length ? INDEX.filter(entry => words.every(word => entry.words.includes(word))).slice(0, 8) : [];
    active = hits.length ? 0 : -1;
    list.replaceChildren(...(hits.length ? hits.map((hit, index) => {
      const section = SECTIONS.find(item => item.id === hit.section);
      return el('li', { role: 'option', id: `cfg-hit-${index}`, 'aria-selected': String(index === active), dataset: { hit: String(index) } },
        icon(section.icon), el('span', { class: 'cfg-hit-label', text: hit.label }), el('span', { class: 'cfg-hit-section', text: section.label }));
    }) : words.length ? [el('li', { class: 'cfg-hit-none', role: 'option', 'aria-disabled': 'true', text: 'Aucun réglage ne correspond.' })] : []));
    list.hidden = !words.length; search.setAttribute('aria-expanded', String(!!words.length));
    if (active >= 0) search.setAttribute('aria-activedescendant', `cfg-hit-${active}`); else search.removeAttribute('aria-activedescendant');
  }
  function choose(index) {
    const hit = hits[index]; if (!hit) return;
    search.value = ''; paintHits(); show(hit.section, { setting: hit.target });
  }
  search.addEventListener('input', paintHits);
  search.addEventListener('keydown', event => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (!hits.length) return;
      event.preventDefault(); active = (active + (event.key === 'ArrowDown' ? 1 : -1) + hits.length) % hits.length;
      for (const item of list.querySelectorAll('[data-hit]')) item.setAttribute('aria-selected', String(Number(item.dataset.hit) === active));
      search.setAttribute('aria-activedescendant', `cfg-hit-${active}`);
    } else if (event.key === 'Enter') { event.preventDefault(); choose(active); }
    else if (event.key === 'Escape' && search.value) { event.preventDefault(); event.stopPropagation(); search.value = ''; paintHits(); }
  });
  list.addEventListener('pointerdown', event => {
    const item = event.target instanceof Element ? event.target.closest('[data-hit]') : null;
    if (item) { event.preventDefault(); choose(Number(item.dataset.hit)); }
  });
  search.addEventListener('blur', () => setTimeout(() => { if (document.activeElement !== search) { list.hidden = true; search.setAttribute('aria-expanded', 'false'); } }, 120));
  // « / » focuses the search of the settings, outside text fields and dialogs.
  const onKey = event => {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || !root.isConnected) return;
    const target = event.target;
    if (target instanceof HTMLElement && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName) || target.closest('dialog'))) return;
    if (document.querySelector('dialog[open]')) return;
    event.preventDefault(); search.focus();
  };
  document.addEventListener('keydown', onKey);

  // ----- Start -----
  const panelSection = params.panel ? PANEL_SECTION[params.panel] : null;
  const first = params.deploymentResult ? 'deploiement' : sectionOf(params.section) || panelSection || keptSection || 'apparence';
  show(first, { first: true });
  if (params.setting) show(first, { setting: params.setting });
  if (first === 'demonstration') demonstration.querySelector('.button')?.focus({ preventScroll: true });
  if (params.deploymentResult) requestAnimationFrame(() => root.querySelector('[data-deployment-applied]')?.scrollIntoView({ block: 'center' }));
  root.dataset.ready = 'true';

  // As in Domenia, FILPRO and PESTO, the demonstration is only opened, reset and closed here.
  function demonstrationCard() {
    // demoSimulation survives an archive opened inside the demonstration, where the displayed relevé is no longer the demo one.
    const demo = state.session.demoSimulation === true || state.session.demo === true, file = state.session.testMode === true && !demo;
    const result = card('Espace de démonstration', el('p', { text: 'Explorez GLPI Pilot avec 60 tickets fictifs, dans un espace séparé. La connexion et les téléchargements y restent simulés ; vos données et réglages habituels ne sont pas modifiés.' }),
      toolbar(...(demo ? [button('Quitter la démonstration', () => switchDemonstration('exit'), { primary: true, icon: 'close' }),
        button('Réinitialiser les données fictives', () => switchDemonstration('reset'), { icon: 'refresh' })]
        : [button('Ouvrir la démonstration', () => switchDemonstration('enter'), { icon: 'spark' })])));
    result.classList.add('settings-demonstration'); result.dataset.settingsSection = 'demo'; result.dataset.setting = 'demo';
    async function switchDemonstration(change) {
      const [title, message] = {
        enter: ['Ouvrir la démonstration', `GLPI Pilot ouvre un espace fictif séparé.${file ? ' La source CSV / Excel en simulation est fermée.' : ''} Les saisies non enregistrées de cet écran sont abandonnées. Vos données et réglages habituels sont conservés.`],
        exit: ['Quitter la démonstration', 'Revenir à votre espace habituel. Les données fictives et les réglages modifiés pendant la démonstration ne sont pas conservés.'],
        reset: ['Réinitialiser les données fictives', 'Recréer les tickets fictifs de la démonstration. Les changements faits pendant la démonstration sont perdus.']
      }[change];
      if (!await confirm(title, message, title)) return;
      // The context change replaces this page: report a failure as a notification, the card is no longer displayed.
      try { if (change === 'exit') await ctx.exitSimulation(); else { await api.call('app.demo'); await navigate('home'); } }
      catch (error) { if (error?.name !== 'AbortError' && error?.code !== 'CANCELLED') notify(`${title} n’a pas abouti : ${error.message}`, 'danger'); }
    }
    return result;
  }

  // The cards of the settings editor (collection, installation, ticket reading, history, team configuration), shown
  // inline. A save in one of them rebuilds them all from the saved settings, without leaving the section.
  // The card that was just saved is rebuilt; another card keeps the values typed and not yet applied.
  async function paintEditor(saved = null) {
    const detached = el('div'), revision = state.session?.revision;
    await renderSettingsEditor({ ...ctx, root: detached, refresh: () => paintEditor(lastSlot).then(paintTiles) });
    if (signal.aborted) return;
    editorRevision = revision;
    for (const child of [...detached.children]) {
      const host = editorHosts.get(child.querySelector('h2')?.textContent);
      if (host && (!host.firstElementChild || host === saved || !dirty(host))) host.replaceChildren(child);
    }
    const exportButton = editorHosts.get('Collecte et exports')?.querySelector('.toolbar .button:nth-child(2)');
    if (exportButton) exportButton.dataset.setting = 'export-folder';
  }

  function note(text) { return el('li', {}, icon('check'), el('span', { text })); }
  function tool(title, description, panel, symbol) {
    const control = button('', () => openTool(panel), { attributes: { 'data-open-panel': panel } });
    replaceChildren(control, icon(symbol), el('span', {}, el('strong', { text: title }), el('small', { text: description }))); return control;
  }
  async function openTool(name) {
    if (name === 'sites') return navigate('terrain-map', { panel: 'reference' });
    if (name === 'connection') return navigate('data', { panel: 'connection' });
    if (name === 'alert-rules' || name === 'alerts') return navigate('home', { panel: 'alert-rules' });
    if (INLINE_PANELS[name]) return show(PANEL_SECTION[name], { setting: INLINE_PANELS[name] });
    const definitions = { 'pilot-settings': ['Règles du pilotage', renderBusinessSettings],
      automation: ['Programmer les collectes', automationTools.find(item => item.id === 'automation').render],
      updates: ['Mettre à jour GLPI Pilot', renderUpdates], operations: ['Journal et diagnostic', renderOperations], help: ['Guide et raccourcis', renderHelp] };
    const definition = definitions[name]; if (definition) return panels.open(definition[0], definition[1], { ...ctx.params, panel: name, embedded: true });
  }
  if (params.panel) await openTool(params.panel);
  const dispose = async () => {
    document.removeEventListener('keydown', onKey); resize?.disconnect(); offSettings?.(); offSession?.();
    deployment.dispose(); motionPreview.dispose(); await panels.dispose(); root.classList.remove('settings-workspace');
  };
  // Rebuilt in place (Actualiser, identity change): the section shown stays the same.
  dispose.refresh = async () => { kept = { section: current, at: performance.now() }; await navigate('settings', {}, { preserveScroll: true }); };
  dispose.identityParams = () => ({ section: current });
  return dispose;
}
