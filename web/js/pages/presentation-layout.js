import { el, caption, formatDate, formatNumber } from './analytics-ui.js';

export const slideWidth = 1280;
export const slideHeight = 720;
export const newId = () => crypto.randomUUID().replaceAll('-', '');
export const copy = value => structuredClone(value);
const bounded = (value, min, max) => Math.min(max, Math.max(min, Math.round(value)));

export function geometry(item) {
  const width = bounded(Number(item.width) || 480, 80, 1000);
  const height = bounded(Number(item.height) || 460, 80, 1000);
  return { ...item, x: bounded(Number(item.x) || 0, 0, 1000 - width), y: bounded(Number(item.y) || 0, 0, 1000 - height), width, height };
}

export function positionItem(node, item) {
  node.style.left = `${item.x / 10}%`; node.style.top = `${item.y / 10}%`;
  node.style.width = `${item.width / 10}%`; node.style.height = `${item.height / 10}%`;
}

export function arrangeSlide(slide, catalog) {
  const items = slide.items, metrics = items.filter(item => ['metric', 'weather'].includes(catalog.get(item.widgetId)?.setting.kind));
  const analyses = items.filter(item => !metrics.includes(item));
  if (metrics.length > 0 && metrics.length <= 4 && analyses.length > 0 && analyses.length <= 2) {
    const width = Math.floor((1000 - 20 * (metrics.length - 1)) / metrics.length);
    const topHeight = metrics.length >= 3 ? 460 : 290;
    metrics.forEach((item, i) => Object.assign(item, { x: i * (width + 20), y: 0, width, height: topHeight }));
    const chartWidth = Math.floor((1000 - 20 * (analyses.length - 1)) / analyses.length);
    analyses.forEach((item, i) => Object.assign(item, { x: i * (chartWidth + 20), y: topHeight + 25, width: chartWidth, height: 975 - topHeight }));
    return;
  }
  const columns = items.length <= 1 ? 1 : items.length <= 4 ? 2 : items.length <= 9 ? 3 : 4;
  const rows = Math.max(1, Math.ceil(items.length / columns));
  const width = Math.floor((1000 - 20 * (columns - 1)) / columns), height = Math.floor((1000 - 20 * (rows - 1)) / rows);
  items.forEach((item, i) => Object.assign(item, { x: (i % columns) * (width + 20), y: Math.floor(i / columns) * (height + 20), width, height }));
}

export function initialDeck(available) {
  const catalog = new Map(available.map(item => [item.id, item]));
  const metrics = available.filter(item => ['metric', 'weather'].includes(item.setting.kind)).slice(0, 4);
  const analyses = available.filter(item => !['metric', 'weather', 'note', 'history'].includes(item.setting.kind)).slice(0, 2);
  const selected = [...metrics, ...analyses];
  if (!selected.length) selected.push(...available.slice(0, 4));
  const slide = { id: newId(), title: 'Vue d’ensemble', items: selected.map(item => ({ id: newId(), widgetId: item.id, x: 0, y: 0, width: 480, height: 460 })) };
  arrangeSlide(slide, catalog);
  return { version: 1, slides: [slide] };
}

export function groupedDeck(available) {
  const catalog = new Map(available.map(item => [item.id, item])), slides = [];
  const groups = new Map();
  for (const item of available) {
    const key = `${item.section}\n${['metric', 'weather'].includes(item.setting.kind) ? 'metrics' : 'analyses'}`;
    if (!groups.has(key)) groups.set(key, []); groups.get(key).push(item);
  }
  for (const items of groups.values()) {
    const limit = ['metric', 'weather'].includes(items[0].setting.kind) ? 6 : 2;
    for (let start = 0; start < items.length; start += limit) {
      const slide = { id: newId(), title: items[0].section + (items.length > limit ? ` · ${Math.floor(start / limit) + 1}` : ''),
        items: items.slice(start, start + limit).map(item => ({ id: newId(), widgetId: item.id, x: 0, y: 0, width: 480, height: 460 })) };
      arrangeSlide(slide, catalog); slides.push(slide);
    }
  }
  if (slides.length > 40) throw new Error('La composition dépasse 40 diapos. Choisissez les widgets à présenter.');
  return { version: 1, slides: slides.length ? slides : [{ id: newId(), title: 'Vue d’ensemble', items: [] }] };
}

