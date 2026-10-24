import { el, button, toolbar, field, input, table, dialog, showError } from '../ui.js';

const call = (ctx, operation, args = {}) => ctx.api.call(operation, args, { signal: ctx.signal });
function popup(ctx, title, content, actions = []) {
  const result = dialog(title, content, actions); result.element.classList.add('google-maps-dialog');
  ctx.signal.addEventListener('abort', () => result.close(), { once: true }); return result;
}
function action(ctx, errors, label, run, options = {}) {
  let control; control = button(label, async () => {
    if (ctx.signal.aborted) return;
    control.disabled = true; errors.replaceChildren();
    try { await run(); } catch (error) { if (!ctx.signal.aborted) showError(errors, error); }
    finally { if (control.isConnected) control.disabled = !!options.disabled; }
  }, options); return control;
}

export async function openGoogleMaps(ctx, request) {
  const model = await call(ctx, 'maps.preview', request);
  if (ctx.signal.aborted) return;
  const errors = el('div', { role: 'alert' }), segments = el('div', { class: 'google-maps-segments' });
  const keyState = el('p'), settings = action(ctx, errors, 'Réglages Google Maps', async () => {
    await openGoogleMapsSettings(ctx, async () => { model.settings = await call(ctx, 'maps.settings.get'); paint(); });
  });
  const body = el('div', {}, el('p', { class: 'google-maps-notice', text: model.notice }),
    model.simulation ? el('p', { text: 'Simulation : les données de travail restent isolées. L’ouverture de Google Maps est un accès Internet réel, uniquement à votre demande.' }) : '',
    keyState, toolbar(settings), errors,
    ...model.notes.map(text => el('p', { class: 'google-maps-note', text })), segments);
  function paint() {
    keyState.textContent = model.settings.configured
      ? 'Clé renseignée : vous pouvez essayer la carte intégrée. Son activation et ses restrictions seront vérifiées par Google lors de l’affichage. Le lien sans clé reste disponible.'
      : 'Sans clé : le bouton Ouvrir Google Maps fonctionne immédiatement. La carte intégrée nécessite une clé Maps Embed API facultative.';
    if (model.settings.warning) keyState.textContent += ' ' + model.settings.warning;
    segments.replaceChildren(...model.segments.map(segment => el('section', { class: 'google-maps-segment' },
      el('h3', { text: segment.title }),
      el('ol', { start: segment.from }, ...segment.places.map(place => el('li', {}, el('strong', { text: place.label }),
        el('span', { text: ` · ${place.query}` }), el('small', { text: place.precision })))),
      toolbar(action(ctx, errors, 'Ouvrir Google Maps', () => call(ctx, 'maps.open', { token: segment.token, consent: true }), { primary: true }),
        action(ctx, errors, 'Afficher la carte intégrée', () => call(ctx, 'maps.open', { token: segment.token, consent: true, embedded: true,
          theme: document.documentElement.dataset.theme }), { disabled: !model.settings.configured })))));
  }
  paint(); return popup(ctx, 'Google Maps · lieux et parcours', body);
}

export async function openSiteMaps(ctx, siteId = '') {
  if (siteId) return openGoogleMaps(ctx, { kind: 'site', id: siteId });
  const search = input('', { maxLength: 120, placeholder: 'Nom ou adresse du site', 'aria-label': 'Rechercher un site pour la carte' });
  const results = el('div'), errors = el('div', { role: 'alert' }); let offset = 0;
  const body = el('div', {}, el('p', { text: 'Choisissez un site du référentiel. Cette liste et ses aperçus restent locaux jusqu’au clic d’ouverture de Google Maps.' }),
    toolbar(field('Rechercher un site', search), action(ctx, errors, 'Rechercher', async () => { offset = 0; await load(); })), errors, results);
  const modal = popup(ctx, 'Cartes des sites', body);
  async function load() {
    const model = await call(ctx, 'maps.sites', { search: search.value, offset, limit: 40 });
    if (ctx.signal.aborted || !modal.element.isConnected) return;
    results.replaceChildren(table(model.rows, [{ key: 'name', label: 'Site' }, { key: 'location', label: 'Lieu à transmettre', render: row => row.location.query },
      { key: 'precision', label: 'Précision', render: row => row.location.precision },
      { key: 'open', label: 'Carte', render: row => action(ctx, errors, 'Voir la carte', () => openSiteMaps(ctx, row.id)) }], { maxRows: 40, emptyText: 'Aucun site correspondant. Renseignez vos sites dans les paramètres ou le référentiel.' }),
      toolbar(action(ctx, errors, 'Précédent', async () => { offset = Math.max(0, offset - 40); await load(); }, { disabled: offset === 0 }),
        el('span', { text: `${model.total ? offset + 1 : 0}–${Math.min(model.total, offset + 40)} sur ${model.total}` }),
        action(ctx, errors, 'Suivant', async () => { offset += 40; await load(); }, { disabled: offset + 40 >= model.total })));
  }
  search.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); offset = 0; load().catch(error => showError(errors, error)); } });
  await load(); return modal;
}

export async function openGoogleMapsSettings(ctx, changed = null) {
  const model = await call(ctx, 'maps.settings.get'), errors = el('div', { role: 'alert' });
  const key = input('', { type: 'password', maxLength: 200, autocomplete: 'off', spellcheck: false });
  const remember = input('', { type: 'checkbox', disabled: !model.canRemember });
  const forget = input('', { type: 'checkbox' });
  const body = el('div', {}, el('p', { text: 'Les liens Google Maps sont utilisables sans clé. Ce réglage facultatif ajoute la carte dans une fenêtre de GLPI Pilot.' }),
    el('p', { text: model.configured ? 'Une clé est disponible. Laissez le champ vide pour la conserver.' : 'Aucune clé Maps Embed API configurée.' }),
    model.warning ? el('p', { class: 'notice warning', text: model.warning }) : '',
    field('Clé Maps Embed API', key, 'Créez votre clé dans Google Cloud et activez Maps Embed API. Aucune clé n’est incluse dans le logiciel.'),
    field('Mémoriser la clé pour ce compte Windows', remember, model.canRemember
      ? 'Protection locale Windows ; la clé reste en dehors des exports de configuration.' : 'Dans cette simulation, la clé reste seulement en mémoire jusqu’à sa fermeture.'),
    field('Oublier la clé actuelle', forget), errors,
    el('h3', { text: 'Configurer la carte intégrée' }),
    el('ol', {}, el('li', { text: 'Dans votre projet Google Cloud, activez Maps Embed API et créez une clé.' }),
      el('li', { text: 'Limitez cette clé à Maps Embed API et à l’origine locale suivante : ' + model.referrer }),
      el('li', { text: 'Collez la clé ci-dessus. L’enregistrement ne contacte pas Google ; l’affichage de la carte demande Internet.' })),
    el('p', { text: 'Documentation officielle : ' + model.documentation }));
  return popup(ctx, 'Réglages Google Maps', body, [{ label: 'Annuler', action: close => close() },
    { label: 'Enregistrer', primary: true, action: async close => {
      errors.replaceChildren();
      try { await call(ctx, 'maps.settings.save', { key: key.value.trim(), remember: remember.checked, forget: forget.checked }); key.value = ''; await changed?.(); close(); }
      catch (error) { showError(errors, error); }
    } }]);
}
