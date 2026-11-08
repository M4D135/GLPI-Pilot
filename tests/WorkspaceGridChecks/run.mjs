import test from 'node:test';
import assert from 'node:assert/strict';
import { arrangeWorkspaceWidgets, planWorkspaceDrop, gridOverlap, gridRows, gridHeight, snapGridHeight,
  widgetRows, previewMoveRect, previewResizeRect, GRID_COLUMNS, GRID_STEP, GRID_GAP,
  MIN_RESIZE_HEIGHT, MAX_WIDGET_HEIGHT, MAX_SNAP_HEIGHT, WIDGET_SIZES, presetForWidget, effectiveGridSpan } from '../../web/js/pages/workspace-grid.js';
// Keep the lifecycle regressions in the existing mandatory Windows Node gate.
import './terrain-panel-close.mjs';
const widget = (id, extra = {}) => ({ id, span: 4, height: 220, autoHeight: false, hidden: false, locked: false, ...extra });
const snapshot = value => JSON.stringify(value);
function disjoint(rects) {
  for (let i = 0; i < rects.length; i++) {
    const a = rects[i]; assert.ok(a.x >= 0 && a.x + a.w <= 12 && a.y >= 0 && a.y <= 10000);
    for (let j = i + 1; j < rects.length; j++) assert.equal(gridOverlap(a, rects[j]), false, `${a.id} overlaps ${rects[j].id}`);
  }
}
test('three explicit independent size presets', () => {
  assert.deepEqual(Object.keys(WIDGET_SIZES), ['small', 'medium', 'large']);
  for (const [name, size] of Object.entries(WIDGET_SIZES)) assert.equal(presetForWidget(widget(name, size)), name);
  assert.equal(presetForWidget(widget('auto', { autoHeight: true })), '');
});
test('a tall right-hand card cannot stretch the two small indicators', () => {
  const items = [widget('a'), widget('b'), widget('large', { height: 800 })];
  const rects = arrangeWorkspaceWidgets(items, 1200);
  assert.deepEqual(rects.map(r => r.h), [gridRows(220), gridRows(220), gridRows(800)]);
  assert.deepEqual(rects.map(r => r.x), [0, 4, 8]); disjoint(rects);
});
test('measured content has its own height, and hidden cards are not laid out', () => {
  const rects = arrangeWorkspaceWidgets([widget('a'), widget('b'), widget('hidden', { hidden: true })], 1200, new Map([['a', 180], ['b', 700]]));
  assert.equal(rects.length, 2); assert.equal(rects[0].h, gridRows(180)); assert.equal(rects[1].h, gridRows(700)); disjoint(rects);
});
test('legacy automatic layouts remain deterministic and unchanged in storage', () => {
  const items = [widget('a'), widget('b'), widget('c', { startNewRow: true }), widget('d')], before = snapshot(items);
  const a = arrangeWorkspaceWidgets(items, 1200), b = arrangeWorkspaceWidgets(items, 1200);
  assert.deepEqual(a, b); assert.ok(a[2].y >= a[0].h); assert.equal(snapshot(items), before); disjoint(a);
});
test('explicit empty rows and horizontal gaps survive a reload', () => {
  const items = [widget('a', { gridX: 8, gridY: 30 }), widget('b', { gridX: 0, gridY: 0 })];
  const rects = arrangeWorkspaceWidgets(items, 1200);
  assert.equal(rects[0].x, 8); assert.equal(rects[0].y, 30); assert.equal(rects[1].y, 0); disjoint(rects);
});
test('responsive widths never rewrite desktop positions or sizes', () => {
  const items = [widget('a', { gridX: 0, gridY: 0 }), widget('b', { gridX: 4, gridY: 0 }), widget('c', { gridX: 8, gridY: 0 })];
  const before = snapshot(items), desktop = arrangeWorkspaceWidgets(items, 1200);
  for (const width of [350, 639, 640, 800, 959, 960, 1200]) disjoint(arrangeWorkspaceWidgets(items, width));
  assert.deepEqual(arrangeWorkspaceWidgets(items, 1200), desktop); assert.equal(snapshot(items), before);
});
test('placing into an empty cell does not shuffle unrelated cards', () => {
  const rects = arrangeWorkspaceWidgets([widget('a'), widget('b')], 1200), before = snapshot(rects);
  const plan = planWorkspaceDrop(rects, { ...rects[0], x: 8, y: 20 });
  assert.deepEqual(plan.find(r => r.id === 'b'), rects[1]); assert.equal(plan.find(r => r.id === 'a').y, 20);
  assert.equal(snapshot(rects), before); disjoint(plan);
});
test('collisions push only unlocked cards down and preserve all sizes', () => {
  const rects = arrangeWorkspaceWidgets([widget('a'), widget('b'), widget('c')], 1200);
  const plan = planWorkspaceDrop(rects, { ...rects[0], x: 4, y: 0 });
  assert.equal(plan.find(r => r.id === 'b').y, rects[0].h); assert.deepEqual(plan.find(r => r.id === 'c'), rects[2]);
  for (const r of plan) assert.equal(r.h, rects.find(old => old.id === r.id).h); disjoint(plan);
});
test('a locked destination rejects the move without changing either layout', () => {
  const rects = arrangeWorkspaceWidgets([widget('locked', { gridX: 4, gridY: 0, locked: true }), widget('a', { gridX: 0, gridY: 0 })], 1200);
  const before = snapshot(rects);
  assert.equal(planWorkspaceDrop(rects, { ...rects[1], x: 4, y: 0 }), null);
  assert.equal(planWorkspaceDrop(rects, { ...rects[0], x: 0, y: 20 }), null);
  assert.equal(snapshot(rects), before);
});
test('a card can enter another section without duplicating an existing id', () => {
  const rects = arrangeWorkspaceWidgets([widget('b')], 1200);
  const plan = planWorkspaceDrop(rects, { id: 'a', x: 4, y: 0, w: 4, h: 12, locked: false });
  assert.equal(plan.length, 2); assert.equal(new Set(plan.map(r => r.id)).size, 2); disjoint(plan);
});
test('invalid positions, nonfinite values and out-of-bounds sizes are refused', () => {
  const good = { id: 'a', x: 0, y: 0, w: 4, h: 12, locked: false };
  for (const extra of [{ x: -1 }, { x: 9 }, { y: -1 }, { y: 10001 }, { x: NaN }, { y: Infinity }, { w: 13 }, { w: 2 }, { h: 0 }, { h: 100 }, { y: 1.2 }])
    assert.equal(planWorkspaceDrop([], { ...good, ...extra }), null);
});
test('repeated seeded arrangements and moves remain bounded, stable and non-overlapping', () => {
  let seed = 311;
  const rnd = n => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed % n; };
  for (let run = 0; run < 250; run++) {
    const items = Array.from({ length: 10 + rnd(45) }, (_, i) => widget(String(i), { span: 3 + rnd(10), height: 160 + rnd(41) * 20, locked: rnd(9) === 0 }));
    const before = snapshot(items), rects = arrangeWorkspaceWidgets(items, 1200);
    disjoint(rects); assert.equal(snapshot(items), before);
    const current = rects.find(r => !r.locked); if (!current) continue;
    const plan = planWorkspaceDrop(rects, { ...current, x: rnd(13 - current.w), y: rnd(100) });
    if (plan) {
      disjoint(plan);
      for (const locked of rects.filter(r => r.locked)) assert.deepEqual(plan.find(r => r.id === locked.id), locked);
    }
  }
});

