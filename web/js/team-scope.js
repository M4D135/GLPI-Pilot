// A group's scope uses OrdinalIgnoreCase in DashboardService.ResolveScope.
// Do not use search folding here: accents, punctuation, whitespace and Unicode
// composition remain significant. Uppercase expansions (ß -> SS) do not match.
function groupKey(value) {
  return Array.from(value, character => {
    const point = character.codePointAt(0);
    // Ordinal comparison does not equate these characters with ASCII I / S.
    if (point === 0x131 || point === 0x17f) return character;
    // Keep newer Unicode casing pairs distinct on the supported .NET runtime.
    if ([0x19b, 0x264, 0x1c8a, 0xa7cd, 0xa7cf, 0xa7d3, 0xa7d5, 0xa7db].includes(point)
      || point >= 0x10d70 && point <= 0x10d85 || point >= 0x16ebb && point <= 0x16ed3) return character;
    // These Greek letters have a simple uppercase form in .NET, while JS's
    // full uppercase conversion expands them into more than one character.
    if (point >= 0x1f80 && point <= 0x1f87 || point >= 0x1f90 && point <= 0x1f97 || point >= 0x1fa0 && point <= 0x1fa7)
      return String.fromCodePoint(point + 8);
    if (point === 0x1fb3 || point === 0x1fc3 || point === 0x1ff3) return String.fromCodePoint(point + 9);
    const upper = character.toUpperCase();
    return Array.from(upper).length === 1 ? upper : character;
  }).join('');
}

/** One choice for each exact group set; the first configured team supplies its label. */
export function teamScopeCatalog(teams = [], groups = []) {
  const options = [], byScope = new Map();
  function add(key, label, values, kind) {
    if (typeof key !== 'string' || !key || !Array.isArray(values) || !values.length || values.some(value => typeof value !== 'string' || !value)) return;
    const unique = new Map();
    for (const value of values) if (!unique.has(groupKey(value))) unique.set(groupKey(value), value);
    const signature = JSON.stringify([...unique.keys()].sort());
    let option = byScope.get(signature);
    if (!option) {
      option = { key, label: label || [...unique.values()].join(' · '), kind, groups: [...unique.values()], aliases: [], labels: [], members: [] };
      byScope.set(signature, option); options.push(option);
    }
    if (!option.aliases.includes(key)) option.aliases.push(key);
    if (label && !option.labels.includes(label)) option.labels.push(label);
    option.members.push({ key, label, groups: [...values], kind });
  }
  // Preserve rule priority. Equivalent weather rules still exist independently;
  // only their filter choices share one representative and their original keys.
  for (const team of teams) add(team.key, team.label, team.groups, 'team');
  for (const group of groups) add(`group:${group}`, group, [group], 'group');
  for (const option of options) {
    option.detail = option.groups.length === 1 && groupKey(option.label) === groupKey(option.groups[0]) ? ''
      : `${option.groups.length === 1 ? 'Groupe inclus' : `${option.groups.length} groupes inclus`} : ${option.groups.join(' · ')}`;
    option.search = [...option.labels, ...option.members.flatMap(member => member.groups)].join(' ');
  }
  return options;
}

/** Resolve existing keys without dropping unknown selections or widening their scope. */
export function teamScopeSelection(options, requested = []) {
  if (!Array.isArray(requested)) throw new TypeError('La sélection des équipes doit être une liste.');
  const aliases = new Map(options.flatMap(option => option.aliases.map(key => [key, option])));
  const keys = [], selected = new Set(), unknownKeys = [];
  for (const key of requested) {
    const option = aliases.get(key), resolved = option?.key ?? key;
    if (keys.includes(resolved)) continue;
    keys.push(resolved);
    if (option) selected.add(option); else unknownKeys.push(key);
  }
  return { keys, options: options.filter(option => selected.has(option)), unknownKeys };
}
