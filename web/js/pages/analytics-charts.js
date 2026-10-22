import { el, button, card, toolbar, badge, table, empty, caption, command, details, number, formatNumber, formatDate, svg, sparkline } from './analytics-ui.js';

const palette = ['var(--c1, #066ddb)', 'var(--c2, #8265c9)', 'var(--c3, #20877e)', 'var(--c4, #c45586)', 'var(--c5, #31748b)', 'var(--c6, #bf7334)', 'var(--c7, #9867ac)', 'var(--c8, #667b35)'];
function valueOf(bucket) { return bucket.isAggregate ? bucket.value : bucket.value ?? bucket.count; }
function clickable(node, callback, label) {
  node.setAttribute('tabindex', '0'); node.setAttribute('role', 'button'); node.setAttribute('aria-label', label);
  node.addEventListener('click', callback); node.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); callback(); } });
  return node;
}

export function widgetContent(ctx, widget, activate, more) {
  const setting = widget.settings; const data = widget.content;
  if (setting.kind === 'history') return el('div', { class: 'analytics-history-mount' }, sparkline(data));
  if (!data) return empty('Source indisponible', `La source « ${setting.sourceKey} » n’est pas calculable dans ce relevé. Modifiez le widget ou les correspondances de colonnes.`);
  const body = el('div', { class: `analytics-widget-body kind-${setting.kind}` });
  if (setting.kind === 'metric') {
    const unavailable = data.value == null || !Number.isFinite(data.value);
    const metric = el('button', { class: `analytics-metric-value${unavailable ? ' is-unavailable' : ''}`, text: unavailable ? '—' : number(data.value),
      disabled: unavailable, 'aria-label': unavailable ? 'Donnée indisponible' : undefined, title: unavailable ? 'Cette mesure ne peut pas être calculée sur ce relevé.' : 'Voir les lignes contributrices', onClick: () => activate({ kind: 'metric', key: data.key }) });
    body.append(metric, caption(unavailable ? 'Donnée indisponible' : data.unit), badge(data.sample == null ? `${formatNumber(data.count)} lignes contributrices` : `Échantillon : ${formatNumber(data.sample)}`));
    if (setting.presentation === 'gauge' && data.value != null) {
      const maximum = setting.targetValue ?? (data.unit === '%' ? 100 : null);
      if (maximum > 0) body.append(gauge(data.value, maximum, `Objectif ${number(maximum, data.unit)}`));
      else body.append(caption('Définissez un objectif positif pour afficher la jauge ; la valeur reste disponible.'));
    }
    if (data.value != null && setting.targetValue != null) body.append(el('progress', { value: Math.max(0, data.value), max: Math.max(setting.targetValue, data.value), 'aria-label': 'Valeur et objectif' }), caption(`Objectif : ${number(setting.targetValue, data.unit)}`));
    if (data.value != null && (setting.warningValue != null || setting.criticalValue != null)) {
      const exceeded = threshold => threshold != null && (setting.lowerIsBetter ? data.value >= threshold : data.value <= threshold);
      body.append(badge(exceeded(setting.criticalValue) ? 'Seuil critique atteint' : exceeded(setting.warningValue) ? 'Seuil de vigilance atteint' : 'Dans les seuils configurés', exceeded(setting.criticalValue) ? 'danger' : exceeded(setting.warningValue) ? 'warning' : 'success'));
    }
    if (setting.showMiniHistory) body.append(el('div', { class: 'analytics-mini-history', 'data-history-widget': widget.id }, caption('Historique : calcul des observations en attente.')));
    if (setting.showDescription) body.append(details('Définition et échantillon', caption(data.explanation)));
  } else if (setting.kind === 'distribution' || setting.kind === 'histogram') {
    body.append(distribution(ctx, data, setting, bucket => activate({ kind: setting.kind, key: data.key, label: bucket.label })));
    if (data.truncated) body.append(command(ctx, `Toutes les catégories (${formatNumber(data.totalBuckets)})`, () => more('distribution', data.key)));
  } else if (setting.kind === 'weather') {
    const states = { sun: 'Situation calme', watch: 'Vigilance', rain: 'Points à traiter', storm: 'Vigilance forte', partial: 'Lecture à compléter', unknown: 'Données insuffisantes' };
    body.append(el('div', { class: 'analytics-weather-state' }, el('span', { text: data.symbol, 'aria-hidden': 'true' }),
      el('div', {}, el('strong', { text: states[data.state] || data.title }), caption(data.score == null ? 'Couverture insuffisante pour un indice.' : `Indice descriptif ${number(data.score)} / 100`))));
    if (data.signals?.length) body.append(el('div', { class: 'analytics-weather-signals' }, ...data.signals.map(signal => el('div', {},
      el('strong', { text: formatNumber(signal.count) }), el('span', { text: signal.label }), el('small', { text: `sur ${formatNumber(signal.sample)}` })))));
    if (setting.showDescription) body.append(details('Méthode, échantillons et limites', caption(data.title), caption(data.description),
      table(data.signals, [{ key: 'label', label: 'Signal' }, { key: 'count', label: 'Tickets' }, { key: 'sample', label: 'Échantillon' }, { key: 'weight', label: 'Poids' }, { key: 'explanation', label: 'Définition' }], { maxRows: 30 })));
  } else if (setting.kind === 'timeline') {
    body.append(timeline(data));
  } else if (setting.kind === 'attention') {
    body.append(table(data.rows, [{ key: 'id', label: 'Ticket' }, { key: 'title', label: 'Titre' }, { key: 'status', label: 'Statut' }, { key: 'reasons', label: 'Points d’attention' }],
      { key: 'index', maxRows: 30, onSelect: row => ctx.navigate('tickets', { rowIndex: row.index, revision: ctx.analyticsRevision }) }));
    if (data.total > data.rows.length) body.append(command(ctx, `${formatNumber(data.total)} lignes à examiner`, () => more('attention', '')));
  } else if (setting.kind === 'quality') {
    for (const warning of data.warnings ?? []) body.append(caption(warning, 'warning'));
    body.append(table(data.columns, [{ key: 'label', label: 'Rôle' }, { key: 'header', label: 'Colonne observée' }, { key: 'state', label: 'Correspondance' }], { maxRows: 100 }));
  } else if (setting.kind === 'note') {
    body.append(el('p', { class: 'analytics-note', text: data.text }));
  } else if (setting.kind === 'matrix') {
    body.append(matrix(ctx, data, setting, cell => activate({ kind: 'matrix', key: data.key, label: cell.rowLabel, columnLabel: cell.columnLabel })));
    body.append(command(ctx, `Détail des ${formatNumber(data.total)} cellules`, () => more('matrix', data.key)));
  } else if (setting.kind === 'boxplot') {
    body.append(boxplot(ctx, data, box => activate({ kind: 'boxplot', key: data.key, label: box.label })));
    if (data.total > data.boxes.length) body.append(caption(`${data.boxes.length} groupes représentés sur ${data.total}.`, 'warning'), command(ctx, 'Tous les groupes statistiques', () => more('boxplot', data.key)));
  } else if (setting.kind === 'scatter') {
    body.append(scatter(ctx, data));
    if (data.total > data.points.length) body.append(command(ctx, `Table des ${formatNumber(data.total)} points`, () => more('scatter', data.key)));
  } else if (setting.kind === 'map') {
    body.append(geography(ctx, data, setting, point => activate({ kind: 'map', key: point.key, label: point.label, active: setting.sourceKey === 'active' })));
    if (data.total > data.points.length) body.append(command(ctx, `Table des ${formatNumber(data.total)} localisations`, () => more('map', '')));
  }
  return body;
}

