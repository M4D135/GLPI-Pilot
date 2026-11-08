import { replaceChildren, el, button, card, toolbar, badge, form, dialog, select, field, icon, confirm } from '../ui.js';
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
  const layout = el('div', { class: 'workspace-settings-grid' }); root.append(layout);
  const motionPreview = createMotionPreview(state);
  layout.append(card('Confort et présentation', motionPreview.element, appearance.element,
    toolbar(button('Appliquer', async () => {
      const { theme, interfaceZoomPercent, motionMode, ...preferences } = appearance.read();
      await api.call('app.appearance', { ...themeChange(theme, { ...state.settings.interface, ...preferences,
        reduceMotion: motionMode === 'reduce', followWindowsMotion: motionMode === 'system' }), zoom: interfaceZoomPercent });
      appearance.markSaved();
      notify('Présentation enregistrée.');
    }, { primary: true, icon: 'check' }),
      button('Organiser le menu', () => openNavigationEditor({ pages: ctx.pages, preferences: state.settings.interface,
        save: async preferences => { await api.call('app.appearance', { preferences: { ...state.settings.interface, ...preferences } }); notify('Menu enregistré.'); } }), { icon: 'layers' }),
      button('Choisir l’écran', async () => { const screens = await api.call('host.screens'); const choice = select(screens.map(screen => ({ value: screen.id, label: `${screen.primary ? 'Principal · ' : ''}${screen.width} × ${screen.height} · ${screen.id}` })), state.settings.interface.preferredMonitor);
        dialog('Déplacer la fenêtre', field('Écran', choice), [{ label: 'Déplacer', primary: true, action: async close => { await api.call('host.screen', { id: choice.value }); close(); } }]);
      }))));
  layout.append(card('Pilotage et travail d’équipe', el('div', { class: 'workspace-settings-tools' },
    tool('Seuils, équipes et règles météo', 'Valeurs de départ, correspondances et actualité du service', 'pilot-settings', 'sun'),
    tool('Sites et communes', 'Importer le référentiel et régler la détection', 'sites', 'map'),
    tool('Alertes', 'Conditions de surveillance et prise en compte', 'alert-rules', 'bell'),
    tool('Collectes programmées', 'Calendrier, recette et santé des sources', 'automation', 'clock'),
    tool('Configuration d’équipe', 'Partager certaines sections, fusionner les collections ou reprendre une version locale', 'profile', 'folder'))));
  const demonstration = demonstrationCard(); root.append(demonstration);
  const deployment = await createDeploymentConfiguration(ctx);
  if (signal.aborted) { deployment.dispose(); motionPreview.dispose(); return () => panels.dispose(); }
  root.append(deployment.element);
  root.append(el('div', { class: 'workspace-section-grid' },
    card('Connexion, stockage et lecture', el('div', { class: 'workspace-settings-tools' },
      tool('Collecte et exports', 'Délais, limites et adaptation de votre installation', 'acquisition', 'download'),
      tool('Conservation des observations', 'Archives, durée et budget de stockage', 'storage', 'history'),
      tool('Contenu des tickets', 'Sections, repères et limites de lecture', 'tickets', 'ticket'),
      tool('Partage entre postes', 'Dossier réseau facultatif ou PostgreSQL · planning et données communes', 'connection', 'users'))),
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
    await api.call('app.appearance', { preferences: { ...state.settings.interface, hiddenNavigation } }); optional.markSaved(); notify('Fonctions contextuelles actualisées.');
  }, { primary: true })));
  if (ctx.params?.section === 'demo') { demonstration.scrollIntoView({ block: 'center' }); document.scrollingElement.scrollTop = 0; demonstration.querySelector('.button')?.focus({ preventScroll: true }); }
  root.dataset.ready = 'true';

  // As in Domenia, FILPRO and PESTO, the demonstration is only opened, reset and closed here.
  function demonstrationCard() {
    // demoSimulation survives an archive opened inside the demonstration, where the displayed relevé is no longer the demo one.
    const demo = state.session.demoSimulation === true || state.session.demo === true, file = state.session.testMode === true && !demo;
    const result = card('Démonstration', el('p', { text: 'Explorez GLPI Pilot avec 60 tickets fictifs, dans un espace séparé. La connexion et les téléchargements y restent simulés ; vos données et réglages habituels ne sont pas modifiés.' }),
      toolbar(...(demo ? [button('Quitter la démonstration', () => switchDemonstration('exit'), { primary: true, icon: 'close' }),
        button('Réinitialiser les données fictives', () => switchDemonstration('reset'), { icon: 'refresh' })]
        : [button('Ouvrir la démonstration', () => switchDemonstration('enter'), { icon: 'spark' })])));
    result.classList.add('settings-demonstration'); result.dataset.settingsSection = 'demo';
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
  return async () => { deployment.dispose(); motionPreview.dispose(); await panels.dispose(); root.classList.remove('settings-workspace'); };
}
