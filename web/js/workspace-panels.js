import { el, dialog, showError } from './ui.js';

// An editor belongs to its workspace until its render and cleanup have settled.
// In particular, pilot:closed must never let a parent reload before that cleanup.
export function createWorkspacePanels(ctx) {
  const entries = new Set();
  let disposed = false, disposing = null;
  const report = reason => ctx.notify(reason?.message || String(reason), 'danger');

  async function open(title, render, params = {}) {
    if (disposed || ctx.signal?.aborted) return null;
    const body = el('div', { class: 'workspace-tool-body', 'data-tool': params.panel || '' });
    const modal = dialog(title, body), nativeClose = modal.close;
    modal.element.classList.add('workspace-tool-panel');
    modal.element.dataset.workspacePanel = params.panel || '';
    const controller = new AbortController();
    const entry = { modal, controller, cleanup: null, cleaning: null, cleanupFailure: null, pending: null, closing: null,
      phase: 'open', refreshRequested: false, destination: null };
    entries.add(entry);

    function clean() {
      if (entry.cleaning) return entry.cleaning;
      const cleanup = entry.cleanup;
      if (typeof cleanup !== 'function') return Promise.resolve();
      // Share the attempt, then consume only on success. A temporary save
      // failure must leave the same editor and cleanup available for a retry.
      entry.cleaning = Promise.resolve().then(cleanup).then(() => {
        if (entry.cleanup === cleanup) entry.cleanup = null;
        entry.cleanupFailure = null;
      }, reason => { entry.cleanupFailure = reason; throw reason; }).finally(() => { entry.cleaning = null; });
      return entry.cleaning;
    }
    function close(value) {
      if (entry.closing) return entry.closing;
      entry.phase = 'closing'; entry.destination = null;
      const previousInert = body.inert; body.inert = true;
      entry.closing = Promise.resolve().then(async () => {
        await entry.pending?.catch(reason => {
          // A close concurrent with a failed refresh must not silently replay
          // its failed save. The next explicit close can try it again.
          if (entry.cleanupFailure === reason) throw reason;
        });
        await clean();
        controller.abort();
        entry.phase = 'closed'; entries.delete(entry);
        nativeClose(value);
      }).catch(reason => {
        entry.phase = 'open'; entry.closing = null; body.inert = previousInert;
        throw reason;
      });
      return entry.closing;
    }
    async function navigate(page, args) {
      if (entry.phase !== 'open' || disposed || ctx.signal?.aborted) return;
      if (entry.pending) {
        // A renderer may await navigate(). Waiting here for that same renderer
        // would deadlock; defer the destination until its cleanup is available.
        entry.destination = { page, args }; return;
      }
      await close();
      if (!disposed && !ctx.signal?.aborted) return ctx.navigate(page, args);
    }
    const local = { ...ctx, root: body, params: { ...ctx.params, ...params },
      signal: AbortSignal.any([ctx.signal, controller.signal].filter(Boolean)), navigate, refresh };

    function refresh() {
      if (entry.phase !== 'open' || local.signal.aborted) return Promise.resolve();
      entry.refreshRequested = true;
      // Coalesce refreshes requested while the renderer is still running. A
      // render can itself request one without awaiting its own pending task.
      if (entry.pending) return Promise.resolve();
      let succeeded = false;
      entry.pending = Promise.resolve().then(async () => {
        while (entry.refreshRequested && entry.phase === 'open' && !local.signal.aborted) {
          entry.refreshRequested = false;
          await clean();
          if (entry.phase !== 'open' || local.signal.aborted) break;
          body.replaceChildren();
          entry.cleanup = await render(local);
          body.querySelector(':scope > .analytics-heading')?.remove();
        }
        succeeded = true;
      }).finally(() => {
        entry.pending = null;
        if (succeeded && entry.destination && entry.phase === 'open' && !disposed && !ctx.signal?.aborted) {
          const destination = entry.destination; entry.destination = null;
          // Let the containing workspace finish its initial render too.
          setTimeout(() => { void navigate(destination.page, destination.args).catch(report); }, 0);
        } else entry.destination = null;
      });
      return entry.pending;
    }
    modal.close = close;
    const requestClose = event => {
      event.preventDefault(); event.stopImmediatePropagation();
      void close().catch(report);
    };
    // The shared dialog has a synchronous X/Escape handler. Intercept only this
    // panel's controls so its DOM and opener remain intact until cleanup ends.
    modal.element.querySelector(':scope > .modal-heading button')?.addEventListener('click', requestClose, { capture: true });
    modal.element.addEventListener('cancel', requestClose, { capture: true });
    entry.close = close;
    try {
      await refresh();
      if (ctx.signal?.aborted) await close();
    } catch (reason) { if (!local.signal.aborted) showError(body, reason); }
    return modal;
  }

  function dispose() {
    if (disposing) return disposing;
    disposed = true;
    disposing = Promise.resolve().then(async () => { for (const entry of [...entries]) await entry.close(); })
      .catch(reason => { disposed = false; disposing = null; throw reason; });
    return disposing;
  }
  return { open, dispose };
}
