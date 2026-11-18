const oldCacheMs = 7 * 24 * 60 * 60 * 1000;
const dateFormat = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short', timeStyle: 'medium' });

function timestamp(value) {
  if (!value) return null;
  const result = new Date(value).getTime();
  return Number.isFinite(result) ? result : null;
}

function knownDate(value, raw) {
  const time = timestamp(value);
  return time == null ? String(raw || '').trim() : dateFormat.format(time);
}

// Colour describes the local copy against the loaded observation. It never
// asserts that the live GLPI ticket has remained unchanged since that date.
export function ticketCacheView(cache, now = Date.now()) {
  cache ||= {};
  const state = ['missing', 'current', 'recent', 'stale', 'unknown'].includes(cache.state) ? cache.state : 'unknown';
  const readTime = timestamp(cache.readAt), sourceTime = timestamp(cache.sourceModifiedAt);
  let tone = 'unknown', mark = '?', label = 'État du cache inconnu';
  if (state === 'missing' && !cache.hasContent) {
    tone = 'missing'; mark = '×'; label = 'Cache absent';
  } else if (state === 'current') {
    tone = 'warning'; mark = '!';
    if (sourceTime == null) label = 'Cache conservé · version à vérifier';
    else if (readTime == null || readTime > now) label = 'Cache conforme au relevé · date de lecture à vérifier';
    else if (now - readTime > oldCacheMs) label = 'Cache ancien · version inchangée dans le relevé';
    else { tone = 'current'; mark = '✓'; label = 'Cache récent · version conforme au relevé'; }
  } else if (state === 'recent') {
    tone = 'warning'; mark = '!'; label = 'Lu récemment · version à vérifier';
  } else if (state === 'stale') {
    tone = 'warning'; mark = '!'; label = 'Cache ancien ou modifié · à vérifier';
  }
  const sourceModified = knownDate(cache.sourceModifiedAt, cache.sourceModifiedRaw);
  const cachedModified = knownDate(cache.cachedModifiedAt, cache.cachedModifiedRaw);
  const dates = [
    { label: sourceModified ? 'Dernière modification (relevé)' : cachedModified ? 'Dernière modification (fiche en cache)' : 'Dernière modification du ticket',
      value: sourceModified || cachedModified || 'non connue' },
    { label: 'Dernière lecture / mise en cache', value: knownDate(cache.readAt) || (tone === 'missing' ? 'aucune' : 'non connue') }
  ];
  const details = [label, cache.reason, ...dates.map(item => `${item.label} : ${item.value}`)];
  if (sourceModified && cachedModified && sourceModified !== cachedModified) details.push('Version de la fiche en cache : ' + cachedModified);
  if (cache.sourceObservedAt) details.push('Relevé observé le : ' + (knownDate(cache.sourceObservedAt) || 'date inconnue'));
  return { state, tone, mark, label, dates, description: details.filter(Boolean).join('\n') };
}