function distribution(ctx, data, setting, selectBucket) {
  if (!data.available) return empty('Répartition indisponible', data.note);
  const buckets = data.buckets ?? [];
  const incompatible = buckets.some(b => b.isAggregate) && ['donut', 'pareto', 'stacked', 'gauge'].includes(setting.presentation);
  if (incompatible) setting = { ...setting, presentation: 'bars' };
  const panel = el('div', { class: `analytics-distribution presentation-${setting.presentation}` });
  if (incompatible) panel.append(caption('Ce résultat est une agrégation par groupe. Ses valeurs et unités sont affichées sans les additionner ni les convertir en parts d’un total.', 'warning'));
  const usable = buckets.filter(b => valueOf(b) != null && Number.isFinite(valueOf(b)));
  const positiveTotal = usable.reduce((sum, b) => sum + Math.max(0, valueOf(b)), 0);
  const max = Math.max(1, ...usable.map(b => Math.abs(valueOf(b))));
  if (setting.presentation === 'columns' || setting.presentation === 'pareto') panel.append(columnChart(data, setting, selectBucket));
  if (setting.presentation === 'heatmap') panel.append(el('div', { class: 'analytics-category-heatmap' }, ...buckets.map(bucket =>
    el('button', { class: 'analytics-category-heat', style: { '--heat': Math.max(.05, Math.abs(valueOf(bucket) ?? 0) / max) }, onClick: () => selectBucket(bucket) },
      el('span', { text: bucket.label }), el('strong', { text: number(valueOf(bucket), bucket.unit) })))));
  if (setting.presentation === 'stacked' && positiveTotal > 0) panel.append(el('div', { class: 'analytics-stacked-bar' }, ...usable.filter(b => valueOf(b) > 0).map((bucket, index) =>
    el('button', { style: { width: `${100 * valueOf(bucket) / positiveTotal}%`, background: palette[index % palette.length] }, onClick: () => selectBucket(bucket),
      title: `${bucket.label} : ${number(valueOf(bucket), bucket.unit)} · parts des valeurs positives affichées`, 'aria-label': `${bucket.label} : ${number(valueOf(bucket), bucket.unit)}`, text: bucket.label }))),
    caption('Empilage proportionnel des valeurs positives affichées. Le détail ci-dessous conserve les valeurs nulles ou négatives.'));
  if (setting.presentation === 'donut' && positiveTotal > 0) {
    const drawing = svg('svg', { viewBox: '0 0 200 200', role: 'img', 'aria-label': 'Parts dans le total des valeurs affichées' });
    let start = 0;
    for (const [index, bucket] of usable.entries()) {
      const amount = Math.max(0, valueOf(bucket)) / positiveTotal; if (!amount) continue;
      const segment = svg('circle', { cx: 100, cy: 100, r: 68, fill: 'none', stroke: palette[index % palette.length], 'stroke-width': 30,
        'stroke-dasharray': `${amount * 427.256} ${427.256 - amount * 427.256}`, 'stroke-dashoffset': -start * 427.256, transform: 'rotate(-90 100 100)' },
        svg('title', {}, `${bucket.label} : ${number(valueOf(bucket), bucket.unit)}`));
      drawing.append(clickable(segment, () => selectBucket(bucket), bucket.label)); start += amount;
    }
    panel.append(drawing, caption('Parts calculées sur les seules valeurs affichées ; le détail conserve les échantillons et unités.'));
  }
  const rows = setting.presentation === 'pareto' ? [...buckets].sort((a, b) => (valueOf(b) ?? -Infinity) - (valueOf(a) ?? -Infinity)) : buckets;
  let cumulative = 0;
  for (const [index, bucket] of rows.entries()) {
    const value = valueOf(bucket); cumulative += Math.max(0, value ?? 0);
    const item = el('button', { class: 'analytics-bar-row', onClick: () => selectBucket(bucket), title: `${bucket.label} · ${number(value, bucket.unit)} · ${bucket.count} lignes` },
      el('span', { class: 'analytics-bar-label', text: bucket.label }),
      el('span', { class: 'analytics-bar-track' }, el('span', { class: 'analytics-bar-fill', style: { width: `${value == null ? 0 : 100 * Math.abs(value) / max}%`, background: palette[index % palette.length] } })),
      el('strong', { text: number(value, bucket.unit) }));
    if (bucket.sample != null) item.append(el('small', { text: `n=${formatNumber(bucket.sample)}` }));
    if (setting.presentation === 'gauge' && positiveTotal > 0 && value != null) item.append(gauge(Math.max(0, value), positiveTotal, 'Part des valeurs positives affichées'));
    if (setting.presentation === 'pareto' && data.positiveTotal > 0) item.append(el('small', { text: `Cumul global ${number(100 * cumulative / data.positiveTotal, '%')} · cible ${setting.paretoThresholdPercent} %`, class: cumulative / data.positiveTotal * 100 >= setting.paretoThresholdPercent ? 'target-met' : '' }));
    panel.append(item);
  }
  if (!rows.length) panel.append(empty('Aucune catégorie dans ce périmètre'));
  panel.append(caption(`Dénominateur de la répartition : ${formatNumber(data.denominator)} lignes.`));
  if (setting.showDescription) panel.append(details('Définition', caption(data.note)));
  return panel;
}

