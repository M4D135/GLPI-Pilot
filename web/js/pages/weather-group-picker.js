import { el, button, card, toolbar, input, select, field, dialog, caption, uuid } from './analytics-ui.js';
import { weatherGroupCatalog, addWeatherGroups, selectedGroupValues, MAX_WEATHER_RULES, MAX_TEAM_GROUPS } from '../weather-groups.js';

const fold = value => String(value).normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase('fr');

export function createWeatherGroupTools(ctx, model, business, onChange) {
  const summary = el('p', { class: 'help', role: 'status', 'aria-live': 'polite' });
  const open = button('Ajouter des groupes à la météo', () => choose(), { primary: true, icon: 'plus' });
  const element = card('Groupes GLPI → cartes météo',
    caption('Cochez les groupes du relevé : une carte par groupe, sans recopier les noms. Les espaces sont conservés.'),
    summary, toolbar(open),
    caption('Les changements restent à enregistrer avec « Enregistrer les paramètres ». Les seuils et l’ordre de vos cartes existantes sont conservés.'));
  element.dataset.weatherGroupTools = 'true';
  const popups = new Set();
  let disposed = false;
  function closeAll() { disposed = true; for (const popup of [...popups]) popup.close(); popups.clear(); }
  ctx.signal?.addEventListener('abort', closeAll, { once: true });
  function track(popup) {
    popups.add(popup);
    popup.element.addEventListener('pilot:closed', () => popups.delete(popup), { once: true });
    popup.element.classList.add('maps-filter-dialog');
    return popup;
  }
  function refresh() {
    const entries = weatherGroupCatalog(model.groups, business.weatherRules).filter(item => item.observed);
    const missing = entries.filter(item => item.state === 'missing').length;
    const hidden = entries.filter(item => item.state === 'hidden').length;
    summary.textContent = `${entries.length} groupes détectés · ${missing} sans carte · ${hidden} avec carte masquée · ${Math.max(0, MAX_WEATHER_RULES - business.weatherRules.length)} places disponibles`;
    open.disabled = !entries.length;
    open.title = entries.length ? 'Rechercher et cocher les groupes à ajouter ou à réafficher' : 'Chargez un relevé contenant les groupes attribués';
  }
  function description(item, rule) {
    const others = item.owners.filter(owner => owner !== rule);
    if (!item.observed) return 'Absent du relevé actuel ; correspondance conservée';
    if (rule) return others.length ? `Également dans : ${others.map(owner => owner.label).join(' · ')}` : 'Groupe du relevé';
    if (item.state === 'missing') return 'Sans carte météo';
    return `${item.state === 'hidden' ? 'Carte masquée à réafficher' : 'Déjà configuré'} : ${item.owners[0].label}`;
  }
  function choose(rule = null, afterApply = null) {
    if (disposed) return;
    const all = weatherGroupCatalog(model.groups, business.weatherRules);
    const entries = rule ? all : all.filter(item => item.observed);
    const aliases = new Map(entries.flatMap(item => item.aliases.map(key => [key, item.key])));
    const chosen = new Set(rule ? (rule.values || []).map(value => aliases.get(`group:${value}`)).filter(Boolean) : []);
    const query = input('', { type: 'search', placeholder: 'Rechercher un groupe…', 'aria-label': 'Rechercher un groupe', maxLength: 200 });
    const filter = select([{ value: 'needed', label: 'Sans carte ou masqués' }, { value: 'all', label: 'Tous les groupes' }], 'needed');
    const count = el('p', { class: 'help', role: 'status', 'aria-live': 'polite' });
    const errors = el('p', { class: 'notice warning', role: 'alert', hidden: true });
    const list = el('div', { class: 'maps-picker-options', 'aria-label': 'Groupes disponibles' });
    list.style.maxHeight = '45vh'; list.style.overflowY = 'auto';
    const rows = entries.map(item => {
      const eligible = !!rule || item.state !== 'visible';
      const control = input('', { type: 'checkbox', checked: chosen.has(item.key), disabled: !eligible, 'aria-label': item.value });
      const label = el('label', { class: 'maps-picker-option' }, control,
        el('span', { style: { overflowWrap: 'anywhere' } }, el('strong', { text: item.value }), el('small', { class: 'help', text: description(item, rule) })));
      control.addEventListener('change', () => { control.checked ? chosen.add(item.key) : chosen.delete(item.key); update(); });
      return { item, control, label, eligible, search: fold(`${item.value} ${item.owners.map(owner => owner.label).join(' ')}`) };
    });
    const noMatch = el('p', { class: 'help', text: 'Aucun groupe ne correspond à cette recherche.', hidden: true });
    list.append(...rows.map(row => row.label), noMatch);
    function update() {
      let shown = 0;
      for (const row of rows) {
        row.label.hidden = !row.search.includes(fold(query.value)) || (!rule && filter.value === 'needed' && row.item.state === 'visible');
        row.control.checked = chosen.has(row.item.key);
        row.control.disabled = !row.eligible || (!!rule && chosen.size >= MAX_TEAM_GROUPS && !row.control.checked);
        if (!row.label.hidden) shown++;
      }
      noMatch.hidden = shown > 0;
      const selected = rows.filter(row => chosen.has(row.item.key));
      const added = selected.filter(row => row.item.state === 'missing').length;
      count.textContent = rule ? `${chosen.size} groupe(s) sélectionné(s) / ${MAX_TEAM_GROUPS}`
        : `${chosen.size} groupe(s) sélectionné(s) · ${added} nouvelle(s) carte(s) · ${Math.max(0, MAX_WEATHER_RULES - business.weatherRules.length)} places disponibles`;
    }
    const selectShown = button('Tout cocher dans les résultats', () => {
      let capacity = rule ? MAX_TEAM_GROUPS - chosen.size : Math.max(0, MAX_WEATHER_RULES - business.weatherRules.length)
        - rows.filter(row => chosen.has(row.item.key) && row.item.state === 'missing').length;
      let skipped = 0;
      for (const row of rows) {
        if (row.label.hidden || !row.eligible || chosen.has(row.item.key)) continue;
        if ((rule || row.item.state === 'missing') && capacity <= 0) { skipped++; continue; }
        chosen.add(row.item.key); if (rule || row.item.state === 'missing') capacity--;
      }
      errors.hidden = !skipped;
      errors.textContent = skipped ? `${skipped} groupe(s) non cochés : capacité atteinte. Rien n’est ajouté silencieusement.` : '';
      update();
    });
    const clear = button('Décocher les résultats', () => { for (const row of rows) if (!row.label.hidden && row.eligible) chosen.delete(row.item.key); errors.hidden = true; update(); });
    const popup = track(dialog(rule ? `Groupes de ${rule.label}` : 'Ajouter des groupes à la météo',
      el('div', { class: 'maps-picker' },
        caption(rule ? 'Cochez les groupes à réunir dans cette équipe. En cas de chevauchement, la première équipe dans l’ordre reste prioritaire.'
          : 'Les cartes existantes ne sont pas dupliquées. Cocher une carte masquée la réaffiche, sans changer ses seuils.'),
        field('Recherche', query), !rule ? field('Afficher', filter) : null,
        toolbar(selectShown, clear), count, errors, list),
      [{ label: 'Annuler', action: close => close() }, { label: rule ? 'Appliquer la sélection' : 'Ajouter / réafficher les cartes', primary: true,
        action: close => {
          try {
            if (disposed) return;
            if (rule) { rule.values = selectedGroupValues(entries, chosen); afterApply?.(); }
            else {
              if (!chosen.size) throw new Error('Cochez au moins un groupe.');
              const result = addWeatherGroups(business, model.groups, chosen, model.weatherThresholdPresets, uuid);
              onChange();
              ctx.notify(`${result.added} carte(s) ajoutée(s), ${result.restored} réaffichée(s). Enregistrez les paramètres pour les conserver.`);
            }
            refresh(); close();
          } catch (error) { errors.textContent = error.message; errors.hidden = false; }
        } }]));
    query.addEventListener('input', update); filter.addEventListener('change', update);
    update(); query.focus(); return popup;
  }
  function editor(rule, changed) {
    const chips = el('div', { class: 'toolbar', 'aria-label': `Groupes de ${rule.label}` });
    function paint() {
      chips.replaceChildren(...(rule.values || []).map(value => button(`${value} ×`, () => {
        rule.values = rule.values.filter(item => item !== value); changed(); refresh(); paint();
      }, { title: `Retirer ${value} de cette équipe`, attributes: { 'aria-label': `Retirer le groupe ${value}` } })));
      if (!rule.values?.length) chips.append(caption('Aucun groupe sélectionné.'));
    }
    const host = el('div', {}, button('Choisir les groupes', () => choose(rule, () => { changed(); paint(); }), { icon: 'users' }), chips);
    paint(); return { element: host, refresh: paint };
  }
  refresh();
  return { element, refresh, editor, dispose: () => { ctx.signal?.removeEventListener('abort', closeAll); closeAll(); } };
}
