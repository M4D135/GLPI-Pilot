import { teamScopeCatalog } from './team-scope.js';

export const MAX_WEATHER_RULES = 48;
export const MAX_TEAM_GROUPS = 32;

// Identity stays identical to the dashboard filters. Search folding is never
// used for matching groups: spaces, accents and punctuation remain meaningful.
export function weatherGroupCatalog(groups = [], rules = []) {
  const observed = new Set(groups.map(value => `group:${value}`));
  const teams = rules.filter(rule => rule.kind === 'team');
  return teamScopeCatalog([], [...groups, ...teams.flatMap(rule => rule.values || [])]).map(option => {
    const owners = teams.filter(rule => (rule.values || []).some(value => option.aliases.includes(`group:${value}`)));
    return { ...option, value: option.groups[0], observed: option.aliases.some(key => observed.has(key)), owners,
      state: !owners.length ? 'missing' : owners[0].visible === false ? 'hidden' : 'visible' };
  });
}

// Validate the entire request before touching the draft. Saving remains an
// explicit, revision-checked operation in the existing C# settings handler.
export function addWeatherGroups(business, groups, requested, presets, makeId) {
  const catalog = weatherGroupCatalog(groups, business.weatherRules);
  const byKey = new Map(catalog.flatMap(item => item.aliases.map(key => [key, item])));
  const selected = new Map();
  for (const key of requested) {
    const item = byKey.get(key);
    if (!item?.observed) throw new Error('Un groupe n’est plus disponible dans ce relevé. Rouvrez la sélection.');
    selected.set(item.key, item);
  }
  const missing = [...selected.values()].filter(item => item.state === 'missing');
  const room = Math.max(0, MAX_WEATHER_RULES - business.weatherRules.length);
  if (missing.length > room) throw new Error(`Il reste ${room} place(s) sur ${MAX_WEATHER_RULES} cartes. Réduisez la sélection ou réunissez plusieurs groupes dans une équipe existante.`);
  const preset = (presets || []).find(item => item.kind === 'team' && item.sourceKey === 'active');
  const additions = missing.map(item => ({ id: makeId(), label: item.value.slice(0, 180), kind: 'team', sourceKey: 'active',
    values: [item.value], orangeAbove: preset?.orangeAbove ?? null, redAbove: preset?.redAbove ?? null,
    visible: true, weatherEnabled: true, includeDescendants: false, note: '' }));
  const restored = new Set([...selected.values()].filter(item => item.state === 'hidden').map(item => item.owners[0]));
  for (const rule of restored) rule.visible = true;
  business.weatherRules.push(...additions);
  return { added: additions.length, restored: restored.size };
}

export function selectedGroupValues(catalog, selected) {
  const byKey = new Map(catalog.flatMap(item => item.aliases.map(key => [key, item])));
  const result = new Map();
  for (const key of selected) {
    const item = byKey.get(key);
    if (!item) throw new Error('Une correspondance a changé. Rouvrez le sélecteur.');
    result.set(item.key, item.value);
  }
  if (result.size > MAX_TEAM_GROUPS) throw new Error(`Une équipe accepte au maximum ${MAX_TEAM_GROUPS} groupes.`);
  return [...result.values()];
}
