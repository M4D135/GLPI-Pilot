// Decorative Domenia 2 motion only. It never owns navigation, data, focus or drag gestures.
// Reference: domenia-d120 v2/web/js/ui.js (delegated spotlight/ripple and reduced motion).
let initialized = false;

export function initializeDomeniaMotion() {
  if (initialized) return;
  initialized = true;
  const root = document.documentElement;
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');
  const contrast = matchMedia('(forced-colors: active)');
  const pointer = matchMedia('(hover: hover) and (pointer: fine)');
  const surfaces = new WeakSet();
  const drawnCharts = new WeakSet();
  const queuedSurfaces = new Set();
  const revealingSurfaces = new Set();
  const trackedSurfaces = new Set();
  const trackedCharts = new Set();
  const drawingStrokes = new Set();
  const heroSelector = '.hero,.home-overview-heading';
  const surfaceSelector = `${heroSelector},.page-heading,.metric,.calendar-metric,.analytics-widget,.library-result,.library-stat,.quick-link,.page-content > .card,.page-content > .grid > .card`;
  const metricSelector = '.metric,.library-stat,.analytics-kpi-card';
  const chartSelector = '.analytics-history svg,.analytics-column-chart svg';
  const decorationSelector = '.domenia-orbs,.domenia-glow-ring,.domenia-spotlight,.domenia-widget-light,.domenia-cursor-halo,.domenia-ripple';
  const fixedContentSelector = 'button:not(:disabled),a[href],[role=button],table,[role=grid],.leaflet-container,.maps-map,[draggable=true],form,input,select,textarea,[contenteditable]:not([contenteditable=false]),[role=checkbox],[role=radio],[role=switch],[role=slider]';
  let decoratedMetrics = 0;
  let revealFrame = 0;
  let frame = 0;
  let pending = null;
  let spotlight = null;
  let halo = null;

  const allowed = () => !document.hidden && root.dataset.motion !== 'reduce'
    && root.dataset.contrast !== 'true' && !contrast.matches
    && (root.dataset.motionPolicy === 'full' || !reduced.matches);
  const editing = node => node?.closest('.is-editing,[data-business-editing=true],[data-layout-editing=true]');
  const inWorkspace = node => node.closest('.page-content') || node.matches('.workspace > .page-heading');
  const viewport = new IntersectionObserver(entries => {
    for (const entry of entries) {
      entry.target.dataset.motionVisible = String(entry.isIntersecting);
      if (entry.isIntersecting) queueSurface(entry.target);
      else if (spotlight === entry.target) resetPointer();
    }
  }, {rootMargin: '40px 0px', threshold: 0});
  const chartViewport = new IntersectionObserver(entries => {
    for (const entry of entries) {
      entry.target.dataset.motionVisible = String(entry.isIntersecting);
      if (entry.isIntersecting) drawChart(entry.target);
    }
  }, {rootMargin: '0px', threshold: 0});

  function decoration(host, className) {
    let child = host.querySelector(`:scope > .${className}`);
    if (!child) {
      child = document.createElement('span');
      child.className = className; child.setAttribute('aria-hidden', 'true');
      host.prepend(child);
    }
    return child;
  }

  function observeSurface(node) {
    if (!inWorkspace(node)) return;
    // Rendering may replace a known host's children. Membership only guards the
    // observer; the pointer-transparent children are reconciled every time.
    node.classList.add('domenia-motion-surface', 'domenia-spot');
    node.classList.toggle('domenia-surface-fixed', node.matches(fixedContentSelector) || !!node.querySelector(fixedContentSelector));
    decoration(node, 'domenia-spotlight');
    if (node.matches(metricSelector)) {
      node.classList.add('domenia-widget-surface');
      if (!node.style.getPropertyValue('--d-widget-phase')) node.style.setProperty('--d-widget-phase', `${-(decoratedMetrics++ % 7) * 1.1}s`);
      decoration(node, 'domenia-widget-light');
    }
    if (trackedSurfaces.has(node)) return;
    trackedSurfaces.add(node);
    node.dataset.motionVisible = 'false';
    viewport.observe(node);
  }

  function finishStroke(stroke) {
    stroke.classList.remove('domenia-chart-draw');
    stroke.style.removeProperty('--d-chart-length');
    stroke.style.removeProperty('--d-chart-delay');
    drawingStrokes.delete(stroke);
  }
  function drawChart(chart) {
    if (drawnCharts.has(chart)) return;
    drawnCharts.add(chart);
    if (!allowed() || editing(chart) || chart.closest('dialog,[hidden],[draggable=true]')) return;
    // Only undashed data curves: the dash patterns of gauges/donuts and every
    // path/point/value supplied by the renderer remain untouched.
    const strokes = chart.querySelectorAll('polyline[fill="none"][stroke]:not([stroke-dasharray]),path[fill="none"][stroke]:not([stroke-dasharray])');
    let index = 0;
    for (const stroke of strokes) {
      let length;
      try { length = stroke.getTotalLength(); } catch { continue; }
      if (!Number.isFinite(length) || length <= 0) continue;
      stroke.style.setProperty('--d-chart-length', String(Math.ceil(length) + 1));
      stroke.style.setProperty('--d-chart-delay', `${160 + Math.min(index++, 5) * 90}ms`);
      stroke.classList.add('domenia-chart-draw'); drawingStrokes.add(stroke);
    }
  }
  function observeChart(chart) {
    if (trackedCharts.has(chart) || !chart.closest('.page-content')) return;
    trackedCharts.add(chart);
    chart.classList.add('domenia-chart'); chart.dataset.motionVisible = 'false';
    chartViewport.observe(chart);
  }

  function finishSurface(node) {
    node.classList.remove('domenia-surface-enter');
    node.style.removeProperty('--d-card-delay');
    revealingSurfaces.delete(node);
  }
  function clearSurfaces() {
    if (revealFrame) cancelAnimationFrame(revealFrame);
    revealFrame = 0; queuedSurfaces.clear();
    for (const node of revealingSurfaces) finishSurface(node);
  }
  function queueSurface(node) {
    if (surfaces.has(node) || !inWorkspace(node)) return;
    surfaces.add(node);
    // An existing card moved by the editor must never replay its entrance.
    if (!allowed() || editing(node) || node.closest('dialog,[hidden],[draggable=true]')) return;
    queuedSurfaces.add(node);
    if (revealFrame) return;
    revealFrame = requestAnimationFrame(() => {
      revealFrame = 0;
      const pending = [...queuedSurfaces]; queuedSurfaces.clear();
      if (!allowed()) return;
      let index = 0;
      for (const surface of pending) {
        if (!surface.isConnected || editing(surface) || surface.closest('dialog,[hidden],[draggable=true]')) continue;
        // Buttons, links, forms, tables, maps and drag controls retain their hit-test rectangles.
        // Finishing an entrance on pointerdown/focus must not move a checkbox
        // away from the pointer before the browser dispatches its click.
        surface.style.setProperty('--d-card-delay', `${Math.min(index++, 7) * 75}ms`);
        surface.classList.add('domenia-surface-enter'); revealingSurfaces.add(surface);
      }
    });
  }
  function resetPointer() {
    if (frame) cancelAnimationFrame(frame);
    frame = 0; pending = null;
    halo?.classList.remove('is-visible');
    if (spotlight) {
      spotlight.style.removeProperty('--d-mx'); spotlight.style.removeProperty('--d-my');
      spotlight.style.removeProperty('--d-rx'); spotlight.style.removeProperty('--d-ry');
      spotlight.style.removeProperty('--d-px'); spotlight.style.removeProperty('--d-py');
      spotlight.removeAttribute('data-motion-pointer');
      spotlight = null;
    }
  }
  function sync() {
    const active = allowed();
    root.dataset.domeniaMotion = active ? 'on' : 'off';
    if (!active) {
      resetPointer(); clearSurfaces();
      for (const stroke of drawingStrokes) finishStroke(stroke);
      document.querySelectorAll('.domenia-ripple').forEach(node => node.remove());
      // A capture or a browser animation control can resume a CSS animation after
      // its busy selector disappears. Cancel only our named decorative effects,
      // including such detached animations, when the preference is switched off.
      for (const animation of document.getAnimations()) {
        if (/^(domenia-|pilot-weather-)/.test(animation.animationName || '')) animation.cancel();
      }
    }
  }
  function decorateHero(hero) {
    const orbs = decoration(hero, 'domenia-orbs');
    for (const name of ['a', 'b', 'c']) {
      if (orbs.querySelector(`:scope > .${name}`)) continue;
      const orb = document.createElement('span'); orb.className = `domenia-orb ${name}`; orbs.append(orb);
    }
    decoration(hero, 'domenia-glow-ring'); hero.classList.add('domenia-decorated');
  }
  function decorate(node) {
    if (!(node instanceof Element)) return;
    if (node.closest(`table,[role=grid],dialog,${decorationSelector}`)) return;
    if (node.matches(heroSelector)) decorateHero(node);
    node.querySelectorAll(heroSelector).forEach(decorateHero);
    if (node.matches(surfaceSelector)) observeSurface(node);
    node.querySelectorAll(surfaceSelector).forEach(observeSurface);
    if (node.matches(chartSelector)) observeChart(node);
    node.querySelectorAll(chartSelector).forEach(observeChart);
  }
  new MutationObserver(changes => {
    const hosts = new Set();
    for (const change of changes) {
      const host = change.target instanceof Element ? change.target.closest(surfaceSelector) : null;
      if (host) hosts.add(host);
      for (const node of change.addedNodes) decorate(node);
    }
    for (const host of hosts) {
      if (!host.isConnected || host.closest('table,[role=grid],dialog')) continue;
      if (host.matches(heroSelector)) decorateHero(host);
      observeSurface(host);
    }
    for (const node of revealingSurfaces) if (!node.isConnected) finishSurface(node);
    for (const node of trackedSurfaces) if (!node.isConnected) { viewport.unobserve(node); trackedSurfaces.delete(node); }
    for (const stroke of drawingStrokes) if (!stroke.isConnected) finishStroke(stroke);
    for (const chart of trackedCharts) if (!chart.isConnected) { chartViewport.unobserve(chart); trackedCharts.delete(chart); }
  }).observe(document.body, {childList: true, subtree: true});
  new MutationObserver(sync).observe(root, {attributes: true, attributeFilter: ['data-motion', 'data-motion-policy', 'data-contrast']});
  const finishEntrance = event => {
    if (event.animationName === 'domenia-surface-enter' && event.target instanceof Element) finishSurface(event.target);
    if (event.animationName === 'domenia-chart-draw' && event.target instanceof Element) finishStroke(event.target);
  };
  document.addEventListener('animationend', finishEntrance);
  document.addEventListener('animationcancel', finishEntrance);
  document.addEventListener('dragstart', clearSurfaces, true);
  document.addEventListener('focusin', event => {
    const surface = event.target instanceof Element ? event.target.closest('.domenia-surface-enter') : null;
    if (surface) finishSurface(surface);
  });

  document.addEventListener('pointermove', event => {
    if (!allowed() || !pointer.matches || event.pointerType === 'touch' || event.buttons) { resetPointer(); return; }
    const target = event.target instanceof Element ? event.target : null;
    if (!target || editing(target) || target.closest(`${fixedContentSelector},dialog,.analytics-widget-controls,.navigation-handle`)) { resetPointer(); return; }
    const host = target.closest('.domenia-spot,.quick-link');
    if (host?.dataset.motionVisible === 'false') { resetPointer(); return; }
    pending = {host, x: event.clientX, y: event.clientY};
    if (frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      if (!allowed() || !pending) return;
      const {host, x, y} = pending; pending = null;
      if (host && !host.isConnected) return;
      halo = halo?.isConnected ? halo : decoration(document.body, 'domenia-cursor-halo');
      halo.style.setProperty('--d-hx', `${x}px`); halo.style.setProperty('--d-hy', `${y}px`);
      halo.classList.add('is-visible');
      if (spotlight && spotlight !== host) {
        spotlight.style.removeProperty('--d-mx'); spotlight.style.removeProperty('--d-my');
        spotlight.style.removeProperty('--d-rx'); spotlight.style.removeProperty('--d-ry');
        spotlight.style.removeProperty('--d-px'); spotlight.style.removeProperty('--d-py');
        spotlight.removeAttribute('data-motion-pointer');
      }
      spotlight = host;
      if (!host) return;
      const bounds = host.getBoundingClientRect();
      host.style.setProperty('--d-mx', `${Math.round(x - bounds.left)}px`);
      host.style.setProperty('--d-my', `${Math.round(y - bounds.top)}px`);
      const px = Math.max(-1, Math.min(1, ((x - bounds.left) / bounds.width - .5) * 2));
      const py = Math.max(-1, Math.min(1, ((y - bounds.top) / bounds.height - .5) * 2));
      host.style.setProperty('--d-rx', `${(-py * 1.5).toFixed(2)}deg`);
      host.style.setProperty('--d-ry', `${(px * 1.5).toFixed(2)}deg`);
      host.style.setProperty('--d-px', px.toFixed(3)); host.style.setProperty('--d-py', py.toFixed(3));
      host.dataset.motionPointer = 'true';
    });
  }, {passive: true});

  document.addEventListener('pointerdown', event => {
    resetPointer();
    const surface = event.target instanceof Element ? event.target.closest('.domenia-surface-enter') : null;
    if (surface) finishSurface(surface);
    if (!allowed() || event.button !== 0) return;
    const host = event.target instanceof Element ? event.target.closest('.button') : null;
    // Do not decorate data cells, tabs, widget handles or controls participating in a drag.
    if (!host || host.disabled || host.closest('table,[role=tablist],[role=tab],[draggable=true],.work-tabs,.work-open-tabs,.library-modes,.analytics-tabs,.analytics-widget-controls,.navigation-handle')) return;
    const bounds = host.getBoundingClientRect(); const size = Math.max(bounds.width, bounds.height);
    if (!size) return;
    host.querySelectorAll('.domenia-ripple').forEach(node => node.remove());
    const ripple = document.createElement('span'); ripple.className = 'domenia-ripple'; ripple.setAttribute('aria-hidden', 'true');
    ripple.style.width = ripple.style.height = `${size}px`;
    ripple.style.left = `${event.clientX - bounds.left - size / 2}px`;
    ripple.style.top = `${event.clientY - bounds.top - size / 2}px`;
    const drop = () => ripple.remove();
    ripple.addEventListener('animationend', drop, {once: true}); host.append(ripple);
    setTimeout(drop, 900);
  }, {passive: true});

  document.addEventListener('visibilitychange', sync);
  window.addEventListener('blur', resetPointer);
  document.addEventListener('pointerleave', resetPointer);
  reduced.addEventListener('change', sync); contrast.addEventListener('change', sync);
  pointer.addEventListener('change', resetPointer);
  decorate(document.body); sync();
}
