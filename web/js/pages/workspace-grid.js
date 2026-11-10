// Presentation geometry only. Counts, permissions and persistence remain in C#.
export const GRID_COLUMNS = 12;
export const GRID_STEP = 20;
export const GRID_GAP = 16;
export const GRID_MAX_ROW = 10000;
export const MIN_WIDGET_HEIGHT = 40;
export const MIN_RESIZE_HEIGHT = 160;
export const MAX_WIDGET_HEIGHT = 960;
export const MAX_WIDGET_ROWS = Math.floor((MAX_WIDGET_HEIGHT + GRID_GAP) / GRID_STEP);
// Tallest height that still lands on the lattice: a card asked for more is
// shortened by one row rather than left breaking the gap with the next row.
export const MAX_SNAP_HEIGHT = MAX_WIDGET_ROWS * GRID_STEP - GRID_GAP;
export const WIDGET_SIZES = Object.freeze({
  small: Object.freeze({ label: 'Petit', span: 4, height: 220 }),
  medium: Object.freeze({ label: 'Moyen', span: 6, height: 360 }),
  large: Object.freeze({ label: 'Grand', span: 12, height: 480 })
});
const clamp = (n, a, b) => Math.min(b, Math.max(a, n));
export const effectiveGridSpan = (span, width) => width < 640 ? 12 : width < 960 ? Math.max(6, span) : span;
export const gridRows = height => Math.ceil((height + GRID_GAP) / GRID_STEP);
// One lattice for both the preview and the committed card: a card spanning h rows
// is painted gridHeight(h) tall, so its bottom always stops one gap above the next row.
export const gridHeight = rows => rows * GRID_STEP - GRID_GAP;
export const widgetRows = (height, maximum = MAX_WIDGET_HEIGHT) =>
  gridRows(clamp(Math.round(Number.isFinite(height) ? height : 320), MIN_WIDGET_HEIGHT, maximum));
export const snapGridHeight = (height, minimum = MIN_WIDGET_HEIGHT) =>
  gridHeight(clamp(widgetRows(height, MAX_WIDGET_HEIGHT), widgetRows(minimum), MAX_WIDGET_ROWS));
export const gridOverlap = (a, b) => a.x < b.x + b.w && a.x + a.w > b.x && a.y < b.y + b.h && a.y + a.h > b.y;
const validPosition = w => Number.isInteger(w.gridX) && Number.isInteger(w.gridY)
  && w.gridX >= 0 && w.gridX <= GRID_COLUMNS - w.span && w.gridY >= 0 && w.gridY <= GRID_MAX_ROW;

function freeAtColumn(rect, occupied) {
  let y = rect.y;
  while (y <= GRID_MAX_ROW) {
    const candidate = { ...rect, y };
    const collisions = occupied.filter(other => gridOverlap(candidate, other));
    if (!collisions.length) return candidate;
    y = Math.max(...collisions.map(other => other.y + other.h));
  }
  throw new Error('La disposition dépasse la hauteur maximale. Rapprochez les cartes ou ajoutez une section.');
}
// A narrow window is a projection, never a rewrite of the saved desktop layout.
// Stored empty spaces are retained. Automatic cards follow aligned rows in their
// original order, rather than climbing into holes below shorter neighbours.
export function arrangeWorkspaceWidgets(widgets, width, heights = new Map(), maximumMeasuredHeight = 960) {
  const visible = widgets.filter(w => !w.hidden);
  const raw = visible.map(w => ({ id: w.id, locked: !!w.locked,
    x: 0, y: 0, w: clamp(effectiveGridSpan(clamp(w.span || 4, 3, 12), width), 3, 12),
    h: widgetRows(heights.get(w.id) ?? w.height ?? 320, maximumMeasuredHeight) }));
  const occupied = [], byId = new Map();
  // A locked card gets first claim on its position when a legacy layout is repaired.
  for (const w of [...visible.filter(w => w.locked), ...visible.filter(w => !w.locked)]) {
    if (!validPosition(w)) continue;
    const rect = raw.find(r => r.id === w.id);
    const placed = freeAtColumn({ ...rect, x: Math.min(w.gridX, GRID_COLUMNS - rect.w), y: w.gridY }, occupied);
    occupied.push(placed); byId.set(w.id, placed);
  }
  let floor = 0, column = 0, row = [];
  const flushRow = () => {
    if (!row.length) return;
    while (floor <= GRID_MAX_ROW) {
      const placed = row.map(rect => ({ ...rect, y: floor }));
      const collisions = occupied.filter(other => placed.some(rect => gridOverlap(rect, other)));
      if (!collisions.length) {
        for (const rect of placed) { occupied.push(rect); byId.set(rect.id, rect); }
        floor += Math.max(...placed.map(rect => rect.h)); row = []; column = 0; return;
      }
      floor = Math.max(...collisions.map(rect => rect.y + rect.h));
    }
    throw new Error('La disposition dépasse la hauteur maximale. Ajoutez une section.');
  };
  for (const w of visible) {
    if (byId.has(w.id)) continue;
    const rect = raw.find(r => r.id === w.id);
    if (w.startNewRow || column + rect.w > GRID_COLUMNS) flushRow();
    if (w.startNewRow) floor = Math.max(floor, 0, ...occupied.map(r => r.y + r.h));
    row.push({ ...rect, x: column }); column += rect.w;
  }
  flushRow();
  return visible.map(w => byId.get(w.id));
}

