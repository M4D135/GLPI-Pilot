// View preferences live in the current WebView profile. Operational documents
// and calendar entries are owned by the C# stores, never by browser storage.
const memory = new Map();
export const workspaceContextKey = state => [state.session?.testMode || state.session?.demo ? 'simulation' : 'normal', state.settings?.dashboard?.personalTechnician || 'unselected'].join(':');
const key = (state, name) => `glpi-pilot:view:3:${workspaceContextKey(state)}:${name}`;
// A local background is a temporary choice, not the next launch's default.
// Keep the viewport and filters; only the background resets to detailed streets.
function defaultMapBackground(name, view) {
 const viewport = name === 'map' ? view?.viewport : name === 'calendar-map' ? view : null;
 if (viewport && typeof viewport === 'object' && !Array.isArray(viewport)) viewport.streets = true;
 return view;
}
export function readWorkspaceView(state, name, fallback = {}) {
 const id = key(state, name);
 try {
  const remembered = memory.has(id), value = remembered ? memory.get(id) : JSON.parse(localStorage.getItem(id) || 'null');
  const view = value && typeof value === 'object' ? structuredClone(value) : structuredClone(fallback);
  return remembered ? view : defaultMapBackground(name, view);
 }
 catch { return defaultMapBackground(name, structuredClone(fallback)); }
}
export function saveWorkspaceView(state, name, value) {
 const id = key(state, name), copy = structuredClone(value); memory.set(id, copy);
 // Bound persisted view settings. Do not persist source rows, notes or drafts.
 try { const text = JSON.stringify(defaultMapBackground(name, structuredClone(copy))); if (text.length <= 24000) localStorage.setItem(id, text); } catch { /* A disabled browser store still keeps the current session usable. */ }
}
export function rememberScope(state, scope) {
 const value = { focusKeys: [...new Set(scope.focusKeys || [])], technicianKeys: [...new Set(scope.technicianKeys || (scope.technicianKey ? [scope.technicianKey] : []))] };
 saveWorkspaceView(state, 'scope', value); return value;
}
export function currentScope(state) { return readWorkspaceView(state, 'scope', { focusKeys: [], technicianKeys: [] }); }