function gauge(value, maximum, label) {
  const fraction = Math.max(0, Math.min(1, value / maximum));
  const drawing = svg('svg', { viewBox: '0 0 160 100', class: 'analytics-gauge', role: 'img', 'aria-label': `${number(value)} sur ${number(maximum)} · ${label}` },
    svg('path', { d: 'M20 80 A60 60 0 0 1 140 80', pathLength: 100, fill: 'none', stroke: 'var(--border)', 'stroke-width': 12 }),
    svg('path', { d: 'M20 80 A60 60 0 0 1 140 80', pathLength: 100, fill: 'none', stroke: 'var(--widget-accent, var(--accent))', 'stroke-width': 12, 'stroke-dasharray': `${100 * fraction} 100` }),
    svg('text', { x: 80, y: 72, 'text-anchor': 'middle', fill: 'currentColor', 'font-size': 13 }, `${number(100 * value / maximum, '%')}`));
  return el('div', { class: 'analytics-gauge-wrap' }, drawing, caption(label));
}

function columnChart(data, setting, selectBucket) {
  const buckets = setting.presentation === 'pareto' ? [...data.buckets].sort((a, b) => (valueOf(b) ?? -Infinity) - (valueOf(a) ?? -Infinity)) : data.buckets;
  const values = buckets.map(b => valueOf(b)).filter(Number.isFinite); const minimum = Math.min(0, ...values); const maximum = Math.max(1, ...values);
  const scale = value => 185 - (value - minimum) / (maximum - minimum) * 160;
  const drawing = svg('svg', { viewBox: '0 0 660 235', role: 'img', 'aria-label': setting.presentation === 'pareto' ? 'Valeurs décroissantes et courbe du cumul global positif' : 'Valeurs par catégorie' });
  const step = 590 / Math.max(1, buckets.length); const zero = scale(0); let cumulative = 0; const points = [];
  drawing.append(svg('line', { x1: 35, x2: 625, y1: zero, y2: zero, stroke: 'var(--border)' }));
  buckets.forEach((bucket, index) => {
    const value = valueOf(bucket); if (value == null || !Number.isFinite(value)) return;
    const x = 35 + index * step; const y = scale(value);
    const group = svg('g', {}, svg('rect', { x: x + step * .12, y: Math.min(zero, y), width: Math.max(1, step * .76), height: Math.max(.8, Math.abs(zero - y)), fill: palette[index % palette.length] }),
      svg('text', { x: x + step / 2, y: value >= 0 ? y - 5 : y + 13, 'text-anchor': 'middle', 'font-size': 10, fill: 'currentColor' }, number(value)),
      svg('text', { x: x + step / 2, y: 207, 'text-anchor': 'middle', 'font-size': 10, fill: 'currentColor' }, bucket.label.slice(0, Math.max(3, Math.floor(75 / Math.max(1, buckets.length))))),
      svg('title', {}, `${bucket.label} · ${number(value, bucket.unit)}`));
    drawing.append(clickable(group, () => selectBucket(bucket), `${bucket.label} : ${number(value, bucket.unit)}`));
    cumulative += Math.max(0, value); if (data.positiveTotal > 0) points.push(`${x + step / 2},${185 - cumulative / data.positiveTotal * 160}`);
  });
  if (setting.presentation === 'pareto' && data.positiveTotal > 0) {
    const threshold = 185 - setting.paretoThresholdPercent / 100 * 160;
    drawing.append(svg('line', { x1: 35, x2: 625, y1: threshold, y2: threshold, stroke: 'var(--warning)', 'stroke-dasharray': '6 4' }),
      svg('text', { x: 625, y: threshold - 5, 'text-anchor': 'end', 'font-size': 10, fill: 'var(--warning)' }, `${setting.paretoThresholdPercent} %`),
      svg('polyline', { points: points.join(' '), fill: 'none', stroke: 'var(--text)', 'stroke-width': 2.5 }));
  }
  return el('div', { class: 'analytics-column-chart' }, drawing, setting.presentation === 'pareto'
    ? caption(`Cumul sur le total des valeurs positives de toutes les catégories : ${number(data.positiveTotal)}. ${data.truncated ? 'Le graphique des premières catégories peut s’arrêter avant 100 %.' : 'Les valeurs négatives restent visibles sans entrer dans le cumul.'}`) : null);
}