// Put the selected card exactly at the requested cell. Only colliding unlocked
// cards move down; unrelated cards, locked cards and deliberate gaps stay put.
export function planWorkspaceDrop(placements, moving) {
  if (!moving || moving.locked || !Number.isInteger(moving.x) || !Number.isInteger(moving.y)
    || !Number.isInteger(moving.w) || !Number.isInteger(moving.h)
    || moving.w < 3 || moving.w > 12 || moving.h < 1 || moving.h > gridRows(960)
    || moving.x < 0 || moving.x + moving.w > 12 || moving.y < 0 || moving.y > GRID_MAX_ROW) return null;
  const others = placements.filter(r => r.id !== moving.id);
  const locked = others.filter(r => r.locked);
  if (locked.some(r => gridOverlap(r, moving))) return null;
  const occupied = [...locked.map(r => ({ ...r })), { ...moving }];
  for (const rect of others.filter(r => !r.locked).sort((a, b) => a.y - b.y || a.x - b.x || a.id.localeCompare(b.id)))
    occupied.push(freeAtColumn({ ...rect }, occupied));
  return occupied;
}

// A card keeps its preset name after its height has been snapped to the lattice.
export function presetForWidget(widget) {
  if (!widget || widget.autoHeight) return '';
  const height = snapGridHeight(widget.height, MIN_RESIZE_HEIGHT);
  return Object.entries(WIDGET_SIZES).find(([, size]) => widget.span === size.span
    && snapGridHeight(size.height, MIN_RESIZE_HEIGHT) === height)?.[0] || '';
}

// The drag preview and the saved card are computed here, once, from grid-relative
// pointer coordinates. The view only paints what these two functions return.
export function previewMoveRect({ rect, span, width, pointerX, pointerY, offsetRatio, offsetY }) {
  const w = clamp(effectiveGridSpan(clamp(span || 4, 3, GRID_COLUMNS), width), 3, GRID_COLUMNS);
  const step = (width + GRID_GAP) / GRID_COLUMNS;
  const x = clamp(Math.round((pointerX - offsetRatio * (w * step - GRID_GAP)) / step), 0, GRID_COLUMNS - w);
  const y = clamp(Math.round((pointerY - offsetY) / GRID_STEP), 0, GRID_MAX_ROW);
  return { ...rect, w, x, y };
}

export function previewResizeRect({ rect, span, width, startX, startY, pointerX, pointerY, startHeight }) {
  const step = (width + GRID_GAP) / GRID_COLUMNS;
  const columns = clamp((span || rect.w || 4) + Math.round((pointerX - startX) / step), 3, GRID_COLUMNS);
  const height = snapGridHeight(startHeight + (pointerY - startY), MIN_RESIZE_HEIGHT);
  const projected = effectiveGridSpan(columns, width);
  return { moving: { ...rect, x: Math.min(rect.x, GRID_COLUMNS - projected), w: projected, h: gridRows(height) },
    size: { span: columns, height } };
}