// The preview and the committed card must be painted from one single geometry.
// Reported by David: a card looks right while dragging, then comes back slightly
// shorter and out of line with its neighbours once the move is validated.
test('every preset renders exactly as tall as the rows it occupies', () => {
  for (const [name, size] of Object.entries(WIDGET_SIZES)) {
    const rows = widgetRows(size.height);
    const previewPx = rows * GRID_STEP - GRID_GAP;
    assert.equal(snapGridHeight(size.height), previewPx, `${name}: preview ${previewPx}px but committed ${size.height}px`);
    assert.equal(gridHeight(rows), snapGridHeight(size.height), `${name}: rendered height leaves the grid`);
  }
});
test('heights are snapped to the row lattice and snapping is idempotent', () => {
  for (let height = 40; height <= MAX_SNAP_HEIGHT; height++) {
    const rows = widgetRows(height), snapped = snapGridHeight(height);
    assert.equal(snapped, gridHeight(rows));
    assert.ok(snapped >= height && snapped - height < GRID_STEP, `${height} snaps to ${snapped}`);
    assert.equal(snapGridHeight(snapped), snapped, `${height} is not stable`);
    assert.equal(gridRows(snapped), rows, `${height} changes its row span when committed`);
  }
  for (const height of [MAX_SNAP_HEIGHT + 1, 950, 960, 5000])
    assert.equal(snapGridHeight(height), MAX_SNAP_HEIGHT, `${height} must land on the tallest lattice row`);
});
test('a card bottom always stops exactly one grid gap above the next row', () => {
  for (const height of [220, 360, 480, 221, 359, 481, 160, 960]) {
    const [rect] = arrangeWorkspaceWidgets([widget('a', { gridX: 0, gridY: 3, height })], 1200);
    assert.equal(rect.y * GRID_STEP + gridHeight(rect.h), (rect.y + rect.h) * GRID_STEP - GRID_GAP, `height ${height}`);
  }
});
test('a card pushed under another leaves exactly the grid gap', () => {
  const items = [widget('a', { gridX: 0, gridY: 0, height: 221 }), widget('b', { gridX: 0, gridY: 1, height: 360 })];
  const [a, b] = arrangeWorkspaceWidgets(items, 1200);
  assert.equal(b.y, a.y + a.h);
  assert.equal(b.y * GRID_STEP - (a.y * GRID_STEP + gridHeight(a.h)), GRID_GAP);
});
test('two cards of the same preset on the same row render at the same height', () => {
  for (const [name, size] of Object.entries(WIDGET_SIZES)) {
    const items = [widget('a', { gridX: 0, gridY: 0, span: size.span, height: size.height }),
      widget('b', { gridX: size.span, gridY: 0, span: size.span, height: size.height })];
    const rects = arrangeWorkspaceWidgets(items, 1200);
    assert.equal(rects[0].h, rects[1].h, name);
    assert.equal(gridHeight(rects[0].h), gridHeight(rects[1].h), name);
    if (size.span < GRID_COLUMNS) assert.equal(rects[0].y, rects[1].y, name);
  }
});
test('the resize preview commits the height it just showed', () => {
  const base = { id: 'a', x: 0, y: 0, w: 4, h: widgetRows(220), locked: false };
  for (const delta of [0, 1, 7, 19, 20, 33, -13, -220, 4000]) {
    const { moving, size } = previewResizeRect({ rect: base, span: 4, width: 1200,
      startX: 100, startY: 100, pointerX: 100, pointerY: 100 + delta, startHeight: 220 });
    assert.equal(size.height, snapGridHeight(size.height), `delta ${delta}: preview label lies`);
    assert.equal(moving.h, gridRows(size.height), `delta ${delta}: span disagrees with height`);
    assert.equal(gridHeight(moving.h), size.height, `delta ${delta}: rendered height leaves the grid`);
    assert.ok(size.height >= 160 && size.height <= 960, `delta ${delta} escapes the bounds`);
  }
});
test('the move preview uses the same effective span as the layout', () => {
  for (const width of [350, 639, 640, 800, 959, 960, 1200]) {
    const rect = { id: 'a', x: 0, y: 0, w: 4, h: widgetRows(220), locked: false };
    const moving = previewMoveRect({ rect, span: 4, width, pointerX: 400, pointerY: 300, offsetRatio: 0.5, offsetY: 0 });
    assert.equal(moving.w, effectiveGridSpan(4, width), `width ${width}: preview span disagrees with the layout`);
    assert.ok(moving.x >= 0 && moving.x + moving.w <= GRID_COLUMNS, `width ${width}: preview leaves the grid`);
  }
});
test('the resize preview never escapes the grid and keeps the card reachable', () => {
  const base = { id: 'a', x: 8, y: 0, w: 4, h: widgetRows(220), locked: false };
  const { moving, size } = previewResizeRect({ rect: base, span: 4, width: 1200,
    startX: 100, startY: 100, pointerX: 100000, pointerY: 100000, startHeight: 220 });
  assert.equal(size.span, 12); assert.equal(moving.x, 0);
  assert.ok(size.height <= MAX_WIDGET_HEIGHT, `height ${size.height} escapes the bound`);
  assert.equal(moving.h, gridRows(size.height));
  assert.equal(gridHeight(moving.h), size.height);
});