function timeline(days) {
  const panel = el('div'); if (!days.length) return empty('Aucune date d’événement exploitable');
  const drawing = svg('svg', { viewBox: '0 0 660 180', role: 'img', 'aria-label': 'Dates d’ouverture, de résolution et de clôture présentes dans le relevé' });
  const maximum = Math.max(1, ...days.flatMap(d => [d.created, d.resolved, d.closed]));
  const width = 600 / days.length;
  days.forEach((day, index) => ['created', 'resolved', 'closed'].forEach((key, lane) => drawing.append(svg('rect', {
    x: 30 + index * width + lane * width / 3, y: 150 - 130 * day[key] / maximum, width: Math.max(.5, width / 3 - 1), height: 130 * day[key] / maximum,
    fill: palette[lane]
  }, svg('title', {}, `${formatDate(day.date)} · ${key === 'created' ? 'Ouvertures' : key === 'resolved' ? 'Résolutions' : 'Clôtures'} : ${day[key]}`)))));
  panel.append(drawing, caption('Ouvertures · Résolutions · Clôtures. Dates effectivement renseignées dans le relevé ; les tickets absents ne sont pas des résolutions.'),
    details('Valeurs datées', table(days, [{ key: 'date', label: 'Date', render: d => formatDate(d.date) }, { key: 'created', label: 'Ouvertures' }, { key: 'resolved', label: 'Résolutions' }, { key: 'closed', label: 'Clôtures' }], { maxRows: 366 })));
  return panel;
}

