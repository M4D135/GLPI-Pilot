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
  const decorations = new WeakSet();
  let frame = 0;
  let pending = null;
  let spotlight = null;

  const allowed = () => !document.hidden && root.dataset.motion !== 'reduce'
    && root.dataset.contrast !== 'true' && !reduced.matches && !contrast.matches;

  function resetPointer() {
    if (frame) cancelAnimationFrame(frame);
    frame = 0; pending = null;
    if (spotlight) {
      spotlight.style.removeProperty('--d-mx'); spotlight.style.removeProperty('--d-my');
      spotlight = null;
    }
  }
  function sync() {
    const active = allowed();
    root.dataset.domeniaMotion = active ? 'on' : 'off';
    if (!active) {
      resetPointer();
      document.querySelectorAll('.domenia-ripple').forEach(node => node.remove());
    }
  }
  function decorateHero(hero) {
    if (decorations.has(hero)) return;
    decorations.add(hero);
    const orbs = document.createElement('span');
    orbs.className = 'domenia-orbs'; orbs.setAttribute('aria-hidden', 'true');
    for (const name of ['a', 'b', 'c']) {
      const orb = document.createElement('span'); orb.className = `domenia-orb ${name}`; orbs.append(orb);
    }
    const ring = document.createElement('span'); ring.className = 'domenia-glow-ring'; ring.setAttribute('aria-hidden', 'true');
    hero.prepend(orbs, ring); hero.classList.add('domenia-decorated');
  }
  function decorate(node) {
    if (!(node instanceof Element)) return;
    if (node.matches('.hero')) decorateHero(node);
    node.querySelectorAll('.hero').forEach(decorateHero);
  }
  new MutationObserver(changes => {
    for (const change of changes) for (const node of change.addedNodes) decorate(node);
  }).observe(document.body, {childList: true, subtree: true});
  new MutationObserver(sync).observe(root, {attributes: true, attributeFilter: ['data-motion', 'data-contrast']});

  document.addEventListener('pointermove', event => {
    if (!allowed() || !pointer.matches || event.pointerType === 'touch' || event.buttons) { resetPointer(); return; }
    const host = event.target instanceof Element ? event.target.closest('.quick-link') : null;
    if (!host) { resetPointer(); return; }
    pending = {host, x: event.clientX, y: event.clientY};
    if (frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      if (!allowed() || !pending?.host.isConnected) return;
      const {host, x, y} = pending; pending = null;
      if (spotlight && spotlight !== host) {
        spotlight.style.removeProperty('--d-mx'); spotlight.style.removeProperty('--d-my');
      }
      spotlight = host;
      const bounds = host.getBoundingClientRect();
      host.style.setProperty('--d-mx', `${Math.round(x - bounds.left)}px`);
      host.style.setProperty('--d-my', `${Math.round(y - bounds.top)}px`);
    });
  }, {passive: true});

  document.addEventListener('pointerdown', event => {
    resetPointer();
    if (!allowed() || event.button !== 0) return;
    const host = event.target instanceof Element ? event.target.closest('.button.primary') : null;
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
