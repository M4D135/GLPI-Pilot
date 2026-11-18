import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const production = ['../../web/js/team-scope.js', '../../web/js/collection-scope.js', '../../web/js/pages/library-collection.js']
  .map(path => readFileSync(new URL(path, import.meta.url), 'utf8').replace(/^import[^\n]+\n/gm, '').replace(/\bexport (?=(?:async )?function)/g, '')).join('\n');
export const runA = 'a'.repeat(32), runB = 'b'.repeat(32), sessionA = 'c'.repeat(32), sessionB = 'd'.repeat(32);
export const options = { automatic: false, intervalMinutes: 120, delayMilliseconds: 3000, batchSize: 25, batchPauseSeconds: 30,
  maximumMinutes: 60, consecutiveErrorLimit: 3, startMinute: 0, endMinute: 1440, timeZone: 'UTC' };
export const event = (sequence, patch = {}) => ({ sequence, at: '2026-11-18T14:32:09Z', level: 'info', stage: 'Lecture de l’index',
  code: 'index.progress', message: `Étape conservée ${sequence}`, ...patch });
export const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };

// Run the entire production page and its bridge/timer lifecycle in a small DOM
// double. These checks neither start a browser nor duplicate collection policy.
export async function fixture(patch = {}) {
  const queued = [], timers = new Map(), listeners = new Map(), calls = [], handlers = new Map(); let timerId = 0;
  class Element {
    constructor(tag) { this.tagName = tag; this.children = []; this.attributes = {}; this.dataset = {}; this.listeners = new Map(); this.className = ''; this._text = ''; this._open = false; this.hidden = false; this.disabled = false;
      this.classList = { add: value => { this.className += ` ${value}`; }, contains: value => this.className.split(/\s+/).includes(value) }; }
    set textContent(value) { this._text = String(value ?? ''); this.children = []; }
    get textContent() { return this._text + this.children.map(child => typeof child === 'string' ? child : child.textContent).join(''); }
    set open(value) { const changed = this._open !== !!value; this._open = !!value; if (changed) queued.push(() => this.emit('toggle')); }
    get open() { return this._open; }
    append(...children) { for (const child of children) { if (child instanceof Element) child.parentNode = this; this.children.push(child instanceof Element ? child : String(child)); } }
    replaceChildren(...children) { this.children = []; this._text = ''; this.append(...children); }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    removeAttribute(name) { delete this.attributes[name]; }
    addEventListener(name, callback) { const callbacks = this.listeners.get(name) || []; callbacks.push(callback); this.listeners.set(name, callbacks); }
    async emit(name) { for (const callback of this.listeners.get(name) || []) await callback({ type: name, target: this, preventDefault() {} }); }
    reportValidity() { return true; }
  }
  const el = (tag, attributes = {}, ...children) => {
    const node = new Element(tag);
    for (const [key, value] of Object.entries(attributes || {})) {
      if (value === undefined || value === null) continue;
      if (key === 'text') node.textContent = value;
      else if (key === 'class') node.className = value;
      else if (key === 'dataset') Object.assign(node.dataset, value);
      else node[key] = value;
    }
    node.append(...children.flat(Infinity).filter(child => child !== null && child !== undefined && child !== false)); return node;
  };
  const button = (label, handler, settings = {}) => {
    const node = el('button', { disabled: !!settings.disabled, class: `button ${settings.primary ? 'primary' : ''}`, ...settings.attributes }, el('span', { text: label }));
    if (handler) node.addEventListener('click', async () => {
      if (node.disabled) return; const disabled = node.disabled; node.disabled = true;
      try { await handler(); } finally { node.disabled = disabled; }
    });
    return node;
  };
  const data = { state: { revision: 1, state: 'attention', runId: runA, nextAt: null, options },
    report: { runId: runA, observed: 0, read: 0, reused: 0, retryCount: 0, message: 'Passage interrompu avant lecture des fiches.',
      limitations: ['Le délai de lecture de l’index a été dépassé. La reprise relira la liste.'], startedAt: '2026-11-18T14:31:00Z' },
    activity: { state: 'idle' }, persisted: true,
    events: [event(3, { level: 'error', code: 'index.timeout', message: 'Le délai de lecture de l’index a été dépassé.' }), event(2), event(1, { stage: 'Préparation', code: 'collection.started', message: 'Passage démarré.' })], ...patch };
  const api = {
    on: (name, callback) => { listeners.set(name, callback); return () => listeners.delete(name); },
    call: async (operation, args = {}) => {
      calls.push({ operation, args: structuredClone(args) });
      if (handlers.has(operation)) return handlers.get(operation)(args);
      if (operation === 'library.collection.status') return structuredClone(data.activity);
      if (operation === 'library.collection.get') {
        if (data.modelReadFailure) throw new Error(data.modelReadFailure);
        return structuredClone({ state: data.state, report: data.report, activity: data.activity, ...(data.scopeModel || {}) });
      }
      if (operation === 'library.collection.events') return { runId: args.runId, total: data.events.length, offset: args.offset, limit: args.limit,
        persisted: data.persisted, items: structuredClone(data.events.slice(args.offset, args.offset + args.limit)) };
      if (operation === 'library.collection.scope') return structuredClone(data.scopeModel || {});
      if (operation === 'library.collection.preview') return { ...(structuredClone(data.scopeModel || {})), scope: structuredClone(args.scope), previewToken: 'preview-token', coverage: structuredClone(data.previewCoverage || { selected: 10, complete: 6, missing: 2, partial: 1, changed: 1, unconfirmed: 0, toRead: 4 }) };
      if (operation === 'library.capture.entries') return { total: 0, items: [] };
      throw new Error(`Unexpected operation: ${operation}`);
    }
  };
  const root = el('main'), controller = new AbortController();
  const context = vm.createContext({
    el, button, icon: name => el('svg', { name }), toolbar: (...nodes) => el('div', { class: 'toolbar' }, nodes),
    input: (value, attributes) => el('input', { value, ...attributes }), select: (options, value) => el('select', { value }, options.map(option => el('option', { value: option.value, text: option.label }))), field: (label, node) => el('label', {}, label, node),
    confirm: async () => true, formatNumber: value => String(value ?? 0), formatDate: value => value || '—',
    showError: (host, error) => host.append(el('p', { text: error.message })), isSimulation: session => !!session.simulation, isFileSource: session => !!session.fileSource,
    document: { visibilityState: 'visible' }, structuredClone, URL,
    setInterval: callback => { const id = ++timerId; timers.set(id, callback); return id; }, clearInterval: id => timers.delete(id)
  });
  vm.runInContext(production + '\nglobalThis.render = renderLibraryCollection; globalThis.coveragePolicy = collectionCoverage;', context);
  const dispose = await context.render({ root, api, signal: controller.signal, state: { session: {} }, notify() {}, navigate() {} });
  function all(node = root) { return [node, ...node.children.filter(child => child instanceof Element).flatMap(child => all(child))]; }
  const find = predicate => all().find(predicate);
  async function flush() {
    for (let attempt = 0; attempt < 5; attempt++) {
      for (const callback of queued.splice(0)) await callback();
      await new Promise(resolve => setImmediate(resolve));
    }
  }
  await flush();
  return { data, calls, handlers, root, dispose, timers, listeners, all, find, flush, coveragePolicy: context.coveragePolicy,
    byClass: value => find(node => node.classList.contains(value)),
    fieldControl: label => find(node => node.tagName === 'label' && node.children[0] === label)?.children.find(child => child instanceof Element),
    panel: value => find(node => node.dataset.collectionPanel === value),
    button: label => find(node => node.tagName === 'button' && node.textContent === label),
    emit: activity => { data.activity = structuredClone(activity); listeners.get('library.collection.progress')?.(activity); },
    async tick() { for (const callback of timers.values()) callback(); await flush(); }
  };
}