function matrix(ctx, data, setting, activate) {
  if (!data.available) return empty('Matrice indisponible', data.explanation);
  const panel = el('div'); const groups = new Map();
  for (const cell of data.cells) { if (!groups.has(cell.rowLabel)) groups.set(cell.rowLabel, []); groups.get(cell.rowLabel).push(cell); }
  const rowTotals = new Map((data.rowTotals ?? []).map(row => [row.label, row.value]));
  const maxRow = Math.max(1, data.maximumRowTotal ?? 0);
  if (setting.presentation === 'stacked') {
    const columnNames = [...new Set(data.cells.map(c => c.columnLabel))];
    for (const [label, cells] of groups) {
      const total = rowTotals.get(label) ?? 0;
      const row = el('div', { class: 'analytics-stack-row' }, el('span', { text: label })); const track = el('div', { class: 'analytics-stack' });
      for (const cell of cells) {
        const width = 100 * Math.max(0, cell.value) / Math.max(1, setting.stackedMode === 'percent' ? total : maxRow);
        const segment = el('button', { style: { width: `${width}%`, background: palette[columnNames.indexOf(cell.columnLabel) % palette.length] }, title: `${cell.columnLabel} : ${number(cell.value, data.unit)}`, onClick: () => activate(cell), text: width > 12 ? number(cell.value) : '' });
        segment.setAttribute('aria-label', `${label}, ${cell.columnLabel}, ${number(cell.value, data.unit)}`); track.append(segment);
      }
      row.append(track, el('strong', { text: number(total, data.unit) })); panel.append(row);
    }
    panel.append(caption(setting.stackedMode === 'percent' ? 'Le total complet de chaque ligne représente 100 %. Les cellules non affichées ne sont pas redistribuées.' : 'Toutes les lignes utilisent la même échelle de volume, calculée sur la matrice complète.'));
  } else {
    const maximum = Math.max(1, ...data.cells.map(c => c.value));
    const columns = [...new Set(data.cells.map(c => c.columnLabel))]; const rowLabels = [...groups.keys()];
    const displayedRows = rowLabels.slice(0, 40); const displayedColumns = columns.slice(0, 30);
    panel.append(table(displayedRows.map(label => ({ label })), [{ key: 'label', label: 'Ligne / colonne' }, ...displayedColumns.map((name, index) => ({ key: `column-${index}`, label: name,
      render: row => {
        const cell = groups.get(row.label).find(c => c.columnLabel === name);
        return cell ? el('button', { class: 'analytics-heat-cell', style: { '--heat': Math.max(.08, cell.value / maximum) }, text: number(cell.value),
          title: `${row.label} · ${name} · ${number(cell.value, data.unit)} · ${cell.count} lignes`, onClick: () => activate(cell) }) : el('span', { text: '—', title: 'Aucune cellule fournie dans cette sélection ; consulter le détail complet.' });
      } }))], { maxRows: 40 }));
    if (rowLabels.length > 40 || columns.length > 30) panel.append(caption(`Vue croisée limitée à 40 lignes et 30 colonnes. Le détail paginé donne accès à toutes les cellules.`, 'warning'));
  }
  if (data.total > data.cells.length) panel.append(caption(`Affichage limité à ${data.cells.length} cellules sur ${data.total}. Les totaux des empilements incluent toutes les cellules du calcul.`, 'warning'));
  if (setting.showDescription) panel.append(details('Méthode et unité', caption(data.explanation)));
  return panel;
}

