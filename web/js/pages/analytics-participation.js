import { el, button, table, caption, command, details, input, field, textArea, formatNumber } from './analytics-ui.js';

const measures = { followups: 'Avec suivi', tasks: 'Avec tâche', both: 'Suivi et tâche', writers: 'Rédacteur · total distinct', assigned: 'Tickets attribués' };

export function participationTable(ctx, participants, mode, activate, maximum = null) {
  const parts = mode === 'comparison' ? ['writers', 'assigned'] : ['followups', 'tasks', 'both', 'writers'];
  const maximumCount = maximum ?? Math.max(0, ...participants.flatMap(person => parts.map(part => person[part] ?? 0)));
  const grid = table(participants, [
    { key: 'name', label: 'Personne / regroupement', render: person => el('span', {
      text: person.name, title: person.isAggregate ? 'Union des lignes de ce regroupement'
        : [person.identity?.startsWith('ID:') ? `Identifiant GLPI ${person.identity.slice(3)}` : '', ...(person.observedNames ?? [])].filter(Boolean).join(' · ')
    }) },
    ...parts.map(part => ({ key: part, label: measures[part], render: person => {
      if (person[part] == null) return el('span', { text: '—', title: 'La source ne permet pas de calculer cette mesure.' });
      const control = command(ctx, formatNumber(person[part]), () => activate({ kind: 'participation', key: person.key, part }), {
        disabled: person[part] === 0,
        attributes: { class: 'button analytics-participation-value', 'aria-label': `${person.name} · ${measures[part]} · ${formatNumber(person[part])}` }
      });
      if (mode !== 'comparison') return control;
      return el('div', { class: `analytics-participation-measure measure-${part}` },
        el('span', { class: 'analytics-participation-track', 'aria-hidden': 'true' },
          el('span', { style: { width: `${maximumCount > 0 ? 100 * person[part] / maximumCount : 0}%` } })), control);
    } })),
    ...(mode === 'authors' ? [{ key: 'identity', label: 'Clé GLPI', render: person => person.isAggregate ? 'Regroupement'
      : person.identity?.startsWith('ID:') ? person.identity.slice(3) : 'Non fournie' }] : [])
  ], { key: 'key', maxRows: 500 });
  if (mode === 'comparison') grid.classList.add('analytics-participation-comparison');
  return grid;
}

export function renderParticipation(ctx, data, setting, activate, more) {
  const body = el('div', { class: 'analytics-participation' },
    caption(`${formatNumber(data.population)} ${data.grain} actifs dans ce périmètre. Les volumes par personne et par rôle ne s’additionnent pas.`),
    participationTable(ctx, data.participants ?? [], setting.sourceKey, activate, data.maximumCount));
  if (data.total > data.participants.length) body.append(command(ctx, `Toutes les personnes et regroupements (${formatNumber(data.total)})`, () => more('participation', setting.sourceKey)));
  if (data.warnings?.length) body.append(details('Disponibilité et rapprochement des identités', ...data.warnings.map(warning => caption(warning, 'warning'))));
  if (setting.showDescription) body.append(details('Ce que mesurent ces nombres', caption(data.explanation),
    caption('Total distinct = avec suivi + avec tâche − suivi et tâche. Les rédacteurs peuvent être des techniciens, des demandeurs ou d’autres comptes ; aucune présence ou productivité n’est déduite.')));
  return body;
}

export function participationRosterEditor(initial = []) {
  const entries = []; const list = el('div', { class: 'analytics-participation-roster' });
  let addButton;
  const add = entry => {
    if (entries.length >= 200) throw new Error('Une liste accepte au maximum 200 personnes.');
    const name = input(entry.name ?? '', { maxLength: 160 });
    const identity = input(entry.identity ?? '', { maxLength: 200, placeholder: 'Identifiant GLPI, si connu' });
    const aliases = textArea((entry.aliases ?? []).join('\n'), 2);
    const item = { read: () => ({ identity: identity.value.trim(), name: name.value.trim(),
      aliases: [...new Set(aliases.value.split(/\r?\n/).map(value => value.trim()).filter(Boolean))] }) };
    const row = el('div', { class: 'analytics-participation-roster-row' }, field('Nom affiché', name), field('Identifiant facultatif', identity),
      field('Autres noms exacts · un par ligne', aliases), button('Retirer cette personne', () => {
        entries.splice(entries.indexOf(item), 1); row.remove(); if (addButton) addButton.disabled = false;
      }));
    entries.push(item); list.append(row); if (addButton) addButton.disabled = entries.length >= 200;
  };
  addButton = button('+ Personne', () => add({}));
  for (const entry of initial) add(entry);
  const element = el('div', {}, caption('Liste vide : toutes les personnes réellement présentes dans les colonnes. Avec une liste, les autres personnes sont regroupées sous « Autres » ; les tickets sans rédacteur ou sans attribution restent visibles.'),
    caption('Les alias sont des correspondances exactes. Une correspondance ambiguë reste à vérifier.'), list, addButton);
  return { element, read: () => {
    const values = entries.map(entry => entry.read());
    if (values.some(entry => (!entry.identity && !entry.name) || entry.aliases.length > 16 || entry.aliases.some(alias => alias.length > 200)))
      throw new Error('Renseignez un nom ou un identifiant par personne, et au maximum 16 alias de 200 caractères.');
    return values;
  } };
}
