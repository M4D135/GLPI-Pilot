import { replaceChildren, el, button, card, toolbar, badge, form, dialog, select, field, icon } from '../ui.js';
import { createWorkspacePanels } from '../workspace-panels.js';
import { openNavigationEditor, navigationVisible } from '../navigation.js';
import { renderSettingsEditor, renderHelp } from './settings.js';
import { renderBusinessSettings } from './analytics-business.js';
import { renderOperations } from './foundation.js';
import { renderUpdates } from './terrain-update.js';
import { pages as automationTools } from './automation.js';

const sections = {
  acquisition: ['Collecte et exports', 'Adaptation à votre installation'], storage: ['Historique local'],
  tickets: ['Lecture des tickets'], profile: ['Configuration d’équipe']
};

export async function renderSettingsWorkspace(ctx) {
  const { root, api, state, signal, navigate, notify } = ctx;
  const panels = createWorkspacePanels(ctx);
  const settings = await api.call('settings.get', {}, { signal });
  if (signal.aborted) return () => panels.dispose();
  root.classList.add('settings-workspace'); root.dataset.workspace = 'settings';
  root.append(el('section', { class: 'hero workspace-intro' }, el('div', {},
    el('p', { class: 'workspace-eyebrow', text: 'VOTRE INSTALLATION' }), el('h2', { text: 'Un outil à votre manière.' }),
    el('p', { text: 'Réglez le confort de lecture, le pilotage et vos connexions. Les fonctions avancées restent accessibles depuis leur contexte.' }),
    el('div', { class: 'workspace-intro-meta' }, badge(`GLPI Pilot ${state.version}`, 'info'), el('span', { text: 'Préférences du profil utilisateur' }))),
    el('div', { class: 'hero-icon', 'aria-hidden': 'true' }, icon('settings'))));
  const appearance = form([
    { key: 'theme', label: 'Thème', type: 'select', options: [{ value: 'dark', label: 'Sombre' }, { value: 'light', label: 'Clair' }] },
    { key: 'accent', label: 'Accent', type: 'select', options: [{ value: 'blue', label: 'Bleu' }, { value: 'violet', label: 'Violet' }, { value: 'green', label: 'Vert' }, { value: 'orange', label: 'Ambre' }, { value: 'rose', label: 'Rose' }] },
    { key: 'motionMode', label: 'Animations', type: 'select', options: [{ value: 'full', label: 'Expressives' }, { value: 'system', label: 'Suivre Windows' }, { value: 'reduce', label: 'Réduites' }], help: 'La météo et les décors s’animent ; les tableaux restent stables.' },
    { key: 'density', label: 'Densité', type: 'select', options: [{ value: 'compact', label: 'Compacte' }, { value: 'balanced', label: 'Équilibrée' }, { value: 'comfortable', label: 'Confortable' }] },
    { key: 'interfaceZoomPercent', label: 'Zoom (%)', type: 'number', min: 75, max: 150 },
    { key: 'colorVision', label: 'Perception des couleurs', type: 'select', options: [{ value: 'standard', label: 'Standard' }, { value: 'deuteranopia', label: 'Deutéranopie' }, { value: 'protanopia', label: 'Protanopie' }, { value: 'tritanopia', label: 'Tritanopie' }] },
    { key: 'followWindowsTheme', label: 'Suivre le thème Windows', type: 'checkbox' },
    { key: 'highContrast', label: 'Contrastes renforcés', type: 'checkbox' },
    { key: 'focusMode', label: 'Mode concentration', type: 'checkbox' },
    { key: 'frozenColumns', label: 'Colonnes source figées', type: 'number', min: 0, max: 8 }
  ], { ...settings.interface, theme: settings.theme, interfaceZoomPercent: settings.interfaceZoomPercent,
    motionMode: settings.interface.reduceMotion ? 'reduce' : settings.interface.followWindowsMotion === false ? 'full' : 'system' });
  const layout = el('div', { class: 'workspace-settings-grid' }); root.append(layout);
  layout.append(card('Confort et présentation', appearance.element,
    toolbar(button('Appliquer', async () => {
      const { theme, interfaceZoomPercent, motionMode, ...preferences } = appearance.read();
      await api.call('app.appearance', { theme, zoom: interfaceZoomPercent, preferences: { ...state.settings.interface, ...preferences,
        reduceMotion: motionMode === 'reduce', followWindowsMotion: motionMode === 'system' } });
      notify('Présentation enregistrée.');
    }, { primary: true, icon: 'check' }),
      button('Organiser les six espaces', () => openNavigationEditor({ pages: ctx.pages, preferences: state.settings.interface,
        save: async preferences => { await api.call('app.appearance', { preferences: { ...state.settings.interface, ...preferences } }); notify('Menu enregistré.'); } }), { icon: 'layers' }),
      button('Choisir l’écran', async () => { const screens = await api.call('host.screens'); const choice = select(screens.map(screen => ({ value: screen.id, label: `${screen.primary ? 'Principal · ' : ''}${screen.width} × ${screen.height} · ${screen.id}` })), state.settings.interface.preferredMonitor);
        dialog('Déplacer la fenêtre', field('Écran', choice), [{ label: 'Déplacer', primary: true, action: async close => { await api.call('host.screen', { id: choice.value }); close(); } }]);
      }))));
  layout.append(card('Pilotage et travail d’équipe', el('div', { class: 'workspace-settings-tools' },
    tool('Seuils, équipes et règles météo', 'Valeurs de départ, correspondances et actualité du service', 'pilot-settings', 'sun'),
    tool('Sites et communes', 'Importer le référentiel et régler la détection', 'sites', 'map'),
    tool('Alertes', 'Conditions de surveillance et prise en compte', 'alert-rules', 'bell'),
    tool('Collectes programmées', 'Calendrier, recette et santé des sources', 'automation', 'clock'),
    tool('Profil et configuration', 'Importer, exporter ou reprendre une version locale', 'profile', 'folder'))));
  root.append(el('div', { class: 'workspace-section-grid' },
    card('Connexion, stockage et lecture', el('div', { class: 'workspace-settings-tools' },
      tool('Collecte et exports', 'Délais, limites et adaptation de votre installation', 'acquisition', 'download'),
      tool('Conservation des observations', 'Archives, durée et budget de stockage', 'storage', 'history'),
      tool('Contenu des tickets', 'Sections, repères et limites de lecture', 'tickets', 'ticket'),
      tool('Bibliothèque partagée', 'Dossier local et central facultatif', 'connection', 'users'))),
    card('Entretien et aide', el('div', { class: 'workspace-settings-tools' },
      tool('Mettre à jour', 'Vérifier un pack et préparer son installation', 'updates', 'refresh'),
      tool('Journal et diagnostic', 'Résultats des opérations et rapport partageable', 'operations', 'log'),
      tool('Guide et raccourcis', 'Prise en main, clavier et lecture des périmètres', 'help', 'help')))));
  const optional = form([
    { key: 'assistant', label: 'Assistant de langage', type: 'checkbox', help: 'À activer lorsqu’un service de langage est configuré.' },
    { key: 'enrichment', label: 'Lectures ciblées', type: 'checkbox', help: 'Compléter une sélection depuis Données.' },
    { key: 'populations', label: 'Effectifs des sites', type: 'checkbox', help: 'Utiliser les effectifs datés dans Terrain.' }
  ], Object.fromEntries(['assistant', 'enrichment', 'populations'].map(key => [key, navigationVisible(key, settings.interface)])));
  root.append(card('Fonctions facultatives', optional.element, button('Appliquer les fonctions visibles', async () => {
    const values = optional.read(), hiddenNavigation = (state.settings.interface.hiddenNavigation || []).filter(key => !Object.hasOwn(values, key));
    for (const [key, enabled] of Object.entries(values)) if (!enabled) hiddenNavigation.push(key);
    await api.call('app.appearance', { preferences: { ...state.settings.interface, hiddenNavigation } }); notify('Fonctions contextuelles actualisées.');
  }, { primary: true })));
  root.dataset.ready = 'true';

  function tool(title, description, panel, symbol) {
    const control = button('', () => openTool(panel), { attributes: { 'data-open-panel': panel } });
    replaceChildren(control, icon(symbol), el('span', {}, el('strong', { text: title }), el('small', { text: description }))); return control;
  }
  async function openTool(name) {
    if (name === 'sites') return navigate('terrain-map', { panel: 'reference' });
    if (name === 'connection') return navigate('data', { panel: 'connection' });
    if (name === 'alert-rules' || name === 'alerts') return navigate('home', { panel: 'alert-rules' });
    const definitions = { 'pilot-settings': ['Règles du pilotage', renderBusinessSettings],
      automation: ['Programmer les collectes', automationTools.find(tool => tool.id === 'automation').render],
      updates: ['Mettre à jour GLPI Pilot', renderUpdates], operations: ['Journal et diagnostic', renderOperations], help: ['Guide et raccourcis', renderHelp] };
    if (sections[name]) return panels.open({ acquisition: 'Collecte et exports', storage: 'Conserver les observations', tickets: 'Lire les contenus des tickets', profile: 'Partager et reprendre une configuration' }[name],
      async local => {
        const detached = el('div'); await renderSettingsEditor({ ...local, root: detached });
        for (const child of [...detached.children]) if (sections[name].includes(child.querySelector('h2')?.textContent)) local.root.append(child);
      }, { panel: name });
    const definition = definitions[name]; if (definition) return panels.open(definition[0], definition[1], { ...ctx.params, panel: name, embedded: true });
  }
  if (ctx.params?.panel) await openTool(ctx.params.panel);
  return async () => { await panels.dispose(); root.classList.remove('settings-workspace'); };
}