function boxplot(ctx, data, activate) {
  if (!data.available || !data.boxes.length) return empty('Échantillons insuffisants', data.explanation);
  const panel = el('div'); const low = Math.min(...data.boxes.map(b => b.minimum)); const high = Math.max(...data.boxes.map(b => b.maximum));
  const x = value => 180 + 390 * (value - low) / Math.max(1, high - low);
  const drawing = svg('svg', { viewBox: `0 0 620 ${data.boxes.length * 44 + 20}`, role: 'img', 'aria-label': 'Boîtes à moustaches : minimum, quartiles, médiane et maximum observés' });
  data.boxes.forEach((box, index) => {
    const y = 25 + index * 44; const group = svg('g', {}, svg('text', { x: 3, y: y + 4, 'font-size': 12, fill: 'currentColor' }, box.label.slice(0, 24)),
      svg('line', { x1: x(box.minimum), x2: x(box.maximum), y1: y, y2: y, stroke: 'currentColor' }),
      svg('rect', { x: x(box.firstQuartile), y: y - 11, width: Math.max(1, x(box.thirdQuartile) - x(box.firstQuartile)), height: 22, fill: palette[0], opacity: .35, stroke: 'currentColor' }),
      svg('line', { x1: x(box.median), x2: x(box.median), y1: y - 13, y2: y + 13, stroke: 'currentColor', 'stroke-width': 2 }),
      svg('title', {}, `${box.label} · n=${box.sample} · médiane ${number(box.median, data.unit)}`));
    drawing.append(clickable(group, () => activate(box), box.label));
  });
  panel.append(drawing, details('Valeurs et échantillons', caption(data.explanation), table(data.boxes, [
    { key: 'label', label: 'Groupe' }, { key: 'sample', label: 'n' }, ...['minimum', 'firstQuartile', 'median', 'thirdQuartile', 'maximum', 'p90'].map(key => ({ key, label: ({ minimum: 'Min', firstQuartile: 'Q1', median: 'Médiane', thirdQuartile: 'Q3', maximum: 'Max', p90: 'P90' })[key], render: b => number(b[key], data.unit) }))
  ], { maxRows: 30, onSelect: activate })));
  return panel;
}

function scatter(ctx, data) {
  if (!data.points.length) return empty('Aucun couple de valeurs exploitable', data.explanation);
  const xmin = Math.min(...data.points.map(p => p.x)); const xmax = Math.max(...data.points.map(p => p.x));
  const ymin = Math.min(...data.points.map(p => p.y)); const ymax = Math.max(...data.points.map(p => p.y));
  const drawing = svg('svg', { viewBox: '0 0 650 330', role: 'img', 'aria-label': `${data.xLabel} / ${data.yLabel}. ${data.points.length} points affichés.` });
  drawing.append(svg('path', { d: 'M45 20V280H625', fill: 'none', stroke: 'currentColor' }), svg('text', { x: 300, y: 315, fill: 'currentColor', 'font-size': 12 }, data.xLabel),
    svg('text', { x: 50, y: 16, fill: 'currentColor', 'font-size': 12 }, data.yLabel));
  for (const point of data.points) {
    const dot = svg('circle', { cx: 50 + 560 * (point.x - xmin) / Math.max(1, xmax - xmin), cy: 275 - 245 * (point.y - ymin) / Math.max(1, ymax - ymin), r: 4, fill: palette[0], opacity: .65 },
      svg('title', {}, `${point.label} : ${point.x} / ${point.y}`));
    drawing.append(clickable(dot, () => ctx.navigate('tickets', { rowIndex: point.row, revision: ctx.analyticsRevision }), point.label));
  }
  return el('div', {}, drawing, caption(`${data.points.length} points sur ${formatNumber(data.total)}. ${data.explanation}`), caption(`X : ${number(xmin)} → ${number(xmax)} · Y : ${number(ymin)} → ${number(ymax)}`));
}

