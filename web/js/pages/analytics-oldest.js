import { el, table, toolbar, badge, empty, caption, details, dialog, formatNumber, formatDate, error } from './analytics-ui.js';

const parts = [
  { part: 'missing', count: 'missingDateCount', rows: 'missingDateRows', label: 'Dates absentes' },
  { part: 'invalid', count: 'invalidDateCount', rows: 'invalidDateRows', label: 'Dates invalides' },
  { part: 'future', count: 'futureDateCount', rows: 'futureDateRows', label: 'Dates futures' }
];
const countOf = (data, count, rows) => Number.isInteger(data?.[count]) && data[count] >= 0
  ? data[count] : Array.isArray(data?.[rows]) ? data[rows].length : null;
const labelOf = group => group.key === 'unassigned:' ? 'Sans groupe · affectation absente' : group.label;
const topCountOf = data => Number.isInteger(data.topCount) && data.topCount >= 1 && data.topCount <= 30 ? data.topCount : 5;
const countText = count => count == null ? 'Indisponible' : formatNumber(count);

// Keep the triggering control focusable while a drilldown opens. Native disabled
// buttons can lose focus before dialog() records where Escape should return it.
function actionButton(ctx, text, action, attributes = {}) {
  const control = el('button', { type: 'button', class: 'button', text, ...attributes });
  let running = false;
  control.addEventListener('click', async () => {
    if (running) return;
    running = true; control.setAttribute('aria-busy', 'true'); control.setAttribute('aria-disabled', 'true');
    try { await action(control); } catch (reason) { error(ctx, reason); }
    finally { running = false; control.removeAttribute('aria-busy'); control.removeAttribute('aria-disabled'); }
  });
  return control;
}

function ageLabel(value, color) {
  if (!Number.isInteger(value) || value < 0) return el('span', { text: 'Indisponible' });
  const style = /^#[0-9a-f]{6}$/i.test(color ?? '')
    ? { backgroundColor: color, color: '#25364a', borderRadius: '.4rem', padding: '.2rem .5rem', whiteSpace: 'nowrap' } : {};
  return el('span', { class: 'analytics-oldest-age', style, text: `${formatNumber(value)} j`,
    title: `${formatNumber(value)} jours calendaires ; couleur relative au maximum commun, sans seuil SLA.` });
}

function referenceDate(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})(?:T|$)/.exec(String(value ?? ''));
  return match ? `${match[3]}/${match[2]}/${match[1]}` : 'Indisponible';
}

function populationButton(ctx, group, part, count, activate, label) {
  if (count == null || count === 0 || typeof activate !== 'function') return el('span', { text: `${label ? `${label} : ` : ''}${countText(count)}` });
  return actionButton(ctx, `${label ? `${label} : ` : ''}${countText(count)}`,
    () => activate({ kind: 'oldest', key: group.key, part }),
    { 'aria-label': `${label || 'Stock actif'} · ${labelOf(group)} : ${formatNumber(count)} lignes. Voir les lignes contributrices.` });
}

function dateQuality(ctx, group, activate) {
  return toolbar(...parts.map(part => populationButton(ctx, group, part.part,
    countOf(group, part.count, part.rows), activate, part.label)));
}

function openGroup(ctx, data, group, activate, trigger) {
  const topCount = topCountOf(data);
  const oldest = (group.oldest ?? []).slice(0, topCount);
  const body = el('div', { class: 'analytics-oldest-group-detail' });
  const modal = dialog(`Plus anciens · ${labelOf(group)}`, body, [], { returnFocus: trigger });
  const openTicket = ticket => {
    if (!Number.isInteger(ticket.index) || ticket.index < 0) return;
    modal.close();
    return activate?.({ kind: 'record', rowIndex: ticket.index });
  };
  body.append(caption(`Référence : ${referenceDate(data.referenceDate)} · ${topCount} plus anciennes ouvertures au maximum. Les heures puis les ID numériques départagent les dates identiques.`),
    toolbar(populationButton(ctx, group, 'stock', countOf(group, 'count', 'rows'), activate, 'Stock actif'),
      populationButton(ctx, group, 'dated', countOf(group, 'datedCount', 'datedRows'), activate, 'Ouvertures valides'),
      ageLabel(group.maximumAgeDays, group.color)), dateQuality(ctx, group, activate));
  if (!oldest.length) body.append(empty('Aucune ouverture classable',
    'Le stock reste consultable. Les dates absentes, invalides ou postérieures au jour de référence sont exclues du classement.'));
  else body.append(table(oldest, [
    { key: 'rank', label: 'Rang', render: (_, index) => String(index + 1) },
    { key: 'id', label: 'Ticket', render: ticket => el('div', {},
      actionButton(ctx, ticket.id || 'Identifiant absent', () => openTicket(ticket),
        { 'aria-label': `Consulter le ticket ${ticket.id || 'sans identifiant'}, ligne source ${ticket.index + 1}.` }),
      el('small', { text: `Ligne ${ticket.index + 1}` })) },
    { key: 'title', label: 'Titre' },
    { key: 'openedAt', label: 'Ouverture', render: ticket => formatDate(ticket.openedAt) },
    { key: 'ageDays', label: 'Âge calendaire', render: ticket => ageLabel(ticket.ageDays, ticket.color) },
    { key: 'status', label: 'Statut' },
    { key: 'priority', label: 'Priorité' },
    { key: 'assignee', label: 'Technicien(s)' }
  ], { key: 'index', maxRows: topCount, label: `Les ${topCount} plus anciennes ouvertures · ${labelOf(group)}` }));
  body.append(caption(group.key === 'scope:'
    ? 'Chaque ligne source apparaît une fois dans ce classement, même en cas d’affectations multiples. Les identifiants répétés sur des lignes distinctes restent des occurrences séparées.'
    : 'Les lignes source distinguent les occurrences dont les identifiants sont répétés. Un ticket multigroupe apparaît dans chaque groupe auquel il est affecté.'),
    caption('Les couleurs emploient le même maximum pour tous les groupes de ce périmètre. Elles ne constituent ni un délai SLA, ni un état météo.'));
  return modal;
}