export function widgetIds(deck) { return [...new Set(deck.slides.flatMap(slide => slide.items.map(item => item.widgetId)))]; }

let cloneSequence = 0;
export function passiveWidget(original) {
  const copy = original.cloneNode(true);
  const prefix = `pilot-presentation-${++cloneSequence}-`;
  const svgIds = new Map([...copy.querySelectorAll('svg [id],svg[id]')].map(node => [node.id, `${prefix}${node.id}`]));
  const remap = value => value.replace(/url\(\s*(['"]?)#([^\s)'"\(]+)\1\s*\)/g,
    (reference, quote, id) => svgIds.has(id) ? `url(#${svgIds.get(id)})` : reference);
  copy.classList.add('presentation-widget'); copy.classList.remove('is-fixed-height', 'is-grid-moving');
  copy.removeAttribute('id');
  // The slide owns its geometry. Desktop canvas coordinates and measured widths
  // must not offset or clip the cloned visual in the editor, projection or PDF.
  for (const property of ['--widget-height', 'position', 'left', 'top', 'right', 'bottom', 'width', 'height', 'transform', 'grid-column', 'grid-row'])
    copy.style.removeProperty(property);
  copy.removeAttribute('data-grid-column'); copy.removeAttribute('data-grid-row');
  copy.removeAttribute('data-reading-size'); copy.removeAttribute('data-reading-limited');
  for (const row of copy.querySelectorAll('[data-reading-hidden]')) { row.hidden = false; row.removeAttribute('data-reading-hidden'); }
  copy.querySelector('.analytics-widget-heading')?.remove();
  copy.querySelectorAll('.toolbar,.no-print,input,select,textarea,[data-grid-move],[data-grid-size],[data-grid-resize],.workspace-grid-preview,.workspace-grid-ghost,.domenia-orbs,.domenia-glow-ring,.domenia-spotlight,.domenia-widget-light,.domenia-cursor-halo,.domenia-ripple').forEach(node => node.remove());
  for (const node of copy.querySelectorAll('button,a')) {
    const span = document.createElement('span'); span.className = node.className;
    span.classList.remove('button', 'primary'); span.classList.add('presentation-passive');
    span.style.cssText = node.style.cssText; span.title = node.title; span.append(...node.childNodes); node.replaceWith(span);
  }
  for (const node of [copy, ...copy.querySelectorAll('*')]) {
    [...node.classList].filter(name => name.startsWith('domenia-')).forEach(name => node.classList.remove(name));
    [...node.attributes].filter(attribute => attribute.name.startsWith('data-motion-')).forEach(attribute => node.removeAttribute(attribute.name));
    if (node.namespaceURI === 'http://www.w3.org/2000/svg' && svgIds.has(node.id)) node.id = svgIds.get(node.id);
    else node.removeAttribute('id');
    node.removeAttribute('tabindex'); node.removeAttribute('contenteditable');
    if (node.getAttribute('role') === 'button') node.removeAttribute('role');
    for (const attribute of [...node.attributes]) {
      if (attribute.name.startsWith('on')) { node.removeAttribute(attribute.name); continue; }
      let value = remap(attribute.value);
      if ((attribute.name === 'href' || attribute.name === 'xlink:href') && value.startsWith('#') && svgIds.has(value.slice(1))) value = `#${svgIds.get(value.slice(1))}`;
      if (value !== attribute.value) node.setAttribute(attribute.name, value);
    }
    if (node.localName === 'style') node.textContent = remap(node.textContent);
  }
  for (const note of copy.querySelectorAll('.analytics-caption')) {
    if (note.childElementCount) continue;
    note.textContent = note.textContent.replace(/(?:Cliquer un groupe|Ouvrir le périmètre) pour consulter ses (\d+) plus anciennes ouvertures\./,
      'Les $1 plus anciennes ouvertures figurent dans l’export détaillé.');
  }
  return copy;
}

export function slideCanvas(slide, catalog, context, { editable = false, number = 1, total = 1 } = {}) {
  const area = el('div', { class: 'presentation-canvas-content', 'data-presentation-area': '' });
  const canvas = el('section', { class: 'presentation-canvas', 'data-presentation-canvas': slide.id, 'aria-label': slide.title || `Diapo ${number}` },
    el('header', { class: 'presentation-canvas-heading' },
      el('p', { class: 'presentation-canvas-eyebrow', text: context.boardName || 'GLPI PILOT' }),
      el('h2', { text: slide.title || `Diapo ${number}` })), area,
    el('footer', { class: 'presentation-canvas-footer' },
      el('span', { text: context.scopeText || 'Périmètre du tableau', title: `${context.scopeText || ''}\n${context.filterText || ''}` }),
      el('span', { text: `${context.sourceKind} · ${context.observedLabel || formatDate(context.observed)} · ${number} / ${total}` })));
  for (const item of slide.items) {
    const entry = catalog.get(item.widgetId), missing = !entry;
    const title = entry?.title || 'Widget indisponible';
    const frame = el('article', { class: `presentation-item${missing ? ' is-missing' : ''}`, tabIndex: editable ? 0 : -1,
      'data-placement-id': item.id, 'data-widget-id': item.widgetId, 'data-presented-widget': item.widgetId, 'aria-label': title },
      el('div', { class: 'presentation-item-heading', 'data-presentation-drag': editable ? item.id : null },
        el('strong', { class: 'presentation-item-title', text: title }), editable ? el('span', { class: 'presentation-item-grip', text: '⠿', 'aria-hidden': 'true' }) : null),
      el('div', { class: 'presentation-item-content' }, entry ? passiveWidget(entry.node) : caption('Ce widget a été retiré ou masqué. Supprimez cet emplacement ou choisissez un autre widget.')));
    positionItem(frame, item);
    if (editable) for (const handle of ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w']) frame.append(el('span', {
      class: `presentation-resize presentation-resize-${handle}`, 'data-presentation-resize': handle,
      role: 'presentation', title: 'Redimensionner le widget', 'aria-hidden': 'true' }));
    area.append(frame);
  }
  if (!slide.items.length) area.append(el('div', { class: 'presentation-empty-canvas' },
    el('strong', { text: 'Une page pour vos chiffres' }), caption('Ajoutez un widget depuis la liste à droite, puis déplacez-le et ajustez sa taille.')));
  return canvas;
}

export function fitCanvas(host, viewport, canvas) {
  let lastScale = 1;
  const resize = () => {
    const width = Math.max(1, host.clientWidth - 24), height = Math.max(1, host.clientHeight - 24);
    lastScale = Math.max(.05, Math.min(width / slideWidth, height / slideHeight));
    viewport.style.width = `${slideWidth * lastScale}px`; viewport.style.height = `${slideHeight * lastScale}px`;
    canvas.style.transform = `scale(${lastScale})`;
  };
  const observer = new ResizeObserver(resize); observer.observe(host); requestAnimationFrame(resize);
  return { dispose: () => observer.disconnect(), scale: () => lastScale, resize };
}

export function slideThumbnail(slide, index, active, select) {
  const miniature = el('div', { class: 'presentation-miniature', 'aria-hidden': 'true' },
    el('span', { class: 'presentation-miniature-title', text: slide.title || `Diapo ${index + 1}` }));
  const area = el('div', { class: 'presentation-miniature-content' });
  slide.items.forEach(item => { const block = el('i'); positionItem(block, item); area.append(block); }); miniature.append(area);
  return el('button', { type: 'button', class: 'presentation-slide-thumb', 'data-slide-id': slide.id,
    'aria-current': active ? 'page' : 'false', 'aria-label': `Diapo ${index + 1} · ${slide.title || 'Sans titre'}`, onClick: select },
    miniature, el('span', { class: 'presentation-thumb-caption' }, el('strong', { text: `${index + 1}. ${slide.title || 'Sans titre'}` }),
      el('small', { text: `${formatNumber(slide.items.length)} widget${slide.items.length > 1 ? 's' : ''}` })));
}