function geography(ctx, data, setting, activate) {
  const points = data.points.filter(p => setting.sourceKey !== 'active' || p.active > 0);
  if (!points.length) return empty('Aucune coordonnée disponible', 'Renseignez des coordonnées de ville ou de site dans le référentiel. Aucune adresse n’est envoyée à un service externe.');
  const panel = el('div', { class: 'analytics-geography' });
  const xmin = Math.min(...points.map(p => p.longitude)); const xmax = Math.max(...points.map(p => p.longitude));
  const ymin = Math.min(...points.map(p => p.latitude)); const ymax = Math.max(...points.map(p => p.latitude));
  const xpad = Math.max(.02, (xmax - xmin) * .1); const ypad = Math.max(.02, (ymax - ymin) * .1);
  const width = xmax - xmin + 2 * xpad; const height = ymax - ymin + 2 * ypad;
  const drawing = svg('svg', { viewBox: '0 0 640 360', role: 'img', 'aria-label': 'Carte locale des coordonnées fournies, sans fond de carte ni service externe' });
  for (let i = 1; i < 5; i++) drawing.append(svg('line', { x1: 20 + i * 120, x2: 20 + i * 120, y1: 20, y2: 330, stroke: 'var(--border, #d7dee9)', 'stroke-dasharray': '4 4' }),
    svg('line', { x1: 20, x2: 620, y1: 20 + i * 62, y2: 20 + i * 62, stroke: 'var(--border, #d7dee9)', 'stroke-dasharray': '4 4' }));
  const max = Math.max(1, ...points.map(p => setting.sourceKey === 'active' ? p.active : p.count));
  for (const point of points) {
    const count = setting.sourceKey === 'active' ? point.active : point.count;
    const dot = svg('circle', { cx: 20 + (point.longitude - xmin + xpad) / width * 600, cy: 330 - (point.latitude - ymin + ypad) / height * 310,
      r: 5 + 13 * Math.sqrt(count / max), fill: palette[0], 'fill-opacity': .65, stroke: 'var(--text, #17243a)' },
      svg('title', {}, `${point.label} · ${count} tickets · ${point.latitude}, ${point.longitude}`));
    drawing.append(clickable(dot, () => activate(point), `${point.label}, ${count} tickets`));
  }
  let zoom = 1; let dx = 0; let dy = 0;
  const update = () => drawing.setAttribute('viewBox', `${320 - 320 / zoom + dx} ${180 - 180 / zoom + dy} ${640 / zoom} ${360 / zoom}`);
  panel.append(toolbar(button('−', () => { zoom = Math.max(1, zoom / 1.4); update(); }, { title: 'Dézoomer' }),
    button('+', () => { zoom = Math.min(8, zoom * 1.4); update(); }, { title: 'Zoomer' }),
    button('←', () => { dx -= 60 / zoom; update(); }), button('→', () => { dx += 60 / zoom; update(); }),
    button('↑', () => { dy -= 40 / zoom; update(); }), button('↓', () => { dy += 40 / zoom; update(); }),
    button('Recentrer', () => { zoom = 1; dx = dy = 0; update(); })), drawing,
    caption(`Longitude ${number(xmin)} → ${number(xmax)} · latitude ${number(ymin)} → ${number(ymax)}. Coordonnées fournies, sans géocodage ni transmission.`),
    details('Localisations et coordonnées', table(points, [{ key: 'label', label: 'Lieu' }, { key: 'latitude', label: 'Latitude' }, { key: 'longitude', label: 'Longitude' }, { key: 'count', label: 'Tickets' }, { key: 'active', label: 'Actifs' }], { key: 'key', onSelect: activate, maxRows: 2000 })));
  return panel;
}
