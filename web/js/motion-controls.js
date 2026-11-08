import { el, button, toolbar, dialog } from './ui.js';
import { weatherScene } from './pages/weather-scene.js';

const reduced = matchMedia('(prefers-reduced-motion: reduce)');
const forcedColors = matchMedia('(forced-colors: active)');

export function motionStatus(state) {
  const preferences = state.settings?.interface || {};
  const mode = preferences.reduceMotion ? 'reduce' : preferences.followWindowsMotion === false ? 'full' : 'system';
  if (preferences.highContrast || forcedColors.matches || document.documentElement.dataset.theme === 'contrast') return { mode, active: false, label: 'Décors arrêtés pour les contrastes renforcés.',
    detail: 'Le mode de contraste garde une présentation fixe et lisible.' };
  if (mode === 'reduce') return { mode, active: false, label: 'Animations réduites dans votre profil.',
    detail: 'Choisissez Expressives pour retrouver le mouvement de Domenia.' };
  if (mode === 'system' && reduced.matches) return { mode, active: false, canActivate: true, label: 'Animations en pause à la demande de Windows.',
    detail: 'Expressives permet de les activer dans GLPI Pilot en conservant le réglage Windows.' };
  if (document.hidden) return { mode, active: false, label: 'Animations en pause en arrière-plan.',
    detail: 'Elles reprennent quand vous revenez dans la fenêtre.' };
  return { mode, active: true, label: mode === 'full' ? 'Animations expressives actives.' : 'Animations actives · préférence Windows suivie.',
    detail: 'Reflets, décors, météo et apparitions des cartes s’animent.' };
}

function watchMotion(state, render) {
  const update = () => render(motionStatus(state));
  const observer = new MutationObserver(update);
  observer.observe(document.documentElement, { attributes: true,
    attributeFilter: ['data-motion', 'data-motion-policy', 'data-domenia-motion', 'data-contrast'] });
  reduced.addEventListener('change', update); forcedColors.addEventListener('change', update);
  document.addEventListener('visibilitychange', update); update();
  return () => { observer.disconnect(); reduced.removeEventListener('change', update);
    forcedColors.removeEventListener('change', update); document.removeEventListener('visibilitychange', update); };
}

// A live sample of the same weather and decorative CSS as the workspaces.
// It contains no ticket values and follows the effective motion preference.
export function createMotionPreview(state) {
  const status = el('strong', { class: 'motion-preview-status', role: 'status', 'aria-live': 'polite' });
  const detail = el('p', { class: 'help' });
  const orbs = el('span', { class: 'domenia-orbs', 'aria-hidden': 'true' },
    ...['a', 'b', 'c'].map(name => el('span', { class: `domenia-orb ${name}` })));
  const illustration = el('div', { class: 'motion-preview-illustration', 'aria-hidden': 'true' }, weatherScene('watch'));
  const element = el('section', { class: 'motion-preview domenia-motion-surface', 'aria-label': 'Aperçu des animations',
    dataset: { motionVisible: 'true' } }, orbs,
    el('span', { class: 'domenia-glow-ring', 'aria-hidden': 'true' }),
    el('div', { class: 'motion-preview-copy' }, el('small', { text: 'DOMENIA · APERÇU EN DIRECT' }), status, detail), illustration);
  const disposeWatch = watchMotion(state, value => {
    status.textContent = value.label; detail.textContent = value.detail;
    element.dataset.motionActive = String(value.active); element.dataset.motionMode = value.mode;
  });
  const viewport = new IntersectionObserver(entries => {
    for (const entry of entries) entry.target.dataset.motionVisible = String(entry.isIntersecting);
  });
  viewport.observe(element);
  return { element, dispose() { disposeWatch(); viewport.disconnect(); } };
}

export function createMotionButton(ctx, { quickActivate = false } = {}) {
  let modal = null, disposed = false, pending = false;
  const control = button('Animations', open, { icon: 'spark',
    attributes: { class: 'button motion-control', 'aria-label': 'Régler les animations', 'aria-haspopup': 'dialog' } });
  const reason = el('small', { class: 'motion-control-reason', role: 'status', hidden: true });
  const activate = button('Activer les animations partout', async () => {
    if (pending || disposed) return;
    pending = true; control.disabled = true;
    try {
      await ctx.api.call('app.appearance', { preferences: { ...ctx.state.settings.interface,
        reduceMotion: false, followWindowsMotion: false } }, { signal: ctx.signal });
    } catch (error) { if (error.name !== 'AbortError') { reason.hidden = false; reason.textContent = error.message; } }
    finally { pending = false; control.disabled = false; }
  }, { attributes: { 'data-motion-activate-global': '', hidden: true }, title: 'Anime tous les espaces de GLPI Pilot et mémorise ce choix.' });
  const element = quickActivate ? el('div', { class: 'motion-control-group' }, control, activate, reason) : control;
  const disposeWatch = watchMotion(ctx.state, value => {
    control.querySelector('span').textContent = value.active ? 'Animations actives' : 'Animations en pause';
    control.dataset.motionActive = String(value.active); control.title = value.label;
    reason.hidden = value.active; reason.textContent = value.label;
    activate.hidden = !value.canActivate;
  });

  function open() {
    if (disposed || modal) return;
    const preview = createMotionPreview(ctx.state);
    const controls = toolbar();
    const error = el('p', { class: 'notice danger', role: 'alert', hidden: true });
    const modes = [
      ['full', 'Activer les animations Domenia', 'spark'],
      ['system', 'Suivre Windows'],
      ['reduce', 'Réduire les animations']
    ];
    const buttons = modes.map(([mode, label, symbol]) => button(label, () => apply(mode),
      { primary: mode === 'full', icon: symbol, attributes: { 'aria-pressed': 'false', 'data-motion-choice': mode } }));
    controls.append(...buttons);
    modal = dialog('Animations Domenia', el('div', { class: 'motion-options' },
      el('p', { text: 'Choisissez le mouvement qui vous convient. Chaque choix prend effet immédiatement et reste mémorisé.' }),
      preview.element, controls, error), [{ label: 'Fermer', action: close => { if (!pending) close(); } }], { returnFocus: control });
    modal.element.classList.add('motion-dialog');
    const disposeState = watchMotion(ctx.state, value => {
      for (const item of buttons) item.setAttribute('aria-pressed', String(item.dataset.motionChoice === value.mode));
    });
    modal.element.addEventListener('cancel', event => { if (pending) { event.preventDefault(); event.stopImmediatePropagation(); } }, true);
    modal.element.addEventListener('pilot:closed', () => { preview.dispose(); disposeState(); modal = null; }, { once: true });

    async function apply(mode) {
      if (pending || disposed) return;
      pending = true; error.hidden = true;
      const currentModal = modal;
      for (const item of currentModal.element.querySelectorAll('button')) item.disabled = true;
      try {
        await ctx.api.call('app.appearance', { preferences: { ...ctx.state.settings.interface,
          reduceMotion: mode === 'reduce', followWindowsMotion: mode === 'system' } }, { signal: ctx.signal });
      } catch (reason) {
        if (reason.name !== 'AbortError') { error.textContent = reason.message; error.hidden = false; }
      } finally {
        pending = false;
        for (const item of currentModal.element.querySelectorAll('button')) item.disabled = false;
      }
    }
  }

  function dispose() { if (disposed) return; disposed = true; disposeWatch(); modal?.close(); ctx.signal?.removeEventListener('abort', dispose); }
  ctx.signal?.addEventListener('abort', dispose, { once: true });
  return { element, dispose };
}