/**
 * data is the bounded bridge projection of DashboardOldestGroupsSnapshot. The
 * backend retains source-row arrays; the UI accepts their explicit count fields.
 * activate receives { kind: 'oldest', key: group.key, part } for a source drilldown.
 * The caller owns paging (data.totalGroups or data.total) and the dashboard run ID.
 */
export function renderOldestGroups(ctx, data, activate) {
  if (!data || data.available === false) return empty('Plus anciens indisponibles',
    data?.unavailableReason || 'Les colonnes Statut, Date d’ouverture et Groupe doivent être reconnues.');
  const groups = data.groups ?? [];
  const count = countOf(data, 'count', 'rows');
  const dated = countOf(data, 'datedCount', 'datedRows');
  const unknown = countOf(data, 'unknownStatusCount', 'unknownStatusRows');
  const focus = groups.length === 1 && groups[0].key === 'scope:';
  const grain = data.grain === 'tickets' ? 'tickets' : 'occurrences';
  const root = el('div', { class: 'analytics-oldest-groups' },
    toolbar(badge(`Stock actif : ${countText(count)} ${grain}`), badge(`Ouvertures valides : ${countText(dated)}`),
      el('span', {}, 'Âge maximum : ', ageLabel(data.maximumAgeDays, data.maximumAgeDays === 0 ? '#EBF5FB' : '#FCB38A'))),
    caption(`Référence : ${referenceDate(data.referenceDate)} · ${focus ? 'Ouvrir le périmètre pour consulter ses' : 'Cliquer un groupe pour consulter ses'} ${topCountOf(data)} plus anciennes ouvertures.`));
  if (unknown > 0) root.append(caption(`${formatNumber(unknown)} ligne(s) à statut inconnu, exclue(s) du stock actif.`, 'warning'));
  if (!groups.length) {
    root.append(empty('Aucun groupe à afficher', count === 0
      ? 'Aucun ticket actif reconnu dans ce périmètre.' : 'Aucun groupe dans cette page de résultats.'));
    return root;
  }
  root.append(table(groups, [
    { key: 'label', label: focus ? 'Périmètre' : 'Groupe', render: group => actionButton(ctx, labelOf(group),
      trigger => { openGroup(ctx, data, group, activate, trigger); }, { 'aria-label': `${labelOf(group)}. Consulter les plus anciens.` }) },
    { key: 'count', label: 'Stock actif', render: group => populationButton(ctx, group, 'stock', countOf(group, 'count', 'rows'), activate) },
    { key: 'datedCount', label: 'Ouvertures valides', render: group => populationButton(ctx, group, 'dated', countOf(group, 'datedCount', 'datedRows'), activate) },
    { key: 'oldestOpenedAt', label: 'Plus ancienne ouverture', render: group => group.oldestOpenedAt ? formatDate(group.oldestOpenedAt) : 'Indisponible' },
    { key: 'maximumAgeDays', label: 'Âge maximal', render: group => ageLabel(group.maximumAgeDays, group.color) },
    { key: 'unrankedCount', label: 'Hors classement', render: group => dateQuality(ctx, group, activate) }
  ], { key: 'key', maxRows: Math.min(500, groups.length), label: 'Stock actif et plus anciennes ouvertures par groupe' }));
  const total = data.totalGroups ?? data.total ?? groups.length;
  if (Number.isInteger(total) && total > groups.length) root.append(caption(`${formatNumber(groups.length)} groupes affichés sur ${formatNumber(total)}. Le détail complet est accessible dans toutes les valeurs.`));
  root.append(caption(focus ? 'Classement unique du périmètre : les affectations multiples ne répètent pas une ligne source.'
    : 'Les stocks des groupes ne s’additionnent pas : une ligne multigroupe participe à chacun de ses groupes.'),
    details('Méthode de classement et couleur',
      caption(data.explanation || 'Classement des ouvertures valides par date et heure, puis identifiant numérique. Les dates absentes, invalides ou futures restent dans le stock et sont consultables séparément.'),
      caption(data.maximumAgeDays == null ? 'Aucun âge exploitable dans ce périmètre.'
        : `Échelle commune : 0 à ${formatNumber(data.maximumAgeDays)} jours calendaires. Couleur relative, sans seuil SLA ni état météo.`),
      toolbar(ageLabel(0, '#EBF5FB'), data.maximumAgeDays == null ? null : ageLabel(data.maximumAgeDays, data.maximumAgeDays === 0 ? '#EBF5FB' : '#FCB38A'))));
  return root;
}
